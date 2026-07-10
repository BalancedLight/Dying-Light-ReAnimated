from __future__ import annotations

import json
import os
from pathlib import Path

import numpy as np
import pytest

from dlanm2_gui.anm2 import Anm2Header
from dlanm2_gui.anm2_components import decode_file_samples
from dlanm2_gui.oracle.binary_fbx_mixamo import _FbxDocument
from dlanm2_gui.oracle.custom_fbx_release_candidate_editor_rpack import (
    FINGER_CHAINS,
    FINGER_MAP,
    build_custom_fbx_release_candidate_editor_rpack,
)
from dlanm2_gui.oracle.smd_bind_pose import parse_smd_bind_pose


def _asset_root() -> Path:
    return Path(os.environ.get("DLR_TEST_ASSET_ROOT", "/mnt/data"))


@pytest.fixture(scope="module")
def assets() -> dict[str, object]:
    root = _asset_root()
    values: dict[str, object] = {
        "animation_fbxs": [
            root / "Standing Greeting.fbx",
            root / "Hip Hop Dancing.fbx",
            root / "Right Turn - Binary.fbx",
            root / "Taunt.fbx",
            root / "Walk Strafe Left.fbx",
            root / "Crouch To Stand.fbx",
        ],
        "source_rest_fbx": root / "T-Pose.fbx",
        "trusted_source_rest_json": Path("reference/same_model_tpose_20260619.json"),
        "canonical_smd": Path("reference/player_1_tpp.smd"),
        "target_template_anm2": Path("reference/infected_turn_90r.template.anm2"),
        "stock_writer_control_anm2": Path("reference/stock_writer_control.anm2"),
    }
    paths = [*values["animation_fbxs"]]  # type: ignore[arg-type]
    paths.extend(
        value for key, value in values.items()
        if key != "animation_fbxs" and isinstance(value, Path)
    )
    if not all(path.exists() for path in paths):
        pytest.skip("external FBX/SMD/ANM2 assets are not present")
    return values


@pytest.fixture(scope="module")
def built_package(
    tmp_path_factory: pytest.TempPathFactory,
    assets: dict[str, object],
) -> tuple[dict, Path]:
    out = tmp_path_factory.mktemp("custom_fbx_release_candidate")
    report = build_custom_fbx_release_candidate_editor_rpack(
        **assets,  # type: ignore[arg-type]
        out_dir=out,
    )
    return report, out


def test_mixamo_unit_scale_is_centimeters(assets: dict[str, object]) -> None:
    document = _FbxDocument(Path(assets["source_rest_fbx"]))
    assert document.meters_per_unit == pytest.approx(0.01)


def test_pack_contains_controls_and_two_variants_per_clip(
    built_package: tuple[dict, Path],
) -> None:
    report, _out = built_package
    assert report["status"] == "ok"
    assert report["pack"]["pack_name"] == "common_anims_sp_pc.rpack"
    assert report["pack"]["forbidden_common_anims_PC_produced"] is False
    assert report["pack"]["animation_count"] == 14
    assert report["pack"]["animation_scripts"] == ["anims_man_all_DLC60"]
    assert report["pack"]["missing_resources"] == []


def test_finger_mapping_covers_all_30_anm2_digit_tracks() -> None:
    assert len(FINGER_MAP) == 30
    assert len(FINGER_CHAINS) == 10
    assert {name[:2] for name in FINGER_MAP} == {"l_", "r_"}
    assert "r_finger01" in FINGER_MAP
    assert "r_finger43" in FINGER_MAP
    assert not any(source.endswith("4") for source in FINGER_MAP.values())
    assert all(chain.source_joints[-1].endswith("4") for chain in FINGER_CHAINS)


def test_target_finger_root_parent_routes_match_smd_hierarchy(assets: dict[str, object]) -> None:
    expected = {
        "thumb": "hand",
        "index": "hand",
        "middle": "hand",
        "ring": "hand1",
        "pinky": "hand1",
    }
    pose = parse_smd_bind_pose(assets["canonical_smd"])
    by_index = pose.by_index
    for chain in FINGER_CHAINS:
        suffix = "l" if chain.side == "left" else "r"
        expected_parent = f"{suffix}_{expected[chain.digit]}"
        assert chain.target_root_parent == expected_parent
        root = pose.by_name[chain.target_bones[0]]
        assert by_index[root.parent_index].name == expected_parent


def test_finger_strategy_is_absolute_not_bind_biased(
    built_package: tuple[dict, Path],
) -> None:
    _report, out = built_package
    rows = json.loads((out / "retarget_candidate_summary.json").read_text())
    for row in rows:
        details = row["finger_details"]
        assert details["strategy"] == "absolute_anatomical_palm_direction"
        assert details["source_rest_maps_to_target_bind"] is False
        assert details["target_hand1_helpers_animated"] is False
        assert details["target_root_parents"]["l_finger31"] == "l_hand1"
        assert details["target_root_parents"]["l_finger41"] == "l_hand1"
        assert details["target_root_parents"]["r_finger31"] == "r_hand1"
        assert details["target_root_parents"]["r_finger41"] == "r_hand1"


def test_candidates_keep_helpers_static_and_only_expected_root_tracks_move(
    built_package: tuple[dict, Path],
) -> None:
    _report, out = built_package
    rows = json.loads((out / "retarget_candidate_summary.json").read_text())
    assert len(rows) == 12
    for row in rows:
        assert row["helper_tracks_animated"] == []
        assert row["unintended_moving_named_tracks"] == []
        assert row["frame0_max_component_delta_from_smd_bind"] > 1.0e-3
        if row["root_policy"] == "inplace":
            assert row["motion_summary"]["ccc3_translation_dynamic"] is False
        else:
            assert row["motion_summary"]["ccc3_translation_dynamic"] is True
            assert row["motion_summary"]["bip01_translation_dynamic"] is True


def test_greeting_candidate_adds_real_finger_motion(
    built_package: tuple[dict, Path],
) -> None:
    _report, out = built_package
    rows = {
        row["candidate_name"]: row
        for row in json.loads((out / "retarget_candidate_summary.json").read_text())
    }
    moving = set(rows["greeting_inplace"]["moving_finger_tracks"])
    assert len(moving) >= 20
    assert any(name.startswith("r_finger") for name in moving)
    assert any(name.startswith("l_finger") for name in moving)
    parity = rows["greeting_inplace"]["finger_direction_parity"]
    assert parity["status"] == "ok"
    assert parity["max_angular_delta_degrees"] < 1.0e-4


def test_motion_variants_encode_expected_movement_ranges(
    built_package: tuple[dict, Path],
) -> None:
    _report, out = built_package
    rows = {
        row["candidate_name"]: row
        for row in json.loads((out / "retarget_candidate_summary.json").read_text())
    }
    strafe = rows["strafeleft_motion"]["motion_summary"]
    assert max(abs(value) for value in strafe["mapped_motion_end"]) > 0.85
    crouch = rows["crouchstand_motion"]["motion_summary"]
    assert crouch["mapped_pose_offset_range"][1] > 0.50
    turn = rows["rightturn_motion"]["motion_summary"]
    assert turn["ccc3_rotation_vector_range"][1] > 0.35


def test_inplace_and_motion_payloads_have_same_duration_but_different_root_data(
    built_package: tuple[dict, Path],
) -> None:
    _report, out = built_package
    inplace = out / "candidates" / "strafeleft_inplace" / "candidate.anm2"
    motion = out / "candidates" / "strafeleft_motion" / "candidate.anm2"
    inplace_header = Anm2Header.parse(inplace.read_bytes())
    motion_header = Anm2Header.parse(motion.read_bytes())
    assert inplace_header.frame_count == motion_header.frame_count == 45

    frame_times = list(range(inplace_header.frame_count))
    inplace_sample = decode_file_samples(inplace, frame_times)
    motion_sample = decode_file_samples(motion, frame_times)
    # Descriptor index 1 is the known non-mesh motion accumulator 0xCCC3CDDF.
    inplace_values = np.asarray([frame.tracks[1][3:6] for frame in inplace_sample.frames])
    motion_values = np.asarray([frame.tracks[1][3:6] for frame in motion_sample.frames])
    assert np.max(np.ptp(inplace_values, axis=0)) < 1.0e-6
    assert np.max(np.ptp(motion_values, axis=0)) > 0.85


def test_all_root_policy_toggles_and_ik_sidecar(
    tmp_path: Path,
    assets: dict[str, object],
) -> None:
    report = build_custom_fbx_release_candidate_editor_rpack(
        animation_fbxs=[assets["animation_fbxs"][0]],  # type: ignore[index]
        source_rest_fbx=assets["source_rest_fbx"],
        trusted_source_rest_json=assets["trusted_source_rest_json"],
        canonical_smd=assets["canonical_smd"],
        target_template_anm2=assets["target_template_anm2"],
        stock_writer_control_anm2=assets["stock_writer_control_anm2"],
        out_dir=tmp_path,
        root_policies=("inplace", "bip01", "motion"),
        ik_authoring_preset="runtime",
    )
    assert report["root_policies"] == ["inplace", "bip01", "motion"]
    assert report["pack"]["animation_count"] == 5
    candidates = {
        row["candidate_name"]: row
        for row in json.loads((tmp_path / "retarget_candidate_summary.json").read_text())
    }
    assert candidates["greeting_bip01"]["motion_summary"]["bip01_translation_dynamic"] is True
    assert candidates["greeting_bip01"]["motion_summary"]["ccc3_translation_dynamic"] is False
    assert candidates["greeting_motion"]["motion_summary"]["ccc3_translation_dynamic"] is True
    presets = json.loads((tmp_path / "movie_authoring_presets.json").read_text())
    assert presets["ik_authoring_preset"] == "runtime"
    assert presets["root_motion_presets"]["motion"]["movie_use_offset_helper"] is True
