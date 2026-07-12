from __future__ import annotations

"""Inspect the compact runtime entity table embedded in compiled ``.msh_obj`` files.

The source MSH parser can prove that bones and weights were authored, but the
Dying Light editor's Bones tab reads the *compiled compact mesh*.  A source
geometry node incorrectly typed as ordinary ``MESH`` can compile without an
error while producing a one-entity runtime object with no bones.  This module
checks the actual compiler result.
"""

from dataclasses import dataclass
from pathlib import Path
from typing import Any
import struct

from .rp6l import MESH_PAYLOAD_TYPE, Rp6lError, load_rp6l_link_input

COMPACT_HEADER_ENTITY_TABLE_PTR = 0x08
COMPACT_HEADER_ENTITY_COUNT = 0x64
COMPACT_HEADER_ROOT_COUNT = 0x68
COMPACT_ENTITY_STRIDE = 0xD0
COMPACT_ENTITY_NAME_PTR = 0x78
COMPACT_ENTITY_FLAGS = 0xC0
COMPACT_ENTITY_PARENT_INDEX = 0xC6
COMPACT_ENTITY_TYPE = 0xC8
COMPACT_ENTITY_CHILD_COUNT = 0xC9
COMPACT_ENTITY_LOD_COUNT = 0xCA

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
RUNTIME_ENTITY_BONE = 8
RUNTIME_ENTITY_MESH_SKINNED = 2


class CompactMeshError(ValueError):
    pass


@dataclass(frozen=True)
class CompactMeshEntity:
    index: int
    name: str
    flags: int
    parent_index: int
    element_type: int
    child_count: int
    lod_count: int

    @property
    def element_type_name(self) -> str:
        return RUNTIME_ENTITY_TYPE_NAMES.get(
            self.element_type, f"UNKNOWN_{self.element_type}"
        )

    def to_dict(self) -> dict[str, Any]:
        return {
            "index": self.index,
            "name": self.name,
            "flags": f"0x{self.flags:08X}",
            "parent_index": self.parent_index,
            "element_type": self.element_type,
            "element_type_name": self.element_type_name,
            "child_count": self.child_count,
            "lod_count": self.lod_count,
        }


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
        if include_entities:
            value["entities"] = [row.to_dict() for row in self.entities]
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
                parent_index=struct.unpack_from(
                    "<h", payload, base + COMPACT_ENTITY_PARENT_INDEX
                )[0],
                element_type=payload[base + COMPACT_ENTITY_TYPE],
                child_count=payload[base + COMPACT_ENTITY_CHILD_COUNT],
                lod_count=payload[base + COMPACT_ENTITY_LOD_COUNT],
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
