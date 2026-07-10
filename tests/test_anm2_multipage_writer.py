from __future__ import annotations

import struct

from dlanm2_gui import anm2
from dlanm2_gui.anm2_writer import (
    _build_packed_pages,
    _build_payload_with_pages,
)


def _header(*, frame_count: int, track_count: int = 1) -> anm2.Anm2Header:
    return anm2.Anm2Header(
        format_version=anm2.FORMAT_VERSION,
        unknown06=1,
        frame_count=frame_count,
        track_count=track_count,
        unknown12=1,
        unknown14=0,
        declared_length=0,
        unknown20=1,
        unknown24=0,
        unknown28=0,
    )


def test_large_packed_clip_is_split_into_valid_pages() -> None:
    base_segment = bytes(0x1000)
    stream_chunks = [bytes([index & 0xFF]) * 0x1000 for index in range(20)]
    frame_count = 20 * 15 + 1

    pages, spans = _build_packed_pages(base_segment, stream_chunks, frame_count)
    payload = _build_payload_with_pages(_header(frame_count=frame_count), [0x12345678], pages, spans)

    parsed_header = anm2.Anm2Header.parse(payload)
    layout = anm2.probe_v1_layout(parsed_header, payload)

    assert parsed_header.unknown12 == 2
    assert spans == [210, 90]
    assert len(pages[0]) <= anm2.PAGE_SIZE
    assert len(pages[1]) <= anm2.PAGE_SIZE
    assert layout is not None
    assert layout.validation_errors == []
    assert layout.page_frame_spans == spans
    assert layout.pages[1].file_offset - layout.pages[0].file_offset == anm2.PAGE_SIZE


def test_small_packed_clip_remains_one_page() -> None:
    base_segment = bytes(0x400)
    stream_chunks = [bytes(0x400) for _ in range(4)]
    frame_count = 4 * 15 + 1

    pages, spans = _build_packed_pages(base_segment, stream_chunks, frame_count)
    payload = _build_payload_with_pages(_header(frame_count=frame_count), [0x12345678], pages, spans)
    parsed_header = anm2.Anm2Header.parse(payload)
    layout = anm2.probe_v1_layout(parsed_header, payload)

    assert len(pages) == 1
    assert spans == [60]
    assert parsed_header.unknown12 == 1
    assert layout is not None
    assert layout.validation_errors == []


def test_project_validator_rejects_declared_oversized_one_page_clip() -> None:
    from dlanm2_gui.project_builder import _validate_generated_anm2_payload

    base_segment = bytes(0x400)
    stream_chunks = [bytes(0x400) for _ in range(4)]
    frame_count = 4 * 15 + 1
    pages, spans = _build_packed_pages(base_segment, stream_chunks, frame_count)
    payload = bytearray(
        _build_payload_with_pages(
            _header(frame_count=frame_count),
            [0x12345678],
            pages,
            spans,
        )
    )

    # Reproduce the pre-alpha.4 structural fault: the file physically extends
    # into a second 64 KiB page position while the header still declares one.
    payload.extend(bytes(anm2.PAGE_SIZE))
    struct.pack_into("<I", payload, 0x10, len(payload))

    try:
        _validate_generated_anm2_payload(bytes(payload), resource_name="bad_long_clip")
    except ValueError as exc:
        assert "invalid ANM2 page layout" in str(exc)
        assert "page_count 1" in str(exc)
    else:
        raise AssertionError("malformed oversized one-page ANM2 was accepted")
