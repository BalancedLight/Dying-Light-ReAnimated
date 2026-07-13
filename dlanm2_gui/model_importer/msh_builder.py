from __future__ import annotations

"""FBX model -> Chrome Engine 6 source-MSH authoring.

The production bind policy is intentionally fixed to the manually validated
Chrome 6 rule:

    node.local      = parent_global^-1 * node_global
    node.reference  = node_global^-1

Only the inverse-global reference policy is exposed by normal builds.
"""

from collections import Counter, defaultdict
from dataclasses import dataclass, field
from pathlib import Path
from typing import Any, Iterable, Sequence
import hashlib
import json
import math
import re

import numpy as np

from .fbx_model import FbxGeometry, FbxScene, FbxTriangleCorner
from .vendor.chrome_mesh_tools.math3d import matrix3x4_from_matrix4
from .vendor.chrome_mesh_tools.msh import MshFile
from .vendor.chrome_mesh_tools.smd import SmdFile
from .vendor.chrome_mesh_tools.smd_bind import build_smd_bind_matrices
from .vendor.chrome_mesh_tools.source_contract import audit_source_msh_for_compiler
from .vendor.chrome_mesh_tools.writer import (
    SourceLod,
    SourceMsh,
    SourceNode,
    SourceSkinVertex,
    SourceSubset,
)

SOURCE_NODE_MESH = 1
SOURCE_NODE_MESH_VBLEND = 2
SOURCE_NODE_HELPER = 4
SOURCE_NODE_BONE = 8
IDENTITY4 = np.eye(4, dtype=float)


@dataclass(slots=True)
class ModelBuildOptions:
    resource_name: str
    mode: str = "auto"  # auto, static, exact_rig, dying_light_humanoid
    material_mode: str = "test"  # test, preserve_slots
    test_material: str = "bottle_trash_a.mat"
    surface_name: str = "Flesh"
    flip_v: bool = False
    retain_full_skeleton: bool = True
    retention_weight_i16: int = 2
    max_vertices_per_mesh: int = 60_000
    animation_script: str = ""
    target_smd: str = ""
    include_empty_marker_geometry: bool = False
    preserve_helpers: bool = True
    orientation_policy: str = "auto"
    # Explicit source-name -> Dying Light target-name overrides.  Auto mapping
    # fills only rows not present here.
    humanoid_bone_map: dict[str, str] = field(default_factory=dict)

    def validate(self) -> None:
        self.resource_name = sanitize_name(self.resource_name, max_bytes=56)
        if self.mode not in {"auto", "static", "exact_rig", "dying_light_humanoid"}:
            raise ValueError(f"unknown model import mode {self.mode!r}")
        if self.material_mode not in {"test", "preserve_slots"}:
            raise ValueError(f"unknown material mode {self.material_mode!r}")
        if not self.test_material or len(self.test_material.encode("utf-8")) >= 64:
            raise ValueError("test material must be a non-empty source-MSH material name under 64 bytes")
        if not self.surface_name or len(self.surface_name.encode("utf-8")) >= 64:
            raise ValueError("surface name must fit the source-MSH 64-byte name field")
        if not 3 <= self.max_vertices_per_mesh <= 65_535:
            raise ValueError("max_vertices_per_mesh must be between 3 and 65535")
        if not 1 <= self.retention_weight_i16 <= 64:
            raise ValueError("retention_weight_i16 must be between 1 and 64")
        if self.animation_script and any(value in self.animation_script for value in '"\r\n'):
            raise ValueError("animation script alias contains unsafe characters")
        # Validate eagerly so a typo cannot silently produce a sideways model.
        from .fbx_model import ORIENTATION_POLICIES
        if self.orientation_policy not in ORIENTATION_POLICIES:
            raise ValueError(f"unknown orientation policy {self.orientation_policy!r}")
        for source_name, target_name in self.humanoid_bone_map.items():
            if not str(source_name).strip() or not str(target_name).strip():
                raise ValueError("manual humanoid mapping rows need both source and target names")


@dataclass(slots=True)
class _BuildVertex:
    position: np.ndarray
    normal: np.ndarray
    uv: tuple[float, float]
    color: tuple[int, int, int, int]
    influences: list[tuple[int, float]]


@dataclass(slots=True)
class _MeshChunk:
    node_name: str
    material_index: int
    vertices: list[_BuildVertex] = field(default_factory=list)


@dataclass(slots=True)
class ModelBuildResult:
    source: SourceMsh
    report: dict[str, Any]
    ascr_text: str | None
    bscr_text: str | None

    def write(self, output_directory: str | Path) -> dict[str, Path]:
        output = Path(output_directory)
        output.mkdir(parents=True, exist_ok=True)
        resource_name = str(self.report["resource_name"])
        msh_path = output / f"{resource_name}.msh"
        payload = self.source.build()
        msh_path.write_bytes(payload)
        paths = {"msh": msh_path}
        if self.ascr_text is not None:
            ascr = msh_path.with_suffix(".ascr")
            ascr.write_text(self.ascr_text, encoding="utf-8")
            paths["ascr"] = ascr
        if self.bscr_text is not None:
            bscr = msh_path.with_suffix(".bscr")
            bscr.write_text(self.bscr_text, encoding="utf-8")
            paths["bscr"] = bscr
        report = dict(self.report)
        report.update(
            {
                "msh_path": str(msh_path),
                "msh_size": len(payload),
                "msh_sha256": hashlib.sha256(payload).hexdigest(),
                "ascr_path": str(paths.get("ascr", "")),
                "bscr_path": str(paths.get("bscr", "")),
            }
        )
        parsed = MshFile.parse(payload, str(msh_path))
        report["parser_lossless_roundtrip"] = parsed.is_lossless_roundtrip()
        report["parsed_has_skinning"] = parsed.has_skinning
        report["compiler_preflight"] = audit_source_msh_for_compiler(msh_path)
        report_path = output / f"{resource_name}.model_import.json"
        report_path.write_text(json.dumps(report, indent=2) + "\n", encoding="utf-8")
        paths["report"] = report_path
        return paths


def build_source_from_fbx(scene: FbxScene, options: ModelBuildOptions) -> ModelBuildResult:
    options.validate()
    mode = options.mode
    weighted_bone_ids = {
        cluster.bone_id
        for geometry in scene.geometries
        for cluster in geometry.clusters
        if cluster.bone_id is not None and any(weight > 1.0e-12 for weight in cluster.weights)
    }
    if mode == "auto":
        mode = "exact_rig" if weighted_bone_ids else "static"
    if mode == "static":
        return _build_static(scene, options)
    if mode == "exact_rig":
        if not weighted_bone_ids:
            raise ValueError("Exact rig mode needs Skin/Cluster weights; choose Static for this FBX")
        return _build_exact_rig(scene, options, weighted_bone_ids)
    if mode == "dying_light_humanoid":
        if not weighted_bone_ids:
            raise ValueError("Dying Light humanoid mode needs an FBX armature and skin weights")
        if not options.target_smd:
            raise ValueError("Dying Light humanoid mode needs a target SMD")
        return _build_dying_light_humanoid(scene, options, weighted_bone_ids)
    raise AssertionError(mode)


def _build_static(scene: FbxScene, options: ModelBuildOptions) -> ModelBuildResult:
    materials, material_lookup, material_report = _material_table(scene, options)
    chunks: list[_MeshChunk] = []
    warnings = list(scene.warnings)
    for geometry in scene.geometries:
        if not geometry.triangles:
            if len(geometry.control_points) and options.include_empty_marker_geometry:
                warnings.append(f"{geometry.name}: marker-only geometry has no triangles and cannot be emitted as MSH")
            continue
        chunks.extend(
            _geometry_chunks(
                scene,
                geometry,
                options=options,
                material_lookup=material_lookup,
                bone_local_by_id=None,
                transfer_by_source_bone=None,
                fallback_bone_local_index=None,
            )
        )
    if not chunks:
        raise ValueError("FBX contains no triangle geometry that can be emitted")
    mesh_nodes = [
        SourceNode(
            name=chunk.node_name,
            node_type=SOURCE_NODE_MESH,
            parent_index=-1,
            local_matrix=_matrix3x4(IDENTITY4),
            reference_matrix=_matrix3x4(IDENTITY4),
            lods=(_chunk_to_lod(chunk, bone_palette=()),),
        )
        for chunk in chunks
    ]
    helper_nodes: list[SourceNode] = []
    if options.preserve_helpers:
        used_names = {node.name.casefold() for node in mesh_nodes}
        for model_id in scene.model_ids:
            subtype = scene.model_subtypes.get(model_id, "")
            if subtype not in {"Null", "LimbNode"}:
                continue
            raw_name = scene.model_names.get(model_id, f"helper_{model_id}")
            name = sanitize_name(raw_name, max_bytes=63)
            candidate = name
            suffix = 1
            while candidate.casefold() in used_names:
                candidate = sanitize_name(f"{name}_{suffix}", max_bytes=63)
                suffix += 1
            used_names.add(candidate.casefold())
            global_matrix = scene.to_chrome_global_matrix(
                _matrix_units_to_meters(scene.model_global_matrix(model_id), scene.meters_per_unit),
                options.orientation_policy,
            )
            helper_nodes.append(
                SourceNode(
                    name=candidate,
                    node_type=SOURCE_NODE_HELPER,
                    parent_index=-1,
                    local_matrix=_matrix3x4(global_matrix),
                    reference_matrix=_matrix3x4(np.linalg.inv(global_matrix)),
                )
            )
    nodes = tuple([*mesh_nodes, *helper_nodes])
    source = SourceMsh(materials=materials, surface_names=(options.surface_name,), nodes=nodes)
    source.validate()
    report = _base_report(scene, options, effective_mode="static")
    report.update(
        {
            "bone_count": 0,
            "geometry_node_count": len(chunks),
            "helper_node_count": len(helper_nodes),
            "material_policy": material_report,
            "total_vertices": sum(len(chunk.vertices) for chunk in chunks),
            "total_triangles": sum(len(chunk.vertices) // 3 for chunk in chunks),
            "warnings": warnings,
            "reference_matrix_policy": "identity geometry roots; helper nodes use inverse(global transform)",
        }
    )
    return ModelBuildResult(source, report, None, None)


def _build_exact_rig(
    scene: FbxScene,
    options: ModelBuildOptions,
    weighted_bone_ids: set[int],
) -> ModelBuildResult:
    bone_ids = scene.depth_first_bones_for_weighted_ids(weighted_bone_ids)
    if not bone_ids:
        raise ValueError("could not resolve the weighted FBX armature")
    if len(bone_ids) > 256:
        raise ValueError("Chrome source skin palettes use uint8 local indexes; rig exceeds 256 bones")
    _validate_unique_bone_names(scene, bone_ids)
    globals_units = scene.bone_globals(bone_ids)
    globals_m = {
        bone_id: scene.to_chrome_global_matrix(
            _matrix_units_to_meters(value, scene.meters_per_unit),
            options.orientation_policy,
        )
        for bone_id, value in globals_units.items()
    }
    physical_by_id = {bone_id: index for index, bone_id in enumerate(bone_ids)}
    bone_nodes = _source_bones_from_globals(scene, bone_ids, globals_m, physical_by_id)
    materials, material_lookup, material_report = _material_table(scene, options)
    chunks: list[_MeshChunk] = []
    warnings = list(scene.warnings)
    for geometry in scene.geometries:
        if not geometry.triangles:
            continue
        chunks.extend(
            _geometry_chunks(
                scene,
                geometry,
                options=options,
                material_lookup=material_lookup,
                bone_local_by_id=physical_by_id,
                transfer_by_source_bone=None,
                fallback_bone_local_index=0,
            )
        )
    if not chunks:
        raise ValueError("FBX contains no skinned triangle geometry")
    retention = _retain_full_palette(chunks, len(bone_ids), options) if options.retain_full_skeleton else []
    palette = tuple(range(len(bone_ids)))
    geometry_nodes = [
        SourceNode(
            name=chunk.node_name,
            node_type=SOURCE_NODE_MESH_VBLEND,
            parent_index=-1,
            local_matrix=_matrix3x4(IDENTITY4),
            reference_matrix=_matrix3x4(IDENTITY4),
            lods=(_chunk_to_lod(chunk, bone_palette=palette),),
        )
        for chunk in chunks
    ]
    source = SourceMsh(
        materials=materials,
        surface_names=(options.surface_name,),
        nodes=tuple([*bone_nodes, *geometry_nodes]),
    )
    source.validate()
    names = [scene.model_names[row] for row in bone_ids]
    ascr, bscr = _companions(names, options.animation_script)
    report = _base_report(scene, options, effective_mode="exact_rig")
    report.update(
        {
            "bone_count": len(bone_ids),
            "bone_names": names,
            "weighted_source_bone_count": len(weighted_bone_ids),
            "geometry_node_count": len(chunks),
            "material_policy": material_report,
            "total_vertices": sum(len(chunk.vertices) for chunk in chunks),
            "total_triangles": sum(len(chunk.vertices) // 3 for chunk in chunks),
            "full_skeleton_retention": {
                "enabled": options.retain_full_skeleton,
                "assignment_count": len(retention),
                "weight_i16": options.retention_weight_i16,
                "assignments": retention,
            },
            "warnings": warnings,
            "reference_matrix_policy": "validated inverse(global_bind) for every FBX bone",
            "animation_script": options.animation_script,
        }
    )
    return ModelBuildResult(source, report, ascr, bscr)


def _build_dying_light_humanoid(
    scene: FbxScene,
    options: ModelBuildOptions,
    weighted_bone_ids: set[int],
) -> ModelBuildResult:
    smd = SmdFile.from_path(options.target_smd)
    target_bind = build_smd_bind_matrices(smd)
    target_nodes = list(smd.nodes)
    if len(target_nodes) > 256:
        raise ValueError("target Dying Light skeleton exceeds source palette capacity")
    target_index_by_name = {node.name.casefold(): index for index, node in enumerate(target_nodes)}
    source_bone_ids = scene.depth_first_bones_for_weighted_ids(weighted_bone_ids)
    source_globals = {
        bone_id: scene.to_chrome_global_matrix(
            _matrix_units_to_meters(value, scene.meters_per_unit),
            options.orientation_policy,
        )
        for bone_id, value in scene.bone_globals(source_bone_ids).items()
    }
    mapping, mapping_report = humanoid_bone_mapping(
        scene,
        source_bone_ids,
        target_nodes,
        manual_mapping=options.humanoid_bone_map,
    )
    mapped_count = sum(value is not None for value in mapping.values())
    if mapped_count < 12:
        raise ValueError(
            f"humanoid auto-map resolved only {mapped_count} source bones; use Exact rig mode"
        )
    target_globals = {
        index: np.asarray(target_bind.global_bind[node.index], dtype=float)
        for index, node in enumerate(target_nodes)
    }
    transfer_by_source: dict[int, tuple[int, np.ndarray]] = {}
    for source_id, target_physical in mapping.items():
        if target_physical is None:
            continue
        transfer_by_source[source_id] = (
            target_physical,
            target_globals[target_physical] @ np.linalg.inv(source_globals[source_id]),
        )
    # Unmapped source bones follow the nearest mapped ancestor. This preserves
    # weights on twist/end/helper rows without inventing target bones.
    for source_id in source_bone_ids:
        if source_id in transfer_by_source:
            continue
        ancestor = scene.model_parent_id(source_id)
        while ancestor is not None and ancestor not in transfer_by_source:
            ancestor = scene.model_parent_id(ancestor)
        if ancestor in transfer_by_source:
            target_physical = transfer_by_source[ancestor][0]
        else:
            target_physical = target_index_by_name.get("pelvis", 0)
        transfer_by_source[source_id] = (
            target_physical,
            target_globals[target_physical] @ np.linalg.inv(source_globals[source_id]),
        )

    materials, material_lookup, material_report = _material_table(scene, options)
    chunks: list[_MeshChunk] = []
    for geometry in scene.geometries:
        if not geometry.triangles:
            continue
        chunks.extend(
            _geometry_chunks(
                scene,
                geometry,
                options=options,
                material_lookup=material_lookup,
                bone_local_by_id=None,
                transfer_by_source_bone=transfer_by_source,
                fallback_bone_local_index=target_index_by_name.get("pelvis", 0),
            )
        )
    if not chunks:
        raise ValueError("FBX contains no skinned triangle geometry")
    retention = _retain_full_palette(chunks, len(target_nodes), options) if options.retain_full_skeleton else []
    bone_nodes: list[SourceNode] = []
    physical_by_smd_index = {node.index: position for position, node in enumerate(target_nodes)}
    for position, node in enumerate(target_nodes):
        parent = physical_by_smd_index[node.parent_index] if node.parent_index >= 0 else -1
        bone_nodes.append(
            SourceNode(
                name=node.name,
                node_type=SOURCE_NODE_BONE,
                parent_index=parent,
                local_matrix=matrix3x4_from_matrix4(target_bind.local[node.index]),
                reference_matrix=matrix3x4_from_matrix4(target_bind.inverse_global_bind[node.index]),
            )
        )
    palette = tuple(range(len(target_nodes)))
    geometry_nodes = [
        SourceNode(
            name=chunk.node_name,
            node_type=SOURCE_NODE_MESH_VBLEND,
            parent_index=-1,
            local_matrix=_matrix3x4(IDENTITY4),
            reference_matrix=_matrix3x4(IDENTITY4),
            lods=(_chunk_to_lod(chunk, bone_palette=palette),),
        )
        for chunk in chunks
    ]
    source = SourceMsh(
        materials=materials,
        surface_names=(options.surface_name,),
        nodes=tuple([*bone_nodes, *geometry_nodes]),
    )
    source.validate()
    names = [node.name for node in target_nodes]
    ascr, bscr = _companions(names, options.animation_script or "anims_man_all.scr")
    report = _base_report(scene, options, effective_mode="dying_light_humanoid")
    report.update(
        {
            "bone_count": len(target_nodes),
            "bone_names": names,
            "geometry_node_count": len(chunks),
            "material_policy": material_report,
            "total_vertices": sum(len(chunk.vertices) for chunk in chunks),
            "total_triangles": sum(len(chunk.vertices) // 3 for chunk in chunks),
            "humanoid_mapping": mapping_report,
            "mapped_source_bone_count": mapped_count,
            "full_skeleton_retention": {
                "enabled": options.retain_full_skeleton,
                "assignment_count": len(retention),
                "weight_i16": options.retention_weight_i16,
                "assignments": retention,
            },
            "reference_matrix_policy": "validated inverse(global_bind) from target Dying Light SMD",
            "bind_transfer_policy": (
                "per-influence target_bind * inverse(source_bind), then weighted reconstruction "
                "of model-space vertices and normals"
            ),
            "animation_script": options.animation_script or "anims_man_all.scr",
            "warnings": [
                *scene.warnings,
                "Humanoid mesh retarget is experimental until the generated model is checked in ChromeEd.",
            ],
        }
    )
    return ModelBuildResult(source, report, ascr, bscr)


# --------------------------------------------------------------------------- geometry

def _geometry_chunks(
    scene: FbxScene,
    geometry: FbxGeometry,
    *,
    options: ModelBuildOptions,
    material_lookup: dict[tuple[int, int], int],
    bone_local_by_id: dict[int, int] | None,
    transfer_by_source_bone: dict[int, tuple[int, np.ndarray]] | None,
    fallback_bone_local_index: int | None,
) -> list[_MeshChunk]:
    bake_units = geometry.mesh_bind_global @ geometry.geometric_transform
    conversion = scene.coordinate_conversion_matrix(options.orientation_policy)
    determinant = float(np.linalg.det(conversion[:3, :3] @ bake_units[:3, :3]))
    normal_matrix_fbx = np.linalg.inv(bake_units[:3, :3]).T
    influences = geometry.skin_influences
    normal_layer = geometry.first_layer("LayerElementNormal")
    uv_layer = geometry.first_layer("LayerElementUV")
    color_layer = geometry.first_layer("LayerElementColor")

    by_material: dict[int, list[Any]] = defaultdict(list)
    for triangle in geometry.triangles:
        by_material[geometry.material_slot_for_polygon(triangle.polygon_index)].append(triangle)

    chunks: list[_MeshChunk] = []
    for material_slot, triangles in sorted(by_material.items()):
        global_material_index = material_lookup.get((geometry.object_id, material_slot), 0)
        chunk_index = 0
        current = _MeshChunk(
            node_name=_mesh_node_name(options.resource_name, geometry.model_name or geometry.name, material_slot, chunk_index),
            material_index=global_material_index,
        )
        for triangle in triangles:
            corners = list(triangle.corners)
            if determinant < 0.0:
                corners[1], corners[2] = corners[2], corners[1]
            triangle_vertices: list[_BuildVertex] = []
            face_positions: list[np.ndarray] = []
            for corner in corners:
                local = geometry.control_points[corner.control_point_index]
                point_units = _transform_point(bake_units, local)
                point_m = (conversion[:3, :3] @ point_units) * scene.meters_per_unit
                face_positions.append(point_m)
                normal = None
                if normal_layer is not None:
                    try:
                        raw = np.asarray(
                            normal_layer.value(
                                control_point_index=corner.control_point_index,
                                polygon_vertex_index=corner.polygon_vertex_index,
                                polygon_index=triangle.polygon_index,
                            ),
                            dtype=float,
                        )
                        normal = _safe_normalize(
                            conversion[:3, :3] @ (normal_matrix_fbx @ raw[:3])
                        )
                    except (ValueError, IndexError, np.linalg.LinAlgError):
                        normal = None
                uv = (0.0, 0.0)
                if uv_layer is not None:
                    try:
                        raw_uv = uv_layer.value(
                            control_point_index=corner.control_point_index,
                            polygon_vertex_index=corner.polygon_vertex_index,
                            polygon_index=triangle.polygon_index,
                        )
                        uv = (float(raw_uv[0]), 1.0 - float(raw_uv[1]) if options.flip_v else float(raw_uv[1]))
                    except (ValueError, IndexError):
                        pass
                color = (255, 255, 255, 255)
                if color_layer is not None:
                    try:
                        raw_color = color_layer.value(
                            control_point_index=corner.control_point_index,
                            polygon_vertex_index=corner.polygon_vertex_index,
                            polygon_index=triangle.polygon_index,
                        )
                        color = tuple(
                            max(0, min(255, int(round((value if value <= 1.0 else value / 255.0) * 255.0))))
                            for value in raw_color[:4]
                        )  # type: ignore[assignment]
                    except (ValueError, IndexError):
                        pass
                source_rows = _clean_influences(influences.get(corner.control_point_index, []))
                if not source_rows and fallback_bone_local_index is not None:
                    vertex_influences = [(fallback_bone_local_index, 1.0)]
                elif transfer_by_source_bone is not None:
                    vertex_influences, point_m, normal = _transfer_vertex_to_target_bind(
                        point_m,
                        normal,
                        source_rows,
                        transfer_by_source_bone,
                        fallback_bone_local_index or 0,
                    )
                elif bone_local_by_id is not None:
                    mapped = [(bone_local_by_id[bone_id], weight) for bone_id, weight in source_rows if bone_id in bone_local_by_id]
                    vertex_influences = _normalize_top4(mapped) or [(fallback_bone_local_index or 0, 1.0)]
                else:
                    vertex_influences = []
                triangle_vertices.append(
                    _BuildVertex(
                        position=point_m,
                        normal=normal if normal is not None else np.zeros(3, dtype=float),
                        uv=uv,
                        color=color,
                        influences=vertex_influences,
                    )
                )
            face_normal = _safe_normalize(
                np.cross(face_positions[1] - face_positions[0], face_positions[2] - face_positions[0])
            )
            for vertex in triangle_vertices:
                if float(np.linalg.norm(vertex.normal)) < 1.0e-8:
                    vertex.normal = face_normal
            if len(current.vertices) + 3 > options.max_vertices_per_mesh:
                chunks.append(current)
                chunk_index += 1
                current = _MeshChunk(
                    node_name=_mesh_node_name(options.resource_name, geometry.model_name or geometry.name, material_slot, chunk_index),
                    material_index=global_material_index,
                )
            current.vertices.extend(triangle_vertices)
        if current.vertices:
            chunks.append(current)
    return chunks


def _chunk_to_lod(chunk: _MeshChunk, *, bone_palette: tuple[int, ...]) -> SourceLod:
    positions = [tuple(float(v) for v in row.position) for row in chunk.vertices]
    normals = [tuple(float(v) for v in _safe_normalize(row.normal)) for row in chunk.vertices]
    uvs = [row.uv for row in chunk.vertices]
    tangents, bitangents = _calculate_tangent_space(chunk.vertices)
    skin = tuple(
        SourceSkinVertex(
            bone_indices=tuple(index for index, _ in row.influences),
            weights=tuple(weight for _, weight in row.influences),
        )
        for row in chunk.vertices
    ) if bone_palette else ()
    return SourceLod(
        positions=tuple(positions),
        normals=tuple(normals),
        tangents=tuple(tangents),
        bitangents=tuple(bitangents),
        colors=tuple(row.color for row in chunk.vertices),
        uvs=tuple(uvs),
        indices=tuple(range(len(chunk.vertices))),
        skin_vertices=skin,
        subsets=(
            SourceSubset(
                material_index=chunk.material_index,
                first_index=0,
                index_count=len(chunk.vertices),
                bone_palette=bone_palette,
            ),
        ),
    )


def _calculate_tangent_space(vertices: Sequence[_BuildVertex]) -> tuple[list[tuple[float, float, float]], list[tuple[float, float, float]]]:
    tangents: list[tuple[float, float, float]] = []
    bitangents: list[tuple[float, float, float]] = []
    for index in range(0, len(vertices), 3):
        a, b, c = vertices[index : index + 3]
        edge1 = b.position - a.position
        edge2 = c.position - a.position
        duv1 = np.asarray((b.uv[0] - a.uv[0], b.uv[1] - a.uv[1]), dtype=float)
        duv2 = np.asarray((c.uv[0] - a.uv[0], c.uv[1] - a.uv[1]), dtype=float)
        denominator = duv1[0] * duv2[1] - duv1[1] * duv2[0]
        if abs(float(denominator)) > 1.0e-12:
            reciprocal = 1.0 / denominator
            tangent = (edge1 * duv2[1] - edge2 * duv1[1]) * reciprocal
            bitangent = (edge2 * duv1[0] - edge1 * duv2[0]) * reciprocal
        else:
            normal = _safe_normalize(a.normal + b.normal + c.normal)
            seed = np.asarray((1.0, 0.0, 0.0)) if abs(normal[0]) < 0.9 else np.asarray((0.0, 0.0, 1.0))
            tangent = np.cross(seed, normal)
            bitangent = np.cross(normal, tangent)
        for vertex in (a, b, c):
            normal = _safe_normalize(vertex.normal)
            tangent_orthogonal = _safe_normalize(tangent - normal * float(np.dot(normal, tangent)))
            bitangent_orthogonal = _safe_normalize(np.cross(normal, tangent_orthogonal))
            if float(np.dot(bitangent_orthogonal, bitangent)) < 0.0:
                bitangent_orthogonal = -bitangent_orthogonal
            tangents.append(tuple(float(v) for v in tangent_orthogonal))
            bitangents.append(tuple(float(v) for v in bitangent_orthogonal))
    return tangents, bitangents


# --------------------------------------------------------------------------- rigging

def _source_bones_from_globals(
    scene: FbxScene,
    bone_ids: Sequence[int],
    globals_m: dict[int, np.ndarray],
    physical_by_id: dict[int, int],
) -> list[SourceNode]:
    result: list[SourceNode] = []
    selected = set(bone_ids)
    for bone_id in bone_ids:
        parent_id = scene.model_parent_id(bone_id)
        parent_physical = physical_by_id[parent_id] if parent_id in selected else -1
        local = np.linalg.inv(globals_m[parent_id]) @ globals_m[bone_id] if parent_id in selected else globals_m[bone_id]
        reference = np.linalg.inv(globals_m[bone_id])
        result.append(
            SourceNode(
                name=scene.model_names[bone_id],
                node_type=SOURCE_NODE_BONE,
                parent_index=parent_physical,
                local_matrix=_matrix3x4(local),
                reference_matrix=_matrix3x4(reference),
            )
        )
    return result


def _clean_influences(rows: Sequence[tuple[int, float]]) -> list[tuple[int, float]]:
    combined: dict[int, float] = defaultdict(float)
    for bone_id, weight in rows:
        if math.isfinite(weight) and weight > 1.0e-12:
            combined[int(bone_id)] += float(weight)
    return sorted(combined.items(), key=lambda row: (-row[1], row[0]))


def _normalize_top4(rows: Sequence[tuple[int, float]]) -> list[tuple[int, float]]:
    combined: dict[int, float] = defaultdict(float)
    for index, weight in rows:
        if math.isfinite(weight) and weight > 1.0e-12:
            combined[int(index)] += float(weight)
    selected = sorted(combined.items(), key=lambda row: (-row[1], row[0]))[:4]
    total = sum(weight for _, weight in selected)
    return [(index, weight / total) for index, weight in selected] if total > 0.0 else []


def _transfer_vertex_to_target_bind(
    source_position: np.ndarray,
    source_normal: np.ndarray | None,
    source_rows: Sequence[tuple[int, float]],
    transfer_by_source_bone: dict[int, tuple[int, np.ndarray]],
    fallback_target: int,
) -> tuple[list[tuple[int, float]], np.ndarray, np.ndarray]:
    """Move one source bind vertex into the target bind without collapsing matrices.

    Several source bones can intentionally map to one target bone (twists, end
    bones, helper chains).  The old importer merged those weights first and then
    applied only the first source bone's transfer matrix.  That is mathematically
    invalid and produced the severe limb/torso distortion seen on the Boss test.

    We now evaluate every source influence with its own bind-transfer matrix,
    then combine only the *final target skin weights*.
    """

    cleaned = _normalize_top4(source_rows)
    if not cleaned:
        cleaned = [(fallback_target, 1.0)]
        mapped_rows: list[tuple[int, float, np.ndarray]] = [
            (fallback_target, 1.0, IDENTITY4)
        ]
    else:
        mapped_rows = []
        for source_id, weight in cleaned:
            target, matrix = transfer_by_source_bone.get(
                source_id, (fallback_target, IDENTITY4)
            )
            mapped_rows.append((target, weight, matrix))

    position = np.zeros(3, dtype=float)
    normal = np.zeros(3, dtype=float)
    target_weights: dict[int, float] = defaultdict(float)
    for target, weight, matrix in mapped_rows:
        target_weights[int(target)] += float(weight)
        position += float(weight) * _transform_point(matrix, source_position)
        if source_normal is not None:
            try:
                transformed_normal = np.linalg.inv(matrix[:3, :3]).T @ source_normal
            except np.linalg.LinAlgError:
                transformed_normal = source_normal
            normal += float(weight) * transformed_normal

    final_weights = _normalize_top4(list(target_weights.items()))
    if not final_weights:
        final_weights = [(fallback_target, 1.0)]
    fallback_normal = (
        source_normal
        if source_normal is not None
        else np.asarray((0.0, 0.0, 1.0), dtype=float)
    )
    final_normal = _safe_normalize(
        normal if np.linalg.norm(normal) > 1.0e-10 else fallback_normal
    )
    return final_weights, position, final_normal


def _retain_full_palette(
    chunks: Sequence[_MeshChunk],
    bone_count: int,
    options: ModelBuildOptions,
) -> list[dict[str, Any]]:
    used = {
        bone
        for chunk in chunks
        for vertex in chunk.vertices
        for bone, weight in vertex.influences
        if weight > 0.0
    }
    missing = [bone for bone in range(bone_count) if bone not in used]
    vertices = [(chunk_index, vertex_index, vertex) for chunk_index, chunk in enumerate(chunks) for vertex_index, vertex in enumerate(chunk.vertices)]
    if len(vertices) < len(missing):
        raise ValueError("not enough model vertices to assign complete skeleton retention influences")
    retention = options.retention_weight_i16 / 32767.0
    assignments: list[dict[str, Any]] = []
    cursor = 0
    for bone in missing:
        chosen = None
        for _ in range(len(vertices)):
            chunk_index, vertex_index, vertex = vertices[cursor % len(vertices)]
            cursor += 1
            if bone in {index for index, _ in vertex.influences}:
                continue
            if len(vertex.influences) <= 3:
                chosen = (chunk_index, vertex_index, vertex)
                break
        if chosen is None:
            chunk_index, vertex_index, vertex = vertices[cursor % len(vertices)]
            cursor += 1
            chosen = (chunk_index, vertex_index, vertex)
            vertex.influences = sorted(vertex.influences, key=lambda row: row[1], reverse=True)[:3]
        chunk_index, vertex_index, vertex = chosen
        base = _normalize_top4(vertex.influences)
        vertex.influences = [(index, weight * (1.0 - retention)) for index, weight in base]
        vertex.influences.append((bone, retention))
        vertex.influences = _normalize_top4(vertex.influences)
        assignments.append(
            {
                "bone_palette_index": bone,
                "mesh_chunk_index": chunk_index,
                "vertex_index": vertex_index,
                "weight_i16": options.retention_weight_i16,
            }
        )
    return assignments


# --------------------------------------------------------------------------- humanoid map

def humanoid_bone_mapping(
    scene: FbxScene,
    source_bone_ids: Sequence[int],
    target_nodes: Sequence[Any],
    *,
    manual_mapping: dict[str, str] | None = None,
) -> tuple[dict[int, int | None], dict[str, Any]]:
    target_by_name = {str(node.name).casefold(): index for index, node in enumerate(target_nodes)}
    aliases = _humanoid_alias_table()
    overrides = {
        str(source).casefold(): str(target)
        for source, target in dict(manual_mapping or {}).items()
    }
    mapping: dict[int, int | None] = {}
    rows: list[dict[str, Any]] = []
    invalid_overrides: list[dict[str, str]] = []
    for bone_id in source_bone_ids:
        source_name = scene.model_names[bone_id]
        normalized = _normalize_bone_name(source_name)
        override = overrides.get(source_name.casefold())
        if override is None:
            # Also allow namespace-free/manual keys.
            override = overrides.get(source_name.split(":")[-1].casefold())
        if override is not None:
            target_index = target_by_name.get(override.casefold()) if override else None
            if override and target_index is None:
                invalid_overrides.append({"source_bone": source_name, "target_bone": override})
            method = "manual" if target_index is not None else "manual_unmapped"
        else:
            target_name = aliases.get(normalized)
            target_index = target_by_name.get(target_name.casefold()) if target_name else None
            method = (
                "semantic_alias"
                if target_index is not None
                else "nearest_mapped_ancestor_fallback"
            )
        mapping[bone_id] = target_index
        rows.append(
            {
                "source_bone": source_name,
                "normalized": normalized,
                "target_bone": target_nodes[target_index].name if target_index is not None else None,
                "method": method,
            }
        )
    if invalid_overrides:
        rendered = ", ".join(
            f"{row['source_bone']} -> {row['target_bone']}"
            for row in invalid_overrides[:8]
        )
        raise ValueError(f"manual humanoid mapping references unknown target bones: {rendered}")
    return mapping, {
        "source_bone_count": len(source_bone_ids),
        "directly_mapped_count": sum(value is not None for value in mapping.values()),
        "manual_mapped_count": sum(row["method"] == "manual" for row in rows),
        "unmapped_count": sum(value is None for value in mapping.values()),
        "rows": rows,
    }


def _humanoid_alias_table() -> dict[str, str]:
    result = {
        "hips": "pelvis",
        "pelvis": "pelvis",
        "root": "bip01",
        "spine": "spine",
        "spine1": "spine2",
        "spine2": "spine3",
        "spine3": "hspine1",
        "chest": "spine3",
        "upperchest": "hspine1",
        "neck": "neck",
        "neck1": "neck1",
        "head": "head",
        "leftshoulder": "l_clavicle",
        "leftclavicle": "l_clavicle",
        "leftarm": "l_upperarm",
        "leftupperarm": "l_upperarm",
        "leftforearm": "l_forearm",
        "leftlowerarm": "l_forearm",
        "lefthand": "l_hand",
        "rightshoulder": "r_clavicle",
        "rightclavicle": "r_clavicle",
        "rightarm": "r_upperarm",
        "rightupperarm": "r_upperarm",
        "rightforearm": "r_forearm",
        "rightlowerarm": "r_forearm",
        "righthand": "r_hand",
        "leftupleg": "l_thigh",
        "leftthigh": "l_thigh",
        "leftleg": "l_calf",
        "leftcalf": "l_calf",
        "leftfoot": "l_foot",
        "lefttoebase": "l_toebase",
        "lefttoe": "l_toebase",
        "rightupleg": "r_thigh",
        "rightthigh": "r_thigh",
        "rightleg": "r_calf",
        "rightcalf": "r_calf",
        "rightfoot": "r_foot",
        "righttoebase": "r_toebase",
        "righttoe": "r_toebase",
    }
    fingers = {"thumb": 0, "index": 1, "middle": 2, "ring": 3, "pinky": 4}
    for side_word, side in (("left", "l"), ("right", "r")):
        for finger_word, group in fingers.items():
            for source_segment, target_segment in (("1", "1"), ("2", "2"), ("3", "3"), ("4", "3")):
                result[f"{side_word}hand{finger_word}{source_segment}"] = f"{side}_finger{group}{target_segment}"
                result[f"{side_word}{finger_word}{source_segment}"] = f"{side}_finger{group}{target_segment}"
    # Blender/common generic names.
    result.update(
        {
            "upperarml": "l_upperarm",
            "forearml": "l_forearm",
            "handl": "l_hand",
            "upperarmr": "r_upperarm",
            "forearmr": "r_forearm",
            "handr": "r_hand",
            "legtopl": "l_thigh",
            "legbottoml": "l_calf",
            "footl": "l_foot",
            "legtopr": "r_thigh",
            "legbottomr": "r_calf",
            "footr": "r_foot",
            "armtopl": "l_upperarm",
            "armbottoml": "l_forearm",
            "armtopr": "r_upperarm",
            "armbottomr": "r_forearm",
        }
    )
    return result


def _normalize_bone_name(name: str) -> str:
    value = name.split(":")[-1].casefold()
    value = value.replace(".l", "l").replace(".r", "r")
    value = re.sub(r"[^a-z0-9]+", "", value)
    value = re.sub(r"^(mixamorig|bip01)", "", value)
    return value


# --------------------------------------------------------------------------- materials, scripts, reporting

def _material_table(
    scene: FbxScene,
    options: ModelBuildOptions,
) -> tuple[tuple[str, ...], dict[tuple[int, int], int], dict[str, Any]]:
    lookup: dict[tuple[int, int], int] = {}
    if options.material_mode == "test":
        for geometry in scene.geometries:
            slots = max(1, len(geometry.material_names))
            for slot in range(slots):
                lookup[(geometry.object_id, slot)] = 0
        return (options.test_material,), lookup, {
            "mode": "test_material_for_all_submeshes",
            "materials": [options.test_material],
            "fbx_materials_preserved_in_report_only": True,
        }

    materials: list[str] = []
    material_index: dict[str, int] = {}
    for geometry in scene.geometries:
        names = geometry.material_names or (f"{geometry.model_name}_material",)
        for slot, source_name in enumerate(names):
            safe = sanitize_name(f"{options.resource_name}_{source_name}", max_bytes=55) + ".mat"
            if safe not in material_index:
                material_index[safe] = len(materials)
                materials.append(safe)
            lookup[(geometry.object_id, slot)] = material_index[safe]
    if not materials:
        materials.append(options.test_material)
    return tuple(materials), lookup, {
        "mode": "preserve_fbx_slots_as_placeholder_material_names",
        "materials": materials,
        "warning": "The importer does not convert FBX shaders or textures into Techland .mat files yet.",
    }


def _companions(bone_names: Sequence[str], animation_script: str) -> tuple[str | None, str | None]:
    if not animation_script:
        return None, None
    ascr = f'AnimScriptAlias("{animation_script}")\n'
    lines = ['import "bscr.def"', "", "sub main()", "{"]
    for index, name in enumerate(bone_names):
        escaped = name.replace("\\", "\\\\").replace('"', '\\"')
        components = "POS | ROT | SCL" if index == 0 else "POS | ROT"
        lines.append(f'    SetBoneAnimTrans("{escaped}", {components}, LOD_OFF);')
    lines.extend(("}", ""))
    return ascr, "\n".join(lines)


def _base_report(scene: FbxScene, options: ModelBuildOptions, *, effective_mode: str) -> dict[str, Any]:
    return {
        "format": "dl_reanimated_model_import_build_v1",
        "resource_name": options.resource_name,
        "source_fbx": str(scene.path),
        "source_fbx_sha256": scene.sha256,
        "source_fbx_version": scene.version,
        "effective_mode": effective_mode,
        "coordinate_contract": {
            "source": "FBX X-right/Y-up/Z-front, scene units",
            "output": "Chrome model space, meters",
            "meters_per_fbx_unit": scene.meters_per_unit,
            "orientation_policy": options.orientation_policy,
            "basis_conversion": "FBX (x,y,z) -> Chrome (x,z,-y)" if options.orientation_policy in {"auto", "fbx_y_up_to_dying_light"} else options.orientation_policy,
            "vertex_space": "global bind/model space",
            "bone_local": "inverse(parent global bind) * global bind",
            "bone_reference": "inverse(global bind)",
        },
        "normal_policy": "FBX layer normal transformed by inverse-transpose; geometric face fallback",
        "tangent_policy": "reconstructed from final positions, normals and UV0",
        "vertex_policy": "triangle-corner expansion; material split; <=65535 vertices per source mesh node",
        "skin_policy": "combine duplicate clusters, discard zero weights, keep top four, normalize",
        "uv_policy": "UV0; V flipped" if options.flip_v else "UV0 preserved",
        "engine_or_editor_tested": False,
    }


def sanitize_name(value: str, *, max_bytes: int = 63) -> str:
    text = re.sub(r"[^A-Za-z0-9_]+", "_", str(value)).strip("_") or "model"
    encoded = text.encode("utf-8")
    if len(encoded) <= max_bytes:
        return text
    digest = hashlib.sha1(encoded).hexdigest()[:8]
    while len((text + "_" + digest).encode("utf-8")) > max_bytes and text:
        text = text[:-1]
    return (text.rstrip("_") + "_" + digest)[:max_bytes]


def _mesh_node_name(resource: str, model: str, material_slot: int, chunk_index: int) -> str:
    return sanitize_name(f"{resource}_{model}_m{material_slot:02d}_p{chunk_index:02d}", max_bytes=63)


def _validate_unique_bone_names(scene: FbxScene, bone_ids: Sequence[int]) -> None:
    names = [scene.model_names[row] for row in bone_ids]
    duplicates = [name for name, count in Counter(name.casefold() for name in names).items() if count > 1]
    if duplicates:
        raise ValueError(f"FBX skeleton contains duplicate bone names: {duplicates[:8]}")
    too_long = [name for name in names if len(name.encode("utf-8")) >= 64]
    if too_long:
        raise ValueError(f"FBX bone names exceed the Chrome 63-byte limit: {too_long[:8]}")


# --------------------------------------------------------------------------- matrix helpers

def _matrix_units_to_meters(matrix: np.ndarray, factor: float) -> np.ndarray:
    result = np.asarray(matrix, dtype=float).copy()
    result[:3, 3] *= factor
    return result


def _matrix3x4(matrix: np.ndarray) -> tuple[float, ...]:
    if matrix.shape != (4, 4):
        raise ValueError("expected a 4x4 matrix")
    return tuple(float(matrix[row, column]) for row in range(3) for column in range(4))


def _transform_point(matrix: np.ndarray, point: Sequence[float]) -> np.ndarray:
    value = matrix @ np.asarray((point[0], point[1], point[2], 1.0), dtype=float)
    if abs(float(value[3])) > 1.0e-12 and abs(float(value[3] - 1.0)) > 1.0e-12:
        value = value / value[3]
    return value[:3]


def _safe_normalize(value: Sequence[float]) -> np.ndarray:
    result = np.asarray(value, dtype=float)
    length = float(np.linalg.norm(result))
    if not math.isfinite(length) or length <= 1.0e-12:
        return np.asarray((0.0, 1.0, 0.0), dtype=float)
    return result / length
