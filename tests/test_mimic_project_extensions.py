from __future__ import annotations

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
