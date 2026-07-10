from __future__ import annotations

import hashlib
import json
import math
import struct
from dataclasses import asdict, dataclass, field
from pathlib import Path
from typing import Any


MAGIC = b"ANM2"
FORMAT_VERSION = 42
HEADER_LENGTH = 32
HEADER_STRUCT = struct.Struct("<4sHHHHHHIIII")
PAGE_SIZE = 0x10000
SEMANTIC_CODEC_READY = False


@dataclass(frozen=True, slots=True)
class Anm2Header:
    format_version: int
    unknown06: int
    frame_count: int
    track_count: int
    unknown12: int
    unknown14: int
    declared_length: int
    unknown20: int
    unknown24: int
    unknown28: int

    @classmethod
    def parse(cls, data: bytes) -> "Anm2Header":
        if len(data) < HEADER_LENGTH:
            raise ValueError("ANM2 payload is smaller than the 32-byte header.")
        magic, fmt, u06, frames, tracks, u12, u14, declared, u20, u24, u28 = HEADER_STRUCT.unpack_from(data, 0)
        if magic != MAGIC:
            found = magic.decode("ascii", errors="replace")
            raise ValueError(f"Expected ANM2 magic, found {found!r}.")
        return cls(
            format_version=fmt,
            unknown06=u06,
            frame_count=frames,
            track_count=tracks,
            unknown12=u12,
            unknown14=u14,
            declared_length=declared,
            unknown20=u20,
            unknown24=u24,
            unknown28=u28,
        )

    def to_bytes(self, total_length: int | None = None) -> bytes:
        declared = self.declared_length if total_length is None else total_length
        return HEADER_STRUCT.pack(
            MAGIC,
            self.format_version,
            self.unknown06,
            self.frame_count,
            self.track_count,
            self.unknown12,
            self.unknown14,
            declared,
            self.unknown20,
            self.unknown24,
            self.unknown28,
        )

    def signature(self) -> tuple[int, int, int, int, int, int, int]:
        return (
            self.format_version,
            self.unknown06,
            self.unknown12,
            self.unknown14,
            self.unknown20,
            self.unknown24,
            self.unknown28,
        )

    def length_candidates(self) -> dict[int, int]:
        return {
            16: self.declared_length,
            24: self.unknown24,
            28: self.unknown28,
        }

    def declared_length_matches(self, actual_length: int) -> bool:
        return actual_length in self.length_candidates().values()

    def declared_length_offset(self, actual_length: int) -> int | None:
        for offset, value in self.length_candidates().items():
            if value == actual_length:
                return offset
        return None

    def diagnostics(self, actual_length: int) -> dict[str, Any]:
        return {
            **asdict(self),
            "actual_length": actual_length,
            "body_length": max(0, actual_length - HEADER_LENGTH),
            "declared_length_matches": self.declared_length_matches(actual_length),
            "declared_length_offset": self.declared_length_offset(actual_length),
            "length_candidates": self.length_candidates(),
            "expected_format": self.format_version == FORMAT_VERSION,
        }


@dataclass(slots=True)
class BodyProbe:
    body_length: int
    first_32_bytes_hex: str
    possible_u16_table_offsets: list[int] = field(default_factory=list)
    repeated_float_markers: dict[str, int] = field(default_factory=dict)
    notes: list[str] = field(default_factory=list)


@dataclass(frozen=True, slots=True)
class Anm2SegmentProbe:
    segment_index: int
    segment_word_offset: int
    file_offset: int
    byte_length: int | None
    header_words: list[int]
    computed_track_groups: int | None
    mask_groups: int | None
    flags_or_stream_offset: int | None
    stream_base_offset: int | None = None
    direct_values_offset: int | None = None
    mask_table_offset: int | None = None
    mask_table_bytes: int | None = None
    mask_value_counts: dict[str, int] = field(default_factory=dict)


@dataclass(frozen=True, slots=True)
class Anm2StreamSlotProbe:
    table_index: int
    stream_word_offset: int
    file_offset: int
    byte_length: int | None
    first_16_bytes_hex: str
    looks_like_segment_header: bool


@dataclass(frozen=True, slots=True)
class Anm2PageProbe:
    page_index: int
    file_offset: int
    frame_span: int
    table_words: list[int]
    segment_count: int
    segments: list[Anm2SegmentProbe]
    base_segment: Anm2SegmentProbe | None = None
    engine_stream_slots: list[Anm2StreamSlotProbe] = field(default_factory=list)
    engine_playable_slot_count: int = 0


@dataclass(frozen=True, slots=True)
class Anm2TimeSelection:
    requested_time: float
    evaluated_frame: float
    adjusted_frame: int
    page_index: int
    table_index: int
    in_segment_frame: int
    fraction: float
    stream_word_offset: int | None
    stream_file_offset: int | None


@dataclass(frozen=True, slots=True)
class Anm2V1Layout:
    version: int
    page_count: int
    page_offset: int
    duration_key_count: int
    track_table_file_offset: int
    track_table_bytes: int
    track_descriptors_preview: list[int]
    page_frame_spans: list[int]
    duration_words: list[int]
    duration_total_frames: float | None
    pages: list[Anm2PageProbe]
    validation_errors: list[str]
    sample_time_selections: list[Anm2TimeSelection] = field(default_factory=list)

    @property
    def is_engine_header_valid(self) -> bool:
        return not self.validation_errors


@dataclass(slots=True)
class Anm2Clip:
    name: str
    header: Anm2Header
    body: bytes = field(repr=False)
    sha256: str
    semantic_status: str = "body_preserved_only"
    warnings: list[str] = field(default_factory=list)
    original_bytes: bytes = field(default=b"", repr=False)

    @property
    def total_length(self) -> int:
        return HEADER_LENGTH + len(self.body)

    def diagnostics(self) -> dict[str, Any]:
        layout = probe_v1_layout(self.header, self.original_bytes or self.header.to_bytes(self.total_length) + self.body)
        return {
            "name": self.name,
            "sha256": self.sha256,
            "header": self.header.diagnostics(self.total_length),
            "semantic_status": self.semantic_status,
            "warnings": self.warnings,
            "body_probe": asdict(probe_body(self.header, self.body)),
            "v1_layout": asdict(layout) if layout else None,
        }


def decode(data: bytes, name: str = "") -> Anm2Clip:
    header = Anm2Header.parse(data)
    body = data[HEADER_LENGTH:]
    warnings: list[str] = []
    if not header.declared_length_matches(len(data)):
        warnings.append(f"No known length slot matches actual length {len(data)}.")
    elif header.declared_length != len(data):
        warnings.append(
            f"File length is stored at header offset {header.declared_length_offset(len(data))}, not offset 16."
        )
    if header.format_version != FORMAT_VERSION:
        warnings.append(f"Unexpected ANM2 format version {header.format_version}; known stock files use 42.")
    if not SEMANTIC_CODEC_READY:
        warnings.append("Packed curve body is preserved only; semantic TRS decode/encode is not proven yet.")
    return Anm2Clip(
        name=name,
        header=header,
        body=body,
        sha256=hashlib.sha256(data).hexdigest().upper(),
        warnings=warnings,
        original_bytes=data,
    )


def decode_file(path: str | Path) -> Anm2Clip:
    file_path = Path(path)
    return decode(file_path.read_bytes(), file_path.name)


def encode_preserving_body(clip: Anm2Clip) -> bytes:
    if clip.original_bytes:
        return clip.original_bytes
    total_length = HEADER_LENGTH + len(clip.body)
    return clip.header.to_bytes(total_length=total_length) + clip.body


def round_trip_report(path: str | Path) -> dict[str, Any]:
    clip = decode_file(path)
    encoded = encode_preserving_body(clip)
    return {
        "name": clip.name,
        "byte_identical": encoded == clip.original_bytes,
        "frame_count_matches": clip.header.frame_count >= 0,
        "track_count_matches": clip.header.track_count >= 0,
        "curve_codec_decoded": SEMANTIC_CODEC_READY,
        "bone_names_resolved": False,
        "max_numeric_error": None,
        "diagnostics": clip.diagnostics(),
    }


def evaluate_duration_words(words: list[int]) -> float | None:
    if not words:
        return None
    scale = words[0]
    if scale == 0:
        return None
    total = 0.0
    pairs = words[1:]
    for index in range(0, len(pairs) - 1, 2):
        total += (pairs[index] / scale) * (pairs[index + 1] / scale)
    return total


def probe_v1_layout(header: Anm2Header, data: bytes) -> Anm2V1Layout | None:
    if header.unknown06 != 1:
        return None

    validation_errors: list[str] = []
    actual_length = len(data)
    page_count = header.unknown12
    page_offset = header.unknown14
    duration_key_count = header.unknown20 & 0xFFFF
    track_table_offset = HEADER_LENGTH
    track_table_bytes = 4 * header.track_count
    page_spans_offset = track_table_offset + track_table_bytes
    duration_offset = page_spans_offset + 2 * page_count
    duration_word_count = 1 + 2 * duration_key_count if duration_key_count else 0
    expected_page_count = (header.declared_length - page_offset + PAGE_SIZE - 1) // PAGE_SIZE if header.declared_length >= page_offset else None

    if header.format_version != FORMAT_VERSION:
        validation_errors.append(f"format_version {header.format_version} != {FORMAT_VERSION}")
    if header.frame_count == 0:
        validation_errors.append("frame_count is zero")
    if header.track_count == 0:
        validation_errors.append("track_count is zero")
    if page_count == 0:
        validation_errors.append("page_count is zero")
    if expected_page_count is not None and page_count != expected_page_count:
        validation_errors.append(f"page_count {page_count} != ceil((declared_length - page_offset) / 65536) {expected_page_count}")
    if page_offset < duration_offset + 2 * duration_word_count:
        validation_errors.append("page_offset overlaps the header-side track/page/duration tables")
    if header.declared_length > actual_length:
        validation_errors.append(f"declared_length {header.declared_length} exceeds actual length {actual_length}")
    if actual_length < min(header.declared_length, page_offset):
        validation_errors.append("file is shorter than the declared page offset")

    track_preview: list[int] = []
    if actual_length >= track_table_offset + min(track_table_bytes, 32):
        preview_bytes = min(track_table_bytes, 32)
        track_preview = list(struct.unpack_from(f"<{preview_bytes // 4}I", data, track_table_offset))

    page_spans: list[int] = []
    if actual_length >= page_spans_offset + 2 * page_count:
        page_spans = list(struct.unpack_from(f"<{page_count}H", data, page_spans_offset))
        if sum(page_spans) < max(0, header.frame_count - 1):
            validation_errors.append("page frame spans do not cover frame_count - 1")
    else:
        validation_errors.append("file is shorter than the page frame span table")

    duration_words: list[int] = []
    duration_total = None
    if duration_word_count:
        if actual_length >= duration_offset + 2 * duration_word_count:
            duration_words = list(struct.unpack_from(f"<{duration_word_count}H", data, duration_offset))
            duration_total = evaluate_duration_words(duration_words)
            expected_duration = max(0, header.frame_count - 1)
            if duration_total is not None and duration_total != float(expected_duration):
                validation_errors.append(f"duration curve evaluates to {duration_total}, expected {expected_duration}")
        else:
            validation_errors.append("file is shorter than the duration curve words")
    else:
        validation_errors.append("duration/control key count is zero")

    pages: list[Anm2PageProbe] = []
    for page_index in range(page_count):
        file_offset = page_offset + PAGE_SIZE * page_index
        if file_offset + 32 > actual_length:
            validation_errors.append(f"page {page_index} starts past the available data")
            continue
        first_word = struct.unpack_from("<H", data, file_offset)[0]
        table_byte_count = max(32, first_word * 16)
        table_byte_count = min(table_byte_count, max(0, actual_length - file_offset))
        table_word_count = table_byte_count // 2
        table_words = list(struct.unpack_from(f"<{table_word_count}H", data, file_offset))
        segment_offsets = _positive_increasing_words(table_words)
        base_segment = _probe_segment(data, file_offset, segment_offsets, 0) if segment_offsets else None
        engine_stream_slots = [
            _probe_stream_slot(data, file_offset, segment_offsets, table_index)
            for table_index in range(1, max(1, len(segment_offsets) - 1))
        ]
        segments = [
            _probe_segment(data, file_offset, segment_offsets, segment_index)
            for segment_index in range(min(len(segment_offsets), 4))
        ]
        pages.append(
            Anm2PageProbe(
                page_index=page_index,
                file_offset=file_offset,
                frame_span=page_spans[page_index] if page_index < len(page_spans) else 0,
                table_words=table_words[:16],
                segment_count=max(0, len(segment_offsets) - 1),
                segments=segments,
                base_segment=base_segment,
                engine_stream_slots=engine_stream_slots[:4],
                engine_playable_slot_count=max(0, len(segment_offsets) - 2),
            )
        )

    sample_time_selections = _sample_time_selections(
        page_offset=page_offset,
        page_spans=page_spans,
        duration_words=duration_words,
        page_tables=[page.table_words for page in pages],
        frame_count=header.frame_count,
    )

    return Anm2V1Layout(
        version=header.unknown06,
        page_count=page_count,
        page_offset=page_offset,
        duration_key_count=duration_key_count,
        track_table_file_offset=track_table_offset,
        track_table_bytes=track_table_bytes,
        track_descriptors_preview=track_preview,
        page_frame_spans=page_spans,
        duration_words=duration_words,
        duration_total_frames=duration_total,
        pages=pages,
        validation_errors=validation_errors,
        sample_time_selections=sample_time_selections,
    )


def _positive_increasing_words(words: list[int]) -> list[int]:
    offsets: list[int] = []
    last = -1
    for word in words:
        if word == 0:
            break
        if word <= last:
            break
        offsets.append(word)
        last = word
    return offsets


def _probe_segment(data: bytes, page_file_offset: int, segment_offsets: list[int], segment_index: int) -> Anm2SegmentProbe:
    segment_word_offset = segment_offsets[segment_index]
    file_offset = page_file_offset + 16 * segment_word_offset
    next_offset = segment_offsets[segment_index + 1] if segment_index + 1 < len(segment_offsets) else None
    byte_length = 16 * (next_offset - segment_word_offset) if next_offset is not None else None
    header_words: list[int] = []
    if file_offset + 16 <= len(data):
        header_words = list(struct.unpack_from("<8H", data, file_offset))
    computed_track_groups = None
    mask_groups = None
    flags_or_stream_offset = None
    if len(header_words) >= 4:
        candidate_track_groups = (header_words[0] + header_words[1]) // 9
        candidate_mask_groups = header_words[2] // 9
        candidate_stream_offset = header_words[3]
        plausible_group_counts = 0 < candidate_track_groups <= 512 and 0 < candidate_mask_groups <= 512
        plausible_stream_offset = byte_length is None or candidate_stream_offset <= byte_length
        if plausible_group_counts and plausible_stream_offset:
            computed_track_groups = candidate_track_groups
            mask_groups = candidate_mask_groups
            flags_or_stream_offset = candidate_stream_offset
    stream_base_offset = None
    direct_values_offset = None
    mask_table_offset = None
    mask_table_bytes = None
    mask_value_counts: dict[str, int] = {}
    if len(header_words) >= 4 and computed_track_groups is not None and mask_groups is not None:
        stream_base_offset = (file_offset + 25) & ~0xF
        direct_values_offset = (stream_base_offset + header_words[3] + 15) & ~0xF
        mask_table_offset = (direct_values_offset + 4 * header_words[0] + 3) & ~0x3
        mask_table_bytes = mask_groups
        available_end = len(data)
        if byte_length is not None:
            available_end = min(available_end, file_offset + byte_length)
        mask_end = min(mask_table_offset + mask_table_bytes, available_end)
        if mask_table_offset < mask_end:
            mask_bytes = data[mask_table_offset:mask_end]
            for value in mask_bytes:
                key = f"0x{value:02X}"
                mask_value_counts[key] = mask_value_counts.get(key, 0) + 1
    return Anm2SegmentProbe(
        segment_index=segment_index,
        segment_word_offset=segment_word_offset,
        file_offset=file_offset,
        byte_length=byte_length,
        header_words=header_words,
        computed_track_groups=computed_track_groups,
        mask_groups=mask_groups,
        flags_or_stream_offset=flags_or_stream_offset,
        stream_base_offset=stream_base_offset,
        direct_values_offset=direct_values_offset,
        mask_table_offset=mask_table_offset,
        mask_table_bytes=mask_table_bytes,
        mask_value_counts=mask_value_counts,
    )


def _probe_stream_slot(
    data: bytes,
    page_file_offset: int,
    segment_offsets: list[int],
    table_index: int,
) -> Anm2StreamSlotProbe:
    stream_word_offset = segment_offsets[table_index]
    file_offset = page_file_offset + 16 * stream_word_offset
    next_offset = segment_offsets[table_index + 1] if table_index + 1 < len(segment_offsets) else None
    byte_length = 16 * (next_offset - stream_word_offset) if next_offset is not None else None
    first = data[file_offset : min(file_offset + 16, len(data))]
    looks_like_segment_header = False
    if len(first) == 16:
        words = list(struct.unpack_from("<8H", first))
        track_groups = (words[0] + words[1]) // 9
        mask_groups = words[2] // 9
        looks_like_segment_header = (
            words[0] + words[1] == words[2]
            and words[2] > 0
            and 0 < track_groups <= 512
            and 0 < mask_groups <= 512
            and (byte_length is None or words[3] <= byte_length)
        )
    return Anm2StreamSlotProbe(
        table_index=table_index,
        stream_word_offset=stream_word_offset,
        file_offset=file_offset,
        byte_length=byte_length,
        first_16_bytes_hex=first.hex(" "),
        looks_like_segment_header=looks_like_segment_header,
    )


def _sample_time_selections(
    *,
    page_offset: int,
    page_spans: list[int],
    duration_words: list[int],
    page_tables: list[list[int]],
    frame_count: int,
) -> list[Anm2TimeSelection]:
    if not page_spans or not duration_words:
        return []
    sample_times = [0.0]
    if frame_count > 2:
        sample_times.extend([1.0, 15.0, 16.0, float(max(0, frame_count - 1))])
    selections: list[Anm2TimeSelection] = []
    seen: set[float] = set()
    for sample_time in sample_times:
        if sample_time in seen:
            continue
        seen.add(sample_time)
        selection = select_v1_time(
            page_offset=page_offset,
            page_spans=page_spans,
            duration_words=duration_words,
            page_tables=page_tables,
            time=sample_time,
        )
        if selection is not None:
            selections.append(selection)
    return selections


def select_v1_time(
    *,
    page_offset: int,
    page_spans: list[int],
    duration_words: list[int],
    page_tables: list[list[int]],
    time: float,
) -> Anm2TimeSelection | None:
    """Mirror the v1 time-to-page-table lookup seen in `sub_1800E3490`.

    The returned `table_index` is the value later used by `sub_1800DFE40` to
    select a page-table word. Table word 0 is the base segment header; playable
    packed streams start at table index 1.
    """

    evaluated_frame = _evaluate_duration_at_time(duration_words, time)
    if evaluated_frame is None:
        return None
    adjusted_frame = _ceil_to_previous_index(evaluated_frame)
    remaining = adjusted_frame
    page_index = 0
    for index, span in enumerate(page_spans):
        if remaining < span:
            page_index = index
            break
        remaining -= span
    else:
        page_index = max(0, len(page_spans) - 1)
        remaining = max(0, page_spans[page_index] - 1)

    table_index = remaining // 15 + 1
    in_segment_frame = remaining % 15
    fraction = evaluated_frame - float(adjusted_frame)
    stream_word_offset = None
    stream_file_offset = None
    if 0 <= page_index < len(page_tables):
        table = page_tables[page_index]
        if 0 <= table_index < len(table):
            word = table[table_index]
            if word:
                stream_word_offset = word
                stream_file_offset = page_offset + anm2_page_file_delta(page_index) + 16 * word
    return Anm2TimeSelection(
        requested_time=float(time),
        evaluated_frame=evaluated_frame,
        adjusted_frame=adjusted_frame,
        page_index=page_index,
        table_index=table_index,
        in_segment_frame=in_segment_frame,
        fraction=fraction,
        stream_word_offset=stream_word_offset,
        stream_file_offset=stream_file_offset,
    )


def _evaluate_duration_at_time(duration_words: list[int], time: float) -> float | None:
    if not duration_words:
        return None
    scale = duration_words[0]
    if scale == 0:
        return None
    elapsed = 0.0
    result = 0.0
    for index in range(1, len(duration_words) - 1, 2):
        duration = duration_words[index] / scale
        speed = duration_words[index + 1] / scale
        if time - elapsed < duration:
            return result + (time - elapsed) * speed
        elapsed += duration
        result += speed * duration
    return result


def _ceil_to_previous_index(value: float) -> int:
    # Decompiled v1 code effectively computes ceil(value) - 1, except it clamps
    # the zero case back to zero.
    ceiled = math.ceil(value)
    if ceiled == 0:
        return 0
    return ceiled - 1


def anm2_page_file_delta(page_index: int) -> int:
    return PAGE_SIZE * page_index


def inspect_file(path: str | Path) -> dict[str, Any]:
    clip = decode_file(path)
    return {
        "clip": clip.diagnostics(),
        "round_trip": round_trip_report(path),
    }


def probe_body(header: Anm2Header, body: bytes) -> BodyProbe:
    marker_counts: dict[str, int] = {}
    for marker, label in ((b"\x00\x00\x80?", "float_1_0"), (b"\x00\x00\x00\x00", "float_0_0")):
        marker_counts[label] = body.count(marker)

    offsets: list[int] = []
    limit = min(len(body) - 16, 512)
    for offset in range(0, max(0, limit), 2):
        values = struct.unpack_from("<8H", body, offset)
        if all(value <= max(header.frame_count + 1, header.track_count + 1, 4) for value in values):
            offsets.append(offset)
            if len(offsets) >= 12:
                break

    notes = [
        "This probe is diagnostic only.",
        "Track names appear implicit; target skeleton order is still required before custom writing.",
    ]
    return BodyProbe(
        body_length=len(body),
        first_32_bytes_hex=body[:32].hex(" "),
        possible_u16_table_offsets=offsets,
        repeated_float_markers=marker_counts,
        notes=notes,
    )


def dumps_inspection(path: str | Path) -> str:
    return json.dumps(inspect_file(path), indent=2)
