from __future__ import annotations

"""Compiler-facing Chrome 6 source-MSH invariants.

The generic parser intentionally accepts more than the stock DevTools exporter
emits.  This module is the stricter preflight used before calling the official
mesh frontend.
"""

from pathlib import Path
from typing import Any
from .msh import MshFile

SOURCE_NODE_MESH = 1
SOURCE_NODE_MESH_VBLEND = 2
SOURCE_NODE_HELPER = 4
SOURCE_NODE_BONE = 8
SOURCE_NODE_HULL = 16
SOURCE_NODE_LIGHT = 32
SOURCE_NODE_CAMERA = 64

SOURCE_NODE_TYPE_NAMES = {
    SOURCE_NODE_MESH: "MESH",
    SOURCE_NODE_MESH_VBLEND: "MESH_VBLEND",
    SOURCE_NODE_HELPER: "HELPER",
    SOURCE_NODE_BONE: "BONE",
    SOURCE_NODE_HULL: "HULL",
    SOURCE_NODE_LIGHT: "LIGHT",
    SOURCE_NODE_CAMERA: "CAMERA",
}
GEOMETRY_NODE_TYPES = {SOURCE_NODE_MESH, SOURCE_NODE_MESH_VBLEND, SOURCE_NODE_HULL}
TRANSFORM_ONLY_NODE_TYPES = {SOURCE_NODE_HELPER, SOURCE_NODE_BONE, SOURCE_NODE_LIGHT, SOURCE_NODE_CAMERA}


def audit_source_msh_for_compiler(path: str | Path) -> dict[str, Any]:
    source = Path(path)
    parsed = MshFile.from_path(source)
    errors: list[str] = []
    warnings: list[str] = []
    node_type_counts: dict[str, int] = {}

    for index, node in enumerate(parsed.nodes):
        type_name = SOURCE_NODE_TYPE_NAMES.get(node.node_type, f"UNKNOWN_{node.node_type}")
        node_type_counts[type_name] = node_type_counts.get(type_name, 0) + 1
        if node.node_type in GEOMETRY_NODE_TYPES and not node.lods:
            errors.append(
                f"node {index} {node.name!r} is {type_name} but has no LOD; "
                "the compiler treats geometry types as mesh elements"
            )
        if node.node_type in TRANSFORM_ONLY_NODE_TYPES and node.lods:
            errors.append(
                f"node {index} {node.name!r} is {type_name} but contains geometry LODs"
            )
        if node.node_type not in SOURCE_NODE_TYPE_NAMES:
            warnings.append(
                f"node {index} {node.name!r} uses unclassified type {node.node_type}"
            )

        for lod_index, lod in enumerate(node.lods):
            color_bytes = lod.streams.get(0x110, b"")
            color_count = len(color_bytes) // 4 if len(color_bytes) % 4 == 0 else -1
            if node.node_type == SOURCE_NODE_MESH_VBLEND and not lod.skin_vertices:
                errors.append(
                    f"node {index} {node.name!r} LOD {lod_index} is MESH_VBLEND "
                    "but has no skin stream"
                )
            if color_count != lod.vertex_count:
                errors.append(
                    f"node {index} LOD {lod_index} has {color_count} colors for "
                    f"{lod.vertex_count} vertices; all observed CE6 DevTools source meshes "
                    "carry a complete 0x110 vertex-color stream"
                )
            if lod.skin_vertices:
                if node.node_type != SOURCE_NODE_MESH_VBLEND:
                    errors.append(
                        f"node {index} {node.name!r} LOD {lod_index} contains skin "
                        f"weights but is {type_name}; Chrome 6 requires skinned "
                        "geometry to use MESH_VBLEND (2), otherwise the compiler "
                        "emits an ordinary Mesh and strips the bone hierarchy from "
                        "the compact runtime object"
                    )
                for subset_index, subset in enumerate(lod.subsets):
                    for palette_index in subset.bone_palette:
                        if not 0 <= palette_index < len(parsed.nodes):
                            errors.append(
                                f"node {index} LOD {lod_index} subset {subset_index} "
                                f"palette index {palette_index} is outside the node table"
                            )
                            continue
                        target = parsed.nodes[palette_index]
                        if target.node_type != SOURCE_NODE_BONE:
                            errors.append(
                                f"node {index} LOD {lod_index} subset {subset_index} "
                                f"palette entry {palette_index} targets {target.name!r} type "
                                f"{SOURCE_NODE_TYPE_NAMES.get(target.node_type, target.node_type)}; "
                                "skinning palettes must target BONE (8) nodes"
                            )

    if not parsed.surface_names:
        errors.append(
            "root physical-surface table is empty; every observed CE6 DevTools source "
            "mesh contains at least one 0x700 surface name"
        )

    return {
        "format": "chrome_mesh_tools_ce6_source_compile_audit_v2",
        "path": str(source),
        "node_count": len(parsed.nodes),
        "node_type_counts": node_type_counts,
        "material_count": len(parsed.materials),
        "surface_count": len(parsed.surface_names),
        "surface_names": list(parsed.surface_names),
        "has_skinning": parsed.has_skinning,
        "error_count": len(errors),
        "warning_count": len(warnings),
        "errors": errors,
        "warnings": warnings,
        "ready": not errors,
    }
