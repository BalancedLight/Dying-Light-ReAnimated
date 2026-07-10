from __future__ import annotations

import json
import os
from pathlib import Path

import numpy as np
import pytest

from dlanm2_gui.oracle.binary_fbx_mixamo import _axis_rotation, _euler_matrix
from dlanm2_gui.oracle.custom_fbx_smd_intrinsic_absolute_editor_rpack import (
    CANDIDATES,
    build_custom_fbx_smd_intrinsic_absolute_editor_rpack,
    validate_fbx_intrinsic_euler_against_trusted_rest,
)
from dlanm2_gui.oracle.custom_fbx_smd_two_vector_fullbody_editor_rpack import HELPER_TRACKS


def _asset_root() -> Path:
    return Path(os.environ.get("DLR_TEST_ASSET_ROOT", "/mnt/data"))


@pytest.fixture(scope="module")
def assets() -> dict[str, Path]:
    root = _asset_root()
    values = {
        "animation_fbx": root / "Standing Greeting.fbx",
        "source_rest_fbx": root / "T-Pose.fbx",
        "trusted_source_rest_json": Path("reference/same_model_tpose_20260619.json"),
        "canonical_smd": Path("reference/player_1_tpp.smd"),
        "target_template_anm2": Path("reference/infected_turn_90r.template.anm2"),
        "stock_writer_control_anm2": Path("reference/stock_writer_control.anm2"),
    }
    if not all(path.exists() for path in values.values()):
        pytest.skip("external FBX/SMD/ANM2 assets are not present")
    return values


@pytest.fixture(scope="module")
def built_package(
    tmp_path_factory: pytest.TempPathFactory,
    assets: dict[str, Path],
) -> tuple[dict, Path]:
    out = tmp_path_factory.mktemp("custom_fbx_smd_intrinsic_absolute")
    report = build_custom_fbx_smd_intrinsic_absolute_editor_rpack(
        **assets,
        out_dir=out,
    )
    return report, out


def test_xyz_euler_uses_intrinsic_fbx_order() -> None:
    angles = np.asarray((17.0, -29.0, 41.0), dtype=float)
    observed = _euler_matrix(angles, "XYZ")
    expected = (
        _axis_rotation("Z", angles[2])
        @ _axis_rotation("Y", angles[1])
        @ _axis_rotation("X", angles[0])
    )
    legacy = (
        _axis_rotation("X", angles[0])
        @ _axis_rotation("Y", angles[1])
        @ _axis_rotation("Z", angles[2])
    )
    assert np.allclose(observed, expected, atol=1.0e-12)
    assert not np.allclose(observed, legacy, atol=1.0e-5)


def test_corrected_tpose_matches_trusted_blender_rest(assets: dict[str, Path]) -> None:
    report = validate_fbx_intrinsic_euler_against_trusted_rest(
        source_rest_fbx=assets["source_rest_fbx"],
        trusted_source_rest_json=assets["trusted_source_rest_json"],
    )
    assert report["bone_count"] == 65
    assert report["fixed_max_abs_matrix_delta"] < 1.0e-3
    assert report["fixed_max_position_error"] < 1.0e-3
    assert report["legacy_max_position_error"] > 10.0
    right_forearm = report["critical_right_arm_positions"]["mixamorig:RightForeArm"]
    assert right_forearm["fixed_position_error"] < 1.0e-3
    assert right_forearm["legacy_position_error"] > 10.0


def test_pack_contains_controls_right_arm_and_fullbody_resources(
    built_package: tuple[dict, Path],
) -> None:
    report, _out = built_package
    assert report["status"] == "ok"
    assert report["pack"]["pack_name"] == "common_anims_sp_pc.rpack"
    assert report["pack"]["forbidden_common_anims_PC_produced"] is False
    assert report["pack"]["animation_count"] == 8
    assert report["pack"]["animation_scripts"] == ["anims_man_all_DLC60"]
    expected = {candidate.resource_name for candidate in CANDIDATES}
    expected.update({
        "dl_reanimated_fbxfix_stock_rebuilt_control",
        "dl_reanimated_fbxfix_target_bind_control",
    })
    assert set(report["pack"]["animations"]) == expected


def test_custom_frame0_preserves_source_pose_instead_of_target_bind(
    built_package: tuple[dict, Path],
) -> None:
    _report, out = built_package
    rows = json.loads((out / "retarget_candidate_summary.json").read_text())
    for row in rows:
        # This experiment intentionally begins in Standing Greeting frame 0,
        # rather than collapsing the animation to the Dying Light bind pose.
        assert row["frame0_max_component_delta_from_smd_bind"] > 1.0e-3
        assert row["first_frame_policy"] == "source animation frame 0 retained, not replaced by target bind"
        assert row["unintended_moving_named_tracks"] == []
        assert row["helper_tracks_animated"] == []
        assert HELPER_TRACKS.isdisjoint(row["moving_named_tracks"])


def test_corrected_right_arm_transfers_frame70_body_space_directions(
    built_package: tuple[dict, Path],
) -> None:
    _report, out = built_package
    rows = {
        row["candidate_name"]: row
        for row in json.loads((out / "retarget_candidate_summary.json").read_text())
    }
    detail = rows["right_arm_absolute_locked"]["limb_details"]["right_arm"]
    assert detail["frame70_root_direction_parity_error"] < 1.0e-10
    assert detail["frame70_mid_direction_parity_error"] < 1.0e-10
    source_root = np.asarray(detail["source_frame70_root_direction_body"], dtype=float)
    source_mid = np.asarray(detail["source_frame70_mid_direction_body"], dtype=float)
    # Correctly parsed source: upper arm mostly outward/horizontal; forearm
    # folds inward and upward toward the head.
    assert source_root[0] > 0.90
    assert abs(source_root[1]) < 0.10
    assert source_mid[0] < -0.50
    assert source_mid[1] > 0.55
    assert 110.0 < detail["source_frame70_angle_degrees"] < 125.0


def test_fullbody_candidates_move_both_arms_legs_and_torso(
    built_package: tuple[dict, Path],
) -> None:
    _report, out = built_package
    rows = {
        row["candidate_name"]: row
        for row in json.loads((out / "retarget_candidate_summary.json").read_text())
    }
    limbs = set(rows["fullbody_absolute_limbs"]["moving_named_tracks"])
    assert {"l_upperarm", "l_forearm", "r_upperarm", "r_forearm"}.issubset(limbs)
    assert {"l_thigh", "l_calf", "r_thigh", "r_calf"}.issubset(limbs)
    full = set(rows["fullbody_absolute_primary"]["moving_named_tracks"])
    assert {"pelvis", "hspine", "spine1", "spine3", "neck", "head"}.issubset(full)
    terminal = set(rows["fullbody_absolute_hands_feet"]["moving_named_tracks"])
    assert {"l_hand", "r_hand", "l_foot", "r_foot"}.issubset(terminal)


def test_rebuild_is_byte_reproducible(
    built_package: tuple[dict, Path],
    assets: dict[str, Path],
    tmp_path: Path,
) -> None:
    report, _out = built_package
    second = build_custom_fbx_smd_intrinsic_absolute_editor_rpack(
        **assets,
        out_dir=tmp_path / "rebuild",
    )
    assert second["pack"]["sha256"] == report["pack"]["sha256"]
