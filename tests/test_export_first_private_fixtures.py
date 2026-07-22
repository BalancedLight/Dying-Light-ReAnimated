from __future__ import annotations

from pathlib import Path
import math

import numpy as np
import pytest

from dlanm2_gui.anm2_fbx import decode_anm2_animation
from dlanm2_gui.blender_fbx import discover_blender, export_anm2_to_fbx
from dlanm2_gui.chrome_rig import ChromeRig
from dlanm2_gui.fbx_core import FbxDocument
from dlanm2_gui.fbx_preflight import preflight_fbx
from dlanm2_gui.game_profiles import DL2_GAME_ID
from dlanm2_gui.automatic_retarget import build_verified_dl2_advanced_body_map
from dlanm2_gui.retarget_engines.exact_rig import build_exact_rig_anm2
from dlanm2_gui.retarget_engines.legacy_exact_rig import (
    _dlr_native_metadata,
    _native_sparse_helper_to_game_local,
)
from dlanm2_gui.target_retarget_policy import build_target_retarget_policy


ROOT = Path(__file__).resolve().parents[1]
LEFT_HAND_JUMP = Path(r"S:\Downloads\left_hand_jump_test.fbx")
LEFT_HAND_JUMP_STACK = "Armature|m_fpp_unarmed_jumpsprint_mirror_Armature"
ALONE_TEST_FALL = Path(r"S:\Downloads\AloneTestFall.fbx")


@pytest.mark.skipif(
    not LEFT_HAND_JUMP.is_file(),
    reason="private left_hand_jump_test.fbx is not available",
)
def test_left_hand_jump_export_first_regression(tmp_path: Path) -> None:
    rig = ChromeRig.load(ROOT / "reference" / "dl2" / "player_skeleton.crig")
    document = FbxDocument(
        LEFT_HAND_JUMP,
        animation_stack=LEFT_HAND_JUMP_STACK,
        purpose="animation",
    )

    assert len(document.limb_models) == 76
    assert document.declared_timebase.time_mode == 11
    assert document.declared_fps == pytest.approx(24.0)
    assert document.frame_count(fps=document.declared_fps) == 305
    assert document.selected_animation_stack is not None
    assert document.selected_animation_stack.name == LEFT_HAND_JUMP_STACK
    diagnostics = document.bind_diagnostics()
    assert diagnostics["selected_bind_source"] == "Pose::BindPose"
    assert diagnostics["bind_coverage"]["authoritative"] == 76
    assert diagnostics["bind_coverage"]["total"] == 76
    assert diagnostics["bind_coverage"]["Pose::BindPose"] == 76
    assert diagnostics["bind_coverage"]["ModelTransformsFallback"] == 0

    contract = document.transform_contract
    assert contract.common_wrapper_models == ("Armature",)
    assert contract.common_wrapper_is_static
    assert contract.common_wrapper_is_uniform
    assert contract.common_wrapper_is_reflected
    assert contract.canonicalized_wrapper_reflection
    assert contract.local_reflected_bones == ()
    canonical = contract.canonical_transform_validation
    assert canonical["sample_count"] == 305 * 76
    assert canonical["negative_determinants"] == 0
    assert canonical["singular"] == 0
    assert canonical["non_finite"] == 0
    assert canonical["minimum_determinant"] == pytest.approx(
        0.9999917,
        rel=2.0e-6,
    )
    assert canonical["maximum_shear"] < 3.0e-5

    preflight = preflight_fbx(
        LEFT_HAND_JUMP,
        purpose="animation",
        animation_stack=LEFT_HAND_JUMP_STACK,
        target_rig=rig,
        game_id=DL2_GAME_ID,
        document=document,
    )
    assert not preflight.import_blocking
    assert not any(
        row.code == "reflected_or_negative_bone_scale"
        for row in preflight.findings
    )
    assert preflight.readiness_level == "ready"
    assert preflight.readiness_label == (
        "Ready — automatically repaired wrapper transform"
    )
    compatibility = preflight.inventory["target_compatibility"]
    assert compatibility["classification"] == "exact_target_subset"
    assert compatibility["exact_target_subset_rows"] == 76
    assert compatibility["target_bind_rows"] == 195
    assert compatibility["extra_source_bones"] == []
    assert compatibility["hierarchy_mismatches"] == []

    policy = build_target_retarget_policy(rig, game_id=DL2_GAME_ID)
    verified_map = build_verified_dl2_advanced_body_map(document, rig, policy)
    certificate = verified_map.extensions["automatic_retarget_certificate"]
    assert certificate["exact_target_subset_rows"] == 76
    assert certificate["semantic_rows"] == 0
    assert certificate["target_bind_rows"] == 195
    assert certificate["spatial_only_row_count"] == 0
    assert sum(bool(row.source_fbx_bone) for row in verified_map.pairs) == 76
    direct_by_target = {
        row.target_rig_bone: row.source_fbx_bone for row in verified_map.pairs
    }
    assert direct_by_target["l_foretwist"] == "l_foretwist"
    assert direct_by_target["refcamera"] == "refcamera"

    build = build_exact_rig_anm2(
        LEFT_HAND_JUMP,
        rig,
        fps=document.declared_fps,
        animation_stack=LEFT_HAND_JUMP_STACK,
        document=document,
    )
    assert build.frame_count == 305
    assert build.report["preflight_policy"] == "export_first_v1"
    assert build.report["wrapper_canonicalization"]["applied"] is True
    assert build.report["mapping"] == {
        "exact_target_subset_rows": 76,
        "semantic_rows": 0,
        "manual_target_overrides": 0,
        "target_bind_rows": 195,
        "spatial_only_rows": 0,
    }
    assert build.report["canonical_transform_validation"]["sample_count"] == 23180

    output = tmp_path / "left_hand_jump_test.anm2"
    output.write_bytes(build.payload)
    decoded = decode_anm2_animation(output, fps=document.declared_fps)
    assert decoded.frame_count == 305
    assert np.isfinite(decoded.values).all()

    blender = discover_blender()
    if blender is None:
        pytest.skip("Blender is not installed for the private round-trip audit")
    reverse_fbx = tmp_path / "left_hand_jump_test.fbx"
    reverse = export_anm2_to_fbx(
        output,
        rig,
        reverse_fbx,
        anm2_input_fps=24.0,
        fbx_output_fps=24.0,
        blender_executable=blender,
    )
    assert reverse.frame_count == 305
    assert reverse.root_parity_max_angular_degrees <= 0.05
    assert reverse.root_parity_max_heading_degrees <= 0.05
    assert reverse.root_parity_max_translation_m <= 1.0e-5
    assert reverse.native_rest_basis_max_rotation_degrees >= 0.0

    reverse_document = FbxDocument(reverse_fbx, purpose="animation")
    reverse_document.select_animation_stack()
    assert reverse_document.declared_fps == pytest.approx(24.0)
    assert reverse_document.frame_count(24.0) == 305
    motion_helper = "DLR_OffsetHelper_CCC3CDDF"
    assert motion_helper in reverse_document.null_models
    helper_id = reverse_document.null_models[motion_helper]
    reverse_ticks = reverse_document.frame_ticks(24.0)
    for tick in (reverse_ticks[0], reverse_ticks[-1]):
        helper_game = _native_sparse_helper_to_game_local(
            reverse_document._local_matrix(
                helper_id, tick=tick, use_animation=True
            ),
            meters_per_unit=reverse_document.meters_per_unit,
        )
        assert helper_game == pytest.approx(np.eye(4), abs=2.0e-5)
    source_duration = (
        document.animation_stop_tick - document.animation_start_tick
    ) / 46_186_158_000.0
    reverse_duration = (
        reverse_document.animation_stop_tick - reverse_document.animation_start_tick
    ) / 46_186_158_000.0
    assert abs(reverse_duration - source_duration) <= 1.0 / 24.0

    metadata = _dlr_native_metadata(reverse_document)
    assert metadata["basis_mode"] == "child_pivot_display_v1"
    assert any(
        row["status"] == "display_delta"
        for row in metadata["native_rest_basis_errors"].values()
    )
    assert any(
        not np.allclose(
            np.asarray(values, dtype=float).reshape(4, 4), np.eye(4), atol=1.0e-8
        )
        for values in metadata["display_basis_corrections"].values()
    )

    pelvis = rig.bones[rig.root_index]
    pelvis_track = decoded.descriptors.index(pelvis.descriptor)
    quaternions = decoded.quaternions_wxyz[:, pelvis_track]

    def multiply(left: np.ndarray, right: np.ndarray) -> np.ndarray:
        w, x, y, z = left
        rw, rx, ry, rz = right
        return np.asarray((
            w * rw - x * rx - y * ry - z * rz,
            w * rx + x * rw + y * rz - z * ry,
            w * ry - x * rz + y * rw + z * rx,
            w * rz + x * ry - y * rx + z * rw,
        ))

    first = quaternions[0] / np.linalg.norm(quaternions[0])
    inverse_first = first * np.asarray((1.0, -1.0, -1.0, -1.0))
    up = np.asarray((0.0, 1.0, 0.0))
    swing_angles = []
    for quaternion in quaternions:
        relative = multiply(quaternion / np.linalg.norm(quaternion), inverse_first)
        relative /= np.linalg.norm(relative)
        projected = up * float(relative[1:] @ up)
        twist = np.concatenate(([relative[0]], projected))
        twist /= np.linalg.norm(twist)
        inverse_twist = twist * np.asarray((1.0, -1.0, -1.0, -1.0))
        swing = multiply(relative, inverse_twist)
        swing /= np.linalg.norm(swing)
        swing_angles.append(
            math.degrees(2.0 * math.acos(np.clip(abs(swing[0]), 0.0, 1.0)))
        )
    assert max(swing_angles) == pytest.approx(84.689, abs=0.1)


@pytest.mark.skipif(
    not ALONE_TEST_FALL.is_file(),
    reason="private AloneTestFall.fbx is not available",
)
def test_alone_test_fall_remains_readable_under_export_first_preflight() -> None:
    stack = "Armature.007|Armature.007Action"
    rig = ChromeRig.load(ROOT / "reference" / "dl2" / "player_skeleton.crig")
    document = FbxDocument(
        ALONE_TEST_FALL, animation_stack=stack, purpose="animation"
    )
    report = preflight_fbx(
        ALONE_TEST_FALL,
        purpose="animation",
        animation_stack=stack,
        target_rig=rig,
        game_id=DL2_GAME_ID,
        document=document,
    )
    compatibility = report.inventory["target_compatibility"]

    assert not report.blocking
    assert not report.import_blocking
    assert report.readiness_level == "ready"
    assert compatibility["hierarchy_mismatches"] == []
    assert compatibility["optional_hierarchy_mismatches_held_at_bind"] == [
        {
            "bone": "headend",
            "expected_target_parent": "head",
            "source_target_ancestor": None,
        }
    ]

    policy = build_target_retarget_policy(rig, game_id=DL2_GAME_ID)
    verified_map = build_verified_dl2_advanced_body_map(document, rig, policy)
    certificate = verified_map.extensions["automatic_retarget_certificate"]
    assert certificate["exact_target_subset_rows"] == 78
    assert certificate["target_bind_rows"] == 193
    assert certificate["spatial_only_row_count"] == 0
