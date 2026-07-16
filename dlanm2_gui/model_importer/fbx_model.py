from __future__ import annotations

"""Binary FBX model, skeleton, skin and material reader.

The implementation deliberately mirrors the binary-FBX evaluator already used by
DL ReAnimated.  It supports the subset required by Blender, Mixamo and common
DCC FBX exports and keeps every coordinate-space conversion explicit.
"""

from collections import Counter, defaultdict
from dataclasses import dataclass, field
from pathlib import Path
from typing import Any, Callable, Iterable, Mapping, Sequence
import hashlib
import math
import struct
import zlib

import numpy as np

FBX_TICKS_PER_SECOND = 46_186_158_000

# Dying Light/Chrome model space uses the same basis recovered by the validated
# ANM2/FBX pipeline.  Common binary FBX files in this project are X-right,
# Y-up, Z-front.  Chrome's model basis is obtained with (x, z, -y).
FBX_Y_UP_TO_DYING_LIGHT = np.asarray(
    ((1.0, 0.0, 0.0, 0.0),
     (0.0, 0.0, 1.0, 0.0),
     (0.0, -1.0, 0.0, 0.0),
     (0.0, 0.0, 0.0, 1.0)),
    dtype=float,
)
DYING_LIGHT_TO_FBX_Y_UP = np.linalg.inv(FBX_Y_UP_TO_DYING_LIGHT)

ORIENTATION_POLICIES = {
    "auto",
    # Resolved-only value retained in reports and accepted for deterministic
    # replay of a previously analysed project.
    "fbx_global_settings",
    "fbx_y_up_to_dying_light",
    "none",
    "rotate_x_90",
    "rotate_x_minus_90",
    "rotate_y_90",
    "rotate_y_minus_90",
    "rotate_z_90",
    "rotate_z_minus_90",
}
ROTATION_ORDERS = {0: "XYZ", 1: "XZY", 2: "YZX", 3: "YXZ", 4: "ZXY", 5: "ZYX"}

IDENTITY_BLENDSHAPE_POSITION_EPSILON = 1.0e-8
IDENTITY_BLENDSHAPE_NORMAL_EPSILON = 1.0e-5
IDENTITY_BLENDSHAPE_WEIGHT_EPSILON = 1.0e-8

BLENDSHAPE_IDENTITY_NOOP = "identity_noop"
BLENDSHAPE_REAL_STATIC = "real_static_morph"
BLENDSHAPE_REAL_ANIMATED = "real_animated_morph"
BLENDSHAPE_MALFORMED = "malformed"


@dataclass(slots=True)
class FbxNode:
    name: str
    properties: list[Any]
    children: list["FbxNode"]
    start_offset: int
    end_offset: int


@dataclass(frozen=True, slots=True)
class FbxAnimationStack:
    name: str
    layer_names: tuple[str, ...]
    start_tick: int
    stop_tick: int


@dataclass(frozen=True, slots=True)
class FbxBone:
    object_id: int
    name: str
    parent_id: int | None
    global_bind: np.ndarray


@dataclass(frozen=True, slots=True)
class FbxCluster:
    object_id: int
    name: str
    bone_id: int | None
    bone_name: str | None
    indexes: tuple[int, ...]
    weights: tuple[float, ...]
    transform: np.ndarray | None
    transform_link: np.ndarray | None
    # Optional in older exporters.  When present this is the mesh/associate
    # model's bind transform, not the linked bone transform.
    transform_associate_model: np.ndarray | None = None


@dataclass(frozen=True, slots=True)
class FbxBlendShapeTarget:
    """One connected FBX Shape target with its channel and weight evidence.

    Shape payloads are sparse.  ``position_values`` and ``normal_values``
    retain the authored rows, while their ``*_deltas`` counterparts expose a
    common relative representation for classification.  Legacy FBX Shapes are
    relative by definition; modern Shapes may declare absolute storage.
    """

    shape_object_id: int | None
    shape_name: str
    channel_object_id: int | None
    channel_name: str
    base_geometry_id: int | None
    base_geometry_name: str
    control_point_indexes: tuple[int, ...]
    position_values: tuple[tuple[float, float, float], ...]
    position_deltas: tuple[tuple[float, float, float], ...]
    normal_values: tuple[tuple[float, float, float], ...]
    normal_deltas: tuple[tuple[float, float, float], ...]
    default_deform_percent: float
    full_weights: tuple[float, ...]
    animation_curve_ids: tuple[int, ...]
    animation_curve_times: tuple[int, ...]
    animation_curve_values: tuple[float, ...]
    shape_mode: str
    shape_mode_source: str
    classification: str
    malformed_fields: tuple[str, ...] = ()

    @property
    def name(self) -> str:
        return self.shape_name or self.channel_name or "UnnamedShape"

    @property
    def maximum_position_delta(self) -> float:
        return max(
            (abs(float(value)) for row in self.position_deltas for value in row),
            default=0.0,
        )

    @property
    def maximum_normal_delta(self) -> float:
        return max(
            (abs(float(value)) for row in self.normal_deltas for value in row),
            default=0.0,
        )

    @property
    def curve_key_count(self) -> int:
        return len(self.animation_curve_values)

    @property
    def curve_changes(self) -> bool:
        values = self.animation_curve_values
        return bool(
            len(values) > 1
            and max(values) - min(values) > IDENTITY_BLENDSHAPE_WEIGHT_EPSILON
        )

    def ignored_identity_report(self) -> dict[str, Any]:
        return {
            "name": self.name,
            "geometry": self.base_geometry_name,
            "maximum_position_delta": self.maximum_position_delta,
            "maximum_normal_delta": self.maximum_normal_delta,
            "default_weight": float(self.default_deform_percent),
            "curve_key_count": self.curve_key_count,
            "reason": (
                "the target contains no position deformation and its weight remains zero"
            ),
        }

    def to_dict(self) -> dict[str, Any]:
        return {
            "name": self.name,
            "shape_object_id": self.shape_object_id,
            "shape_name": self.shape_name,
            "channel_object_id": self.channel_object_id,
            "channel_name": self.channel_name,
            "base_geometry_id": self.base_geometry_id,
            "base_geometry_name": self.base_geometry_name,
            "control_point_indexes": list(self.control_point_indexes),
            "position_values": [list(row) for row in self.position_values],
            "position_deltas": [list(row) for row in self.position_deltas],
            "normal_values": [list(row) for row in self.normal_values],
            "normal_deltas": [list(row) for row in self.normal_deltas],
            "default_deform_percent": self.default_deform_percent,
            "full_weights": list(self.full_weights),
            "animation_curve_ids": list(self.animation_curve_ids),
            "animation_curve_times": list(self.animation_curve_times),
            "animation_curve_values": list(self.animation_curve_values),
            "shape_mode": self.shape_mode,
            "shape_mode_source": self.shape_mode_source,
            "classification": self.classification,
            "maximum_position_delta": self.maximum_position_delta,
            "maximum_normal_delta": self.maximum_normal_delta,
            "curve_key_count": self.curve_key_count,
            "curve_changes": self.curve_changes,
            "malformed_fields": list(self.malformed_fields),
        }

    def diagnostic_summary(self) -> dict[str, Any]:
        """Return bounded report evidence while the scene retains full payload rows."""

        return {
            "name": self.name,
            "shape_object_id": self.shape_object_id,
            "shape_name": self.shape_name,
            "channel_object_id": self.channel_object_id,
            "channel_name": self.channel_name,
            "base_geometry_id": self.base_geometry_id,
            "base_geometry_name": self.base_geometry_name,
            "sparse_index_count": len(self.control_point_indexes),
            "position_row_count": len(self.position_values),
            "normal_row_count": len(self.normal_values),
            "default_deform_percent": self.default_deform_percent,
            "full_weights": list(self.full_weights),
            "animation_curve_ids": list(self.animation_curve_ids),
            "animation_curve_times": list(self.animation_curve_times),
            "animation_curve_values": list(self.animation_curve_values),
            "shape_mode": self.shape_mode,
            "shape_mode_source": self.shape_mode_source,
            "classification": self.classification,
            "maximum_position_delta": self.maximum_position_delta,
            "maximum_normal_delta": self.maximum_normal_delta,
            "curve_key_count": self.curve_key_count,
            "curve_changes": self.curve_changes,
            "malformed_fields": list(self.malformed_fields),
        }


def _classify_blend_shape_target(
    *,
    position_deltas: Sequence[Sequence[float]],
    normal_deltas: Sequence[Sequence[float]],
    default_deform_percent: float,
    animation_curve_values: Sequence[float],
    malformed_fields: Sequence[str],
) -> str:
    if malformed_fields:
        return BLENDSHAPE_MALFORMED
    maximum_position_delta = max(
        (abs(float(value)) for row in position_deltas for value in row),
        default=0.0,
    )
    maximum_normal_delta = max(
        (abs(float(value)) for row in normal_deltas for value in row),
        default=0.0,
    )
    curve_values = tuple(float(value) for value in animation_curve_values)
    curve_changes = bool(
        len(curve_values) > 1
        and max(curve_values) - min(curve_values)
        > IDENTITY_BLENDSHAPE_WEIGHT_EPSILON
    )
    every_curve_key_is_zero = all(
        abs(value) <= IDENTITY_BLENDSHAPE_WEIGHT_EPSILON
        for value in curve_values
    )
    geometric_identity = (
        maximum_position_delta <= IDENTITY_BLENDSHAPE_POSITION_EPSILON
        and maximum_normal_delta <= IDENTITY_BLENDSHAPE_NORMAL_EPSILON
    )
    default_is_zero = (
        abs(float(default_deform_percent))
        <= IDENTITY_BLENDSHAPE_WEIGHT_EPSILON
    )
    if (
        geometric_identity
        and default_is_zero
        and every_curve_key_is_zero
        and not curve_changes
    ):
        return BLENDSHAPE_IDENTITY_NOOP
    if (
        not default_is_zero
        or not every_curve_key_is_zero
        or curve_changes
    ):
        return BLENDSHAPE_REAL_ANIMATED
    return BLENDSHAPE_REAL_STATIC


@dataclass(frozen=True, slots=True)
class FbxTriangleCorner:
    control_point_index: int
    polygon_vertex_index: int


@dataclass(frozen=True, slots=True)
class FbxTriangle:
    polygon_index: int
    corners: tuple[FbxTriangleCorner, FbxTriangleCorner, FbxTriangleCorner]


@dataclass(slots=True)
class FbxLayerElement:
    kind: str
    index: int
    name: str
    mapping: str
    reference: str
    direct: list[float] | list[int]
    indices: list[int]
    tuple_size: int

    def _mapped_index(
        self,
        *,
        control_point_index: int,
        polygon_vertex_index: int,
        polygon_index: int,
    ) -> int:
        mapping = self.mapping.casefold()
        if mapping in {"byvertice", "byvertex", "bycontrolpoint"}:
            return control_point_index
        if mapping == "bypolygonvertex":
            return polygon_vertex_index
        if mapping == "bypolygon":
            return polygon_index
        if mapping == "allsame":
            return 0
        raise ValueError(f"unsupported FBX layer mapping {self.mapping!r} for {self.kind}")

    def direct_index(
        self,
        *,
        control_point_index: int,
        polygon_vertex_index: int,
        polygon_index: int,
    ) -> int:
        mapped = self._mapped_index(
            control_point_index=control_point_index,
            polygon_vertex_index=polygon_vertex_index,
            polygon_index=polygon_index,
        )
        reference = self.reference.casefold()
        if reference == "direct":
            return mapped
        if reference in {"indextodirect", "index"}:
            if mapped < 0 or mapped >= len(self.indices):
                raise IndexError(
                    f"FBX {self.kind} index {mapped} outside {len(self.indices)} entries"
                )
            return int(self.indices[mapped])
        raise ValueError(f"unsupported FBX layer reference {self.reference!r} for {self.kind}")

    def value(
        self,
        *,
        control_point_index: int,
        polygon_vertex_index: int,
        polygon_index: int,
    ) -> tuple[float, ...]:
        index = self.direct_index(
            control_point_index=control_point_index,
            polygon_vertex_index=polygon_vertex_index,
            polygon_index=polygon_index,
        )
        start = index * self.tuple_size
        end = start + self.tuple_size
        if start < 0 or end > len(self.direct):
            raise IndexError(
                f"FBX {self.kind} direct row {index} outside {len(self.direct) // self.tuple_size} rows"
            )
        return tuple(float(value) for value in self.direct[start:end])


@dataclass(slots=True)
class FbxGeometry:
    object_id: int
    name: str
    model_id: int | None
    model_name: str
    control_points: np.ndarray
    polygons: tuple[tuple[FbxTriangleCorner, ...], ...]
    triangles: tuple[FbxTriangle, ...]
    layers: dict[str, list[FbxLayerElement]]
    material_ids: tuple[int, ...]
    material_names: tuple[str, ...]
    clusters: tuple[FbxCluster, ...]
    mesh_bind_global: np.ndarray
    geometric_transform: np.ndarray
    blend_shape_names: tuple[str, ...] = ()
    blend_shapes: tuple[FbxBlendShapeTarget, ...] = ()

    @property
    def skin_influences(self) -> dict[int, list[tuple[int, float]]]:
        result: dict[int, list[tuple[int, float]]] = defaultdict(list)
        for cluster in self.clusters:
            if cluster.bone_id is None:
                continue
            for index, weight in zip(cluster.indexes, cluster.weights):
                if abs(weight) > 1.0e-12:
                    result[int(index)].append((cluster.bone_id, float(weight)))
        return dict(result)

    def first_layer(self, kind: str) -> FbxLayerElement | None:
        rows = self.layers.get(kind, [])
        return min(rows, key=lambda row: row.index) if rows else None

    def material_slot_for_polygon(self, polygon_index: int) -> int:
        layer = self.first_layer("LayerElementMaterial")
        if layer is None:
            return 0
        mapping = layer.mapping.casefold()
        if mapping not in {"bypolygon", "allsame"}:
            raise ValueError(
                f"Geometry {self.name!r} has unsupported material mapping "
                f"{layer.mapping!r}; assign materials ByPolygon or AllSame and re-export."
            )
        try:
            value = layer.value(
                control_point_index=0,
                polygon_vertex_index=0,
                polygon_index=polygon_index,
            )
        except (ValueError, IndexError) as exc:
            raise ValueError(
                f"Geometry {self.name!r} polygon {polygon_index} has an invalid "
                f"{layer.kind} row: {exc}. Repair material layer indexes and re-export."
            ) from exc
        slot = int(value[0])
        slot_count = max(len(self.material_ids), len(self.material_names), 1)
        if slot < 0 or slot >= slot_count:
            raise ValueError(
                f"Geometry {self.name!r} polygon {polygon_index} selects material slot "
                f"{slot}, outside 0..{slot_count - 1}. Repair material assignments and "
                "re-export before building."
            )
        return slot


@dataclass(slots=True)
class FbxScene:
    path: Path
    version: int
    top: dict[str, FbxNode]
    object_by_id: dict[int, FbxNode]
    parents: dict[int, list[tuple[str, int, list[Any]]]]
    children: dict[int, list[tuple[str, int, list[Any]]]]
    model_ids: tuple[int, ...]
    limb_ids: tuple[int, ...]
    model_names: dict[int, str]
    model_subtypes: dict[int, str]
    material_names: dict[int, str]
    bind_pose_matrices: dict[int, np.ndarray]
    geometries: tuple[FbxGeometry, ...]
    animation_stacks: tuple[FbxAnimationStack, ...]
    blend_shape_names: tuple[str, ...]
    axis_settings: dict[str, int | float | None]
    meters_per_unit: float
    blend_shapes: tuple[FbxBlendShapeTarget, ...] = ()
    warnings: list[str] = field(default_factory=list)
    mesh_bind_source_by_geometry: dict[str, str] = field(default_factory=dict)

    @classmethod
    def from_path(cls, path: str | Path) -> "FbxScene":
        source = Path(path)
        data = source.read_bytes()
        if not data.startswith(b"Kaydara FBX Binary"):
            raise ValueError(
                f"Detected an ASCII, non-FBX, or unsupported non-binary file at {source}. "
                "The canonical importer requires binary FBX transform, bind, and animation "
                "records and cannot safely infer them from this input. Re-export as FBX "
                "2011-2020 Binary and retry. Exact Rig is viable only after that supported "
                "export contains the intended hierarchy; no model or animation output was written."
            )
        if len(data) < 27:
            raise ValueError(f"FBX file is truncated: {source}")
        version = struct.unpack_from("<I", data, 23)[0]
        if version < 7100 or version > 7700:
            raise ValueError(
                f"unsupported binary FBX version {version} in {source}. Re-export as FBX "
                "2011-2020 binary (versions 7100-7700); ASCII FBX is not supported."
            )
        nodes, _ = _parse_nodes(data, 27, version)
        top = {node.name: node for node in nodes}
        if "Objects" not in top or "Connections" not in top:
            raise ValueError("FBX is missing Objects or Connections")
        objects = top["Objects"]
        object_by_id = {
            int(node.properties[0]): node
            for node in objects.children
            if node.properties and isinstance(node.properties[0], int)
        }
        parents: dict[int, list[tuple[str, int, list[Any]]]] = defaultdict(list)
        children: dict[int, list[tuple[str, int, list[Any]]]] = defaultdict(list)
        for connection in top["Connections"].children:
            if len(connection.properties) < 3:
                continue
            kind, child_id, parent_id, *rest = connection.properties
            parents[int(child_id)].append((str(kind), int(parent_id), rest))
            children[int(parent_id)].append((str(kind), int(child_id), rest))

        model_ids = tuple(
            int(node.properties[0])
            for node in objects.children
            if node.name == "Model" and len(node.properties) >= 3
        )
        limb_ids = tuple(
            int(node.properties[0])
            for node in objects.children
            if node.name == "Model"
            and len(node.properties) >= 3
            and str(node.properties[2]) == "LimbNode"
        )
        model_names = {
            object_id: _clean_name(object_by_id[object_id].properties[1])
            for object_id in model_ids
        }
        model_subtypes = {
            object_id: str(object_by_id[object_id].properties[2])
            for object_id in model_ids
        }
        material_names = {
            int(node.properties[0]): _clean_name(node.properties[1])
            for node in objects.children
            if node.name == "Material" and len(node.properties) >= 2
        }
        bind_pose_matrices = _read_bind_pose_matrices(objects)
        axis = _axis_settings(top.get("GlobalSettings"))
        unit_factor = float(axis.get("UnitScaleFactor") or 1.0)
        if not math.isfinite(unit_factor) or unit_factor <= 0.0:
            raise ValueError(f"invalid FBX UnitScaleFactor {unit_factor!r}")
        meters_per_unit = unit_factor / 100.0

        scene = cls(
            path=source,
            version=version,
            top=top,
            object_by_id=object_by_id,
            parents=dict(parents),
            children=dict(children),
            model_ids=model_ids,
            limb_ids=limb_ids,
            model_names=model_names,
            model_subtypes=model_subtypes,
            material_names=material_names,
            bind_pose_matrices=bind_pose_matrices,
            geometries=(),
            animation_stacks=(),
            blend_shape_names=(),
            axis_settings=axis,
            meters_per_unit=meters_per_unit,
            blend_shapes=(),
        )
        scene._validate_axis_contract()
        scene.animation_stacks = scene._read_animation_stacks()
        scene.geometries = scene._read_geometries()
        scene.blend_shapes = scene._read_blend_shapes()
        scene.blend_shape_names = scene._read_blend_shape_names()
        for geometry in scene.geometries:
            geometry.blend_shapes = tuple(
                row
                for row in scene.blend_shapes
                if row.base_geometry_id == geometry.object_id
            )
            geometry.blend_shape_names = tuple(
                dict.fromkeys(row.channel_name or row.name for row in geometry.blend_shapes)
            )
        scene._add_scene_warnings()
        return scene

    @property
    def sha256(self) -> str:
        return hashlib.sha256(self.path.read_bytes()).hexdigest()

    @property
    def armature_roots(self) -> tuple[int, ...]:
        return tuple(
            object_id
            for object_id in self.limb_ids
            if self.nearest_limb_parent_id(object_id) is None
        )

    def model_parent_id(self, object_id: int) -> int | None:
        for kind, parent_id, _ in self.parents.get(object_id, []):
            if kind == "OO" and parent_id in self.model_names:
                return parent_id
        return None

    def nearest_limb_parent_id(self, object_id: int) -> int | None:
        """Return the nearest LimbNode ancestor through arbitrary Model wrappers.

        FBX exporters commonly insert ``Null`` or ``Root`` Model objects between
        armature joints.  Those objects still participate in global transform
        evaluation, but they must not make the child joint look like an
        independent skeleton root to animation mapping and compatibility code.
        """

        limb = set(self.limb_ids)
        visited: set[int] = set()
        parent = self.model_parent_id(object_id)
        while parent in self.model_names and parent not in visited:
            if parent in limb:
                return parent
            visited.add(parent)
            parent = self.model_parent_id(parent)
        return None

    def limb_children_ids(self, object_id: int) -> tuple[int, ...]:
        """Return nearest LimbNode descendants, traversing non-bone Models."""

        limb = set(self.limb_ids)
        result: list[int] = []
        visited: set[int] = set()

        def visit(current: int) -> None:
            if current in visited:
                raise ValueError(
                    "FBX model hierarchy cycle at "
                    f"{self.model_names.get(current, current)!r}"
                )
            visited.add(current)
            for child_id in self.model_children_ids(current, limb_only=False):
                if child_id in limb:
                    result.append(child_id)
                else:
                    visit(child_id)
            visited.remove(current)

        visit(object_id)
        return tuple(dict.fromkeys(result))

    def model_children_ids(self, object_id: int, *, limb_only: bool = False) -> tuple[int, ...]:
        allowed = set(self.limb_ids) if limb_only else set(self.model_ids)
        return tuple(
            child_id
            for kind, child_id, _ in self.children.get(object_id, [])
            if kind == "OO" and child_id in allowed
        )

    def model_local_matrix(
        self,
        object_id: int,
        *,
        property_overrides: Mapping[str, Sequence[float]] | None = None,
        euler_matrix_resolver: Callable[[Sequence[float], str], np.ndarray] | None = None,
    ) -> np.ndarray:
        node = self.object_by_id[object_id]
        props = _properties70(node)

        def animated_property(name: str, default: Sequence[float]) -> np.ndarray:
            if property_overrides is not None and name in property_overrides:
                value = np.asarray(property_overrides[name], dtype=float)
                if value.shape != (3,) or not np.isfinite(value).all():
                    raise ValueError(
                        f"FBX Model {self.model_names.get(object_id, object_id)!r} "
                        f"has an invalid animated {name} value"
                    )
                return value.copy()
            return _vector_property(props, name, default)

        translation = animated_property("Lcl Translation", (0.0, 0.0, 0.0))
        rotation = animated_property("Lcl Rotation", (0.0, 0.0, 0.0))
        scaling = animated_property("Lcl Scaling", (1.0, 1.0, 1.0))
        pre = _vector_property(props, "PreRotation", (0.0, 0.0, 0.0))
        post = _vector_property(props, "PostRotation", (0.0, 0.0, 0.0))
        rotation_offset = _vector_property(props, "RotationOffset", (0.0, 0.0, 0.0))
        rotation_pivot = _vector_property(props, "RotationPivot", (0.0, 0.0, 0.0))
        scaling_offset = _vector_property(props, "ScalingOffset", (0.0, 0.0, 0.0))
        scaling_pivot = _vector_property(props, "ScalingPivot", (0.0, 0.0, 0.0))
        order = ROTATION_ORDERS.get(int((props.get("RotationOrder") or [0])[0]), "XYZ")
        evaluate_euler = euler_matrix_resolver or _euler_matrix
        return (
            _translation_matrix(translation)
            @ _translation_matrix(rotation_offset)
            @ _translation_matrix(rotation_pivot)
            @ evaluate_euler(pre, order)
            @ evaluate_euler(rotation, order)
            @ np.linalg.inv(evaluate_euler(post, order))
            @ _translation_matrix(-rotation_pivot)
            @ _translation_matrix(scaling_offset)
            @ _translation_matrix(scaling_pivot)
            @ _scale_matrix(scaling)
            @ _translation_matrix(-scaling_pivot)
        )

    def model_geometric_matrix(self, object_id: int) -> np.ndarray:
        node = self.object_by_id[object_id]
        props = _properties70(node)
        translation = _vector_property(props, "GeometricTranslation", (0.0, 0.0, 0.0))
        rotation = _vector_property(props, "GeometricRotation", (0.0, 0.0, 0.0))
        scaling = _vector_property(props, "GeometricScaling", (1.0, 1.0, 1.0))
        order = ROTATION_ORDERS.get(int((props.get("RotationOrder") or [0])[0]), "XYZ")
        return _translation_matrix(translation) @ _euler_matrix(rotation, order) @ _scale_matrix(scaling)

    def model_global_matrices(
        self,
        object_ids: Iterable[int] | None = None,
        *,
        local_matrix_resolver: Callable[[int], np.ndarray] | None = None,
    ) -> dict[int, np.ndarray]:
        """Evaluate Model globals once through the canonical hierarchy solver."""

        cache: dict[int, np.ndarray] = {}
        visiting: set[int] = set()
        resolver = local_matrix_resolver or self.model_local_matrix

        def resolve(current: int) -> np.ndarray:
            if current in cache:
                return cache[current]
            if current in visiting:
                raise ValueError(f"FBX model hierarchy cycle at {self.model_names.get(current, current)}")
            visiting.add(current)
            local = np.asarray(resolver(current), dtype=float)
            if local.shape != (4, 4) or not np.isfinite(local).all():
                raise ValueError(
                    f"FBX Model {self.model_names.get(current, current)!r} "
                    "evaluated to a non-finite or malformed local matrix"
                )
            parent = self.model_parent_id(current)
            value = resolve(parent) @ local if parent in self.model_names else local
            visiting.remove(current)
            cache[current] = value
            return value

        selected = tuple(self.model_ids if object_ids is None else object_ids)
        return {object_id: resolve(object_id).copy() for object_id in selected}

    def model_global_matrix(self, object_id: int) -> np.ndarray:
        return self.model_global_matrices((object_id,))[object_id]

    def object_bind_matrix(self, object_id: int) -> np.ndarray:
        value = self.bind_pose_matrices.get(object_id)
        return value.copy() if value is not None else self.model_global_matrix(object_id)

    def bone_globals(self, bone_ids: Sequence[int]) -> dict[int, np.ndarray]:
        # Import lazily to avoid a module cycle: fbx_core owns the public
        # document/contract and imports this parser/data model.
        from ..fbx_core import resolve_bind_globals

        return resolve_bind_globals(self, bone_ids).globals_by_id

    def depth_first_bones_for_weighted_ids(self, weighted_ids: Iterable[int]) -> tuple[int, ...]:
        weighted = set(weighted_ids)
        limb = set(self.limb_ids)
        selected_roots: list[int] = []
        for root in self.armature_roots:
            descendants = set(self.depth_first_model_ids(root, limb_only=True))
            if descendants & weighted:
                selected_roots.append(root)
        if not selected_roots and self.armature_roots:
            selected_roots.append(self.armature_roots[0])
        result: list[int] = []
        for root in selected_roots:
            result.extend(self.depth_first_model_ids(root, limb_only=True))
        return tuple(dict.fromkeys(result))

    def depth_first_model_ids(self, root_id: int, *, limb_only: bool) -> tuple[int, ...]:
        result: list[int] = []

        def visit(object_id: int) -> None:
            result.append(object_id)
            children = (
                self.limb_children_ids(object_id)
                if limb_only
                else self.model_children_ids(object_id, limb_only=False)
            )
            for child_id in children:
                visit(child_id)

        visit(root_id)
        return tuple(result)

    def inventory(self) -> dict[str, Any]:
        weighted_bones = {
            cluster.bone_id
            for geometry in self.geometries
            for cluster in geometry.clusters
            if cluster.bone_id is not None and any(weight > 1.0e-12 for weight in cluster.weights)
        }
        geometry_rows: list[dict[str, Any]] = []
        for geometry in self.geometries:
            influences = geometry.skin_influences
            counts = [len([row for row in influences.get(i, []) if row[1] > 1.0e-12]) for i in range(len(geometry.control_points))]
            sums = [sum(weight for _, weight in influences.get(i, []) if weight > 1.0e-12) for i in range(len(geometry.control_points))]
            material_hist = Counter(geometry.material_slot_for_polygon(i) for i in range(len(geometry.polygons)))
            geometry_rows.append(
                {
                    "object_id": geometry.object_id,
                    "name": geometry.name,
                    "model_id": geometry.model_id,
                    "model_name": geometry.model_name,
                    "control_point_count": len(geometry.control_points),
                    "polygon_count": len(geometry.polygons),
                    "triangle_count": len(geometry.triangles),
                    "material_names": list(geometry.material_names),
                    "material_slot_polygon_counts": {str(k): v for k, v in sorted(material_hist.items())},
                    "cluster_count": len(geometry.clusters),
                    "weighted_bone_count": len({row[0] for values in influences.values() for row in values}),
                    "max_influences": max(counts, default=0),
                    "unweighted_control_points": sum(count == 0 for count in counts),
                    "weight_sum_off_one": sum(abs(value - 1.0) > 1.0e-4 for value in sums if value > 0.0),
                    "layer_elements": {
                        kind: [
                            {
                                "index": row.index,
                                "name": row.name,
                                "mapping": row.mapping,
                                "reference": row.reference,
                                "direct_count": len(row.direct) // max(1, row.tuple_size),
                                "index_count": len(row.indices),
                            }
                            for row in rows
                        ]
                        for kind, rows in geometry.layers.items()
                    },
                    "blend_shape_names": list(geometry.blend_shape_names),
                    "blend_shapes": [
                        row.diagnostic_summary() for row in geometry.blend_shapes
                    ],
                }
            )
        detected_mode = "skinned" if weighted_bones else "static"
        return {
            "format": "dl_reanimated_model_importer_fbx_inventory_v1",
            "path": str(self.path),
            "filename": self.path.name,
            "size": self.path.stat().st_size,
            "sha256": self.sha256,
            "fbx_version": self.version,
            "axis_settings": self.axis_settings,
            "meters_per_unit": self.meters_per_unit,
            "detected_mode": detected_mode,
            "model_count": len(self.model_ids),
            "mesh_geometry_count": len(self.geometries),
            "limb_node_count": len(self.limb_ids),
            "armature_roots": [self.model_names[row] for row in self.armature_roots],
            "weighted_bone_count": len(weighted_bones),
            "material_count": len(self.material_names),
            "materials": [self.material_names[key] for key in self.material_names],
            "animation_stacks": [
                {
                    "name": row.name,
                    "layer_names": list(row.layer_names),
                    "start_tick": row.start_tick,
                    "stop_tick": row.stop_tick,
                }
                for row in self.animation_stacks
            ],
            "blend_shape_names": list(self.blend_shape_names),
            "blend_shapes": [
                row.diagnostic_summary() for row in self.blend_shapes
            ],
            "ignored_identity_blendshapes": [
                row.ignored_identity_report()
                for row in self.blend_shapes
                if row.classification == BLENDSHAPE_IDENTITY_NOOP
            ],
            "geometries": geometry_rows,
            "warnings": list(self.warnings),
            "ready_for_source_build": bool(self.geometries),
        }

    # ------------------------------------------------------------------ readers
    def _read_geometries(self) -> tuple[FbxGeometry, ...]:
        result: list[FbxGeometry] = []
        for node in self.object_by_id.values():
            if node.name != "Geometry" or len(node.properties) < 3 or str(node.properties[2]) != "Mesh":
                continue
            geometry_id = int(node.properties[0])
            vertices = list(_child_value(node, "Vertices", []) or [])
            if len(vertices) % 3:
                raise ValueError(f"geometry {_clean_name(node.properties[1])!r} has malformed Vertices")
            control_points = np.asarray(vertices, dtype=float).reshape((-1, 3))
            geometry_name = _clean_name(node.properties[1])
            raw_indices = [int(value) for value in (_child_value(node, "PolygonVertexIndex", []) or [])]
            polygons: list[tuple[FbxTriangleCorner, ...]] = []
            triangles: list[FbxTriangle] = []
            current: list[FbxTriangleCorner] = []
            polygon_vertex_index = 0
            for raw in raw_indices:
                end = raw < 0
                cp_index = -raw - 1 if end else raw
                current.append(FbxTriangleCorner(cp_index, polygon_vertex_index))
                polygon_vertex_index += 1
                if end:
                    polygon_index = len(polygons)
                    polygon = tuple(current)
                    if len(polygon) < 3:
                        raise ValueError(
                            f"geometry {geometry_name!r} polygon {polygon_index} has only "
                            f"{len(polygon)} corners; remove the invalid face and re-export"
                        )
                    invalid_control_points = [
                        corner.control_point_index
                        for corner in polygon
                        if corner.control_point_index < 0
                        or corner.control_point_index >= len(control_points)
                    ]
                    if invalid_control_points:
                        raise ValueError(
                            f"geometry {geometry_name!r} polygon {polygon_index} references "
                            f"control point {invalid_control_points[0]} outside 0.."
                            f"{max(0, len(control_points) - 1)}; repair the mesh and re-export"
                        )
                    _validate_polygon_for_fan(
                        np.asarray(
                            [
                                control_points[corner.control_point_index]
                                for corner in polygon
                            ],
                            dtype=float,
                        ),
                        geometry_name=geometry_name,
                        polygon_index=polygon_index,
                    )
                    polygons.append(polygon)
                    for corner_index in range(1, len(polygon) - 1):
                        triangles.append(
                            FbxTriangle(
                                polygon_index,
                                (polygon[0], polygon[corner_index], polygon[corner_index + 1]),
                            )
                        )
                    current = []
            if current:
                raise ValueError(f"geometry {geometry_name!r} has unterminated polygon")

            model_id = self._linked_object_id(geometry_id, object_name="Model", subtype="Mesh")
            model_name = self.model_names.get(model_id, _clean_name(node.properties[1]))
            material_ids = self._model_material_ids(model_id) if model_id is not None else ()
            material_names = tuple(self.material_names.get(row, f"material_{row}") for row in material_ids)
            layers = _read_layer_elements(node)
            clusters = self._geometry_clusters(geometry_id)
            bind = self._resolve_geometry_mesh_bind(
                geometry_name=geometry_name,
                model_id=model_id,
                clusters=clusters,
            )
            geometric = self.model_geometric_matrix(model_id) if model_id is not None else np.eye(4, dtype=float)
            shapes = self._geometry_blend_shape_names(geometry_id)
            result.append(
                FbxGeometry(
                    object_id=geometry_id,
                    name=geometry_name,
                    model_id=model_id,
                    model_name=model_name,
                    control_points=control_points,
                    polygons=tuple(polygons),
                    triangles=tuple(triangles),
                    layers=layers,
                    material_ids=material_ids,
                    material_names=material_names,
                    clusters=clusters,
                    mesh_bind_global=bind,
                    geometric_transform=geometric,
                    blend_shape_names=shapes,
                )
            )
        return tuple(result)

    def _resolve_geometry_mesh_bind(
        self,
        *,
        geometry_name: str,
        model_id: int | None,
        clusters: Sequence[FbxCluster],
    ) -> np.ndarray:
        """Resolve the mesh model's bind transform without confusing it with a cluster offset.

        FBX Cluster ``Transform`` is exporter-dependent.  In common Blender
        files it is the offset from the associate mesh model to the linked
        bone, and can be identity even when the mesh object has a non-identity
        bind transform.  The mesh bind is instead available from the mesh
        Model's BindPose, ``TransformAssociateModel``, or (when both cluster
        matrices exist) ``TransformLink @ Transform``.

        Candidate sources are compared as groups.  A candidate supported by
        the other independent sources wins; deterministic source priority is
        used only for ties.  This keeps the usual BindPose path while allowing
        a corrupt/mismatched pose entry to be outvoted by cluster evidence.
        """

        def usable(label: str, matrix: np.ndarray | None) -> np.ndarray | None:
            if matrix is None:
                return None
            value = np.asarray(matrix, dtype=float)
            if value.shape != (4, 4) or not np.all(np.isfinite(value)):
                self.warnings.append(
                    f"{geometry_name}: ignored invalid {label} mesh-bind matrix."
                )
                return None
            if not np.allclose(value[3], (0.0, 0.0, 0.0, 1.0), rtol=0.0, atol=1.0e-7):
                self.warnings.append(
                    f"{geometry_name}: ignored non-affine {label} mesh-bind matrix."
                )
                return None
            determinant = float(np.linalg.det(value[:3, :3]))
            if not math.isfinite(determinant) or abs(determinant) <= 1.0e-12:
                self.warnings.append(
                    f"{geometry_name}: ignored singular {label} mesh-bind matrix."
                )
                return None
            return value

        def agree(left: np.ndarray, right: np.ndarray) -> bool:
            # FBX exporters commonly serialize independently calculated bind
            # matrices with low-order float noise; observed valid reconstruction
            # deltas can reach roughly 1.5e-5.
            return bool(np.allclose(left, right, rtol=1.0e-5, atol=5.0e-5))

        def representative(
            label: str,
            rows: Sequence[tuple[str, np.ndarray | None]],
        ) -> np.ndarray | None:
            valid: list[tuple[str, np.ndarray]] = []
            for row_label, row_matrix in rows:
                value = usable(row_label, row_matrix)
                if value is not None:
                    valid.append((row_label, value))
            if not valid:
                return None

            # A malformed first cluster must not control the whole mesh.  Pick
            # the matrix agreeing with the most peers, preserving input order
            # only when the group has no stronger consensus.
            scores = [
                sum(agree(matrix, other) for _, other in valid)
                for _, matrix in valid
            ]
            chosen_index = max(range(len(valid)), key=lambda index: scores[index])
            chosen_label, chosen = valid[chosen_index]
            disagreeing = [
                row_label
                for row_label, matrix in valid
                if not agree(chosen, matrix)
            ]
            if disagreeing:
                self.warnings.append(
                    f"{geometry_name}: {label} mesh-bind candidates disagree; "
                    f"using {chosen_label}, disagreeing candidates: {', '.join(disagreeing)}."
                )
            return chosen

        bind_pose = usable(
            "mesh Model BindPose",
            self.bind_pose_matrices.get(model_id) if model_id is not None else None,
        )
        associate = representative(
            "TransformAssociateModel",
            tuple(
                (f"cluster {cluster.name} TransformAssociateModel", cluster.transform_associate_model)
                for cluster in clusters
            ),
        )
        reconstructed = representative(
            "reconstructed TransformLink @ Transform",
            tuple(
                (
                    f"cluster {cluster.name} TransformLink @ Transform",
                    (
                        cluster.transform_link @ cluster.transform
                        if cluster.transform_link is not None and cluster.transform is not None
                        else None
                    ),
                )
                for cluster in clusters
            ),
        )
        evaluated = usable(
            "evaluated mesh Model transform",
            self.model_global_matrix(model_id) if model_id is not None else None,
        )

        candidates = [
            ("mesh Model BindPose", bind_pose),
            ("TransformAssociateModel", associate),
            ("TransformLink @ Transform reconstruction", reconstructed),
            ("evaluated mesh Model transform", evaluated),
        ]
        valid_candidates = [(label, matrix) for label, matrix in candidates if matrix is not None]
        if not valid_candidates:
            if clusters:
                self.warnings.append(
                    f"{geometry_name}: no usable mesh-bind evidence was found; using identity."
                )
            if not hasattr(self, "mesh_bind_source_by_geometry"):
                self.mesh_bind_source_by_geometry = {}
            self.mesh_bind_source_by_geometry[geometry_name] = "identity fallback"
            return np.eye(4, dtype=float)

        support = [
            sum(agree(matrix, other) for _, other in valid_candidates)
            for _, matrix in valid_candidates
        ]
        selected_index = max(range(len(valid_candidates)), key=lambda index: support[index])
        selected_label, selected = valid_candidates[selected_index]
        disagreeing = [
            (label, float(np.max(np.abs(selected - matrix))))
            for label, matrix in valid_candidates
            if not agree(selected, matrix)
        ]
        if disagreeing:
            details = ", ".join(
                f"{label} (max delta {delta:.6g})"
                for label, delta in disagreeing
            )
            self.warnings.append(
                f"{geometry_name}: mesh-bind sources disagree; using {selected_label}; {details}."
            )
        if not hasattr(self, "mesh_bind_source_by_geometry"):
            self.mesh_bind_source_by_geometry = {}
        self.mesh_bind_source_by_geometry[geometry_name] = selected_label
        return selected.copy()

    def _geometry_clusters(self, geometry_id: int) -> tuple[FbxCluster, ...]:
        skin_ids = self._linked_object_ids(geometry_id, object_name="Deformer", subtype="Skin")
        clusters: list[FbxCluster] = []
        for skin_id in skin_ids:
            for cluster_id in self._linked_object_ids(skin_id, object_name="Deformer", subtype="Cluster"):
                node = self.object_by_id[cluster_id]
                bone_id = self._linked_object_id(cluster_id, object_name="Model", subtype="LimbNode")
                indexes = tuple(int(value) for value in (_child_value(node, "Indexes", []) or []))
                weights = tuple(float(value) for value in (_child_value(node, "Weights", []) or []))
                if len(indexes) != len(weights):
                    raise ValueError(f"cluster {_clean_name(node.properties[1])!r} index/weight counts differ")
                clusters.append(
                    FbxCluster(
                        object_id=cluster_id,
                        name=_clean_name(node.properties[1]),
                        bone_id=bone_id,
                        bone_name=self.model_names.get(bone_id),
                        indexes=indexes,
                        weights=weights,
                        transform=_matrix_from_array(_child_value(node, "Transform", [])),
                        transform_link=_matrix_from_array(_child_value(node, "TransformLink", [])),
                        transform_associate_model=_matrix_from_array(
                            _child_value(node, "TransformAssociateModel", [])
                        ),
                    )
                )
        return tuple(dict((row.object_id, row) for row in clusters).values())

    def _linked_object_ids(self, object_id: int, *, object_name: str, subtype: str | None = None) -> tuple[int, ...]:
        result: list[int] = []
        for kind, other_id, _ in self.children.get(object_id, []) + self.parents.get(object_id, []):
            node = self.object_by_id.get(other_id)
            if kind not in {"OO", "OP"} or node is None or node.name != object_name:
                continue
            if subtype is not None and (len(node.properties) < 3 or str(node.properties[2]) != subtype):
                continue
            result.append(other_id)
        return tuple(dict.fromkeys(result))

    def _linked_object_id(self, object_id: int, *, object_name: str, subtype: str | None = None) -> int | None:
        rows = self._linked_object_ids(object_id, object_name=object_name, subtype=subtype)
        return rows[0] if rows else None

    def _model_material_ids(self, model_id: int | None) -> tuple[int, ...]:
        if model_id is None:
            return ()
        result: list[int] = []
        for kind, other_id, _ in self.children.get(model_id, []) + self.parents.get(model_id, []):
            if kind != "OO" or other_id not in self.material_names:
                continue
            result.append(other_id)
        return tuple(dict.fromkeys(result))

    def _geometry_blend_shape_names(self, geometry_id: int) -> tuple[str, ...]:
        names: list[str] = []
        blend_ids = self._linked_object_ids(geometry_id, object_name="Deformer", subtype="BlendShape")
        for blend_id in blend_ids:
            channel_ids = self._linked_object_ids(blend_id, object_name="Deformer", subtype="BlendShapeChannel")
            for channel_id in channel_ids:
                names.append(_clean_name(self.object_by_id[channel_id].properties[1]))
        return tuple(dict.fromkeys(names))

    def _read_blend_shapes(self) -> tuple[FbxBlendShapeTarget, ...]:
        """Read and classify connected sparse Shape geometry without mutating it."""

        shape_nodes = {
            object_id: node
            for object_id, node in self.object_by_id.items()
            if node.name == "Geometry"
            and len(node.properties) >= 3
            and str(node.properties[2]) == "Shape"
        }
        channel_nodes = {
            object_id: node
            for object_id, node in self.object_by_id.items()
            if node.name == "Deformer"
            and len(node.properties) >= 3
            and str(node.properties[2]) == "BlendShapeChannel"
        }
        targets: list[FbxBlendShapeTarget] = []
        connected_channels: set[int] = set()

        for shape_id, shape_node in sorted(shape_nodes.items()):
            channel_ids = self._linked_object_ids(
                shape_id,
                object_name="Deformer",
                subtype="BlendShapeChannel",
            )
            connected_channels.update(channel_ids)
            targets.append(
                self._read_blend_shape_target(
                    shape_id=shape_id,
                    shape_node=shape_node,
                    channel_ids=channel_ids,
                )
            )

        # A channel without Shape geometry is not safe to discard merely
        # because there are no deltas to inspect.  Keep it as a malformed
        # record so model preflight can name the broken connection.
        for channel_id, channel_node in sorted(channel_nodes.items()):
            if channel_id in connected_channels:
                continue
            targets.append(
                self._read_blend_shape_target(
                    shape_id=None,
                    shape_node=None,
                    channel_ids=(channel_id,),
                )
            )
        return tuple(
            sorted(
                targets,
                key=lambda row: (
                    row.base_geometry_name.casefold(),
                    row.name.casefold(),
                    row.shape_object_id if row.shape_object_id is not None else -1,
                ),
            )
        )

    def _read_blend_shape_target(
        self,
        *,
        shape_id: int | None,
        shape_node: FbxNode | None,
        channel_ids: Sequence[int],
    ) -> FbxBlendShapeTarget:
        errors: list[str] = []
        shape_name = (
            _clean_name(shape_node.properties[1])
            if shape_node is not None and len(shape_node.properties) >= 2
            else ""
        )
        if len(channel_ids) != 1:
            errors.append(
                "channel_connection: shape "
                f"{shape_name or shape_id!r} is connected to {len(channel_ids)} "
                f"BlendShapeChannel objects {list(channel_ids)}; expected exactly one"
            )
        channel_id = int(channel_ids[0]) if channel_ids else None
        channel_node = self.object_by_id.get(channel_id) if channel_id is not None else None
        channel_name = (
            _clean_name(channel_node.properties[1])
            if channel_node is not None and len(channel_node.properties) >= 2
            else ""
        )
        if shape_node is None:
            errors.append(
                "shape_connection: channel "
                f"{channel_name or channel_id!r} has no connected Geometry subtype Shape"
            )

        blend_ids = (
            self._linked_object_ids(
                channel_id,
                object_name="Deformer",
                subtype="BlendShape",
            )
            if channel_id is not None
            else ()
        )
        if len(blend_ids) != 1:
            errors.append(
                "base_geometry_connection: channel "
                f"{channel_name or channel_id!r} is connected to {len(blend_ids)} "
                f"BlendShape deformers {list(blend_ids)}; expected exactly one"
            )
        base_ids = tuple(
            dict.fromkeys(
                geometry_id
                for blend_id in blend_ids
                for geometry_id in self._linked_object_ids(
                    blend_id,
                    object_name="Geometry",
                    subtype="Mesh",
                )
            )
        )
        if len(base_ids) != 1:
            errors.append(
                "base_geometry_connection: shape "
                f"{shape_name or shape_id!r} resolves to {len(base_ids)} base Mesh "
                f"geometries {list(base_ids)}; expected exactly one"
            )
        base_geometry_id = int(base_ids[0]) if base_ids else None
        base_node = (
            self.object_by_id.get(base_geometry_id)
            if base_geometry_id is not None
            else None
        )
        base_geometry_name = (
            _clean_name(base_node.properties[1])
            if base_node is not None and len(base_node.properties) >= 2
            else "<unresolved>"
        )
        base_geometry = next(
            (
                row
                for row in self.geometries
                if row.object_id == base_geometry_id
            ),
            None,
        )
        if base_geometry_id is not None and base_geometry is None:
            errors.append(
                "base_geometry_connection: resolved Geometry "
                f"{base_geometry_name!r} ({base_geometry_id}) is not a parsed Mesh"
            )

        indexes = self._shape_integer_array(
            shape_node,
            "Indexes",
            errors,
        )
        raw_positions = self._shape_numeric_array(
            shape_node,
            "Vertices",
            errors,
        )
        raw_normals = self._shape_numeric_array(
            shape_node,
            "Normals",
            errors,
        )
        position_values = self._shape_vector_rows(
            raw_positions,
            "Vertices",
            errors,
        )
        normal_values = self._shape_vector_rows(
            raw_normals,
            "Normals",
            errors,
        )
        if len(indexes) != len(position_values):
            errors.append(
                "Indexes/Vertices: sparse index count "
                f"{len(indexes)} does not match position-row count {len(position_values)}"
            )
        if normal_values and len(normal_values) != len(position_values):
            errors.append(
                "Normals: normal-row count "
                f"{len(normal_values)} must be zero or match position-row count "
                f"{len(position_values)}"
            )
        if base_geometry is not None:
            for row_index, control_point_index in enumerate(indexes):
                if not 0 <= control_point_index < len(base_geometry.control_points):
                    errors.append(
                        f"Indexes[{row_index}]: sparse control-point index "
                        f"{control_point_index} is outside base geometry "
                        f"{base_geometry_name!r} range 0.."
                        f"{max(0, len(base_geometry.control_points) - 1)}"
                    )

        default_deform_percent = self._shape_channel_default(channel_node, errors)
        full_weights = self._shape_numeric_array(
            channel_node,
            "FullWeights",
            errors,
        )
        curve_ids, curve_times, curve_values = self._shape_channel_curves(
            channel_id,
            errors,
        )
        shape_mode, shape_mode_source = self._shape_storage_mode(
            shape_node,
            errors,
        )

        position_deltas = position_values
        normal_deltas = normal_values
        if shape_mode == "absolute" and base_geometry is not None:
            converted_positions: list[tuple[float, float, float]] = []
            for row_index, value in enumerate(position_values):
                if row_index >= len(indexes):
                    break
                control_point_index = indexes[row_index]
                if not 0 <= control_point_index < len(base_geometry.control_points):
                    converted_positions.append(value)
                    continue
                base = base_geometry.control_points[control_point_index]
                converted_positions.append(
                    tuple(float(value[axis] - base[axis]) for axis in range(3))
                )
            position_deltas = tuple(converted_positions)

            normal_layer = base_geometry.first_layer("LayerElementNormal")
            if (
                normal_values
                and normal_layer is not None
                and normal_layer.mapping.casefold()
                in {"byvertice", "byvertex", "bycontrolpoint"}
            ):
                converted_normals: list[tuple[float, float, float]] = []
                for row_index, value in enumerate(normal_values):
                    if row_index >= len(indexes):
                        break
                    control_point_index = indexes[row_index]
                    try:
                        base = normal_layer.value(
                            control_point_index=control_point_index,
                            polygon_vertex_index=0,
                            polygon_index=0,
                        )
                    except (ValueError, IndexError) as exc:
                        errors.append(
                            f"Normals[{row_index}]: absolute Shape normal cannot resolve "
                            f"base geometry {base_geometry_name!r} normal: {exc}"
                        )
                        converted_normals.append(value)
                    else:
                        converted_normals.append(
                            tuple(float(value[axis] - base[axis]) for axis in range(3))
                        )
                normal_deltas = tuple(converted_normals)

        classification = _classify_blend_shape_target(
            position_deltas=position_deltas,
            normal_deltas=normal_deltas,
            default_deform_percent=default_deform_percent,
            animation_curve_values=curve_values,
            malformed_fields=errors,
        )
        return FbxBlendShapeTarget(
            shape_object_id=shape_id,
            shape_name=shape_name,
            channel_object_id=channel_id,
            channel_name=channel_name,
            base_geometry_id=base_geometry_id,
            base_geometry_name=base_geometry_name,
            control_point_indexes=indexes,
            position_values=position_values,
            position_deltas=position_deltas,
            normal_values=normal_values,
            normal_deltas=normal_deltas,
            default_deform_percent=default_deform_percent,
            full_weights=full_weights,
            animation_curve_ids=curve_ids,
            animation_curve_times=curve_times,
            animation_curve_values=curve_values,
            shape_mode=shape_mode,
            shape_mode_source=shape_mode_source,
            classification=classification,
            malformed_fields=tuple(errors),
        )

    @staticmethod
    def _shape_numeric_array(
        node: FbxNode | None,
        field_name: str,
        errors: list[str],
    ) -> tuple[float, ...]:
        if node is None:
            return ()
        raw = _child_value(node, field_name, []) or []
        if not isinstance(raw, (list, tuple)):
            raw = [raw]
        values: list[float] = []
        for index, item in enumerate(raw):
            try:
                value = float(item)
            except (TypeError, ValueError):
                errors.append(
                    f"{field_name}[{index}]: value {item!r} is not numeric"
                )
                continue
            if not math.isfinite(value):
                errors.append(
                    f"{field_name}[{index}]: value {item!r} is not finite"
                )
            values.append(value)
        return tuple(values)

    @staticmethod
    def _shape_integer_array(
        node: FbxNode | None,
        field_name: str,
        errors: list[str],
    ) -> tuple[int, ...]:
        if node is None:
            return ()
        raw = _child_value(node, field_name, []) or []
        if not isinstance(raw, (list, tuple)):
            raw = [raw]
        values: list[int] = []
        for index, item in enumerate(raw):
            try:
                numeric = float(item)
                value = int(item)
            except (TypeError, ValueError, OverflowError):
                errors.append(
                    f"{field_name}[{index}]: value {item!r} is not an integer"
                )
                continue
            if not math.isfinite(numeric) or numeric != value:
                errors.append(
                    f"{field_name}[{index}]: value {item!r} is not a finite integer"
                )
            values.append(value)
        return tuple(values)

    @staticmethod
    def _shape_vector_rows(
        values: Sequence[float],
        field_name: str,
        errors: list[str],
    ) -> tuple[tuple[float, float, float], ...]:
        if len(values) % 3:
            errors.append(
                f"{field_name}: component count {len(values)} is not divisible by 3"
            )
        return tuple(
            (float(values[index]), float(values[index + 1]), float(values[index + 2]))
            for index in range(0, len(values) - 2, 3)
        )

    @staticmethod
    def _shape_channel_default(
        channel_node: FbxNode | None,
        errors: list[str],
    ) -> float:
        if channel_node is None:
            return 0.0
        raw = _child_value(channel_node, "DeformPercent", None)
        if raw is None:
            props = _properties70(channel_node)
            values = props.get("DeformPercent") or props.get("Deform Percent") or [0.0]
            raw = values[0] if values else 0.0
        try:
            value = float(raw)
        except (TypeError, ValueError):
            errors.append(f"DeformPercent: value {raw!r} is not numeric")
            return math.nan
        if not math.isfinite(value):
            errors.append(f"DeformPercent: value {raw!r} is not finite")
        return value

    def _shape_channel_curves(
        self,
        channel_id: int | None,
        errors: list[str],
    ) -> tuple[tuple[int, ...], tuple[int, ...], tuple[float, ...]]:
        if channel_id is None:
            return (), (), ()
        curve_node_ids: list[int] = []
        for kind, other_id, rest in (
            self.children.get(channel_id, []) + self.parents.get(channel_id, [])
        ):
            node = self.object_by_id.get(other_id)
            property_name = str(rest[0]) if rest else ""
            if (
                kind == "OP"
                and node is not None
                and node.name == "AnimationCurveNode"
                and "deform" in property_name.casefold()
            ):
                curve_node_ids.append(int(other_id))
        curve_ids: list[int] = []
        times: list[int] = []
        values: list[float] = []
        for curve_node_id in dict.fromkeys(curve_node_ids):
            for kind, other_id, _rest in (
                self.children.get(curve_node_id, [])
                + self.parents.get(curve_node_id, [])
            ):
                curve = self.object_by_id.get(other_id)
                if kind != "OP" or curve is None or curve.name != "AnimationCurve":
                    continue
                if other_id in curve_ids:
                    continue
                curve_ids.append(int(other_id))
                raw_times = _child_value(curve, "KeyTime", []) or []
                raw_values = _child_value(curve, "KeyValueFloat", []) or []
                if not isinstance(raw_times, (list, tuple)):
                    raw_times = [raw_times]
                if not isinstance(raw_values, (list, tuple)):
                    raw_values = [raw_values]
                if len(raw_times) != len(raw_values):
                    errors.append(
                        f"AnimationCurve {other_id} KeyTime/KeyValueFloat: counts "
                        f"{len(raw_times)} and {len(raw_values)} differ"
                    )
                for key_index, raw_time in enumerate(raw_times):
                    try:
                        time = int(raw_time)
                    except (TypeError, ValueError, OverflowError):
                        errors.append(
                            f"AnimationCurve {other_id} KeyTime[{key_index}]: "
                            f"value {raw_time!r} is not an integer"
                        )
                        continue
                    times.append(time)
                for key_index, raw_value in enumerate(raw_values):
                    try:
                        value = float(raw_value)
                    except (TypeError, ValueError):
                        errors.append(
                            f"AnimationCurve {other_id} KeyValueFloat[{key_index}]: "
                            f"value {raw_value!r} is not numeric"
                        )
                        continue
                    if not math.isfinite(value):
                        errors.append(
                            f"AnimationCurve {other_id} KeyValueFloat[{key_index}]: "
                            f"value {raw_value!r} is not finite"
                        )
                    values.append(value)
        return tuple(curve_ids), tuple(times), tuple(values)

    @staticmethod
    def _shape_storage_mode(
        shape_node: FbxNode | None,
        errors: list[str],
    ) -> tuple[str, str]:
        if shape_node is None:
            return "relative", "fbx_legacy_default"
        props = _properties70(shape_node)

        def setting(name: str) -> Any | None:
            direct = _child_value(shape_node, name, None)
            if direct is not None:
                return direct
            values = props.get(name)
            return values[0] if values else None

        legacy = setting("LegacyStyle")
        absolute = setting("AbsoluteMode")
        try:
            if legacy is not None and bool(int(legacy)):
                return "relative", "LegacyStyle"
            if absolute is not None:
                return (
                    ("absolute" if bool(int(absolute)) else "relative"),
                    "AbsoluteMode",
                )
        except (TypeError, ValueError, OverflowError):
            errors.append(
                "ShapeMode: LegacyStyle/AbsoluteMode must be a boolean integer, got "
                f"LegacyStyle={legacy!r}, AbsoluteMode={absolute!r}"
            )
        return "relative", "fbx_legacy_default"

    def _read_animation_stacks(self) -> tuple[FbxAnimationStack, ...]:
        layers = {
            object_id: _clean_name(node.properties[1])
            for object_id, node in self.object_by_id.items()
            if node.name == "AnimationLayer" and len(node.properties) >= 2
        }
        takes: dict[str, tuple[int, int]] = {}
        takes_node = self.top.get("Takes")
        if takes_node:
            for row in takes_node.children:
                if row.name != "Take" or not row.properties:
                    continue
                local_time = _child(row, "LocalTime")
                if local_time and len(local_time.properties) >= 2:
                    takes[_clean_name(row.properties[0])] = (
                        int(local_time.properties[0]),
                        int(local_time.properties[1]),
                    )
        result: list[FbxAnimationStack] = []
        for object_id, node in self.object_by_id.items():
            if node.name != "AnimationStack" or len(node.properties) < 2:
                continue
            name = _clean_name(node.properties[1])
            layer_ids = [
                other_id
                for kind, other_id, _ in self.children.get(object_id, []) + self.parents.get(object_id, [])
                if kind == "OO" and other_id in layers
            ]
            props = _properties70(node)
            start = int((props.get("LocalStart") or [0])[0])
            stop = int((props.get("LocalStop") or [start])[0])
            if name in takes:
                start, stop = takes[name]
            result.append(FbxAnimationStack(name, tuple(layers[row] for row in dict.fromkeys(layer_ids)), start, stop))
        return tuple(result)

    def _read_blend_shape_names(self) -> tuple[str, ...]:
        return tuple(
            dict.fromkeys(
                _clean_name(node.properties[1])
                for node in self.object_by_id.values()
                if node.name == "Deformer"
                and len(node.properties) >= 3
                and str(node.properties[2]) == "BlendShapeChannel"
            )
        )

    def coordinate_conversion_matrix(self, policy: str = "auto") -> np.ndarray:
        """Return the explicit FBX-scene to Chrome model-space basis matrix.

        FBX Model and BindPose matrices are already evaluated in the coordinate
        system declared by ``GlobalSettings``.  The supported automatic
        contract is X-right/Y-up/Z-front, which is also the axis layout used by
        the Dying Light source skeletons, so ``auto`` must not add a second
        quarter-turn.  Manual policies remain available for deliberately
        unusual assets and legacy projects.
        """

        value = self.resolved_orientation_policy(policy)
        if value == "none":
            return np.eye(4, dtype=float)
        if value == "fbx_global_settings":
            return self.global_settings_conversion_matrix()
        if value == "fbx_y_up_to_dying_light":
            return FBX_Y_UP_TO_DYING_LIGHT.copy()
        axis, sign = {
            "rotate_x_90": ("X", 90.0),
            "rotate_x_minus_90": ("X", -90.0),
            "rotate_y_90": ("Y", 90.0),
            "rotate_y_minus_90": ("Y", -90.0),
            "rotate_z_90": ("Z", 90.0),
            "rotate_z_minus_90": ("Z", -90.0),
        }[value]
        return _axis_rotation(axis, sign)

    def resolved_orientation_policy(self, policy: str = "auto") -> str:
        """Resolve a requested orientation policy to the transform actually used.

        Automatic orientation is derived from FBX ``GlobalSettings``.  The
        common X-right/Y-up/Z-front layout resolves to ``none``; any other
        representable axis permutation/sign layout resolves to the explicit
        ``fbx_global_settings`` conversion.
        """

        value = str(policy or "auto").strip().lower()
        if value not in ORIENTATION_POLICIES:
            raise ValueError(f"unknown orientation policy {policy!r}")
        if value == "auto":
            conversion = self.global_settings_conversion_matrix()
            return (
                "none"
                if np.allclose(conversion, np.eye(4), rtol=0.0, atol=1.0e-12)
                else "fbx_global_settings"
            )
        return value

    def global_settings_conversion_matrix(self) -> np.ndarray:
        """Convert declared FBX axes into canonical X-right/Y-up/Z-front.

        The FBX axis metadata describes a signed permutation of the three
        coordinate axes.  Such a basis is exactly representable without
        approximation.  Missing metadata retains the historical identity
        convention; malformed, repeated, or non-unit axis declarations are
        rejected with the complete setting inventory.
        """

        keys = (
            ("CoordAxis", "CoordAxisSign"),
            ("UpAxis", "UpAxisSign"),
            ("FrontAxis", "FrontAxisSign"),
        )
        if all(self.axis_settings.get(axis) is None for axis, _sign in keys):
            return np.eye(4, dtype=float)
        rows: list[np.ndarray] = []
        used: list[int] = []
        for axis_key, sign_key in keys:
            raw_axis = self.axis_settings.get(axis_key)
            raw_sign = self.axis_settings.get(sign_key)
            try:
                axis = int(raw_axis)  # type: ignore[arg-type]
                sign = int(raw_sign)  # type: ignore[arg-type]
            except (TypeError, ValueError) as exc:
                raise ValueError(
                    "FBX GlobalSettings axis metadata is incomplete: "
                    f"{self.axis_settings}"
                ) from exc
            if axis not in {0, 1, 2} or sign not in {-1, 1}:
                raise ValueError(
                    "FBX GlobalSettings axes must use indexes 0..2 and signs +/-1; "
                    f"found {axis_key}={raw_axis!r}, {sign_key}={raw_sign!r}"
                )
            used.append(axis)
            row = np.zeros(3, dtype=float)
            row[axis] = float(sign)
            rows.append(row)
        if len(set(used)) != 3:
            raise ValueError(
                "FBX GlobalSettings axes are not an orthonormal permutation: "
                f"{self.axis_settings}"
            )
        result = np.eye(4, dtype=float)
        result[:3, :3] = np.vstack(rows)
        if not np.allclose(
            result[:3, :3] @ result[:3, :3].T,
            np.eye(3),
            rtol=0.0,
            atol=1.0e-12,
        ):
            raise ValueError(
                "FBX GlobalSettings axis basis is not orthonormal: "
                f"{self.axis_settings}"
            )
        return result

    def to_chrome_global_matrix(self, matrix: np.ndarray, policy: str = "auto") -> np.ndarray:
        conversion = self.coordinate_conversion_matrix(policy)
        return conversion @ np.asarray(matrix, dtype=float) @ np.linalg.inv(conversion)

    def to_chrome_point(self, value: Sequence[float], policy: str = "auto") -> np.ndarray:
        conversion = self.coordinate_conversion_matrix(policy)
        source = np.ones(4, dtype=float)
        source[:3] = np.asarray(value, dtype=float)[:3]
        return (conversion @ source)[:3]

    def to_chrome_direction(self, value: Sequence[float], policy: str = "auto") -> np.ndarray:
        conversion = self.coordinate_conversion_matrix(policy)
        return conversion[:3, :3] @ np.asarray(value, dtype=float)[:3]

    def _validate_axis_contract(self) -> None:
        self.global_settings_conversion_matrix()

    def _add_scene_warnings(self) -> None:
        if not self.limb_ids:
            self.warnings.append("No LimbNode armature was found; Auto mode will build a static mesh.")
        if self.limb_ids and not any(row.clusters for row in self.geometries):
            self.warnings.append("An armature exists, but no mesh Skin/Cluster weights were found.")
        if len(self.armature_roots) > 1:
            self.warnings.append(f"The FBX contains {len(self.armature_roots)} armature roots.")
        for geometry in self.geometries:
            if not geometry.triangles and len(geometry.control_points):
                self.warnings.append(f"{geometry.name}: geometry has vertices but no polygons and will be skipped.")
            influences = geometry.skin_influences
            max_influences = max((len(rows) for rows in influences.values()), default=0)
            if max_influences > 4:
                self.warnings.append(
                    f"{geometry.name}: up to {max_influences} skin influences will be reduced to four."
                )
            if geometry.clusters:
                unweighted = sum(index not in influences for index in range(len(geometry.control_points)))
                if unweighted:
                    self.warnings.append(
                        f"{geometry.name}: {unweighted} unweighted control points will be assigned to the rig root."
                    )


# --------------------------------------------------------------------------- raw parser

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
        properties: list[Any] = []
        property_end = offset + property_length
        for _ in range(property_count):
            value, offset = _parse_property(data, offset)
            properties.append(value)
        if offset != property_end:
            raise ValueError(f"FBX property length mismatch in node {name!r}")
        children: list[FbxNode] = []
        if offset < end_offset:
            children, offset = _parse_nodes(data, offset, version)
        offset = int(end_offset)
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
    return next((row for row in node.children if row.name == name), None)


def _child_value(node: FbxNode, name: str, default: Any = None) -> Any:
    row = _child(node, name)
    return row.properties[0] if row and row.properties else default


def _properties70(node: FbxNode | None) -> dict[str, list[Any]]:
    result: dict[str, list[Any]] = {}
    if node is None:
        return result
    container = _child(node, "Properties70")
    if container:
        for row in container.children:
            if row.name == "P" and row.properties:
                result[str(row.properties[0])] = list(row.properties[4:])
    return result


def _clean_name(value: Any) -> str:
    return str(value).split("\x00", 1)[0].split("::", 1)[-1]


def _vector_property(properties: dict[str, list[Any]], name: str, default: Sequence[float]) -> np.ndarray:
    value = properties.get(name)
    return np.asarray(value[:3] if value else default, dtype=float)


def _translation_matrix(value: Sequence[float]) -> np.ndarray:
    result = np.eye(4, dtype=float)
    result[:3, 3] = value
    return result


def _scale_matrix(value: Sequence[float]) -> np.ndarray:
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


def _euler_matrix(value: Sequence[float], order: str) -> np.ndarray:
    """Evaluate intrinsic FBX Euler channels using column-vector matrices.

    For written order XYZ the effective matrix is Rz @ Ry @ Rx.  This is the
    same corrected rule validated in DL ReAnimated against a Blender T-pose.
    """

    result = np.eye(4, dtype=float)
    values = {"X": value[0], "Y": value[1], "Z": value[2]}
    for axis in order:
        result = _axis_rotation(axis, values[axis]) @ result
    return result


def _validate_polygon_for_fan(
    points: np.ndarray,
    *,
    geometry_name: str,
    polygon_index: int,
) -> None:
    """Prove that deterministic first-corner fan triangulation is safe.

    Triangles need no additional proof. Convex planar quads/n-gons preserve
    their source winding under a fan. Concave, self-intersecting, degenerate,
    or non-planar polygons are rejected with an export-time remedy rather than
    being silently converted into different visible geometry.
    """

    values = np.asarray(points, dtype=float)
    if values.ndim != 2 or values.shape[1:] != (3,):
        raise ValueError(
            f"geometry {geometry_name!r} polygon {polygon_index} has malformed positions; "
            "repair the face and re-export"
        )
    if not np.isfinite(values).all():
        raise ValueError(
            f"geometry {geometry_name!r} polygon {polygon_index} contains non-finite "
            "positions; repair the face and re-export"
        )
    if len(values) <= 3:
        return

    extent = float(np.max(np.ptp(values, axis=0)))
    position_tolerance = max(1.0e-8, extent * 1.0e-7)
    for left in range(len(values)):
        for right in range(left + 1, len(values)):
            if float(np.linalg.norm(values[left] - values[right])) <= position_tolerance:
                raise ValueError(
                    f"geometry {geometry_name!r} polygon {polygon_index} repeats a corner. "
                    "Remove the degenerate face, triangulate it in the DCC, and re-export."
                )

    # Newell's method gives a deterministic normal for any simple planar
    # polygon without choosing a potentially collinear first triple.
    normal = np.zeros(3, dtype=float)
    for index, current in enumerate(values):
        following = values[(index + 1) % len(values)]
        normal += np.asarray(
            (
                (current[1] - following[1]) * (current[2] + following[2]),
                (current[2] - following[2]) * (current[0] + following[0]),
                (current[0] - following[0]) * (current[1] + following[1]),
            ),
            dtype=float,
        )
    normal_length = float(np.linalg.norm(normal))
    if normal_length <= max(1.0e-12, extent * extent * 1.0e-10):
        raise ValueError(
            f"geometry {geometry_name!r} polygon {polygon_index} is degenerate or "
            "self-intersecting. Triangulate/repair this face in the DCC and re-export."
        )
    normal /= normal_length
    distances = np.abs((values - values[0]) @ normal)
    planarity_tolerance = max(1.0e-7, extent * 1.0e-5)
    maximum_distance = float(np.max(distances))
    if maximum_distance > planarity_tolerance:
        raise ValueError(
            f"geometry {geometry_name!r} polygon {polygon_index} is non-planar "
            f"(maximum plane deviation {maximum_distance:.6g}). Triangulate this face in "
            "the DCC before FBX export; automatic fan triangulation is intentionally blocked."
        )

    drop_axis = int(np.argmax(np.abs(normal)))
    projected = np.delete(values, drop_axis, axis=1)
    signs: list[float] = []
    cross_tolerance = max(1.0e-14, extent * extent * 1.0e-10)
    for index in range(len(projected)):
        before = projected[index - 1]
        current = projected[index]
        after = projected[(index + 1) % len(projected)]
        first = current - before
        second = after - current
        cross = float(first[0] * second[1] - first[1] * second[0])
        if abs(cross) <= cross_tolerance:
            raise ValueError(
                f"geometry {geometry_name!r} polygon {polygon_index} has a collinear or "
                "degenerate n-gon corner. Triangulate/repair this face in the DCC and re-export."
            )
        signs.append(cross)
    if min(signs) < 0.0 < max(signs):
        raise ValueError(
            f"geometry {geometry_name!r} polygon {polygon_index} is concave. Triangulate "
            "this face in the DCC before FBX export; first-corner fan triangulation would "
            "change or overlap visible geometry."
        )


def _matrix_from_array(values: Sequence[float] | None) -> np.ndarray | None:
    if not values or len(values) != 16:
        return None
    # FBX Matrix array values are row-vector matrices in the common exporters
    # used by these fixtures (translation occupies values 12..14). Transpose to
    # the column-vector convention used by the importer.
    return np.asarray(values, dtype=float).reshape((4, 4)).T


def _read_bind_pose_matrices(objects: FbxNode) -> dict[int, np.ndarray]:
    result: dict[int, np.ndarray] = {}
    for pose in objects.children:
        if pose.name != "Pose" or len(pose.properties) < 3 or str(pose.properties[2]) != "BindPose":
            continue
        for pose_node in pose.children:
            if pose_node.name != "PoseNode":
                continue
            object_id = _child_value(pose_node, "Node")
            values = _child_value(pose_node, "Matrix", [])
            matrix = _matrix_from_array(values)
            if object_id is not None and matrix is not None:
                result.setdefault(int(object_id), matrix)
    return result


def _axis_settings(node: FbxNode | None) -> dict[str, int | float | None]:
    props = _properties70(node)
    names = (
        "UpAxis",
        "UpAxisSign",
        "FrontAxis",
        "FrontAxisSign",
        "CoordAxis",
        "CoordAxisSign",
        "UnitScaleFactor",
        "OriginalUnitScaleFactor",
    )
    return {name: (props.get(name) or [None])[0] for name in names}


def _read_layer_elements(geometry: FbxNode) -> dict[str, list[FbxLayerElement]]:
    result: dict[str, list[FbxLayerElement]] = defaultdict(list)
    specs = {
        "LayerElementNormal": ("Normals", "NormalsIndex", 3),
        "LayerElementTangent": ("Tangents", "TangentsIndex", 3),
        "LayerElementBinormal": ("Binormals", "BinormalsIndex", 3),
        "LayerElementUV": ("UV", "UVIndex", 2),
        "LayerElementColor": ("Colors", "ColorIndex", 4),
        # Material rows are already material-slot indexes; their misleading
        # IndexToDirect marker does not refer to a separate direct array.
        "LayerElementMaterial": ("Materials", "", 1),
    }
    for row in geometry.children:
        if row.name not in specs:
            continue
        direct_name, index_name, tuple_size = specs[row.name]
        mapping = str(_child_value(row, "MappingInformationType", "AllSame"))
        reference = str(_child_value(row, "ReferenceInformationType", "Direct"))
        direct = list(_child_value(row, direct_name, []) or [])
        indices = list(_child_value(row, index_name, []) or []) if index_name else []
        if row.name == "LayerElementMaterial":
            reference = "Direct"
        result[row.name].append(
            FbxLayerElement(
                kind=row.name,
                index=int(row.properties[0]) if row.properties else 0,
                name=str(_child_value(row, "Name", "")),
                mapping=mapping,
                reference=reference,
                direct=direct,
                indices=[int(value) for value in indices],
                tuple_size=tuple_size,
            )
        )
    return dict(result)
