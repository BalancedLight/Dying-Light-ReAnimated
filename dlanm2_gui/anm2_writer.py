"""Production ANM2 payload writer.

This module contains the validated packed-component encoder used by FBX
exports.  It intentionally has no dependency on the historical research
pipeline so the release application does not import old diagnostic modules.
"""

from __future__ import annotations

import struct
from typing import Any

from . import anm2
from .anm2_base_segment import anm2_base_table_start_relative_for_aligned_base
from .anm2_components import COMPONENT_SLOTS
from .anm2_packed import encode_group_8


COMPONENT_NAMES = ("rx", "ry", "rz", "tx", "ty", "tz", "sx", "sy", "sz")


def build_payload_from_values(
    header: anm2.Anm2Header,
    descriptors: list[int],
    desired_values: list[list[list[float]]],
    packed_flags: list[list[bool]],
) -> bytes:
    """Encode decoded component values into a complete, paged ANM2 payload."""

    _validate_inputs(header, descriptors, desired_values, packed_flags)
    direct_values: list[float] = []
    packed_curves: list[dict[str, Any]] = []
    masks: list[int] = []
    for track_index in range(header.track_count):
        mask = 0
        for component_index, (bit, _group, _axis) in enumerate(COMPONENT_SLOTS):
            if bit == 64:
                if component_index != 6:
                    continue
                scale_direct = not any(
                    packed_flags[track_index][axis] for axis in (6, 7, 8)
                )
                if scale_direct:
                    mask |= 64
                    for axis in (6, 7, 8):
                        direct_values.append(float(desired_values[0][track_index][axis]))
                else:
                    for axis in (6, 7, 8):
                        packed_curves.append(
                            _packed_curve(track_index, axis, desired_values)
                        )
                continue
            if packed_flags[track_index][component_index]:
                packed_curves.append(
                    _packed_curve(track_index, component_index, desired_values)
                )
            else:
                mask |= bit
                direct_values.append(
                    float(desired_values[0][track_index][component_index])
                )
        masks.append(mask)

    slot_count = max(1, (header.frame_count - 2) // 15 + 1)
    stream_chunks = _build_stream_chunks(
        packed_curves, header.frame_count, slot_count
    )
    base_segment = _build_packed_base_segment(
        direct_values=direct_values,
        masks=masks,
        packed_biases=[curve["bias"] for curve in packed_curves],
        packed_scales=[curve["scale"] for curve in packed_curves],
    )
    pages, page_spans = _build_packed_pages(
        base_segment, stream_chunks, header.frame_count
    )
    return _build_payload_with_pages(header, descriptors, pages, page_spans)


def _validate_inputs(
    header: anm2.Anm2Header,
    descriptors: list[int],
    desired_values: list[list[list[float]]],
    packed_flags: list[list[bool]],
) -> None:
    if len(descriptors) != header.track_count:
        raise ValueError("descriptor count does not match the ANM2 header")
    if len(desired_values) != header.frame_count:
        raise ValueError("frame value count does not match the ANM2 header")
    if len(packed_flags) != header.track_count:
        raise ValueError("packed flag count does not match the ANM2 header")
    for frame_index, frame in enumerate(desired_values):
        if len(frame) != header.track_count:
            raise ValueError(f"frame {frame_index} has the wrong track count")
        if any(len(track) != 9 for track in frame):
            raise ValueError(f"frame {frame_index} contains a non-nine-component track")
    for track_index, flags in enumerate(packed_flags):
        if len(flags) != 9:
            raise ValueError(f"track {track_index} has the wrong packed flag count")
        scale = flags[6:9]
        if any(scale) and not all(scale):
            raise ValueError(
                "mixed scale direct/packed flags are not representable for "
                f"track {track_index}"
            )


def _packed_curve(
    track_index: int,
    component_index: int,
    values: list[list[list[float]]],
) -> dict[str, Any]:
    curve = [float(frame[track_index][component_index]) for frame in values]
    bias = curve[0]
    quantized, scale = _quantize_packed_curve(curve, bias)
    return {
        "track_index": track_index,
        "component_index": component_index,
        "component_name": COMPONENT_NAMES[component_index],
        "bias": bias,
        "scale": scale,
        "quantized": quantized,
    }


def _quantize_packed_curve(
    values: list[float], reference_value: float
) -> tuple[list[int], float]:
    max_delta = max(abs(value - reference_value) for value in values)
    if max_delta <= 0.0:
        return [0 for _value in values], 1.0
    scale = max(max_delta / 28000.0, 1.0e-9)
    for _attempt in range(12):
        quantized = [
            int(round((value - reference_value) / scale)) for value in values
        ]
        max_value = max(abs(value) for value in quantized)
        max_second_order = _max_second_order_delta_abs(quantized)
        if max_value <= 30000 and max_second_order <= 30000:
            return quantized, scale
        scale *= max(
            max_value / 30000.0,
            max_second_order / 30000.0,
            1.25,
        )
    quantized = [
        int(round((value - reference_value) / scale)) for value in values
    ]
    if (
        max(abs(value) for value in quantized) > 32767
        or _max_second_order_delta_abs(quantized) > 32767
    ):
        raise ValueError(
            "could not quantize packed source curve into int16 second-order stream"
        )
    return quantized, scale


def _max_second_order_delta_abs(values: list[int]) -> int:
    maximum = 0
    for index, current in enumerate(values):
        if index == 0:
            delta = current
        elif index == 1:
            delta = current - values[index - 1]
        else:
            delta = current - 2 * values[index - 1] + values[index - 2]
        maximum = max(maximum, abs(int(delta)))
    return maximum


def _build_stream_chunks(
    packed_curves: list[dict[str, Any]], frame_count: int, slot_count: int
) -> list[bytes]:
    group_count = max(1, (len(packed_curves) + 7) // 8)
    chunks: list[bytes] = []
    for slot_index in range(slot_count):
        base_frame = slot_index * 15
        chunk = bytearray()
        for group_index in range(group_count):
            chunk_values: list[list[int]] = []
            for frame_offset in range(16):
                frame = min(base_frame + frame_offset, frame_count - 1)
                lanes = [0] * 8
                for lane in range(8):
                    curve_index = group_index * 8 + lane
                    if curve_index < len(packed_curves):
                        lanes[lane] = int(
                            packed_curves[curve_index]["quantized"][frame]
                        )
                chunk_values.append(lanes)
            chunk.extend(encode_group_8(chunk_values))
        chunks.append(bytes(chunk))
    return chunks


def _build_packed_base_segment(
    *,
    direct_values: list[float],
    masks: list[int],
    packed_biases: list[float],
    packed_scales: list[float],
) -> bytes:
    direct_count = len(direct_values)
    packed_count = len(packed_biases)
    total_count = direct_count + packed_count
    packed_group_count = (packed_count + 7) // 8
    packed_table_bytes = 64 * packed_group_count
    if total_count != 9 * len(masks):
        raise AssertionError("packed base component counts do not match mask count")
    calibration = bytearray()
    for group_index in range(packed_group_count):
        start = group_index * 8
        biases = packed_biases[start : start + 8] + [0.0] * max(
            0, start + 8 - len(packed_biases)
        )
        scales = packed_scales[start : start + 8] + [1.0] * max(
            0, start + 8 - len(packed_scales)
        )
        calibration.extend(struct.pack("<8f", *map(float, biases)))
        calibration.extend(struct.pack("<8f", *map(float, scales)))
    segment = bytearray(
        struct.pack(
            "<8H",
            direct_count,
            packed_count,
            total_count,
            packed_table_bytes,
            0,
            0,
            0,
            0,
        )
    )
    table_start = anm2_base_table_start_relative_for_aligned_base()
    segment.extend(b"\0" * max(0, table_start - len(segment)))
    segment.extend(calibration)
    segment.extend(b"\0" * (-len(segment) % 16))
    segment.extend(struct.pack(f"<{direct_count}f", *direct_values))
    segment.extend(b"\0" * (-len(segment) % 4))
    segment.extend(bytes(masks))
    segment.extend(b"\0" * (-len(segment) % 16))
    return bytes(segment)


def _build_packed_page(base_segment: bytes, stream_chunks: list[bytes]) -> bytes:
    if not stream_chunks:
        raise ValueError("packed page needs at least one stream chunk")
    offset_word_count = len(stream_chunks) + 2
    table_word_count = max(16, offset_word_count)
    first_segment_word = (2 * table_word_count + 15) // 16
    offsets = [first_segment_word]
    cursor = first_segment_word + len(base_segment) // 16
    for chunk in stream_chunks:
        offsets.append(cursor)
        cursor += len(chunk) // 16
    offsets.append(cursor)
    if cursor * 16 > anm2.PAGE_SIZE:
        raise ValueError(
            f"packed page is {cursor * 16} bytes, exceeding the 64 KiB ANM2 page size"
        )
    table = offsets + [0] * (table_word_count - len(offsets))
    page = bytearray(struct.pack(f"<{table_word_count}H", *table))
    page.extend(b"\0" * (first_segment_word * 16 - len(page)))
    page.extend(base_segment)
    for chunk in stream_chunks:
        page.extend(chunk)
    return bytes(page)


def _build_packed_pages(
    base_segment: bytes, stream_chunks: list[bytes], frame_count: int
) -> tuple[list[bytes], list[int]]:
    if not stream_chunks:
        raise ValueError("packed ANM2 needs at least one stream chunk")
    page_chunk_groups: list[list[bytes]] = []
    current: list[bytes] = []
    for chunk in stream_chunks:
        candidate = [*current, chunk]
        try:
            _build_packed_page(base_segment, candidate)
        except ValueError:
            if not current:
                raise ValueError(
                    "a single packed stream chunk cannot fit in one ANM2 page"
                )
            page_chunk_groups.append(current)
            current = [chunk]
            _build_packed_page(base_segment, current)
        else:
            current = candidate
    if current:
        page_chunk_groups.append(current)

    pages = [
        _build_packed_page(base_segment, group) for group in page_chunk_groups
    ]
    remaining_span = max(0, frame_count - 1)
    page_spans: list[int] = []
    for group in page_chunk_groups:
        span = min(15 * len(group), remaining_span)
        page_spans.append(span)
        remaining_span -= span
    if remaining_span:
        raise AssertionError(
            f"packed pages do not cover the final {remaining_span} frame intervals"
        )
    return pages, page_spans


def _build_payload_with_pages(
    header: anm2.Anm2Header,
    descriptors: list[int],
    pages: list[bytes],
    page_spans: list[int],
) -> bytes:
    if not pages or len(pages) != len(page_spans):
        raise ValueError("ANM2 pages and page spans must be non-empty and aligned")
    if any(len(page) > anm2.PAGE_SIZE for page in pages):
        raise ValueError("ANM2 page exceeds 64 KiB")
    if sum(page_spans) != max(0, header.frame_count - 1):
        raise ValueError("page spans do not cover frame_count - 1")

    header_side = bytearray(struct.pack(f"<{header.track_count}I", *descriptors))
    header_side.extend(struct.pack(f"<{len(page_spans)}H", *page_spans))
    frame_span = max(0, header.frame_count - 1)
    header_side.extend(struct.pack("<HHH", 1, frame_span, 1))
    page_offset = anm2.HEADER_LENGTH + len(header_side)
    header_side.extend(b"\0" * (-page_offset % 16))
    page_offset = anm2.HEADER_LENGTH + len(header_side)

    page_blob = bytearray()
    for index, page in enumerate(pages):
        page_blob.extend(page)
        if index + 1 < len(pages):
            page_blob.extend(b"\0" * (anm2.PAGE_SIZE - len(page)))
    declared_length = page_offset + len(page_blob)
    out_header = anm2.HEADER_STRUCT.pack(
        anm2.MAGIC,
        anm2.FORMAT_VERSION,
        header.unknown06,
        header.frame_count,
        header.track_count,
        len(pages),
        page_offset,
        declared_length,
        1,
        0,
        0,
    )
    return bytes(out_header) + bytes(header_side) + bytes(page_blob)


__all__ = ["build_payload_from_values"]
