from __future__ import annotations

import struct
from dataclasses import dataclass, field
from pathlib import Path
from typing import Iterator


class MshFormatError(ValueError):
    """Raised when a source MSH chunk tree is structurally invalid."""


@dataclass(frozen=True)
class ChunkHeader:
    chunk_id: int
    version: int
    chunk_size: int
    data_size: int

    STRUCT = struct.Struct("<4I")
    SIZE = STRUCT.size

    @classmethod
    def parse_from(cls, data: bytes, offset: int, limit: int) -> "ChunkHeader":
        if offset < 0 or offset + cls.SIZE > limit:
            raise MshFormatError(
                f"chunk header at 0x{offset:X} exceeds container end 0x{limit:X}"
            )
        values = cls.STRUCT.unpack_from(data, offset)
        header = cls(*values)
        if header.chunk_size < cls.SIZE:
            raise MshFormatError(
                f"chunk 0x{header.chunk_id:X} at 0x{offset:X}: "
                f"chunk_size {header.chunk_size} is smaller than 16-byte header"
            )
        if header.data_size > header.chunk_size - cls.SIZE:
            raise MshFormatError(
                f"chunk 0x{header.chunk_id:X} at 0x{offset:X}: data_size "
                f"{header.data_size} exceeds chunk payload {header.chunk_size - cls.SIZE}"
            )
        if offset + header.chunk_size > limit:
            raise MshFormatError(
                f"chunk 0x{header.chunk_id:X} at 0x{offset:X}: end "
                f"0x{offset + header.chunk_size:X} exceeds container end 0x{limit:X}"
            )
        return header

    def pack(self) -> bytes:
        return self.STRUCT.pack(
            self.chunk_id, self.version, self.chunk_size, self.data_size
        )


@dataclass
class Chunk:
    header: ChunkHeader
    offset: int
    payload: bytes
    children: list["Chunk"] = field(default_factory=list)
    trailing: bytes = b""

    @property
    def chunk_id(self) -> int:
        return self.header.chunk_id

    @property
    def version(self) -> int:
        return self.header.version

    @property
    def end(self) -> int:
        return self.offset + self.header.chunk_size

    @property
    def data_offset(self) -> int:
        return self.offset + ChunkHeader.SIZE

    @property
    def child_offset(self) -> int:
        return self.data_offset + self.header.data_size

    def walk(self) -> Iterator["Chunk"]:
        yield self
        for child in self.children:
            yield from child.walk()

    def find_all(self, chunk_id: int) -> list["Chunk"]:
        return [chunk for chunk in self.walk() if chunk.chunk_id == chunk_id]

    def serialize(self) -> bytes:
        child_blob = b"".join(child.serialize() for child in self.children)
        body = self.payload + child_blob + self.trailing
        header = ChunkHeader(
            chunk_id=self.chunk_id,
            version=self.version,
            chunk_size=ChunkHeader.SIZE + len(body),
            data_size=len(self.payload),
        )
        return header.pack() + body

    def to_tree_dict(self) -> dict:
        return {
            "id": f"0x{self.chunk_id:08X}",
            "version": self.version,
            "offset": self.offset,
            "chunk_size": self.header.chunk_size,
            "data_size": self.header.data_size,
            "trailing_size": len(self.trailing),
            "children": [child.to_tree_dict() for child in self.children],
        }


def _parse_chunk(
    data: bytes,
    offset: int,
    limit: int,
    *,
    allow_trailing: bool,
) -> Chunk:
    header = ChunkHeader.parse_from(data, offset, limit)
    data_start = offset + ChunkHeader.SIZE
    data_end = data_start + header.data_size
    chunk_end = offset + header.chunk_size
    payload = data[data_start:data_end]
    children: list[Chunk] = []
    cursor = data_end
    trailing = b""

    while cursor < chunk_end:
        remaining = chunk_end - cursor
        if remaining < ChunkHeader.SIZE:
            tail = data[cursor:chunk_end]
            if allow_trailing or not any(tail):
                trailing = tail
                cursor = chunk_end
                break
            raise MshFormatError(
                f"chunk 0x{header.chunk_id:X} at 0x{offset:X}: "
                f"{remaining} nonzero bytes cannot form a child header"
            )

        try:
            child_header = ChunkHeader.parse_from(data, cursor, chunk_end)
        except MshFormatError:
            tail = data[cursor:chunk_end]
            if allow_trailing:
                trailing = tail
                cursor = chunk_end
                break
            raise

        child = _parse_chunk(
            data, cursor, cursor + child_header.chunk_size, allow_trailing=allow_trailing
        )
        children.append(child)
        cursor += child_header.chunk_size

    if cursor != chunk_end:
        raise MshFormatError(
            f"chunk 0x{header.chunk_id:X} at 0x{offset:X}: parser stopped at "
            f"0x{cursor:X}, expected 0x{chunk_end:X}"
        )
    return Chunk(header, offset, payload, children, trailing)


def parse_chunk_tree(data: bytes, *, allow_trailing: bool = False) -> Chunk:
    if len(data) < ChunkHeader.SIZE:
        raise MshFormatError("file is smaller than one 16-byte chunk header")
    root = _parse_chunk(data, 0, len(data), allow_trailing=allow_trailing)
    if root.end != len(data):
        raise MshFormatError(
            f"root chunk ends at 0x{root.end:X}, file ends at 0x{len(data):X}"
        )
    return root


def load_chunk_tree(path: str | Path, *, allow_trailing: bool = False) -> Chunk:
    return parse_chunk_tree(Path(path).read_bytes(), allow_trailing=allow_trailing)
