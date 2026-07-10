from __future__ import annotations

import math
import struct
from dataclasses import dataclass
from pathlib import Path

from . import anm2
from .anm2_base_segment import anm2_base_table_start
from .anm2_packed import decode_group_8, packed_group_length


COMPONENT_SLOTS = (
    (1, "r", 0),
    (2, "r", 1),
    (4, "r", 2),
    (8, "t", 0),
    (16, "t", 1),
    (32, "t", 2),
    (64, "s", 0),
    (64, "s", 1),
    (64, "s", 2),
)


@dataclass(frozen=True, slots=True)
class ComponentRef:
    track_index: int
    component_index: int
    group: str
    axis: int
    source: str
    source_index: int


@dataclass(frozen=True, slots=True)
class ComponentSampleInfo:
    component: str
    component_index: int
    group: str
    axis: int
    source: str
    source_index: int
    mask_byte: int
    page_index: int
    table_index: int
    in_segment_frame: int
    fraction: float
    current_value: float | None
    next_value: float | None
    value: float


@dataclass(frozen=True, slots=True)
class DecodedFrame:
    requested_time: float
    page_index: int
    table_index: int
    in_segment_frame: int
    fraction: float
    tracks: tuple[tuple[float, ...], ...]
    component_sources: tuple[tuple[ComponentSampleInfo, ...], ...] = ()


@dataclass(frozen=True, slots=True)
class DecodedClipSample:
    frame_count: int
    track_count: int
    descriptors: tuple[int, ...]
    frames: tuple[DecodedFrame, ...]


def decode_file_samples(path: str | Path, times: list[float]) -> DecodedClipSample:
    return decode_samples(Path(path).read_bytes(), times)


def decode_samples(data: bytes, times: list[float]) -> DecodedClipSample:
    header = anm2.Anm2Header.parse(data)
    descriptors = tuple(struct.unpack_from(f"<{header.track_count}I", data, anm2.HEADER_LENGTH))
    page_spans, duration_words = _header_side_tables(header, data)
    page_tables = [_read_page_table(data, header.unknown14 + anm2.PAGE_SIZE * index) for index in range(header.unknown12)]
    frames = tuple(
        _decode_frame(
            data=data,
            header=header,
            page_spans=page_spans,
            duration_words=duration_words,
            page_tables=page_tables,
            time=time,
        )
        for time in times
    )
    return DecodedClipSample(
        frame_count=header.frame_count,
        track_count=header.track_count,
        descriptors=descriptors,
        frames=frames,
    )


def max_frame_component_delta(left: DecodedFrame, right: DecodedFrame) -> float:
    maximum = 0.0
    for left_track, right_track in zip(left.tracks, right.tracks):
        for left_value, right_value in zip(left_track, right_track):
            maximum = max(maximum, abs(right_value - left_value))
    return maximum


def _decode_frame(
    *,
    data: bytes,
    header: anm2.Anm2Header,
    page_spans: list[int],
    duration_words: list[int],
    page_tables: list[list[int]],
    time: float,
) -> DecodedFrame:
    selection = anm2.select_v1_time(
        page_offset=header.unknown14,
        page_spans=page_spans,
        duration_words=duration_words,
        page_tables=page_tables,
        time=time,
    )
    if selection is None:
        raise ValueError("could not select v1 ANM2 time")
    page_offset = header.unknown14 + anm2.PAGE_SIZE * selection.page_index
    table = page_tables[selection.page_index]
    if selection.table_index + 1 >= len(table):
        raise ValueError(f"selected table index {selection.table_index} has no end offset")

    base_offset = page_offset + 16 * table[0]
    base_header = list(struct.unpack_from("<8H", data, base_offset))
    direct_count, packed_count, total_count, packed_table_bytes = base_header[:4]
    if total_count != 9 * header.track_count:
        raise ValueError(f"component count {total_count} does not match track count {header.track_count}")
    if packed_table_bytes < ((packed_count + 7) // 8) * 64:
        raise ValueError("packed calibration table is shorter than packed component groups")

    last_error: Exception | None = None
    for stream_base in _candidate_stream_bases(base_offset):
        try:
            return _decode_frame_with_stream_base(
                data=data,
                header=header,
                page_offset=page_offset,
                table=table,
                selection=selection,
                direct_count=direct_count,
                packed_count=packed_count,
                total_count=total_count,
                packed_table_bytes=packed_table_bytes,
                stream_base=stream_base,
                time=time,
            )
        except (ValueError, IndexError, struct.error) as exc:
            last_error = exc
    raise ValueError(f"could not decode ANM2 frame with engine-aligned or legacy base layout: {last_error}")


def _candidate_stream_bases(base_offset: int) -> list[int]:
    return [anm2_base_table_start(base_offset)]


def _decode_frame_with_stream_base(
    *,
    data: bytes,
    header: anm2.Anm2Header,
    page_offset: int,
    table: list[int],
    selection: anm2.Anm2TimeSelection,
    direct_count: int,
    packed_count: int,
    total_count: int,
    packed_table_bytes: int,
    stream_base: int,
    time: float,
) -> DecodedFrame:
    direct_offset = (stream_base + packed_table_bytes + 15) & ~0xF
    mask_offset = (direct_offset + 4 * direct_count + 3) & ~0x3
    direct_values = list(struct.unpack_from(f"<{direct_count}f", data, direct_offset)) if direct_count else []
    masks = data[mask_offset : mask_offset + header.track_count]
    refs = _component_refs(masks)
    if len(refs) != total_count:
        raise ValueError(f"mask component refs {len(refs)} != total component count {total_count}")

    stream_start = page_offset + 16 * table[selection.table_index]
    stream_end = page_offset + 16 * table[selection.table_index + 1]
    packed_frames = _decode_packed_frames(
        data=data,
        stream_start=stream_start,
        stream_end=stream_end,
        stream_base=stream_base,
        packed_count=packed_count,
        current_frame=selection.in_segment_frame,
    )
    tracks: list[tuple[float, ...]] = []
    component_sources: list[tuple[ComponentSampleInfo, ...]] = []
    for track_index in range(header.track_count):
        components: list[float] = []
        source_rows: list[ComponentSampleInfo] = []
        for component_index in range(9):
            ref = refs[track_index * 9 + component_index]
            if ref.source == "direct":
                value = direct_values[ref.source_index]
                current_value = float(value)
                next_value = float(value)
            else:
                current = packed_frames[selection.in_segment_frame][ref.source_index]
                next_value = packed_frames[min(selection.in_segment_frame + 1, 15)][ref.source_index]
                value = current * (1.0 - selection.fraction) + next_value * selection.fraction
                current_value = float(current)
                next_value = float(next_value)
            if not math.isfinite(value):
                raise ValueError("decoded component is not finite")
            components.append(float(value))
            _, group, axis = COMPONENT_SLOTS[component_index]
            source_rows.append(
                ComponentSampleInfo(
                    component=_component_name(component_index),
                    component_index=component_index,
                    group=group,
                    axis=axis,
                    source=ref.source,
                    source_index=ref.source_index,
                    mask_byte=int(masks[track_index]) if track_index < len(masks) else 0,
                    page_index=selection.page_index,
                    table_index=selection.table_index,
                    in_segment_frame=selection.in_segment_frame,
                    fraction=float(selection.fraction),
                    current_value=current_value,
                    next_value=next_value,
                    value=float(value),
                )
            )
        tracks.append(tuple(components))
        component_sources.append(tuple(source_rows))

    return DecodedFrame(
        requested_time=float(time),
        page_index=selection.page_index,
        table_index=selection.table_index,
        in_segment_frame=selection.in_segment_frame,
        fraction=selection.fraction,
        tracks=tuple(tracks),
        component_sources=tuple(component_sources),
    )


def _component_name(component_index: int) -> str:
    return ("rx", "ry", "rz", "tx", "ty", "tz", "sx", "sy", "sz")[component_index]


def _decode_packed_frames(
    *,
    data: bytes,
    stream_start: int,
    stream_end: int,
    stream_base: int,
    packed_count: int,
    current_frame: int,
) -> list[list[float]]:
    frames = [[0.0 for _ in range(packed_count)] for _ in range(16)]
    if packed_count == 0:
        return frames
    cursor = stream_start
    group_count = (packed_count + 7) // 8
    for group_index in range(group_count):
        group_payload = data[cursor:stream_end]
        length = packed_group_length(group_payload)
        values = decode_group_8(group_payload[:length], max_frame=min(15, current_frame + 1))
        bias_offset = stream_base + group_index * 64
        scale_offset = bias_offset + 32
        biases = struct.unpack_from("<8f", data, bias_offset)
        scales = struct.unpack_from("<8f", data, scale_offset)
        for frame_index, frame_values in enumerate(values):
            for lane, raw in enumerate(frame_values):
                packed_index = group_index * 8 + lane
                if packed_index < packed_count:
                    frames[frame_index][packed_index] = biases[lane] + raw * scales[lane]
        cursor += length
    if cursor != stream_end:
        raise ValueError(f"packed stream decode left {stream_end - cursor} trailing byte(s)")
    return frames


def _component_refs(masks: bytes) -> list[ComponentRef]:
    refs: list[ComponentRef] = []
    direct_index = 0
    packed_index = 0
    for track_index, mask in enumerate(masks):
        for component_index, (bit, group, axis) in enumerate(COMPONENT_SLOTS):
            if mask & bit:
                refs.append(ComponentRef(track_index, component_index, group, axis, "direct", direct_index))
                direct_index += 1
            else:
                refs.append(ComponentRef(track_index, component_index, group, axis, "packed", packed_index))
                packed_index += 1
    return refs


def _header_side_tables(header: anm2.Anm2Header, data: bytes) -> tuple[list[int], list[int]]:
    page_spans_offset = anm2.HEADER_LENGTH + 4 * header.track_count
    page_spans = list(struct.unpack_from(f"<{header.unknown12}H", data, page_spans_offset))
    duration_count = header.unknown20 & 0xFFFF
    duration_offset = page_spans_offset + 2 * header.unknown12
    duration_word_count = 1 + 2 * duration_count if duration_count else 0
    duration_words = list(struct.unpack_from(f"<{duration_word_count}H", data, duration_offset))
    return page_spans, duration_words


def _read_page_table(data: bytes, page_offset: int) -> list[int]:
    first_word = struct.unpack_from("<H", data, page_offset)[0]
    word_count = max(16, first_word * 8)
    words = list(struct.unpack_from(f"<{word_count}H", data, page_offset))
    table: list[int] = []
    for word in words:
        if word == 0:
            break
        table.append(word)
    if len(table) < 2:
        raise ValueError("v1 page table has fewer than base and end offsets")
    return table
