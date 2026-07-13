from __future__ import annotations

"""Generic, conservative RP6L inspection and opaque-resource merging.

This module deliberately does not decode mesh or skin runtime payloads.  It only
parses the RP6L container tables, keeps compiled chunk bytes opaque, and rebuilds
item/resource indexes when several compatible RP6L files are combined.

The only payload type normalized during a merge is BuilderInformation
(resource type -32257).  Builder records are plain, uncompressed text in the
known Dying Light object packs.  Duplicate builder categories such as ``_MESH_``
are coalesced into one record while the compiled runtime chunks remain byte-for-
byte unchanged.
"""

from dataclasses import dataclass, replace
from pathlib import Path
from typing import Any, Iterable, Sequence
import hashlib
import json
import struct


RP6L_MAGIC = b"RP6L"
RP6L_VERSION = 1

BUILDER_INFORMATION_TYPE = -32257
COMPILER_OBJECT_TYPE_BIT = 0x8000

SOURCE_MESH_TYPE = 16
SOURCE_SKIN_TYPE = 18
SOURCE_ANIMATION_TYPE = 64
SOURCE_ANIMATION_SCR_TYPE = 66

RUNTIME_TYPE_BIAS = 256
MESH_PAYLOAD_TYPE = SOURCE_MESH_TYPE + RUNTIME_TYPE_BIAS      # 272
SKIN_PAYLOAD_TYPE = SOURCE_SKIN_TYPE + RUNTIME_TYPE_BIAS      # 274
ANIMATION_PAYLOAD_TYPE = SOURCE_ANIMATION_TYPE + RUNTIME_TYPE_BIAS  # 320
ANIMATION_SCR_PAYLOAD_TYPE = SOURCE_ANIMATION_SCR_TYPE + RUNTIME_TYPE_BIAS  # 322

_HEADER = struct.Struct("<4s8i")
_CHUNK = struct.Struct("<HHIIiHH")
_ITEM = struct.Struct("<BBhIii")
_RESOURCE = struct.Struct("<hhii")


class Rp6lError(ValueError):
    """Raised when an RP6L container is structurally unsafe to inspect or merge."""


def resource_type_name(value: int) -> str:
    names = {
        BUILDER_INFORMATION_TYPE: "BuilderInformation",
        MESH_PAYLOAD_TYPE: "_MESH_ runtime",
        SKIN_PAYLOAD_TYPE: "_SKIN_ runtime",
        ANIMATION_PAYLOAD_TYPE: "_ANIMATION_ runtime",
        ANIMATION_SCR_PAYLOAD_TYPE: "_ANIMATION_SCR_ runtime",
    }
    return names.get(value, f"type_{value}")


@dataclass(frozen=True)
class Rp6lChunk:
    flags: int
    unknown0: int
    logical_size: int
    packed_size: int
    unknown1: int
    unknown2: int
    data: bytes
    original_offset: int = 0

    @classmethod
    def uncompressed(
        cls,
        data: bytes,
        *,
        flags: int,
        unknown0: int,
        unknown1: int = 1,
        unknown2: int = 2,
    ) -> "Rp6lChunk":
        return cls(
            flags=flags,
            unknown0=unknown0,
            logical_size=len(data),
            packed_size=0,
            unknown1=unknown1,
            unknown2=unknown2,
            data=bytes(data),
        )

    @property
    def raw_size(self) -> int:
        return len(self.data)

    @property
    def is_uncompressed(self) -> bool:
        return self.packed_size == 0

    def descriptor_dict(self, index: int, offset: int | None = None) -> dict[str, Any]:
        return {
            "index": index,
            "flags": self.flags,
            "unknown0": self.unknown0,
            "offset": self.original_offset if offset is None else offset,
            "logical_size": self.logical_size,
            "packed_size": self.packed_size,
            "raw_size": len(self.data),
            "unknown1": self.unknown1,
            "unknown2": self.unknown2,
            "sha256": hashlib.sha256(self.data).hexdigest(),
        }


@dataclass(frozen=True)
class Rp6lItem:
    chunk_index: int
    flags: int
    unknown0: int
    offset: int
    size_or_hash: int
    unknown1: int = 0

    def to_dict(self, index: int, names: Sequence[str]) -> dict[str, Any]:
        return {
            "index": index,
            "chunk_index": self.chunk_index,
            "flags": self.flags,
            "unknown0": self.unknown0,
            "unknown0_name": (
                names[self.unknown0] if 0 <= self.unknown0 < len(names) else None
            ),
            "offset": self.offset,
            "size_or_hash": self.size_or_hash,
            "unknown1": self.unknown1,
        }


@dataclass(frozen=True)
class Rp6lResource:
    item_count: int
    resource_type: int
    name_index: int
    first_item_index: int

    def to_dict(
        self,
        index: int,
        names: Sequence[str],
        items: Sequence[Rp6lItem],
    ) -> dict[str, Any]:
        name = names[self.name_index] if 0 <= self.name_index < len(names) else None
        item_range = list(
            range(self.first_item_index, self.first_item_index + self.item_count)
        )
        return {
            "index": index,
            "item_count": self.item_count,
            "resource_type": self.resource_type,
            "resource_type_name": resource_type_name(self.resource_type),
            "name_index": self.name_index,
            "name": name,
            "first_item_index": self.first_item_index,
            "item_indices": item_range,
            "items": [
                items[item_index].to_dict(item_index, names)
                for item_index in item_range
                if 0 <= item_index < len(items)
            ],
        }


@dataclass(frozen=True)
class Rp6lFile:
    version: int
    header_unknown0: int
    header_unknown1: int
    chunks: tuple[Rp6lChunk, ...]
    items: tuple[Rp6lItem, ...]
    resources: tuple[Rp6lResource, ...]
    names: tuple[str, ...]
    table_padding: bytes = b""
    physical_chunk_order: tuple[int, ...] = ()
    source_path: str = ""

    @classmethod
    def parse(cls, data: bytes, source_path: str = "") -> "Rp6lFile":
        if len(data) < _HEADER.size:
            raise Rp6lError("RP6L file is smaller than the 36-byte header")
        (
            magic,
            version,
            header_unknown0,
            item_count,
            chunk_count,
            resource_count,
            names_blob_size,
            name_count,
            header_unknown1,
        ) = _HEADER.unpack_from(data, 0)
        if magic != RP6L_MAGIC:
            raise Rp6lError(f"bad RP6L magic {magic!r}")
        counts = {
            "item_count": item_count,
            "chunk_count": chunk_count,
            "resource_count": resource_count,
            "names_blob_size": names_blob_size,
            "name_count": name_count,
        }
        for label, value in counts.items():
            if value < 0:
                raise Rp6lError(f"{label} is negative: {value}")
        if chunk_count > 255:
            raise Rp6lError(
                f"chunk count {chunk_count} exceeds the uint8 item-index limit"
            )

        cursor = _HEADER.size
        table_bytes = (
            _CHUNK.size * chunk_count
            + _ITEM.size * item_count
            + _RESOURCE.size * resource_count
            + 4 * name_count
            + names_blob_size
        )
        table_end = cursor + table_bytes
        if table_end > len(data):
            raise Rp6lError(
                f"RP6L tables end at 0x{table_end:X}, beyond file size 0x{len(data):X}"
            )

        chunk_descs: list[tuple[int, int, int, int, int, int, int]] = []
        for _ in range(chunk_count):
            chunk_descs.append(_CHUNK.unpack_from(data, cursor))
            cursor += _CHUNK.size

        items: list[Rp6lItem] = []
        for _ in range(item_count):
            row = _ITEM.unpack_from(data, cursor)
            cursor += _ITEM.size
            items.append(Rp6lItem(*row))

        resources: list[Rp6lResource] = []
        for _ in range(resource_count):
            row = _RESOURCE.unpack_from(data, cursor)
            cursor += _RESOURCE.size
            resources.append(Rp6lResource(*row))

        name_offsets: list[int] = []
        for _ in range(name_count):
            (offset,) = struct.unpack_from("<i", data, cursor)
            cursor += 4
            name_offsets.append(offset)

        names_blob = data[cursor : cursor + names_blob_size]
        cursor += names_blob_size
        names: list[str] = []
        for index, offset in enumerate(name_offsets):
            if offset < 0 or offset >= len(names_blob):
                raise Rp6lError(
                    f"name {index} offset {offset} is outside names blob size "
                    f"{len(names_blob)}"
                )
            end = names_blob.find(b"\0", offset)
            if end < 0:
                raise Rp6lError(f"name {index} is not NUL terminated")
            names.append(names_blob[offset:end].decode("utf-8", errors="replace"))

        offsets = [row[2] for row in chunk_descs]
        if len(set(offsets)) != len(offsets):
            raise Rp6lError("two RP6L chunks share the same data offset")
        physical_order = tuple(sorted(range(chunk_count), key=offsets.__getitem__))
        first_chunk_offset = offsets[physical_order[0]] if physical_order else len(data)
        if first_chunk_offset < cursor:
            raise Rp6lError(
                f"first chunk starts at 0x{first_chunk_offset:X}, inside RP6L tables "
                f"ending at 0x{cursor:X}"
            )
        table_padding = data[cursor:first_chunk_offset]

        next_offset_by_index: dict[int, int] = {}
        for order_index, chunk_index in enumerate(physical_order):
            offset = offsets[chunk_index]
            if offset < cursor or offset > len(data):
                raise Rp6lError(
                    f"chunk {chunk_index} offset 0x{offset:X} is outside file"
                )
            next_offset = (
                offsets[physical_order[order_index + 1]]
                if order_index + 1 < len(physical_order)
                else len(data)
            )
            if next_offset < offset:
                raise Rp6lError("chunk offsets are not monotonic")
            next_offset_by_index[chunk_index] = next_offset

        chunks: list[Rp6lChunk] = []
        for chunk_index, row in enumerate(chunk_descs):
            (
                flags,
                unknown0,
                offset,
                logical_size,
                packed_size,
                unknown1,
                unknown2,
            ) = row
            next_offset = next_offset_by_index.get(chunk_index, offset)
            raw = data[offset:next_offset]
            expected_raw = packed_size if packed_size > 0 else logical_size
            if expected_raw < 0:
                raise Rp6lError(
                    f"chunk {chunk_index} has negative packed/logical size"
                )
            if expected_raw > len(raw):
                raise Rp6lError(
                    f"chunk {chunk_index} descriptor requires {expected_raw} bytes, "
                    f"but only {len(raw)} bytes remain before the next chunk"
                )
            # Keep the full physical span.  Some compiler outputs align chunks and
            # the alignment bytes must survive an opaque roundtrip.
            chunks.append(
                Rp6lChunk(
                    flags=flags,
                    unknown0=unknown0,
                    logical_size=logical_size,
                    packed_size=packed_size,
                    unknown1=unknown1,
                    unknown2=unknown2,
                    data=raw,
                    original_offset=offset,
                )
            )

        result = cls(
            version=version,
            header_unknown0=header_unknown0,
            header_unknown1=header_unknown1,
            chunks=tuple(chunks),
            items=tuple(items),
            resources=tuple(resources),
            names=tuple(names),
            table_padding=table_padding,
            physical_chunk_order=physical_order,
            source_path=source_path,
        )
        result.validate()
        return result

    @classmethod
    def from_path(cls, path: str | Path) -> "Rp6lFile":
        source = Path(path)
        return cls.parse(source.read_bytes(), str(source))

    def validate(self) -> None:
        if self.version != RP6L_VERSION:
            raise Rp6lError(
                f"RP6L version {self.version} is not the known version {RP6L_VERSION}"
            )
        if len(self.chunks) > 255:
            raise Rp6lError("RP6L item chunk indexes are uint8; more than 255 chunks")
        if self.physical_chunk_order:
            if sorted(self.physical_chunk_order) != list(range(len(self.chunks))):
                raise Rp6lError("physical_chunk_order is not a permutation of chunks")
        for index, item in enumerate(self.items):
            if not 0 <= item.chunk_index < len(self.chunks):
                raise Rp6lError(
                    f"item {index} references invalid chunk {item.chunk_index}"
                )
            if item.offset < 0:
                raise Rp6lError(f"item {index} has negative chunk offset")
            chunk = self.chunks[item.chunk_index]
            if chunk.is_uncompressed and item.size_or_hash >= 0:
                if item.offset + item.size_or_hash > chunk.logical_size:
                    raise Rp6lError(
                        f"item {index} range 0x{item.offset:X}+0x{item.size_or_hash:X} "
                        f"exceeds uncompressed chunk logical size 0x{chunk.logical_size:X}"
                    )
        for index, resource in enumerate(self.resources):
            if resource.item_count < 0:
                raise Rp6lError(f"resource {index} has negative item count")
            if not 0 <= resource.name_index < len(self.names):
                raise Rp6lError(
                    f"resource {index} name index {resource.name_index} is invalid"
                )
            if resource.first_item_index < 0:
                raise Rp6lError(f"resource {index} has negative first item index")
            if resource.first_item_index + resource.item_count > len(self.items):
                raise Rp6lError(
                    f"resource {index} item range exceeds item table"
                )

    def _encoded_names(self) -> tuple[list[int], bytes]:
        offsets: list[int] = []
        blob = bytearray()
        for name in self.names:
            encoded = name.encode("utf-8")
            if b"\0" in encoded:
                raise Rp6lError(f"RP6L name contains NUL: {name!r}")
            offsets.append(len(blob))
            blob.extend(encoded)
            blob.append(0)
        return offsets, bytes(blob)

    def serialize(self) -> bytes:
        self.validate()
        name_offsets, names_blob = self._encoded_names()
        table_length = (
            _HEADER.size
            + _CHUNK.size * len(self.chunks)
            + _ITEM.size * len(self.items)
            + _RESOURCE.size * len(self.resources)
            + 4 * len(name_offsets)
            + len(names_blob)
        )
        padding = bytes(self.table_padding)
        cursor = table_length + len(padding)
        physical_order = (
            self.physical_chunk_order
            if self.physical_chunk_order
            else tuple(range(len(self.chunks)))
        )
        chunk_offsets = [0] * len(self.chunks)
        for chunk_index in physical_order:
            chunk_offsets[chunk_index] = cursor
            cursor += len(self.chunks[chunk_index].data)

        out = bytearray()
        out.extend(
            _HEADER.pack(
                RP6L_MAGIC,
                self.version,
                self.header_unknown0,
                len(self.items),
                len(self.chunks),
                len(self.resources),
                len(names_blob),
                len(name_offsets),
                self.header_unknown1,
            )
        )
        for index, chunk in enumerate(self.chunks):
            out.extend(
                _CHUNK.pack(
                    chunk.flags,
                    chunk.unknown0,
                    chunk_offsets[index],
                    chunk.logical_size,
                    chunk.packed_size,
                    chunk.unknown1,
                    chunk.unknown2,
                )
            )
        for item in self.items:
            out.extend(
                _ITEM.pack(
                    item.chunk_index,
                    item.flags,
                    item.unknown0,
                    item.offset,
                    item.size_or_hash,
                    item.unknown1,
                )
            )
        for resource in self.resources:
            out.extend(
                _RESOURCE.pack(
                    resource.item_count,
                    resource.resource_type,
                    resource.name_index,
                    resource.first_item_index,
                )
            )
        for offset in name_offsets:
            out.extend(struct.pack("<i", offset))
        out.extend(names_blob)
        out.extend(padding)
        for chunk_index in physical_order:
            out.extend(self.chunks[chunk_index].data)
        return bytes(out)

    def write(self, path: str | Path) -> Path:
        output = Path(path)
        output.parent.mkdir(parents=True, exist_ok=True)
        output.write_bytes(self.serialize())
        return output

    def is_lossless_roundtrip(self, original: bytes | None = None) -> bool:
        if original is None:
            if not self.source_path:
                return False
            original = Path(self.source_path).read_bytes()
        return self.serialize() == original

    def resource_name(self, resource: Rp6lResource) -> str:
        return self.names[resource.name_index]

    def resource_key(self, resource: Rp6lResource) -> tuple[int, str]:
        return (resource.resource_type, self.resource_name(resource))

    def resource_items(self, resource: Rp6lResource) -> tuple[Rp6lItem, ...]:
        start = resource.first_item_index
        return self.items[start : start + resource.item_count]

    def item_payload(self, item: Rp6lItem) -> bytes:
        chunk = self.chunks[item.chunk_index]
        if not chunk.is_uncompressed:
            raise Rp6lError(
                "cannot extract an item payload from a compressed opaque chunk"
            )
        if item.size_or_hash < 0:
            raise Rp6lError("item size_or_hash is negative and cannot be sliced")
        end = item.offset + item.size_or_hash
        if end > chunk.logical_size:
            raise Rp6lError("item payload exceeds chunk logical size")
        return chunk.data[item.offset:end]

    def resource_payloads(self, resource: Rp6lResource) -> tuple[bytes, ...]:
        return tuple(self.item_payload(item) for item in self.resource_items(resource))

    def resources_of_type(self, resource_type: int) -> tuple[Rp6lResource, ...]:
        return tuple(
            resource
            for resource in self.resources
            if resource.resource_type == resource_type
        )

    def to_dict(self, *, include_chunks: bool = True) -> dict[str, Any]:
        serialized = self.serialize()
        resources = [
            resource.to_dict(index, self.names, self.items)
            for index, resource in enumerate(self.resources)
        ]
        data: dict[str, Any] = {
            "format": "RP6L",
            "source_path": self.source_path,
            "version": self.version,
            "header_unknown0": self.header_unknown0,
            "header_unknown1": self.header_unknown1,
            "file_size": len(serialized),
            "sha256": hashlib.sha256(serialized).hexdigest(),
            "chunk_count": len(self.chunks),
            "item_count": len(self.items),
            "resource_count": len(self.resources),
            "name_count": len(self.names),
            "names": list(self.names),
            "resources": resources,
            "resource_type_counts": {},
            "table_padding_size": len(self.table_padding),
            "lossless_roundtrip": (
                self.is_lossless_roundtrip() if self.source_path else None
            ),
        }
        counts: dict[str, int] = {}
        for resource in self.resources:
            key = resource_type_name(resource.resource_type)
            counts[key] = counts.get(key, 0) + 1
        data["resource_type_counts"] = counts
        if include_chunks:
            # Recalculate offsets exactly as serialize() will use them.
            name_offsets, names_blob = self._encoded_names()
            cursor = (
                _HEADER.size
                + _CHUNK.size * len(self.chunks)
                + _ITEM.size * len(self.items)
                + _RESOURCE.size * len(self.resources)
                + 4 * len(name_offsets)
                + len(names_blob)
                + len(self.table_padding)
            )
            offsets = [0] * len(self.chunks)
            order = (
                self.physical_chunk_order
                if self.physical_chunk_order
                else tuple(range(len(self.chunks)))
            )
            for index in order:
                offsets[index] = cursor
                cursor += len(self.chunks[index].data)
            data["chunks"] = [
                chunk.descriptor_dict(index, offsets[index])
                for index, chunk in enumerate(self.chunks)
            ]
        return data


def _pad16(data: bytes) -> bytes:
    return data + b"\0" * ((-len(data)) % 16)


def _builder_payload(
    pack: Rp6lFile, resource: Rp6lResource
) -> tuple[bytes, Rp6lChunk, Rp6lItem]:
    items = pack.resource_items(resource)
    if not items:
        raise Rp6lError(
            f"builder resource {pack.resource_name(resource)!r} has no item"
        )
    payloads: list[bytes] = []
    for item in items:
        payloads.append(pack.item_payload(item).rstrip(b"\0"))
    payload = b"\n".join(part for part in payloads if part)
    if payload and not payload.endswith(b"\n"):
        payload += b"\n"
    first_item = items[0]
    first_chunk = pack.chunks[first_item.chunk_index]
    return payload, first_chunk, first_item


def merge_rp6l(
    packs: Sequence[Rp6lFile],
    *,
    collision_policy: str = "error",
    coalesce_builder_information: bool = True,
    source_path: str = "",
) -> Rp6lFile:
    """Merge compatible RP6L containers without decoding runtime payloads.

    Normal resources are copied resource-by-resource.  Their referenced compiled
    chunks are retained byte-for-byte and may remain shared.  Duplicate
    BuilderInformation categories are coalesced into a single uncompressed text
    record.  Duplicate runtime resource keys are rejected by default.
    """

    if not packs:
        raise Rp6lError("at least one RP6L input is required")
    if collision_policy not in {"error", "replace"}:
        raise Rp6lError("collision_policy must be 'error' or 'replace'")
    for pack in packs:
        pack.validate()
    version = packs[0].version
    header_unknown0 = packs[0].header_unknown0
    header_unknown1 = packs[0].header_unknown1
    for index, pack in enumerate(packs[1:], start=1):
        if pack.version != version:
            raise Rp6lError(
                f"pack {index} version {pack.version} does not match {version}"
            )
        if pack.header_unknown1 != header_unknown1:
            raise Rp6lError(
                f"pack {index} platform/header marker {pack.header_unknown1} "
                f"does not match {header_unknown1}"
            )

    # Use a deterministic, exact-string name table shared by all inputs.
    names: list[str] = []
    name_index: dict[str, int] = {}

    def intern(name: str) -> int:
        existing = name_index.get(name)
        if existing is not None:
            return existing
        index = len(names)
        if index > 0x7FFF:
            raise Rp6lError("merged RP6L name table exceeds signed int16 item index")
        names.append(name)
        name_index[name] = index
        return index

    old_name_maps: list[list[int]] = []
    for pack in packs:
        old_name_maps.append([intern(name) for name in pack.names])

    kept_resources: list[
        tuple[int, Rp6lFile, Rp6lResource, list[int]]
    ] = []
    key_to_position: dict[tuple[int, str], int] = {}
    builder_groups: dict[str, list[tuple[Rp6lFile, Rp6lResource]]] = {}

    for pack_index, pack in enumerate(packs):
        for resource in pack.resources:
            name = pack.resource_name(resource)
            if (
                coalesce_builder_information
                and resource.resource_type == BUILDER_INFORMATION_TYPE
            ):
                builder_groups.setdefault(name, []).append((pack, resource))
                continue
            key = (resource.resource_type, name)
            if key in key_to_position:
                if collision_policy == "error":
                    raise Rp6lError(
                        f"duplicate RP6L resource {resource_type_name(key[0])} "
                        f"{name!r}"
                    )
                old_position = key_to_position[key]
                kept_resources[old_position] = (
                    pack_index,
                    pack,
                    resource,
                    old_name_maps[pack_index],
                )
            else:
                key_to_position[key] = len(kept_resources)
                kept_resources.append(
                    (pack_index, pack, resource, old_name_maps[pack_index])
                )

    chunks: list[Rp6lChunk] = []
    items: list[Rp6lItem] = []
    resources: list[Rp6lResource] = []
    chunk_maps: list[dict[int, int]] = [dict() for _ in packs]

    def copy_chunk(pack_index: int, old_index: int) -> int:
        mapping = chunk_maps[pack_index]
        if old_index in mapping:
            return mapping[old_index]
        if len(chunks) >= 255:
            raise Rp6lError(
                "merged RP6L would exceed 255 chunks; split the resource library"
            )
        old = packs[pack_index].chunks[old_index]
        new_index = len(chunks)
        chunks.append(replace(old, original_offset=0))
        mapping[old_index] = new_index
        return new_index

    for pack_index, pack, resource, name_map in kept_resources:
        first_item = len(items)
        for old_item in pack.resource_items(resource):
            unknown0 = old_item.unknown0
            if 0 <= unknown0 < len(name_map):
                unknown0 = name_map[unknown0]
            if not -0x8000 <= unknown0 <= 0x7FFF:
                raise Rp6lError(
                    f"remapped item name/index {unknown0} does not fit int16"
                )
            items.append(
                replace(
                    old_item,
                    chunk_index=copy_chunk(pack_index, old_item.chunk_index),
                    unknown0=unknown0,
                )
            )
        resources.append(
            Rp6lResource(
                item_count=resource.item_count,
                resource_type=resource.resource_type,
                name_index=intern(pack.resource_name(resource)),
                first_item_index=first_item,
            )
        )

    # Rebuild one compact builder item per category.  This is the only payload
    # touched by the merger; compiled mesh/skin/animation bytes above stay opaque.
    for builder_name in sorted(builder_groups):
        payload_parts: list[bytes] = []
        first_chunk: Rp6lChunk | None = None
        first_item: Rp6lItem | None = None
        seen_payloads: set[bytes] = set()
        for pack, resource in builder_groups[builder_name]:
            payload, chunk_template, item_template = _builder_payload(pack, resource)
            if payload and payload not in seen_payloads:
                seen_payloads.add(payload)
                payload_parts.append(payload.rstrip(b"\0"))
            if first_chunk is None:
                first_chunk = chunk_template
                first_item = item_template
        payload = b"".join(
            part if part.endswith(b"\n") else part + b"\n"
            for part in payload_parts
        )
        raw = _pad16(payload)
        template = first_chunk or Rp6lChunk.uncompressed(
            b"", flags=255, unknown0=4, unknown2=1
        )
        if template.packed_size:
            # Builder text must be inspectable to coalesce.  We already extracted
            # it above, so emit a canonical uncompressed builder chunk.
            template = Rp6lChunk.uncompressed(
                b"", flags=255, unknown0=4, unknown1=1, unknown2=1
            )
        if len(chunks) >= 255:
            raise Rp6lError(
                "merged RP6L would exceed 255 chunks after builder coalescing"
            )
        chunk_index = len(chunks)
        chunks.append(
            Rp6lChunk(
                flags=template.flags,
                unknown0=template.unknown0,
                logical_size=len(raw),
                packed_size=0,
                unknown1=template.unknown1,
                unknown2=template.unknown2,
                data=raw,
            )
        )
        name_id = intern(builder_name)
        item_template = first_item or Rp6lItem(
            chunk_index=chunk_index,
            flags=0,
            unknown0=name_id,
            offset=0,
            size_or_hash=len(payload),
            unknown1=0,
        )
        first_item_index = len(items)
        items.append(
            Rp6lItem(
                chunk_index=chunk_index,
                flags=item_template.flags,
                unknown0=name_id,
                offset=0,
                size_or_hash=len(payload),
                unknown1=item_template.unknown1,
            )
        )
        resources.append(
            Rp6lResource(
                item_count=1,
                resource_type=BUILDER_INFORMATION_TYPE,
                name_index=name_id,
                first_item_index=first_item_index,
            )
        )

    result = Rp6lFile(
        version=version,
        header_unknown0=header_unknown0,
        header_unknown1=header_unknown1,
        chunks=tuple(chunks),
        items=tuple(items),
        resources=tuple(resources),
        names=tuple(names),
        table_padding=b"",
        physical_chunk_order=tuple(range(len(chunks))),
        source_path=source_path,
    )
    result.validate()
    return result



def _signed_int16(value: int) -> int:
    value &= 0xFFFF
    return value - 0x10000 if value & 0x8000 else value


def link_compiler_object_pack(
    data: bytes,
    source_path: str = "",
) -> Rp6lFile:
    """Normalize a Techland ``*_obj`` RP6L link unit into a normal RP6L file.

    Mesh compiler object files are not ordinary, self-contained RP6L packs even
    though they use the same header and tables.  Several chunk descriptors have
    an offset of zero, while the items referencing those chunks carry absolute
    file offsets.  The runtime resource type also has bit 0x8000 set (for
    example ``0x8110`` instead of runtime ``0x0110``).  This function resolves
    those link-unit conventions without decoding any mesh, skin, vertex, or
    index payload.
    """

    if len(data) < _HEADER.size:
        raise Rp6lError("compiler object file is smaller than the RP6L header")
    (
        magic,
        version,
        header_unknown0,
        item_count,
        chunk_count,
        resource_count,
        names_blob_size,
        name_count,
        header_unknown1,
    ) = _HEADER.unpack_from(data, 0)
    if magic != RP6L_MAGIC:
        raise Rp6lError(f"bad RP6L magic {magic!r}")
    for label, value in {
        "item_count": item_count,
        "chunk_count": chunk_count,
        "resource_count": resource_count,
        "names_blob_size": names_blob_size,
        "name_count": name_count,
    }.items():
        if value < 0:
            raise Rp6lError(f"{label} is negative: {value}")
    if chunk_count > 255:
        raise Rp6lError("compiler object has more than 255 chunks")

    cursor = _HEADER.size
    table_size = (
        _CHUNK.size * chunk_count
        + _ITEM.size * item_count
        + _RESOURCE.size * resource_count
        + 4 * name_count
        + names_blob_size
    )
    table_end = cursor + table_size
    if table_end > len(data):
        raise Rp6lError("compiler object tables extend beyond the file")

    chunk_descs: list[tuple[int, int, int, int, int, int, int]] = []
    for _ in range(chunk_count):
        chunk_descs.append(_CHUNK.unpack_from(data, cursor))
        cursor += _CHUNK.size

    items: list[Rp6lItem] = []
    for _ in range(item_count):
        items.append(Rp6lItem(*_ITEM.unpack_from(data, cursor)))
        cursor += _ITEM.size

    resources: list[Rp6lResource] = []
    for _ in range(resource_count):
        resources.append(Rp6lResource(*_RESOURCE.unpack_from(data, cursor)))
        cursor += _RESOURCE.size

    name_offsets: list[int] = []
    for _ in range(name_count):
        (offset,) = struct.unpack_from("<i", data, cursor)
        cursor += 4
        name_offsets.append(offset)
    names_blob = data[cursor : cursor + names_blob_size]
    cursor += names_blob_size
    names: list[str] = []
    for index, offset in enumerate(name_offsets):
        if offset < 0 or offset >= len(names_blob):
            raise Rp6lError(
                f"compiler object name {index} offset {offset} is invalid"
            )
        end = names_blob.find(b"\0", offset)
        if end < 0:
            raise Rp6lError(f"compiler object name {index} is not NUL terminated")
        names.append(names_blob[offset:end].decode("utf-8", errors="replace"))

    shared_zero_chunks = [
        index for index, row in enumerate(chunk_descs) if row[2] == 0
    ]
    if not shared_zero_chunks:
        raise Rp6lError("RP6L file does not use compiler-object chunk addressing")

    rewritten_items = list(items)
    chunks: list[Rp6lChunk] = []
    for chunk_index, row in enumerate(chunk_descs):
        (
            flags,
            unknown0,
            declared_offset,
            logical_size,
            packed_size,
            unknown1,
            unknown2,
        ) = row
        raw_size = packed_size if packed_size > 0 else logical_size
        if raw_size < 0:
            raise Rp6lError(f"compiler object chunk {chunk_index} has bad size")
        refs = [
            (item_index, item)
            for item_index, item in enumerate(items)
            if item.chunk_index == chunk_index
        ]

        if declared_offset == 0:
            if not refs:
                raise Rp6lError(
                    f"zero-offset compiler object chunk {chunk_index} has no item"
                )
            absolute_offsets = [item.offset for _, item in refs]
            base_offset = min(absolute_offsets)
            if base_offset < table_end:
                raise Rp6lError(
                    f"compiler object chunk {chunk_index} payload starts inside tables"
                )
            if base_offset + raw_size > len(data):
                raise Rp6lError(
                    f"compiler object chunk {chunk_index} payload exceeds file"
                )
            raw = data[base_offset : base_offset + raw_size]
            for item_index, item in refs:
                relative = item.offset - base_offset
                if relative < 0:
                    raise Rp6lError(
                        f"compiler object item {item_index} precedes its chunk base"
                    )
                if item.size_or_hash >= 0 and relative + item.size_or_hash > logical_size:
                    raise Rp6lError(
                        f"compiler object item {item_index} exceeds chunk {chunk_index}"
                    )
                rewritten_items[item_index] = replace(item, offset=relative)
        else:
            if declared_offset < table_end:
                raise Rp6lError(
                    f"compiler object chunk {chunk_index} starts inside tables"
                )
            if declared_offset + raw_size > len(data):
                raise Rp6lError(
                    f"compiler object chunk {chunk_index} payload exceeds file"
                )
            raw = data[declared_offset : declared_offset + raw_size]
            # Most nonzero object chunks (notably BuilderInformation) already
            # use chunk-relative item offsets.  Accept absolute offsets too and
            # normalize them when they clearly point into this chunk.
            for item_index, item in refs:
                if (
                    item.offset >= declared_offset
                    and item.size_or_hash >= 0
                    and item.offset + item.size_or_hash <= declared_offset + logical_size
                ):
                    rewritten_items[item_index] = replace(
                        item, offset=item.offset - declared_offset
                    )

        chunks.append(
            Rp6lChunk(
                flags=flags,
                unknown0=unknown0,
                logical_size=logical_size,
                packed_size=packed_size,
                unknown1=unknown1,
                unknown2=unknown2,
                data=raw,
                original_offset=declared_offset,
            )
        )

    normalized_resources: list[Rp6lResource] = []
    converted_count = 0
    for resource in resources:
        resource_type = resource.resource_type
        raw_type = resource_type & 0xFFFF
        if (
            resource_type != BUILDER_INFORMATION_TYPE
            and raw_type & COMPILER_OBJECT_TYPE_BIT
        ):
            resource_type = _signed_int16(raw_type & ~COMPILER_OBJECT_TYPE_BIT)
            converted_count += 1
        normalized_resources.append(replace(resource, resource_type=resource_type))
    if converted_count == 0:
        raise Rp6lError(
            "shared-offset RP6L did not contain a compiler-object resource type"
        )

    result = Rp6lFile(
        version=version,
        header_unknown0=header_unknown0,
        header_unknown1=header_unknown1,
        chunks=tuple(chunks),
        items=tuple(rewritten_items),
        resources=tuple(normalized_resources),
        names=tuple(names),
        table_padding=b"",
        physical_chunk_order=tuple(range(len(chunks))),
        source_path=source_path,
    )
    result.validate()
    return result


def load_rp6l_link_input(path: str | Path) -> tuple[Rp6lFile, str]:
    """Load either a normal RP6L pack or a compiler ``*_obj`` link unit."""

    source = Path(path)
    data = source.read_bytes()
    try:
        return Rp6lFile.parse(data, str(source)), "standard_rp6l"
    except Rp6lError as standard_error:
        try:
            return link_compiler_object_pack(data, str(source)), "compiler_object_linked"
        except Rp6lError as object_error:
            raise Rp6lError(
                f"cannot load RP6L input {source}: standard parse failed: "
                f"{standard_error}; compiler-object link failed: {object_error}"
            ) from object_error

def merge_rp6l_paths(
    inputs: Iterable[str | Path],
    output: str | Path,
    *,
    collision_policy: str = "error",
    coalesce_builder_information: bool = True,
) -> dict[str, Any]:
    paths = [Path(path) for path in inputs]
    loaded = [load_rp6l_link_input(path) for path in paths]
    packs = [row[0] for row in loaded]
    input_modes = [row[1] for row in loaded]
    merged = merge_rp6l(
        packs,
        collision_policy=collision_policy,
        coalesce_builder_information=coalesce_builder_information,
        source_path=str(output),
    )
    output_path = merged.write(output)
    reparsed = Rp6lFile.from_path(output_path)
    report = {
        "format": "chrome_mesh_tools_rp6l_merge_v1",
        "inputs": [
            {
                "path": str(path),
                "size": path.stat().st_size,
                "sha256": hashlib.sha256(path.read_bytes()).hexdigest(),
                "input_mode": input_mode,
                "resource_count": len(pack.resources),
                "chunk_count": len(pack.chunks),
            }
            for path, pack, input_mode in zip(paths, packs, input_modes)
        ],
        "output": str(output_path),
        "output_size": output_path.stat().st_size,
        "output_sha256": hashlib.sha256(output_path.read_bytes()).hexdigest(),
        "resource_count": len(reparsed.resources),
        "chunk_count": len(reparsed.chunks),
        "resource_type_counts": reparsed.to_dict(include_chunks=False)[
            "resource_type_counts"
        ],
        "opaque_runtime_payloads_reencoded": False,
        "builder_information_coalesced": coalesce_builder_information,
        "collision_policy": collision_policy,
        "parse_after_write": True,
        "engine_or_editor_tested": False,
    }
    return report


def write_inspection_report(
    input_path: str | Path,
    output_path: str | Path,
    *,
    include_chunks: bool = True,
) -> dict[str, Any]:
    pack, input_mode = load_rp6l_link_input(input_path)
    report = pack.to_dict(include_chunks=include_chunks)
    report["input_mode"] = input_mode
    Path(output_path).write_text(
        json.dumps(report, indent=2) + "\n", encoding="utf-8"
    )
    return report
