from __future__ import annotations

import json
from pathlib import Path
from types import SimpleNamespace

import pytest

from dlanm2_gui import anm2, project_builder
from dlanm2_gui.anm2_writer import _build_packed_pages, _build_payload_with_pages
from dlanm2_gui.chrome_rig import ChromeRig, ChromeRigBone
from dlanm2_gui.trackmap import dl_name_hash
from dlanm2_gui.workspace_project import (
    CURRENT_PROJECT_SCHEMA_VERSION,
    DlReanimatedProject,
    ProjectAnimation,
)


def test_v8_schema_names_default_and_per_animation_targets() -> None:
    root = Path(__file__).resolve().parents[1]
    schema = json.loads(
        (root / "docs/schemas/dlraproj.schema.v8.json").read_text(encoding="utf-8")
    )

    assert schema["properties"]["schema_version"]["const"] == 8
    assert {
        "default_target_rig_ref",
        "default_target_rig_path",
    } <= schema["$defs"]["rig"]["properties"].keys()
    assert {
        "target_rig_ref",
        "target_rig_path",
        "mapping_profile_id",
        "source_root_bone",
        "target_root_bone",
    } <= schema["$defs"]["animation"]["properties"].keys()


def test_old_project_animation_inherits_target_without_losing_settings_or_map() -> None:
    mapping = {
        "format": "dl-reanimated-bone-map",
        "version": 1,
        "pairs": [
            {
                "source_bone": "target_hips",
                "target_bone": "SourceHips",
                "future_pair_setting": {"kept": True},
            }
        ],
        "future_profile_setting": 17,
    }
    payload = {
        "format": "dl-reanimated-project",
        "schema_version": 7,
        "minimum_reader_version": 1,
        "name": "Legacy multi target migration",
        "project_id": "legacy-id",
        "created_utc": "created",
        "modified_utc": "modified",
        "future_top": {"opaque": [1, 2, 3]},
        "rig": {
            "retarget_mode": "exact",
            "target_rig_ref": "custom:legacy",
            "target_rig_path": "rigs/legacy.crig",
            "future_rig": "keep me",
        },
        "animations": [
            {
                "animation_id": "clip",
                "source_fbx": "clip.fbx",
                "display_name": "Clip",
                "resource_name": "clip",
                "mapping_profile_id": "legacy-map",
                "fps": 48,
                "start_frame": 3,
                "end_frame": 90,
                "future_animation": {"opaque": True},
                "extensions": {
                    "root_mapping_v1": {
                        "source_bone": "SourceRoot",
                        "target_bone": "TargetRoot",
                    }
                },
            }
        ],
        "mapping_profiles": {"legacy-map": mapping},
    }

    project = DlReanimatedProject.from_dict(payload)
    row = project.animations[0]

    assert project.schema_version == CURRENT_PROJECT_SCHEMA_VERSION == 8
    assert project.rig.default_target_rig_ref == "custom:legacy"
    assert row.target_rig_ref == ""
    assert row.target_rig_path == ""
    assert row.mapping_profile_id == "legacy-map"
    assert (row.fps, row.start_frame, row.end_frame) == (48, 3, 90)
    assert (row.source_root_bone, row.target_root_bone) == (
        "SourceRoot",
        "TargetRoot",
    )
    assert project.mapping_profiles["legacy-map"] == mapping

    roundtrip = project.to_dict()
    assert roundtrip["future_top"] == payload["future_top"]
    assert roundtrip["rig"]["future_rig"] == "keep me"
    assert roundtrip["animations"][0]["future_animation"] == {"opaque": True}
    assert roundtrip["mapping_profiles"]["legacy-map"] == mapping


def test_animation_target_override_path_is_portable_and_roundtrips(tmp_path: Path) -> None:
    project_file = tmp_path / "project" / "targets.dlraproj"
    rig_path = tmp_path / "project" / "rigs" / "animal.crig"
    source_path = tmp_path / "project" / "sources" / "walk.fbx"
    rig_path.parent.mkdir(parents=True)
    source_path.parent.mkdir(parents=True)
    rig_path.write_bytes(b"rig")
    source_path.write_bytes(b"fbx")

    project = DlReanimatedProject.new("Targets")
    project.rig.retarget_mode = "exact"
    project.rig.target_rig_ref = "custom:default"
    project.rig.target_rig_path = str(rig_path)
    row = ProjectAnimation.create(str(source_path), resource_name="walk")
    row.target_rig_ref = "custom:animal"
    row.target_rig_path = str(rig_path)
    row.source_root_bone = "AnimalRoot"
    row.target_root_bone = "root"
    project.animations.append(row)

    project.save(project_file)
    raw = json.loads(project_file.read_text(encoding="utf-8"))
    assert not Path(raw["rig"]["target_rig_path"]).is_absolute()
    assert raw["rig"]["default_target_rig_path"] == raw["rig"]["target_rig_path"]
    assert not Path(raw["animations"][0]["target_rig_path"]).is_absolute()
    assert raw["animations"][0]["target_rig_ref"] == "custom:animal"

    loaded = DlReanimatedProject.load(project_file)
    loaded_row = loaded.animations[0]
    assert Path(loaded.rig.target_rig_path) == rig_path.resolve()
    assert Path(loaded_row.target_rig_path) == rig_path.resolve()
    assert (loaded_row.source_root_bone, loaded_row.target_root_bone) == (
        "AnimalRoot",
        "root",
    )


def test_exact_project_can_use_only_per_animation_targets() -> None:
    project = DlReanimatedProject.new("Per clip")
    project.rig.retarget_mode = "exact"
    project.rig.target_rig_ref = ""
    project.rig.target_rig_path = ""
    first = ProjectAnimation.create("one.fbx", resource_name="one")
    first.target_rig_ref = "custom:one"
    first.target_rig_path = "one.crig"
    second = ProjectAnimation.create("two.fbx", resource_name="two")
    second.target_rig_ref = "custom:two"
    second.target_rig_path = "two.crig"
    project.animations.extend((first, second))

    assert not any("default .crig target" in message for message in project.validate())


def _minimal_rig(rig_id: str, name: str, translation: float) -> ChromeRig:
    rig = ChromeRig(
        rig_id,
        name,
        "Generic Object",
        (
            ChromeRigBone(
                0,
                "root",
                -1,
                dl_name_hash("root"),
                (translation, 0.0, 0.0),
                (1.0, 0.0, 0.0, 0.0),
            ),
        ),
        0,
        extensions={"game_id": "dying_light_1"},
    )
    rig.validate().require_valid()
    return rig


def _valid_anm2_payload(descriptor: int) -> bytes:
    header = anm2.Anm2Header(
        format_version=anm2.FORMAT_VERSION,
        unknown06=1,
        frame_count=2,
        track_count=1,
        unknown12=1,
        unknown14=0,
        declared_length=0,
        unknown20=1,
        unknown24=0,
        unknown28=0,
    )
    pages, spans = _build_packed_pages(bytes(0x10), [bytes(0x10)], 2)
    return _build_payload_with_pages(header, [descriptor], pages, spans)


def test_builder_groups_two_per_animation_crig_targets(
    tmp_path: Path, monkeypatch: pytest.MonkeyPatch
) -> None:
    first_rig = _minimal_rig("custom:first", "First", 0.0)
    second_rig = _minimal_rig("custom:second", "Second", 1.25)
    first_path = first_rig.save(tmp_path / "first.crig")
    second_path = second_rig.save(tmp_path / "second.crig")
    first_source = tmp_path / "first.fbx"
    second_source = tmp_path / "second.fbx"
    first_source.write_bytes(b"synthetic exact fixture")
    second_source.write_bytes(b"synthetic exact fixture")

    project = DlReanimatedProject.new("Two target rigs")
    project.rig.retarget_mode = "exact"
    project.rig.target_rig_ref = ""
    project.rig.target_rig_path = ""
    project.export.output_directory = str(tmp_path / "build")
    project.export.include_validation_controls = False
    for source, rig, path in (
        (first_source, first_rig, first_path),
        (second_source, second_rig, second_path),
    ):
        row = ProjectAnimation.create(str(source), resource_name=source.stem)
        row.target_rig_ref = rig.rig_id
        row.target_rig_path = str(path)
        project.animations.append(row)

    class FakeDocument:
        animation_stacks: tuple[object, ...] = ()

        def __init__(self, _path: Path) -> None:
            pass

    monkeypatch.setattr(project_builder, "_FbxDocument", FakeDocument)
    monkeypatch.setattr(
        project_builder,
        "classify_target_compatibility",
        lambda _document, _rig: {
            "classification": "exact_identity",
            "required_missing_bones": [],
            "hierarchy_mismatches": [],
        },
    )

    seen: list[str] = []

    def fake_exact(_source: Path, rig: ChromeRig, **_kwargs):
        seen.append(rig.rig_id)
        return SimpleNamespace(
            payload=_valid_anm2_payload(rig.bones[0].descriptor),
            report={
                "frame_count": 2,
                "warnings": [],
                "source_animation_stack": "",
            },
        )

    monkeypatch.setattr(project_builder, "build_exact_rig_anm2", fake_exact)

    result = project_builder.build_project(project)

    assert seen == ["custom:first", "custom:second"]
    assert {row.target_rig_ref for row in result.built_animations} == {
        "custom:first",
        "custom:second",
    }
    report = json.loads(Path(result.report_path).read_text(encoding="utf-8"))
    assert report["retarget_mode"] == "exact"
    assert report["target_rig_group_count"] == 2
    assert {
        group["target_rig_ref"] for group in report["target_rig_groups"]
    } == {"custom:first", "custom:second"}


def test_missing_late_animation_fails_before_any_output(
    tmp_path: Path, monkeypatch: pytest.MonkeyPatch
) -> None:
    rig = _minimal_rig("custom:preflight", "Preflight", 0.0)
    rig_path = rig.save(tmp_path / "target.crig")
    present = tmp_path / "present.fbx"
    present.write_bytes(b"synthetic exact fixture")
    missing = tmp_path / "missing.fbx"
    output = tmp_path / "must_not_exist"

    project = DlReanimatedProject.new("Pre-output gate")
    project.rig.retarget_mode = "exact"
    project.rig.target_rig_ref = rig.rig_id
    project.rig.target_rig_path = str(rig_path)
    project.export.output_directory = str(output)
    project.export.include_validation_controls = False
    project.animations.extend(
        (
            ProjectAnimation.create(str(present), resource_name="present"),
            ProjectAnimation.create(str(missing), resource_name="missing"),
        )
    )

    class FakeDocument:
        def __init__(self, _path: Path) -> None:
            pass

    monkeypatch.setattr(project_builder, "_FbxDocument", FakeDocument)
    monkeypatch.setattr(
        project_builder,
        "classify_target_compatibility",
        lambda *_args: {
            "classification": "exact_identity",
            "required_missing_bones": [],
            "hierarchy_mismatches": [],
        },
    )

    with pytest.raises(FileNotFoundError, match="No ANM2 or RPack output was created"):
        project_builder.build_project(project)

    assert not output.exists()


def test_invalid_frame_range_fails_before_candidate_anm2_output(
    tmp_path: Path,
    monkeypatch: pytest.MonkeyPatch,
) -> None:
    rig = _minimal_rig("custom:frames", "Frames", 0.0)
    rig_path = rig.save(tmp_path / "frames.crig")
    source = tmp_path / "clip.fbx"
    source.write_bytes(b"synthetic exact fixture")
    output = tmp_path / "must_not_exist"
    project = DlReanimatedProject.new("Frame preflight")
    project.rig.retarget_mode = "exact"
    project.rig.target_rig_ref = rig.rig_id
    project.rig.target_rig_path = str(rig_path)
    project.export.output_directory = str(output)
    row = ProjectAnimation.create(str(source), resource_name="clip")
    row.end_frame = 5
    project.animations.append(row)

    class FakeDocument:
        def __init__(self, _path: Path) -> None:
            pass

        def frame_ticks(self, *, fps: int) -> list[int]:
            assert fps == 30
            return [0, 1]

    monkeypatch.setattr(project_builder, "_FbxDocument", FakeDocument)
    monkeypatch.setattr(
        project_builder,
        "classify_target_compatibility",
        lambda *_args: {
            "classification": "exact_identity",
            "required_missing_bones": [],
            "hierarchy_mismatches": [],
        },
    )
    monkeypatch.setattr(
        project_builder,
        "build_exact_rig_anm2",
        lambda *_args, **_kwargs: pytest.fail("retargeting started before frame preflight"),
    )

    with pytest.raises(ValueError, match="invalid configured frame range.*No ANM2"):
        project_builder.build_project(project)

    assert not output.exists()


def test_append_name_collision_fails_before_candidate_output(
    tmp_path: Path,
    monkeypatch: pytest.MonkeyPatch,
) -> None:
    rig = _minimal_rig("custom:collision", "Collision", 0.0)
    rig_path = rig.save(tmp_path / "collision.crig")
    source = tmp_path / "walk.fbx"
    source.write_bytes(b"synthetic exact fixture")
    existing = tmp_path / "existing.rpack"
    existing.write_bytes(b"tool pack fixture")
    output = tmp_path / "must_not_exist"
    project = DlReanimatedProject.new("Collision preflight")
    project.rig.retarget_mode = "exact"
    project.rig.target_rig_ref = rig.rig_id
    project.rig.target_rig_path = str(rig_path)
    project.export.mode = "append"
    project.export.existing_rpack = str(existing)
    project.export.collision_policy = "error"
    project.export.output_directory = str(output)
    project.animations.append(
        ProjectAnimation.create(str(source), resource_name="walk")
    )

    class FakeDocument:
        def __init__(self, _path: Path) -> None:
            pass

        def frame_ticks(self, *, fps: int) -> list[int]:
            return [0, 1]

    monkeypatch.setattr(project_builder, "_FbxDocument", FakeDocument)
    monkeypatch.setattr(
        project_builder,
        "classify_target_compatibility",
        lambda *_args: {
            "classification": "exact_identity",
            "required_missing_bones": [],
            "hierarchy_mismatches": [],
        },
    )
    monkeypatch.setattr(
        project_builder,
        "extract_animation_library",
        lambda _payload: project_builder.AnimationLibrary(
            {"dl_reanimated_walk": b"old"}, {}
        ),
    )
    monkeypatch.setattr(
        project_builder.PackManifest,
        "load_for_pack",
        staticmethod(lambda _path: None),
    )
    monkeypatch.setattr(
        project_builder,
        "build_exact_rig_anm2",
        lambda *_args, **_kwargs: pytest.fail("retargeting started before collision preflight"),
    )

    with pytest.raises(ValueError, match="already exist.*no ANM2"):
        project_builder.build_project(project)

    assert not output.exists()
