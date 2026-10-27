from __future__ import annotations

import struct
from pathlib import Path

import numpy as np
import pytest

from dlanm2_gui.anm2_components import decode_all_frames_cached
from dlanm2_gui.dl2_anm2 import (
    DL2_HEADER_SIZE,
    detect_anm2_format,
    inspect_anm2,
    normalize_header_version3,
    parse_anm2_v2_layout,
)


ROOT = Path(__file__).resolve().parents[1]
SAMPLE = ROOT / "reference" / "dl2" / "0_m_fpp_farjump.anm2"


def _as_header_v3(v2: bytes, pose_streams: int = 0) -> bytes:
    """Header_Version3 = Version2 + u16 pose stream count + u16 padding after the fixed header."""

    header_bytes = struct.unpack_from("<H", v2, 12)[0] << 4
    tables = v2[DL2_HEADER_SIZE:header_bytes]
    new_units = (DL2_HEADER_SIZE + 4 + len(tables) + 15) >> 4
    head = bytearray(v2[:DL2_HEADER_SIZE])
    struct.pack_into("<H", head, 6, 3)
    struct.pack_into("<H", head, 12, new_units)
    header = head + struct.pack("<HH", pose_streams, 0) + tables
    header += b"\0" * ((new_units << 4) - len(header))
    return bytes(header) + v2[header_bytes:]


def test_header_v3_is_read_like_v2(tmp_path: Path) -> None:
    v2 = SAMPLE.read_bytes()
    v3 = _as_header_v3(v2)
    path = tmp_path / "farjump_v3.anm2"
    path.write_bytes(v3)

    assert detect_anm2_format(v3) == 42
    assert normalize_header_version3(v2) is v2
    layout_v2 = parse_anm2_v2_layout(v2)
    layout_v3 = parse_anm2_v2_layout(v3)
    assert layout_v3.is_valid, layout_v3.validation_errors
    assert layout_v3.descriptors == layout_v2.descriptors
    assert layout_v3.frame_count == layout_v2.frame_count

    report = inspect_anm2(path)
    assert report["source_header_version"] == 3
    assert report["pose_stream_count"] == 0

    a = decode_all_frames_cached(v2)
    b = decode_all_frames_cached(v3)
    assert np.array_equal(np.asarray(a.values), np.asarray(b.values))


def test_header_v3_with_pose_streams_is_rejected() -> None:
    v3 = _as_header_v3(SAMPLE.read_bytes(), pose_streams=2)
    with pytest.raises(ValueError, match="pose stream"):
        detect_anm2_format(v3)
