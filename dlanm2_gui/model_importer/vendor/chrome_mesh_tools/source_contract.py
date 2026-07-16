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


def audit_source_msh_bytes_for_compiler(
    payload: bytes,
    source_name: str = "<in-memory source MSH>",
) -> dict[str, Any]:
    parsed = MshFile.parse(bytes(payload), source_name)
    errors: list[str] = []
    warnings: list[str] = []
    node_type_counts: dict[str, int] = {}

    if len(parsed.nodes) > 32_768:
        errors.append(
            f"source MSH contains {len(parsed.nodes)} physical nodes; parent indexes are "
            "signed int16 and support at most 32768. This is independent of the 256-entry "
            "local skin-palette limit."
        )
    for issue in parsed.warnings:
        errors.append(
            f"source-MSH hierarchy/header validation failed: {issue}. Rebuild the source "
            "from the original FBX before compiling."
        )

    for index, node in enumerate(parsed.nodes):
        type_name = SOURCE_NODE_TYPE_NAMES.get(node.node_type, f"UNKNOWN_{node.node_type}")
        node_type_counts[type_name] = node_type_counts.get(type_name, 0) + 1
        if node.node_type in GEOMETRY_NODE_TYPES and not node.lods:
            is_explicit_bounds_carrier = (
                node.node_type == SOURCE_NODE_MESH
                and node.name.casefold().endswith("_bounds")
                and len(node.bounds) == 6
                and any(float(value) > 0.0 for value in node.bounds[3:6])
            )
            if is_explicit_bounds_carrier:
                warnings.append(
                    f"node {index} {node.name!r} is the explicit non-rendering model-bounds "
                    "carrier; it has no LOD by design and must remain after visible geometry"
                )
            else:
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
            location = f"node {index} {node.name!r} LOD {lod_index}"
            if lod.vertex_count > 65_535:
                errors.append(
                    f"{location} has {lod.vertex_count} vertices; split the geometry so each "
                    "emitted source-MSH node has at most 65535 vertices."
                )
            invalid_indices = [
                value for value in lod.indices if value < 0 or value >= lod.vertex_count
            ]
            if invalid_indices:
                errors.append(
                    f"{location} contains {len(invalid_indices)} vertex indexes outside "
                    f"0..{max(0, lod.vertex_count - 1)}; rebuild or repair the source mesh."
                )
            for issue in lod.validate():
                errors.append(f"{location}: {issue}")
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
                    if len(subset.bone_palette) > 256:
                        errors.append(
                            f"{location} subset {subset_index} has "
                            f"{len(subset.bone_palette)} palette entries; vertex bone bytes are "
                            "local indexes, so partition weighted triangles into palettes of at "
                            "most 256 entries. The total hierarchy may remain larger."
                        )
                    if len(set(subset.bone_palette)) != len(subset.bone_palette):
                        errors.append(
                            f"{location} subset {subset_index} contains duplicate global node "
                            "entries in its local palette; rebuild the partition."
                        )
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
                    palette_size = len(subset.bone_palette)
                    referenced_vertices = {
                        lod.indices[position]
                        for position in range(
                            subset.first_index,
                            min(
                                subset.first_index + subset.index_count,
                                len(lod.indices),
                            ),
                        )
                        if 0 <= lod.indices[position] < len(lod.skin_vertices)
                    }
                    for vertex_index in sorted(referenced_vertices):
                        row = lod.skin_vertices[vertex_index]
                        invalid_local = [
                            value
                            for value in row.bone_indices
                            if value < 0 or value >= palette_size
                        ]
                        if invalid_local:
                            errors.append(
                                f"{location} subset {subset_index} vertex {vertex_index} stores "
                                f"local bone index {invalid_local[0]}, outside palette size "
                                f"{palette_size}. A global node index must never be written into "
                                "the uint8 local-palette field. Rebuild the model source."
                            )

    if not parsed.surface_names:
        errors.append(
            "root physical-surface table is empty; every observed CE6 DevTools source "
            "mesh contains at least one 0x700 surface name"
        )

    return {
        "format": "chrome_mesh_tools_ce6_source_compile_audit_v2",
        "path": str(source_name),
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


def audit_source_msh_for_compiler(path: str | Path) -> dict[str, Any]:
    source = Path(path)
    return audit_source_msh_bytes_for_compiler(source.read_bytes(), str(source))


__all__ = ["audit_source_msh_bytes_for_compiler", "audit_source_msh_for_compiler"]
