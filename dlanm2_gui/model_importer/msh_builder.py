from __future__ import annotations

"""FBX model -> Chrome Engine 6 source-MSH authoring.

The production bind policy is intentionally fixed to the manually validated
Chrome 6 rule:

    node.local      = parent_global^-1 * node_global
    node.reference  = node_global^-1

Only the inverse-global reference policy is exposed by normal builds.
"""

from collections import Counter, defaultdict
from dataclasses import dataclass, field, replace
from pathlib import Path
from typing import Any, Iterable, Sequence
import hashlib
import json
import math
import re

import numpy as np

from .fbx_model import (
    BLENDSHAPE_IDENTITY_NOOP,
    BLENDSHAPE_MALFORMED,
    BLENDSHAPE_REAL_ANIMATED,
    BLENDSHAPE_REAL_STATIC,
    FbxGeometry,
    FbxScene,
    FbxTriangleCorner,
)
from ..retarget_mapping import HumanoidBoneMatch, scan_humanoid_bones
from .vendor.chrome_mesh_tools.math3d import matrix3x4_from_matrix4
from .vendor.chrome_mesh_tools.msh import MshFile
from .vendor.chrome_mesh_tools.smd import SmdFile
from .vendor.chrome_mesh_tools.smd_bind import build_smd_bind_matrices
from .vendor.chrome_mesh_tools.source_contract import (
    audit_source_msh_bytes_for_compiler,
)
from .vendor.chrome_mesh_tools.writer import (
    MSH_NODE_FLAG_ANIMATED,
    SourceLod,
    SourceMsh,
    SourceNode,
    SourceSkinVertex,
    SourceSubset,
)
from .skin_partition import (
    EmittedMeshPartition,
    MAX_SUBSET_PALETTE_ENTRIES,
    remap_global_influences_to_local,
    validate_local_palette_round_trip,
)
from .rig_contract import AuthoredRigContract

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
    # Position immediately before humanoid bind transfer.  Keeping this next
    # to the emitted position lets the preflight compare triangle topology in
    # the two bind spaces without re-parsing or guessing FBX corner order.
    source_position: np.ndarray
    normal: np.ndarray
    uv: tuple[float, float]
    color: tuple[int, int, int, int]
    # Always stored in the physical/global source-MSH node index space until
    # the final subset palette is known.  _chunk_to_lod is the only boundary
    # allowed to convert these to uint8 subset-local indexes.
    influences: list[tuple[int, float]]
    source_tangent: np.ndarray | None = None
    source_binormal: np.ndarray | None = None
    morph_identity: tuple[Any, ...] = ()
    # Diagnostic provenance used when tangent reconstruction encounters an
    # authored but unusable UV triangle.  These fields never enter the emitted
    # vertex key or binary payload.
    source_geometry_name: str = ""
    source_polygon_index: int = -1


@dataclass(slots=True)
class _MeshChunk:
    node_name: str
    material_index: int
    vertices: list[_BuildVertex] = field(default_factory=list)
    bone_palette: tuple[int, ...] = ()
    partition_index: int = 0
    source_triangle_count: int = 0
    maximum_influences: int = 0
    dropped_weight_total: float = 0.0
    fallback_weight_total: float = 0.0
    tangent_policy: str = "rebuilt_missing_source"


@dataclass(slots=True)
class ModelBuildResult:
    source: SourceMsh
    report: dict[str, Any]
    ascr_text: str | None
    bscr_text: str | None
    authored_rig_contract: AuthoredRigContract | None = None

    def write(self, output_directory: str | Path) -> dict[str, Path]:
        output = Path(output_directory)
        resource_name = str(self.report["resource_name"])
        msh_path = output / f"{resource_name}.msh"
        payload = self.source.build()
        compiler_preflight = audit_source_msh_bytes_for_compiler(
            payload, str(msh_path)
        )
        if not compiler_preflight["ready"]:
            raise ValueError(
                f"Source-MSH compiler preflight blocked {resource_name!r} before output:\n- "
                + "\n- ".join(str(value) for value in compiler_preflight["errors"])
                + "\nCorrect the named geometry/palette/node types and rebuild. Exact Rig is "
                "a viable alternative when a fitted humanoid mapping caused the invalid rows."
            )
        output.mkdir(parents=True, exist_ok=True)
        msh_path.write_bytes(payload)
        paths = {"msh": msh_path}
        if self.ascr_text is not None:
            ascr = msh_path.with_suffix(".ascr")
            ascr.write_text(self.ascr_text, encoding="utf-8")
            paths["ascr"] = ascr
        else:
            msh_path.with_suffix(".ascr").unlink(missing_ok=True)
        if self.bscr_text is not None:
            bscr = msh_path.with_suffix(".bscr")
            bscr.write_text(self.bscr_text, encoding="utf-8")
            paths["bscr"] = bscr
        else:
            msh_path.with_suffix(".bscr").unlink(missing_ok=True)
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
        report["compiler_preflight"] = compiler_preflight
        report_path = output / f"{resource_name}.model_import.json"
        report_path.write_text(json.dumps(report, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")
        paths["report"] = report_path
        return paths


def build_source_from_fbx(scene: FbxScene, options: ModelBuildOptions) -> ModelBuildResult:
    options.validate()
    _validate_non_morph_blend_shapes(scene)
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


def _validate_non_morph_blend_shapes(scene: FbxScene) -> None:
    targets = tuple(getattr(scene, "blend_shapes", ()) or ())
    malformed = tuple(
        row
        for row in targets
        if getattr(row, "classification", "") == BLENDSHAPE_MALFORMED
    )
    if malformed:
        details = []
        for row in malformed[:8]:
            details.append(
                f"shape {getattr(row, 'shape_name', '<unnamed>')!r} "
                f"({getattr(row, 'shape_object_id', None)}), channel "
                f"{getattr(row, 'channel_name', '<unresolved>')!r} "
                f"({getattr(row, 'channel_object_id', None)}), geometry "
                f"{getattr(row, 'base_geometry_name', '<unresolved>')!r} "
                f"({getattr(row, 'base_geometry_id', None)}): "
                + "; ".join(getattr(row, "malformed_fields", ()) or ())
            )
        raise ValueError(
            "Malformed model blendshape target blocked the build before output:\n- "
            + "\n- ".join(details)
            + "\nRepair the named connection or sparse field and re-export the FBX."
        )
    real = tuple(
        row
        for row in targets
        if getattr(row, "classification", "")
        in {BLENDSHAPE_REAL_STATIC, BLENDSHAPE_REAL_ANIMATED}
    )
    if real:
        raise ValueError(
            "Real model blendshape targets are unsupported by this non-morph build and "
            "cannot be discarded safely: "
            + ", ".join(
                f"{getattr(row, 'name', 'UnnamedShape')} "
                f"({getattr(row, 'classification', '')})"
                for row in real[:12]
            )
            + ". Enable a model importer with morph emission, bake the intended shape, or "
            "remove/export the real morph targets separately before rebuilding."
        )
    legacy_names = tuple(getattr(scene, "blend_shape_names", ()) or ())
    if legacy_names and not targets:
        raise ValueError(
            "Model blendshape channels lack inspectable Shape records and cannot be "
            "discarded safely: "
            + ", ".join(str(value) for value in legacy_names[:12])
            + ". Reload through the production FBX parser before building."
        )


def _canonical_bind_globals_meters(
    scene: FbxScene,
    bone_ids: Sequence[int],
    orientation_policy: str,
) -> dict[int, np.ndarray]:
    """Resolve model bind globals through the shared animation FBX contract."""

    from ..fbx_core import FbxDocument

    document = FbxDocument.from_scene(
        scene,
        orientation_policy=orientation_policy,
    )
    result: dict[int, np.ndarray] = {}
    for bone_id in bone_ids:
        name = scene.model_names[bone_id]
        normalized = document.bind_global_matrices[name]
        result[bone_id] = document.normalized_matrix_to_target_space(
            name,
            normalized,
        )
    return result


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
                target_by_source_bone=None,
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
    contract = _build_authored_rig_contract(scene, options, source, report)
    return ModelBuildResult(source, report, None, None, contract)


def _build_exact_rig(
    scene: FbxScene,
    options: ModelBuildOptions,
    weighted_bone_ids: set[int],
) -> ModelBuildResult:
    bone_ids = scene.depth_first_bones_for_weighted_ids(weighted_bone_ids)
    if not bone_ids:
        raise ValueError("could not resolve the weighted FBX armature")
    # The complete hierarchy is not stored in a uint8 field.  Source node
    # parents are int16 and subset palette entries are uint16; only the vertex
    # lookup into *one subset's* palette is uint8.  _geometry_chunks therefore
    # partitions actual weighted triangles instead of rejecting this hierarchy.
    if len(bone_ids) > 32_768:
        raise ValueError(
            f"Exact rig contains {len(bone_ids)} hierarchy nodes, but source-MSH parent "
            "indexes are signed int16 (maximum supported hierarchy size 32768). "
            "Remove nonessential hierarchy nodes or split the model before export."
        )
    _validate_unique_bone_names(scene, bone_ids)
    globals_m = _canonical_bind_globals_meters(
        scene,
        bone_ids,
        options.orientation_policy,
    )
    physical_by_id = {bone_id: index for index, bone_id in enumerate(bone_ids)}
    parent_indices = [
        physical_by_id.get(scene.nearest_limb_parent_id(bone_id), -1)
        for bone_id in bone_ids
    ]
    deform_indices = frozenset(
        physical_by_id[bone_id]
        for bone_id in weighted_bone_ids
        if bone_id in physical_by_id
    )
    authored_globals, frame_report = _author_chrome_bone_frames(
        [globals_m[bone_id] for bone_id in bone_ids],
        parent_indices,
        [scene.model_names[bone_id] for bone_id in bone_ids],
        deform_indices=deform_indices,
    )
    authored_by_id = {
        bone_id: authored_globals[index] for index, bone_id in enumerate(bone_ids)
    }
    bone_nodes = _source_bones_from_globals(
        scene,
        bone_ids,
        authored_by_id,
        physical_by_id,
        deform_bone_ids=weighted_bone_ids,
    )
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
                target_by_source_bone=None,
                transfer_by_source_bone=None,
                fallback_bone_local_index=0,
            )
        )
    if not chunks:
        raise ValueError("FBX contains no skinned triangle geometry")
    # Animated flags, companion BSCR entities and hierarchy references retain
    # unweighted deform/helper nodes.  Do not inject artificial weights into
    # visible vertices merely to make every global node appear in a palette.
    retention: list[dict[str, Any]] = []
    bone_bounds, bone_bounds_report = _compute_bone_local_bounds(
        chunks,
        authored_globals,
        [node.parent_index for node in bone_nodes],
        retention_weight_i16=options.retention_weight_i16,
        segment_proxy=True,
    )
    bone_nodes = [replace(node, bounds=bone_bounds[index]) for index, node in enumerate(bone_nodes)]
    geometry_nodes = [
        SourceNode(
            name=chunk.node_name,
            node_type=SOURCE_NODE_MESH_VBLEND,
            parent_index=-1,
            local_matrix=_matrix3x4(IDENTITY4),
            reference_matrix=_matrix3x4(IDENTITY4),
            tail_words=(MSH_NODE_FLAG_ANIMATED, 0, 0),
            lods=(_chunk_to_lod(chunk),),
        )
        for chunk in chunks
    ]
    bounds_carrier, model_bounds_report = _model_bounds_carrier(chunks, options.resource_name)
    source = SourceMsh(
        materials=materials,
        surface_names=(options.surface_name,),
        nodes=tuple([*bone_nodes, *geometry_nodes, bounds_carrier]),
    )
    source.validate()
    names = [scene.model_names[row] for row in bone_ids]
    ascr, bscr = _companions(names, options.animation_script)
    report = _base_report(scene, options, effective_mode="exact_rig")
    report.update(
        {
            "bone_count": len(deform_indices),
            "bone_names": [
                names[index] for index in sorted(deform_indices)
            ],
            "helper_count": len(bone_ids) - len(deform_indices),
            "helper_names": [
                names[index] for index in range(len(names)) if index not in deform_indices
            ],
            "animation_entity_names": names,
            "weighted_source_bone_count": len(weighted_bone_ids),
            "geometry_node_count": len(chunks),
            "material_policy": material_report,
            "total_vertices": sum(node.lods[0].vertex_count for node in geometry_nodes),
            "total_triangles": sum(len(node.lods[0].indices) // 3 for node in geometry_nodes),
            "skin_partitions": _skin_partition_report(chunks),
            "full_skeleton_retention": {
                "enabled": False,
                "requested_legacy_visible_weight_retention": options.retain_full_skeleton,
                "policy": "animation flags, BSCR entities and hierarchy references; no visible artificial weights",
                "assignment_count": len(retention),
                "weight_i16": options.retention_weight_i16,
                "assignments": retention,
                "retained_by_real_skin_weight": sorted(deform_indices),
                "retained_as_animated_helper": [
                    index for index in range(len(bone_ids)) if index not in deform_indices
                ],
                "retained_by_explicit_carrier": [],
            },
            "bone_bounds": bone_bounds_report,
            "model_bounds": model_bounds_report,
            "rig_frame_policy": frame_report,
            "warnings": warnings,
            "reference_matrix_policy": (
                "validated inverse(Chrome-authored global bind) at original FBX joint pivots"
            ),
            "animation_script": options.animation_script,
        }
    )
    contract = _build_authored_rig_contract(scene, options, source, report)
    return ModelBuildResult(source, report, ascr, bscr, contract)


def _build_dying_light_humanoid(
    scene: FbxScene,
    options: ModelBuildOptions,
    weighted_bone_ids: set[int],
) -> ModelBuildResult:
    if options.animation_script.strip().casefold() == "anims_man_all.scr":
        raise ValueError(
            "Dying Light Humanoid imports preserve the model's proportions and therefore use "
            "a fitted custom bind. Direct anims_man_all.scr tracks contain stock absolute "
            "translations and will deform this mesh. Leave Animation script empty, retarget "
            "the desired clips to the generated model .crig, then use a dedicated script for "
            "those retargeted resources."
        )
    smd = SmdFile.from_path(options.target_smd)
    target_bind = build_smd_bind_matrices(smd)
    target_nodes = list(smd.nodes)
    if len(target_nodes) > 32_768:
        raise ValueError(
            f"Target hierarchy contains {len(target_nodes)} nodes, exceeding the signed-int16 "
            "source-MSH parent-index capacity of 32768 nodes. This is independent of the "
            "256-entry per-subset skin-palette limit."
        )
    target_index_by_name = {node.name.casefold(): index for index, node in enumerate(target_nodes)}
    source_bone_ids = scene.depth_first_bones_for_weighted_ids(weighted_bone_ids)
    source_globals = _canonical_bind_globals_meters(
        scene,
        source_bone_ids,
        options.orientation_policy,
    )
    source_weight_usage = source_skin_weight_usage(scene, source_bone_ids)
    mapping, mapping_report = humanoid_bone_mapping(
        scene,
        source_bone_ids,
        target_nodes,
        manual_mapping=options.humanoid_bone_map,
        source_weight_totals=source_weight_usage["bone_weight_totals"],
    )
    mapped_count = sum(value is not None for value in mapping.values())
    if mapped_count < 12:
        raise ValueError(
            f"humanoid auto-map resolved only {mapped_count} source bones; use Exact rig mode"
        )
    fallback_target = target_index_by_name.get("pelvis")
    if fallback_target is None:
        available_roots = [
            str(node.name)
            for node in target_nodes
            if int(node.parent_index) < 0
        ]
        raise ValueError(
            "Dying Light humanoid target SMD has no explicit 'pelvis' bone for resolving "
            "unweighted/collapsed source influences. A first-node/root fallback would be "
            "ambiguous and is not safe. Affected target roots: "
            + (", ".join(available_roots) if available_roots else "none")
            + ". Use the canonical DL1 target SMD, explicitly repair the target hierarchy, "
            "or choose Exact original FBX rig mode."
        )
    effective_targets, effective_methods = _effective_humanoid_targets(
        scene,
        source_bone_ids,
        mapping,
        fallback_target=fallback_target,
    )
    weighted_coverage = _humanoid_weighted_coverage(
        scene,
        source_bone_ids,
        target_nodes,
        mapping_report,
        source_weight_usage,
        effective_targets,
        effective_methods,
    )
    _validate_humanoid_weighted_coverage(weighted_coverage)
    mapping_report["weighted_coverage"] = weighted_coverage
    stock_target_globals = {
        index: np.asarray(target_bind.global_bind[node.index], dtype=float)
        for index, node in enumerate(target_nodes)
    }
    bind_compatibility = _humanoid_bind_compatibility(
        scene,
        source_bone_ids,
        target_nodes,
        source_globals,
        stock_target_globals,
        effective_targets,
        source_weight_usage,
    )
    target_profile = _dying_light_humanoid_target_profile(target_nodes)
    active_target_count = int(target_profile["animation_entity_count"])
    active_target_nodes = target_nodes[:active_target_count]
    helper_names = frozenset(str(name).casefold() for name in target_profile["helper_names"])
    bone_target_indices = tuple(
        index
        for index, node in enumerate(active_target_nodes)
        if str(node.name).casefold() not in helper_names
    )
    bone_target_index_set = frozenset(bone_target_indices)
    invalid_skin_targets = sorted(
        {
            int(target)
            for target in effective_targets.values()
            if int(target) not in bone_target_index_set
        }
    )
    if invalid_skin_targets:
        names = ", ".join(
            str(target_nodes[index].name)
            for index in invalid_skin_targets[:10]
            if 0 <= index < len(target_nodes)
        )
        raise ValueError(
            "humanoid skin mapping targets non-deforming helper/mesh elements: "
            f"{names or invalid_skin_targets}. Map those source bones to actual Dying Light bones."
        )

    fitted_bind = _fit_dying_light_target_bind(
        scene,
        source_bone_ids,
        target_nodes,
        source_globals,
        stock_target_globals,
        mapping,
        effective_targets,
        source_weight_usage,
    )
    fitted_bind["report"]["purpose"] = (
        "author the stock-named humanoid hierarchy at the imported FBX joint pivots; "
        "the imported surface remains in its original bind shape"
    )
    active_parents = [
        next(
            (
                physical
                for physical, candidate in enumerate(active_target_nodes)
                if int(candidate.index) == int(node.parent_index)
            ),
            -1,
        )
        if int(node.parent_index) >= 0
        else -1
        for node in active_target_nodes
    ]
    active_fitted_globals = [
        np.asarray(fitted_bind["global_matrices"][index], dtype=float)
        for index in range(active_target_count)
    ]
    authored_target_globals, frame_report = _author_chrome_bone_frames(
        active_fitted_globals,
        active_parents,
        [str(node.name) for node in active_target_nodes],
        deform_indices=bone_target_index_set,
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
                # Humanoid mode changes only the skin palette.  Moving the
                # bind-pose vertices onto the stock player pivots changes the
                # character's proportions and was the direct cause of the
                # compressed torso, elongated neck and collapsed clothing in
                # ChromeEd.  Custom animations are retargeted to the authored
                # .crig instead of pretending this fitted rig is stock-bind.
                target_by_source_bone=effective_targets,
                transfer_by_source_bone=None,
                fallback_bone_local_index=fallback_target,
            )
        )
    if not chunks:
        raise ValueError("FBX contains no skinned triangle geometry")
    topology_preflight = _bind_topology_preflight(chunks)
    _validate_bind_topology_preflight(topology_preflight)
    retention: list[dict[str, Any]] = []
    physical_by_smd_index = {
        node.index: position for position, node in enumerate(active_target_nodes)
    }
    bone_bounds, bone_bounds_report = _compute_bone_local_bounds(
        chunks,
        authored_target_globals,
        active_parents,
        retention_weight_i16=options.retention_weight_i16,
        segment_proxy=True,
    )
    bone_nodes: list[SourceNode] = []
    for position, node in enumerate(active_target_nodes):
        parent = physical_by_smd_index[node.parent_index] if node.parent_index >= 0 else -1
        global_matrix = np.asarray(authored_target_globals[position], dtype=float)
        local_matrix = (
            np.linalg.inv(np.asarray(authored_target_globals[parent], dtype=float)) @ global_matrix
            if parent >= 0
            else global_matrix
        )
        bone_nodes.append(
            SourceNode(
                name=node.name,
                node_type=(
                    SOURCE_NODE_HELPER
                    if str(node.name).casefold() in helper_names
                    else SOURCE_NODE_BONE
                ),
                parent_index=parent,
                local_matrix=matrix3x4_from_matrix4(local_matrix),
                reference_matrix=matrix3x4_from_matrix4(np.linalg.inv(global_matrix)),
                bounds=bone_bounds[position],
                tail_words=(MSH_NODE_FLAG_ANIMATED, 0, 0),
            )
        )
    geometry_nodes = [
        SourceNode(
            name=chunk.node_name,
            node_type=SOURCE_NODE_MESH_VBLEND,
            parent_index=-1,
            local_matrix=_matrix3x4(IDENTITY4),
            reference_matrix=_matrix3x4(IDENTITY4),
            tail_words=(MSH_NODE_FLAG_ANIMATED, 0, 0),
            lods=(_chunk_to_lod(chunk),),
        )
        for chunk in chunks
    ]
    bounds_carrier, model_bounds_report = _model_bounds_carrier(chunks, options.resource_name)
    source = SourceMsh(
        materials=materials,
        surface_names=(options.surface_name,),
        # CMeshFileBase::InitNumAnimEntities stops at the first unattached
        # skinned mesh.  The carrier is deliberately after geometry: it fixes
        # CMeshFileBase::CalculateBoundingBox without becoming an animation
        # entity or affecting skin palette indices.
        nodes=tuple([*bone_nodes, *geometry_nodes, bounds_carrier]),
    )
    source.validate()
    names = [
        node.name
        for node in active_target_nodes
        if str(node.name).casefold() not in helper_names
    ]
    companion_names = [node.name for node in active_target_nodes]
    ascr, bscr = _companions(
        companion_names,
        options.animation_script,
        fitted_humanoid=True,
    )
    report = _base_report(scene, options, effective_mode="dying_light_humanoid")
    report.update(
        {
            "bone_count": len(bone_target_indices),
            "bone_names": names,
            "helper_count": len(helper_names),
            "helper_names": [node.name for node in active_target_nodes if str(node.name).casefold() in helper_names],
            "omitted_stock_mesh_root_count": len(target_nodes) - active_target_count,
            "humanoid_target_profile": target_profile,
            "geometry_node_count": len(chunks),
            "material_policy": material_report,
            "total_vertices": sum(node.lods[0].vertex_count for node in geometry_nodes),
            "total_triangles": sum(len(node.lods[0].indices) // 3 for node in geometry_nodes),
            "skin_partitions": _skin_partition_report(chunks),
            "humanoid_mapping": mapping_report,
            "humanoid_bind_compatibility": bind_compatibility,
            "humanoid_fitted_bind": fitted_bind["report"],
            "bind_topology_preflight": topology_preflight,
            "bone_bounds": bone_bounds_report,
            "model_bounds": model_bounds_report,
            "rig_frame_policy": frame_report,
            "mapped_source_bone_count": mapped_count,
            "full_skeleton_retention": {
                "enabled": False,
                "requested_legacy_visible_weight_retention": options.retain_full_skeleton,
                "policy": "animation flags, BSCR entities and hierarchy references; no visible artificial weights",
                "assignment_count": len(retention),
                "weight_i16": options.retention_weight_i16,
                "assignments": retention,
                "retained_by_real_skin_weight": sorted(bone_target_indices),
                "retained_as_animated_helper": [
                    index
                    for index, node in enumerate(active_target_nodes)
                    if str(node.name).casefold() in helper_names
                ],
                "retained_by_explicit_carrier": [],
            },
            "reference_matrix_policy": (
                "validated inverse(Chrome-authored fitted target global bind)"
            ),
            "bind_transfer_policy": (
                "preserve every imported bind-pose vertex and normal exactly; remap only the skin "
                "weights to the fitted stock-named target palette"
            ),
            "animation_script": options.animation_script,
            "warnings": [
                *scene.warnings,
                *_humanoid_mapping_warnings(
                    weighted_coverage,
                    topology_preflight,
                    bind_compatibility,
                ),
                "Dying Light Humanoid mode preserves the imported proportions and emits a fitted "
                "stock-named rig. Retarget animations to the generated model .crig; raw stock "
                "anims_man_all tracks are not bind-compatible with this fitted skeleton.",
            ],
        }
    )
    aliases_by_name: dict[str, list[str]] = {}
    for row in weighted_coverage.get("rows", []):
        target_name = str(row.get("effective_target_bone", "")).strip()
        source_name = str(row.get("source_bone", "")).strip()
        if target_name and source_name and target_name.casefold() != source_name.casefold():
            aliases_by_name.setdefault(target_name, []).append(source_name)
    contract = _build_authored_rig_contract(
        scene,
        options,
        source,
        report,
        aliases_by_name=aliases_by_name,
    )
    return ModelBuildResult(source, report, ascr, bscr, contract)


_PLAYER_1_TPP_HELPER_NAMES = (
    "hspine",
    "hspine1",
    "refcamera",
    "eyecamera",
    "l_normal",
    "l_normal2",
    "l_handholder",
    "headend",
    "l_eye",
    "l_eye_pos",
    "r_eye",
    "r_eye_pos",
    "eyes",
    "r_normal",
    "r_normal2",
    "r_handholder",
    "propsholder1",
    "propsholder2",
)

_PLAYER_1_TPP_MESH_ROOT_NAMES = (
    "sc_boots",
    "sc_hand_l",
    "sc_hand_r",
    "sc_head",
    "sc_shirt",
    "sc_trousers",
    "beard",
    "cult_arm_belt",
    "flashlight",
    "hair",
    "kevin_boots",
    "kevin_shirt",
    "kevin_trousers",
    "mask",
    "player_1_hand_l_tpp",
    "player_1_hand_r_tpp",
    "player_1_hip_bag",
    "player_4_head",
    "watch",
)


def _dying_light_humanoid_target_profile(target_nodes: Sequence[Any]) -> dict[str, Any]:
    """Recover the stock player compact-element roles from its source SMD.

    ``player_1_tpp.smd`` contains 69 deform bones, 18 transform helpers and
    19 root mesh slots.  Treating all 106 rows as bones made ChromeEd render
    cameras, holders and clothing roots in the Bones overlay and forced tiny
    retention weights into nodes which are not skin bones.  Imported geometry
    replaces the final mesh slots, so only the 87 animation entities are kept.
    """

    names = tuple(str(node.name).casefold() for node in target_nodes)
    stock_mesh_roots = tuple(value.casefold() for value in _PLAYER_1_TPP_MESH_ROOT_NAMES)
    stock_helpers = tuple(value.casefold() for value in _PLAYER_1_TPP_HELPER_NAMES)
    if len(names) >= 106 and names[87:106] == stock_mesh_roots:
        prefix = names[:87]
        missing_helpers = sorted(set(stock_helpers) - set(prefix))
        if missing_helpers:
            raise ValueError(
                "player_1_tpp target hierarchy is missing expected helper rows: "
                + ", ".join(missing_helpers)
            )
        return {
            "name": "player_1_tpp_stock_compact_prefix",
            "animation_entity_count": 87,
            "bone_count": 69,
            "helper_count": 18,
            "helper_names": list(_PLAYER_1_TPP_HELPER_NAMES),
            "omitted_mesh_root_names": list(_PLAYER_1_TPP_MESH_ROOT_NAMES),
        }
    return {
        "name": "generic_smd_all_bones",
        "animation_entity_count": len(target_nodes),
        "bone_count": len(target_nodes),
        "helper_count": 0,
        "helper_names": [],
        "omitted_mesh_root_names": [],
    }


# --------------------------------------------------------------------------- geometry

def _geometry_chunks(
    scene: FbxScene,
    geometry: FbxGeometry,
    *,
    options: ModelBuildOptions,
    material_lookup: dict[tuple[int, int], int],
    bone_local_by_id: dict[int, int] | None,
    target_by_source_bone: dict[int, int] | None,
    transfer_by_source_bone: dict[int, tuple[int, np.ndarray]] | None,
    fallback_bone_local_index: int | None,
) -> list[_MeshChunk]:
    for cluster in geometry.clusters:
        if len(cluster.indexes) != len(cluster.weights):
            raise ValueError(
                f"Geometry {geometry.name!r} skin cluster {cluster.name!r} has different "
                "index/weight counts. Repair the skin modifier and re-export. Exact Rig "
                "cannot bypass malformed skin data."
            )
        for row_index, (control_point_index, weight) in enumerate(
            zip(cluster.indexes, cluster.weights)
        ):
            if not 0 <= int(control_point_index) < len(geometry.control_points):
                raise ValueError(
                    f"Geometry {geometry.name!r} skin cluster {cluster.name!r} row "
                    f"{row_index} references control point {control_point_index}, outside "
                    f"0..{len(geometry.control_points) - 1}. Repair the skin weights and "
                    "re-export before building. Exact Rig cannot bypass an invalid control-"
                    "point reference."
                )
            if not math.isfinite(float(weight)) or float(weight) < 0.0:
                raise ValueError(
                    f"Geometry {geometry.name!r} skin cluster {cluster.name!r} row "
                    f"{row_index} has invalid weight {weight!r}. Skin weights must be "
                    "finite and non-negative; normalize/repair them and re-export. Exact "
                    "Rig cannot make a non-finite or negative source weight valid."
                )
        if cluster.bone_id is None and any(
            float(weight) > 1.0e-12 for weight in cluster.weights
        ):
            raise ValueError(
                f"Geometry {geometry.name!r} skin cluster {cluster.name!r} has positive "
                "weights but is not linked to a LimbNode bone. Relink the cluster to the "
                "intended armature bone and re-export. Exact Rig still requires a valid "
                "skin-to-bone link and is not an alternative for this error."
            )
    bake_units = geometry.mesh_bind_global @ geometry.geometric_transform
    conversion = scene.coordinate_conversion_matrix(options.orientation_policy)
    determinant = float(np.linalg.det(conversion[:3, :3] @ bake_units[:3, :3]))
    if not math.isfinite(determinant) or abs(determinant) <= 1.0e-12:
        raise ValueError(
            f"Geometry {geometry.name!r} has a singular/non-finite mesh bind or geometric "
            "transform. Freeze zero scale/remove shear and re-export before building."
        )
    normal_matrix_fbx = np.linalg.inv(bake_units[:3, :3]).T
    direction_matrix_fbx = bake_units[:3, :3]
    influences = geometry.skin_influences
    normal_layer = geometry.first_layer("LayerElementNormal")
    tangent_layer = geometry.first_layer("LayerElementTangent")
    binormal_layer = geometry.first_layer("LayerElementBinormal")
    uv_layer = geometry.first_layer("LayerElementUV")
    color_layer = geometry.first_layer("LayerElementColor")

    by_material: dict[int, list[Any]] = defaultdict(list)
    for triangle in geometry.triangles:
        material_slot = (
            0
            if options.material_mode == "test"
            else geometry.material_slot_for_polygon(triangle.polygon_index)
        )
        by_material[material_slot].append(triangle)

    chunks: list[_MeshChunk] = []
    for material_slot, triangles in sorted(by_material.items()):
        global_material_index = material_lookup.get((geometry.object_id, material_slot), 0)
        chunk_index = 0
        current_palette: set[int] = set()

        def new_chunk(index: int) -> _MeshChunk:
            return _MeshChunk(
                node_name=_mesh_node_name(
                    options.resource_name,
                    geometry.model_name or geometry.name,
                    material_slot,
                    index,
                ),
                material_index=global_material_index,
                partition_index=index,
                tangent_policy=(
                    "imported" if tangent_layer is not None else "rebuilt_missing_source"
                ),
            )

        current = new_chunk(chunk_index)

        def flush_current() -> None:
            nonlocal current, current_palette, chunk_index
            if not current.vertices:
                return
            if current.tangent_policy != "imported" and uv_layer is not None:
                _reject_extreme_degenerate_uv_tangent_fallback(
                    current,
                    geometry_name=geometry.name,
                )
            current.bone_palette = tuple(sorted(current_palette))
            if len(current.bone_palette) > MAX_SUBSET_PALETTE_ENTRIES:
                raise ValueError(
                    f"Geometry {geometry.name!r} material {material_slot} partition "
                    f"{current.partition_index} needs {len(current.bone_palette)} bones; "
                    "the vertex-local palette index is uint8 and permits 256. "
                    "No valid influence was dropped."
                )
            chunks.append(current)
            chunk_index += 1
            current = new_chunk(chunk_index)
            current_palette = set()

        for triangle in triangles:
            corners = list(triangle.corners)
            if determinant < 0.0:
                corners[1], corners[2] = corners[2], corners[1]
            control_point_indexes = [corner.control_point_index for corner in corners]
            if len(set(control_point_indexes)) != 3:
                raise ValueError(
                    f"Geometry {geometry.name!r} polygon {triangle.polygon_index} contains a "
                    "triangle with repeated control-point indexes. Remove the degenerate face "
                    "and re-export before building."
                )
            triangle_vertices: list[_BuildVertex] = []
            face_positions: list[np.ndarray] = []
            triangle_dropped_weight_total = 0.0
            triangle_fallback_weight_total = 0.0
            triangle_has_invalid_source_tangent = False
            for corner in corners:
                if not 0 <= corner.control_point_index < len(geometry.control_points):
                    raise ValueError(
                        f"Geometry {geometry.name!r} polygon {triangle.polygon_index} references "
                        f"control point {corner.control_point_index}, outside 0.."
                        f"{len(geometry.control_points) - 1}. Triangulate/repair the mesh and re-export."
                    )
                local = geometry.control_points[corner.control_point_index]
                if not np.isfinite(local).all():
                    raise ValueError(
                        f"Geometry {geometry.name!r} control point {corner.control_point_index} "
                        "contains a non-finite position. Repair the vertex and re-export."
                    )
                point_units = _transform_point(bake_units, local)
                point_m = (conversion[:3, :3] @ point_units) * scene.meters_per_unit
                if not np.isfinite(point_m).all():
                    raise ValueError(
                        f"Geometry {geometry.name!r} polygon {triangle.polygon_index} produced a "
                        "non-finite transformed position. Check mesh bind, units and wrapper transforms."
                    )
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
                    except (ValueError, IndexError, np.linalg.LinAlgError) as exc:
                        raise ValueError(
                            f"Geometry {geometry.name!r} polygon {triangle.polygon_index} has "
                            f"an invalid {normal_layer.kind} lookup at polygon-vertex "
                            f"{corner.polygon_vertex_index}: {exc}. Repair the normal layer "
                            "mapping/index data or recalculate normals in the DCC, then re-export."
                        ) from exc
                source_tangent = None
                source_binormal = None
                if tangent_layer is not None:
                    try:
                        raw_tangent = np.asarray(
                            tangent_layer.value(
                                control_point_index=corner.control_point_index,
                                polygon_vertex_index=corner.polygon_vertex_index,
                                polygon_index=triangle.polygon_index,
                            ),
                            dtype=float,
                        )
                        transformed_tangent = (
                            conversion[:3, :3]
                            @ (direction_matrix_fbx @ raw_tangent[:3])
                        )
                        tangent_length = float(np.linalg.norm(transformed_tangent))
                        if not math.isfinite(tangent_length) or tangent_length <= 1.0e-12:
                            source_tangent = None
                        else:
                            source_tangent = transformed_tangent / tangent_length
                    except (ValueError, IndexError, np.linalg.LinAlgError):
                        source_tangent = None
                if binormal_layer is not None:
                    try:
                        raw_binormal = np.asarray(
                            binormal_layer.value(
                                control_point_index=corner.control_point_index,
                                polygon_vertex_index=corner.polygon_vertex_index,
                                polygon_index=triangle.polygon_index,
                            ),
                            dtype=float,
                        )
                        transformed_binormal = (
                            conversion[:3, :3]
                            @ (direction_matrix_fbx @ raw_binormal[:3])
                        )
                        binormal_length = float(np.linalg.norm(transformed_binormal))
                        if not math.isfinite(binormal_length) or binormal_length <= 1.0e-12:
                            source_binormal = None
                        else:
                            source_binormal = transformed_binormal / binormal_length
                    except (ValueError, IndexError, np.linalg.LinAlgError):
                        source_binormal = None
                uv = (0.0, 0.0)
                if uv_layer is not None:
                    try:
                        raw_uv = uv_layer.value(
                            control_point_index=corner.control_point_index,
                            polygon_vertex_index=corner.polygon_vertex_index,
                            polygon_index=triangle.polygon_index,
                        )
                        uv = (float(raw_uv[0]), 1.0 - float(raw_uv[1]) if options.flip_v else float(raw_uv[1]))
                    except (ValueError, IndexError) as exc:
                        raise ValueError(
                            f"Geometry {geometry.name!r} polygon {triangle.polygon_index} has "
                            f"an invalid {uv_layer.kind} lookup at polygon-vertex "
                            f"{corner.polygon_vertex_index}: {exc}. Repair the UV layer "
                            "mapping/index data and re-export before building."
                        ) from exc
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
                    except (ValueError, IndexError) as exc:
                        raise ValueError(
                            f"Geometry {geometry.name!r} polygon {triangle.polygon_index} has "
                            f"an invalid {color_layer.kind} lookup at polygon-vertex "
                            f"{corner.polygon_vertex_index}: {exc}. Repair the color layer "
                            "mapping/index data and re-export before building."
                        ) from exc
                source_rows = _clean_influences(influences.get(corner.control_point_index, []))
                source_positive_total = sum(weight for _, weight in source_rows)
                retained_source_total = sum(weight for _, weight in source_rows[:4])
                dropped_fraction = (
                    max(0.0, source_positive_total - retained_source_total) / source_positive_total
                    if source_positive_total > 0.0
                    else 0.0
                )
                if not source_rows and fallback_bone_local_index is not None:
                    vertex_influences = [(fallback_bone_local_index, 1.0)]
                    triangle_fallback_weight_total += 1.0
                elif target_by_source_bone is not None:
                    vertex_influences = _remap_vertex_influences(
                        source_rows,
                        target_by_source_bone,
                        fallback_bone_local_index or 0,
                    )
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
                        source_position=np.asarray(face_positions[-1], dtype=float).copy(),
                        normal=normal if normal is not None else np.zeros(3, dtype=float),
                        uv=uv,
                        color=color,
                        influences=vertex_influences,
                        source_tangent=source_tangent,
                        source_binormal=source_binormal,
                        source_geometry_name=geometry.name,
                        source_polygon_index=int(triangle.polygon_index),
                    )
                )
                triangle_dropped_weight_total += dropped_fraction
            face_normal = _safe_normalize(
                np.cross(face_positions[1] - face_positions[0], face_positions[2] - face_positions[0])
            )
            doubled_area = float(
                np.linalg.norm(
                    np.cross(
                        face_positions[1] - face_positions[0],
                        face_positions[2] - face_positions[0],
                    )
                )
            )
            if not math.isfinite(doubled_area) or doubled_area <= 1.0e-12:
                raise ValueError(
                    f"Geometry {geometry.name!r} polygon {triangle.polygon_index} contains a "
                    "zero-area triangle after bind/unit/orientation conversion. Remove the "
                    "degenerate face and re-export before building."
                )
            for vertex in triangle_vertices:
                if float(np.linalg.norm(vertex.normal)) < 1.0e-8:
                    vertex.normal = face_normal
                if vertex.source_tangent is None:
                    triangle_has_invalid_source_tangent = True
            triangle_bones = {
                int(global_node_index)
                for vertex in triangle_vertices
                for global_node_index, weight in vertex.influences
                if weight > 0.0
            }
            if len(triangle_bones) > 12:
                raise ValueError(
                    f"Geometry {geometry.name!r} polygon {triangle.polygon_index} requires "
                    f"{len(triangle_bones)} distinct bones after top-four normalization; "
                    "a triangle can require at most 12. Repair the corrupted skin data."
                )
            candidate_palette = current_palette | triangle_bones
            if current.vertices and (
                len(current.vertices) + 3 > options.max_vertices_per_mesh
                or len(candidate_palette) > MAX_SUBSET_PALETTE_ENTRIES
            ):
                flush_current()
                candidate_palette = set(triangle_bones)
            if len(candidate_palette) > MAX_SUBSET_PALETTE_ENTRIES:
                raise ValueError(
                    f"Geometry {geometry.name!r} polygon {triangle.polygon_index} cannot fit a "
                    "256-entry local skin palette by itself. No influence was dropped; repair "
                    "the source weights and re-export."
                )
            current.vertices.extend(triangle_vertices)
            current_palette = candidate_palette
            current.source_triangle_count += 1
            current.dropped_weight_total += triangle_dropped_weight_total
            current.fallback_weight_total += triangle_fallback_weight_total
            if triangle_has_invalid_source_tangent:
                current.tangent_policy = (
                    "rebuilt_invalid_source"
                    if tangent_layer is not None
                    else "rebuilt_missing_source"
                )
            current.maximum_influences = max(
                current.maximum_influences,
                max((len(vertex.influences) for vertex in triangle_vertices), default=0),
            )
        flush_current()
    return chunks


def _chunk_to_lod(
    chunk: _MeshChunk,
    *,
    bone_palette: tuple[int, ...] | None = None,
) -> SourceLod:
    """Deduplicate a partition and cross the global -> local palette boundary."""

    palette = chunk.bone_palette if bone_palette is None else tuple(bone_palette)
    if len(palette) > MAX_SUBSET_PALETTE_ENTRIES:
        raise ValueError(
            f"Mesh partition {chunk.node_name!r} has {len(palette)} palette entries; "
            "vertex local indexes are uint8 and permit at most 256."
        )
    has_skin = any(row.influences for row in chunk.vertices)
    if has_skin and not palette:
        raise ValueError(
            f"Skinned mesh partition {chunk.node_name!r} has no subset palette. "
            "This importer error was caught before output."
        )

    if (
        chunk.tangent_policy == "imported"
        and all(row.source_tangent is not None for row in chunk.vertices)
    ):
        expanded_tangents = [
            tuple(float(value) for value in _safe_normalize(row.source_tangent))
            for row in chunk.vertices
        ]
        expanded_bitangents = []
        for row, tangent in zip(chunk.vertices, expanded_tangents):
            normal = _safe_normalize(row.normal)
            if row.source_binormal is not None:
                bitangent = _safe_normalize(row.source_binormal)
            else:
                bitangent = _safe_normalize(
                    np.cross(normal, np.asarray(tangent, dtype=float))
                )
            expanded_bitangents.append(tuple(float(value) for value in bitangent))
    else:
        expanded_tangents, expanded_bitangents = _calculate_tangent_space(chunk.vertices)

    unique: dict[tuple[Any, ...], int] = {}
    emitted_vertices: list[_BuildVertex] = []
    positions: list[tuple[float, float, float]] = []
    normals: list[tuple[float, float, float]] = []
    tangents: list[tuple[float, float, float]] = []
    bitangents: list[tuple[float, float, float]] = []
    uvs: list[tuple[float, float]] = []
    colors: list[tuple[int, int, int, int]] = []
    skin_rows: list[SourceSkinVertex] = []
    indices: list[int] = []

    for corner_index, row in enumerate(chunk.vertices):
        global_influences = tuple((int(index), float(weight)) for index, weight in row.influences)
        local_influences = (
            remap_global_influences_to_local(global_influences, palette)
            if palette
            else ()
        )
        if palette:
            validate_local_palette_round_trip(
                local_influences,
                palette,
                global_influences,
            )
        position = tuple(float(value) for value in row.position)
        normal = tuple(float(value) for value in _safe_normalize(row.normal))
        tangent = tuple(float(value) for value in expanded_tangents[corner_index])
        bitangent = tuple(float(value) for value in expanded_bitangents[corner_index])
        key = (
            position,
            normal,
            tangent,
            bitangent,
            tuple(float(value) for value in row.uv),
            tuple(int(value) for value in row.color),
            global_influences,
            tuple(row.morph_identity),
        )
        emitted_index = unique.get(key)
        if emitted_index is None:
            emitted_index = len(emitted_vertices)
            if emitted_index > 0xFFFF:
                raise ValueError(
                    f"Mesh partition {chunk.node_name!r} exceeds 65535 unique vertices after "
                    "complete-key seam-safe deduplication. Split or simplify the mesh."
                )
            unique[key] = emitted_index
            emitted_vertices.append(row)
            positions.append(position)
            normals.append(normal)
            tangents.append(tangent)
            bitangents.append(bitangent)
            uvs.append(row.uv)
            colors.append(row.color)
            if palette:
                skin_rows.append(
                    SourceSkinVertex(
                        bone_indices=tuple(index for index, _ in local_influences),
                        weights=tuple(weight for _, weight in local_influences),
                    )
                )
        indices.append(emitted_index)

    skin = tuple(skin_rows)
    return SourceLod(
        positions=tuple(positions),
        normals=tuple(normals),
        tangents=tuple(tangents),
        bitangents=tuple(bitangents),
        colors=tuple(colors),
        uvs=tuple(uvs),
        indices=tuple(indices),
        skin_vertices=skin,
        subsets=(
            SourceSubset(
                material_index=chunk.material_index,
                first_index=0,
                index_count=len(indices),
                bone_palette=palette,
            ),
        ),
    )


def _maximum_weight_quantization_error(vertices: Sequence[_BuildVertex]) -> float:
    maximum = 0.0
    for vertex in vertices:
        weights = [max(0.0, float(weight)) for _, weight in vertex.influences]
        total = sum(weights)
        if total <= 0.0:
            continue
        normalized = [weight / total for weight in weights]
        quantized = [int(math.floor(weight * 32767.0)) for weight in normalized]
        remainder = 32767 - sum(quantized)
        if remainder:
            quantized[max(range(len(normalized)), key=normalized.__getitem__)] += remainder
        maximum = max(
            maximum,
            max(
                abs(weight - integer / 32767.0)
                for weight, integer in zip(normalized, quantized)
            ),
        )
    return maximum


def _skin_partition_report(chunks: Sequence[_MeshChunk]) -> dict[str, Any]:
    rows: list[dict[str, Any]] = []
    for chunk in chunks:
        lod = _chunk_to_lod(chunk)
        emitted = EmittedMeshPartition(
            material_index=chunk.material_index,
            partition_index=chunk.partition_index,
            triangle_count=len(lod.indices) // 3,
            vertex_count=lod.vertex_count,
            global_palette=chunk.bone_palette,
            maximum_influences=chunk.maximum_influences,
            dropped_weight_total=float(chunk.dropped_weight_total),
            fallback_weight_total=float(chunk.fallback_weight_total),
            maximum_quantization_error=_maximum_weight_quantization_error(chunk.vertices),
        ).to_dict()
        emitted.update(
            {
                "node_name": chunk.node_name,
                "expanded_corner_count": len(chunk.vertices),
                "deduplicated_vertex_count": lod.vertex_count,
                "tangent_policy": chunk.tangent_policy,
            }
        )
        rows.append(emitted)
    return {
        "partition_count": len(rows),
        "maximum_local_palette_size": max(
            (int(row["palette_size"]) for row in rows), default=0
        ),
        "maximum_emitted_vertex_count": max(
            (int(row["vertex_count"]) for row in rows), default=0
        ),
        "maximum_influences": max(
            (int(row["maximum_influences"]) for row in rows), default=0
        ),
        "maximum_weight_quantization_error": max(
            (float(row["maximum_quantization_error"]) for row in rows), default=0.0
        ),
        "dropped_weight_total": sum(
            float(row["dropped_weight_total"]) for row in rows
        ),
        "fallback_weight_total": sum(
            float(row["fallback_weight_total"]) for row in rows
        ),
        "partitions": rows,
        "index_contract": (
            "subset palettes store global source-node uint16 indexes; vertex skin bytes "
            "store only local indexes into their emitted subset palette"
        ),
    }


_EXTREME_DEGENERATE_UV_DETERMINANT = 1.0e-12


def _reject_extreme_degenerate_uv_tangent_fallback(
    chunk: _MeshChunk,
    *,
    geometry_name: str,
) -> None:
    """Block authored UV0 triangles that cannot define tangent space.

    UV-less geometry intentionally keeps the historical normal-derived
    fallback.  This gate is called only when an FBX UV layer was authored, so
    silently replacing that data with an arbitrary tangent basis would hide a
    damaged unwrap or layer export.
    """

    affected: list[tuple[int, int, float]] = []
    for corner_index in range(0, len(chunk.vertices), 3):
        triangle = chunk.vertices[corner_index : corner_index + 3]
        if len(triangle) != 3:
            raise ValueError(
                f"Geometry {geometry_name!r} mesh node {chunk.node_name!r} has an "
                "incomplete expanded triangle while rebuilding tangents. Repair the "
                "source topology and re-export before building."
            )
        a, b, c = triangle
        duv1 = np.asarray((b.uv[0] - a.uv[0], b.uv[1] - a.uv[1]), dtype=float)
        duv2 = np.asarray((c.uv[0] - a.uv[0], c.uv[1] - a.uv[1]), dtype=float)
        determinant = float(duv1[0] * duv2[1] - duv1[1] * duv2[0])
        if (
            not math.isfinite(determinant)
            or abs(determinant) <= _EXTREME_DEGENERATE_UV_DETERMINANT
        ):
            affected.append(
                (
                    corner_index // 3,
                    int(a.source_polygon_index),
                    determinant,
                )
            )

    if not affected:
        return

    preview = ", ".join(
        f"partition triangle {triangle_index} / source polygon {polygon_index} "
        f"(UV determinant {determinant!r})"
        for triangle_index, polygon_index, determinant in affected[:8]
    )
    if len(affected) > 8:
        preview += f", and {len(affected) - 8} more"
    raise ValueError(
        f"Geometry {geometry_name!r} mesh node {chunk.node_name!r} tangent rebuild "
        f"({chunk.tangent_policy}) would use a synthetic fallback basis for "
        f"{len(affected)} of {len(chunk.vertices) // 3} triangle(s) because authored "
        f"UV0 is extremely degenerate (absolute determinant <= "
        f"{_EXTREME_DEGENERATE_UV_DETERMINANT:g}). Affected rows: {preview}. "
        "Repair/unwrap UV0 or export a valid source tangent layer, then re-export "
        "before building. Exact Rig changes skeleton ownership, not invalid geometry, "
        "so it is not a viable alternative for this UV/tangent error."
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


def _orthonormal_rotation(linear: np.ndarray) -> np.ndarray:
    value = np.asarray(linear, dtype=float)
    if value.shape != (3, 3) or not np.isfinite(value).all():
        raise ValueError("bone rotation must be a finite 3x3 matrix")
    u, _scale, vt = np.linalg.svd(value)
    result = u @ vt
    if float(np.linalg.det(result)) < 0.0:
        u[:, -1] *= -1.0
        result = u @ vt
    return result


def _aim_positive_x_rotation(direction: np.ndarray, reference: np.ndarray) -> np.ndarray:
    x_axis = np.asarray(direction, dtype=float)
    length = float(np.linalg.norm(x_axis))
    if not math.isfinite(length) or length <= 1.0e-10:
        return _orthonormal_rotation(reference)
    x_axis /= length
    reference = _orthonormal_rotation(reference)
    z_axis = reference[:, 2] - x_axis * float(np.dot(reference[:, 2], x_axis))
    if float(np.linalg.norm(z_axis)) <= 1.0e-8:
        z_axis = reference[:, 0] - x_axis * float(np.dot(reference[:, 0], x_axis))
    if float(np.linalg.norm(z_axis)) <= 1.0e-8:
        seed = np.asarray((0.0, 0.0, 1.0), dtype=float)
        if abs(float(np.dot(seed, x_axis))) > 0.95:
            seed = np.asarray((0.0, 1.0, 0.0), dtype=float)
        z_axis = seed - x_axis * float(np.dot(seed, x_axis))
    z_axis /= float(np.linalg.norm(z_axis))
    y_axis = np.cross(z_axis, x_axis)
    y_axis /= float(np.linalg.norm(y_axis))
    return _orthonormal_rotation(np.column_stack((x_axis, y_axis, z_axis)))


def _author_chrome_bone_frames(
    global_bind_matrices: Sequence[np.ndarray],
    parent_indices: Sequence[int],
    bone_names: Sequence[str],
    *,
    deform_indices: frozenset[int] | set[int],
) -> tuple[list[np.ndarray], dict[str, Any]]:
    """Preserve pivots while authoring the +X frames ChromeEd visualizes.

    FBX/Blender armatures conventionally point bones along local +Y. Chrome's
    character assets and terminal-bone renderer use local +X. The bind matrix
    is free to use a different orthonormal frame as long as the reference matrix
    is its exact inverse and animations target the same emitted bind (the .crig
    generated by the model importer does exactly that).
    """

    count = len(global_bind_matrices)
    if len(parent_indices) != count or len(bone_names) != count:
        raise ValueError("bone frame tables have inconsistent lengths")
    originals = [np.asarray(value, dtype=float) for value in global_bind_matrices]
    if any(value.shape != (4, 4) or not np.isfinite(value).all() for value in originals):
        raise ValueError("bone global bind matrices must be finite 4x4 matrices")
    positions = [value[:3, 3].copy() for value in originals]
    references = [_orthonormal_rotation(value[:3, :3]) for value in originals]
    children: list[list[int]] = [[] for _ in range(count)]
    for index, parent in enumerate(parent_indices):
        if 0 <= parent < count:
            children[parent].append(index)

    def descendant_direction(index: int, child: int) -> np.ndarray | None:
        pending = [child]
        visited: set[int] = set()
        while pending:
            current = pending.pop(0)
            if current in visited:
                continue
            visited.add(current)
            direction = positions[current] - positions[index]
            if float(np.linalg.norm(direction)) > 1.0e-8:
                return direction
            pending.extend(children[current])
        return None

    authored: list[np.ndarray] = []
    aim_rows: list[dict[str, Any]] = []
    deform = set(deform_indices)
    for index in range(count):
        candidates: list[tuple[int, np.ndarray]] = []
        for child in children[index]:
            direction = descendant_direction(index, child)
            if direction is not None:
                candidates.append((child, direction))
        chosen_child: int | None = None
        direction: np.ndarray | None = None
        if candidates:
            reference_y = references[index][:, 1]
            chosen_child, direction = max(
                candidates,
                key=lambda row: (
                    float(np.dot(row[1] / np.linalg.norm(row[1]), reference_y)),
                    row[0] in deform,
                    float(np.linalg.norm(row[1])),
                    -row[0],
                ),
            )
        else:
            parent = parent_indices[index]
            if 0 <= parent < count:
                incoming = positions[index] - positions[parent]
                if float(np.linalg.norm(incoming)) > 1.0e-8:
                    direction = incoming

        value = np.eye(4, dtype=float)
        value[:3, :3] = (
            _aim_positive_x_rotation(direction, references[index])
            if direction is not None
            else authored[parent_indices[index]][:3, :3]
            if 0 <= parent_indices[index] < len(authored)
            else references[index]
        )
        value[:3, 3] = positions[index]
        authored.append(value)
        if direction is not None:
            unit = direction / float(np.linalg.norm(direction))
            cosine = max(-1.0, min(1.0, float(np.dot(value[:3, 0], unit))))
            aim_rows.append(
                {
                    "bone": str(bone_names[index]),
                    "chosen_child": (
                        str(bone_names[chosen_child]) if chosen_child is not None else None
                    ),
                    "positive_x_error_degrees": math.degrees(math.acos(cosine)),
                }
            )
    return authored, {
        "policy": "preserve FBX pivots; orthonormalize and aim local +X along the authored chain",
        "entity_count": count,
        "deform_bone_count": len(deform),
        "helper_count": count - len(deform),
        "maximum_positive_x_aim_error_degrees": max(
            (float(row["positive_x_error_degrees"]) for row in aim_rows), default=0.0
        ),
        "rows": aim_rows,
    }


def _model_bounds_carrier(
    chunks: Sequence[_MeshChunk],
    resource_name: str,
) -> tuple[SourceNode, dict[str, Any]]:
    points = np.asarray(
        [vertex.position for chunk in chunks for vertex in chunk.vertices],
        dtype=float,
    )
    if not len(points) or points.ndim != 2 or points.shape[1] != 3:
        raise ValueError("cannot build model bounds without emitted geometry")
    if not np.isfinite(points).all():
        raise ValueError("model bounds contain non-finite geometry")
    low = np.min(points, axis=0)
    high = np.max(points, axis=0)
    center = (low + high) * 0.5
    half = np.maximum((high - low) * 0.5, 0.005)
    bounds = tuple(float(value) for value in np.concatenate((center, half)))
    name = sanitize_name(f"{resource_name}_bounds", max_bytes=63)
    return (
        SourceNode(
            name=name,
            node_type=SOURCE_NODE_MESH,
            parent_index=-1,
            local_matrix=_matrix3x4(IDENTITY4),
            reference_matrix=_matrix3x4(IDENTITY4),
            bounds=bounds,
        ),
        {
            "policy": "non-rendering ordinary-MESH bounds carrier appended after skinned geometry",
            "node_name": name,
            "minimum_xyz": low.tolist(),
            "maximum_xyz": high.tolist(),
            "center_xyz": center.tolist(),
            "half_extents_xyz": half.tolist(),
            "diagonal_m": float(np.linalg.norm(high - low)),
        },
    )

def _source_bones_from_globals(
    scene: FbxScene,
    bone_ids: Sequence[int],
    globals_m: dict[int, np.ndarray],
    physical_by_id: dict[int, int],
    *,
    deform_bone_ids: set[int] | frozenset[int] | None = None,
) -> list[SourceNode]:
    result: list[SourceNode] = []
    selected = set(bone_ids)
    deform = selected if deform_bone_ids is None else set(deform_bone_ids)
    for bone_id in bone_ids:
        parent_id = scene.nearest_limb_parent_id(bone_id)
        parent_physical = physical_by_id[parent_id] if parent_id in selected else -1
        local = np.linalg.inv(globals_m[parent_id]) @ globals_m[bone_id] if parent_id in selected else globals_m[bone_id]
        reference = np.linalg.inv(globals_m[bone_id])
        result.append(
            SourceNode(
                name=scene.model_names[bone_id],
                node_type=(
                    SOURCE_NODE_BONE if bone_id in deform else SOURCE_NODE_HELPER
                ),
                parent_index=parent_physical,
                local_matrix=_matrix3x4(local),
                reference_matrix=_matrix3x4(reference),
                tail_words=(MSH_NODE_FLAG_ANIMATED, 0, 0),
            )
        )
    return result


def _compute_bone_local_bounds(
    chunks: Sequence[_MeshChunk],
    global_bind_matrices: Sequence[np.ndarray],
    parent_indices: Sequence[int],
    *,
    retention_weight_i16: int,
    segment_proxy: bool = False,
) -> tuple[list[tuple[float, float, float, float, float, float]], dict[str, Any]]:
    """Build the node AABBs ChromeEd uses for bones and model extents.

    The Techland compiler copies source-node bounds directly into compact
    entities.  Zero bone bounds make ChromeEd show only tiny pivot dots and
    also collapse the aggregate model box because the engine deliberately
    excludes skinned-mesh nodes from its reference-bounds calculation.
    """

    count = len(global_bind_matrices)
    if len(parent_indices) != count:
        raise ValueError("bone bound parent table does not match bind matrix count")
    inverse_globals = [np.linalg.inv(np.asarray(value, dtype=float)) for value in global_bind_matrices]
    retention = retention_weight_i16 / 32767.0
    meaningful_threshold = max(1.0e-4, retention * 4.0)
    points_by_bone: list[list[np.ndarray]] = [[] for _ in range(count)]
    for chunk in chunks:
        for vertex in chunk.vertices:
            meaningful = [
                (int(bone_index), float(weight))
                for bone_index, weight in vertex.influences
                if 0 <= int(bone_index) < count and float(weight) > meaningful_threshold
            ]
            if segment_proxy:
                # Bone overlays describe joint segments, not the complete set
                # of vertices touched by a blended influence.  Counting every
                # secondary influence makes a forearm box enclose the hand,
                # sleeve and torso and produces the giant crossed pyramids
                # visible in ChromeEd.  Dominant ownership is sufficient for
                # estimating a small transverse radius around the segment.
                meaningful = [max(meaningful, key=lambda row: (row[1], -row[0]))] if meaningful else []
            for bone_index, _weight in meaningful:
                points_by_bone[bone_index].append(
                    _transform_point(inverse_globals[bone_index], vertex.position)
                )

    children: list[list[int]] = [[] for _ in range(count)]
    for index, parent in enumerate(parent_indices):
        if 0 <= parent < count:
            children[parent].append(index)

    bounds: list[tuple[float, float, float, float, float, float]] = []
    weighted_count = 0
    fallback_count = 0
    minimum_half_extent = 0.005
    for index in range(count):
        points = points_by_bone[index]
        if points and not segment_proxy:
            weighted_count += 1
            array = np.asarray(points, dtype=float)
            low = np.min(array, axis=0)
            high = np.max(array, axis=0)
        else:
            fallback_count += 1
            segment_points = [np.zeros(3, dtype=float)]
            child_points = [
                _transform_point(
                    inverse_globals[index],
                    np.asarray(global_bind_matrices[child], dtype=float)[:3, 3],
                )
                for child in children[index]
            ]
            if child_points:
                # A branch point must not create one giant box enclosing every
                # child. Techland character bounds follow one local segment.
                # Frames authored by _author_chrome_bone_frames point local +X
                # at the selected continuation, so prefer alignment over raw
                # distance (raw distance makes pelvis aim at a thigh).
                segment_points.append(
                    max(
                        child_points,
                        key=lambda value: (
                            float(value[0]) / max(float(np.linalg.norm(value)), 1.0e-12),
                            float(np.linalg.norm(value)),
                        ),
                    )
                )
            elif segment_proxy:
                parent = parent_indices[index]
                parent_length = (
                    float(
                        np.linalg.norm(
                            np.asarray(global_bind_matrices[index], dtype=float)[:3, 3]
                            - np.asarray(global_bind_matrices[parent], dtype=float)[:3, 3]
                        )
                    )
                    if 0 <= parent < count
                    else 0.04
                )
                continuation_length = parent_length
                # Twist/share leaves are commonly colocated with their parent.
                # Their visible length is represented by the parent's main
                # continuation (thigh->calf, upperarm->forearm, and so on).
                # Walk up at most two levels to find that segment rather than
                # collapsing the leaf to the minimum marker size.
                ancestor = parent
                for _ in range(2):
                    if not 0 <= ancestor < count:
                        break
                    ancestor_origin = np.asarray(
                        global_bind_matrices[ancestor], dtype=float
                    )[:3, 3]
                    candidates = [
                        float(
                            np.linalg.norm(
                                np.asarray(global_bind_matrices[child], dtype=float)[:3, 3]
                                - ancestor_origin
                            )
                        )
                        for child in children[ancestor]
                        if child != index
                    ]
                    if candidates:
                        continuation_length = max(continuation_length, max(candidates))
                    if continuation_length > 0.02:
                        break
                    ancestor = parent_indices[ancestor]
                terminal_length = max(
                    0.012,
                    min(0.22, max(parent_length * 0.35, continuation_length * 0.5)),
                )
                segment_points.append(np.asarray((terminal_length, 0.0, 0.0), dtype=float))
            array = np.asarray(segment_points, dtype=float)
            low = np.min(array, axis=0)
            high = np.max(array, axis=0)
            if segment_proxy:
                segment_length = float(np.linalg.norm(array[-1] - array[0]))
                default_radius = max(
                    minimum_half_extent,
                    min(0.025, max(segment_length, 0.02) * 0.12),
                )
                radius_cap = max(0.015, min(0.08, max(segment_length, 0.02) * 0.25))
                if points:
                    support = np.asarray(points, dtype=float)
                    radius_y = min(
                        radius_cap,
                        max(default_radius, float(np.percentile(np.abs(support[:, 1]), 75.0))),
                    )
                    radius_z = min(
                        radius_cap,
                        max(default_radius, float(np.percentile(np.abs(support[:, 2]), 75.0))),
                    )
                else:
                    radius_y = default_radius
                    radius_z = default_radius
                low[1], high[1] = -radius_y, radius_y
                low[2], high[2] = -radius_z, radius_z
        center = (low + high) * 0.5
        half = np.maximum((high - low) * 0.5, minimum_half_extent)
        bounds.append(
            tuple(float(value) for value in np.concatenate((center, half)))
        )

    aggregate_low = np.full(3, np.inf, dtype=float)
    aggregate_high = np.full(3, -np.inf, dtype=float)
    for index, row in enumerate(bounds):
        center = np.asarray(row[:3], dtype=float)
        half = np.asarray(row[3:], dtype=float)
        local_corners = [
            center + half * np.asarray((x, y, z), dtype=float)
            for x in (-1.0, 1.0)
            for y in (-1.0, 1.0)
            for z in (-1.0, 1.0)
        ]
        model_corners = np.asarray(
            [
                _transform_point(np.asarray(global_bind_matrices[index], dtype=float), corner)
                for corner in local_corners
            ],
            dtype=float,
        )
        aggregate_low = np.minimum(aggregate_low, np.min(model_corners, axis=0))
        aggregate_high = np.maximum(aggregate_high, np.max(model_corners, axis=0))
    diagonal = float(np.linalg.norm(aggregate_high - aggregate_low)) if count else 0.0
    if not math.isfinite(diagonal) or diagonal <= 0.01:
        raise ValueError("generated bone bounds collapse to an invalid model extent")
    return bounds, {
        "policy": (
            "bone-local bind-segment proxy"
            if segment_proxy
            else "bone-local weighted-vertex AABB with segment fallback"
        ),
        "bone_count": count,
        "weighted_vertex_bound_count": weighted_count,
        "segment_fallback_count": fallback_count,
        "nonzero_bound_count": sum(
            float(np.linalg.norm(np.asarray(row[3:], dtype=float))) > 1.0e-8
            for row in bounds
        ),
        "retention_weight_ignored_below": meaningful_threshold,
        "minimum_half_extent_m": minimum_half_extent,
        "aggregate_model_min": aggregate_low.tolist(),
        "aggregate_model_max": aggregate_high.tolist(),
        "aggregate_model_diagonal_m": diagonal,
    }


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


def _remap_vertex_influences(
    source_rows: Sequence[tuple[int, float]],
    target_by_source_bone: dict[int, int],
    fallback_target: int,
) -> list[tuple[int, float]]:
    """Map skin weights without destructively warping the authored bind surface.

    A source and target skeleton may have very different proportions and bind
    rotations.  Applying ``target_bind * inverse(source_bind)`` independently
    to every weighted vertex influence reshapes the *base mesh* with linear
    blend skinning before Chrome ever sees it.  On stylised or non-matching
    rigs that produces stretched triangles and the shredded meshes observed in
    ChromeEd.  Chrome's inverse-global reference matrices already make every
    target skin matrix identity in the bind pose, so the safe conversion is to
    preserve the evaluated FBX vertex and remap only its palette weights.
    """

    cleaned = _normalize_top4(source_rows)
    if not cleaned:
        return [(fallback_target, 1.0)]
    remapped = [
        (target_by_source_bone.get(source_id, fallback_target), weight)
        for source_id, weight in cleaned
    ]
    return _normalize_top4(remapped) or [(fallback_target, 1.0)]


def source_skin_weight_usage(
    scene: FbxScene,
    source_bone_ids: Sequence[int],
) -> dict[str, Any]:
    """Measure weights as they will be consumed by emitted triangle corners.

    Counting cluster rows alone overstates unused control points and understates
    control points duplicated at UV/material seams.  This audit follows the
    triangle-corner expansion and top-four normalization used by the writer.
    """

    selected = set(source_bone_ids)
    totals: dict[int, float] = defaultdict(float)
    weighted_corners = 0
    unweighted_corners = 0
    for geometry in scene.geometries:
        influences = geometry.skin_influences
        for triangle in geometry.triangles:
            for corner in triangle.corners:
                rows = _normalize_top4(
                    [
                        (bone_id, weight)
                        for bone_id, weight in _clean_influences(
                            influences.get(corner.control_point_index, [])
                        )
                        if bone_id in selected
                    ]
                )
                if not rows:
                    unweighted_corners += 1
                    continue
                weighted_corners += 1
                for bone_id, weight in rows:
                    totals[bone_id] += weight
    return {
        "bone_weight_totals": dict(totals),
        "weighted_corner_count": weighted_corners,
        "unweighted_corner_count": unweighted_corners,
        "total_corner_count": weighted_corners + unweighted_corners,
        "total_normalized_weight": float(sum(totals.values())),
    }


def _effective_humanoid_targets(
    scene: FbxScene,
    source_bone_ids: Sequence[int],
    direct_mapping: dict[int, int | None],
    *,
    fallback_target: int,
) -> tuple[dict[int, int], dict[int, str]]:
    """Resolve every source bone without confusing fallback with direct coverage."""

    effective: dict[int, int] = {}
    methods: dict[int, str] = {}
    for source_id in source_bone_ids:
        direct = direct_mapping.get(source_id)
        if direct is not None:
            effective[source_id] = direct
            methods[source_id] = "direct"
            continue
        ancestor = scene.model_parent_id(source_id)
        while ancestor is not None:
            target = direct_mapping.get(ancestor)
            if target is not None:
                effective[source_id] = target
                methods[source_id] = "ancestor_fallback"
                break
            ancestor = scene.model_parent_id(ancestor)
        else:
            effective[source_id] = fallback_target
            methods[source_id] = "root_fallback"
    return effective, methods


def _humanoid_weighted_coverage(
    scene: FbxScene,
    source_bone_ids: Sequence[int],
    target_nodes: Sequence[Any],
    mapping_report: dict[str, Any],
    usage: dict[str, Any],
    effective_targets: dict[int, int],
    effective_methods: dict[int, str],
) -> dict[str, Any]:
    totals = {
        int(bone_id): float(weight)
        for bone_id, weight in dict(usage.get("bone_weight_totals", {})).items()
    }
    total_weight = float(sum(totals.values()))
    category_weights = {"direct": 0.0, "ancestor_fallback": 0.0, "root_fallback": 0.0}
    report_rows = {
        str(row.get("source_bone")): row
        for row in mapping_report.get("rows", [])
    }
    rows: list[dict[str, Any]] = []
    for source_id in source_bone_ids:
        weight = totals.get(source_id, 0.0)
        method = effective_methods[source_id]
        category_weights[method] += weight
        source_name = scene.model_names[source_id]
        target_index = effective_targets[source_id]
        direct_row = report_rows.get(source_name, {})
        rows.append(
            {
                "source_bone": source_name,
                "source_weight": weight,
                "source_weight_fraction": weight / total_weight if total_weight > 0.0 else 0.0,
                "semantic_role": direct_row.get("role"),
                "direct_mapping_method": direct_row.get("method", "unmapped"),
                "manual_role_mismatch": bool(direct_row.get("manual_role_mismatch", False)),
                "effective_method": method,
                "effective_target_bone": str(target_nodes[target_index].name),
            }
        )
    rows.sort(key=lambda row: (-row["source_weight"], row["source_bone"].casefold()))
    fractions = {
        key: value / total_weight if total_weight > 0.0 else 0.0
        for key, value in category_weights.items()
    }
    fallback_rows = [row for row in rows if row["effective_method"] != "direct" and row["source_weight"] > 0.0]
    weighted_corner_count = int(usage.get("weighted_corner_count", 0))
    unweighted_corner_count = int(usage.get("unweighted_corner_count", 0))
    total_corners = weighted_corner_count + unweighted_corner_count
    return {
        "total_normalized_weight": total_weight,
        "weighted_corner_count": weighted_corner_count,
        "unweighted_corner_count": unweighted_corner_count,
        "unweighted_corner_fraction": unweighted_corner_count / total_corners if total_corners else 0.0,
        "direct_weight": category_weights["direct"],
        "direct_weight_fraction": fractions["direct"],
        "ancestor_fallback_weight": category_weights["ancestor_fallback"],
        "ancestor_fallback_weight_fraction": fractions["ancestor_fallback"],
        "root_fallback_weight": category_weights["root_fallback"],
        "root_fallback_weight_fraction": fractions["root_fallback"],
        "review_required": (
            fractions["ancestor_fallback"] > 0.10
            or fractions["root_fallback"] > 0.0
        ),
        "top_fallback_bones": fallback_rows[:12],
        "rows": rows,
    }


def _validate_humanoid_weighted_coverage(report: dict[str, Any]) -> None:
    total = float(report["total_normalized_weight"])
    if total <= 0.0:
        raise ValueError("humanoid skin mapping has no weighted triangle corners")
    direct = float(report["direct_weight_fraction"])
    root_fallback = float(report["root_fallback_weight_fraction"])
    top = ", ".join(
        f"{row['source_bone']} ({row['source_weight_fraction']:.1%})"
        for row in report.get("top_fallback_bones", [])[:5]
    ) or "none"
    if direct < 0.50:
        raise ValueError(
            "humanoid mapping directly covers only "
            f"{direct:.1%} of emitted skin weight (minimum 50%); review manual "
            f"bone mapping or use Exact rig mode. Highest fallback rows: {top}"
        )
    if root_fallback > 0.02:
        raise ValueError(
            f"humanoid mapping sends {root_fallback:.1%} of emitted skin weight "
            "to the generic pelvis fallback because no mapped ancestor exists "
            f"(maximum 2%). Map these source roots/deformation bones manually: {top}"
        )


def _percentiles(values: Sequence[float]) -> dict[str, float]:
    if not values:
        return {"p50": 0.0, "p95": 0.0, "p99": 0.0, "max": 0.0}
    array = np.asarray(values, dtype=float)
    return {
        "p50": float(np.percentile(array, 50)),
        "p95": float(np.percentile(array, 95)),
        "p99": float(np.percentile(array, 99)),
        "max": float(np.max(array)),
    }


def _weighted_percentile(
    rows: Sequence[tuple[float, float]],
    percentile: float,
) -> float:
    values = sorted(
        (float(value), float(weight))
        for value, weight in rows
        if math.isfinite(value) and math.isfinite(weight) and weight > 0.0
    )
    total = sum(weight for _value, weight in values)
    if not values or total <= 0.0:
        return 0.0
    threshold = max(0.0, min(1.0, float(percentile))) * total
    cumulative = 0.0
    for value, weight in values:
        cumulative += weight
        if cumulative >= threshold:
            return value
    return values[-1][0]


def _humanoid_bind_compatibility(
    scene: FbxScene,
    source_bone_ids: Sequence[int],
    target_nodes: Sequence[Any],
    source_globals: dict[int, np.ndarray],
    target_globals: dict[int, np.ndarray],
    effective_targets: dict[int, int],
    usage: dict[str, Any],
) -> dict[str, Any]:
    """Measure pivot/frame mismatch without modifying the model surface.

    The metric is deliberately diagnostic.  Different characters can have
    valid proportions, but large bind-pivot differences predict that stock
    animations will rotate vertices around points far from the authored rig.
    """

    weights = {
        int(bone_id): float(weight)
        for bone_id, weight in dict(usage.get("bone_weight_totals", {})).items()
        if math.isfinite(float(weight)) and float(weight) > 0.0
    }
    distances: list[tuple[float, float]] = []
    angles: list[tuple[float, float]] = []
    source_points: list[np.ndarray] = []
    target_points: list[np.ndarray] = []
    rows: list[dict[str, Any]] = []
    for source_id in source_bone_ids:
        weight = weights.get(source_id, 0.0)
        if weight <= 0.0:
            continue
        target_index = effective_targets[source_id]
        source_matrix = np.asarray(source_globals[source_id], dtype=float)
        target_matrix = np.asarray(target_globals[target_index], dtype=float)
        source_point = source_matrix[:3, 3]
        target_point = target_matrix[:3, 3]
        distance = float(np.linalg.norm(target_point - source_point))
        relative_rotation = target_matrix[:3, :3] @ source_matrix[:3, :3].T
        cosine = max(-1.0, min(1.0, (float(np.trace(relative_rotation)) - 1.0) * 0.5))
        angle = math.degrees(math.acos(cosine))
        distances.append((distance, weight))
        angles.append((angle, weight))
        source_points.append(source_point)
        target_points.append(target_point)
        rows.append(
            {
                "source_bone": scene.model_names[source_id],
                "target_bone": str(target_nodes[target_index].name),
                "source_weight": weight,
                "pivot_distance_m": distance,
                "frame_rotation_degrees": angle,
                "weighted_pivot_error": distance * weight,
            }
        )
    total_weight = sum(weight for _value, weight in distances)
    mean_distance = (
        sum(value * weight for value, weight in distances) / total_weight
        if total_weight > 0.0
        else 0.0
    )
    mean_angle = (
        sum(value * weight for value, weight in angles) / total_weight
        if total_weight > 0.0
        else 0.0
    )
    distance_p95 = _weighted_percentile(distances, 0.95)
    angle_p50 = _weighted_percentile(angles, 0.50)
    review = mean_distance > 0.08 or distance_p95 > 0.20 or angle_p50 > 45.0
    rows.sort(
        key=lambda row: (
            -float(row["weighted_pivot_error"]),
            str(row["source_bone"]).casefold(),
        )
    )
    return {
        "status": "review" if review else "compatible",
        "weighted_bone_count": len(rows),
        "total_normalized_skin_weight": total_weight,
        "weighted_mean_pivot_distance_m": mean_distance,
        "weighted_p95_pivot_distance_m": distance_p95,
        "weighted_mean_frame_rotation_degrees": mean_angle,
        "weighted_p50_frame_rotation_degrees": angle_p50,
        "source_weighted_bone_bounds_diagonal_m": _bounds_diagonal(source_points),
        "target_mapped_bone_bounds_diagonal_m": _bounds_diagonal(target_points),
        "top_mismatches": rows[:12],
        "interpretation": (
            "Large values mean Dying Light Humanoid mode must conform the source surface "
            "substantially to the stock target pivots. Exact rig plus animation retargeting "
            "is the fidelity-preserving alternative."
        ),
    }


def _fit_dying_light_target_bind(
    scene: FbxScene,
    source_bone_ids: Sequence[int],
    target_nodes: Sequence[Any],
    source_globals: dict[int, np.ndarray],
    stock_target_globals: dict[int, np.ndarray],
    direct_mapping: dict[int, int | None],
    effective_targets: dict[int, int],
    usage: dict[str, Any],
) -> dict[str, Any]:
    """Fit stock-named target pivots to the FBX before Chrome frame authoring.

    The target names provide the Dying Light humanoid semantic palette while
    the imported pivots preserve the character's proportions.  The resulting
    rig is intentionally a custom bind: animations must be retargeted to the
    emitted `.crig`, where bind-basis correction converts their rotations.
    """

    count = len(target_nodes)
    physical_by_smd_index = {
        int(node.index): position for position, node in enumerate(target_nodes)
    }
    parents = [
        physical_by_smd_index[int(node.parent_index)]
        if int(node.parent_index) >= 0
        else -1
        for node in target_nodes
    ]
    stock_globals = [
        np.asarray(stock_target_globals[index], dtype=float).copy()
        for index in range(count)
    ]
    weights = {
        int(bone_id): max(0.0, float(weight))
        for bone_id, weight in dict(usage.get("bone_weight_totals", {})).items()
        if math.isfinite(float(weight))
    }

    sources_by_target: dict[int, list[int]] = defaultdict(list)
    for source_id in source_bone_ids:
        target_index = direct_mapping.get(source_id)
        if target_index is not None:
            sources_by_target[int(target_index)].append(int(source_id))

    anchors: dict[int, np.ndarray] = {}
    anchor_sources: dict[int, int] = {}
    for target_index, source_ids in sources_by_target.items():
        # Manual/canonical direct rows should define the actual joint pivot.
        # Skin weight is a deterministic tie-breaker when duplicate manual
        # rows target one Chrome bone.
        source_id = min(
            source_ids,
            key=lambda value: (
                -weights.get(value, 0.0),
                source_bone_ids.index(value),
            ),
        )
        anchors[target_index] = np.asarray(source_globals[source_id], dtype=float)[:3, 3].copy()
        anchor_sources[target_index] = source_id

    target_by_name = {
        str(node.name).casefold(): index for index, node in enumerate(target_nodes)
    }
    pelvis_index = target_by_name.get("pelvis")
    bip01_index = target_by_name.get("bip01")
    # The model root must share the fitted hip pivot.  FBX scene/armature roots
    # often sit on the ground and are not the skeletal deformation origin.
    if pelvis_index is not None and pelvis_index in anchors and bip01_index is not None:
        anchors[bip01_index] = anchors[pelvis_index].copy()
        anchor_sources[bip01_index] = anchor_sources[pelvis_index]

    if not anchors:
        raise ValueError("humanoid target fitting has no mapped FBX pivot anchors")

    ratios: list[float] = []
    anchor_items = sorted(anchors.items())
    for left in range(len(anchor_items)):
        left_index, left_point = anchor_items[left]
        for right in range(left + 1, len(anchor_items)):
            right_index, right_point = anchor_items[right]
            stock_distance = float(
                np.linalg.norm(
                    stock_globals[right_index][:3, 3]
                    - stock_globals[left_index][:3, 3]
                )
            )
            fitted_distance = float(np.linalg.norm(right_point - left_point))
            if stock_distance > 0.05 and fitted_distance > 1.0e-5:
                ratios.append(fitted_distance / stock_distance)
    scale = float(np.median(np.asarray(ratios, dtype=float))) if ratios else 1.0
    scale = max(0.25, min(4.0, scale))

    origin_index = (
        pelvis_index
        if pelvis_index is not None and pelvis_index in anchors
        else min(anchors)
    )
    stock_origin = stock_globals[origin_index][:3, 3]
    fitted_origin = anchors[origin_index]
    fitted_positions: list[np.ndarray | None] = [None] * count
    for target_index, point in anchors.items():
        fitted_positions[target_index] = point.copy()

    # Fit intermediary hierarchy nodes *between* mapped pivots.  Scaling every
    # stock global around the pelvis and then overwriting mapped anchors creates
    # discontinuities whenever the source and stock skeletons have different
    # chain counts.  On the player torso that placed hspine1 above the FBX head
    # and then made the chain reverse back down to neck.  ChromeEd correctly
    # drew those remote pivots and stock rotations amplified the malformed
    # parent chain.  Interpolation by stock hierarchy arc length preserves the
    # intended ordering while retaining each node's stock rotation basis.
    anchored_indices = frozenset(anchors)
    children: list[list[int]] = [[] for _ in range(count)]
    for index, parent in enumerate(parents):
        if parent >= 0:
            children[parent].append(index)

    def nearest_anchor_ancestor(index: int) -> int | None:
        parent = parents[index]
        while parent >= 0:
            if parent in anchored_indices:
                return parent
            parent = parents[parent]
        return None

    def anchored_descendant_paths(index: int) -> list[tuple[float, tuple[int, ...]]]:
        rows: list[tuple[float, tuple[int, ...]]] = []
        pending: list[tuple[int, tuple[int, ...], float]] = [(index, (index,), 0.0)]
        while pending:
            current, path, distance = pending.pop()
            for child in children[current]:
                edge = float(
                    np.linalg.norm(
                        stock_globals[child][:3, 3]
                        - stock_globals[current][:3, 3]
                    )
                )
                child_path = (*path, child)
                child_distance = distance + edge
                if child in anchored_indices:
                    rows.append((child_distance, child_path))
                else:
                    pending.append((child, child_path, child_distance))
        return rows

    interpolated_count = 0
    for index in range(count):
        if fitted_positions[index] is not None:
            continue
        ancestor = nearest_anchor_ancestor(index)
        descendants = anchored_descendant_paths(index)
        if ancestor is None or not descendants:
            continue
        # Prefer the shortest stock-space continuation.  This naturally picks
        # neck over the lateral clavicle branches at hspine1, where the stock
        # hspine1 and neck pivots coincide.
        _distance, descendant_path = min(
            descendants,
            key=lambda row: (row[0], len(row[1]), row[1][-1]),
        )
        descendant = descendant_path[-1]
        path_from_ancestor: list[int] = [index]
        cursor = index
        while cursor != ancestor:
            cursor = parents[cursor]
            if cursor < 0:
                path_from_ancestor = []
                break
            path_from_ancestor.append(cursor)
        if not path_from_ancestor:
            continue
        path_from_ancestor.reverse()
        full_path = [*path_from_ancestor, *descendant_path[1:]]
        segment_lengths = [
            float(
                np.linalg.norm(
                    stock_globals[right][:3, 3]
                    - stock_globals[left][:3, 3]
                )
            )
            for left, right in zip(full_path, full_path[1:])
        ]
        total_length = sum(segment_lengths)
        node_offset = path_from_ancestor.index(index)
        numerator = sum(segment_lengths[:node_offset])
        fraction = (
            numerator / total_length
            if total_length > 1.0e-8
            else node_offset / max(1, len(full_path) - 1)
        )
        start = anchors[ancestor]
        end = anchors[descendant]
        fitted_positions[index] = start + fraction * (end - start)
        interpolated_count += 1

    # Resolve every remaining branch from its fitted parent using the scaled
    # stock global offset.  Unlike pelvis-relative global scaling, this cannot
    # detach an unanchored helper from an already-fitted parent.
    resolving: set[int] = set()

    def resolve_position(index: int) -> np.ndarray:
        value = fitted_positions[index]
        if value is not None:
            return value
        if index in resolving:
            raise ValueError(f"target skeleton contains a hierarchy cycle at {index}")
        resolving.add(index)
        parent = parents[index]
        if parent >= 0:
            value = resolve_position(parent) + scale * (
                stock_globals[index][:3, 3] - stock_globals[parent][:3, 3]
            )
        else:
            value = fitted_origin + scale * (
                stock_globals[index][:3, 3] - stock_origin
            )
        resolving.remove(index)
        fitted_positions[index] = value
        return value

    resolved_positions = [resolve_position(index) for index in range(count)]

    fitted_globals: list[np.ndarray] = []
    for index, stock_global in enumerate(stock_globals):
        value = np.asarray(stock_global, dtype=float).copy()
        value[:3, 3] = resolved_positions[index]
        fitted_globals.append(value)

    fitted_locals: list[np.ndarray] = []
    inverse_globals: list[np.ndarray] = []
    for index, value in enumerate(fitted_globals):
        parent = parents[index]
        local = (
            np.linalg.inv(fitted_globals[parent]) @ value
            if parent >= 0
            else value.copy()
        )
        fitted_locals.append(local)
        inverse_globals.append(np.linalg.inv(value))

    pivot_rows: list[tuple[float, float]] = []
    top_rows: list[dict[str, Any]] = []
    for source_id in source_bone_ids:
        weight = weights.get(source_id, 0.0)
        if weight <= 0.0:
            continue
        target_index = int(effective_targets[source_id])
        source_point = np.asarray(source_globals[source_id], dtype=float)[:3, 3]
        fitted_point = fitted_globals[target_index][:3, 3]
        distance = float(np.linalg.norm(fitted_point - source_point))
        pivot_rows.append((distance, weight))
        top_rows.append(
            {
                "source_bone": scene.model_names[source_id],
                "target_bone": str(target_nodes[target_index].name),
                "source_weight": weight,
                "fitted_pivot_distance_m": distance,
                "weighted_pivot_error": distance * weight,
            }
        )
    total_weight = sum(weight for _distance, weight in pivot_rows)
    mean_distance = (
        sum(distance * weight for distance, weight in pivot_rows) / total_weight
        if total_weight > 0.0
        else 0.0
    )
    top_rows.sort(
        key=lambda row: (
            -float(row["weighted_pivot_error"]),
            str(row["source_bone"]).casefold(),
        )
    )
    anchor_report = [
        {
            "target_bone": str(target_nodes[target_index].name),
            "source_bone": scene.model_names[source_id],
            "position": anchors[target_index].tolist(),
        }
        for target_index, source_id in sorted(anchor_sources.items())
    ]
    return {
        "global_matrices": fitted_globals,
        "local_matrices": fitted_locals,
        "inverse_global_matrices": inverse_globals,
        "report": {
            "policy": (
                "fit stock-named target joints to FBX pivots, then author Chrome +X bone frames"
            ),
            "anchor_count": len(anchors),
            "interpolated_hierarchy_node_count": interpolated_count,
            "unanchored_stock_proportion_scale": scale,
            "weighted_mean_pivot_distance_m": mean_distance,
            "weighted_p95_pivot_distance_m": _weighted_percentile(pivot_rows, 0.95),
            "anchors": anchor_report,
            "top_residual_mismatches": top_rows[:12],
            "animation_contract": (
                "retarget source or stock animations to the generated model .crig; do not bind "
                "raw stock absolute tracks directly to this fitted hierarchy"
            ),
        },
    }


def _bounds_diagonal(points: Sequence[np.ndarray]) -> float:
    if not points:
        return 0.0
    array = np.asarray(points, dtype=float)
    if not np.all(np.isfinite(array)):
        return 0.0
    return float(np.linalg.norm(np.max(array, axis=0) - np.min(array, axis=0)))


def _bind_topology_preflight(chunks: Sequence[_MeshChunk]) -> dict[str, Any]:
    """Detect bind-transfer explosions before invoking Techland's compiler."""

    source_points: list[np.ndarray] = []
    output_points: list[np.ndarray] = []
    stretch: list[float] = []
    distortion: list[float] = []
    source_degenerate = 0
    output_collapsed = 0
    nonfinite = 0
    triangle_count = 0
    epsilon = 1.0e-9
    for chunk in chunks:
        for vertex in chunk.vertices:
            source_points.append(vertex.source_position)
            output_points.append(vertex.position)
            if not (
                np.all(np.isfinite(vertex.source_position))
                and np.all(np.isfinite(vertex.position))
                and np.all(np.isfinite(vertex.normal))
            ):
                nonfinite += 1
        for index in range(0, len(chunk.vertices), 3):
            triangle = chunk.vertices[index : index + 3]
            if len(triangle) != 3:
                continue
            triangle_count += 1
            for left, right in ((0, 1), (1, 2), (2, 0)):
                source_length = float(np.linalg.norm(
                    triangle[right].source_position - triangle[left].source_position
                ))
                output_length = float(np.linalg.norm(
                    triangle[right].position - triangle[left].position
                ))
                if not math.isfinite(source_length) or not math.isfinite(output_length):
                    continue
                if source_length <= epsilon:
                    source_degenerate += 1
                    continue
                ratio = output_length / source_length
                if output_length <= epsilon:
                    output_collapsed += 1
                    continue
                stretch.append(ratio)
                distortion.append(max(ratio, 1.0 / ratio))
    edge_count = triangle_count * 3
    collapsed_fraction = output_collapsed / edge_count if edge_count else 0.0
    stretch_summary = _percentiles(stretch)
    distortion_summary = _percentiles(distortion)
    blocking: list[str] = []
    if nonfinite:
        blocking.append(f"{nonfinite} emitted vertices contain non-finite bind data")
    if collapsed_fraction > 0.01:
        blocking.append(f"{collapsed_fraction:.1%} of triangle edges collapse after bind transfer")
    if distortion_summary["p95"] > 4.0 and distortion_summary["p99"] > 10.0:
        blocking.append(
            "bind transfer severely distorts triangle topology "
            f"(p95={distortion_summary['p95']:.2f}x, p99={distortion_summary['p99']:.2f}x)"
        )
    warning = distortion_summary["p95"] > 2.0 or distortion_summary["p99"] > 5.0
    return {
        "status": "fail" if blocking else "warning" if warning else "pass",
        "triangle_count": triangle_count,
        "edge_count": edge_count,
        "source_bounds_diagonal": _bounds_diagonal(source_points),
        "output_bounds_diagonal": _bounds_diagonal(output_points),
        "nonfinite_vertex_count": nonfinite,
        "source_degenerate_edge_count": source_degenerate,
        "output_collapsed_edge_count": output_collapsed,
        "output_collapsed_edge_fraction": collapsed_fraction,
        "edge_stretch_ratio": stretch_summary,
        "edge_distortion_ratio": distortion_summary,
        "blocking_reasons": blocking,
    }


def _validate_bind_topology_preflight(report: dict[str, Any]) -> None:
    reasons = list(report.get("blocking_reasons", []))
    if reasons:
        raise ValueError(
            "humanoid bind/topology preflight failed before compilation: "
            + "; ".join(str(reason) for reason in reasons)
            + ". Check the FBX mesh bind transform, axis conversion, and bone mapping."
        )


def _humanoid_mapping_warnings(
    coverage: dict[str, Any],
    topology: dict[str, Any],
    compatibility: dict[str, Any] | None = None,
) -> list[str]:
    warnings: list[str] = []
    ancestor = float(coverage["ancestor_fallback_weight_fraction"])
    root = float(coverage["root_fallback_weight_fraction"])
    if ancestor > 0.10:
        names = ", ".join(
            row["source_bone"]
            for row in coverage.get("top_fallback_bones", [])[:6]
        )
        warnings.append(
            f"Humanoid mapping uses nearest-ancestor fallback for {ancestor:.1%} "
            f"of emitted skin weight; review these high-weight rows: {names}."
        )
    if root > 0.0:
        warnings.append(
            f"Humanoid mapping uses generic pelvis fallback for {root:.1%} of emitted skin weight."
        )
    mismatches = [
        row["source_bone"]
        for row in coverage.get("rows", [])
        if row.get("manual_role_mismatch") and float(row.get("source_weight", 0.0)) > 0.0
    ]
    if mismatches:
        warnings.append(
            "Manual humanoid mappings cross anatomical roles for weighted bones: "
            + ", ".join(mismatches[:10])
            + ". Review or re-run Auto-map humanoid."
        )
    if topology.get("status") == "warning":
        distortion = topology["edge_distortion_ratio"]
        warnings.append(
            "Bind transfer changes triangle edge lengths more than usual "
            f"(p95={distortion['p95']:.2f}x, p99={distortion['p99']:.2f}x); "
            "inspect the bind pose in ChromeEd."
        )
    if compatibility and compatibility.get("status") == "review":
        warnings.append(
            "The Dying Light skeleton differs substantially from the FBX bind rig "
            f"(weighted mean pivot offset {compatibility['weighted_mean_pivot_distance_m']:.3f} m; "
            f"weighted p95 {compatibility['weighted_p95_pivot_distance_m']:.3f} m). "
            "Humanoid mode will conform the surface to the stock skeleton; use Exact rig "
            "plus animation retargeting if preserving the source proportions matters more."
        )
    return warnings


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
    *,
    eligible_bone_indices: Sequence[int] | None = None,
) -> list[dict[str, Any]]:
    used = {
        bone
        for chunk in chunks
        for vertex in chunk.vertices
        for bone, weight in vertex.influences
        if weight > 0.0
    }
    eligible = (
        tuple(int(value) for value in eligible_bone_indices)
        if eligible_bone_indices is not None
        else tuple(range(bone_count))
    )
    if any(value < 0 or value >= bone_count for value in eligible):
        raise ValueError("skeleton retention contains an out-of-range bone index")
    missing = [bone for bone in eligible if bone not in used]
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
    source_weight_totals: dict[int, float] | None = None,
) -> tuple[dict[int, int | None], dict[str, Any]]:
    target_by_name = {str(node.name).casefold(): index for index, node in enumerate(target_nodes)}
    overrides = {
        str(source).casefold(): str(target)
        for source, target in dict(manual_mapping or {}).items()
    }
    source_names = [scene.model_names[bone_id] for bone_id in source_bone_ids]
    source_matches = _scan_model_humanoid_bones(source_names)
    target_matches = _scan_model_target_bones(str(node.name) for node in target_nodes)
    source_weight_totals = {
        int(bone_id): max(0.0, float(weight))
        for bone_id, weight in dict(source_weight_totals or {}).items()
        if math.isfinite(float(weight))
    }

    def candidate_priority(name: str, method: str) -> tuple[int, int, int, str]:
        plain = re.sub(r"[^a-z0-9]+", "_", name.casefold()).strip("_")
        helper = int(any(token in plain for token in ("twist", "share", "helper", "end")))
        decorated = int(any(token in plain for token in ("cc_base", "armature", "def_")))
        native = 0 if method == "native_dl" else 1
        return helper + decorated, native, len(plain), plain

    targets_by_role: dict[str, list[tuple[tuple[int, int, int, str], int]]] = defaultdict(list)
    for index, node in enumerate(target_nodes):
        name = str(node.name)
        match = target_matches.get(name)
        if match:
            targets_by_role[match.role].append(
                (candidate_priority(name, match.method), index)
            )
    target_for_role = {
        role: min(candidates, key=lambda row: row[0])[1]
        for role, candidates in targets_by_role.items()
    }

    # Only one source bone should own each anatomical role. This avoids
    # mapping CC share/deformation duplicates onto the same DL transform.
    sources_by_role: dict[str, list[tuple[tuple[float, int, int, int, str], int]]] = defaultdict(list)
    for bone_id in source_bone_ids:
        name = scene.model_names[bone_id]
        match = source_matches.get(name)
        if match:
            # Skin influence is the decisive signal when an FBX contains both
            # a control/helper row and a deformation row for one anatomical
            # role (notably CC_Base_Pelvis/CC_Base_Hip).  Name quality remains
            # the deterministic tie breaker for unweighted GUI previews.
            sources_by_role[match.role].append(
                ((-source_weight_totals.get(bone_id, 0.0), *candidate_priority(name, match.method)), bone_id)
            )
    selected_source_for_role = {
        role: min(candidates, key=lambda row: row[0])[1]
        for role, candidates in sources_by_role.items()
    }

    mapping: dict[int, int | None] = {}
    rows: list[dict[str, Any]] = []
    invalid_overrides: list[dict[str, str]] = []
    for bone_id in source_bone_ids:
        source_name = scene.model_names[bone_id]
        match = source_matches.get(source_name)
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
            native_target = _model_native_target_alias(source_name)
            target_index = (
                target_by_name.get(native_target.casefold())
                if native_target is not None
                else None
            )
            if target_index is not None:
                method = "native_model_alias"
            else:
                selected = bool(
                    match
                    and selected_source_for_role.get(match.role) == bone_id
                )
                target_index = target_for_role.get(match.role) if match and selected else None
                method = (
                    f"shared_{match.method}"
                    if target_index is not None and match is not None
                    else "nearest_mapped_ancestor_fallback"
                )
        mapping[bone_id] = target_index
        target_match = (
            target_matches.get(str(target_nodes[target_index].name))
            if target_index is not None
            else None
        )
        manual_role_mismatch = bool(
            method == "manual"
            and match is not None
            and target_match is not None
            and match.role != target_match.role
        )
        rows.append(
            {
                "source_bone": source_name,
                "normalized": _normalize_bone_name(source_name),
                "role": match.role if match else None,
                "confidence": match.confidence if match and target_index is not None else 0.0,
                "source_skin_weight": source_weight_totals.get(bone_id, 0.0),
                "target_bone": target_nodes[target_index].name if target_index is not None else None,
                "target_role": target_match.role if target_match else None,
                "method": method,
                "manual_role_mismatch": manual_role_mismatch,
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
        "manual_role_mismatches": [
            {
                "source_bone": row["source_bone"],
                "source_role": row["role"],
                "target_bone": row["target_bone"],
                "target_role": row["target_role"],
            }
            for row in rows
            if row["manual_role_mismatch"]
        ],
        "unmapped_count": sum(value is None for value in mapping.values()),
        "selection_policy": "highest emitted skin weight per anatomical role, then canonical-name priority",
        "rows": rows,
    }


def _scan_model_humanoid_bones(names: Iterable[str]) -> dict[str, HumanoidBoneMatch]:
    """Shared scan plus narrow deformation-bone aliases seen in model FBXs.

    ``CC_Base_Hip`` and ``CC_Base_Waist`` are common weighted deformation rows,
    but are not safe generic animation aliases.  They are recognized only in
    the model importer, where their skin weight and bind matrix are available.
    """

    values = [str(name) for name in names]
    matches = scan_humanoid_bones(values)
    for name in values:
        if name in matches:
            continue
        plain = name.split(":")[-1]
        plain = re.sub(r"([a-z0-9])([A-Z])", r"\1_\2", plain)
        plain = re.sub(r"[^a-z0-9]+", "_", plain.casefold()).strip("_")
        for prefix in ("cc_base_", "armature_", "def_"):
            if plain.startswith(prefix):
                plain = plain[len(prefix) :]
                break
        if plain in {"hip", "hips"}:
            matches[name] = HumanoidBoneMatch("pelvis", 0.96, "model_deformation_alias")
        elif plain == "waist":
            matches[name] = HumanoidBoneMatch("spine_1", 0.92, "model_deformation_alias")
        else:
            twist = re.fullmatch(
                r"([lr])_(upperarm|forearm|thigh)_twist_?0?([12])",
                plain,
            )
            if twist:
                side, limb, segment = twist.groups()
                matches[name] = HumanoidBoneMatch(
                    f"{side}_{limb}_twist_{int(segment)}",
                    0.94,
                    "model_deformation_twist",
                )
                continue
            neck_twist = re.fullmatch(r"neck_twist_?0?([12])", plain)
            if neck_twist:
                matches[name] = HumanoidBoneMatch(
                    f"neck_{int(neck_twist.group(1))}",
                    0.94,
                    "model_deformation_twist",
                )
    return matches


def _model_native_target_alias(source_name: str) -> str | None:
    """Resolve narrow model-deformation rows that generic retarget roles merge.

    CC rigs contain both Waist and Spine01, while the generic semantic mapper
    calls both ``spine_1``.  The model fitter needs their distinct pivots, so
    keep these source-format-specific aliases out of animation retargeting.
    The same applies to CC's ``Mid`` spelling for the middle finger.
    """

    plain = source_name.split(":")[-1]
    plain = re.sub(r"([a-z0-9])([A-Z])", r"\1_\2", plain)
    plain = re.sub(r"[^a-z0-9]+", "_", plain.casefold()).strip("_")
    if plain == "cc_base_spine01":
        return "spine1"
    middle = re.fullmatch(r"cc_base_([lr])_mid([123])", plain)
    if middle:
        side, segment = middle.groups()
        return f"{side}_finger2{segment}"
    return None


def _scan_model_target_bones(names: Iterable[str]) -> dict[str, HumanoidBoneMatch]:
    """Extend the generic scan with Dying Light deformation-chain targets."""

    values = [str(name) for name in names]
    matches = scan_humanoid_bones(values)
    for name in values:
        plain = re.sub(r"[^a-z0-9]+", "_", name.casefold()).strip("_")
        if plain == "neck":
            matches[name] = HumanoidBoneMatch("neck_1", 0.99, "native_dl")
        elif plain == "neck1":
            matches[name] = HumanoidBoneMatch("neck_2", 0.99, "native_dl")
        else:
            twist_roles = {
                "l_uparmtwist": "l_upperarm_twist_1",
                "r_uparmtwist": "r_upperarm_twist_1",
                "l_thightwist": "l_thigh_twist_1",
                "r_thightwist": "r_thigh_twist_1",
                "l_foretwist": "l_forearm_twist_1",
                "r_foretwist": "r_forearm_twist_1",
                "l_foretwist1": "l_forearm_twist_2",
                "r_foretwist1": "r_forearm_twist_2",
            }
            role = twist_roles.get(plain)
            if role:
                matches[name] = HumanoidBoneMatch(role, 0.99, "native_dl")
    return matches


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


def _companions(
    bone_names: Sequence[str],
    animation_script: str,
    *,
    fitted_humanoid: bool = False,
) -> tuple[str | None, str | None]:
    # BSCR controls which compact transform components survive compilation;
    # it is required even when the model intentionally has no stock animation
    # alias.  ASCR is optional and should only point at a script whose tracks
    # were authored/retargeted for this exact emitted bind.
    ascr = f'AnimScriptAlias("{animation_script}")\n' if animation_script else None
    lines = ['import "bscr.def"', "", "sub main()", "{"]
    for index, name in enumerate(bone_names):
        escaped = name.replace("\\", "\\\\").replace('"', '\\"')
        # Retargeted custom-rig tracks are authored in this exact parent-local
        # bind, so both translation and rotation are valid.  The first entity
        # retains scale for root policies.  Raw stock animation scripts are not
        # attached automatically to fitted humanoids.
        components = "POS | ROT | SCL" if index == 0 else "POS | ROT"
        lines.append(f'    SetBoneAnimTrans("{escaped}", {components}, LOD_OFF);')
    lines.extend(("}", ""))
    return ascr, "\n".join(lines)


def _base_report(scene: FbxScene, options: ModelBuildOptions, *, effective_mode: str) -> dict[str, Any]:
    resolved_orientation = scene.resolved_orientation_policy(options.orientation_policy)
    basis_conversion = (
        "FBX evaluated scene basis preserved"
        if resolved_orientation == "none"
        else "FBX (x,y,z) -> Chrome (x,z,-y)"
        if resolved_orientation == "fbx_y_up_to_dying_light"
        else resolved_orientation
    )
    return {
        "format": "dl_reanimated_model_import_build_v1",
        "resource_name": options.resource_name,
        "source_fbx": str(scene.path),
        "source_fbx_sha256": scene.sha256,
        "source_fbx_version": scene.version,
        "import_tolerance": getattr(scene, "import_tolerance", "recommended"),
        "effective_mode": effective_mode,
        "coordinate_contract": {
            "source": "FBX X-right/Y-up/Z-front, scene units",
            "output": "Chrome model space, meters",
            "meters_per_fbx_unit": scene.meters_per_unit,
            "orientation_policy": options.orientation_policy,
            "resolved_orientation_policy": resolved_orientation,
            "basis_conversion": basis_conversion,
            "axis_settings": dict(scene.axis_settings),
            "basis_matrix": scene.coordinate_conversion_matrix(
                options.orientation_policy
            ).tolist(),
            "vertex_space": "global bind/model space",
            "bone_local": "inverse(parent global bind) * global bind",
            "bone_reference": "inverse(global bind)",
        },
        "normal_policy": "FBX layer normal transformed by inverse-transpose; geometric face fallback",
        "tangent_policy": "reconstructed from final positions, normals and UV0",
        "triangulation_policy": (
            "source-order triangles; deterministic scored quad diagonals; stable projected "
            "ear clipping; validated fan recovery only when needed"
        ),
        "vertex_policy": "source-corner provenance; material split; <=65535 vertices per source mesh node",
        "skin_policy": "combine duplicate clusters, discard zero weights, keep top four, normalize",
        "uv_policy": "UV0; V flipped" if options.flip_v else "UV0 preserved",
        "engine_or_editor_tested": False,
        "ignored_identity_blendshapes": [
            row.ignored_identity_report()
            for row in (getattr(scene, "blend_shapes", ()) or ())
            if getattr(row, "classification", "") == BLENDSHAPE_IDENTITY_NOOP
            and hasattr(row, "ignored_identity_report")
        ],
        "model_geometry_findings": [
            dict(row) for row in (getattr(scene, "geometry_findings", ()) or ())
        ],
    }


def _build_authored_rig_contract(
    scene: FbxScene,
    options: ModelBuildOptions,
    source: SourceMsh,
    report: dict[str, Any],
    *,
    aliases_by_name: dict[str, Sequence[str]] | None = None,
) -> AuthoredRigContract:
    """Freeze and validate the exact hierarchy immediately before MSH output."""

    contract = AuthoredRigContract.from_source_msh(
        source,
        source_fbx_sha256=scene.sha256,
        source_model_name=scene.path.name,
        authored_msh_resource_name=options.resource_name,
        coordinate_contract=dict(report.get("coordinate_contract", {}) or {}),
        aliases_by_name=aliases_by_name,
    )
    report["authored_rig_contract"] = contract.to_dict()
    report["authored_rig_validation"] = contract.validate()
    report["authored_rig_contract_id"] = contract.contract_id
    report["authored_bind_hash"] = contract.bind_hash
    report["authored_skeleton_hash"] = contract.skeleton_hash
    report["authored_descriptor_hash"] = contract.descriptor_hash
    report["total_hierarchy_node_count"] = len(contract.nodes)
    report["animation_entity_prefix_length"] = contract.animation_entity_prefix_length
    from .model_validation import validate_model_bind_skin

    report["model_bind_cpu_skin_validation"] = validate_model_bind_skin(
        source, contract
    )
    return contract


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
