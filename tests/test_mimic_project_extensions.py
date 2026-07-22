from __future__ import annotations

from copy import deepcopy

from dlanm2_gui.mimic_project_builder import _copy_mapping_state
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
