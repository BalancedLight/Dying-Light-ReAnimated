from __future__ import annotations

import math
from pathlib import Path

import numpy as np
import pytest

import dlanm2_gui.anm2_components as components
from dlanm2_gui.animation_targets import resolve_animation_target
from dlanm2_gui.anm2_components import decode_all_frames_cached, decode_samples
from dlanm2_gui.anm2_fbx import (
    build_sparse_fbx_job,
    cayley_to_quaternion_wxyz,
    cayley_to_quaternions_wxyz,
    decode_anm2_animation,
    reconstruct_native_scene,
    write_sparse_fbx_job,
)
from dlanm2_gui.chrome_rig import ChromeRig, ChromeRigBone
from dlanm2_gui.blender_fbx import FbxExportResult, export_anm2_to_fbx
from dlanm2_gui.game_profiles import (
    DL1_GAME_ID,
    DL2_ADVANCED_RIG_REF,
    DL2_GAME_ID,
    apply_game_profile_defaults,
)
from dlanm2_gui.oracle.smd_bind_pose import (
    anm2_cayley_vector_from_quaternion,
    quaternion_wxyz_from_anm2_cayley,
)
from dlanm2_gui.root_heading import (
    apply_target_root_policy,
    quaternion_inverse,
    quaternion_multiply,
)
from dlanm2_gui.root_motion import RootMotionSelection
from dlanm2_gui.retarget_engines.mapped_rig import _inplace_root_policy_warning
from dlanm2_gui.workspace_project import (
    CURRENT_PROJECT_SCHEMA_VERSION,
    DlReanimatedProject,
    ProjectAnimation,
)
from dlanm2_gui.trackmap import dl_name_hash


ROOT = Path(__file__).resolve().parents[1]
DL1_SAMPLE = ROOT / "reference" / "infected_turn_90r.template.anm2"
DL2_SAMPLE = ROOT / "reference" / "dl2" / "0_m_fpp_farjump.anm2"
ADVANCED_CRIG = ROOT / "reference" / "dl2" / "player_skeleton.crig"
GENERATED_FIXTURE = (
    ROOT
    / ".pytest_tmp"
    / "project_builder_acceptance_final"
    / "dl_reanimated_build"
    / "animations"
    / "dl_reanimated_dlr_test_dl2.anm2"
)


def _project_payload(*, override=None) -> dict:
    extensions = {}
    if override is not None:
        extensions["expert_solver_override"] = override
    return {
        "format": "dl-reanimated-project",
        "schema_version": 8,
        "minimum_reader_version": 1,
        "name": "DL2 migration",
        "game_id": DL2_GAME_ID,
        "rig": {
            "target_rig_ref": DL2_ADVANCED_RIG_REF,
            "target_rig_path": str(ADVANCED_CRIG),
            "retarget_mode": "exact",
            "extensions": extensions,
        },
    }


def _axis_quaternion(axis, radians):
    axis = np.asarray(axis, dtype=float)
    axis /= np.linalg.norm(axis)
    return np.asarray(
        (math.cos(radians * 0.5), *(axis * math.sin(radians * 0.5))),
        dtype=float,
    )


def _quaternion_angle_degrees(left, right):
    dot = abs(float(np.dot(left, right)))
    return math.degrees(2.0 * math.acos(max(-1.0, min(1.0, dot))))


def _two_turn_values(rig: ChromeRig):
    bind = np.asarray(rig.bind_track_values(), dtype=float)
    values = np.repeat(bind[np.newaxis, ...], 181, axis=0)
    root = next(bone for bone in rig.bones if bone.name == "pelvis")
    root_track = rig.descriptors.index(root.descriptor)
    tilt = _axis_quaternion((1.0, 0.0, 0.0), math.radians(12.0))
    for frame in range(len(values)):
        heading = _axis_quaternion(
            (0.0, 1.0, 0.0), math.radians(720.0 * frame / (len(values) - 1))
        )
        rotation = quaternion_multiply(
            quaternion_multiply(heading, tilt), root.bind_rotation_wxyz
        )
        values[frame, root_track, :3] = anm2_cayley_vector_from_quaternion(rotation)
        values[frame, root_track, 3:6] = (
            2.0 * frame / (len(values) - 1),
            root.bind_translation[1] + 0.25 * frame / (len(values) - 1),
            -3.0 * frame / (len(values) - 1),
        )
    return values, root, root_track, tilt


def test_builtins_default_to_auto_and_custom_targets_stay_exact() -> None:
    dl1 = DlReanimatedProject.new("DL1")
    assert dl1.game_id == DL1_GAME_ID
    assert dl1.rig.retarget_mode == "auto"

    dl1.game_id = DL2_GAME_ID
    apply_game_profile_defaults(dl1, ROOT, previous_game_id=DL1_GAME_ID)
    assert dl1.rig.target_rig_ref == DL2_ADVANCED_RIG_REF
    assert dl1.rig.retarget_mode == "auto"
    clip = ProjectAnimation.create("clip.fbx")
    assert resolve_animation_target(dl1, clip).retarget_mode == "auto"

    dl1.rig.target_rig_ref = "custom:machine"
    dl1.rig.target_rig_path = "machine.crig"
    dl1.rig.retarget_mode = "exact"
    assert resolve_animation_target(dl1, clip).retarget_mode == "exact"


def test_schema_v9_migrates_builtin_dl2_exact_unless_expert_override() -> None:
    migrated = DlReanimatedProject.from_dict(_project_payload())
    assert migrated.schema_version == CURRENT_PROJECT_SCHEMA_VERSION
    assert migrated.rig.retarget_mode == "auto"
    assert migrated.rig.extensions["retarget_mode_migration_v9"]["from"] == "exact"

    expert = DlReanimatedProject.from_dict(
        _project_payload(
            override={"deliberate": True, "retarget_mode": "exact"}
        )
    )
    assert expert.rig.retarget_mode == "exact"
    assert "retarget_mode_migration_v9" not in expert.rig.extensions


def test_two_turn_root_policies_remove_or_transfer_heading_and_preserve_tilt() -> None:
    rig = ChromeRig.load(ADVANCED_CRIG)
    values, root, root_track, tilt = _two_turn_values(rig)
    motion_track = rig.descriptors.index(0xCCC3CDDF)
    identity = np.asarray((0, 0, 0, 0, 0, 0, 1, 1, 1), dtype=float)

    inplace = values.copy()
    inplace_report = apply_target_root_policy(inplace, rig, "pelvis", "inplace")
    assert inplace_report.source_heading_degrees == pytest.approx(720.0, abs=0.05)
    assert inplace_report.maximum_source_heading_offset_degrees == pytest.approx(
        720.0, abs=0.05
    )
    assert inplace_report.maximum_source_planar_displacement_meters == pytest.approx(
        math.sqrt(13.0), abs=1.0e-9
    )
    warning = _inplace_root_policy_warning(
        "pelvis",
        RootMotionSelection(target_root_bone="pelvis"),
        inplace_report,
    )
    assert "720.00" in warning
    assert "3.606 m" in warning
    assert "Skeletal root with Preserve heading" in warning
    assert abs(inplace_report.skeletal_root_heading_degrees) <= 0.1
    assert np.max(
        np.abs(inplace[:, root_track, 3:6] - np.asarray(root.bind_translation))
    ) <= 1.0e-12
    assert np.max(np.abs(inplace[:, motion_track] - identity)) <= 1.0e-12
    final = quaternion_wxyz_from_anm2_cayley(inplace[-1, root_track, :3])
    expected_tilted_bind = quaternion_multiply(tilt, root.bind_rotation_wxyz)
    assert _quaternion_angle_degrees(final, expected_tilted_bind) <= 1.0e-5

    skeletal = values.copy()
    skeletal_report = apply_target_root_policy(skeletal, rig, "pelvis", "bip01")
    assert skeletal_report.skeletal_root_heading_degrees == pytest.approx(720.0, abs=0.05)
    assert not _inplace_root_policy_warning(
        "pelvis",
        RootMotionSelection(
            target_root_bone="pelvis",
            motion_mode="skeletal_root",
            heading_mode="preserve",
        ),
        skeletal_report,
    )
    np.testing.assert_allclose(skeletal, values, atol=0.0, rtol=0.0)

    locked = values.copy()
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
    assert locked_report.translation_owner == "skeletal_root"
    assert locked_report.heading_owner == "none"
    np.testing.assert_allclose(
        locked[:, root_track, 3:6], values[:, root_track, 3:6], atol=0.0, rtol=0.0
    )
    final = quaternion_wxyz_from_anm2_cayley(locked[-1, root_track, :3])
    assert _quaternion_angle_degrees(final, expected_tilted_bind) <= 1.0e-5

    motion = values.copy()
    motion_report = apply_target_root_policy(motion, rig, "pelvis", "motion")
    assert abs(motion_report.skeletal_root_heading_degrees) <= 0.1
    assert motion_report.motion_heading_degrees == pytest.approx(720.0, abs=0.05)
    assert motion_report.motion_planar_displacement == pytest.approx((2.0, 0.0, -3.0))
    assert motion_report.skeletal_root_planar_displacement == pytest.approx((0.0, 0.0, 0.0))
    assert motion[-1, motion_track, 3:6] == pytest.approx((2.0, 0.0, -3.0))


def test_heading_policy_reconstructs_local_when_selected_root_has_parent() -> None:
    parent = ChromeRigBone(
        0,
        "armature_parent",
        -1,
        dl_name_hash("armature_parent"),
        (0.5, 0.0, 0.0),
        (1.0, 0.0, 0.0, 0.0),
        (1.0, 1.0, 1.0),
        deform=False,
    )
    child = ChromeRigBone(
        1,
        "pelvis",
        0,
        dl_name_hash("pelvis"),
        (0.0, 1.0, 0.0),
        (1.0, 0.0, 0.0, 0.0),
        (1.0, 1.0, 1.0),
    )
    descriptors = (0xCCC3CDDF, parent.descriptor, child.descriptor)
    rig = ChromeRig(
        "test:parented-root",
        "Parented root",
        "test",
        (parent, child),
        0,
        extra_track_descriptors=(0xCCC3CDDF,),
        track_descriptors=descriptors,
        extensions={"world_up_axis": [0.0, 1.0, 0.0]},
    )
    values = np.repeat(
        np.asarray(rig.bind_track_values(), dtype=float)[np.newaxis, ...], 5, axis=0
    )
    parent_track = rig.descriptors.index(parent.descriptor)
    child_track = rig.descriptors.index(child.descriptor)
    for frame, angle in enumerate(np.linspace(0.0, math.radians(40.0), 5)):
        parent_q = _axis_quaternion((0.0, 0.0, 1.0), angle * 0.25)
        child_q = _axis_quaternion((0.0, 1.0, 0.0), angle)
        values[frame, parent_track, :3] = anm2_cayley_vector_from_quaternion(parent_q)
        values[frame, child_track, :3] = anm2_cayley_vector_from_quaternion(child_q)
    report = apply_target_root_policy(values, rig, "pelvis", "inplace")
    assert abs(report.skeletal_root_heading_degrees) <= 0.1
    assert np.isfinite(values).all()


@pytest.mark.parametrize("sample", (DL1_SAMPLE, DL2_SAMPLE))
def test_cached_decoder_matches_random_time_decoder(sample: Path) -> None:
    data = sample.read_bytes()
    cached = decode_all_frames_cached(data)
    frames = sorted({0, 1, 10, cached.frame_count // 2, cached.frame_count - 1})
    random_time = decode_samples(data, [float(frame) for frame in frames])
    expected = np.asarray([frame.tracks for frame in random_time.frames])
    np.testing.assert_allclose(cached.values[frames], expected, atol=0.0, rtol=0.0)


def test_cached_decoder_decodes_each_unique_slot_once_and_selects_tracks(monkeypatch) -> None:
    calls = 0
    original = components._decode_packed_slot_cached

    def counted(**kwargs):
        nonlocal calls
        calls += 1
        return original(**kwargs)

    monkeypatch.setattr(components, "_decode_packed_slot_cached", counted)
    full = decode_all_frames_cached(DL2_SAMPLE.read_bytes())
    assert calls == full.unique_packed_slots_decoded

    chosen = full.descriptors[::17]
    selected = decode_all_frames_cached(
        DL2_SAMPLE.read_bytes(), selected_descriptors=chosen
    )
    expected_indices = [full.descriptors.index(value) for value in chosen]
    np.testing.assert_allclose(
        selected.values, full.values[:, expected_indices], atol=0.0, rtol=0.0
    )


def test_vectorized_cayley_conversion_matches_scalar_and_is_continuous() -> None:
    angles = np.linspace(0.0, math.radians(720.0), 257)
    quaternions = np.asarray(
        [_axis_quaternion((0.0, 1.0, 0.0), angle) for angle in angles]
    )
    cayley = np.asarray(
        [anm2_cayley_vector_from_quaternion(value) for value in quaternions]
    )[:, np.newaxis, :]
    vectorized = cayley_to_quaternions_wxyz(cayley)
    scalar = np.asarray(
        [cayley_to_quaternion_wxyz(value[0]) for value in cayley]
    )
    assert np.min(np.sum(vectorized[1:] * vectorized[:-1], axis=-1)) >= 0.0
    assert np.min(np.abs(np.sum(vectorized[:, 0] * scalar, axis=-1))) > 1.0 - 1.0e-12


def test_generated_fixture_root_heading_and_sparse_271_job(tmp_path: Path) -> None:
    if not GENERATED_FIXTURE.is_file():
        pytest.skip("generated 3343-frame DL2 acceptance fixture is unavailable")
    rig = ChromeRig.load(ADVANCED_CRIG)
    data = GENERATED_FIXTURE.read_bytes()
    cached = decode_all_frames_cached(data)
    inplace = cached.values.copy()
    report = apply_target_root_policy(inplace, rig, "pelvis", "inplace")
    assert report.source_heading_degrees == pytest.approx(718.9033, abs=0.01)
    assert abs(report.skeletal_root_heading_degrees) <= 0.1

    animation = decode_anm2_animation(
        GENERATED_FIXTURE,
        selected_descriptors=[bone.descriptor for bone in rig.bones],
    )
    scene = reconstruct_native_scene(animation, rig, unknown_track_policy="sidecar")
    job = build_sparse_fbx_job(scene, tmp_path / "out.fbx", tmp_path / "job.npz")
    summary = job.metadata["sparse_summary"]
    assert len(job.metadata["bones"]) == summary["skeleton_bone_count"] == 271
    assert summary["animated_bone_count"] == 52
    assert summary["bind_only_bone_count"] == 219
    assert summary["location_bone_count"] == 0
    assert summary["rotation_bone_count"] == 52
    assert summary["scale_bone_count"] == 0
    assert summary["fcurve_count"] == 52 * 4
    assert summary["scalar_key_count"] == 3343 * 52 * 4

    persisted = write_sparse_fbx_job(
        scene,
        tmp_path / "job.json",
        tmp_path / "job.npz",
        tmp_path / "out.fbx",
    )
    with np.load(tmp_path / "job.npz", allow_pickle=False) as arrays:
        assert arrays["frames"].shape == (3343,)
        assert arrays["rotations_wxyz"].shape == (3343, 52, 4)
        assert arrays["locations"].shape == (3343, 0, 3)
        assert arrays["scales"].shape == (3343, 0, 3)
    assert persisted.scalar_key_count == 695344


def test_blender_helper_uses_bulk_curves_and_one_dependency_update_site() -> None:
    source = (
        ROOT / "dlanm2_gui" / "blender_scripts" / "export_anm2_fbx.py"
    ).read_text(encoding="utf-8")
    assert "keyframe_insert" not in source
    assert source.count("bpy.context.view_layer.update()") == 1
    assert 'foreach_set("co"' in source
    assert "bake_anim_use_all_bones=False" in source
    assert "bake_anim_force_startend_keying=False" in source
    assert source.count("bpy.context.collection.objects.link(helper)") == 1


def test_default_unknown_sidecar_uses_a_separate_selected_decode_pass(
    tmp_path: Path, monkeypatch
) -> None:
    import dlanm2_gui.blender_fbx as service

    rig = ChromeRig.load(ADVANCED_CRIG)
    original = service.decode_anm2_animation
    selected_passes = []

    def tracked(*args, **kwargs):
        selected_passes.append(tuple(kwargs.get("selected_descriptors") or ()))
        return original(*args, **kwargs)

    def fake_blender(scene, output_path, **_kwargs):
        return FbxExportResult(
            str(Path(output_path).resolve()),
            scene.frame_count,
            scene.fps,
            len(scene.bones),
            tuple(scene.warnings),
            "fake blender",
        )

    monkeypatch.setattr(service, "decode_anm2_animation", tracked)
    monkeypatch.setattr(service, "run_blender_export", fake_blender)
    result = export_anm2_to_fbx(DL2_SAMPLE, rig, tmp_path / "farjump.fbx")
    assert [len(rows) for rows in selected_passes] == [271, 97]
    assert result.unknown_track_count == 97
    assert Path(result.unknown_tracks_sidecar).is_file()


def test_cached_decode_cancellation_is_checked_during_work() -> None:
    with pytest.raises(RuntimeError, match="cancelled"):
        decode_all_frames_cached(DL2_SAMPLE.read_bytes(), cancel_check=lambda: True)
