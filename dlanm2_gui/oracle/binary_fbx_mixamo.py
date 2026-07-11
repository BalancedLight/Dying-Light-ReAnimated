from __future__ import annotations

import bisect
import hashlib
import json
import math
import struct
import zlib
from collections import defaultdict
from dataclasses import dataclass
from pathlib import Path
from typing import Any

import numpy as np

FBX_TICKS_PER_SECOND = 46_186_158_000
ROTATION_ORDERS = {0: "XYZ", 1: "XZY", 2: "YZX", 3: "YXZ", 4: "ZXY", 5: "ZYX"}


@dataclass(slots=True)
class FbxNode:
    name: str
    properties: list[Any]
    children: list["FbxNode"]
    start_offset: int
    end_offset: int


@dataclass(frozen=True, slots=True)
class FbxAnimationStack:
    """One independently selectable animation stack in an FBX document."""

    name: str
    layer_names: tuple[str, ...]
    start_tick: int
    stop_tick: int
    object_id: int
    layer_ids: tuple[int, ...]


def extract_mixamo_normalized(
    *,
    animation_fbx: str | Path,
    rest_fbx: str | Path,
    trusted_rest_json: str | Path,
    output_json: str | Path,
    raw_local_rotation: bool = False,
    fps: int = 30,
) -> dict[str, Any]:
    animation_path = Path(animation_fbx)
    rest_path = Path(rest_fbx)
    trusted_path = Path(trusted_rest_json)
    trusted = json.loads(trusted_path.read_text(encoding="utf-8"))
    animation = _FbxDocument(animation_path)
    rest = _FbxDocument(rest_path)

    trusted_bones = {str(row["name"]): np.asarray(row["rest_matrix"], dtype=float) for row in trusted["bones"]}
    if set(animation.limb_models) != set(trusted_bones):
        raise ValueError("animation FBX skeleton does not match trusted rest skeleton")
    if set(rest.limb_models) != set(trusted_bones):
        raise ValueError("rest FBX skeleton does not match trusted rest skeleton")

    raw_rest_globals = rest.global_matrices(tick=0, use_animation=False)
    corrections = {
        name: np.linalg.inv(raw_rest_globals[name]) @ trusted_bones[name]
        for name in trusted_bones
    }
    ticks = animation.frame_ticks(fps=fps)
    frame_count = len(ticks)

    tracks: list[dict[str, Any]] = []
    if raw_local_rotation:
        for bone in trusted["bones"]:
            name = str(bone["name"])
            quaternions = animation.raw_local_quaternions(name, ticks)
            quaternions = _continuous_quaternions(quaternions)
            tracks.append(
                {
                    "bone_name": name,
                    "keys": [
                        {
                            "frame": index,
                            "translation": [0.0, 0.0, 0.0],
                            "rotation": [float(value) for value in quaternion],
                            "scale": [1.0, 1.0, 1.0],
                        }
                        for index, quaternion in enumerate(quaternions)
                    ],
                }
            )
        warnings = [
            "Direct raw FBX Lcl Rotation diagnostic; no per-bone trusted T-pose correction was applied.",
            "This variant exists only to isolate whether rest-basis correction is required.",
        ]
        extraction_mode = "binary_fbx_raw_local_rotation_diagnostic"
    else:
        pose_globals_per_frame: list[dict[str, np.ndarray]] = []
        for tick in ticks:
            raw_pose = animation.global_matrices(tick=tick, use_animation=True)
            pose_globals_per_frame.append({name: raw_pose[name] @ corrections[name] for name in trusted_bones})
        parent_by_name = rest.parent_by_name
        for bone in trusted["bones"]:
            name = str(bone["name"])
            parent = parent_by_name.get(name)
            rows: list[tuple[np.ndarray, np.ndarray, np.ndarray]] = []
            for pose_globals in pose_globals_per_frame:
                if parent and parent in trusted_bones:
                    rest_local = np.linalg.inv(trusted_bones[parent]) @ trusted_bones[name]
                    pose_local = np.linalg.inv(pose_globals[parent]) @ pose_globals[name]
                else:
                    rest_local = trusted_bones[name]
                    pose_local = pose_globals[name]
                basis = np.linalg.inv(rest_local) @ pose_local
                rows.append(_decompose_basis(basis))
            quaternions = _continuous_quaternions([row[1] for row in rows])
            keys = []
            for index, ((translation, _quat, _scale), quaternion) in enumerate(zip(rows, quaternions)):
                if name != "mixamorig:Hips":
                    translation = np.zeros(3, dtype=float)
                keys.append(
                    {
                        "frame": index,
                        "translation": [float(value) for value in translation],
                        "rotation": [float(value) for value in quaternion],
                        "scale": [1.0, 1.0, 1.0],
                    }
                )
            tracks.append({"bone_name": name, "keys": keys})
        warnings = [
            "Extracted from binary FBX without Blender using the exact matching T-Pose fixture as the trusted rest basis.",
            "Non-root source translations are held at zero because the current diagnostic import path evaluates rotation channels only.",
            "Finger rotations are lower confidence than major limb-chain rotations and are packaged separately.",
        ]
        extraction_mode = "binary_fbx_trusted_tpose_basis_correction"

    payload = {
        "source_path": str(animation_path),
        "source_hash": _sha256(animation_path),
        "fps": fps,
        "frame_count": frame_count,
        "bones": trusted["bones"],
        "tracks": tracks,
        "warnings": warnings,
        "extraction_mode": extraction_mode,
        "rest_source_path": str(rest_path),
        "rest_source_hash": _sha256(rest_path),
        "trusted_rest_json": str(trusted_path),
    }
    out = Path(output_json)
    out.parent.mkdir(parents=True, exist_ok=True)
    out.write_text(json.dumps(payload, indent=2), encoding="utf-8")
    return payload


class _FbxDocument:
    def __init__(self, path: Path, animation_stack: str | None = None):
        self.path = path
        data = path.read_bytes()
        if not data.startswith(b"Kaydara FBX Binary"):
            raise ValueError(f"only binary FBX is supported: {path}")
        self.version = struct.unpack_from("<I", data, 23)[0]
        nodes, _offset = _parse_nodes(data, 27, self.version)
        self.top = {node.name: node for node in nodes}
        self.objects = self.top["Objects"]
        self.object_by_id = {
            int(node.properties[0]): node
            for node in self.objects.children
            if node.properties and isinstance(node.properties[0], int)
        }
        self.parents: dict[int, list[tuple[str, int, list[Any]]]] = defaultdict(list)
        self.children: dict[int, list[tuple[str, int, list[Any]]]] = defaultdict(list)
        for connection in self.top["Connections"].children:
            kind, child_id, parent_id, *rest = connection.properties
            self.parents[int(child_id)].append((str(kind), int(parent_id), rest))
            self.children[int(parent_id)].append((str(kind), int(child_id), rest))
        self.limb_models = {
            _clean_name(node.properties[1]): int(node.properties[0])
            for node in self.objects.children
            if node.name == "Model" and len(node.properties) > 2 and node.properties[2] == "LimbNode"
        }
        self.null_models = {
            _clean_name(node.properties[1]): int(node.properties[0])
            for node in self.objects.children
            if node.name == "Model" and len(node.properties) > 2 and node.properties[2] == "Null"
        }
        self.parent_by_name = {
            name: self._model_parent_name(object_id)
            for name, object_id in self.limb_models.items()
        }
        self.animation_stacks = self._animation_stack_inventory()
        self.selected_animation_stack: FbxAnimationStack | None = None
        self.curves: dict[tuple[int, str, str], tuple[list[int], list[float]]] = {}
        self.animation_start_tick = 0
        self.animation_stop_tick = 0
        if animation_stack or len(self.animation_stacks) == 1:
            self.select_animation_stack(animation_stack or self.animation_stacks[0].name)

    @property
    def animation_stack_names(self) -> tuple[str, ...]:
        return tuple(stack.name for stack in self.animation_stacks)

    def select_animation_stack(self, name: str | None = None) -> FbxAnimationStack | None:
        """Select one stack without silently choosing among multiple animations."""

        if not self.animation_stacks:
            if name:
                raise ValueError(f"FBX has no animation stack named {name!r}: {self.path}")
            self.selected_animation_stack = None
            self.curves = {}
            self.animation_start_tick = 0
            self.animation_stop_tick = 0
            return None
        if not name:
            if len(self.animation_stacks) != 1:
                available = ", ".join(repr(row.name) for row in self.animation_stacks)
                raise ValueError(
                    "FBX contains multiple animations; choose an animation stack: " + available
                )
            selected = self.animation_stacks[0]
        else:
            matches = [row for row in self.animation_stacks if row.name == name]
            if not matches:
                available = ", ".join(repr(row.name) for row in self.animation_stacks)
                raise ValueError(
                    f"FBX animation stack {name!r} was not found; available stacks: {available}"
                )
            if len(matches) > 1:
                raise ValueError(f"FBX contains duplicate animation stack names: {name!r}")
            selected = matches[0]
        if len(selected.layer_ids) != 1:
            layers = ", ".join(repr(value) for value in selected.layer_names) or "none"
            raise ValueError(
                f"FBX animation stack {selected.name!r} contains {len(selected.layer_ids)} "
                f"layers ({layers}); bake/flatten it to one animation layer before import"
            )
        self.selected_animation_stack = selected
        self.curves = self._animation_curves(selected.layer_ids[0])
        curve_times = [time for times, _values in self.curves.values() for time in times]
        self.animation_start_tick = int(selected.start_tick)
        self.animation_stop_tick = int(selected.stop_tick)
        if curve_times:
            if self.animation_start_tick == self.animation_stop_tick == 0:
                self.animation_start_tick = min(curve_times)
            self.animation_stop_tick = max(self.animation_stop_tick, max(curve_times))
        return selected

    @property
    def meters_per_unit(self) -> float:
        """Return the FBX scene unit in meters.

        FBX ``UnitScaleFactor`` is expressed in centimeters per scene unit.
        Mixamo exports normally use ``1.0`` (one scene unit is one
        centimeter), so the conversion to the meter-based Dying Light SMD
        skeleton is ``0.01``.
        """

        settings = self.top.get("GlobalSettings")
        properties = _properties70(settings) if settings is not None else {}
        raw = properties.get("UnitScaleFactor") or [1.0]
        factor_centimeters = float(raw[0])
        if not math.isfinite(factor_centimeters) or factor_centimeters <= 0.0:
            raise ValueError(f"invalid FBX UnitScaleFactor: {factor_centimeters!r}")
        return factor_centimeters / 100.0

    def frame_count(self, *, fps: int) -> int:
        return len(self.frame_ticks(fps=fps))

    def frame_ticks(self, *, fps: int) -> list[int]:
        if len(self.animation_stacks) > 1 and self.selected_animation_stack is None:
            self.select_animation_stack(None)
        start = int(self.animation_start_tick)
        stop = max(start, int(self.animation_stop_tick))
        frame_count = int(math.ceil((stop - start) * fps / FBX_TICKS_PER_SECOND)) + 1
        return [
            min(stop, start + int(round(index * FBX_TICKS_PER_SECOND / fps)))
            for index in range(max(1, frame_count))
        ]

    def raw_local_quaternions(self, name: str, ticks: list[int]) -> list[np.ndarray]:
        object_id = self.limb_models[name]
        node = self.object_by_id[object_id]
        props = _properties70(node)
        order = ROTATION_ORDERS.get(int((props.get("RotationOrder") or [0])[0]), "XYZ")
        default_rotation = _vector_property(props, "Lcl Rotation", [0.0, 0.0, 0.0])
        result = []
        for tick in ticks:
            rotation = default_rotation.copy()
            for axis_index, axis in enumerate("XYZ"):
                rotation[axis_index] = _sample_curve(
                    self.curves.get((object_id, "Lcl Rotation", axis)),
                    tick,
                    rotation[axis_index],
                )
            result.append(_quat_wxyz_from_matrix(_euler_matrix(rotation, order)))
        return result

    def global_matrices(self, *, tick: int, use_animation: bool) -> dict[str, np.ndarray]:
        local_by_id = {
            object_id: self._local_matrix(object_id, tick=tick, use_animation=use_animation)
            for object_id in self.limb_models.values()
        }
        name_by_id = {object_id: name for name, object_id in self.limb_models.items()}
        parent_id_by_id = {
            object_id: self._model_parent_id(object_id)
            for object_id in self.limb_models.values()
        }
        cache: dict[int, np.ndarray] = {}

        def calculate(object_id: int) -> np.ndarray:
            if object_id in cache:
                return cache[object_id]
            parent_id = parent_id_by_id[object_id]
            local = local_by_id[object_id]
            cache[object_id] = local if parent_id not in local_by_id else calculate(parent_id) @ local
            return cache[object_id]

        return {name_by_id[object_id]: calculate(object_id) for object_id in local_by_id}

    def _animation_curves(self, layer_id: int) -> dict[tuple[int, str, str], tuple[list[int], list[float]]]:
        result: dict[tuple[int, str, str], tuple[list[int], list[float]]] = {}
        for kind, curve_node_id, _rest in self.children[layer_id]:
            curve_node = self.object_by_id.get(curve_node_id)
            if kind != "OO" or curve_node is None or curve_node.name != "AnimationCurveNode":
                continue
            model_id = None
            property_name = None
            for parent_kind, parent_id, connection_rest in self.parents[curve_node_id]:
                parent = self.object_by_id.get(parent_id)
                if parent_kind == "OP" and parent and parent.name == "Model":
                    model_id = parent_id
                    property_name = str(connection_rest[0])
            if model_id is None or property_name is None:
                continue
            for child_kind, curve_id, connection_rest in self.children[curve_node_id]:
                curve = self.object_by_id.get(curve_id)
                if child_kind != "OP" or curve is None or curve.name != "AnimationCurve":
                    continue
                axis = str(connection_rest[0]).split("|")[-1]
                times = list(_child_value(curve, "KeyTime", []))
                values = [float(value) for value in _child_value(curve, "KeyValueFloat", [])]
                result[(model_id, property_name, axis)] = (times, values)
        return result

    def _animation_stack_inventory(self) -> tuple[FbxAnimationStack, ...]:
        layers = {
            int(node.properties[0]): _clean_name(node.properties[1])
            for node in self.objects.children
            if node.name == "AnimationLayer" and len(node.properties) >= 2
        }
        stacks: list[FbxAnimationStack] = []
        takes: dict[str, tuple[int, int]] = {}
        takes_node = self.top.get("Takes")
        if takes_node:
            for node in takes_node.children:
                if node.name != "Take" or not node.properties:
                    continue
                local_time = _child(node, "LocalTime")
                if local_time and len(local_time.properties) >= 2:
                    takes[_clean_name(node.properties[0])] = (
                        int(local_time.properties[0]),
                        int(local_time.properties[1]),
                    )
        claimed_layers: set[int] = set()
        for node in self.objects.children:
            if node.name != "AnimationStack" or len(node.properties) < 2:
                continue
            object_id = int(node.properties[0])
            name = _clean_name(node.properties[1])
            layer_ids = tuple(
                child_id
                for kind, child_id, _rest in self.children[object_id]
                if kind == "OO" and child_id in layers
            )
            claimed_layers.update(layer_ids)
            props = _properties70(node)
            start = int((props.get("LocalStart") or [0])[0])
            stop = int((props.get("LocalStop") or [start])[0])
            if name in takes:
                start, stop = takes[name]
            stacks.append(
                FbxAnimationStack(
                    name=name,
                    layer_names=tuple(layers[value] for value in layer_ids),
                    start_tick=start,
                    stop_tick=stop,
                    object_id=object_id,
                    layer_ids=layer_ids,
                )
            )
        # Some exporters omit AnimationStack objects. Treat each standalone layer
        # as an independently selectable clip rather than reverting to "Layer0".
        for layer_id, layer_name in layers.items():
            if layer_id not in claimed_layers:
                stacks.append(
                    FbxAnimationStack(
                        name=layer_name,
                        layer_names=(layer_name,),
                        start_tick=0,
                        stop_tick=0,
                        object_id=layer_id,
                        layer_ids=(layer_id,),
                    )
                )
        return tuple(stacks)

    def _local_matrix(self, object_id: int, *, tick: int, use_animation: bool) -> np.ndarray:
        node = self.object_by_id[object_id]
        props = _properties70(node)
        translation = _vector_property(props, "Lcl Translation", [0.0, 0.0, 0.0])
        rotation = _vector_property(props, "Lcl Rotation", [0.0, 0.0, 0.0])
        scaling = _vector_property(props, "Lcl Scaling", [1.0, 1.0, 1.0])
        if use_animation:
            for axis_index, axis in enumerate("XYZ"):
                translation[axis_index] = _sample_curve(
                    self.curves.get((object_id, "Lcl Translation", axis)), tick, translation[axis_index]
                )
                rotation[axis_index] = _sample_curve(
                    self.curves.get((object_id, "Lcl Rotation", axis)), tick, rotation[axis_index]
                )
                scaling[axis_index] = _sample_curve(
                    self.curves.get((object_id, "Lcl Scaling", axis)), tick, scaling[axis_index]
                )
        pre_rotation = _vector_property(props, "PreRotation", [0.0, 0.0, 0.0])
        post_rotation = _vector_property(props, "PostRotation", [0.0, 0.0, 0.0])
        rotation_offset = _vector_property(props, "RotationOffset", [0.0, 0.0, 0.0])
        rotation_pivot = _vector_property(props, "RotationPivot", [0.0, 0.0, 0.0])
        scaling_offset = _vector_property(props, "ScalingOffset", [0.0, 0.0, 0.0])
        scaling_pivot = _vector_property(props, "ScalingPivot", [0.0, 0.0, 0.0])
        order = ROTATION_ORDERS.get(int((props.get("RotationOrder") or [0])[0]), "XYZ")
        return (
            _translation_matrix(translation)
            @ _translation_matrix(rotation_offset)
            @ _translation_matrix(rotation_pivot)
            @ _euler_matrix(pre_rotation, order)
            @ _euler_matrix(rotation, order)
            @ np.linalg.inv(_euler_matrix(post_rotation, order))
            @ _translation_matrix(-rotation_pivot)
            @ _translation_matrix(scaling_offset)
            @ _translation_matrix(scaling_pivot)
            @ _scale_matrix(scaling)
            @ _translation_matrix(-scaling_pivot)
        )

    def _model_parent_id(self, object_id: int) -> int | None:
        for kind, parent_id, _rest in self.parents[object_id]:
            parent = self.object_by_id.get(parent_id)
            if kind == "OO" and parent and parent.name == "Model" and parent_id in self.limb_models.values():
                return parent_id
        return None

    def _model_parent_name(self, object_id: int) -> str | None:
        parent_id = self._model_parent_id(object_id)
        if parent_id is None:
            return None
        parent = self.object_by_id[parent_id]
        return _clean_name(parent.properties[1])


def _parse_nodes(data: bytes, offset: int, version: int) -> tuple[list[FbxNode], int]:
    nodes: list[FbxNode] = []
    null_length = 25 if version >= 7500 else 13
    while offset < len(data):
        if data[offset : offset + null_length] == b"\0" * null_length:
            return nodes, offset + null_length
        start = offset
        if version >= 7500:
            end_offset, property_count, property_length = struct.unpack_from("<QQQ", data, offset)
            offset += 24
        else:
            end_offset, property_count, property_length = struct.unpack_from("<III", data, offset)
            offset += 12
        name_length = data[offset]
        offset += 1
        if end_offset == 0:
            return nodes, offset - 1 + null_length
        name = data[offset : offset + name_length].decode("utf-8", errors="replace")
        offset += name_length
        properties = []
        property_end = offset + property_length
        for _index in range(property_count):
            value, offset = _parse_property(data, offset)
            properties.append(value)
        if offset != property_end:
            raise ValueError(f"FBX property length mismatch in node {name}")
        children: list[FbxNode] = []
        if offset < end_offset:
            children, offset = _parse_nodes(data, offset, version)
        if offset != end_offset:
            offset = end_offset
        nodes.append(FbxNode(name, properties, children, start, int(end_offset)))
    return nodes, offset


def _parse_property(data: bytes, offset: int) -> tuple[Any, int]:
    kind = chr(data[offset])
    offset += 1
    scalar_formats = {"Y": "h", "I": "i", "F": "f", "D": "d", "L": "q"}
    if kind in scalar_formats:
        fmt = "<" + scalar_formats[kind]
        return struct.unpack_from(fmt, data, offset)[0], offset + struct.calcsize(fmt)
    if kind == "C":
        return bool(data[offset]), offset + 1
    if kind in {"S", "R"}:
        length = struct.unpack_from("<I", data, offset)[0]
        offset += 4
        raw = data[offset : offset + length]
        offset += length
        return (raw.decode("utf-8", errors="replace") if kind == "S" else raw), offset
    if kind in {"f", "d", "l", "i", "b", "c"}:
        length, encoding, compressed_length = struct.unpack_from("<III", data, offset)
        offset += 12
        raw = data[offset : offset + compressed_length]
        offset += compressed_length
        if encoding == 1:
            raw = zlib.decompress(raw)
        formats = {"f": "f", "d": "d", "l": "q", "i": "i", "b": "?", "c": "b"}
        fmt = "<" + formats[kind] * length
        return list(struct.unpack_from(fmt, raw, 0)) if length else [], offset
    raise ValueError(f"unsupported FBX property kind {kind!r}")


def _child(node: FbxNode, name: str) -> FbxNode | None:
    return next((child for child in node.children if child.name == name), None)


def _child_value(node: FbxNode, name: str, default: Any) -> Any:
    found = _child(node, name)
    return found.properties[0] if found and found.properties else default


def _properties70(node: FbxNode) -> dict[str, list[Any]]:
    result: dict[str, list[Any]] = {}
    container = _child(node, "Properties70")
    if container:
        for row in container.children:
            if row.name == "P" and row.properties:
                result[str(row.properties[0])] = list(row.properties[4:])
    return result


def _vector_property(properties: dict[str, list[Any]], name: str, default: list[float]) -> np.ndarray:
    values = properties.get(name)
    return np.asarray(values[:3] if values else default, dtype=float)


def _sample_curve(curve: tuple[list[int], list[float]] | None, tick: int, default: float) -> float:
    if curve is None:
        return float(default)
    times, values = curve
    index = bisect.bisect_left(times, tick)
    if index < len(times) and times[index] == tick:
        return float(values[index])
    if index <= 0:
        return float(values[0])
    if index >= len(times):
        return float(values[-1])
    before_time, after_time = times[index - 1], times[index]
    alpha = (tick - before_time) / (after_time - before_time)
    return float(values[index - 1] + alpha * (values[index] - values[index - 1]))


def _translation_matrix(value: np.ndarray) -> np.ndarray:
    result = np.eye(4, dtype=float)
    result[:3, 3] = value
    return result


def _scale_matrix(value: np.ndarray) -> np.ndarray:
    result = np.eye(4, dtype=float)
    result[0, 0], result[1, 1], result[2, 2] = value
    return result


def _axis_rotation(axis: str, degrees: float) -> np.ndarray:
    angle = math.radians(float(degrees))
    c, s = math.cos(angle), math.sin(angle)
    result = np.eye(4, dtype=float)
    if axis == "X":
        result[:3, :3] = ((1, 0, 0), (0, c, -s), (0, s, c))
    elif axis == "Y":
        result[:3, :3] = ((c, 0, s), (0, 1, 0), (-s, 0, c))
    else:
        result[:3, :3] = ((c, -s, 0), (s, c, 0), (0, 0, 1))
    return result


def _euler_matrix(value: np.ndarray, order: str) -> np.ndarray:
    """Evaluate FBX Euler channels using FBX's intrinsic rotation order.

    FBX stores an order such as XYZ but, with this project's column-vector
    matrix convention, the corresponding matrix is Rz @ Ry @ Rx.  The older
    implementation post-multiplied in the written order (Rx @ Ry @ Rz), which
    transposed the effective bone axes on joints with non-trivial PreRotation.
    The uploaded Mixamo T-Pose provides a Blender-derived matrix oracle that
    confirms pre-multiplication for all 65 bones.
    """
    result = np.eye(4, dtype=float)
    axis_values = {"X": value[0], "Y": value[1], "Z": value[2]}
    for axis in order:
        result = _axis_rotation(axis, axis_values[axis]) @ result
    return result


def _decompose_basis(matrix: np.ndarray) -> tuple[np.ndarray, np.ndarray, np.ndarray]:
    translation = matrix[:3, 3].copy()
    linear = matrix[:3, :3].copy()
    scales = np.linalg.norm(linear, axis=0)
    scales = np.where(scales < 1.0e-12, 1.0, scales)
    normalized = linear / scales
    u, _singular, vt = np.linalg.svd(normalized)
    rotation = u @ vt
    if np.linalg.det(rotation) < 0.0:
        u[:, -1] *= -1.0
        rotation = u @ vt
        scales[-1] *= -1.0
    return translation, _quat_wxyz_from_matrix(rotation), scales


def _quat_wxyz_from_matrix(matrix: np.ndarray) -> np.ndarray:
    m = matrix[:3, :3]
    trace = float(np.trace(m))
    if trace > 0.0:
        s = math.sqrt(trace + 1.0) * 2.0
        w = 0.25 * s
        x = (m[2, 1] - m[1, 2]) / s
        y = (m[0, 2] - m[2, 0]) / s
        z = (m[1, 0] - m[0, 1]) / s
    elif m[0, 0] > m[1, 1] and m[0, 0] > m[2, 2]:
        s = math.sqrt(1.0 + m[0, 0] - m[1, 1] - m[2, 2]) * 2.0
        w = (m[2, 1] - m[1, 2]) / s
        x = 0.25 * s
        y = (m[0, 1] + m[1, 0]) / s
        z = (m[0, 2] + m[2, 0]) / s
    elif m[1, 1] > m[2, 2]:
        s = math.sqrt(1.0 + m[1, 1] - m[0, 0] - m[2, 2]) * 2.0
        w = (m[0, 2] - m[2, 0]) / s
        x = (m[0, 1] + m[1, 0]) / s
        y = 0.25 * s
        z = (m[1, 2] + m[2, 1]) / s
    else:
        s = math.sqrt(1.0 + m[2, 2] - m[0, 0] - m[1, 1]) * 2.0
        w = (m[1, 0] - m[0, 1]) / s
        x = (m[0, 2] + m[2, 0]) / s
        y = (m[1, 2] + m[2, 1]) / s
        z = 0.25 * s
    quaternion = np.asarray((w, x, y, z), dtype=float)
    return quaternion / np.linalg.norm(quaternion)


def _continuous_quaternions(values: list[np.ndarray]) -> list[np.ndarray]:
    result: list[np.ndarray] = []
    previous: np.ndarray | None = None
    for value in values:
        current = np.asarray(value, dtype=float)
        current = current / np.linalg.norm(current)
        if previous is not None and float(np.dot(previous, current)) < 0.0:
            current = -current
        result.append(current)
        previous = current
    return result


def _clean_name(value: Any) -> str:
    return str(value).split("\x00", 1)[0]


def _sha256(path: Path) -> str:
    return hashlib.sha256(path.read_bytes()).hexdigest()
