from __future__ import annotations

from pathlib import Path
import hashlib
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
LEFT_WHISTLE = Path(r"S:\Downloads\left_whistle_test.fbx")
DL2TEST = Path(r"S:\Downloads\dl2test.fbx")
PINHEAD_RET = Path(r"S:\Downloads\0_m_fpp_unarmed_dash_pinhead_ret.anm2")
TRUE_050_LEFT_WHISTLE = (
    ROOT / "build" / "comparison" / "true_0_5_left_whistle.anm2"
)
KNOWN_GOOD_LEFT_WHISTLE = ROOT / "build" / "dl_reanimated_left_whistle_test.anm2"
KNOWN_GOOD_DL2TEST = ROOT / "build" / "dl_reanimated_dl2test.anm2"
ROUNDTRIP_WHISTLE = ROOT / "build" / "dl_reanimated_testing.anm2"
CURRENT_WHISTLE_CONTROL = (
    ROOT / "build" / "new" / "dl_reanimated_left_whistle_test.anm2"
)


def _decoded_payload(tmp_path: Path, name: str, payload: bytes, fps: float = 24.0):
    path = tmp_path / name
    path.write_bytes(payload)
    return decode_anm2_animation(path, fps=fps)


def _angular_errors(left, right) -> np.ndarray:
    frame_count = min(left.frame_count, right.frame_count)
    dots = np.clip(
        np.abs(
            np.sum(
                left.quaternions_wxyz[:frame_count]
                * right.quaternions_wxyz[:frame_count],
                axis=2,
            )
        ),
        0.0,
        1.0,
    )
    return np.degrees(2.0 * np.arccos(dots))


def _rotation_displacement_curve(decoded, track: int) -> np.ndarray:
    values = decoded.quaternions_wxyz[:, track]
    first = np.broadcast_to(values[0], values.shape)
    dots = np.clip(np.abs(np.sum(values * first, axis=1)), 0.0, 1.0)
    return np.degrees(2.0 * np.arccos(dots))


def _curve_correlation(left: np.ndarray, right: np.ndarray) -> float:
    if np.std(left) <= 1.0e-10 or np.std(right) <= 1.0e-10:
        return 1.0 if np.allclose(left, right, atol=1.0e-5) else 0.0
    return float(np.corrcoef(left, right)[0, 1])


@pytest.mark.skipif(
    not LEFT_WHISTLE.is_file() or not TRUE_050_LEFT_WHISTLE.is_file(),
    reason="private whistle FBX or isolated tag 0.5.0 artifact is unavailable",
)
def test_left_whistle_legacy_matches_real_tag_0_5_0() -> None:
    rig = ChromeRig.load(ROOT / "reference" / "dl2" / "player_skeleton.crig")
    build = build_exact_rig_anm2(
        LEFT_WHISTLE,
        rig,
        fps=24.0,
        root_mapping={
            "source_bone": "pelvis",
            "target_bone": "pelvis",
        },
        root_policy="bip01",
        fbx_anm2_export_behavior="legacy_5_0",
    )

    historical = TRUE_050_LEFT_WHISTLE.read_bytes()
    assert build.payload == historical
    assert (
        hashlib.sha256(build.payload).hexdigest()
        == "6f0335b69dfbc1ad85f28f38f99d72635b92cf66b84b59bac862de902abd5f33"
    )
    assert build.report["sampler_contract"] == (
        "dlr_0_5_0_global_bind_basis_v1"
    )
    assert build.report["wrapper_scale_normalization_factor"] == pytest.approx(
        0.01
    )
    assert build.report["effective_post_wrapper_translation_scale"] == pytest.approx(
        1.0
    )


@pytest.mark.skipif(
    not LEFT_WHISTLE.is_file() or not KNOWN_GOOD_LEFT_WHISTLE.is_file(),
    reason="private whistle FBX or known-good legacy whistle ANM2 is unavailable",
)
def test_left_whistle_current_v2_preserves_same_side_motion(
    tmp_path: Path,
) -> None:
    rig = ChromeRig.load(ROOT / "reference" / "dl2" / "player_skeleton.crig")
    build = build_exact_rig_anm2(
        LEFT_WHISTLE,
        rig,
        fps=24.0,
        root_mapping={"source_bone": "pelvis", "target_bone": "pelvis"},
        root_motion={
            "motion_mode": "motion_accumulator",
            "heading_mode": "to_motion_accumulator",
        },
        bilateral_semantic_policy="auto",
    )
    current = _decoded_payload(
        tmp_path, "corrected_whistle.anm2", build.payload
    )
    legacy = decode_anm2_animation(KNOWN_GOOD_LEFT_WHISTLE, fps=24.0)
    assert current.frame_count == legacy.frame_count == 73
    assert current.track_count == legacy.track_count == 368
    assert build.report["sampler_contract"] == "dlr_current_normalized_global_v2"
    assert build.report["bilateral_semantic_policy"] == "auto"
    assert build.report["bilateral_swapped_row_count"] == 0
    assert not build.report["bilateral_swap_applied"]
    assert not build.report[
        "post_canonicalization_mirror_conjugation_applied"
    ]
    assert build.report["bilateral_semantic_decision"][
        "same_side_votes"
    ] == 7

    angular = _angular_errors(current, legacy)
    assert np.isfinite(angular).all()
    names_by_descriptor = {
        bone.descriptor: bone.name for bone in rig.bones
    }
    names = [
        names_by_descriptor.get(descriptor, f"#{descriptor}")
        for descriptor in current.descriptors
    ]
    translation = np.linalg.norm(
        current.values[:, :, 3:6] - legacy.values[:, :, 3:6],
        axis=2,
    )
    non_root = [index for index, name in enumerate(names) if name != "pelvis"]
    assert float(np.max(translation[:, non_root], initial=0.0)) <= 1.0e-5
    # Root accumulator implementation differs from the frozen legacy oracle;
    # report that one named row separately instead of loosening any body row.
    pelvis = names.index("pelvis")
    assert float(np.max(translation[:, pelvis], initial=0.0)) <= 0.12

    for stem in ("clavicle", "upperarm", "forearm", "hand", "finger11"):
        for side, opposite in (("l", "r"), ("r", "l")):
            current_track = names.index(f"{side}_{stem}")
            same_track = names.index(f"{side}_{stem}")
            cross_track = names.index(f"{opposite}_{stem}")
            curve = _rotation_displacement_curve(current, current_track)
            same = _curve_correlation(
                curve, _rotation_displacement_curve(legacy, same_track)
            )
            cross = _curve_correlation(
                curve, _rotation_displacement_curve(legacy, cross_track)
            )
            assert same + 0.05 >= cross, (
                f"{side}_{stem}: same-side correlation {same:.6f}, "
                f"cross-side correlation {cross:.6f}"
            )


@pytest.mark.skipif(
    not LEFT_WHISTLE.is_file() or not KNOWN_GOOD_LEFT_WHISTLE.is_file(),
    reason="private whistle ablation inputs are unavailable",
)
def test_left_whistle_four_way_semantic_and_conjugation_ablation(
    tmp_path: Path,
) -> None:
    rig = ChromeRig.load(ROOT / "reference" / "dl2" / "player_skeleton.crig")
    document = FbxDocument(LEFT_WHISTLE, purpose="animation")
    preflight = preflight_fbx(
        LEFT_WHISTLE,
        purpose="animation",
        target_rig=rig,
        game_id=DL2_GAME_ID,
        document=document,
    )
    cases = {
        "A": ("swap_bilateral_explicit", True),
        "B": ("preserve_source_names", True),
        "C": ("swap_bilateral_explicit", False),
        "D": ("preserve_source_names", False),
    }
    legacy = decode_anm2_animation(KNOWN_GOOD_LEFT_WHISTLE, fps=24.0)
    mean_rotation_error: dict[str, float] = {}
    reports = {}
    for label, (semantic_policy, conjugate) in cases.items():
        build = build_exact_rig_anm2(
            LEFT_WHISTLE,
            rig,
            fps=24.0,
            document=document,
            preflight=preflight,
            root_mapping={"source_bone": "pelvis", "target_bone": "pelvis"},
            root_motion={
                "motion_mode": "motion_accumulator",
                "heading_mode": "to_motion_accumulator",
            },
            bilateral_semantic_policy=semantic_policy,
            diagnostic_post_canonicalization_mirror_conjugation=conjugate,
        )
        decoded = _decoded_payload(
            tmp_path, f"whistle_ablation_{label}.anm2", build.payload
        )
        mean_rotation_error[label] = float(
            np.mean(_angular_errors(decoded, legacy))
        )
        reports[label] = build.report

    assert reports["A"]["bilateral_swapped_row_count"] > 0
    assert reports["B"]["bilateral_swapped_row_count"] == 0
    assert reports["C"]["bilateral_swapped_row_count"] > 0
    assert reports["D"]["bilateral_swapped_row_count"] == 0
    assert reports["A"][
        "post_canonicalization_mirror_conjugation_applied"
    ]
    assert reports["B"][
        "post_canonicalization_mirror_conjugation_applied"
    ]
    assert not reports["C"][
        "post_canonicalization_mirror_conjugation_applied"
    ]
    assert not reports["D"][
        "post_canonicalization_mirror_conjugation_applied"
    ]
    assert mean_rotation_error["D"] < mean_rotation_error["A"]
    assert mean_rotation_error["D"] < mean_rotation_error["C"]
    assert mean_rotation_error["D"] <= mean_rotation_error["B"] + 1.0e-3


@pytest.mark.skipif(
    not DL2TEST.is_file() or not KNOWN_GOOD_DL2TEST.is_file(),
    reason="private dl2test FBX or known-good legacy dl2test ANM2 is unavailable",
)
def test_dl2test_current_v2_control_stays_within_legacy_parity(
    tmp_path: Path,
) -> None:
    rig = ChromeRig.load(ROOT / "reference" / "dl2" / "player_skeleton.crig")
    build = build_exact_rig_anm2(
        DL2TEST,
        rig,
        fps=24.0,
        root_motion={"motion_mode": "inplace", "heading_mode": "lock_initial"},
        bilateral_semantic_policy="auto",
    )
    current = _decoded_payload(tmp_path, "dl2test_v2.anm2", build.payload)
    legacy = decode_anm2_animation(KNOWN_GOOD_DL2TEST, fps=24.0)
    angular = _angular_errors(current, legacy)
    translation = np.linalg.norm(
        current.values[:, :, 3:6] - legacy.values[:, :, 3:6],
        axis=2,
    )
    assert float(np.max(angular, initial=0.0)) <= 0.01
    assert float(np.max(translation, initial=0.0)) <= 1.0e-5
    assert build.report["bilateral_swapped_row_count"] == 0


@pytest.mark.skipif(
    not ROUNDTRIP_WHISTLE.is_file() or not CURRENT_WHISTLE_CONTROL.is_file(),
    reason="private Whistle round-trip control artifacts are unavailable",
)
def test_whistle_roundtrip_control_remains_close_to_current_output() -> None:
    roundtrip = decode_anm2_animation(ROUNDTRIP_WHISTLE, fps=24.0)
    current = decode_anm2_animation(CURRENT_WHISTLE_CONTROL, fps=24.0)
    angular = _angular_errors(roundtrip, current)
    frame_count = min(roundtrip.frame_count, current.frame_count)
    translation = np.linalg.norm(
        roundtrip.values[:frame_count, :, 3:6]
        - current.values[:frame_count, :, 3:6],
        axis=2,
    )
    assert float(np.max(angular, initial=0.0)) <= 0.13
    assert int(np.count_nonzero(np.max(angular, axis=0) > 1.0)) == 0
    assert float(np.max(translation, initial=0.0)) <= 1.0e-5


@pytest.mark.skipif(
    not PINHEAD_RET.is_file(),
    reason="private pinhead decoded control is unavailable",
)
def test_pinhead_decoded_control_remains_unchanged() -> None:
    assert (
        hashlib.sha256(PINHEAD_RET.read_bytes()).hexdigest()
        == "e8bccd527b9fa4caea7a50b224f8f9e7fe652a274754460a673f109bb8ce94c2"
    )
    decoded = decode_anm2_animation(PINHEAD_RET)
    assert decoded.frame_count == 97
    assert decoded.track_count == 159
    assert np.isfinite(decoded.values).all()


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
    assert direct_by_target["r_foretwist"] == "r_foretwist"
    assert direct_by_target["refcamera"] == "refcamera"
    semantic = verified_map.extensions["bilateral_semantic_decision"]
    assert semantic["bilateral_swapped_row_count"] == 0
    assert not semantic["bilateral_swap_applied"]

    explicitly_swapped = build_verified_dl2_advanced_body_map(
        document,
        rig,
        policy,
        bilateral_semantic_policy="swap_bilateral_explicit",
    )
    explicit_by_target = {
        row.target_rig_bone: row.source_fbx_bone
        for row in explicitly_swapped.pairs
    }
    assert explicit_by_target["l_foretwist"] == "r_foretwist"
    assert explicit_by_target["r_foretwist"] == "l_foretwist"
    assert explicitly_swapped.extensions["bilateral_semantic_decision"][
        "bilateral_swapped_row_count"
    ] > 0

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
    assert build.report["bilateral_swap_applied"] is False
    assert build.report[
        "post_canonicalization_mirror_conjugation_applied"
    ] is False
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
