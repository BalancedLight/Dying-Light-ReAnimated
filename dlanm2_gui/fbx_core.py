from __future__ import annotations

"""Canonical production FBX scene, animation, and bind evaluator.

``FbxScene`` owns binary parsing and source geometry.  ``FbxDocument`` adds
animation selection/sampling and exposes the same local/global transform
implementation used by the model importer.  The old oracle module re-exports
this API for compatibility; production code should import from here.
"""

from dataclasses import asdict, dataclass
from pathlib import Path
from typing import Any, Callable, Iterable, Mapping, Sequence
import bisect
import math
import unicodedata

import numpy as np

from .model_importer.fbx_model import (
    FBX_TICKS_PER_SECOND,
    FBX_Y_UP_TO_DYING_LIGHT,
    FbxImportTolerance,
    FbxAnimationSkeletonError,
    FbxAnimationStackError,
    FbxDomainError,
    FbxLoadPurpose,
    FbxNode,
    FbxScene,
    _axis_rotation,
    _child,
    _child_value,
    _clean_name,
    _euler_matrix,
    _properties70,
    _vector_property,
)


@dataclass(frozen=True, slots=True)
class FbxAnimationStack:
    name: str
    layer_names: tuple[str, ...]
    start_tick: int
    stop_tick: int
    object_id: int
    layer_ids: tuple[int, ...]


@dataclass(frozen=True, slots=True)
class FbxAnimationStackActivity:
    name: str
    layer_names: tuple[str, ...]
    skeletal_channel_count: int
    changing_skeletal_channel_count: int
    key_count: int
    usable: bool
    reason: str = ""

    @property
    def changing(self) -> bool:
        return self.changing_skeletal_channel_count > 0

    def to_dict(self) -> dict[str, Any]:
        return asdict(self)


@dataclass(frozen=True, slots=True)
class FbxBindResolution:
    globals_by_id: dict[int, np.ndarray]
    source_by_id: dict[int, str]
    coverage: dict[str, int]
    warnings: tuple[str, ...]
    conflicting_transform_links: tuple[str, ...]
    conflicting_pose_transform_links: tuple[str, ...]


@dataclass(frozen=True, slots=True)
class FbxTransformContract:
    source_path: str
    source_sha256: str
    fbx_version: int
    meters_per_unit: float
    axis_settings: dict[str, int | float | None]
    requested_orientation_policy: str
    resolved_orientation_policy: str
    unit_conversion_count: int
    axis_conversion_count: int
    wrapper_models: tuple[str, ...]
    wrapper_scale_normalization: dict[str, Any]
    bind_source_by_bone: dict[str, str]
    mesh_bind_source_by_geometry: dict[str, str]
    bind_coverage: dict[str, int]
    normalized_name_collisions: tuple[tuple[str, str], ...]
    roots: tuple[str, ...]
    non_bone_ancestors: tuple[str, ...]
    reflected_or_negative_scale_nodes: tuple[str, ...]
    warnings: tuple[str, ...]
    errors: tuple[str, ...]

    def to_dict(self) -> dict[str, Any]:
        """Return a deterministic JSON-native diagnostic payload."""

        payload = asdict(self)
        payload["format"] = "dl-reanimated-fbx-transform-contract-v1"
        return payload

    def to_report(self) -> dict[str, Any]:
        return self.to_dict()


def normalize_matrix_to_target_space(
    matrix: np.ndarray,
    *,
    meters_per_unit: float,
    wrapper_scale_normalization_factor: float = 1.0,
    basis_matrix: np.ndarray | None = None,
) -> np.ndarray:
    """Apply the canonical post-wrapper unit and axis conversion exactly once.

    Model binds and animation samples both enter this function after the FBX
    evaluator has applied the per-LimbNode scene-wrapper normalizer.  Keeping
    the arithmetic here prevents the model and animation paths from growing
    separate Y-up/unit special cases again.
    """

    value = np.asarray(matrix, dtype=float).copy()
    if value.shape != (4, 4) or not np.isfinite(value).all():
        raise ValueError("FBX normalized matrix must be a finite 4x4 matrix")
    unit = float(meters_per_unit)
    wrapper = float(wrapper_scale_normalization_factor)
    if not math.isfinite(unit) or unit <= 0.0:
        raise ValueError("FBX meters_per_unit must be finite and positive")
    if not math.isfinite(wrapper) or wrapper <= 0.0:
        raise ValueError(
            "FBX wrapper scale normalization factor must be finite and positive"
        )
    basis = (
        np.eye(4, dtype=float)
        if basis_matrix is None
        else np.asarray(basis_matrix, dtype=float).copy()
    )
    if basis.shape != (4, 4) or not np.isfinite(basis).all():
        raise ValueError("FBX target basis must be a finite 4x4 matrix")
    try:
        inverse_basis = np.linalg.inv(basis)
    except np.linalg.LinAlgError as exc:
        raise ValueError("FBX target basis is singular") from exc
    value[:3, 3] *= unit / wrapper
    result = basis @ value @ inverse_basis
    if not np.isfinite(result).all():
        raise ValueError("normalized FBX target-space matrix is non-finite")
    return result


def _sample_curve(
    curve: tuple[list[int], list[float]] | None,
    tick: int,
    default: float,
) -> float:
    if curve is None:
        return float(default)
    times, values = curve
    if not times:
        return float(default)
    if tick <= times[0]:
        return float(values[0])
    if tick >= times[-1]:
        return float(values[-1])
    index = bisect.bisect_right(times, tick) - 1
    first, second = times[index], times[index + 1]
    if second == first:
        return float(values[index])
    fraction = (tick - first) / (second - first)
    return float(values[index] * (1.0 - fraction) + values[index + 1] * fraction)


def _matrix_agrees(left: np.ndarray, right: np.ndarray) -> bool:
    return bool(np.allclose(left, right, rtol=1.0e-5, atol=5.0e-5))


def resolve_bind_globals(
    scene: FbxScene,
    bone_ids: Sequence[int],
    *,
    normalizer: Callable[[int], np.ndarray] | None = None,
) -> FbxBindResolution:
    """Resolve authoritative bone globals through one shared priority rule.

    BindPose is preferred, then Skin Cluster TransformLink, then evaluated
    unanimated Model transforms.  Conflicting authoritative sources are
    retained as diagnostics instead of being silently ignored.
    """

    selected = tuple(dict.fromkeys(int(value) for value in bone_ids))
    selected_set = set(selected)
    model_names = dict(getattr(scene, "model_names", {}) or {})
    cluster_links: dict[int, list[np.ndarray]] = {}
    conflicting_links: list[str] = []
    scene_clusters = tuple(getattr(scene, "skin_clusters", ()) or ())
    if not scene_clusters:
        scene_clusters = tuple(
            cluster
            for geometry in scene.geometries
            for cluster in geometry.clusters
        )
    for cluster in scene_clusters:
        bone_id = cluster.bone_id
        if (
            bone_id is None
            or bone_id not in selected_set
            or cluster.transform_link is None
        ):
            continue
        value = np.asarray(cluster.transform_link, dtype=float)
        rows = cluster_links.setdefault(int(bone_id), [])
        if rows and not any(_matrix_agrees(value, previous) for previous in rows):
            conflicting_links.append(model_names.get(int(bone_id), str(bone_id)))
        rows.append(value.copy())

    pose_link_conflicts: list[str] = []
    for bone_id in selected:
        pose = scene.bind_pose_matrices.get(bone_id)
        if pose is None:
            continue
        if any(
            not _matrix_agrees(np.asarray(pose, dtype=float), link)
            for link in cluster_links.get(bone_id, ())
        ):
            pose_link_conflicts.append(model_names.get(bone_id, str(bone_id)))

    globals_by_id: dict[int, np.ndarray] = {}
    source_by_id: dict[int, str] = {}
    adjustment = normalizer or (lambda _bone_id: np.eye(4, dtype=float))
    for bone_id in selected:
        if bone_id in scene.bind_pose_matrices:
            matrix = np.asarray(scene.bind_pose_matrices[bone_id], dtype=float).copy()
            source = "Pose::BindPose"
        elif cluster_links.get(bone_id):
            candidates = cluster_links[bone_id]
            # Pick the TransformLink agreeing with the most peer clusters.  A
            # deterministic first-row tie break preserves legacy output.
            scores = [
                sum(_matrix_agrees(candidate, other) for other in candidates)
                for candidate in candidates
            ]
            matrix = candidates[max(range(len(candidates)), key=scores.__getitem__)].copy()
            source = "TransformLink"
        else:
            matrix = scene.model_global_matrix(bone_id)
            source = "ModelTransformsFallback"
        if matrix.shape != (4, 4) or not np.isfinite(matrix).all():
            name = model_names.get(bone_id, str(bone_id))
            raise ValueError(f"FBX bind matrix for {name!r} is not a finite 4x4 matrix")
        globals_by_id[bone_id] = np.asarray(adjustment(bone_id), dtype=float) @ matrix
        source_by_id[bone_id] = source

    counts = {
        key: sum(source == key for source in source_by_id.values())
        for key in ("Pose::BindPose", "TransformLink", "ModelTransformsFallback")
    }
    coverage = {
        **counts,
        "authoritative": counts["Pose::BindPose"] + counts["TransformLink"],
        "total": len(selected),
    }
    warnings: list[str] = []
    if coverage["authoritative"] < coverage["total"]:
        warnings.append(
            "Authoritative bind coverage is incomplete; unanimated Model transforms "
            "are used only for uncovered bones."
        )
    if conflicting_links:
        warnings.append(
            "Conflicting TransformLink matrices were found for: "
            + ", ".join(sorted(set(conflicting_links), key=str.casefold))
        )
    if pose_link_conflicts:
        warnings.append(
            "Pose::BindPose and TransformLink matrices disagree for: "
            + ", ".join(sorted(set(pose_link_conflicts), key=str.casefold))
        )
    return FbxBindResolution(
        globals_by_id=globals_by_id,
        source_by_id=source_by_id,
        coverage=coverage,
        warnings=tuple(warnings),
        conflicting_transform_links=tuple(
            sorted(set(conflicting_links), key=str.casefold)
        ),
        conflicting_pose_transform_links=tuple(
            sorted(set(pose_link_conflicts), key=str.casefold)
        ),
    )


class FbxDocument:
    """Public FBX animation document backed by :class:`FbxScene`."""

    def __init__(
        self,
        path: str | Path,
        animation_stack: str | None = None,
        *,
        orientation_policy: str = "auto",
        purpose: FbxLoadPurpose | str = FbxLoadPurpose.ANIMATION,
        tolerance: FbxImportTolerance | str = FbxImportTolerance.RECOMMENDED,
    ) -> None:
        self.path = Path(path)
        self.load_purpose = FbxLoadPurpose.coerce(purpose).value
        self.import_tolerance = FbxImportTolerance.coerce(tolerance).value
        self.scene = FbxScene.from_path(
            self.path,
            purpose=purpose,
            tolerance=tolerance,
        )
        try:
            self._initialize(animation_stack, orientation_policy)
        except FbxDomainError:
            raise
        except (ValueError, IndexError, np.linalg.LinAlgError) as exc:
            raise FbxAnimationSkeletonError(str(exc)) from exc

    @classmethod
    def from_scene(
        cls,
        scene: FbxScene,
        animation_stack: str | None = None,
        *,
        orientation_policy: str = "auto",
        purpose: FbxLoadPurpose | str | None = None,
        tolerance: FbxImportTolerance | str | None = None,
    ) -> "FbxDocument":
        document = cls.__new__(cls)
        document.path = Path(scene.path)
        document.scene = scene
        document.load_purpose = FbxLoadPurpose.coerce(
            purpose or getattr(scene, "load_purpose", FbxLoadPurpose.ANIMATION.value)
        ).value
        document.import_tolerance = FbxImportTolerance.coerce(
            tolerance
            or getattr(
                scene,
                "import_tolerance",
                FbxImportTolerance.RECOMMENDED.value,
            )
        ).value
        try:
            document._initialize(animation_stack, orientation_policy)
        except FbxDomainError:
            raise
        except (ValueError, IndexError, np.linalg.LinAlgError) as exc:
            raise FbxAnimationSkeletonError(str(exc)) from exc
        return document

    def _initialize(
        self,
        animation_stack: str | None,
        orientation_policy: str,
    ) -> None:
        self.object_by_id = self.scene.object_by_id
        self.parents = self.scene.parents
        self.children = self.scene.children
        self.objects = self.scene.top.get("Objects", FbxNode("Objects", [], [], 0, 0))
        self.limb_models = {
            self.scene.model_names[object_id]: object_id
            for object_id in self.scene.limb_ids
        }
        self.null_models = {
            self.scene.model_names[object_id]: object_id
            for object_id in self.scene.model_ids
            if self.scene.model_subtypes.get(object_id) in {"Null", "Root"}
        }
        limb_ids = set(self.scene.limb_ids)
        self.parent_by_name = {
            name: (
                self.scene.model_names[parent]
                if (parent := self.scene.nearest_limb_parent_id(object_id)) in limb_ids
                else None
            )
            for name, object_id in self.limb_models.items()
        }
        self.top = self.scene.top
        self.blend_shapes = tuple(getattr(self.scene, "blend_shapes", ()) or ())
        self.blend_shape_names = tuple(
            getattr(self.scene, "blend_shape_names", ()) or ()
        )
        self.animation_stacks = self._animation_stack_inventory()
        self.animation_stack_names = tuple(row.name for row in self.animation_stacks)
        self.selected_animation_stack: FbxAnimationStack | None = None
        self.meters_per_unit = self.scene.meters_per_unit
        self.requested_orientation_policy = str(orientation_policy or "auto")
        self.resolved_orientation_policy = self.scene.resolved_orientation_policy(
            self.requested_orientation_policy
        )
        self._curve_cache: dict[int, tuple[list[int], list[float]]] = {}
        self.curves: dict[tuple[int, str, str], tuple[list[int], list[float]]] = {}
        self.animation_start_tick = 0
        self.animation_stop_tick = 0
        self._normalizer_cache: dict[int, np.ndarray] = {}
        self._build_bind_inventory()
        self.transform_contract = self._build_transform_contract()
        try:
            if animation_stack:
                self.select_animation_stack(animation_stack)
            elif len(self.animation_stacks) == 1:
                self.select_animation_stack(self.animation_stacks[0].name)
            elif self.animation_stacks:
                self.select_preferred_animation_stack()
        except FbxDomainError:
            raise
        except (ValueError, IndexError, np.linalg.LinAlgError) as exc:
            raise FbxAnimationStackError(str(exc)) from exc

    @property
    def contract(self) -> FbxTransformContract:
        return self.transform_contract

    def select_animation_stack(
        self, name: str | None = None
    ) -> FbxAnimationStack | None:
        if not self.animation_stacks:
            if name:
                raise ValueError(
                    f"FBX has no animation stack named {name!r}: {self.path}"
                )
            self.selected_animation_stack = None
            self.curves = {}
            return None
        if not name:
            if len(self.animation_stacks) != 1:
                available = ", ".join(repr(row.name) for row in self.animation_stacks)
                raise ValueError(
                    "FBX contains multiple animations; choose an animation stack: "
                    + available
                )
            row = self.animation_stacks[0]
        else:
            matches = [item for item in self.animation_stacks if item.name == name]
            if not matches:
                available = ", ".join(repr(item.name) for item in self.animation_stacks)
                raise ValueError(
                    f"FBX animation stack {name!r} was not found; available stacks: {available}"
                )
            if len(matches) > 1:
                raise ValueError(f"FBX contains duplicate animation stack names: {name!r}")
            row = matches[0]
        if len(row.layer_ids) != 1:
            layers = ", ".join(repr(value) for value in row.layer_names) or "none"
            raise ValueError(
                f"FBX animation stack {row.name!r} contains {len(row.layer_ids)} "
                f"layers ({layers}); bake/flatten it to one animation layer before import"
            )
        self.selected_animation_stack = row
        self.curves = self._animation_curves(row.layer_ids[0])
        times = [time for curve_times, _ in self.curves.values() for time in curve_times]
        self.animation_start_tick = int(row.start_tick)
        self.animation_stop_tick = int(row.stop_tick)
        if times:
            if self.animation_start_tick == self.animation_stop_tick == 0:
                self.animation_start_tick = min(times)
            self.animation_stop_tick = max(self.animation_stop_tick, max(times))
        return row

    def animation_stack_activity(self) -> tuple[FbxAnimationStackActivity, ...]:
        rows: list[FbxAnimationStackActivity] = []
        for stack in self.animation_stacks:
            layer_ids = tuple(getattr(stack, "layer_ids", ()) or ())
            layer_names = tuple(getattr(stack, "layer_names", ()) or ())
            if not layer_ids and stack is getattr(
                self, "selected_animation_stack", None
            ):
                # Compatibility for synthetic/legacy callers that provide a
                # selected stack object plus already-isolated curves, but no
                # FBX AnimationLayer object IDs.
                curves = dict(getattr(self, "curves", {}) or {})
            elif len(layer_ids) != 1:
                rows.append(
                    FbxAnimationStackActivity(
                        stack.name,
                        layer_names,
                        0,
                        0,
                        0,
                        False,
                        "stack must contain exactly one baked animation layer",
                    )
                )
                continue
            else:
                try:
                    curves = self._animation_curves(layer_ids[0])
                except ValueError as exc:
                    rows.append(
                        FbxAnimationStackActivity(
                            stack.name,
                            layer_names,
                            0,
                            0,
                            0,
                            False,
                            str(exc),
                        )
                    )
                    continue
            limb_ids = set(getattr(self, "limb_models", {}).values())
            skeletal_curves = {
                key: curve
                for key, curve in curves.items()
                if key[0] in limb_ids
            }
            changing = sum(
                bool(
                    len(values) > 1
                    and max(values) - min(values) > 1.0e-8
                )
                for _times, values in skeletal_curves.values()
            )
            rows.append(
                FbxAnimationStackActivity(
                    stack.name,
                    layer_names,
                    len(skeletal_curves),
                    changing,
                    sum(
                        len(times)
                        for times, _values in skeletal_curves.values()
                    ),
                    True,
                )
            )
        return tuple(rows)

    def preferred_animation_stack(self) -> FbxAnimationStack | None:
        """Return one unambiguous useful stack without guessing between peers."""

        activity = self.animation_stack_activity()
        usable = [row for row in activity if row.usable]
        changing = [row for row in usable if row.changing]
        selected_name: str | None = None
        if len(changing) == 1:
            selected_name = changing[0].name
        elif not changing:
            with_channels = [row for row in usable if row.skeletal_channel_count > 0]
            if len(with_channels) == 1:
                selected_name = with_channels[0].name
            elif len(self.animation_stacks) == 1 and usable:
                selected_name = usable[0].name
        if selected_name is None:
            return None
        return next(
            (row for row in self.animation_stacks if row.name == selected_name),
            None,
        )

    def select_preferred_animation_stack(self) -> FbxAnimationStack | None:
        row = self.preferred_animation_stack()
        return self.select_animation_stack(row.name) if row is not None else None

    def frame_ticks(self, fps: int = 30) -> list[int]:
        if len(self.animation_stacks) > 1 and self.selected_animation_stack is None:
            self.select_animation_stack(None)
        start = int(self.animation_start_tick)
        stop = max(start, int(self.animation_stop_tick))
        count = int(math.ceil((stop - start) * fps / FBX_TICKS_PER_SECOND)) + 1
        return [
            min(stop, start + int(round(index * FBX_TICKS_PER_SECOND / fps)))
            for index in range(max(1, count))
        ]

    def frame_count(self, fps: int = 30) -> int:
        return len(self.frame_ticks(fps))

    def _linked(self, object_id: int, name: str | None = None):
        for kind, other, rest in (
            self.children.get(object_id, []) + self.parents.get(object_id, [])
        ):
            node = self.object_by_id.get(other)
            if node is not None and (name is None or node.name == name):
                yield kind, other, rest, node

    def _all_curves(self) -> dict[int, tuple[list[int], list[float]]]:
        if self._curve_cache:
            return self._curve_cache
        result: dict[int, tuple[list[int], list[float]]] = {}
        for object_id, node in self.object_by_id.items():
            if node.name != "AnimationCurve":
                continue
            times = [int(value) for value in (_child_value(node, "KeyTime", []) or [])]
            values = [
                float(value)
                for value in (_child_value(node, "KeyValueFloat", []) or [])
            ]
            if times and len(times) == len(values):
                result[object_id] = (times, values)
        self._curve_cache = result
        return result

    def _animation_curves(
        self, layer_id: int
    ) -> dict[tuple[int, str, str], tuple[list[int], list[float]]]:
        result: dict[tuple[int, str, str], tuple[list[int], list[float]]] = {}
        for kind, curve_node_id, _rest in self.children.get(layer_id, []):
            node = self.object_by_id.get(curve_node_id)
            if kind != "OO" or node is None or node.name != "AnimationCurveNode":
                continue
            model_id: int | None = None
            property_name = ""
            for parent_kind, parent_id, rest in self.parents.get(curve_node_id, []):
                parent = self.object_by_id.get(parent_id)
                if parent_kind == "OP" and parent is not None and parent.name == "Model":
                    model_id = parent_id
                    property_name = str(rest[0]) if rest else ""
            if model_id is None or not property_name:
                continue
            for child_kind, curve_id, rest in self.children.get(curve_node_id, []):
                curve = self.object_by_id.get(curve_id)
                if child_kind != "OP" or curve is None or curve.name != "AnimationCurve":
                    continue
                axis = str(rest[0]).split("|")[-1] if rest else ""
                times = [
                    int(value) for value in (_child_value(curve, "KeyTime", []) or [])
                ]
                values = [
                    float(value)
                    for value in (_child_value(curve, "KeyValueFloat", []) or [])
                ]
                if len(times) != len(values):
                    raise ValueError(
                        f"animation curve {curve_id} for "
                        f"{self.scene.model_names.get(model_id, model_id)!r} "
                        f"{property_name} {axis} has {len(times)} KeyTime rows and "
                        f"{len(values)} KeyValueFloat rows"
                    )
                if any(not math.isfinite(value) for value in values):
                    raise ValueError(
                        f"animation curve {curve_id} for "
                        f"{self.scene.model_names.get(model_id, model_id)!r} "
                        f"{property_name} {axis} contains a non-finite key value"
                    )
                if any(right < left for left, right in zip(times, times[1:])):
                    raise ValueError(
                        f"animation curve {curve_id} for "
                        f"{self.scene.model_names.get(model_id, model_id)!r} "
                        f"{property_name} {axis} has decreasing KeyTime rows"
                    )
                if times:
                    result[(model_id, property_name, axis)] = (times, values)
        return result

    def _animation_stack_inventory(self) -> tuple[FbxAnimationStack, ...]:
        layers = {
            int(node.properties[0]): _clean_name(node.properties[1])
            for node in self.objects.children
            if node.name == "AnimationLayer" and len(node.properties) >= 2
        }
        takes: dict[str, tuple[int, int]] = {}
        takes_node = self.top.get("Takes")
        if takes_node:
            for node in takes_node.children:
                local = _child(node, "LocalTime") if node.name == "Take" else None
                if local and len(local.properties) >= 2:
                    takes[_clean_name(node.properties[0])] = (
                        int(local.properties[0]),
                        int(local.properties[1]),
                    )
        rows: list[FbxAnimationStack] = []
        claimed: set[int] = set()
        for node in self.objects.children:
            if node.name != "AnimationStack" or len(node.properties) < 2:
                continue
            object_id = int(node.properties[0])
            name = _clean_name(node.properties[1])
            layer_ids = tuple(
                child
                for kind, child, _ in self.children.get(object_id, [])
                if kind == "OO" and child in layers
            )
            claimed.update(layer_ids)
            props = _properties70(node)
            start = int((props.get("LocalStart") or [0])[0])
            stop = int((props.get("LocalStop") or [start])[0])
            if name in takes:
                start, stop = takes[name]
            rows.append(
                FbxAnimationStack(
                    name,
                    tuple(layers[value] for value in layer_ids),
                    start,
                    stop,
                    object_id,
                    layer_ids,
                )
            )
        for object_id, name in layers.items():
            if object_id not in claimed:
                rows.append(
                    FbxAnimationStack(name, (name,), 0, 0, object_id, (object_id,))
                )
        return tuple(rows)

    def _animated_properties(self, tick: int) -> dict[int, dict[str, np.ndarray]]:
        result: dict[int, dict[str, np.ndarray]] = {}
        for (object_id, property_name, axis), curve in self.curves.items():
            props = _properties70(self.object_by_id.get(object_id))
            default = _vector_property(
                props,
                property_name,
                (1.0, 1.0, 1.0)
                if property_name == "Lcl Scaling"
                else (0.0, 0.0, 0.0),
            ).astype(float)
            value = result.setdefault(object_id, {}).setdefault(
                property_name, default.copy()
            )
            component = {"X": 0, "Y": 1, "Z": 2}.get(axis[-1:] if axis else "")
            if component is not None:
                value[component] = _sample_curve(curve, tick, value[component])
        return result

    def local_matrix(
        self,
        object_id: int,
        *,
        tick: int = 0,
        use_animation: bool = True,
    ) -> np.ndarray:
        overrides: dict[str, np.ndarray] = {}
        if use_animation:
            node = self.object_by_id[object_id]
            props = _properties70(node)
            for property_name, default in (
                ("Lcl Translation", (0.0, 0.0, 0.0)),
                ("Lcl Rotation", (0.0, 0.0, 0.0)),
                ("Lcl Scaling", (1.0, 1.0, 1.0)),
            ):
                value = _vector_property(props, property_name, default).astype(float)
                for index, axis in enumerate("XYZ"):
                    value[index] = _sample_curve(
                        self.curves.get((object_id, property_name, axis)),
                        tick,
                        value[index],
                    )
                overrides[property_name] = value
        return self.scene.model_local_matrix(
            object_id,
            property_overrides=overrides if use_animation else None,
            euler_matrix_resolver=_euler_matrix,
        )

    # Compatibility name used by existing retarget engines and third parties.
    def _local_matrix(
        self,
        object_id: int,
        tick: int = 0,
        use_animation: bool = True,
    ) -> np.ndarray:
        return self.local_matrix(object_id, tick=tick, use_animation=use_animation)

    def global_matrices(
        self, tick: int = 0, use_animation: bool = True
    ) -> dict[str, np.ndarray]:
        globals_by_id = self.scene.model_global_matrices(
            self.scene.model_ids,
            local_matrix_resolver=lambda object_id: self.local_matrix(
                object_id, tick=tick, use_animation=use_animation
            ),
        )
        result: dict[str, np.ndarray] = {}
        limb_ids = set(self.scene.limb_ids)
        for name, object_id in {**self.null_models, **self.limb_models}.items():
            value = globals_by_id[object_id]
            if object_id in limb_ids:
                value = self._scene_scale_normalizer(object_id) @ value
            result[name] = value.copy()
        return result

    def skeletal_local_matrices(
        self,
        tick: int = 0,
        use_animation: bool = True,
        *,
        globals_by_name: Mapping[str, np.ndarray] | None = None,
    ) -> dict[str, np.ndarray]:
        """Evaluate each LimbNode relative to its nearest LimbNode parent.

        Non-bone Model objects between joints participate in the global FBX
        evaluation, but are intentionally absent from Chrome rig topology.
        Deriving locals from canonical globals keeps their transform contribution
        when the skeletal hierarchy is collapsed for mapping/retargeting.
        """

        if globals_by_name is None:
            globals_by_name = self.global_matrices(
                tick=tick,
                use_animation=use_animation,
            )
        result: dict[str, np.ndarray] = {}
        for name in self.limb_models:
            current = np.asarray(globals_by_name[name], dtype=float)
            parent = self.parent_by_name.get(name)
            if parent in globals_by_name:
                try:
                    current = np.linalg.inv(
                        np.asarray(globals_by_name[str(parent)], dtype=float)
                    ) @ current
                except np.linalg.LinAlgError as exc:
                    raise ValueError(
                        f"FBX nearest skeletal parent {parent!r} is singular while "
                        f"evaluating collapsed local transform for {name!r}."
                    ) from exc
            if current.shape != (4, 4) or not np.isfinite(current).all():
                raise ValueError(
                    f"FBX collapsed skeletal local transform for {name!r} is not "
                    "a finite 4x4 matrix."
                )
            result[name] = current.copy()
        return result

    def _wrapper_id_for_bone(self, bone_id: int) -> int | None:
        limb = set(self.scene.limb_ids)
        parent = self.scene.model_parent_id(bone_id)
        wrapper_id: int | None = None
        visited: set[int] = set()
        while parent in self.scene.model_names and parent not in visited:
            visited.add(parent)
            if parent in limb:
                # A non-bone Model below another bone is part of the authored
                # joint-local transform, not a scene/unit wrapper.
                wrapper_id = None
            else:
                wrapper_id = parent
            parent = self.scene.model_parent_id(parent)
        return wrapper_id

    def _scene_scale_normalizer(self, bone_id: int) -> np.ndarray:
        cached = self._normalizer_cache.get(bone_id)
        if cached is not None:
            return cached.copy()
        wrapper_id = self._wrapper_id_for_bone(bone_id)
        if wrapper_id is None:
            result = np.eye(4, dtype=float)
        else:
            wrapper = self.scene.model_global_matrix(wrapper_id)
            linear = wrapper[:3, :3]
            scales = np.linalg.norm(linear, axis=0)
            uniform = float(np.mean(scales))
            if (
                not np.isfinite(uniform)
                or uniform <= 1.0e-12
                or max(abs(scales - uniform)) > max(1.0e-5, uniform * 1.0e-5)
                or abs(uniform - 1.0) < 1.0e-5
            ):
                result = np.eye(4, dtype=float)
            else:
                props = _properties70(self.object_by_id[wrapper_id])
                native_dlr = bool((props.get("dlr_native_anm2_export") or [0])[0])
                normalized = np.eye(4, dtype=float) if native_dlr else wrapper.copy()
                if not native_dlr:
                    normalized[:3, :3] = linear / uniform
                    normalized[:3, 3] = wrapper[:3, 3] / uniform
                try:
                    result = normalized @ np.linalg.inv(wrapper)
                except np.linalg.LinAlgError:
                    result = np.eye(4, dtype=float)
        self._normalizer_cache[bone_id] = result.copy()
        return result

    def wrapper_scale_normalization_factor(self, bone: int | str) -> float:
        """Return the canonical wrapper adjustment scale for one LimbNode."""

        bone_id = (
            int(bone)
            if not isinstance(bone, str)
            else int(self.limb_models[bone])
        )
        adjustment = self._scene_scale_normalizer(bone_id)
        scales = np.linalg.norm(adjustment[:3, :3], axis=0)
        if (
            not np.isfinite(scales).all()
            or min(scales) <= 1.0e-12
            or max(scales) - min(scales) > 1.0e-6
        ):
            return 1.0
        return float(np.mean(scales))

    def normalized_global_to_meters(
        self,
        bone: int | str,
        matrix: np.ndarray,
    ) -> np.ndarray:
        """Apply the one shared post-wrapper FBX-unit conversion."""

        return normalize_matrix_to_target_space(
            matrix,
            meters_per_unit=float(self.meters_per_unit),
            wrapper_scale_normalization_factor=(
                self.wrapper_scale_normalization_factor(bone)
            ),
        )

    def target_basis_matrix(self) -> np.ndarray:
        """Return this document's resolved FBX-scene -> Chrome basis."""

        return self.scene.coordinate_conversion_matrix(
            self.requested_orientation_policy
        )

    def normalized_matrix_to_target_space(
        self,
        bone: int | str,
        matrix: np.ndarray,
    ) -> np.ndarray:
        """Normalize a bind or animated matrix with the shared FBX contract."""

        return normalize_matrix_to_target_space(
            matrix,
            meters_per_unit=float(self.meters_per_unit),
            wrapper_scale_normalization_factor=(
                self.wrapper_scale_normalization_factor(bone)
            ),
            basis_matrix=self.target_basis_matrix(),
        )

    def _build_bind_inventory(self) -> None:
        resolution = resolve_bind_globals(
            self.scene,
            self.scene.limb_ids,
            normalizer=self._scene_scale_normalizer,
        )
        globals_by_name = {
            self.scene.model_names[object_id]: matrix.copy()
            for object_id, matrix in resolution.globals_by_id.items()
        }
        source_by_name = {
            self.scene.model_names[object_id]: source
            for object_id, source in resolution.source_by_id.items()
        }
        locals_by_name: dict[str, np.ndarray] = {}
        for name, matrix in globals_by_name.items():
            parent = self.parent_by_name.get(name)
            if parent in globals_by_name:
                try:
                    locals_by_name[name] = np.linalg.inv(globals_by_name[str(parent)]) @ matrix
                except np.linalg.LinAlgError:
                    locals_by_name[name] = np.full((4, 4), np.nan)
            else:
                locals_by_name[name] = matrix.copy()
        counts = resolution.coverage
        if counts["Pose::BindPose"] == len(self.limb_models):
            selected = "Pose::BindPose"
        elif counts["Pose::BindPose"] or counts["TransformLink"]:
            selected = "mixed_authoritative_with_fallback"
        else:
            selected = "ModelTransformsFallback"
        self.bind_global_matrices = globals_by_name
        self.bind_local_matrices = locals_by_name
        self.bind_source_by_bone = source_by_name
        self.bind_source = selected
        self.bind_coverage = dict(resolution.coverage)
        self.bind_warnings = list(resolution.warnings)
        self.bind_conflicts = resolution.conflicting_transform_links
        self.pose_transform_conflicts = resolution.conflicting_pose_transform_links
        normalized: dict[str, str] = {}
        duplicates: list[tuple[str, str]] = []
        for name in self.limb_models:
            key = unicodedata.normalize("NFKC", name).casefold()
            if key in normalized and normalized[key] != name:
                duplicates.append((normalized[key], name))
            normalized[key] = name
        self.normalized_name_collisions = tuple(duplicates)

    def bind_diagnostics(self) -> dict[str, Any]:
        return {
            "selected_bind_source": self.bind_source,
            "bind_coverage": dict(self.bind_coverage),
            "per_bone_source": dict(self.bind_source_by_bone),
            "warnings": list(self.bind_warnings),
            "conflicting_transform_links": list(self.bind_conflicts),
            "conflicting_pose_transform_links": list(self.pose_transform_conflicts),
        }

    def _non_bone_ancestor_ids(self) -> tuple[int, ...]:
        limb = set(self.scene.limb_ids)
        result: list[int] = []
        for bone_id in self.scene.limb_ids:
            parent = self.scene.model_parent_id(bone_id)
            visited: set[int] = set()
            while parent in self.scene.model_names and parent not in visited:
                visited.add(parent)
                if parent not in limb:
                    result.append(parent)
                parent = self.scene.model_parent_id(parent)
        return tuple(dict.fromkeys(result))

    def _wrapper_report(self) -> tuple[tuple[str, ...], dict[str, Any]]:
        wrapper_ids = tuple(
            dict.fromkeys(
                wrapper
                for bone_id in self.scene.limb_ids
                if (wrapper := self._wrapper_id_for_bone(bone_id)) is not None
            )
        )
        rows: dict[str, Any] = {}
        for wrapper_id in wrapper_ids:
            name = self.scene.model_names.get(wrapper_id, str(wrapper_id))
            matrix = self.scene.model_global_matrix(wrapper_id)
            scales = np.linalg.norm(matrix[:3, :3], axis=0)
            uniform = float(np.mean(scales))
            is_uniform = bool(
                np.isfinite(scales).all()
                and uniform > 1.0e-12
                and max(abs(scales - uniform)) <= max(1.0e-5, uniform * 1.0e-5)
            )
            rows[name] = {
                "scale_xyz": [float(value) for value in scales],
                "uniform": is_uniform,
                "uniform_scale": uniform if is_uniform else None,
                "normalization_factor": (
                    1.0 / uniform
                    if is_uniform and abs(uniform - 1.0) >= 1.0e-5
                    else 1.0
                ),
            }
        return (
            tuple(self.scene.model_names.get(value, str(value)) for value in wrapper_ids),
            rows,
        )

    def _build_transform_contract(self) -> FbxTransformContract:
        errors: list[str] = []
        warnings = [*self.scene.warnings, *self.bind_warnings]
        reflected: list[str] = []
        for object_id in self.scene.model_ids:
            name = self.scene.model_names.get(object_id, str(object_id))
            try:
                matrix = self.scene.model_global_matrix(object_id)
                linear = matrix[:3, :3]
                determinant = float(np.linalg.det(linear))
                scales = np.linalg.norm(linear, axis=0)
                if not math.isfinite(determinant) or abs(determinant) <= 1.0e-12:
                    errors.append(f"{name}: singular or non-finite evaluated Model transform")
                    continue
                if determinant < 0.0:
                    reflected.append(name)
                normalized = linear / scales
                shear = float(np.max(np.abs(normalized.T @ normalized - np.eye(3))))
                if shear > 1.0e-4:
                    errors.append(f"{name}: unsupported evaluated Model shear ({shear:.6g})")
            except (ValueError, np.linalg.LinAlgError) as exc:
                errors.append(f"{name}: transform evaluation failed: {exc}")
        if reflected:
            warnings.append(
                "Reflected or negative-scale Model transforms were found: "
                + ", ".join(reflected[:12])
            )
        wrappers, wrapper_scale = self._wrapper_report()
        ancestors = self._non_bone_ancestor_ids()
        try:
            source_sha256 = self.scene.sha256
        except OSError:
            source_sha256 = ""
        return FbxTransformContract(
            source_path=str(self.path),
            source_sha256=source_sha256,
            fbx_version=int(self.scene.version),
            meters_per_unit=float(self.scene.meters_per_unit),
            axis_settings=dict(self.scene.axis_settings),
            requested_orientation_policy=self.requested_orientation_policy,
            resolved_orientation_policy=self.resolved_orientation_policy,
            unit_conversion_count=1,
            axis_conversion_count=(
                0 if self.resolved_orientation_policy == "none" else 1
            ),
            wrapper_models=wrappers,
            wrapper_scale_normalization=wrapper_scale,
            bind_source_by_bone=dict(self.bind_source_by_bone),
            mesh_bind_source_by_geometry=dict(
                getattr(self.scene, "mesh_bind_source_by_geometry", {})
            ),
            bind_coverage=dict(self.bind_coverage),
            normalized_name_collisions=tuple(self.normalized_name_collisions),
            roots=tuple(
                name
                for name in self.limb_models
                if self.parent_by_name.get(name) is None
            ),
            non_bone_ancestors=tuple(
                self.scene.model_names.get(value, str(value)) for value in ancestors
            ),
            reflected_or_negative_scale_nodes=tuple(reflected),
            warnings=tuple(dict.fromkeys(str(value) for value in warnings)),
            errors=tuple(dict.fromkeys(str(value) for value in errors)),
        )


def _quaternion_wxyz_from_matrix(matrix: np.ndarray) -> np.ndarray:
    value = np.asarray(matrix, dtype=float)
    trace = float(np.trace(value))
    if trace > 0.0:
        scale = math.sqrt(trace + 1.0) * 2.0
        quaternion = np.asarray(
            (
                0.25 * scale,
                (value[2, 1] - value[1, 2]) / scale,
                (value[0, 2] - value[2, 0]) / scale,
                (value[1, 0] - value[0, 1]) / scale,
            ),
            dtype=float,
        )
    else:
        diagonal = int(np.argmax(np.diag(value)))
        if diagonal == 0:
            scale = math.sqrt(1.0 + value[0, 0] - value[1, 1] - value[2, 2]) * 2.0
            quaternion = np.asarray(
                (
                    (value[2, 1] - value[1, 2]) / scale,
                    0.25 * scale,
                    (value[0, 1] + value[1, 0]) / scale,
                    (value[0, 2] + value[2, 0]) / scale,
                )
            )
        elif diagonal == 1:
            scale = math.sqrt(1.0 + value[1, 1] - value[0, 0] - value[2, 2]) * 2.0
            quaternion = np.asarray(
                (
                    (value[0, 2] - value[2, 0]) / scale,
                    (value[0, 1] + value[1, 0]) / scale,
                    0.25 * scale,
                    (value[1, 2] + value[2, 1]) / scale,
                )
            )
        else:
            scale = math.sqrt(1.0 + value[2, 2] - value[0, 0] - value[1, 1]) * 2.0
            quaternion = np.asarray(
                (
                    (value[1, 0] - value[0, 1]) / scale,
                    (value[0, 2] + value[2, 0]) / scale,
                    (value[1, 2] + value[2, 1]) / scale,
                    0.25 * scale,
                )
            )
    norm = float(np.linalg.norm(quaternion))
    if not math.isfinite(norm) or norm <= 1.0e-12:
        raise ValueError("rotation quaternion is not finite or normalizable")
    return quaternion / norm


def _decompose_basis(matrix: np.ndarray):
    value = np.asarray(matrix, dtype=float)
    translation = value[:3, 3].copy()
    linear = value[:3, :3].copy()
    scales = np.linalg.norm(linear, axis=0)
    scales = np.where(scales < 1.0e-12, 1.0, scales)
    normalized = linear / scales
    u, _singular, vt = np.linalg.svd(normalized)
    rotation = u @ vt
    if np.linalg.det(rotation) < 0.0:
        u[:, -1] *= -1.0
        rotation = u @ vt
        scales[-1] *= -1.0
    return translation, _quaternion_wxyz_from_matrix(rotation), scales


AnimationStack = FbxAnimationStack

__all__ = [
    "AnimationStack",
    "FBX_TICKS_PER_SECOND",
    "FbxAnimationStack",
    "FbxBindResolution",
    "FbxDocument",
    "FbxNode",
    "FbxTransformContract",
    "normalize_matrix_to_target_space",
    "resolve_bind_globals",
    "_axis_rotation",
    "_child_value",
    "_clean_name",
    "_decompose_basis",
    "_euler_matrix",
    "_properties70",
    "_sample_curve",
]
