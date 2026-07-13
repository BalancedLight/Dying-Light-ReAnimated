from __future__ import annotations

from dataclasses import replace
from pathlib import Path
import struct

import numpy as np
import pytest

from dlanm2_gui.chrome_rig import ChromeRig, ChromeRigBone
from dlanm2_gui.dl2_anm2 import detect_anm2_format, parse_dl2_header42
from dlanm2_gui.fbx_preflight import classify_target_compatibility, preflight_fbx
from dlanm2_gui.game_profiles import (
    DL1_GAME_ID,
    DL2_GAME_ID,
    DL2_RIG_REF,
    apply_game_profile_defaults,
)
from dlanm2_gui.model_importer.fbx_model import FbxScene
from dlanm2_gui.retarget_engines.mapped_rig import (
    apply_global_root_policy,
    corrected_target_global,
    global_bind_basis_correction,
)
from dlanm2_gui.retarget_engines.exact_rig import build_exact_rig_anm2
from dlanm2_gui.workspace_project import DlReanimatedProject, ProjectAnimation
from dlanm2_gui.trackmap import dl_name_hash


ROOT = Path(__file__).resolve().parents[1]
PRIVATE_FBX = Path(r"S:\Downloads\dl2test.fbx")


def test_old_projects_default_to_dl1_and_preserve_unknown_fields() -> None:
    project = DlReanimatedProject.from_dict(
        {
            "format": "dl-reanimated-project",
            "schema_version": 1,
            "name": "旧项目 Проект café",
            "future_field": {"保留": True},
        }
    )
    assert project.game_id == DL1_GAME_ID
    assert project.rig.target_rig_ref == "builtin:male_npc_infected"
    assert project.extensions["unknown_fields"]["future_field"] == {"保留": True}


def test_unicode_project_path_bom_and_names_roundtrip(tmp_path: Path) -> None:
    folder = tmp_path / "项目 Проект café !"
    project = DlReanimatedProject.new("动画 Анимация cafe\u0301")
    project.animations.append(ProjectAnimation.create(
        folder / "动作 тест e\u0301 !.fbx", resource_name="unicode_path"
    ))
    path = project.save(folder / "工程 проект.dlraproj")
    path.write_bytes(b"\xef\xbb\xbf" + path.read_bytes())
    loaded = DlReanimatedProject.load(path)
    assert loaded.name == "动画 Анимация cafe\u0301"
    assert Path(loaded.animations[0].source_fbx).name == "动作 тест e\u0301 !.fbx"


def test_non_ascii_implicit_descriptor_is_blocked_but_explicit_crig_is_valid() -> None:
    with pytest.raises(ValueError, match="explicit descriptor"):
        dl_name_hash("骨骼")
    bone = ChromeRigBone(
        0, "骨骼", -1, 0x12345678, (0.0, 0.0, 0.0),
        (1.0, 0.0, 0.0, 0.0), (1.0, 1.0, 1.0),
    )
    rig = ChromeRig("custom:unicode", "Unicode", "Test", (bone,), 0)
    assert rig.validate().ok


def test_switching_profiles_updates_only_previous_defaults() -> None:
    project = DlReanimatedProject.new("profiles")
    apply_game_profile_defaults(project, ROOT, force=True)
    project.rig.trusted_source_rest_json = "自定义/custom rest.json"
    project.game_id = DL2_GAME_ID
    result = apply_game_profile_defaults(
        project, ROOT, previous_game_id=DL1_GAME_ID
    )
    assert project.rig.target_rig_ref == DL2_RIG_REF
    assert project.rig.retarget_mode == "exact"
    assert Path(project.rig.canonical_smd).name == "player_shadow_caster.smd"
    assert Path(project.rig.target_template_anm2).name == "0_m_fpp_farjump.anm2"
    assert project.rig.trusted_source_rest_json == "自定义/custom rest.json"
    assert "target rig" in result["changed"]


def test_cross_game_builtin_target_is_blocked() -> None:
    project = DlReanimatedProject.new("incoherent")
    project.game_id = DL2_GAME_ID
    assert any("DL1 male NPC target" in row for row in project.validate())


def _format42_payload() -> bytes:
    active = (0x11111111, 0x22222222)
    reference = (0x33333333,)
    header_size = 28
    descriptor_end = header_size + 4 * len(active)
    data_offset = 64
    header = struct.pack(
        "<4s12H",
        b"ANM2", 42, 2, 12, 0, len(active), data_offset, 2,
        descriptor_end, descriptor_end, 1, len(active) + len(reference), 7,
    )
    return header + struct.pack("<3I", *(active + reference)) + bytes(data_offset - 40) + b"curves"


def test_format1_format42_dispatch_and_descriptor_inspection(tmp_path: Path) -> None:
    assert detect_anm2_format(ROOT / "reference" / "infected_turn_90r.template.anm2") == 1
    path = tmp_path / "动画 формат 42.anm2"
    path.write_bytes(_format42_payload())
    assert detect_anm2_format(path) == 42
    header = parse_dl2_header42(path)
    assert header.frame_count == 12
    assert header.active_descriptors == (0x11111111, 0x22222222)
    assert header.reference_descriptors == (0x33333333,)
    assert header.validation_errors == ()


def test_global_bind_basis_correction_reconstructs_target_bind_with_wrapper_scale() -> None:
    angle = np.deg2rad(-90.0)
    wrapper = np.eye(4)
    wrapper[:3, :3] = 100.0 * np.asarray(
        ((1, 0, 0), (0, np.cos(angle), -np.sin(angle)), (0, np.sin(angle), np.cos(angle)))
    )
    wrapper[:3, 3] = (12.0, 93.0, -4.0)
    source_bind = wrapper.copy()
    target_bind = np.eye(4)
    target_bind[:3, 3] = (0.0, 0.930113, 0.0)
    correction = global_bind_basis_correction(source_bind, target_bind)
    reconstructed = corrected_target_global(source_bind, correction)
    assert np.allclose(reconstructed, target_bind, atol=1.0e-10)
    animated = source_bind.copy()
    animated[:3, 3] += (25.0, 0.0, 0.0)
    corrected = corrected_target_global(animated, correction)
    assert np.isfinite(corrected).all()
    assert not np.allclose(corrected, target_bind)


def test_dl2_root_policies_keep_pelvis_and_independent_motion_tracks_coherent() -> None:
    rig = ChromeRig.load(ROOT / "reference" / "dl2" / "player_shadow_caster.crig")
    bind = rig.bind_track_values()
    values = [[list(row) for row in bind] for _ in range(2)]
    pelvis = next(bone for bone in rig.bones if bone.name == "pelvis")
    pelvis_track = rig.descriptors.index(pelvis.descriptor)
    motion_track = rig.descriptors.index(0xCCC3CDDF)
    values[1][pelvis_track][3:6] = [2.0, 3.0, 4.0]
    first = list(values[0][pelvis_track][3:6])
    apply_global_root_policy(values, rig, "pelvis", "motion")
    assert values[1][pelvis_track][3] == pytest.approx(first[0])
    assert values[1][pelvis_track][5] == pytest.approx(first[2])
    assert values[1][pelvis_track][4] == pytest.approx(3.0)
    assert values[1][motion_track][3] == pytest.approx(2.0 - first[0])
    assert values[1][motion_track][5] == pytest.approx(4.0 - first[2])

    inplace = [[list(row) for row in bind] for _ in range(2)]
    inplace[1][pelvis_track][3:6] = [9.0, 8.0, 7.0]
    apply_global_root_policy(inplace, rig, "pelvis", "inplace")
    assert inplace[1][pelvis_track][3:6] == inplace[0][pelvis_track][3:6]


def test_fbx_scene_bind_priority_pose_then_transformlink_then_fallback() -> None:
    class SyntheticScene(FbxScene):
        def model_global_matrix(self, bone_id: int) -> np.ndarray:
            return np.eye(4) * (bone_id + 3.0)

    scene = object.__new__(SyntheticScene)
    scene.bind_pose_matrices = {1: np.eye(4) * 2.0}
    scene.geometries = (
        type("Geometry", (), {"clusters": (
            type("Cluster", (), {"bone_id": 1, "transform_link": np.eye(4) * 3.0})(),
            type("Cluster", (), {"bone_id": 2, "transform_link": np.eye(4) * 4.0})(),
        )})(),
    )
    values = scene.bone_globals((1, 2, 3))
    assert np.array_equal(values[1], np.eye(4) * 2.0)
    assert np.array_equal(values[2], np.eye(4) * 4.0)
    assert np.array_equal(values[3], np.eye(4) * 6.0)


def test_bundled_dl2_target_has_pelvis_independent_helpers_and_finger_roots() -> None:
    rig = ChromeRig.load(ROOT / "reference" / "dl2" / "player_shadow_caster.crig")
    assert rig.extensions["game_id"] == DL2_GAME_ID
    assert rig.bones[rig.root_index].name == "pelvis"
    roots = {bone.name for bone in rig.bones if bone.parent_index < 0}
    assert roots == {"pelvis", "l_iktarget", "r_iktarget", "player_shadowcaster"}
    names = {bone.name for bone in rig.bones}
    for side in ("l", "r"):
        assert {f"{side}_finger10", f"{side}_finger20", f"{side}_finger30", f"{side}_finger40"} <= names
    assert not any("hand1" in bone.tags for bone in rig.bones)


@pytest.mark.skipif(not PRIVATE_FBX.is_file(), reason="private supplied DL2 FBX is not available")
def test_private_dl2_fbx_is_superset_bind_corrected_and_builds() -> None:
    rig = ChromeRig.load(ROOT / "reference" / "dl2" / "player_shadow_caster.crig")
    stack = "Armature|m_fpp_emote_noise_02_mirror_Armature"
    preflight = preflight_fbx(
        PRIVATE_FBX, purpose="animation", animation_stack=stack,
        target_rig=rig, game_id=DL2_GAME_ID,
    )
    assert not preflight.blocking
    compatibility = preflight.inventory["target_compatibility"]
    assert compatibility["classification"] == "target_compatible_source_superset"
    assert compatibility["required_missing_bones"] == []
    assert compatibility["optional_helper_missing_bones"] == ["player_shadowcaster"]
    assert preflight.inventory["bind"]["selected_bind_source"] == "Pose::BindPose"
    result = build_exact_rig_anm2(PRIVATE_FBX, rig, fps=30, animation_stack=stack)
    assert result.frame_count == 91
    assert result.report["basis_correction_policy"] == "global_bind_basis_correction"
    assert result.report["root_mapping"]["source_bone"] == "pelvis"
    assert result.report["root_mapping"]["target_bone"] == "pelvis"
    assert result.report["static_target_bones"] == ["player_shadowcaster"]
    assert result.report["decoded_max_component_error"] < 0.004
