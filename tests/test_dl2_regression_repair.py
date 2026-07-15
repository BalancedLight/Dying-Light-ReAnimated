from __future__ import annotations

from copy import deepcopy
from pathlib import Path
import shutil

import numpy as np
import pytest

from dlanm2_gui.bone_maps import GenericBoneMap
from dlanm2_gui.chrome_rig import ChromeRig
from dlanm2_gui.game_profiles import (
    DL2_GAME_ID,
    GAME_PROFILES,
    apply_game_profile_defaults,
)
from dlanm2_gui.project_builder import _build_body_project
from dlanm2_gui.retarget_engines.mapped_rig import (
    SourceGlobalNormalization,
    reconstruct_target_globals,
    target_bind_local_matrix,
    validate_hierarchy_safety,
)
from dlanm2_gui.target_package import validate_target_package
from dlanm2_gui.retarget_routing import select_exact_solver
from dlanm2_gui.workspace_project import DlReanimatedProject, ProjectAnimation


ROOT = Path(__file__).resolve().parents[1]
DL2_SMD = ROOT / "reference" / "dl2" / "player_shadow_caster.smd"
DL2_CRIG = ROOT / "reference" / "dl2" / "player_shadow_caster.crig"
DL2_REFERENCE = ROOT / "reference" / "dl2" / "0_m_fpp_farjump.anm2"


def _bind_globals(rig: ChromeRig) -> dict[str, np.ndarray]:
    result: dict[str, np.ndarray] = {}
    for bone in rig.bones:
        local = target_bind_local_matrix(bone)
        result[bone.name] = (
            result[rig.bones[bone.parent_index].name] @ local
            if bone.parent_index >= 0
            else local
        )
    return result


def test_bundled_dl2_target_package_is_the_coherent_81_bone_shadow_caster() -> None:
    result = validate_target_package(GAME_PROFILES[DL2_GAME_ID], ROOT)
    assert result.status == "pass"
    assert result.smd_bone_count == result.crig_bone_count == 81
    assert result.bone_names_match
    assert result.parents_match
    assert result.roots_match
    assert result.bind_pose_match
    assert result.source_smd_filename_match
    assert result.source_smd_hash_match
    assert result.reference_anm2_hash_match
    assert result.reference_anm2_format_match
    assert result.game_id_match
    assert result.primary_root_match
    assert set(result.roots) == {
        "pelvis", "l_iktarget", "r_iktarget", "player_shadowcaster"
    }


def test_modified_smd_fails_coherence_and_blocks_a_builtin_build(
    tmp_path: Path,
) -> None:
    altered_smd = tmp_path / DL2_SMD.name
    shutil.copy2(DL2_SMD, altered_smd)
    text = altered_smd.read_text(encoding="utf-8")
    altered_smd.write_text(
        text.replace('64 "head" 63', '64 "head_regressed" 63'), encoding="utf-8"
    )
    result = validate_target_package(
        GAME_PROFILES[DL2_GAME_ID],
        smd_path=altered_smd,
        crig_path=DL2_CRIG,
        reference_anm2_path=DL2_REFERENCE,
    )
    assert result.status == "fail"
    assert not result.bone_names_match
    assert not result.source_smd_hash_match
    with pytest.raises(ValueError, match="bundled DL2 target SMD and CRIG"):
        result.require_valid("Dying Light 2")

    project = DlReanimatedProject.new("Broken bundled DL2 package")
    project.game_id = DL2_GAME_ID
    apply_game_profile_defaults(project, ROOT, force=True)
    project.rig.canonical_smd = str(altered_smd)
    project.export.output_directory = str(tmp_path / "output")
    project.animations.append(
        ProjectAnimation.create(str(tmp_path / "not_reached.fbx"), resource_name="blocked")
    )
    with pytest.raises(ValueError, match="bundled DL2 target SMD and CRIG"):
        _build_body_project(project)


def test_source_global_normalization_applies_units_and_axis_once() -> None:
    normalized_wrapper_input = np.eye(4)
    normalized_wrapper_input[:3, 3] = (0.0, 0.930113, 0.0)
    wrapper_contract = SourceGlobalNormalization(
        meters_per_unit=0.01,
        convert_y_up_to_dying_light=False,
        wrapper_scale_normalization_factor=0.01,
        wrapper_axis_conversion=True,
    )
    output = wrapper_contract.apply(normalized_wrapper_input)
    assert np.allclose(output[:3, 3], normalized_wrapper_input[:3, 3])
    report = wrapper_contract.to_report()
    assert report["meters_per_unit"] == pytest.approx(0.01)
    assert report["unit_conversion_count"] == 1
    assert report["axis_conversion"] == "fbx_y_up_to_dying_light"
    assert report["axis_conversion_count"] == 1
    assert report["axis_conversion_source"] == "retained_wrapper"
    assert report["wrapper_policy"] == "retained_and_scale_normalized"
    assert report["target_crig_bind_conversion_count"] == 0

    raw_centimeters = np.eye(4)
    raw_centimeters[:3, 3] = (0.0, 93.0113, 0.0)
    explicit_contract = SourceGlobalNormalization(
        meters_per_unit=0.01,
        convert_y_up_to_dying_light=True,
    )
    explicit = explicit_contract.apply(raw_centimeters)
    assert np.linalg.norm(explicit[:3, 3]) == pytest.approx(0.930113)
    assert explicit_contract.to_report()["axis_conversion_count"] == 1
    with pytest.raises(ValueError, match="exactly once"):
        SourceGlobalNormalization(
            meters_per_unit=0.01,
            convert_y_up_to_dying_light=True,
            unit_conversion_count=2,
        )
    with pytest.raises(ValueError, match="axis conversion"):
        SourceGlobalNormalization(
            meters_per_unit=0.01,
            convert_y_up_to_dying_light=True,
            axis_conversion_count=2,
        )


def test_automatic_repair_cannot_silently_select_the_mapped_solver() -> None:
    profile = GenericBoneMap.create(
        "Unreviewed repair",
        "target",
        "source",
        origin="automatic_repair",
    )
    selection = select_exact_solver(
        {
            "classification": "incompatible",
            "required_missing_bones": ["pelvis"],
            "hierarchy_mismatches": [],
        },
        profile,
    )
    assert not selection.build_allowed
    assert selection.selected_engine == ""
    assert selection.mapping_profile_changed_solver is False
    assert "explicitly reviewed" in selection.blocking_error


def test_bind_frame_reconstructs_target_and_independent_roots_stay_independent() -> None:
    rig = ChromeRig.load(DL2_CRIG)
    bind_frame = rig.bind_track_values()
    reconstructed = reconstruct_target_globals(rig, bind_frame)
    expected = _bind_globals(rig)
    assert reconstructed.keys() == expected.keys()
    for name in expected:
        assert np.allclose(reconstructed[name], expected[name], atol=2.0e-6)

    moved = deepcopy(bind_frame)
    pelvis = next(bone for bone in rig.bones if bone.name == "pelvis")
    pelvis_track = rig.descriptors.index(pelvis.descriptor)
    moved[pelvis_track][3] += 0.5
    moved_globals = reconstruct_target_globals(rig, moved)
    for independent in ("l_iktarget", "r_iktarget", "player_shadowcaster"):
        assert np.allclose(moved_globals[independent], reconstructed[independent])


def test_hierarchy_safety_rejects_displacement_and_accepts_normal_pose() -> None:
    rig = ChromeRig.load(DL2_CRIG)
    bind = rig.bind_track_values()
    normal = [deepcopy(bind), deepcopy(bind)]
    upperarm = next(bone for bone in rig.bones if bone.name == "l_upperarm")
    upperarm_track = rig.descriptors.index(upperarm.descriptor)
    normal[1][upperarm_track][0] = 0.08
    report = validate_hierarchy_safety(
        rig, normal, preserve_non_root_translations=True
    )
    assert report["status"] == "pass"
    assert report["maximum_non_root_translation_delta_meters"] == pytest.approx(0.0)
    assert report["maximum_parent_child_length_ratio"] == pytest.approx(1.0)
    assert report["minimum_parent_child_length_ratio"] == pytest.approx(1.0)
    assert report["minimum_scale"] > 0.0

    catastrophic = [deepcopy(bind), deepcopy(bind)]
    calf = next(bone for bone in rig.bones if bone.name == "l_calf")
    calf_track = rig.descriptors.index(calf.descriptor)
    catastrophic[1][calf_track][3] += 3.0
    with pytest.raises(ValueError, match="failed safety validation"):
        validate_hierarchy_safety(
            rig, catastrophic, preserve_non_root_translations=True
        )
