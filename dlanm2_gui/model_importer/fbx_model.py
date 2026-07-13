from __future__ import annotations

"""Binary FBX model, skeleton, skin and material reader.

The implementation deliberately mirrors the binary-FBX evaluator already used by
DL ReAnimated.  It supports the subset required by Blender, Mixamo and common
DCC FBX exports and keeps every coordinate-space conversion explicit.
"""

from collections import Counter, defaultdict
from dataclasses import dataclass, field
from pathlib import Path
from typing import Any, Iterable, Sequence
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
        mapped = polygon_index if mapping == "bypolygon" else 0
        values = layer.direct
        if not values:
            return 0
        if mapped < 0 or mapped >= len(values):
            return 0
        return max(0, int(values[mapped]))


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
    warnings: list[str] = field(default_factory=list)

    @classmethod
    def from_path(cls, path: str | Path) -> "FbxScene":
        source = Path(path)
        data = source.read_bytes()
        if not data.startswith(b"Kaydara FBX Binary"):
            raise ValueError(f"only binary FBX is supported: {source}")
        if len(data) < 27:
            raise ValueError(f"FBX file is truncated: {source}")
        version = struct.unpack_from("<I", data, 23)[0]
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
        )
        scene._validate_axis_contract()
        scene.animation_stacks = scene._read_animation_stacks()
        scene.blend_shape_names = scene._read_blend_shape_names()
        scene.geometries = scene._read_geometries()
        scene._add_scene_warnings()
        return scene

    @property
    def sha256(self) -> str:
        return hashlib.sha256(self.path.read_bytes()).hexdigest()

    @property
    def armature_roots(self) -> tuple[int, ...]:
        limb = set(self.limb_ids)
        return tuple(
            object_id
            for object_id in self.limb_ids
            if self.model_parent_id(object_id) not in limb
        )

    def model_parent_id(self, object_id: int) -> int | None:
        for kind, parent_id, _ in self.parents.get(object_id, []):
            if kind == "OO" and parent_id in self.model_names:
                return parent_id
        return None

    def model_children_ids(self, object_id: int, *, limb_only: bool = False) -> tuple[int, ...]:
        allowed = set(self.limb_ids) if limb_only else set(self.model_ids)
        return tuple(
            child_id
            for kind, child_id, _ in self.children.get(object_id, [])
            if kind == "OO" and child_id in allowed
        )

    def model_local_matrix(self, object_id: int) -> np.ndarray:
        node = self.object_by_id[object_id]
        props = _properties70(node)
        translation = _vector_property(props, "Lcl Translation", (0.0, 0.0, 0.0))
        rotation = _vector_property(props, "Lcl Rotation", (0.0, 0.0, 0.0))
        scaling = _vector_property(props, "Lcl Scaling", (1.0, 1.0, 1.0))
        pre = _vector_property(props, "PreRotation", (0.0, 0.0, 0.0))
        post = _vector_property(props, "PostRotation", (0.0, 0.0, 0.0))
        rotation_offset = _vector_property(props, "RotationOffset", (0.0, 0.0, 0.0))
        rotation_pivot = _vector_property(props, "RotationPivot", (0.0, 0.0, 0.0))
        scaling_offset = _vector_property(props, "ScalingOffset", (0.0, 0.0, 0.0))
        scaling_pivot = _vector_property(props, "ScalingPivot", (0.0, 0.0, 0.0))
        order = ROTATION_ORDERS.get(int((props.get("RotationOrder") or [0])[0]), "XYZ")
        return (
            _translation_matrix(translation)
            @ _translation_matrix(rotation_offset)
            @ _translation_matrix(rotation_pivot)
            @ _euler_matrix(pre, order)
            @ _euler_matrix(rotation, order)
            @ np.linalg.inv(_euler_matrix(post, order))
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

    def model_global_matrix(self, object_id: int) -> np.ndarray:
        cache: dict[int, np.ndarray] = {}
        visiting: set[int] = set()

        def resolve(current: int) -> np.ndarray:
            if current in cache:
                return cache[current]
            if current in visiting:
                raise ValueError(f"FBX model hierarchy cycle at {self.model_names.get(current, current)}")
            visiting.add(current)
            local = self.model_local_matrix(current)
            parent = self.model_parent_id(current)
            value = resolve(parent) @ local if parent in self.model_names else local
            visiting.remove(current)
            cache[current] = value
            return value

        return resolve(object_id)

    def object_bind_matrix(self, object_id: int) -> np.ndarray:
        value = self.bind_pose_matrices.get(object_id)
        return value.copy() if value is not None else self.model_global_matrix(object_id)

    def bone_globals(self, bone_ids: Sequence[int]) -> dict[int, np.ndarray]:
        cluster_links: dict[int, list[np.ndarray]] = defaultdict(list)
        for geometry in self.geometries:
            for cluster in geometry.clusters:
                if cluster.bone_id is not None and cluster.transform_link is not None:
                    cluster_links[cluster.bone_id].append(cluster.transform_link)
        result: dict[int, np.ndarray] = {}
        for bone_id in bone_ids:
            if bone_id in self.bind_pose_matrices:
                result[bone_id] = self.bind_pose_matrices[bone_id].copy()
            elif cluster_links.get(bone_id):
                result[bone_id] = cluster_links[bone_id][0].copy()
            else:
                result[bone_id] = self.model_global_matrix(bone_id)
        return result

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
            for child_id in self.model_children_ids(object_id, limb_only=limb_only):
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
                raise ValueError(f"geometry {_clean_name(node.properties[1])!r} has unterminated polygon")

            model_id = self._linked_object_id(geometry_id, object_name="Model", subtype="Mesh")
            model_name = self.model_names.get(model_id, _clean_name(node.properties[1]))
            material_ids = self._model_material_ids(model_id) if model_id is not None else ()
            material_names = tuple(self.material_names.get(row, f"material_{row}") for row in material_ids)
            layers = _read_layer_elements(node)
            clusters = self._geometry_clusters(geometry_id)
            bind = None
            for cluster in clusters:
                if cluster.transform is not None:
                    bind = cluster.transform
                    break
            if bind is None and model_id is not None:
                bind = self.object_bind_matrix(model_id)
            if bind is None:
                bind = np.eye(4, dtype=float)
            geometric = self.model_geometric_matrix(model_id) if model_id is not None else np.eye(4, dtype=float)
            shapes = self._geometry_blend_shape_names(geometry_id)
            result.append(
                FbxGeometry(
                    object_id=geometry_id,
                    name=_clean_name(node.properties[1]),
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

        ``auto`` is intentionally conservative: for the validated X-right,
        Y-up, Z-front contract it selects the recovered FBX-to-Dying-Light
        basis.  Manual quarter-turn policies exist for unusual authoring files
        and are recorded in build reports instead of requiring editor-side
        object rotation.
        """

        value = str(policy or "auto").strip().lower()
        if value not in ORIENTATION_POLICIES:
            raise ValueError(f"unknown orientation policy {policy!r}")
        if value == "auto":
            value = "fbx_y_up_to_dying_light"
        if value == "fbx_y_up_to_dying_light":
            return FBX_Y_UP_TO_DYING_LIGHT.copy()
        if value == "none":
            return np.eye(4, dtype=float)
        axis, sign = {
            "rotate_x_90": ("X", 90.0),
            "rotate_x_minus_90": ("X", -90.0),
            "rotate_y_90": ("Y", 90.0),
            "rotate_y_minus_90": ("Y", -90.0),
            "rotate_z_90": ("Z", 90.0),
            "rotate_z_minus_90": ("Z", -90.0),
        }[value]
        return _axis_rotation(axis, sign)

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
        expected = {
            "UpAxis": 1,
            "UpAxisSign": 1,
            "CoordAxis": 0,
            "CoordAxisSign": 1,
            "FrontAxis": 2,
            "FrontAxisSign": 1,
        }
        mismatches = {
            key: (self.axis_settings.get(key), value)
            for key, value in expected.items()
            if self.axis_settings.get(key) not in (None, value)
        }
        if mismatches:
            raise ValueError(
                "this first model importer supports FBX X-right/Y-up/Z-front scenes only; "
                f"axis mismatch: {mismatches}"
            )

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
