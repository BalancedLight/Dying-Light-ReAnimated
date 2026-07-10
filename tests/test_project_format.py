from __future__ import annotations

import json
from pathlib import Path

import pytest

from dlanm2_gui.workspace_project import (
    CURRENT_PROJECT_SCHEMA_VERSION,
    DlReanimatedProject,
    ProjectAnimation,
)


def test_project_roundtrip_uses_portable_paths(tmp_path: Path) -> None:
    inputs = tmp_path / "inputs"
    inputs.mkdir()
    fbx = inputs / "clip.fbx"
    rest = inputs / "rest.fbx"
    fbx.write_bytes(b"fbx")
    rest.write_bytes(b"fbx")

    project = DlReanimatedProject.new("Portable")
    project.rig.source_rest_fbx = str(rest)
    project.export.output_directory = str(tmp_path / "build")
    project.animations.append(ProjectAnimation.create(str(fbx), resource_name="clip"))
    path = project.save(tmp_path / "portable.dlraproj")

    raw = json.loads(path.read_text(encoding="utf-8"))
    assert not Path(raw["animations"][0]["source_fbx"]).is_absolute()
    assert not Path(raw["rig"]["source_rest_fbx"]).is_absolute()

    loaded = DlReanimatedProject.load(path)
    assert Path(loaded.animations[0].source_fbx) == fbx.resolve()
    assert Path(loaded.rig.source_rest_fbx) == rest.resolve()
    assert loaded.schema_version == CURRENT_PROJECT_SCHEMA_VERSION


def test_v0_project_is_migrated() -> None:
    project = DlReanimatedProject.from_dict({"name": "Legacy", "schema_version": 0})
    assert project.name == "Legacy"
    assert project.schema_version == CURRENT_PROJECT_SCHEMA_VERSION
    assert project.project_id
    assert project.export.pack_filename.endswith(".rpack")


def test_v3_project_adds_empty_animation_stack_selection() -> None:
    project = DlReanimatedProject.from_dict(
        {
            "format": "dl-reanimated-project",
            "schema_version": 3,
            "minimum_reader_version": 1,
            "name": "Legacy stack",
            "animations": [
                {
                    "animation_id": "clip",
                    "source_fbx": "multi.fbx",
                    "display_name": "Clip",
                    "resource_name": "clip",
                }
            ],
        }
    )
    assert project.schema_version == 4
    assert project.animations[0].source_animation_stack == ""



def test_v1_project_bind_policy_migration_preserves_explicit_rest() -> None:
    explicit = DlReanimatedProject.from_dict(
        {
            "format": "dl-reanimated-project",
            "schema_version": 1,
            "minimum_reader_version": 1,
            "name": "Explicit rest",
            "project_id": "id",
            "created_utc": "x",
            "modified_utc": "x",
            "rig": {"source_rest_fbx": "T-Pose.fbx"},
        }
    )
    assert explicit.schema_version == CURRENT_PROJECT_SCHEMA_VERSION
    assert explicit.rig.use_imported_animation_bind_pose is False

    embedded = DlReanimatedProject.from_dict(
        {
            "format": "dl-reanimated-project",
            "schema_version": 1,
            "minimum_reader_version": 1,
            "name": "Embedded rest",
            "project_id": "id2",
            "created_utc": "x",
            "modified_utc": "x",
            "rig": {"source_rest_fbx": ""},
        }
    )
    assert embedded.rig.use_imported_animation_bind_pose is True

def test_unknown_fields_are_preserved() -> None:
    project = DlReanimatedProject.from_dict(
        {
            "format": "dl-reanimated-project",
            "schema_version": 1,
            "minimum_reader_version": 1,
            "name": "Future extension",
            "project_id": "id",
            "created_utc": "x",
            "modified_utc": "x",
            "future_field": {"hello": "world"},
        }
    )
    assert project.extensions["unknown_fields"]["future_field"] == {"hello": "world"}


def test_newer_project_schema_is_rejected() -> None:
    with pytest.raises(ValueError, match="newer"):
        DlReanimatedProject.from_dict(
            {
                "format": "dl-reanimated-project",
                "schema_version": CURRENT_PROJECT_SCHEMA_VERSION + 1,
                "minimum_reader_version": CURRENT_PROJECT_SCHEMA_VERSION + 1,
            }
        )


def test_duplicate_animation_resource_is_global_across_script_targets() -> None:
    project = DlReanimatedProject.new("Duplicates")
    first = ProjectAnimation.create("one.fbx", resource_name="same_name")
    first.script_target = "player_male"
    second = ProjectAnimation.create("two.fbx", resource_name="same_name")
    second.script_target = "npc_female"
    project.animations.extend([first, second])
    errors = project.validate()
    assert any("Duplicate animation resource name" in error for error in errors)


def test_schema_v2_describes_embedded_bind_policy() -> None:
    root = Path(__file__).resolve().parents[1]
    schema = json.loads((root / "docs/schemas/dlraproj.schema.v2.json").read_text())
    assert schema["properties"]["schema_version"]["const"] == 2
    rig = schema["$defs"]["rig"]
    assert "use_imported_animation_bind_pose" in rig["required"]
    assert rig["properties"]["use_imported_animation_bind_pose"]["type"] == "boolean"


def test_schema_v4_describes_animation_stack_selection() -> None:
    root = Path(__file__).resolve().parents[1]
    schema = json.loads((root / "docs/schemas/dlraproj.schema.v4.json").read_text())
    assert schema["properties"]["schema_version"]["const"] == 4
    animation = schema["$defs"]["animation"]
    assert "source_animation_stack" in animation["required"]
