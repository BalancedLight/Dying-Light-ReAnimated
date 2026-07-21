"""Dying Light 2 ANM2 Header_Version2 parsing and time selection.

DL1 and DL2 both carry the historical ``42`` signature at offset 4.  The
secondary header version at offset 6 is the real container dispatch: version 1
uses the DL1 page layout and version 2 uses the DL2 block layout implemented
here.  The packed sampler inside a selected block is shared with DL1.
"""

from __future__ import annotations

from dataclasses import dataclass
from pathlib import Path
from typing import Any, Sequence
import hashlib
import math
import struct


MAGIC = b"ANM2"
DL2_SIGNATURE = 42
DL2_SECONDARY_VERSION = 2
DL2_HEADER_STRUCT = struct.Struct("<4sHHI8H")
DL2_HEADER_SIZE = DL2_HEADER_STRUCT.size
_U16 = struct.Struct("<H")


def _align_up(value: int, alignment: int) -> int:
    return (value + alignment - 1) & ~(alignment - 1)


def _coerce_data(path_or_data: str | Path | bytes | bytearray) -> bytes:
    if isinstance(path_or_data, (bytes, bytearray)):
        return bytes(path_or_data)
    return Path(path_or_data).read_bytes()


@dataclass(frozen=True, slots=True)
class Anm2V2Header:
    magic: bytes
    signature: int
    header_version: int
    payload_size_units16: int
    header_size_units16: int
    payload_block_size_units16: int
    payload_block_count: int
    time_domain_bound: int
    frame_domain_bound: int
    vfr_interval_count: int
    track_count: int
    static_stream_count: int

    @classmethod
    def parse(cls, data: bytes) -> "Anm2V2Header":
        if len(data) < DL2_HEADER_SIZE:
            raise ValueError(
                f"DL2 Header_Version2 payload is smaller than its {DL2_HEADER_SIZE}-byte disk header."
            )
        return cls(*DL2_HEADER_STRUCT.unpack_from(data, 0))

    @property
    def header_bytes(self) -> int:
        return self.header_size_units16 << 4

    @property
    def payload_bytes(self) -> int:
        return self.payload_size_units16 << 4

    @property
    def payload_block_bytes(self) -> int:
        return self.payload_block_size_units16 << 4

    @property
    def frame_count(self) -> int:
        return self.frame_domain_bound + 1

    @property
    def time_sample_count(self) -> int:
        return self.time_domain_bound + 1

    @property
    def total_component_streams(self) -> int:
        return self.track_count * 9

    @property
    def packed_stream_count(self) -> int:
        return self.total_component_streams - self.static_stream_count


@dataclass(frozen=True, slots=True)
class Anm2V2Block:
    index: int
    file_offset: int
    available_bytes: int
    dictionary: tuple[int, ...]
    base_segment_offset: int
    base_segment_size: int
    base_header_words: tuple[int, ...]
    validation_errors: tuple[str, ...] = ()

    @property
    def dictionary_words(self) -> tuple[int, ...]:
        return self.dictionary

    @property
    def playable_slot_count(self) -> int:
        # entry 0 begins the base segment, entry 1 ends it and starts slot 1,
        # and the final entry terminates the final playable slot.
        return max(0, len(self.dictionary) - 2)

    @property
    def base_segment_end(self) -> int:
        return self.base_segment_offset + self.base_segment_size

    def stream_bounds(self, table_index: int) -> tuple[int, int]:
        if table_index < 1:
            raise ValueError("playable dictionary table indexes start at 1")
        if table_index + 1 >= len(self.dictionary):
            raise ValueError(
                f"block {self.index} dictionary index {table_index} has no terminating entry"
            )
        start = self.file_offset + self.dictionary[table_index] * 16
        end = self.file_offset + self.dictionary[table_index + 1] * 16
        if not self.file_offset <= start < end <= self.file_offset + self.available_bytes:
            raise ValueError(f"block {self.index} stream bounds are outside the block")
        return start, end

    def to_dict(self) -> dict[str, Any]:
        return {
            "index": self.index,
            "file_offset": self.file_offset,
            "available_bytes": self.available_bytes,
            "dictionary": list(self.dictionary),
            "playable_slot_count": self.playable_slot_count,
            "base_segment_offset": self.base_segment_offset,
            "base_segment_size": self.base_segment_size,
            "base_header_words": list(self.base_header_words),
            "validation_errors": list(self.validation_errors),
        }


@dataclass(frozen=True, slots=True)
class Anm2V2TimeSelection:
    requested_time: float
    evaluated_frame: float
    adjusted_frame: int
    block_index: int
    page_table_index: int
    frame_in_15_frame_slot: int
    interpolation_fraction: float
    stream_word_offset: int
    stream_file_offset: int

    # Compatibility-neutral aliases let callers that already consume the v1
    # selection vocabulary inspect either container without branching.
    @property
    def page_index(self) -> int:
        return self.block_index

    @property
    def table_index(self) -> int:
        return self.page_table_index

    @property
    def in_segment_frame(self) -> int:
        return self.frame_in_15_frame_slot

    @property
    def fraction(self) -> float:
        return self.interpolation_fraction


@dataclass(frozen=True, slots=True)
class Anm2V2Layout:
    header: Anm2V2Header
    descriptors: tuple[int, ...]
    block_frame_spans: tuple[int, ...]
    vfr_words: tuple[int, ...]
    blocks: tuple[Anm2V2Block, ...]
    track_table_offset: int
    block_spans_offset: int
    vfr_offset: int
    file_size: int
    sha256: str
    validation_errors: tuple[str, ...] = ()

    @property
    def container(self) -> str:
        return "dl2_header_version_2"

    @property
    def magic(self) -> str:
        return self.header.magic.decode("ascii", errors="replace")

    @property
    def signature(self) -> int:
        return self.header.signature

    @property
    def header_version(self) -> int:
        return self.header.header_version

    @property
    def secondary_version(self) -> int:
        return self.header.header_version

    @property
    def format_label(self) -> str:
        return "format 42"

    @property
    def header_bytes(self) -> int:
        return self.header.header_bytes

    @property
    def payload_offset(self) -> int:
        return self.header.header_bytes

    @property
    def payload_bytes(self) -> int:
        return self.header.payload_bytes

    @property
    def payload_block_bytes(self) -> int:
        return self.header.payload_block_bytes

    @property
    def payload_block_count(self) -> int:
        return self.header.payload_block_count

    @property
    def block_count(self) -> int:
        return self.header.payload_block_count

    @property
    def time_domain_bound(self) -> int:
        return self.header.time_domain_bound

    @property
    def frame_domain_bound(self) -> int:
        return self.header.frame_domain_bound

    @property
    def frame_count(self) -> int:
        return self.header.frame_count

    @property
    def time_sample_count(self) -> int:
        return self.header.time_sample_count

    @property
    def vfr_interval_count(self) -> int:
        return self.header.vfr_interval_count

    @property
    def track_count(self) -> int:
        return self.header.track_count

    @property
    def track_descriptors(self) -> tuple[int, ...]:
        return self.descriptors

    @property
    def total_component_streams(self) -> int:
        return self.header.total_component_streams

    @property
    def static_stream_count(self) -> int:
        return self.header.static_stream_count

    @property
    def packed_stream_count(self) -> int:
        return self.header.packed_stream_count

    @property
    def expected_file_size(self) -> int:
        return self.header.header_bytes + self.header.payload_bytes

    @property
    def is_valid(self) -> bool:
        return not self.validation_errors

    @property
    def layout_valid(self) -> bool:
        return self.is_valid

    def require_valid(self) -> "Anm2V2Layout":
        if self.validation_errors:
            joined = "; ".join(self.validation_errors)
            raise ValueError(f"invalid DL2 Header_Version2 layout: {joined}")
        return self

    def to_dict(self) -> dict[str, Any]:
        return {
            "container": self.container,
            "magic": self.magic,
            "signature": self.signature,
            "header_version": self.header_version,
            "secondary_version": self.secondary_version,
            "payload_size_units16": self.header.payload_size_units16,
            "header_size_units16": self.header.header_size_units16,
            "payload_block_size_units16": self.header.payload_block_size_units16,
            "header_bytes": self.header_bytes,
            "payload_bytes": self.payload_bytes,
            "payload_block_bytes": self.payload_block_bytes,
            "payload_block_count": self.payload_block_count,
            "time_domain_bound": self.time_domain_bound,
            "time_sample_count": self.time_sample_count,
            "frame_domain_bound": self.frame_domain_bound,
            "frame_count": self.frame_count,
            "vfr_interval_count": self.vfr_interval_count,
            "track_count": self.track_count,
            "total_component_streams": self.total_component_streams,
            "static_stream_count": self.static_stream_count,
            "packed_stream_count": self.packed_stream_count,
            "descriptors": list(self.descriptors),
            "block_frame_spans": list(self.block_frame_spans),
            "vfr_words": list(self.vfr_words),
            "blocks": [block.to_dict() for block in self.blocks],
            "track_table_offset": self.track_table_offset,
            "block_spans_offset": self.block_spans_offset,
            "vfr_offset": self.vfr_offset,
            "file_size": self.file_size,
            "expected_file_size": self.expected_file_size,
            "sha256": self.sha256,
            "validation_errors": list(self.validation_errors),
            "layout_valid": self.is_valid,
            "native_curve_decoder": "available" if self.is_valid else "invalid_layout",
            "native_writer": "disabled",
        }


# Historical public name retained as a type alias.  Its semantics are now the
# validated Header_Version2 layout; there is deliberately no active/reference
# descriptor split because the disk format contains one track table.
Dl2Anm2Header42 = Anm2V2Layout


def detect_anm2_format(data_or_path: bytes | bytearray | str | Path) -> int:
    data = _coerce_data(data_or_path)
    if len(data) < 8:
        raise ValueError("ANM2 payload is too small to contain magic and format fields.")
    magic, signature, header_version = struct.unpack_from("<4sHH", data, 0)
    if magic != MAGIC:
        raise ValueError(f"Expected ANM2 magic, found {magic!r}.")
    if signature != DL2_SIGNATURE:
        raise ValueError(f"Unsupported ANM2 signature {signature}; expected 42.")
    if header_version == 1:
        return 1
    if header_version == DL2_SECONDARY_VERSION:
        return 42
    raise ValueError(
        f"Unsupported ANM2 container: signature {signature}, header version {header_version}. "
        "Only Header_Version1 and Header_Version2 are supported."
    )


def _read_u16_table(data: bytes, offset: int, count: int) -> tuple[int, ...]:
    if count <= 0:
        return ()
    return tuple(struct.unpack_from(f"<{count}H", data, offset))


def _parse_block(
    data: bytes,
    *,
    index: int,
    file_offset: int,
    available_bytes: int,
    track_count: int,
    static_stream_count: int,
) -> Anm2V2Block:
    errors: list[str] = []
    dictionary: tuple[int, ...] = ()
    base_segment_offset = file_offset
    base_segment_size = 0
    base_header_words: tuple[int, ...] = ()
    actual_available = max(0, min(available_bytes, len(data) - file_offset))

    if available_bytes <= 0 or file_offset < 0 or file_offset >= len(data):
        errors.append("block starts outside the available payload")
    elif actual_available < 2:
        errors.append("block is too short to contain a dictionary")
    else:
        first_word = _U16.unpack_from(data, file_offset)[0]
        if first_word == 0:
            errors.append("dictionary first word is zero")
        dictionary_table_bytes = first_word * 16
        dictionary_word_count = first_word * 8
        if dictionary_table_bytes > available_bytes:
            errors.append("dictionary table extends beyond the declared block")
        if dictionary_table_bytes > actual_available:
            errors.append("dictionary table is truncated")
        if first_word and dictionary_table_bytes <= actual_available:
            raw_words = _read_u16_table(data, file_offset, dictionary_word_count)
            positive: list[int] = []
            zero_index: int | None = None
            previous = 0
            for word_index, word in enumerate(raw_words):
                if word == 0:
                    zero_index = word_index
                    break
                positive.append(word)
                if word_index and word <= previous:
                    errors.append("dictionary entries are not strictly increasing")
                previous = word
            if zero_index is None:
                errors.append("dictionary has no zero terminator")
            elif any(raw_words[zero_index:]):
                errors.append("dictionary contains nonzero entries after its terminator")
            dictionary = tuple(positive)

            if len(dictionary) < 3:
                errors.append("dictionary has no complete playable stream slot")
            for word in dictionary:
                if word * 16 > available_bytes:
                    errors.append("dictionary entry extends beyond the declared block")
                    break
            if len(dictionary) >= 2:
                base_segment_offset = file_offset + dictionary[0] * 16
                base_segment_size = (dictionary[1] - dictionary[0]) * 16
                base_segment_end = base_segment_offset + base_segment_size
                if base_segment_size <= 0:
                    errors.append("base segment has a non-positive size")
                if base_segment_end > file_offset + available_bytes:
                    errors.append("base segment extends beyond the declared block")
                if base_segment_end > file_offset + actual_available:
                    errors.append("base segment is truncated")
                if base_segment_offset + 16 <= len(data) and base_segment_end <= file_offset + actual_available:
                    base_header_words = _read_u16_table(data, base_segment_offset, 8)
                    direct_count, packed_count, total_count, calibration_bytes = base_header_words[:4]
                    expected_total = track_count * 9
                    expected_packed = expected_total - static_stream_count
                    if total_count != expected_total:
                        errors.append(
                            f"base component count {total_count} does not match track count {track_count} * 9"
                        )
                    if direct_count + packed_count != total_count:
                        errors.append("base direct and packed counts do not equal the total component count")
                    if direct_count != static_stream_count:
                        errors.append(
                            f"base direct count {direct_count} does not match header static count {static_stream_count}"
                        )
                    if packed_count != expected_packed:
                        errors.append(
                            f"base packed count {packed_count} does not match derived count {expected_packed}"
                        )
                    required_calibration = ((packed_count + 7) // 8) * 64
                    if calibration_bytes < required_calibration:
                        errors.append(
                            "base calibration table is shorter than the packed component groups"
                        )

                    # These are the established common-sampler offsets.  Check
                    # them here so decoding cannot read through a base segment.
                    table_start = (base_segment_offset + 0x19) & ~0xF
                    direct_offset = _align_up(table_start + calibration_bytes, 16)
                    mask_offset = _align_up(direct_offset + 4 * direct_count, 4)
                    if mask_offset + track_count > base_segment_end:
                        errors.append("base calibration/direct/mask tables exceed the base segment")

    return Anm2V2Block(
        index=index,
        file_offset=file_offset,
        available_bytes=available_bytes,
        dictionary=dictionary,
        base_segment_offset=base_segment_offset,
        base_segment_size=base_segment_size,
        base_header_words=base_header_words,
        validation_errors=tuple(dict.fromkeys(errors)),
    )


def parse_anm2_v2_layout(path_or_data: str | Path | bytes | bytearray) -> Anm2V2Layout:
    """Parse and validate a Header_Version2 container without decoding curves.

    Disk-header identity errors raise immediately.  Structural errors are
    accumulated in ``validation_errors`` so inspectors can explain every
    problem; curve decode calls ``require_valid()`` before resolving offsets.
    """

    data = _coerce_data(path_or_data)
    header = Anm2V2Header.parse(data)
    if header.magic != MAGIC:
        raise ValueError(f"Expected ANM2 magic, found {header.magic!r}.")
    if header.signature != DL2_SIGNATURE:
        raise ValueError(f"Unsupported ANM2 signature {header.signature}; expected 42.")
    if header.header_version != DL2_SECONDARY_VERSION:
        raise ValueError(
            f"The selected ANM2 has header version {header.header_version}, not Header_Version2."
        )

    errors: list[str] = []
    if header.payload_size_units16 == 0:
        errors.append("payload size in 16-byte units is zero")
    if header.header_size_units16 == 0:
        errors.append("header size in 16-byte units is zero")
    if header.payload_block_size_units16 == 0:
        errors.append("payload block size in 16-byte units is zero")
    if header.payload_block_count == 0:
        errors.append("payload block count is zero")
    if header.track_count == 0:
        errors.append("track count is zero")
    if header.header_bytes < DL2_HEADER_SIZE:
        errors.append("declared header is smaller than the fixed Header_Version2 disk header")
    if header.header_bytes + header.payload_bytes != len(data):
        errors.append(
            f"header bytes {header.header_bytes} + payload bytes {header.payload_bytes} "
            f"does not equal file size {len(data)}"
        )
    if header.static_stream_count > header.total_component_streams:
        errors.append("static stream count exceeds track count * 9")
    if (
        header.payload_block_count
        and header.payload_block_bytes
        and header.payload_bytes > header.payload_block_count * header.payload_block_bytes
    ):
        errors.append("payload exceeds the declared payload-block capacity")

    track_table_offset = _align_up(DL2_HEADER_SIZE, 4)
    track_table_end = track_table_offset + header.track_count * 4
    block_spans_offset = track_table_end
    block_spans_end = block_spans_offset + header.payload_block_count * 2
    vfr_offset = block_spans_end
    vfr_word_count = 1 + 2 * header.vfr_interval_count
    vfr_end = vfr_offset + vfr_word_count * 2

    if track_table_end > header.header_bytes:
        errors.append("track descriptor table extends beyond the declared header")
    if block_spans_end > header.header_bytes:
        errors.append("block frame-span table extends beyond the declared header")
    if vfr_end > header.header_bytes:
        errors.append("VFR table extends beyond the declared header")
    if vfr_end > len(data):
        errors.append("header-side variable tables are truncated")

    descriptor_count = 0
    if track_table_offset < min(header.header_bytes, len(data)):
        descriptor_count = min(
            header.track_count,
            (min(header.header_bytes, len(data)) - track_table_offset) // 4,
        )
    descriptors = (
        tuple(struct.unpack_from(f"<{descriptor_count}I", data, track_table_offset))
        if descriptor_count
        else ()
    )
    if len(descriptors) != header.track_count:
        errors.append("track descriptor table is truncated")

    span_count = 0
    if block_spans_offset < min(header.header_bytes, len(data)):
        span_count = min(
            header.payload_block_count,
            (min(header.header_bytes, len(data)) - block_spans_offset) // 2,
        )
    block_frame_spans = _read_u16_table(data, block_spans_offset, span_count)
    if len(block_frame_spans) != header.payload_block_count:
        errors.append("block frame-span table is truncated")
    else:
        if any(span == 0 for span in block_frame_spans):
            errors.append("block frame spans must be positive")
        if sum(block_frame_spans) != header.frame_count - 1:
            errors.append(
                f"block frame spans sum to {sum(block_frame_spans)}, "
                f"expected frame_count - 1 ({header.frame_count - 1})"
            )

    readable_vfr_count = 0
    if vfr_offset < min(header.header_bytes, len(data)):
        readable_vfr_count = min(
            vfr_word_count,
            (min(header.header_bytes, len(data)) - vfr_offset) // 2,
        )
    vfr_words = _read_u16_table(data, vfr_offset, readable_vfr_count)
    if len(vfr_words) != vfr_word_count:
        errors.append("VFR table is truncated")
    elif not vfr_words or vfr_words[0] == 0:
        errors.append("VFR scale is zero")

    blocks: list[Anm2V2Block] = []
    if header.payload_block_count and header.payload_block_bytes:
        for block_index in range(header.payload_block_count):
            payload_relative = block_index * header.payload_block_bytes
            block_offset = header.header_bytes + payload_relative
            if payload_relative >= header.payload_bytes:
                available = 0
            else:
                available = min(
                    header.payload_block_bytes,
                    header.payload_bytes - payload_relative,
                )
            block = _parse_block(
                data,
                index=block_index,
                file_offset=block_offset,
                available_bytes=available,
                track_count=header.track_count,
                static_stream_count=header.static_stream_count,
            )
            blocks.append(block)
            errors.extend(f"block {block_index}: {error}" for error in block.validation_errors)
            if block_index < len(block_frame_spans):
                required_slots = (block_frame_spans[block_index] + 14) // 15
                if block.playable_slot_count < required_slots:
                    errors.append(
                        f"block {block_index}: {block.playable_slot_count} playable slots "
                        f"do not cover its {block_frame_spans[block_index]} frame intervals"
                    )

    return Anm2V2Layout(
        header=header,
        descriptors=descriptors,
        block_frame_spans=block_frame_spans,
        vfr_words=vfr_words,
        blocks=tuple(blocks),
        track_table_offset=track_table_offset,
        block_spans_offset=block_spans_offset,
        vfr_offset=vfr_offset,
        file_size=len(data),
        sha256=hashlib.sha256(data).hexdigest().upper(),
        validation_errors=tuple(dict.fromkeys(errors)),
    )


def parse_dl2_header42(path_or_data: str | Path | bytes | bytearray) -> Anm2V2Layout:
    """Compatibility entry point returning the corrected Header_Version2 layout."""

    return parse_anm2_v2_layout(path_or_data)


def evaluate_vfr_time(
    vfr_words: Sequence[int],
    time: float,
    *,
    time_domain_bound: int,
    frame_domain_bound: int,
) -> float:
    """Evaluate the common piecewise duration/rate table for a requested time."""

    requested = float(time)
    if not math.isfinite(requested):
        raise ValueError("ANM2 sample time must be finite")
    if not vfr_words:
        raise ValueError("ANM2 VFR table is empty")
    scale = int(vfr_words[0])
    if scale <= 0:
        raise ValueError("ANM2 VFR scale must be positive")
    if (len(vfr_words) - 1) % 2:
        raise ValueError("ANM2 VFR table has an incomplete duration/rate pair")

    clamped_time = min(max(requested, 0.0), float(time_domain_bound))
    elapsed = 0.0
    evaluated = 0.0
    for index in range(1, len(vfr_words), 2):
        duration = int(vfr_words[index]) / scale
        rate = int(vfr_words[index + 1]) / scale
        remaining = clamped_time - elapsed
        if remaining < duration:
            evaluated += max(0.0, remaining) * rate
            break
        elapsed += duration
        evaluated += duration * rate
    return min(max(evaluated, 0.0), float(frame_domain_bound))


def _ceil_to_previous_index(value: float) -> int:
    ceiled = math.ceil(value)
    return 0 if ceiled <= 0 else ceiled - 1


def select_v2_time(layout: Anm2V2Layout, time: float) -> Anm2V2TimeSelection:
    """Resolve Header_Version2 VFR time to a block-local sampler stream."""

    layout.require_valid()
    evaluated_frame = evaluate_vfr_time(
        layout.vfr_words,
        time,
        time_domain_bound=layout.time_domain_bound,
        frame_domain_bound=layout.frame_domain_bound,
    )
    adjusted_frame = _ceil_to_previous_index(evaluated_frame)
    remaining = adjusted_frame
    block_index = 0
    for index, span in enumerate(layout.block_frame_spans):
        if remaining < span:
            block_index = index
            break
        remaining -= span
    else:
        block_index = max(0, len(layout.block_frame_spans) - 1)
        remaining = max(0, layout.block_frame_spans[block_index] - 1)

    table_index = remaining // 15 + 1
    frame_in_slot = remaining % 15
    fraction = evaluated_frame - float(adjusted_frame)
    block = layout.blocks[block_index]
    stream_start, _ = block.stream_bounds(table_index)
    return Anm2V2TimeSelection(
        requested_time=float(time),
        evaluated_frame=evaluated_frame,
        adjusted_frame=adjusted_frame,
        block_index=block_index,
        page_table_index=table_index,
        frame_in_15_frame_slot=frame_in_slot,
        interpolation_fraction=fraction,
        stream_word_offset=block.dictionary[table_index],
        stream_file_offset=stream_start,
    )


def inspect_anm2(path: str | Path, target_descriptors: Sequence[int] | None = None) -> dict[str, Any]:
    source = Path(path)
    detected = detect_anm2_format(source)
    if detected == 42:
        layout = parse_anm2_v2_layout(source)
        report = layout.to_dict()
        report.update({"path": str(source), "detected_format": 42, "game_id": "dying_light_2"})
        if target_descriptors is not None:
            target = {int(value) for value in target_descriptors}
            source_descriptors = set(layout.descriptors)
            report["target_profile_compatibility"] = {
                "matched_descriptors": len(source_descriptors & target),
                "missing_descriptors": sorted(source_descriptors - target),
                "extra_target_descriptors": sorted(target - source_descriptors),
                "compatible_for_inspection": layout.is_valid,
                "compatible_for_curve_decode": layout.is_valid,
            }
        return report

    from . import anm2

    clip = anm2.decode_file(source)
    report = clip.diagnostics()
    report.update({"path": str(source), "detected_format": 1, "game_id": "dying_light_1"})
    return report


__all__ = [
    "Anm2V2Block",
    "Anm2V2Header",
    "Anm2V2Layout",
    "Anm2V2TimeSelection",
    "DL2_HEADER_SIZE",
    "DL2_HEADER_STRUCT",
    "DL2_SECONDARY_VERSION",
    "DL2_SIGNATURE",
    "Dl2Anm2Header42",
    "detect_anm2_format",
    "evaluate_vfr_time",
    "inspect_anm2",
    "parse_anm2_v2_layout",
    "parse_dl2_header42",
    "select_v2_time",
]
