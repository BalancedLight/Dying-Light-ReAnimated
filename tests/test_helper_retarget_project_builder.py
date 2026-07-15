from __future__ import annotations

from pathlib import Path

from dlanm2_gui.helper_profiles import (
    extend_track_descriptors_for_helpers,
    recognized_helper_names,
)
from dlanm2_gui.oracle.smd_bind_pose import parse_smd_bind_pose
from dlanm2_gui.trackmap import dl_name_hash, read_track_descriptors
from dlanm2_gui.workspace_project import DlReanimatedProject, ProjectAnimation


ROOT = Path(__file__).resolve().parents[1]


def test_no_mapped_helpers_keeps_legacy_target_descriptor_order() -> None:
    _header, legacy = read_track_descriptors(
        ROOT / "reference/infected_turn_90r.template.anm2"
    )
    pose = parse_smd_bind_pose(ROOT / "reference/player_1_tpp.smd")

    selected = extend_track_descriptors_for_helpers(
        legacy, (), (bone.name for bone in pose.bones)
    )

    assert selected == legacy
    assert len(selected) == 70


def test_builder_appends_only_helpers_the_user_mapped() -> None:
    _header, legacy = read_track_descriptors(
        ROOT / "reference/infected_turn_90r.template.anm2"
    )
    pose = parse_smd_bind_pose(ROOT / "reference/player_1_tpp.smd")
    names = [bone.name for bone in pose.bones]

    selected = extend_track_descriptors_for_helpers(
        legacy, ("refcamera",), (name for name in names)
    )

    assert selected[: len(legacy)] == legacy
    assert selected[len(legacy) :] == [dl_name_hash("refcamera")]
    assert len(selected) == len(set(selected))


def test_bundled_smd_exposes_camera_helpers_without_a_profile() -> None:
    pose = parse_smd_bind_pose(ROOT / "reference/player_1_tpp.smd")

    helpers = recognized_helper_names(bone.name for bone in pose.bones)

    assert "refcamera" in helpers
    assert "eyecamera" in helpers
    assert "l_handholder" in helpers
    assert "r_handholder" in helpers


def test_helper_settings_persist_per_clip_without_project_schema_migration() -> None:
    project = DlReanimatedProject.new("Helpers")
    row = ProjectAnimation.create("clip.fbx")
    row.extensions["helper_retarget_rules"] = [
        {
            "target_bone": "refcamera",
            "source_bone": "Head",
            "transfer_policy": "rest_relative",
            "component_policy": "translation",
        }
    ]
    project.animations.append(row)

    loaded = DlReanimatedProject.from_dict(project.to_dict())

    assert loaded.animations[0].extensions == row.extensions
    assert loaded.minimum_reader_version == 1
