from __future__ import annotations

import math
import struct
from dataclasses import dataclass
from pathlib import Path
from typing import Callable, Iterable

import numpy as np

from . import anm2
from .anm2_base_segment import anm2_base_table_start
from .anm2_packed import decode_group_8, packed_group_length
from .dl2_anm2 import detect_anm2_format, parse_anm2_v2_layout, select_v2_time


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
    header_version: int = 1
    container: str = "dl1_header_version_1"


@dataclass(frozen=True, slots=True)
class DecodedAllFrames:
    """Array-oriented result from the cached contiguous decoder."""

    frame_count: int
    track_count: int
    descriptors: tuple[int, ...]
    values: np.ndarray
    source_track_indices: tuple[int, ...]
    header_version: int = 1
    container: str = "dl1_header_version_1"
    container_track_count: int = 0
    container_descriptors: tuple[int, ...] = ()
    unique_packed_slots_decoded: int = 0
    prepared_base_segment_count: int = 0
    signature: int = 42
    static_stream_count: int = 0
    packed_stream_count: int = 0
    block_count: int = 0
    block_frame_spans: tuple[int, ...] = ()
    vfr_words: tuple[int, ...] = ()


@dataclass(frozen=True, slots=True)
class _PreparedSamplerBase:
    direct_values: np.ndarray
    direct_indices: np.ndarray
    packed_indices: np.ndarray
    packed_count: int
    stream_base: int


@dataclass(frozen=True, slots=True)
class SamplerSelection:
    """A format-neutral, fully resolved view of one common sampler call."""

    descriptors: tuple[int, ...]
    track_count: int
    base_segment_offset: int
    stream_start: int
    stream_end: int
    frame_in_slot: int
    fraction: float
    block_index: int
    table_index: int
    requested_time: float
    base_segment_size: int | None = None

    @property
    def page_index(self) -> int:
        return self.block_index

    @property
    def in_segment_frame(self) -> int:
        return self.frame_in_slot


def decode_file_samples(path: str | Path, times: list[float]) -> DecodedClipSample:
    return decode_samples(Path(path).read_bytes(), times)


def decode_samples(data: bytes, times: list[float]) -> DecodedClipSample:
    """Decode samples after dispatching on the actual header version."""

    detected = detect_anm2_format(data)
    if detected == 1:
        return decode_v1_samples(data, times)
    if detected == 42:
        return decode_v2_samples(data, times)
    raise ValueError(f"unsupported ANM2 container {detected}")


def decode_all_frames_cached(
    data: bytes,
    selected_descriptors: Iterable[int] | None = None,
    *,
    progress: Callable[[str, int, int], None] | None = None,
    cancel_check: Callable[[], bool] | None = None,
) -> DecodedAllFrames:
    """Decode every contiguous frame while caching each packed slot once.

    The random-time :func:`decode_samples` path intentionally remains intact
    for probes and audits.  Bulk FBX export uses this array-oriented path so
    base tables are parsed once, only selected tracks are assembled, and the
    16-frame packed payload behind each page/segment entry is decoded once.
    """

    detected = detect_anm2_format(data)
    selections: list[SamplerSelection] = []
    if detected == 1:
        header = anm2.Anm2Header.parse(data)
        if header.unknown06 != 1:
            raise ValueError(
                f"expected Header_Version1, found header version {header.unknown06}"
            )
        descriptors = tuple(
            struct.unpack_from(f"<{header.track_count}I", data, anm2.HEADER_LENGTH)
        )
        page_spans, duration_words = _header_side_tables(header, data)
        page_tables = [
            _read_page_table(data, header.unknown14 + anm2.PAGE_SIZE * index)
            for index in range(header.unknown12)
        ]
        for frame_index in range(header.frame_count):
            selected = anm2.select_v1_time(
                page_offset=header.unknown14,
                page_spans=page_spans,
                duration_words=duration_words,
                page_tables=page_tables,
                time=float(frame_index),
            )
            if selected is None:
                raise ValueError(f"could not select v1 ANM2 frame {frame_index}")
            page_offset = header.unknown14 + anm2.PAGE_SIZE * selected.page_index
            table = page_tables[selected.page_index]
            if selected.table_index + 1 >= len(table):
                raise ValueError(
                    f"selected table index {selected.table_index} has no end offset"
                )
            selections.append(
                SamplerSelection(
                    descriptors=descriptors,
                    track_count=header.track_count,
                    base_segment_offset=page_offset + 16 * table[0],
                    base_segment_size=(table[1] - table[0]) * 16,
                    stream_start=page_offset + 16 * table[selected.table_index],
                    stream_end=page_offset + 16 * table[selected.table_index + 1],
                    frame_in_slot=selected.in_segment_frame,
                    fraction=selected.fraction,
                    block_index=selected.page_index,
                    table_index=selected.table_index,
                    requested_time=float(frame_index),
                )
            )
        frame_count = header.frame_count
        header_version = 1
        container = "dl1_header_version_1"
        signature = 42
        static_stream_count = 0
        packed_stream_count = 0
        block_count = int(header.unknown12)
        block_frame_spans = tuple(int(value) for value in page_spans)
        vfr_words: tuple[int, ...] = tuple(int(value) for value in duration_words)
    elif detected == 42:
        layout = parse_anm2_v2_layout(data).require_valid()
        descriptors = layout.descriptors
        for frame_index in range(layout.frame_count):
            outer = select_v2_time(layout, float(frame_index))
            block = layout.blocks[outer.block_index]
            stream_start, stream_end = block.stream_bounds(outer.page_table_index)
            selections.append(
                SamplerSelection(
                    descriptors=descriptors,
                    track_count=layout.track_count,
                    base_segment_offset=block.base_segment_offset,
                    base_segment_size=block.base_segment_size,
                    stream_start=stream_start,
                    stream_end=stream_end,
                    frame_in_slot=outer.frame_in_15_frame_slot,
                    fraction=outer.interpolation_fraction,
                    block_index=outer.block_index,
                    table_index=outer.page_table_index,
                    requested_time=float(frame_index),
                )
            )
        frame_count = layout.frame_count
        header_version = 2
        container = layout.container
        signature = int(getattr(layout, "signature", 42))
        static_stream_count = int(layout.static_stream_count)
        packed_stream_count = int(layout.packed_stream_count)
        block_count = int(
            getattr(
                layout,
                "block_count",
                getattr(layout, "payload_block_count", len(layout.blocks)),
            )
        )
        block_frame_spans = tuple(int(value) for value in layout.block_frame_spans)
        vfr_words = tuple(int(value) for value in layout.vfr_words)
    else:
        raise ValueError(f"unsupported ANM2 container {detected}")

    if selected_descriptors is None:
        source_track_indices = tuple(range(len(descriptors)))
    else:
        requested = tuple(int(value) for value in selected_descriptors)
        if len(set(requested)) != len(requested):
            raise ValueError("selected ANM2 descriptors must be unique")
        descriptor_set = set(requested)
        # Preserve source descriptor-table order even if a caller supplies a
        # set or a differently ordered inventory. Requested rig descriptors
        # absent from this clip are ignored so those skeleton rows can remain
        # at bind without allocating animation curves.
        source_track_indices = tuple(
            index for index, value in enumerate(descriptors) if value in descriptor_set
        )
    selected = tuple(descriptors[index] for index in source_track_indices)
    values = np.empty((frame_count, len(selected), 9), dtype=np.float64)
    prepared_cache: dict[int, _PreparedSamplerBase] = {}
    slot_cache: dict[tuple[int, int, int], np.ndarray] = {}

    for frame_index, selection in enumerate(selections):
        if cancel_check is not None and cancel_check():
            raise RuntimeError("ANM2 decoding was cancelled.")
        prepared = prepared_cache.get(selection.base_segment_offset)
        if prepared is None:
            prepared = _prepare_sampler_base(
                data, selection, source_track_indices=source_track_indices
            )
            prepared_cache[selection.base_segment_offset] = prepared
        slot_key = (
            selection.base_segment_offset,
            selection.stream_start,
            selection.stream_end,
        )
        packed = slot_cache.get(slot_key)
        if packed is None:
            packed = _decode_packed_slot_cached(
                data=data,
                stream_start=selection.stream_start,
                stream_end=selection.stream_end,
                stream_base=prepared.stream_base,
                packed_count=prepared.packed_count,
            )
            slot_cache[slot_key] = packed
        direct_mask = prepared.direct_indices >= 0
        packed_mask = ~direct_mask
        frame_values = values[frame_index]
        if np.any(direct_mask):
            frame_values[direct_mask] = prepared.direct_values[
                prepared.direct_indices[direct_mask]
            ]
        if np.any(packed_mask):
            current = packed[
                selection.frame_in_slot, prepared.packed_indices[packed_mask]
            ]
            following = packed[
                min(selection.frame_in_slot + 1, 15),
                prepared.packed_indices[packed_mask],
            ]
            fraction = float(selection.fraction)
            frame_values[packed_mask] = current * (1.0 - fraction) + following * fraction
        if not np.isfinite(frame_values).all():
            raise ValueError(f"decoded frame {frame_index} contains a non-finite component")
        if progress is not None and (
            frame_index == 0
            or frame_index + 1 == frame_count
            or (frame_index + 1) % 32 == 0
        ):
            progress("Decoding pages/segments", frame_index + 1, frame_count)

    return DecodedAllFrames(
        frame_count=frame_count,
        track_count=len(selected),
        descriptors=selected,
        values=values,
        source_track_indices=source_track_indices,
        header_version=header_version,
        container=container,
        container_track_count=len(descriptors),
        container_descriptors=tuple(descriptors),
        unique_packed_slots_decoded=len(slot_cache),
        prepared_base_segment_count=len(prepared_cache),
        signature=signature,
        static_stream_count=static_stream_count,
        packed_stream_count=packed_stream_count,
        block_count=block_count,
        block_frame_spans=block_frame_spans,
        vfr_words=vfr_words,
    )


def _prepare_sampler_base(
    data: bytes,
    selection: SamplerSelection,
    *,
    source_track_indices: tuple[int, ...],
) -> _PreparedSamplerBase:
    base_header = struct.unpack_from("<8H", data, selection.base_segment_offset)
    direct_count, packed_count, total_count, packed_table_bytes = base_header[:4]
    if total_count != 9 * selection.track_count:
        raise ValueError(
            f"component count {total_count} does not match track count {selection.track_count}"
        )
    if direct_count + packed_count != total_count:
        raise ValueError("direct and packed component counts do not equal the total")
    stream_base = anm2_base_table_start(selection.base_segment_offset)
    direct_offset = (stream_base + packed_table_bytes + 15) & ~0xF
    mask_offset = (direct_offset + 4 * direct_count + 3) & ~0x3
    if selection.base_segment_size is not None:
        base_end = selection.base_segment_offset + selection.base_segment_size
        if mask_offset + selection.track_count > base_end:
            raise ValueError("sampler calibration/direct/mask tables exceed the base segment")
    direct_values = (
        np.frombuffer(data, dtype="<f4", count=direct_count, offset=direct_offset).astype(
            np.float64
        )
        if direct_count
        else np.empty((0,), dtype=np.float64)
    )
    masks = data[mask_offset : mask_offset + selection.track_count]
    if len(masks) != selection.track_count:
        raise ValueError("sampler mask table is truncated")
    refs = _component_refs(masks)
    direct_indices = np.full((len(source_track_indices), 9), -1, dtype=np.int32)
    packed_indices = np.full((len(source_track_indices), 9), -1, dtype=np.int32)
    for selected_index, source_index in enumerate(source_track_indices):
        for component_index in range(9):
            ref = refs[source_index * 9 + component_index]
            if ref.source == "direct":
                direct_indices[selected_index, component_index] = ref.source_index
            else:
                packed_indices[selected_index, component_index] = ref.source_index
    return _PreparedSamplerBase(
        direct_values=direct_values,
        direct_indices=direct_indices,
        packed_indices=packed_indices,
        packed_count=packed_count,
        stream_base=stream_base,
    )


def _decode_packed_slot_cached(
    *,
    data: bytes,
    stream_start: int,
    stream_end: int,
    stream_base: int,
    packed_count: int,
) -> np.ndarray:
    """Decode a complete 16-frame packed slot; one call equals one cache miss."""

    frames = np.zeros((16, packed_count), dtype=np.float64)
    if packed_count == 0:
        return frames
    cursor = stream_start
    for group_index in range((packed_count + 7) // 8):
        payload = data[cursor:stream_end]
        length = packed_group_length(payload)
        raw = np.asarray(decode_group_8(payload[:length], max_frame=15), dtype=np.float64)
        bias_offset = stream_base + group_index * 64
        biases = np.asarray(struct.unpack_from("<8f", data, bias_offset), dtype=np.float64)
        scales = np.asarray(
            struct.unpack_from("<8f", data, bias_offset + 32), dtype=np.float64
        )
        lane_count = min(8, packed_count - group_index * 8)
        frames[:, group_index * 8 : group_index * 8 + lane_count] = (
            biases[:lane_count] + raw[:, :lane_count] * scales[:lane_count]
        )
        cursor += length
    if cursor != stream_end:
        raise ValueError(
            f"packed stream decode left {stream_end - cursor} trailing byte(s)"
        )
    return frames


def decode_v1_samples(data: bytes, times: list[float]) -> DecodedClipSample:
    header = anm2.Anm2Header.parse(data)
    if header.unknown06 != 1:
        raise ValueError(f"expected Header_Version1, found header version {header.unknown06}")
    descriptors = tuple(struct.unpack_from(f"<{header.track_count}I", data, anm2.HEADER_LENGTH))
    page_spans, duration_words = _header_side_tables(header, data)
    page_tables = [_read_page_table(data, header.unknown14 + anm2.PAGE_SIZE * index) for index in range(header.unknown12)]
    frames = tuple(
        _decode_v1_frame(
            data=data,
            header=header,
            descriptors=descriptors,
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
        header_version=1,
        container="dl1_header_version_1",
    )


def decode_v2_samples(data: bytes, times: list[float]) -> DecodedClipSample:
    layout = parse_anm2_v2_layout(data).require_valid()
    frames: list[DecodedFrame] = []
    for time in times:
        outer = select_v2_time(layout, time)
        block = layout.blocks[outer.block_index]
        stream_start, stream_end = block.stream_bounds(outer.page_table_index)
        frames.append(
            decode_selected_sampler_frame(
                data,
                SamplerSelection(
                    descriptors=layout.descriptors,
                    track_count=layout.track_count,
                    base_segment_offset=block.base_segment_offset,
                    base_segment_size=block.base_segment_size,
                    stream_start=stream_start,
                    stream_end=stream_end,
                    frame_in_slot=outer.frame_in_15_frame_slot,
                    fraction=outer.interpolation_fraction,
                    block_index=outer.block_index,
                    table_index=outer.page_table_index,
                    requested_time=float(time),
                ),
            )
        )
    return DecodedClipSample(
        frame_count=layout.frame_count,
        track_count=layout.track_count,
        descriptors=layout.descriptors,
        frames=tuple(frames),
        header_version=2,
        container=layout.container,
    )


def max_frame_component_delta(left: DecodedFrame, right: DecodedFrame) -> float:
    maximum = 0.0
    for left_track, right_track in zip(left.tracks, right.tracks):
        for left_value, right_value in zip(left_track, right_track):
            maximum = max(maximum, abs(right_value - left_value))
    return maximum


def _decode_v1_frame(
    *,
    data: bytes,
    header: anm2.Anm2Header,
    descriptors: tuple[int, ...],
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
    stream_start = page_offset + 16 * table[selection.table_index]
    stream_end = page_offset + 16 * table[selection.table_index + 1]
    return decode_selected_sampler_frame(
        data,
        SamplerSelection(
            descriptors=descriptors,
            track_count=header.track_count,
            base_segment_offset=base_offset,
            base_segment_size=(table[1] - table[0]) * 16,
            stream_start=stream_start,
            stream_end=stream_end,
            frame_in_slot=selection.in_segment_frame,
            fraction=selection.fraction,
            block_index=selection.page_index,
            table_index=selection.table_index,
            requested_time=float(time),
        ),
    )


def _candidate_stream_bases(base_offset: int) -> list[int]:
    return [anm2_base_table_start(base_offset)]


def decode_selected_sampler_frame(data: bytes, selection: SamplerSelection) -> DecodedFrame:
    """Decode one resolved v1/v2 sampler stream with the protected common codec."""

    if len(selection.descriptors) != selection.track_count:
        raise ValueError(
            f"descriptor count {len(selection.descriptors)} does not match track count {selection.track_count}"
        )
    if selection.track_count <= 0:
        raise ValueError("sampler track count must be positive")
    if not 0 <= selection.frame_in_slot <= 14:
        raise ValueError("sampler frame-in-slot must be in 0..14")
    if not 0.0 <= selection.fraction <= 1.0:
        raise ValueError("sampler interpolation fraction must be in 0..1")
    if not 0 <= selection.base_segment_offset <= len(data) - 16:
        raise ValueError("sampler base segment header is outside the file")
    if not 0 <= selection.stream_start < selection.stream_end <= len(data):
        raise ValueError("sampler packed stream bounds are outside the file")

    base_header = struct.unpack_from("<8H", data, selection.base_segment_offset)
    direct_count, packed_count, total_count, packed_table_bytes = base_header[:4]
    if total_count != 9 * selection.track_count:
        raise ValueError(
            f"component count {total_count} does not match track count {selection.track_count}"
        )
    if direct_count + packed_count != total_count:
        raise ValueError("direct and packed component counts do not equal the total component count")
    if packed_table_bytes < ((packed_count + 7) // 8) * 64:
        raise ValueError("packed calibration table is shorter than packed component groups")

    last_error: Exception | None = None
    for stream_base in _candidate_stream_bases(selection.base_segment_offset):
        try:
            return _decode_frame_with_stream_base(
                data=data,
                selection=selection,
                direct_count=direct_count,
                packed_count=packed_count,
                total_count=total_count,
                packed_table_bytes=packed_table_bytes,
                stream_base=stream_base,
            )
        except (ValueError, IndexError, struct.error) as exc:
            last_error = exc
    raise ValueError(f"could not decode common ANM2 sampler frame: {last_error}")


def _decode_frame_with_stream_base(
    *,
    data: bytes,
    selection: SamplerSelection,
    direct_count: int,
    packed_count: int,
    total_count: int,
    packed_table_bytes: int,
    stream_base: int,
) -> DecodedFrame:
    direct_offset = (stream_base + packed_table_bytes + 15) & ~0xF
    mask_offset = (direct_offset + 4 * direct_count + 3) & ~0x3
    if selection.base_segment_size is not None:
        base_end = selection.base_segment_offset + selection.base_segment_size
        if mask_offset + selection.track_count > base_end:
            raise ValueError("sampler calibration/direct/mask tables exceed the base segment")
    direct_values = list(struct.unpack_from(f"<{direct_count}f", data, direct_offset)) if direct_count else []
    masks = data[mask_offset : mask_offset + selection.track_count]
    if len(masks) != selection.track_count:
        raise ValueError("sampler mask table is truncated")
    refs = _component_refs(masks)
    if len(refs) != total_count:
        raise ValueError(f"mask component refs {len(refs)} != total component count {total_count}")
    actual_direct_count = sum(ref.source == "direct" for ref in refs)
    actual_packed_count = len(refs) - actual_direct_count
    if actual_direct_count != direct_count or actual_packed_count != packed_count:
        raise ValueError(
            "sampler mask direct/packed counts do not match the base segment header"
        )

    packed_frames = _decode_packed_frames(
        data=data,
        stream_start=selection.stream_start,
        stream_end=selection.stream_end,
        stream_base=stream_base,
        packed_count=packed_count,
        current_frame=selection.frame_in_slot,
    )
    tracks: list[tuple[float, ...]] = []
    component_sources: list[tuple[ComponentSampleInfo, ...]] = []
    for track_index in range(selection.track_count):
        components: list[float] = []
        source_rows: list[ComponentSampleInfo] = []
        for component_index in range(9):
            ref = refs[track_index * 9 + component_index]
            if ref.source == "direct":
                value = direct_values[ref.source_index]
                current_value = float(value)
                next_value = float(value)
            else:
                current = packed_frames[selection.frame_in_slot][ref.source_index]
                next_value = packed_frames[min(selection.frame_in_slot + 1, 15)][ref.source_index]
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
                    page_index=selection.block_index,
                    table_index=selection.table_index,
                    in_segment_frame=selection.frame_in_slot,
                    fraction=float(selection.fraction),
                    current_value=current_value,
                    next_value=next_value,
                    value=float(value),
                )
            )
        tracks.append(tuple(components))
        component_sources.append(tuple(source_rows))

    return DecodedFrame(
        requested_time=selection.requested_time,
        page_index=selection.block_index,
        table_index=selection.table_index,
        in_segment_frame=selection.frame_in_slot,
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
