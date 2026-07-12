from __future__ import annotations

import struct
from dataclasses import dataclass


class BinaryReadError(ValueError):
    pass


@dataclass
class Reader:
    data: bytes
    offset: int = 0
    label: str = "buffer"

    @property
    def remaining(self) -> int:
        return len(self.data) - self.offset

    def require(self, size: int, what: str = "data") -> None:
        if size < 0 or self.offset + size > len(self.data):
            raise BinaryReadError(
                f"{self.label}: need {size} bytes for {what} at 0x{self.offset:X}, "
                f"only {self.remaining} remain"
            )

    def read(self, size: int, what: str = "data") -> bytes:
        self.require(size, what)
        start = self.offset
        self.offset += size
        return self.data[start : start + size]

    def unpack(self, fmt: str, what: str = "value") -> tuple:
        size = struct.calcsize(fmt)
        self.require(size, what)
        values = struct.unpack_from(fmt, self.data, self.offset)
        self.offset += size
        return values

    def u8(self, what: str = "u8") -> int:
        return self.unpack("<B", what)[0]

    def u16(self, what: str = "u16") -> int:
        return self.unpack("<H", what)[0]

    def i16(self, what: str = "i16") -> int:
        return self.unpack("<h", what)[0]

    def u32(self, what: str = "u32") -> int:
        return self.unpack("<I", what)[0]

    def f32(self, what: str = "f32") -> float:
        return self.unpack("<f", what)[0]

    def f32s(self, count: int, what: str = "float array") -> tuple[float, ...]:
        return self.unpack(f"<{count}f", what)

    def lp_string_u16(self, what: str = "string", encoding: str = "utf-8") -> str:
        length = self.u16(f"{what} length")
        raw = self.read(length, what)
        return raw.decode(encoding, errors="replace")

    def ensure_eof(self, *, allow_zero_padding: bool = False) -> None:
        if self.remaining == 0:
            return
        tail = self.data[self.offset :]
        if allow_zero_padding and not any(tail):
            self.offset = len(self.data)
            return
        raise BinaryReadError(
            f"{self.label}: {self.remaining} trailing bytes at 0x{self.offset:X}"
        )
