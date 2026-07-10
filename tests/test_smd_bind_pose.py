from __future__ import annotations

from pathlib import Path

import numpy as np
import pytest

from dlanm2_gui.oracle.smd_bind_pose import (
    anm2_cayley_vector_from_quaternion,
    parse_smd_bind_pose,
    quaternion_wxyz_from_matrix,
    smd_extrinsic_xyz_matrix,
    validate_smd_against_ascii,
)
from dlanm2_gui.trackmap import dl_name_hash, read_track_descriptors

FIXTURES = Path(__file__).parent / "fixtures"
STOCK_IDLE = Path("exports/oracle/offline_engine_evaluator/stock_anm2_corpus/dd2fd87d_000598_infected_idle_01.anm2")


def test_smd_parser_reads_full_bind_skeleton() -> None:
    pose = parse_smd_bind_pose(FIXTURES / "player_1_tpp_bind.smd")
    assert len(pose.bones) == 106
    assert pose.by_name["r_forearm"].parent_index == pose.by_name["r_upperarm"].index


def test_smd_extrinsic_xyz_reconstructs_ascii_globals() -> None:
    report = validate_smd_against_ascii(
        FIXTURES / "player_1_tpp_bind.smd",
        FIXTURES / "player_1_tpp_bind_skeleton.ascii",
    )
    assert report["status"] == "ok"
    assert report["max_position_delta"] < 3.0e-6
    assert report["rotation_convention"] == "extrinsic_xyz_radians; matrix=Rz@Ry@Rx"


def test_smd_names_cover_69_of_70_stock_tracks() -> None:
    if not STOCK_IDLE.exists():
        pytest.skip("stock idle corpus is not present")
    pose = parse_smd_bind_pose(FIXTURES / "player_1_tpp_bind.smd")
    _header, descriptors = read_track_descriptors(STOCK_IDLE)
    hashes = {dl_name_hash(bone.name) for bone in pose.bones}
    matched = [descriptor for descriptor in descriptors if descriptor in hashes]
    unmatched = [descriptor for descriptor in descriptors if descriptor not in hashes]
    assert len(matched) == 69
    assert unmatched == [0xCCC3CDDF]


def test_pelvis_smd_rotation_matches_engine_cayley_vector() -> None:
    pose = parse_smd_bind_pose(FIXTURES / "player_1_tpp_bind.smd")
    pelvis = pose.by_name["pelvis"]
    quaternion = quaternion_wxyz_from_matrix(smd_extrinsic_xyz_matrix(pelvis.euler_xyz_radians))
    vector = anm2_cayley_vector_from_quaternion(quaternion)
    assert np.allclose(vector, (-1.0 / 3.0, -1.0 / 3.0, 1.0 / 3.0), atol=2.0e-6)
