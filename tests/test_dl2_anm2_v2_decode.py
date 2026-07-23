from __future__ import annotations

import json
import math
from pathlib import Path

import pytest

from dlanm2_gui import anm2
from dlanm2_gui.anm2_components import (
    SamplerSelection,
    decode_samples,
    decode_selected_sampler_frame,
    decode_v1_samples,
    decode_v2_samples,
)
from dlanm2_gui.anm2_writer import build_payload_from_values
from dlanm2_gui.dl2_anm2 import parse_dl2_header42, select_v2_time


ROOT = Path(__file__).resolve().parents[1]
SAMPLE = ROOT / "reference" / "dl2" / "0_m_fpp_farjump.anm2"
GOLDEN = ROOT / "tests" / "fixtures" / "dl2_farjump_selected_values.json"


def test_selected_native_values_match_independent_fixture() -> None:
    fixture = json.loads(GOLDEN.read_text(encoding="utf-8"))
    layout = parse_dl2_header42(SAMPLE).require_valid()
    assert layout.sha256 == fixture["source_sha256"]
    track_index = int(fixture["track_index"])
    assert f"0x{layout.descriptors[track_index]:08X}" == fixture["descriptor"]

    times = [float(frame) for frame in fixture["frames"]]
    decoded = decode_v2_samples(SAMPLE.read_bytes(), times)
    for decoded_frame, frame in zip(decoded.frames, fixture["frames"]):
        assert decoded_frame.tracks[track_index][:6] == pytest.approx(
            fixture["frames"][frame], abs=1.0e-5
        )


def test_native_v2_decode_all_frames_is_finite_and_crosses_blocks() -> None:
    layout = parse_dl2_header42(SAMPLE).require_valid()
    decoded = decode_samples(SAMPLE.read_bytes(), [float(frame) for frame in range(layout.frame_count)])

    assert decoded.container == "dl2_header_version_2"
    assert decoded.header_version == 2
    assert decoded.frame_count == 229
    assert decoded.track_count == 189
    assert decoded.descriptors == layout.descriptors
    assert len(decoded.frames) == 229
    assert all(len(frame.tracks) == 189 for frame in decoded.frames)
    assert all(len(track) == 9 for frame in decoded.frames for track in frame.tracks)
    assert all(math.isfinite(value) for frame in decoded.frames for track in frame.tracks for value in track)
    assert decoded.frames[120].page_index == 0
    assert decoded.frames[120].table_index == 8
    assert decoded.frames[121].page_index == 1
    assert decoded.frames[121].table_index == 1
    assert layout.static_stream_count + layout.packed_stream_count == 1701


def test_resolved_sampler_helper_is_container_neutral() -> None:
    data = SAMPLE.read_bytes()
    layout = parse_dl2_header42(data).require_valid()
    outer = select_v2_time(layout, 121.0)
    block = layout.blocks[outer.block_index]
    stream_start, stream_end = block.stream_bounds(outer.page_table_index)
    direct = decode_selected_sampler_frame(
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
            requested_time=121.0,
        ),
    )
    wrapped = decode_v2_samples(data, [121.0]).frames[0]
    assert direct == wrapped


def test_v1_wrapper_and_dispatch_remain_identical() -> None:
    header = anm2.Anm2Header(
        format_version=42,
        unknown06=1,
        frame_count=3,
        track_count=1,
        unknown12=0,
        unknown14=0,
        declared_length=0,
        unknown20=0,
        unknown24=0,
        unknown28=0,
    )
    values = [
        [[0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 1.0, 1.0, 1.0]],
        [[0.1, 0.0, 0.0, 1.0, 0.0, 0.0, 1.0, 1.0, 1.0]],
        [[0.2, 0.0, 0.0, 2.0, 0.0, 0.0, 1.0, 1.0, 1.0]],
    ]
    flags = [[True, False, False, True, False, False, False, False, False]]
    payload = build_payload_from_values(header, [0x12345678], values, flags)
    times = [0.0, 1.0, 2.0]
    assert decode_samples(payload, times) == decode_v1_samples(payload, times)

