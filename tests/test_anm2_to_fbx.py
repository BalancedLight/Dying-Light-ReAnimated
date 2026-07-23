from __future__ import annotations

import math
from pathlib import Path

import numpy as np
import pytest

from dlanm2_gui.anm2_fbx import (
    MOTION_HELPER_DESCRIPTOR,
    append_motion_accumulator_helper,
    bake_motion_accumulator_into_root,
    cayley_to_quaternion_wxyz,
    decode_anm2_animation,
    inspect_motion_accumulator,
    reconstruct_native_scene,
    retarget_decoded_animation,
)
from dlanm2_gui.anm2_writer import build_payload_from_values
from dlanm2_gui.blender_fbx import FbxExportResult, export_anm2_to_fbx
from dlanm2_gui.bone_maps import BoneMapPair, GenericBoneMap, auto_map_skeletons
from dlanm2_gui.chrome_rig import ChromeRig, ChromeRigBone
from dlanm2_gui.oracle.smd_bind_pose import anm2_cayley_vector_from_quaternion


def _rig(names=("root", "bone_door"), *, renamed=False, extra=()) -> ChromeRig:
    from dlanm2_gui.trackmap import dl_name_hash
    rows = []
    for index, name in enumerate(names):
        actual = f"target_{name}" if renamed else name
        rows.append(ChromeRigBone(
            index, actual, index - 1, dl_name_hash(actual),
            (0.0, 0.0 if index == 0 else 1.0, 0.0),
            (1.0, 0.0, 0.0, 0.0), (1.0, 1.0, 1.0),
        ))
    descriptors = tuple([row.descriptor for row in rows] + list(extra))
    return ChromeRig(
        "test:rig", "Test Rig", "Generic Object", tuple(rows), 0,
        extra_track_descriptors=tuple(extra), track_descriptors=descriptors,
    )


def _payload(path: Path, rig: ChromeRig, *, helper=False) -> Path:
    bind = rig.bind_track_values()
    values = [[list(track) for track in bind] for _ in range(3)]
    quarter_turn = np.asarray([math.cos(math.pi / 4), 0, 0, math.sin(math.pi / 4)])
    values[1][1][:3] = anm2_cayley_vector_from_quaternion(quarter_turn).tolist()
    values[2][1][:3] = values[1][1][:3]
    if helper:
        values[1][-1][3] = 1.0
        values[2][-1][3] = 2.0
    flags = []
    for track in range(len(rig.descriptors)):
        flags.append([
            max(frame[track][component] for frame in values)
            - min(frame[track][component] for frame in values) > 1e-8
            for component in range(9)
        ])
    payload = build_payload_from_values(
        rig.make_header(frame_count=3), rig.descriptors, values, flags
    )
    path.write_bytes(payload)
    return path


def test_full_clip_decode_and_cayley_continuity(tmp_path: Path) -> None:
    rig = _rig()
    path = _payload(tmp_path / "door.anm2", rig)
    animation = decode_anm2_animation(path, fps=60, start_frame=1, end_frame=2)
    assert animation.frame_count == 2
    assert animation.fps == 60
    assert animation.source_frame_start == 1
    assert np.allclose(np.linalg.norm(animation.quaternions_wxyz, axis=2), 1.0)
    assert np.allclose(cayley_to_quaternion_wxyz((0, 0, 0)), (1, 0, 0, 0))


def test_motion_accumulator_bakes_root_and_preserves_helper(tmp_path: Path) -> None:
    rig = _rig(extra=(MOTION_HELPER_DESCRIPTOR,))
    animation = decode_anm2_animation(_payload(tmp_path / "motion.anm2", rig, helper=True))
    info = inspect_motion_accumulator(animation)
    assert info.present and info.active
    scene = reconstruct_native_scene(animation, rig, unknown_track_policy="drop")
    baked = bake_motion_accumulator_into_root(scene, animation)
    preserved = append_motion_accumulator_helper(baked, animation)
    assert baked.translations[-1, rig.root_index, 0] == pytest.approx(2.0, abs=1e-3)
    helper = preserved.bones[-1]
    assert helper.name == "DLR_OffsetHelper_CCC3CDDF"
    assert helper.helper and helper.semantic == "motion_accumulator"
    assert preserved.translations[-1, -1, 0] == pytest.approx(2.0, abs=1e-3)


def test_static_motion_accumulator_does_not_change_root(tmp_path: Path) -> None:
    rig = _rig(extra=(MOTION_HELPER_DESCRIPTOR,))
    animation = decode_anm2_animation(_payload(tmp_path / "static-motion.anm2", rig))
    info = inspect_motion_accumulator(animation)
    assert info.present and not info.active
    scene = reconstruct_native_scene(animation, rig, unknown_track_policy="drop")
    baked = bake_motion_accumulator_into_root(scene, animation)
    np.testing.assert_allclose(baked.translations, scene.translations)
    np.testing.assert_allclose(baked.rotations_wxyz, scene.rotations_wxyz)


def test_export_service_bakes_by_default_and_toggle_keeps_raw_helper(
    tmp_path: Path,
    monkeypatch: pytest.MonkeyPatch,
) -> None:
    rig = _rig(extra=(MOTION_HELPER_DESCRIPTOR,))
    source = _payload(tmp_path / "motion-service.anm2", rig, helper=True)
    captured: list[object] = []

    def fake_blender_export(scene, output_path, **_kwargs):
        captured.append(scene)
        return FbxExportResult(
            str(output_path),
            scene.frame_count,
            scene.fps,
            sum(not bone.helper for bone in scene.bones),
            tuple(scene.warnings),
            "",
        )

    monkeypatch.setattr("dlanm2_gui.blender_fbx.run_blender_export", fake_blender_export)
    baked = export_anm2_to_fbx(
        source, rig, tmp_path / "baked.fbx", unknown_track_policy="helpers"
    )
    baked_scene = captured[-1]
    assert baked.motion_accumulator_detected
    assert baked.motion_accumulator_active
    assert baked.motion_accumulator_baked
    assert baked.motion_accumulator_helper_preserved
    assert baked_scene.translations[-1, 0, 0] == pytest.approx(2.0, abs=1e-3)

    raw = export_anm2_to_fbx(
        source,
        rig,
        tmp_path / "raw.fbx",
        unknown_track_policy="helpers",
        bake_motion_accumulator=False,
    )
    raw_scene = captured[-1]
    assert raw.motion_accumulator_detected
    assert raw.motion_accumulator_active
    assert not raw.motion_accumulator_baked
    assert not raw.motion_accumulator_helper_preserved
    assert raw_scene.translations[-1, 0, 0] == pytest.approx(0.0, abs=1e-6)
    assert any(
        bone.name == "DLR_OffsetHelper_CCC3CDDF"
        and bone.semantic == "motion_accumulator"
        for bone in raw_scene.bones
    )


def test_cross_rig_motion_accumulator_uses_translation_scale(tmp_path: Path) -> None:
    source = _rig(extra=(MOTION_HELPER_DESCRIPTOR,))
    target = _rig(renamed=True)
    mapping = GenericBoneMap.create(
        "Door map", source.skeleton_hash, target.skeleton_hash, source_rig_ref=source.rig_id
    )
    mapping.pairs = [
        BoneMapPair(source.bones[0].descriptor, "root", "target_root"),
        BoneMapPair(source.bones[1].descriptor, "bone_door", "target_bone_door"),
    ]
    animation = decode_anm2_animation(_payload(tmp_path / "motion-cross-rig.anm2", source, helper=True))
    scene = retarget_decoded_animation(animation, source, target, mapping, translation_scale=2.0)
    baked = bake_motion_accumulator_into_root(scene, animation, translation_scale=2.0)
    assert baked.translations[-1, target.root_index, 0] == pytest.approx(4.0, abs=1e-3)


def test_auto_map_then_bind_relative_cross_rig(tmp_path: Path) -> None:
    source = _rig()
    target = _rig(renamed=True)
    parents = {"target_root": None, "target_bone_door": "target_root"}
    suggested = auto_map_skeletons(
        source, parents, parents, target_skeleton_hash=target.skeleton_hash
    )
    assert len(suggested.pairs) == 2
    pairs = [
        BoneMapPair(source.bones[0].descriptor, "root", "target_root"),
        BoneMapPair(source.bones[1].descriptor, "bone_door", "target_bone_door"),
    ]
    mapping = GenericBoneMap.create(
        "Door map", source.skeleton_hash, target.skeleton_hash, source_rig_ref=source.rig_id
    )
    mapping.pairs = pairs
    animation = decode_anm2_animation(_payload(tmp_path / "door.anm2", source))
    scene = retarget_decoded_animation(animation, source, target, mapping)
    assert scene.bones[1].name == "target_bone_door"
    expected = np.asarray([math.cos(math.pi / 4), 0, 0, math.sin(math.pi / 4)])
    assert abs(float(scene.rotations_wxyz[1, 1] @ expected)) > 0.999
    assert suggested.validate() == []


def test_generic_bone_map_roundtrip_allows_source_fanout_and_rejects_duplicate_target(
    tmp_path: Path,
) -> None:
    mapping = GenericBoneMap.create("Map", "source", "target")
    mapping.pairs = [BoneMapPair(1, "door", "hinge", 0.9, "normalized")]
    path = mapping.save(tmp_path / "door-map")
    loaded = GenericBoneMap.load(path)
    assert loaded.pairs[0].target_bone == "hinge"
    loaded.pairs.append(BoneMapPair(2, "handle", "hinge"))
    assert loaded.validate() == []
    loaded.pairs.append(BoneMapPair(2, "handle", "other_source"))
    assert any("target rig" in row.lower() for row in loaded.validate())


def test_full_decoder_crosses_physical_page_boundary(tmp_path: Path) -> None:
    from dlanm2_gui.anm2 import Anm2Header
    rig = ChromeRig.load(Path(__file__).resolve().parents[1] / "reference/male_npc_infected.crig")
    frame_count = 250
    bind = rig.bind_track_values()
    values = [[list(track) for track in bind] for _ in range(frame_count)]
    for frame in range(frame_count):
        for track in range(len(bind)):
            for component in range(6):
                values[frame][track][component] += 0.002 * math.sin(frame * 0.03 + track + component)
    flags = [[True] * 6 + [False] * 3 for _ in bind]
    payload = build_payload_from_values(
        rig.make_header(frame_count=frame_count), rig.descriptors, values, flags
    )
    assert Anm2Header.parse(payload).unknown12 >= 2
    source = tmp_path / "multipage.anm2"
    source.write_bytes(payload)
    decoded = decode_anm2_animation(source)
    assert decoded.frame_count == frame_count
    assert decoded.values[-1, 20, 4] == pytest.approx(values[-1][20][4], abs=2e-4)
