from __future__ import annotations

from dataclasses import dataclass, field
import struct


ANIMATION_SCR_RECORD_SIZE = 56
ANIMATION_SCR_RECORD_MAGIC = 471
ANIMATION_SCR_RECORD_SENTINEL = 0x7FFA


@dataclass(frozen=True)
class AnimationScrEvent:
    frame: int
    event_name: str | int
    argument: str | int | float


@dataclass(frozen=True)
class AnimationScrSequence:
    name: str
    anm2_name: str
    start_frame: float
    end_frame: float
    fps: float
    enabled: int = 1
    blend: float = 0.5
    events: tuple[AnimationScrEvent, ...] = field(default_factory=tuple)

    @property
    def normalized_name(self) -> str:
        return self.name.lower()


@dataclass(frozen=True)
class ParsedAnimationScrSequence:
    name: str
    name_offset: int
    record_offset: int
    enabled: int
    blend: float
    fps: float
    start_frame: float
    end_frame: float
    event_count: int


@dataclass(frozen=True)
class ParsedAnimationScr:
    sequence_count: int
    name_table_offset: int
    sequences: tuple[ParsedAnimationScrSequence, ...]

    def by_name(self) -> dict[str, ParsedAnimationScrSequence]:
        return {sequence.name: sequence for sequence in self.sequences}


def build_animation_scr_sections(sequences: list[AnimationScrSequence] | tuple[AnimationScrSequence, ...]) -> tuple[bytes, bytes]:
    """Build the two payload sections stored by `_ANIMATION_SCR_` resources.

    Current support intentionally covers the no-event case first. That is
    enough for the smallest editor visibility probe and matches stock no-event
    resources byte-for-byte.
    """

    ordered = sorted(sequences, key=lambda item: item.normalized_name)
    if any(sequence.events for sequence in ordered):
        raise NotImplementedError("AnimationScr event payload encoding is not implemented yet")

    names_blob = _names_blob([sequence.normalized_name for sequence in ordered])
    offsets = _name_offsets(names_blob, [sequence.normalized_name for sequence in ordered])

    records = bytearray()
    for sequence, name_offset in zip(ordered, offsets):
        records.extend(
            struct.pack(
                "<IIIIIffffIIIII",
                name_offset,
                ANIMATION_SCR_RECORD_MAGIC,
                0,
                0,
                int(sequence.enabled),
                float(sequence.blend),
                float(sequence.fps),
                float(sequence.start_frame),
                float(sequence.end_frame),
                0,
                0,
                0,
                len(sequence.events),
                ANIMATION_SCR_RECORD_SENTINEL,
            )
        )

    section0 = bytes(records) + names_blob
    section1 = struct.pack("<II", len(ordered), 0) + names_blob
    return section0, section1


def parse_animation_scr_sections(sections: tuple[bytes, bytes]) -> ParsedAnimationScr:
    section0, section1 = sections
    if len(section1) < 8:
        raise ValueError("AnimationScr section 1 is too small to contain a sequence count")
    sequence_count = struct.unpack_from("<I", section1, 0)[0]
    record_bytes = sequence_count * ANIMATION_SCR_RECORD_SIZE
    if len(section0) < record_bytes:
        raise ValueError(
            f"AnimationScr section 0 is too small for {sequence_count} records: "
            f"{len(section0)} < {record_bytes}"
        )

    records = [
        struct.unpack_from("<IIIIIffffIIIII", section0, index * ANIMATION_SCR_RECORD_SIZE)
        for index in range(sequence_count)
    ]
    name_table_offset = _find_name_table_offset(section0, records)
    sequences: list[ParsedAnimationScrSequence] = []
    for index, record in enumerate(records):
        if record[1] != ANIMATION_SCR_RECORD_MAGIC or record[13] != ANIMATION_SCR_RECORD_SENTINEL:
            continue
        name = _read_c_string(section0, name_table_offset + record[0])
        if not name:
            continue
        sequences.append(
            ParsedAnimationScrSequence(
                name=name,
                name_offset=record[0],
                record_offset=index * ANIMATION_SCR_RECORD_SIZE,
                enabled=record[4],
                blend=record[5],
                fps=record[6],
                start_frame=record[7],
                end_frame=record[8],
                event_count=record[12],
            )
        )
    return ParsedAnimationScr(
        sequence_count=sequence_count,
        name_table_offset=name_table_offset,
        sequences=tuple(sequences),
    )


def patch_animation_scr_sequence_ranges(
    sections: tuple[bytes, bytes],
    overrides: dict[str, tuple[float, float, float]],
) -> tuple[bytes, bytes]:
    """Patch fps/start/end fields in copied stock AnimationScr section 0.

    This keeps stock event payloads and sequence names intact while allowing a
    replacement ANM2 to advertise a duration that matches its generated header.
    """

    parsed = parse_animation_scr_sections(sections)
    by_name = parsed.by_name()
    section0 = bytearray(sections[0])
    missing: list[str] = []
    for raw_name, (start_frame, end_frame, fps) in overrides.items():
        name = raw_name.lower()
        sequence = by_name.get(name)
        if sequence is None:
            missing.append(name)
            continue
        struct.pack_into("<fff", section0, sequence.record_offset + 24, float(fps), float(start_frame), float(end_frame))
    if missing:
        raise ValueError(f"AnimationScr section is missing sequence(s): {', '.join(missing)}")
    return bytes(section0), sections[1]


def append_animation_scr_sequences(
    sections: tuple[bytes, bytes],
    sequences: list[AnimationScrSequence] | tuple[AnimationScrSequence, ...],
) -> tuple[bytes, bytes]:
    """Append no-event sequences to an existing compiled AnimationScr resource.

    Stock `anims_man_all` stores fixed-size sequence records first and the
    name table after them. Sequence name offsets are relative to the name
    table, so inserting new records immediately before the name table preserves
    every existing record and appends new names at the end.
    """

    if not sequences:
        return sections
    if any(sequence.events for sequence in sequences):
        raise NotImplementedError("Appending AnimationScr event payloads is not implemented yet")

    parsed = parse_animation_scr_sections(sections)
    record_table_end = parsed.sequence_count * ANIMATION_SCR_RECORD_SIZE
    if parsed.name_table_offset != record_table_end:
        raise NotImplementedError(
            "Appending to AnimationScr resources with event/aux payloads between records and names is not implemented yet"
        )
    existing = {sequence.name.lower() for sequence in parsed.sequences}
    ordered = sorted(sequences, key=lambda item: item.normalized_name)
    duplicates = [sequence.normalized_name for sequence in ordered if sequence.normalized_name in existing]
    if duplicates:
        raise ValueError(f"AnimationScr already has sequence(s): {', '.join(duplicates)}")

    section0, section1 = sections
    if len(section1) < 8:
        raise ValueError("AnimationScr section 1 is too small to append sequences")

    names_blob0 = section0[parsed.name_table_offset :]
    names_blob1 = section1[8:]
    new_names = _names_blob([sequence.normalized_name for sequence in ordered])
    offsets = _name_offsets(new_names, [sequence.normalized_name for sequence in ordered])
    offsets = [len(names_blob0) + offset for offset in offsets]

    records = bytearray()
    for sequence, name_offset in zip(ordered, offsets):
        records.extend(
            struct.pack(
                "<IIIIIffffIIIII",
                name_offset,
                ANIMATION_SCR_RECORD_MAGIC,
                0,
                0,
                int(sequence.enabled),
                float(sequence.blend),
                float(sequence.fps),
                float(sequence.start_frame),
                float(sequence.end_frame),
                0,
                0,
                0,
                0,
                ANIMATION_SCR_RECORD_SENTINEL,
            )
        )

    updated_section0 = (
        section0[: parsed.name_table_offset]
        + bytes(records)
        + names_blob0
        + new_names
    )
    first, second = struct.unpack_from("<II", section1, 0)
    updated_section1 = struct.pack("<II", first + len(ordered), second) + names_blob1 + new_names
    reparsed = parse_animation_scr_sections((updated_section0, updated_section1))
    missing = [sequence.normalized_name for sequence in ordered if sequence.normalized_name not in reparsed.by_name()]
    if missing:
        raise ValueError(f"appended sequence(s) could not be parsed back: {', '.join(missing)}")
    return updated_section0, updated_section1


def _names_blob(names: list[str]) -> bytes:
    return b"".join(name.encode("utf-8") + b"\0" for name in names)


def _name_offsets(blob: bytes, names: list[str]) -> list[int]:
    offsets: list[int] = []
    cursor = 0
    for name in names:
        encoded = name.encode("utf-8") + b"\0"
        if blob[cursor : cursor + len(encoded)] != encoded:
            raise ValueError(f"name blob cursor mismatch for {name!r}")
        offsets.append(cursor)
        cursor += len(encoded)
    return offsets


def _find_name_table_offset(section0: bytes, records: list[tuple]) -> int:
    simple_offset = len(records) * ANIMATION_SCR_RECORD_SIZE
    best_offset = simple_offset
    best_run = _name_run_length(section0, simple_offset)
    position = simple_offset
    previous = 0
    while position < len(section0):
        value = section0[position]
        if value in _NAME_START_BYTES and (position == 0 or previous == 0):
            run = _name_run_length(section0, position)
            if run > best_run:
                best_run = run
                best_offset = position
            if run > 1:
                position = _skip_name_run(section0, position)
                previous = 0
                continue
        previous = value
        position += 1

    sample_records = records[: min(len(records), 128)]
    score = sum(1 for record in sample_records if _looks_like_name_at(section0, best_offset + record[0]))
    if best_run <= 0 or score <= 0:
        raise ValueError("could not locate AnimationScr sequence-name table")
    return best_offset


_NAME_START_BYTES = set(b"abcdefghijklmnopqrstuvwxyz0123456789_")
_NAME_BYTES = set(b"abcdefghijklmnopqrstuvwxyz0123456789_-.")


def _looks_like_name_at(data: bytes, offset: int) -> bool:
    try:
        name = _read_c_string(data, offset)
    except ValueError:
        return False
    if not (1 <= len(name) <= 160):
        return False
    encoded = name.encode("ascii", errors="ignore")
    return len(encoded) == len(name) and all(byte in _NAME_BYTES for byte in encoded)


def _name_run_length(data: bytes, offset: int, *, limit: int = 128) -> int:
    count = 0
    cursor = offset
    while count < limit and _looks_like_name_at(data, cursor):
        end = data.find(b"\0", cursor)
        if end < 0:
            break
        count += 1
        cursor = end + 1
    return count


def _skip_name_run(data: bytes, offset: int) -> int:
    cursor = offset
    while _looks_like_name_at(data, cursor):
        end = data.find(b"\0", cursor)
        if end < 0:
            break
        cursor = end + 1
    return cursor


def _read_c_string(data: bytes, offset: int) -> str:
    if offset < 0 or offset >= len(data):
        raise ValueError("string offset is outside data")
    end = data.find(b"\0", offset)
    if end < 0:
        raise ValueError("unterminated string")
    raw = data[offset:end]
    return raw.decode("ascii", errors="strict")
