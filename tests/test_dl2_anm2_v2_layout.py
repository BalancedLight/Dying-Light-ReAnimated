from __future__ import annotations

import struct
from pathlib import Path

import pytest

from dlanm2_gui.dl2_anm2 import (
    DL2_HEADER_SIZE,
    detect_anm2_format,
    evaluate_vfr_time,
    parse_dl2_header42,
    select_v2_time,
)


ROOT = Path(__file__).resolve().parents[1]
SAMPLE = ROOT / "reference" / "dl2" / "0_m_fpp_farjump.anm2"


def test_exact_header_v2_layout_and_tables() -> None:
    data = SAMPLE.read_bytes()
    layout = parse_dl2_header42(data)

    assert DL2_HEADER_SIZE == 0x1C
    assert detect_anm2_format(data) == 42
    assert layout.is_valid, layout.validation_errors
    assert layout.container == "dl2_header_version_2"
    assert layout.magic == "ANM2"
    assert layout.signature == 42
    assert layout.header_version == 2
    assert layout.header.payload_size_units16 == 6702
    assert layout.header.header_size_units16 == 50
    assert layout.header.payload_block_size_units16 == 4096
    assert layout.header_bytes == 800 == 0x320
    assert layout.payload_bytes == 107232
    assert layout.payload_block_bytes == 65536 == 0x10000
    assert layout.payload_block_count == 2
    assert layout.expected_file_size == layout.file_size == 108032
    assert layout.time_domain_bound == 228
    assert layout.frame_domain_bound == 228
    assert layout.frame_count == 229
    assert layout.track_count == 189
    assert layout.total_component_streams == 1701
    assert layout.static_stream_count == 1354
    assert layout.packed_stream_count == 347
    assert len(layout.descriptors) == 189
    assert layout.track_descriptors == layout.descriptors
    assert layout.track_table_offset == 0x1C
    assert layout.track_table_offset + 4 * layout.track_count == 0x310
    assert layout.block_spans_offset == 0x310
    assert layout.vfr_offset == 0x314
    assert layout.block_frame_spans == (120, 108)
    assert sum(layout.block_frame_spans) == layout.frame_count - 1
    assert layout.vfr_words == (1, 228, 1)
    assert not hasattr(layout, "active_descriptors")
    assert not hasattr(layout, "reference_descriptors")


def test_exact_block_dictionaries_and_base_segments() -> None:
    layout = parse_dl2_header42(SAMPLE).require_valid()
    assert [block.dictionary for block in layout.blocks] == [
        (2, 530, 956, 1341, 1730, 2152, 2511, 2799, 3183, 3653),
        (2, 530, 1012, 1455, 1716, 1960, 2161, 2326, 2493, 2606),
    ]
    for block in layout.blocks:
        assert block.base_segment_offset == block.file_offset + 0x20
        assert block.base_segment_size == 8448
        assert block.base_header_words[:4] == (1354, 347, 1701, 2816)
        assert block.playable_slot_count == 8
    assert layout.blocks[0].available_bytes == 65536
    assert layout.blocks[1].available_bytes == 41696


def test_full_dictionary_table_does_not_require_zero_padding() -> None:
    """A maximum-size block table ends in a stream boundary, not a zero."""
    data = bytearray(SAMPLE.read_bytes())
    layout = parse_dl2_header42(data)
    block = layout.blocks[0]
    assert len(block.dictionary) == 10

    # The table reserves 16 u16 values.  Replace its six unused zero-padded
    # entries with valid increasing offsets, as in a full DL2 dictionary.
    for table_index, offset in enumerate(range(3654, 3660), start=10):
        struct.pack_into("<H", data, block.file_offset + table_index * 2, offset)

    full = parse_dl2_header42(data)
    assert full.is_valid, full.validation_errors
    assert full.blocks[0].dictionary[-1] == 3659
    assert full.blocks[0].playable_slot_count == 14


@pytest.mark.parametrize(
    ("time", "block", "slot", "frame", "fraction"),
    [
        (0, 0, 1, 0, 0.0),
        (1, 0, 1, 0, 1.0),
        (15, 0, 1, 14, 1.0),
        (16, 0, 2, 0, 1.0),
        (120, 0, 8, 14, 1.0),
        (121, 1, 1, 0, 1.0),
        (228, 1, 8, 2, 1.0),
    ],
)
def test_golden_time_selections(
    time: float,
    block: int,
    slot: int,
    frame: int,
    fraction: float,
) -> None:
    selection = select_v2_time(parse_dl2_header42(SAMPLE), time)
    assert selection.block_index == block
    assert selection.page_table_index == slot
    assert selection.frame_in_15_frame_slot == frame
    assert selection.interpolation_fraction == pytest.approx(fraction)


def test_fractional_and_generic_vfr_selection() -> None:
    layout = parse_dl2_header42(SAMPLE)
    selection = select_v2_time(layout, 120.25)
    assert selection.evaluated_frame == pytest.approx(120.25)
    assert selection.adjusted_frame == 120
    assert selection.block_index == 1
    assert selection.page_table_index == 1
    assert selection.frame_in_15_frame_slot == 0
    assert selection.interpolation_fraction == pytest.approx(0.25)

    # scale=2: two time units at 0.5 frame/unit, then one at 2 frames/unit.
    words = (2, 4, 1, 2, 4)
    assert evaluate_vfr_time(
        words, 2.5, time_domain_bound=3, frame_domain_bound=3
    ) == pytest.approx(2.0)
    assert evaluate_vfr_time(
        words, -10, time_domain_bound=3, frame_domain_bound=3
    ) == 0.0
    assert evaluate_vfr_time(
        words, 99, time_domain_bound=3, frame_domain_bound=2
    ) == 2.0


@pytest.mark.parametrize(
    "mutate, expected_error",
    [
        (lambda data, layout: data.pop(), "does not equal file size"),
        (
            lambda data, layout: struct.pack_into("<H", data, layout.blocks[0].file_offset + 4, 500),
            "not strictly increasing",
        ),
        (
            lambda data, layout: struct.pack_into("<H", data, 0x1A, 1702),
            "static stream count exceeds",
        ),
        (
            lambda data, layout: struct.pack_into("<H", data, 0x0C, 1),
            "track descriptor table extends",
        ),
    ],
)
def test_malformed_layouts_are_rejected(mutate, expected_error: str) -> None:
    data = bytearray(SAMPLE.read_bytes())
    original = parse_dl2_header42(data)
    mutate(data, original)
    malformed = parse_dl2_header42(data)
    assert not malformed.is_valid
    assert any(expected_error in error for error in malformed.validation_errors)
    with pytest.raises(ValueError, match="invalid DL2 Header_Version2 layout"):
        malformed.require_valid()
