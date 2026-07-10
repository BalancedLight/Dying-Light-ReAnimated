from __future__ import annotations

import struct


GROUP_SIZE = 8
FRAME_COUNT = 16


def encode_group_8(reconstructed_values: list[list[int]]) -> bytes:
    """Encode one ANM2 packed group for 8 moving components and 16 frames.

    The runtime path is `sub_1800D6550` followed by `sub_1800D6700`.
    This writes the inverse: second-order deltas, per-frame bit widths, and
    a transposed 8-lane signed bitstream.
    """

    _validate_frame_values(reconstructed_values)
    deltas = _second_order_deltas(reconstructed_values)
    widths = [_frame_width(frame) for frame in deltas]
    bit_count = 8 + sum(widths)
    byte_count = _align16(bit_count)
    lane_words = [[0 for _ in range(byte_count // 16)] for _ in range(GROUP_SIZE)]

    for frame_index, width in enumerate(widths):
        nibble = _encode_width_nibble(width)
        if frame_index < GROUP_SIZE:
            lane_words[frame_index][0] |= nibble << 12
        else:
            lane_words[frame_index - GROUP_SIZE][0] |= nibble << 8

    bit_offset = 8
    for width, frame in zip(widths, deltas):
        if width:
            for lane, value in enumerate(frame):
                _insert_signed_bits(lane_words[lane], bit_offset, width, value)
        bit_offset += width

    out = bytearray(byte_count)
    for word_index in range(byte_count // 16):
        for lane in range(GROUP_SIZE):
            struct.pack_into("<H", out, word_index * 16 + lane * 2, lane_words[lane][word_index] & 0xFFFF)
    return bytes(out)


def decode_group_8(data: bytes, max_frame: int = 15) -> list[list[int]]:
    """Decode one packed group using the same frame range as `sub_1800D7B30`."""

    if not 0 <= max_frame < FRAME_COUNT:
        raise ValueError("max_frame must be in 0..15")
    if len(data) < 16:
        raise ValueError("packed group data must contain at least one 16-byte block")
    widths = _read_widths(data)
    byte_count = _align16(8 + sum(widths))
    if len(data) < byte_count:
        raise ValueError(f"packed group data is too short: {len(data)} < {byte_count}")
    deltas: list[list[int]] = []
    bit_offset = 8
    for frame_index, width in enumerate(widths):
        if frame_index > max_frame:
            break
        deltas.append([_extract_signed_bits(data, lane, bit_offset, width) for lane in range(GROUP_SIZE)])
        bit_offset += width
    return _integrate_second_order(deltas)


def packed_group_length(data: bytes) -> int:
    return _align16(8 + sum(_read_widths(data)))


def _validate_frame_values(values: list[list[int]]) -> None:
    if len(values) != FRAME_COUNT:
        raise ValueError(f"expected {FRAME_COUNT} frames, got {len(values)}")
    for frame in values:
        if len(frame) != GROUP_SIZE:
            raise ValueError(f"expected {GROUP_SIZE} values per frame, got {len(frame)}")
        for value in frame:
            if not -32768 <= int(value) <= 32767:
                raise ValueError(f"packed values must fit int16, got {value}")


def _second_order_deltas(values: list[list[int]]) -> list[list[int]]:
    deltas: list[list[int]] = []
    for frame_index, frame in enumerate(values):
        if frame_index == 0:
            delta = list(frame)
        elif frame_index == 1:
            previous = values[frame_index - 1]
            delta = [wrap16(current - prior) for current, prior in zip(frame, previous)]
        else:
            previous = values[frame_index - 1]
            before_previous = values[frame_index - 2]
            delta = [
                wrap16(current - saturate16(2 * prior - before))
                for current, prior, before in zip(frame, previous, before_previous)
            ]
        deltas.append(delta)
    return deltas


def _integrate_second_order(deltas: list[list[int]]) -> list[list[int]]:
    return integrate_engine_second_order(deltas)


def integrate_engine_second_order(deltas: list[list[int]]) -> list[list[int]]:
    values: list[list[int]] = []
    for frame_index, frame in enumerate(deltas):
        if frame_index == 0:
            values.append(list(frame))
        elif frame_index == 1:
            previous = values[frame_index - 1]
            values.append([wrap16(delta + prior) for delta, prior in zip(frame, previous)])
        else:
            previous = values[frame_index - 1]
            before_previous = values[frame_index - 2]
            values.append([
                wrap16(delta + saturate16(2 * prior - before))
                for delta, prior, before in zip(frame, previous, before_previous)
            ])
    return values


def wrap16(value: int) -> int:
    value = int(value) & 0xFFFF
    return value - 0x10000 if value & 0x8000 else value


def saturate16(value: int) -> int:
    return max(-32768, min(32767, int(value)))


def _frame_width(frame: list[int]) -> int:
    width = max(_signed_width(value) for value in frame)
    return 16 if width == 15 else width


def _signed_width(value: int) -> int:
    value = int(value)
    if value == 0:
        return 0
    for width in range(1, 17):
        if width == 15:
            continue
        if -(1 << (width - 1)) <= value <= (1 << (width - 1)) - 1:
            return width
    raise ValueError(f"value does not fit ANM2 signed packed width: {value}")


def _encode_width_nibble(width: int) -> int:
    if width == 16:
        return 15
    if 0 <= width <= 14:
        return width
    raise ValueError(f"unsupported ANM2 packed width {width}")


def _decode_width_nibble(nibble: int) -> int:
    return 16 if nibble == 15 else nibble


def _read_widths(data: bytes) -> list[int]:
    words = struct.unpack_from("<8H", data, 0)
    high = [_decode_width_nibble((word >> 12) & 0xF) for word in words]
    mid = [_decode_width_nibble((word >> 8) & 0xF) for word in words]
    return high + mid


def _insert_signed_bits(words: list[int], bit_offset: int, width: int, value: int) -> None:
    encoded = int(value) & ((1 << width) - 1)
    for bit_index in range(width):
        if encoded & (1 << (width - 1 - bit_index)):
            absolute = bit_offset + bit_index
            word_index = absolute // 16
            bit_in_word = 15 - (absolute % 16)
            words[word_index] |= 1 << bit_in_word


def _extract_signed_bits(data: bytes, lane: int, bit_offset: int, width: int) -> int:
    if width == 0:
        return 0
    value = 0
    for bit_index in range(width):
        absolute = bit_offset + bit_index
        word_index = absolute // 16
        bit_in_word = 15 - (absolute % 16)
        word = struct.unpack_from("<H", data, word_index * 16 + lane * 2)[0]
        value = (value << 1) | ((word >> bit_in_word) & 1)
    sign_bit = 1 << (width - 1)
    if value & sign_bit:
        value -= 1 << width
    return value


def _align16(value: int) -> int:
    return (value + 15) & ~15
