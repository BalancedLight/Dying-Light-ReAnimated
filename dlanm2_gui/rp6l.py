from __future__ import annotations

from dataclasses import dataclass
from pathlib import Path
import struct
from typing import Mapping, Sequence


RP6L_MAGIC = b"RP6L"
RP6L_VERSION = 1
BUILDER_INFORMATION_TYPE = -32257
ANIMATION_PAYLOAD_TYPE = 320
ANIMATION_SCR_PAYLOAD_TYPE = 322


@dataclass(frozen=True)
class Rp6lChunk:
    flags: int
    unknown0: int
    data: bytes
    packed_size: int = 0
    unknown1: int = 1
    unknown2: int = 2


@dataclass(frozen=True)
class Rp6lItem:
    chunk_index: int
    flags: int
    unknown0: int
    offset: int
    size_or_hash: int
    unknown1: int = 0


@dataclass(frozen=True)
class Rp6lResource:
    item_count: int
    resource_type: int
    name_index: int
    first_item_index: int



@dataclass(frozen=True)
class ParsedRp6l:
    version: int
    chunks: tuple[Rp6lChunk, ...]
    items: tuple[Rp6lItem, ...]
    resources: tuple[Rp6lResource, ...]
    names: tuple[str, ...]

    def resource_name(self, resource: Rp6lResource) -> str:
        return self.names[resource.name_index]


@dataclass(frozen=True)
class AnimationLibrary:
    animations: dict[str, bytes]
    animation_scripts: dict[str, tuple[bytes, bytes]]


def build_common_anims_probe_rpack(
    *,
    animation_resource_name: str,
    anm2_data: bytes,
    animation_script_resource_name: str,
    animation_script_sections: tuple[bytes, bytes],
) -> bytes:
    """Build a minimal uncompressed `common_anims_PC.rpack` probe.

    This is a deliberately small shadow pack. It is for proving `_ANIMATION_`
    plus `_ANIMATION_SCR_` visibility behavior, not for merging stock content.
    """

    return build_common_anims_multi_probe_rpack(
        animation_resources=[(animation_resource_name, anm2_data)],
        animation_script_resource_name=animation_script_resource_name,
        animation_script_sections=animation_script_sections,
    )


def build_common_anims_multi_probe_rpack(
    *,
    animation_resources: list[tuple[str, bytes]],
    animation_script_resource_name: str,
    animation_script_sections: tuple[bytes, bytes],
) -> bytes:
    """Build a minimal uncompressed RPack with one animation script."""

    return build_animation_library_rpack(
        animation_resources=animation_resources,
        animation_scripts={animation_script_resource_name: animation_script_sections},
    )


def build_animation_library_rpack(
    *,
    animation_resources: Mapping[str, bytes] | Sequence[tuple[str, bytes]],
    animation_scripts: Mapping[str, tuple[bytes, bytes]],
) -> bytes:
    """Build a tool-owned uncompressed RP6L animation library.

    Unlike the historical probe writer, this form can expose multiple
    `_ANIMATION_SCR_` resources in one pack.  It is the format used by the
    project GUI for create-new and append workflows.
    """

    animation_rows = (
        list(animation_resources.items())
        if isinstance(animation_resources, Mapping)
        else list(animation_resources)
    )
    if not animation_rows:
        raise ValueError("at least one animation resource is required")
    if not animation_scripts:
        raise ValueError("at least one animation-script resource is required")
    animation_names = [name for name, _data in animation_rows]
    script_names = list(animation_scripts)
    if len(set(animation_names)) != len(animation_names):
        raise ValueError("animation resource names must be unique")
    if len(set(script_names)) != len(script_names):
        raise ValueError("animation-script resource names must be unique")
    reserved = {"_ANIMATION_", "_ANIMATION_SCR_"}
    if reserved.intersection(animation_names) or reserved.intersection(script_names):
        raise ValueError("resource names collide with RP6L builder names")
    if set(animation_names).intersection(script_names):
        raise ValueError("animation and animation-script resource names must be distinct")

    names = ["_ANIMATION_", "_ANIMATION_SCR_", *animation_names, *script_names]
    name_indices = {name: index for index, name in enumerate(names)}
    animation_builder = _pad16(
        "".join(f"+{name}\n" for name in animation_names).encode("ascii")
    )
    script_builder = _pad16(
        "".join(f"+{name}\n" for name in script_names).encode("ascii")
    )
    builder_blob = animation_builder + script_builder

    chunks: list[Rp6lChunk] = [Rp6lChunk(64, 2, data) for _name, data in animation_rows]
    script_chunk_indexes: dict[str, tuple[int, int]] = {}
    for script_name in script_names:
        section0, section1 = animation_scripts[script_name]
        first = len(chunks)
        chunks.extend([Rp6lChunk(66, 2, section0), Rp6lChunk(67, 2, section1)])
        script_chunk_indexes[script_name] = (first, first + 1)
    builder_chunk_index = len(chunks)
    chunks.append(Rp6lChunk(255, 4, builder_blob, unknown2=1))

    items: list[Rp6lItem] = [
        Rp6lItem(
            builder_chunk_index,
            0,
            name_indices["_ANIMATION_"],
            0,
            len(animation_builder.rstrip(b"\0")),
        ),
        Rp6lItem(
            builder_chunk_index,
            0,
            name_indices["_ANIMATION_SCR_"],
            len(animation_builder),
            len(script_builder.rstrip(b"\0")),
        ),
    ]
    animation_item_index: dict[str, int] = {}
    for chunk_index, (name, data) in enumerate(animation_rows):
        animation_item_index[name] = len(items)
        items.append(Rp6lItem(chunk_index, 0, name_indices[name], 0, len(data)))
    script_item_indexes: dict[str, int] = {}
    for script_name in script_names:
        section0, section1 = animation_scripts[script_name]
        first_chunk, second_chunk = script_chunk_indexes[script_name]
        script_item_indexes[script_name] = len(items)
        items.extend(
            [
                Rp6lItem(first_chunk, 0, name_indices[script_name], 0, len(section0)),
                Rp6lItem(second_chunk, 0, name_indices[script_name], 0, len(section1)),
            ]
        )

    resources: list[Rp6lResource] = [
        Rp6lResource(1, BUILDER_INFORMATION_TYPE, name_indices["_ANIMATION_"], 0),
        Rp6lResource(1, BUILDER_INFORMATION_TYPE, name_indices["_ANIMATION_SCR_"], 1),
    ]
    resources.extend(
        Rp6lResource(1, ANIMATION_PAYLOAD_TYPE, name_indices[name], animation_item_index[name])
        for name in animation_names
    )
    resources.extend(
        Rp6lResource(2, ANIMATION_SCR_PAYLOAD_TYPE, name_indices[name], script_item_indexes[name])
        for name in script_names
    )
    return build_rp6l(chunks, items, resources, names)


def write_common_anims_probe_rpack(
    path: Path,
    *,
    animation_resource_name: str,
    anm2_data: bytes,
    animation_script_resource_name: str,
    animation_script_sections: tuple[bytes, bytes],
) -> Path:
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_bytes(
        build_common_anims_probe_rpack(
            animation_resource_name=animation_resource_name,
            anm2_data=anm2_data,
            animation_script_resource_name=animation_script_resource_name,
            animation_script_sections=animation_script_sections,
        )
    )
    return path


def build_rp6l(
    chunks: list[Rp6lChunk],
    items: list[Rp6lItem],
    resources: list[Rp6lResource],
    names: list[str],
) -> bytes:
    if len(chunks) > 255:
        raise ValueError("minimal writer only supports chunk indexes that fit in one byte")

    name_offsets: list[int] = []
    names_blob = bytearray()
    for name in names:
        name_offsets.append(len(names_blob))
        names_blob.extend(name.encode("utf-8") + b"\0")

    table_length = (
        36
        + 20 * len(chunks)
        + 16 * len(items)
        + 12 * len(resources)
        + 4 * len(name_offsets)
        + len(names_blob)
    )
    chunk_offsets: list[int] = []
    cursor = table_length
    for chunk in chunks:
        chunk_offsets.append(cursor)
        cursor += len(chunk.data)

    out = bytearray()
    out.extend(RP6L_MAGIC)
    out.extend(
        struct.pack(
            "<iiiiiiii",
            RP6L_VERSION,
            0,
            len(items),
            len(chunks),
            len(resources),
            len(names_blob),
            len(name_offsets),
            1,
        )
    )
    for chunk, offset in zip(chunks, chunk_offsets):
        out.extend(
            struct.pack(
                "<HHIIiHH",
                chunk.flags,
                chunk.unknown0,
                offset,
                len(chunk.data),
                chunk.packed_size,
                chunk.unknown1,
                chunk.unknown2,
            )
        )
    for item in items:
        out.extend(
            struct.pack(
                "<BBhIii",
                item.chunk_index,
                item.flags,
                item.unknown0,
                item.offset,
                item.size_or_hash,
                item.unknown1,
            )
        )
    for resource in resources:
        out.extend(
            struct.pack(
                "<hhii",
                resource.item_count,
                resource.resource_type,
                resource.name_index,
                resource.first_item_index,
            )
        )
    for offset in name_offsets:
        out.extend(struct.pack("<i", offset))
    out.extend(names_blob)
    for chunk in chunks:
        out.extend(chunk.data)
    return bytes(out)



def parse_rp6l(data: bytes) -> ParsedRp6l:
    """Parse the uncompressed RP6L table used by this tool.

    The parser validates every table and chunk boundary.  It intentionally
    rejects compressed chunks because append mode is limited to packs produced
    by DL ReAnimated.
    """

    if len(data) < 36 or data[:4] != RP6L_MAGIC:
        raise ValueError("not an RP6L file")
    (version, _unknown0, item_count, chunk_count, resource_count, names_size,
     name_count, _unknown1) = struct.unpack_from("<iiiiiiii", data, 4)
    if version != RP6L_VERSION:
        raise ValueError(f"unsupported RP6L version: {version}")
    counts = (item_count, chunk_count, resource_count, names_size, name_count)
    if any(value < 0 for value in counts):
        raise ValueError("RP6L contains a negative table count")
    cursor = 36
    chunk_meta = []
    for _index in range(chunk_count):
        if cursor + 20 > len(data):
            raise ValueError("RP6L chunk table is truncated")
        flags, unknown0, offset, size, packed_size, unknown1, unknown2 = struct.unpack_from(
            "<HHIIiHH", data, cursor
        )
        cursor += 20
        if packed_size not in (0, size):
            raise ValueError("compressed RP6L chunks are not supported by append mode")
        if offset < 0 or size < 0 or offset + size > len(data):
            raise ValueError("RP6L chunk points outside the file")
        chunk_meta.append((flags, unknown0, offset, size, packed_size, unknown1, unknown2))
    items: list[Rp6lItem] = []
    for _index in range(item_count):
        if cursor + 16 > len(data):
            raise ValueError("RP6L item table is truncated")
        row = Rp6lItem(*struct.unpack_from("<BBhIii", data, cursor))
        cursor += 16
        items.append(row)
    resources: list[Rp6lResource] = []
    for _index in range(resource_count):
        if cursor + 12 > len(data):
            raise ValueError("RP6L resource table is truncated")
        row = Rp6lResource(*struct.unpack_from("<hhii", data, cursor))
        cursor += 12
        resources.append(row)
    name_offsets: list[int] = []
    for _index in range(name_count):
        if cursor + 4 > len(data):
            raise ValueError("RP6L name-offset table is truncated")
        name_offsets.append(struct.unpack_from("<i", data, cursor)[0])
        cursor += 4
    if cursor + names_size > len(data):
        raise ValueError("RP6L name table is truncated")
    names_blob = data[cursor : cursor + names_size]
    names: list[str] = []
    for offset in name_offsets:
        if offset < 0 or offset >= len(names_blob):
            raise ValueError("RP6L name offset is outside the name table")
        end = names_blob.find(b"\0", offset)
        if end < 0:
            raise ValueError("RP6L resource name is unterminated")
        names.append(names_blob[offset:end].decode("utf-8"))
    chunks = tuple(
        Rp6lChunk(flags, unknown0, data[offset : offset + size], packed_size, unknown1, unknown2)
        for flags, unknown0, offset, size, packed_size, unknown1, unknown2 in chunk_meta
    )
    parsed = ParsedRp6l(version, chunks, tuple(items), tuple(resources), tuple(names))
    _validate_parsed_rp6l(parsed)
    return parsed


def extract_animation_library(data: bytes) -> AnimationLibrary:
    parsed = parse_rp6l(data)
    animations: dict[str, bytes] = {}
    scripts: dict[str, tuple[bytes, bytes]] = {}
    for resource in parsed.resources:
        name = parsed.resource_name(resource)
        resource_items = parsed.items[
            resource.first_item_index : resource.first_item_index + resource.item_count
        ]
        if resource.resource_type == ANIMATION_PAYLOAD_TYPE:
            if len(resource_items) != 1:
                raise ValueError(f"animation resource {name!r} does not have one item")
            animations[name] = _item_payload(parsed, resource_items[0])
        elif resource.resource_type == ANIMATION_SCR_PAYLOAD_TYPE:
            if len(resource_items) != 2:
                raise ValueError(f"animation-script resource {name!r} does not have two items")
            scripts[name] = (
                _item_payload(parsed, resource_items[0]),
                _item_payload(parsed, resource_items[1]),
            )
        elif resource.resource_type != BUILDER_INFORMATION_TYPE:
            raise ValueError(
                f"RPack contains unsupported resource type {resource.resource_type} ({name})"
            )
    if not animations or not scripts:
        raise ValueError("RPack is not a DL ReAnimated animation library")
    return AnimationLibrary(animations, scripts)


def _item_payload(parsed: ParsedRp6l, item: Rp6lItem) -> bytes:
    if item.chunk_index < 0 or item.chunk_index >= len(parsed.chunks):
        raise ValueError("RP6L item references a missing chunk")
    chunk = parsed.chunks[item.chunk_index].data
    end = item.offset + item.size_or_hash
    if item.offset < 0 or item.size_or_hash < 0 or end > len(chunk):
        raise ValueError("RP6L item range is outside its chunk")
    return chunk[item.offset:end]


def _validate_parsed_rp6l(parsed: ParsedRp6l) -> None:
    for item in parsed.items:
        _item_payload(parsed, item)
        if item.unknown0 < -32768 or item.unknown0 > 32767:
            raise ValueError("RP6L item metadata is invalid")
    for resource in parsed.resources:
        if resource.name_index < 0 or resource.name_index >= len(parsed.names):
            raise ValueError("RP6L resource name index is invalid")
        if resource.first_item_index < 0 or resource.item_count < 0:
            raise ValueError("RP6L resource item range is invalid")
        if resource.first_item_index + resource.item_count > len(parsed.items):
            raise ValueError("RP6L resource item range exceeds the item table")


def _pad16(data: bytes) -> bytes:
    padding = (-len(data)) % 16
    return data + (b"\0" * padding)
