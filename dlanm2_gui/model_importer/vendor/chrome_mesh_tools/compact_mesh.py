from __future__ import annotations

"""Inspect the compact runtime entity table embedded in compiled ``.msh_obj`` files.

The source MSH parser can prove that bones and weights were authored, but the
Dying Light editor's Bones tab reads the *compiled compact mesh*.  A source
geometry node incorrectly typed as ordinary ``MESH`` can compile without an
error while producing a one-entity runtime object with no bones.  This module
checks the actual compiler result.
"""

from dataclasses import dataclass
import math
from pathlib import Path
from typing import Any
import struct

from .math3d import (
    IDENTITY_MATRIX4X4,
    Matrix4x4,
    matrix3x4_from_matrix4,
    matrix4_from_matrix3x4,
    matrix4_multiply,
    matrix4_transform_point,
)
from .rp6l import MESH_PAYLOAD_TYPE, Rp6lError, load_rp6l_link_input

COMPACT_HEADER_ENTITY_TABLE_PTR = 0x08
COMPACT_HEADER_ENTITY_COUNT = 0x64
COMPACT_HEADER_ROOT_COUNT = 0x68
COMPACT_ENTITY_STRIDE = 0xD0
COMPACT_ENTITY_LOCAL_MATRIX = 0x00
COMPACT_ENTITY_REFERENCE_MATRIX = 0x30
COMPACT_ENTITY_BOUNDS = 0x60
COMPACT_ENTITY_NAME_PTR = 0x78
COMPACT_ENTITY_FLAGS = 0xC0
COMPACT_ENTITY_PARENT_INDEX = 0xC6
COMPACT_ENTITY_TYPE = 0xC8
COMPACT_ENTITY_CHILD_COUNT = 0xC9
COMPACT_ENTITY_LOD_COUNT = 0xCA

# ``CMeshFileBase::CalculateBoundingBox`` rejects entity extents whose full
# diagonal squared is below the float-epsilon threshold.  Keeping that rule in
# the inspector makes its aggregate match what ChromeEd can use for the model
# bounding box instead of treating position-only/zero-sized bones as geometry.
COMPACT_BOUND_DIAGONAL_SQUARED_EPSILON = 1.1920928955078125e-7

IDENTITY_MATRIX3X4 = matrix3x4_from_matrix4(IDENTITY_MATRIX4X4)

# Compact entities retain the source node-type bit values. The 0.3.0
# validator incorrectly treated these as a dense 1/2/3/4 enum, which
# mislabeled real type-8 bones as UNKNOWN_8.
RUNTIME_ENTITY_TYPE_NAMES = {
    1: "MESH",
    2: "MESH_SKINNED",
    4: "HELPER",
    8: "BONE",
    16: "HULL",
}
RUNTIME_ENTITY_MESH = 1
RUNTIME_ENTITY_BONE = 8
RUNTIME_ENTITY_MESH_SKINNED = 2


class CompactMeshError(ValueError):
    pass


@dataclass(frozen=True)
class CompactMeshEntity:
    index: int
    name: str
    flags: int
    bounds: tuple[float, float, float, float, float, float]
    parent_index: int
    element_type: int
    child_count: int
    lod_count: int
    local_matrix: tuple[float, ...] = IDENTITY_MATRIX3X4
    reference_matrix: tuple[float, ...] = IDENTITY_MATRIX3X4

    @property
    def element_type_name(self) -> str:
        return RUNTIME_ENTITY_TYPE_NAMES.get(
            self.element_type, f"UNKNOWN_{self.element_type}"
        )

    def to_dict(self, *, global_matrix: Matrix4x4 | None = None) -> dict[str, Any]:
        value: dict[str, Any] = {
            "index": self.index,
            "name": self.name,
            "flags": f"0x{self.flags:08X}",
            "local_matrix3x4": list(self.local_matrix),
            "reference_matrix3x4": list(self.reference_matrix),
            "bounds_center_half_extents": list(self.bounds),
            "parent_index": self.parent_index,
            "element_type": self.element_type,
            "element_type_name": self.element_type_name,
            "child_count": self.child_count,
            "lod_count": self.lod_count,
        }
        if global_matrix is not None:
            value["global_matrix3x4"] = list(matrix3x4_from_matrix4(global_matrix))
            value["global_translation_xyz"] = [
                global_matrix[0][3],
                global_matrix[1][3],
                global_matrix[2][3],
            ]
            identity_candidate = matrix4_multiply(
                global_matrix, matrix4_from_matrix3x4(self.reference_matrix)
            )
            value["global_reference_identity_max_abs_error"] = max(
                abs(identity_candidate[row][column] - IDENTITY_MATRIX4X4[row][column])
                for row in range(4)
                for column in range(4)
            )
        return value


@dataclass(frozen=True)
class CompactMesh:
    entity_count: int
    root_count: int
    entity_table_offset: int
    entities: tuple[CompactMeshEntity, ...]

    @property
    def type_counts(self) -> dict[str, int]:
        result: dict[str, int] = {}
        for entity in self.entities:
            key = entity.element_type_name
            result[key] = result.get(key, 0) + 1
        return result

    @property
    def bone_names(self) -> tuple[str, ...]:
        return tuple(row.name for row in self.entities if row.element_type == RUNTIME_ENTITY_BONE)

    @property
    def skinned_mesh_names(self) -> tuple[str, ...]:
        return tuple(row.name for row in self.entities if row.element_type == RUNTIME_ENTITY_MESH_SKINNED)

    @property
    def animation_entity_count_candidate(self) -> int:
        """Mirror ``CMeshFileBase::InitNumAnimEntities`` at a structural level.

        Chrome 6 counts entities preceding the first unattached/root skinned
        mesh as animation entities.  The precise attachment predicate is a
        virtual call; parent ``-1`` is the observable source/runtime contract
        for the generated mannequin and is sufficient for preflight.
        """
        for entity in self.entities:
            if entity.element_type == RUNTIME_ENTITY_MESH_SKINNED and entity.parent_index < 0:
                return entity.index or self.entity_count
        return self.entity_count

    def reconstruct_global_matrices(self) -> tuple[Matrix4x4, ...]:
        """Reconstruct compact global bind transforms from local matrices.

        Compact entities are normally serialized parent-first, but resolving
        recursively also handles a valid table whose parent appears later.  A
        malformed parent reference is reported explicitly rather than silently
        producing misleading global bounds.
        """

        entity_count = len(self.entities)
        resolved: list[Matrix4x4 | None] = [None] * entity_count
        visiting: set[int] = set()

        def resolve(index: int) -> Matrix4x4:
            matrix = resolved[index]
            if matrix is not None:
                return matrix
            if index in visiting:
                raise CompactMeshError(
                    f"compact entity hierarchy contains a cycle at index {index}"
                )
            visiting.add(index)
            entity = self.entities[index]
            local = matrix4_from_matrix3x4(entity.local_matrix)
            if entity.parent_index < 0:
                matrix = local
            elif entity.parent_index >= entity_count:
                raise CompactMeshError(
                    f"compact entity {index} parent {entity.parent_index} is outside "
                    f"the {entity_count}-entity table"
                )
            else:
                matrix = matrix4_multiply(resolve(entity.parent_index), local)
            visiting.remove(index)
            resolved[index] = matrix
            return matrix

        return tuple(resolve(index) for index in range(entity_count))

    def aggregate_bone_bounds(
        self,
        global_matrices: tuple[Matrix4x4, ...] | None = None,
    ) -> dict[str, Any]:
        """Aggregate usable BONE AABBs in compact-mesh global bind space."""

        matrices = global_matrices or self.reconstruct_global_matrices()
        minimum = [math.inf, math.inf, math.inf]
        maximum = [-math.inf, -math.inf, -math.inf]
        bone_count = 0
        contributing_count = 0
        collapsed_count = 0
        invalid_count = 0

        for entity, global_matrix in zip(self.entities, matrices):
            if entity.element_type != RUNTIME_ENTITY_BONE:
                continue
            bone_count += 1
            center = entity.bounds[:3]
            half = entity.bounds[3:]
            if not all(math.isfinite(value) for value in (*center, *half)):
                invalid_count += 1
                continue
            half = tuple(abs(value) for value in half)
            diagonal_squared = 4.0 * sum(value * value for value in half)
            if diagonal_squared < COMPACT_BOUND_DIAGONAL_SQUARED_EPSILON:
                collapsed_count += 1
                continue
            contributing_count += 1
            for x_sign in (-1.0, 1.0):
                for y_sign in (-1.0, 1.0):
                    for z_sign in (-1.0, 1.0):
                        point = matrix4_transform_point(
                            global_matrix,
                            (
                                center[0] + x_sign * half[0],
                                center[1] + y_sign * half[1],
                                center[2] + z_sign * half[2],
                            ),
                        )
                        for axis in range(3):
                            minimum[axis] = min(minimum[axis], point[axis])
                            maximum[axis] = max(maximum[axis], point[axis])

        result: dict[str, Any] = {
            "coordinate_space": "compact_mesh_global_bind",
            "bone_count": bone_count,
            "contributing_bone_count": contributing_count,
            "collapsed_bone_count": collapsed_count,
            "invalid_bone_count": invalid_count,
            "minimum_diagonal_squared": COMPACT_BOUND_DIAGONAL_SQUARED_EPSILON,
            "minimum_xyz": None,
            "maximum_xyz": None,
            "center_xyz": None,
            "half_extents_xyz": None,
            "diagonal_length": 0.0,
        }
        if contributing_count:
            center = [(minimum[axis] + maximum[axis]) * 0.5 for axis in range(3)]
            half = [(maximum[axis] - minimum[axis]) * 0.5 for axis in range(3)]
            result.update(
                {
                    "minimum_xyz": minimum,
                    "maximum_xyz": maximum,
                    "center_xyz": center,
                    "half_extents_xyz": half,
                    "diagonal_length": math.sqrt(
                        sum((maximum[axis] - minimum[axis]) ** 2 for axis in range(3))
                    ),
                }
            )
        return result

    def aggregate_reference_bounds(
        self,
        global_matrices: tuple[Matrix4x4, ...] | None = None,
    ) -> dict[str, Any]:
        """Mirror ``CMeshFileBase::CalculateBoundingBox`` reference extents.

        Chrome 6 deliberately excludes element types 2 and 3 from this pass,
        so a skinned model cannot obtain its editor/model box from the
        MESH_SKINNED entity itself.  Generated imports append an empty ordinary
        MESH carrying the exact emitted-vertex AABB after the skinned meshes.
        """

        matrices = global_matrices or self.reconstruct_global_matrices()
        minimum = [math.inf, math.inf, math.inf]
        maximum = [-math.inf, -math.inf, -math.inf]
        contributors: list[str] = []
        for entity, global_matrix in zip(self.entities, matrices):
            if entity.element_type in {2, 3}:
                continue
            center = entity.bounds[:3]
            half = tuple(abs(value) for value in entity.bounds[3:])
            if not all(math.isfinite(value) for value in (*center, *half)):
                continue
            diagonal_squared = 4.0 * sum(value * value for value in half)
            if diagonal_squared < COMPACT_BOUND_DIAGONAL_SQUARED_EPSILON:
                continue
            contributors.append(entity.name)
            for x_sign in (-1.0, 1.0):
                for y_sign in (-1.0, 1.0):
                    for z_sign in (-1.0, 1.0):
                        point = matrix4_transform_point(
                            global_matrix,
                            (
                                center[0] + x_sign * half[0],
                                center[1] + y_sign * half[1],
                                center[2] + z_sign * half[2],
                            ),
                        )
                        for axis in range(3):
                            minimum[axis] = min(minimum[axis], point[axis])
                            maximum[axis] = max(maximum[axis], point[axis])
        result: dict[str, Any] = {
            "coordinate_space": "compact_mesh_global_bind",
            "contributing_entity_count": len(contributors),
            "contributing_entity_names": contributors,
            "minimum_xyz": None,
            "maximum_xyz": None,
            "center_xyz": None,
            "half_extents_xyz": None,
            "diagonal_length": 0.0,
        }
        if contributors:
            center = [(minimum[axis] + maximum[axis]) * 0.5 for axis in range(3)]
            half = [(maximum[axis] - minimum[axis]) * 0.5 for axis in range(3)]
            result.update(
                {
                    "minimum_xyz": minimum,
                    "maximum_xyz": maximum,
                    "center_xyz": center,
                    "half_extents_xyz": half,
                    "diagonal_length": math.sqrt(
                        sum((maximum[axis] - minimum[axis]) ** 2 for axis in range(3))
                    ),
                }
            )
        return result

    def to_dict(self, *, include_entities: bool = True) -> dict[str, Any]:
        value: dict[str, Any] = {
            "format": "chrome_mesh_tools_compact_mesh_entity_audit_v1",
            "entity_count": self.entity_count,
            "root_count": self.root_count,
            "entity_table_offset": self.entity_table_offset,
            "entity_stride": COMPACT_ENTITY_STRIDE,
            "type_counts": self.type_counts,
            "bone_count": len(self.bone_names),
            "bone_names": list(self.bone_names),
            "skinned_mesh_count": len(self.skinned_mesh_names),
            "skinned_mesh_names": list(self.skinned_mesh_names),
            "animation_entity_count_candidate": self.animation_entity_count_candidate,
        }
        global_matrices: tuple[Matrix4x4, ...] | None = None
        try:
            global_matrices = self.reconstruct_global_matrices()
            value["bone_bounds_global_aggregate"] = self.aggregate_bone_bounds(
                global_matrices
            )
            value["reference_bounds_global_aggregate"] = self.aggregate_reference_bounds(
                global_matrices
            )
        except (CompactMeshError, ValueError) as error:
            # Preserve the rest of the compact audit for damaged inputs while
            # making the transform failure explicit.
            value["global_transform_error"] = str(error)
        if include_entities:
            value["entities"] = [
                row.to_dict(
                    global_matrix=(global_matrices[index] if global_matrices else None)
                )
                for index, row in enumerate(self.entities)
            ]
        return value


def _decode_fixup_pointer(value: int, payload_size: int, label: str) -> int:
    if value == 0:
        raise CompactMeshError(f"{label} pointer is null")
    offset = value - 1
    if not 0 <= offset < payload_size:
        raise CompactMeshError(
            f"{label} pointer 0x{value:X} decodes outside payload size 0x{payload_size:X}"
        )
    return offset


def _read_c_string(payload: bytes, offset: int, label: str) -> str:
    end = payload.find(b"\0", offset, min(len(payload), offset + 1024))
    if end < 0:
        raise CompactMeshError(f"{label} at 0x{offset:X} is not NUL terminated")
    return payload[offset:end].decode("utf-8", errors="replace")


def parse_compact_mesh_payload(payload: bytes) -> CompactMesh:
    if len(payload) < 0xB0:
        raise CompactMeshError(
            f"compact mesh payload is too small: 0x{len(payload):X} bytes"
        )
    entity_count = struct.unpack_from("<I", payload, COMPACT_HEADER_ENTITY_COUNT)[0]
    root_count = struct.unpack_from("<I", payload, COMPACT_HEADER_ROOT_COUNT)[0]
    table_value = struct.unpack_from(
        "<Q", payload, COMPACT_HEADER_ENTITY_TABLE_PTR
    )[0]
    table_offset = _decode_fixup_pointer(
        table_value, len(payload), "entity table"
    )
    if entity_count > 1_000_000:
        raise CompactMeshError(f"implausible entity count {entity_count}")
    table_end = table_offset + entity_count * COMPACT_ENTITY_STRIDE
    if table_end > len(payload):
        raise CompactMeshError(
            f"entity table ends at 0x{table_end:X}, beyond payload 0x{len(payload):X}"
        )

    entities: list[CompactMeshEntity] = []
    for index in range(entity_count):
        base = table_offset + index * COMPACT_ENTITY_STRIDE
        name_value = struct.unpack_from("<Q", payload, base + COMPACT_ENTITY_NAME_PTR)[0]
        name_offset = _decode_fixup_pointer(
            name_value, len(payload), f"entity {index} name"
        )
        name = _read_c_string(payload, name_offset, f"entity {index} name")
        entities.append(
            CompactMeshEntity(
                index=index,
                name=name,
                flags=struct.unpack_from(
                    "<I", payload, base + COMPACT_ENTITY_FLAGS
                )[0],
                bounds=struct.unpack_from(
                    "<6f", payload, base + COMPACT_ENTITY_BOUNDS
                ),
                parent_index=struct.unpack_from(
                    "<h", payload, base + COMPACT_ENTITY_PARENT_INDEX
                )[0],
                element_type=payload[base + COMPACT_ENTITY_TYPE],
                child_count=payload[base + COMPACT_ENTITY_CHILD_COUNT],
                lod_count=payload[base + COMPACT_ENTITY_LOD_COUNT],
                local_matrix=struct.unpack_from(
                    "<12f", payload, base + COMPACT_ENTITY_LOCAL_MATRIX
                ),
                reference_matrix=struct.unpack_from(
                    "<12f", payload, base + COMPACT_ENTITY_REFERENCE_MATRIX
                ),
            )
        )

    observed_roots = sum(row.parent_index < 0 for row in entities)
    if root_count > entity_count:
        raise CompactMeshError(
            f"root count {root_count} exceeds entity count {entity_count}"
        )
    # Do not reject when the engine's root table omits attached/deform roots;
    # retain both values in the report instead.
    result = CompactMesh(entity_count, root_count, table_offset, tuple(entities))
    return result


def inspect_msh_obj(path: str | Path) -> dict[str, Any]:
    source = Path(path)
    pack, input_mode = load_rp6l_link_input(source)
    mesh_resources = [
        resource for resource in pack.resources
        if resource.resource_type == MESH_PAYLOAD_TYPE
    ]
    if not mesh_resources:
        raise CompactMeshError(f"{source} contains no runtime _MESH_ resource")

    rows: list[dict[str, Any]] = []
    for resource in mesh_resources:
        if resource.item_count < 1:
            raise CompactMeshError("runtime _MESH_ resource has no compact-mesh item")
        item = pack.items[resource.first_item_index]
        if not 0 <= item.chunk_index < len(pack.chunks):
            raise CompactMeshError("compact-mesh item references invalid chunk")
        chunk = pack.chunks[item.chunk_index]
        if item.size_or_hash < 0:
            raise CompactMeshError("compact-mesh item has non-size hash field")
        end = item.offset + item.size_or_hash
        if item.offset < 0 or end > len(chunk.data):
            raise CompactMeshError("compact-mesh item exceeds chunk payload")
        compact = parse_compact_mesh_payload(chunk.data[item.offset:end])
        row = compact.to_dict(include_entities=True)
        row["resource_name"] = pack.resource_name(resource)
        rows.append(row)

    return {
        "format": "chrome_mesh_tools_msh_obj_compact_audit_v1",
        "path": str(source),
        "input_mode": input_mode,
        "mesh_resource_count": len(rows),
        "mesh_resources": rows,
    }


def validate_rigged_mannequin_msh_obj(
    path: str | Path,
    *,
    expected_bones: int = 106,
) -> dict[str, Any]:
    report = inspect_msh_obj(path)
    errors: list[str] = []
    for row in report["mesh_resources"]:
        type_counts = row["type_counts"]
        bone_count = int(type_counts.get("BONE", 0))
        skinned_count = int(type_counts.get("MESH_SKINNED", 0))
        names = {str(name).casefold() for name in row["bone_names"]}
        if bone_count != expected_bones:
            errors.append(
                f"{row['resource_name']}: compact mesh contains {bone_count} BONE "
                f"entities; expected {expected_bones}"
            )
        if skinned_count < 1:
            errors.append(
                f"{row['resource_name']}: compact mesh contains no MESH_SKINNED "
                "entity; ChromeEd will show an ordinary Mesh and the Bones tab "
                "will be empty"
            )
        for required in ("bip01", "pelvis"):
            if required not in names:
                errors.append(
                    f"{row['resource_name']}: required runtime bone {required!r} is absent"
                )
        if int(row["animation_entity_count_candidate"]) < expected_bones:
            errors.append(
                f"{row['resource_name']}: animation entity prefix is only "
                f"{row['animation_entity_count_candidate']}; expected at least "
                f"{expected_bones} entities before the root skinned mesh"
            )
    report["expected_bone_count"] = expected_bones
    report["errors"] = errors
    report["error_count"] = len(errors)
    report["ready"] = not errors
    return report
