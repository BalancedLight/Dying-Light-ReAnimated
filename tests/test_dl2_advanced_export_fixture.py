from __future__ import annotations

import hashlib
import math
from pathlib import Path

import numpy as np
import pytest

from dlanm2_gui.anm2_components import decode_all_frames_cached, decode_samples
from dlanm2_gui.automatic_retarget import (
    build_verified_dl2_advanced_body_map,
    revalidate_verified_dl2_advanced_body_map,
)
from dlanm2_gui.bone_maps import mapping_profile_origin
from dlanm2_gui.chrome_rig import ChromeRig
from dlanm2_gui.fbx_core import FbxDocument
from dlanm2_gui.fbx_preflight import classify_target_compatibility
from dlanm2_gui.retarget_engines.mapped_rig import (
    build_mapped_rig_anm2,
    reconstruct_target_globals,
)
from dlanm2_gui.root_heading import apply_target_root_policy
from dlanm2_gui.root_motion import RootMotionSelection
from dlanm2_gui.retarget_routing import select_exact_solver
from dlanm2_gui.skeleton_analysis import analyze_source_skeleton
from dlanm2_gui.target_retarget_policy import build_target_retarget_policy


ROOT = Path(__file__).resolve().parents[1]
ADVANCED_CRIG = ROOT / "reference" / "dl2" / "player_skeleton.crig"
PRIVATE_FBX = Path(r"S:\Downloads\Thriller Combined - Parts 1-4.fbx")
PRIVATE_FBX_SHA256 = (
    "c7d0041db80bbe63efff3934c01de9048f0bff61753ae280012b6a76a829d233"
)
ANIMATION_STACK = "mixamo.com"


def _require_reviewed_private_fbx() -> Path:
    if not PRIVATE_FBX.is_file():
        pytest.skip("private Thriller FBX regression fixture is not available")

    digest = hashlib.sha256()
    with PRIVATE_FBX.open("rb") as stream:
        for chunk in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(chunk)
    if digest.hexdigest() != PRIVATE_FBX_SHA256:
        pytest.skip("private Thriller FBX hash differs from the reviewed fixture")
    return PRIVATE_FBX


def _hierarchy_extent(rig: ChromeRig, frame: list[list[float]]) -> float:
    globals_by_name = reconstruct_target_globals(rig, frame)
    pivots = [matrix[:3, 3] for matrix in globals_by_name.values()]
    spans = [
        max(float(point[axis]) for point in pivots)
        - min(float(point[axis]) for point in pivots)
        for axis in range(3)
    ]
    return math.sqrt(sum(value * value for value in spans))


def test_reviewed_thriller_clip_exports_through_verified_advanced_bridge() -> None:
    source = _require_reviewed_private_fbx()
    rig = ChromeRig.load(ADVANCED_CRIG)
    document = FbxDocument(
        source,
        animation_stack=ANIMATION_STACK,
        purpose="animation",
        tolerance="recommended",
    )

    analysis = analyze_source_skeleton(
        document,
        animation_stack=ANIMATION_STACK,
    )
    assert analysis.selected_animation_stack == ANIMATION_STACK
    assert analysis.archetype == "humanoid"
    assert analysis.animation_domain == "full_body"
    assert not [row for row in analysis.findings if row.severity == "blocking"]

    policy = build_target_retarget_policy(rig, clip_domain=analysis.clip_domain)
    assert policy.automatic_routing_authorized, policy.coherence_errors

    profile = build_verified_dl2_advanced_body_map(analysis, rig, policy)
    mapped_rows = [row for row in profile.pairs if row.source_fbx_bone]
    bind_rows = [row for row in profile.pairs if not row.source_fbx_bone]
    certificate = profile.extensions["automatic_retarget_certificate"]

    assert mapping_profile_origin(profile) == "automatic_verified"
    assert len(profile.pairs) == len(rig.bones) == 271
    assert len({row.target_rig_bone for row in profile.pairs}) == 271
    assert len(mapped_rows) == certificate["mapped_body_row_count"] == 52
    assert len(bind_rows) == certificate["bind_row_count"] == 219
    assert certificate["spatial_only_row_count"] == 0
    assert certificate["mapped_non_body_target_count"] == 0

    live_validation = revalidate_verified_dl2_advanced_body_map(
        profile,
        analysis,
        rig,
        policy,
    )
    assert live_validation.ok
    assert live_validation.status == "pass"
    assert live_validation.live_revalidated

    compatibility = classify_target_compatibility(document, rig)
    assert compatibility["classification"] == "incompatible"
    solver = select_exact_solver(
        compatibility,
        profile,
        automatic_verification=live_validation,
    )
    assert solver.build_allowed
    assert solver.selected_engine == "MappedRigRetargetEngine"
    assert solver.selected_policy == "global_bind_basis_correction"
    assert solver.mapping_profile_origin == "automatic_verified"
    assert solver.automatic_verification_status == "pass"

    build = build_mapped_rig_anm2(
        source,
        rig,
        profile,
        fps=30,
        animation_stack=ANIMATION_STACK,
        document=document,
        transfer_policy=solver.selected_policy,
        root_motion=RootMotionSelection(
            target_root_bone="pelvis",
            motion_mode="skeletal_root",
            heading_mode="preserve",
        ),
    )
    report = build.report

    assert build.payload
    assert build.frame_count == 3343
    assert report["engine"] == solver.selected_engine
    assert report["retarget_mode"] == "mapped_crig"
    assert report["source_animation_stack"] == ANIMATION_STACK
    assert report["main_transfer_policy"] == solver.selected_policy
    assert report["basis_correction_policy"] == solver.selected_policy
    assert report["mapping_certificate_status"] == "pass"
    assert report["target_row_count"] == 271
    assert report["mapped_body_row_count"] == 52
    assert report["verified_bind_default_row_count"] == 219
    assert report["spatial_only_mapping_count"] == 0
    assert report["mapped_non_body_target_count"] == 0
    assert report["base_mapped_bone_count"] == 52
    assert report["bone_count"] == 271
    assert report["track_count"] == len(rig.descriptors) == 368
    assert report["root_motion_policy_applied"] == "bip01"
    root_basis = report["root_motion_basis"]
    assert root_basis["net_reference"] == "source_frame_zero_to_last_frame"
    assert root_basis["model_basis_used_for_root_vector"] is False
    assert root_basis["source_net_actor_displacement_m"]["vertical"] == pytest.approx(
        -0.0727795410, abs=1.0e-7
    )
    assert root_basis["target_net_actor_displacement_m"] == pytest.approx(
        root_basis["source_net_actor_displacement_m"], abs=1.0e-9
    )
    target_net = root_basis["target_net_vector_m"]
    assert target_net[1] == pytest.approx(-0.0727795410, abs=1.0e-7)
    assert math.hypot(target_net[0], target_net[2]) == pytest.approx(
        7.09052647, abs=0.05
    )
    heading = report["fixture_heading_audit"]
    assert heading["source_root_heading_degrees"] == pytest.approx(
        718.9099, abs=0.01
    )
    assert heading["pre_policy_target_root_heading_degrees"] == pytest.approx(
        718.9033, abs=0.01
    )
    assert heading["post_policy_target_root_heading_degrees"] == pytest.approx(
        718.9033, abs=0.01
    )
    parity = report["representative_target_global_rotation_parity"]
    assert parity["frames"] == [
        0, 1, 10, 100, 300, 500, 1000, 1500, 2000, 2200, 2500, 3000, 3342
    ]
    assert parity["maximum_error_degrees"] < 0.05
    assert parity["status"] == "pass"
    assert report["preserves_target_non_root_translation"] is True
    assert report["preserves_target_non_root_scale"] is True
    assert report["preserves_target_non_root_translation_and_scale"] is True
    assert report["authorized_non_root_translation_bones"] == []
    assert not any(
        "Source FBX skeleton hash differs" in warning
        for warning in report["warnings"]
    )

    safety = report["hierarchy_safety"]
    assert safety["status"] == "pass"
    assert safety["validated_frame_count"] == build.frame_count
    assert (
        safety["maximum_animated_hierarchy_extent_meters"]
        <= safety["extent_limit_meters"]
    )
    assert safety["bind_hierarchy_extent_meters"] == pytest.approx(
        2.217483833846062,
        abs=1.0e-6,
    )
    assert safety["non_root_translation_limit_meters"] == pytest.approx(1.0e-5)
    assert safety["maximum_non_root_translation_delta_meters"] == pytest.approx(
        0.0,
        abs=1.0e-9,
    )
    assert (
        safety["maximum_non_root_translation_delta_meters"]
        <= safety["non_root_translation_limit_meters"]
    )
    assert safety["minimum_parent_child_length_ratio"] == pytest.approx(1.0)
    assert safety["maximum_parent_child_length_ratio"] == pytest.approx(1.0)
    assert safety["minimum_scale"] > 1.0e-5
    assert report["decoded_component_error_tolerance"] == pytest.approx(0.004)
    assert report["decoded_max_component_error"] < 0.004

    # Exercise all four independent root/heading choices from the serialized
    # preserve-heading payload. This keeps the expensive FBX retarget pass
    # singular while validating the actual encoded 3,343-frame Thriller data.
    cached = decode_all_frames_cached(build.payload)
    preserve_values = cached.values
    pelvis_track = rig.descriptors.index(
        next(bone.descriptor for bone in rig.bones if bone.name == "pelvis")
    )
    motion_track = rig.descriptors.index(0xCCC3CDDF)
    preserve_net = (
        preserve_values[-1, pelvis_track, 3:6]
        - preserve_values[0, pelvis_track, 3:6]
    )
    assert preserve_net[1] == pytest.approx(-0.0727795410, abs=0.01)
    assert math.hypot(preserve_net[0], preserve_net[2]) == pytest.approx(
        7.09052647, abs=0.05
    )

    locked = preserve_values.copy()
    locked_report = apply_target_root_policy(
        locked,
        rig,
        "pelvis",
        RootMotionSelection(
            target_root_bone="pelvis",
            motion_mode="skeletal_root",
            heading_mode="lock_initial",
        ),
    )
    assert abs(locked_report.skeletal_root_heading_degrees) <= 0.1
    np.testing.assert_allclose(
        locked[:, pelvis_track, 3:6],
        preserve_values[:, pelvis_track, 3:6],
        atol=0.0,
        rtol=0.0,
    )
    del locked

    inplace = preserve_values.copy()
    inplace_report = apply_target_root_policy(inplace, rig, "pelvis", "inplace")
    assert abs(inplace_report.skeletal_root_heading_degrees) <= 0.1
    assert np.max(np.ptp(inplace[:, pelvis_track, 3:6], axis=0)) <= 1.0e-9
    assert np.max(
        np.abs(
            inplace[:, motion_track]
            - np.asarray((0, 0, 0, 0, 0, 0, 1, 1, 1), dtype=float)
        )
    ) <= 1.0e-12
    del inplace

    motion = preserve_values.copy()
    motion_report = apply_target_root_policy(motion, rig, "pelvis", "motion")
    assert abs(motion_report.skeletal_root_heading_degrees) <= 0.1
    assert motion_report.motion_heading_degrees == pytest.approx(718.9033, abs=0.1)
    pelvis_motion_net = (
        motion[-1, pelvis_track, 3:6] - motion[0, pelvis_track, 3:6]
    )
    accumulator_net = (
        motion[-1, motion_track, 3:6] - motion[0, motion_track, 3:6]
    )
    assert math.hypot(pelvis_motion_net[0], pelvis_motion_net[2]) <= 1.0e-6
    assert pelvis_motion_net[1] == pytest.approx(preserve_net[1], abs=1.0e-9)
    assert math.hypot(accumulator_net[0], accumulator_net[2]) == pytest.approx(
        math.hypot(preserve_net[0], preserve_net[2]), abs=1.0e-9
    )
    del motion

    representative_frames = [0, 1, 70, 835, 1671, 2507, 3342]
    decoded = decode_samples(build.payload, representative_frames)
    assert decoded.track_count == len(rig.descriptors) == 368
    assert len(decoded.frames) == len(representative_frames)
    representative_extents = {
        frame_index: _hierarchy_extent(rig, decoded_frame.tracks)
        for frame_index, decoded_frame in zip(
            representative_frames,
            decoded.frames,
            strict=True,
        )
    }
    representative_max_frame = max(
        representative_extents,
        key=representative_extents.__getitem__,
    )
    representative_extent = representative_extents[representative_max_frame]
    print(
        "representative hierarchy extents (m): "
        f"{representative_extents}; max frame: {representative_max_frame}"
    )
    assert representative_extent == pytest.approx(
        2.1310187930902043,
        abs=0.01,
    )
    packed_tolerance = report["decoded_component_error_tolerance"]
    for decoded_frame in decoded.frames:
        for bone in rig.bones:
            if bone.parent_index < 0:
                continue
            track_index = rig.descriptors.index(bone.descriptor)
            track = decoded_frame.tracks[track_index]
            assert track[3:6] == pytest.approx(
                bone.bind_translation,
                abs=packed_tolerance,
            )
            assert track[6:9] == pytest.approx(
                bone.bind_scale,
                abs=packed_tolerance,
            )
