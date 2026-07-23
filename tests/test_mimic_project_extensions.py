from __future__ import annotations

from copy import deepcopy
import json
from pathlib import Path

from dlanm2_gui.game_profiles import (
    DL1_GAME_ID,
    DL2_GAME_ID,
    apply_game_profile_defaults,
)
from dlanm2_gui.mimic_gui import morph_facial_ui_available
from dlanm2_gui.mimic_project_builder import (
    _copy_mapping_state,
    build_project_with_mimics,
)
from dlanm2_gui.project_builder import ProjectBuildResult
from dlanm2_gui.workspace_project import DlReanimatedProject, ProjectAnimation


def test_mimic_and_motion_override_survive_project_save_load(tmp_path):
    project = DlReanimatedProject.new("Facial project")
    project.rig.extensions.update({
        "facial_animation_policy": "auto",
        "mimic_profile_ref": "builtin:human_common46",
    })
    row = ProjectAnimation.create("face.fbx")
    row.extensions["mimic"] = {
        "mode": "both",
        "mapping": [
            {
                "source": "jawOpen",
                "target_descriptor": "0xD38C4C58",
                "weight": 1.0,
                "bias": 0.0,
                "enabled": True,
                "confidence": 1.0,
                "method": "manual",
            }
        ],
    }
    row.extensions["root_motion_source_bone"] = "mixamorig:Spine"
    project.animations.append(row)
    path = project.save(tmp_path / "facial.dlraproj")
    loaded = DlReanimatedProject.load(path)
    assert loaded.rig.extensions["mimic_profile_ref"] == "builtin:human_common46"
    assert loaded.animations[0].extensions["mimic"]["mode"] == "both"
    assert loaded.animations[0].extensions["root_motion_source_bone"] == "mixamorig:Spine"


def test_body_builder_state_is_synchronized_without_copying_enabled_flag():
    project = DlReanimatedProject.new("State sync")
    row = ProjectAnimation.create("body.fbx")
    row.extensions = {"mimic": {"mode": "both"}}
    project.animations.append(row)
    body_project = deepcopy(project)
    body_row = body_project.animations[0]
    body_project.mapping_profiles["generated"] = {"rows": [{"source": "hips"}]}
    body_row.mapping_profile_id = "generated"
    body_row.source_fps = 24.0
    body_row.source_root_bone = "source_hips"
    body_row.target_root_bone = "target_hips"
    body_row.ik_preset = "retargeted"
    body_row.extensions["retarget_domain"] = {"kind": "semantic"}
    body_row.enabled = False

    _copy_mapping_state(project, body_project)

    assert project.animations[0].enabled is True
    assert project.animations[0].mapping_profile_id == "generated"
    assert project.animations[0].source_fps == 24.0
    assert project.animations[0].source_root_bone == "source_hips"
    assert project.animations[0].target_root_bone == "target_hips"
    assert project.animations[0].ik_preset == "retargeted"
    assert project.animations[0].extensions["retarget_domain"] == {
        "kind": "semantic"
    }
    assert project.mapping_profiles == body_project.mapping_profiles
    assert project.mapping_profiles is not body_project.mapping_profiles
    assert project.animations[0].extensions is not body_row.extensions


def test_dl2_hides_morph_facial_ui_without_changing_dl1_support():
    assert morph_facial_ui_available("dying_light_1")
    assert not morph_facial_ui_available(DL2_GAME_ID)


def test_dl2_build_skips_mimic_only_and_keeps_body_from_both(
    tmp_path,
):
    project = DlReanimatedProject.new("DL2 skeletal face")
    project.game_id = DL2_GAME_ID
    apply_game_profile_defaults(
        project,
        Path(__file__).resolve().parents[1],
        previous_game_id=DL1_GAME_ID,
        force=True,
    )
    mimic_only = ProjectAnimation.create("face_only.fbx")
    mimic_only.extensions["mimic"] = {"mode": "mimic_only"}
    both = ProjectAnimation.create("body_and_face.fbx")
    both.extensions["mimic"] = {"mode": "both"}
    project.animations.extend((mimic_only, both))
    captured_enabled = []
    report_path = tmp_path / "build_report.json"

    def body_builder(body_project, *, progress=None):
        captured_enabled.extend(
            row.animation_id for row in body_project.animations if row.enabled
        )
        report_path.write_text(
            json.dumps({"warnings": []}),
            encoding="utf-8",
        )
        return ProjectBuildResult(
            status="ok",
            pack_path=str(tmp_path / "body.rpack"),
            manifest_path=str(tmp_path / "body.rpack.dlrmanifest.json"),
            report_path=str(report_path),
            build_mode="new",
            pack_sha256="0" * 64,
            animation_count=1,
            script_count=1,
        )

    result = build_project_with_mimics(
        project,
        progress=None,
        body_builder=body_builder,
    )

    assert captured_enabled == [both.animation_id]
    assert any("skipped stale mimic-only" in row for row in result.warnings)
    assert any("exported the skeletal body only" in row for row in result.warnings)
    assert project.animations[0].extensions["mimic"]["mode"] == "mimic_only"
    assert project.animations[1].extensions["mimic"]["mode"] == "both"
    report = json.loads(report_path.read_text(encoding="utf-8"))
    assert report["mimic_prototype"]["enabled"] is False
    assert report["mimic_prototype"]["resource_count"] == 0
