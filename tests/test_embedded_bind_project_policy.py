from __future__ import annotations

import json
from pathlib import Path
from types import SimpleNamespace

import pytest

from dlanm2_gui import anm2, project_builder
from dlanm2_gui.anm2_writer import (
    _build_packed_pages,
    _build_payload_with_pages,
)
from dlanm2_gui.retarget_profiles import SourceBoneMappingProfile
from dlanm2_gui.workspace_project import DlReanimatedProject, ProjectAnimation


class _FakeDocument:
    limb_models = ("mixamorig:Hips",)
    parent_by_name = {"mixamorig:Hips": None}

    def __init__(self, _path: Path) -> None:
        pass


def _valid_anm2_payload() -> bytes:
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
    return _build_payload_with_pages(header, [0x12345678], pages, spans)


def _project(tmp_path: Path, *, embedded: bool) -> DlReanimatedProject:
    animation = tmp_path / "clip.fbx"
    rest = tmp_path / "rest.fbx"
    trusted = tmp_path / "rest.json"
    smd = tmp_path / "target.smd"
    template = tmp_path / "template.anm2"
    control = tmp_path / "control.anm2"
    for path in (animation, rest, trusted, smd, template, control):
        path.write_bytes(b"fixture")

    project = DlReanimatedProject.new("Bind policy")
    project.rig.use_imported_animation_bind_pose = embedded
    project.rig.source_rest_fbx = str(rest)
    project.rig.trusted_source_rest_json = str(trusted)
    project.rig.canonical_smd = str(smd)
    project.rig.target_template_anm2 = str(template)
    project.rig.stock_writer_control_anm2 = str(control)
    project.export.output_directory = str(tmp_path / "build")
    project.export.write_intermediate_anm2 = False
    row = ProjectAnimation.create(str(animation), resource_name="clip")
    project.animations.append(row)
    return project


def _install_fakes(monkeypatch: pytest.MonkeyPatch, captured: dict[str, object]) -> None:
    monkeypatch.setattr(project_builder, "_FbxDocument", _FakeDocument)

    profile = SourceBoneMappingProfile.empty(("mixamorig:Hips",))
    profile.set_mapping("hips", "mixamorig:Hips")
    monkeypatch.setattr(
        project_builder,
        "_mapping_profile_for_animation",
        lambda *_args, **_kwargs: SimpleNamespace(
            profile_id=profile.profile_id,
            validate=lambda _bones: [],
            canonical_aliases=lambda: {},
        ),
    )

    def fake_build_fbx_rpack(**kwargs):
        captured.update(kwargs)
        out = Path(kwargs["out_dir"])
        candidate = out / "candidate.anm2"
        candidate.parent.mkdir(parents=True, exist_ok=True)
        candidate.write_bytes(_valid_anm2_payload())
        (out / "retarget_candidate_summary.json").write_text(
            json.dumps(
                [
                    {
                        "candidate_path": str(candidate),
                        "frame_count": 2,
                    }
                ]
            ),
            encoding="utf-8",
        )
        return {"status": "ok"}

    monkeypatch.setattr(project_builder, "build_fbx_rpack", fake_build_fbx_rpack)


def test_embedded_bind_uses_each_animation_fbx_and_skips_trusted_json(
    tmp_path: Path, monkeypatch: pytest.MonkeyPatch
) -> None:
    project = _project(tmp_path, embedded=True)
    captured: dict[str, object] = {}
    _install_fakes(monkeypatch, captured)

    project_builder.build_project(project)

    source = Path(project.animations[0].source_fbx)
    assert Path(captured["source_rest_fbx"]) == source
    assert captured["trusted_source_rest_json"] is None


def test_explicit_rest_mode_uses_selected_rest_and_trusted_json(
    tmp_path: Path, monkeypatch: pytest.MonkeyPatch
) -> None:
    project = _project(tmp_path, embedded=False)
    captured: dict[str, object] = {}
    _install_fakes(monkeypatch, captured)

    project_builder.build_project(project)

    assert Path(captured["source_rest_fbx"]) == Path(project.rig.source_rest_fbx)
    assert Path(captured["trusted_source_rest_json"]) == Path(
        project.rig.trusted_source_rest_json
    )


def test_humanoid_project_passes_embedded_helper_rules_to_value_builder(
    tmp_path: Path, monkeypatch: pytest.MonkeyPatch
) -> None:
    project = _project(tmp_path, embedded=True)
    # Old projects may still contain this extension. It no longer gates helper
    # visibility or descriptor emission.
    project.animations[0].extensions["helper_target_profile"] = "dl1_player_fpp_helpers"
    project.animations[0].extensions["helper_retarget_rules"] = [
        {
            "target_bone": "refcamera",
            "source_bone": "mixamorig:Hips",
            "transfer_policy": "rest_relative",
            "component_policy": "translation",
        }
    ]
    captured: dict[str, object] = {}
    _install_fakes(monkeypatch, captured)

    project_builder.build_project(project)

    assert "helper_target_profile" not in captured
    assert captured["helper_rules"] == project.animations[0].extensions[
        "helper_retarget_rules"
    ]


def test_explicit_rest_mode_rejects_blank_or_directory(tmp_path: Path) -> None:
    project = _project(tmp_path, embedded=False)
    project.rig.source_rest_fbx = ""
    assert any("source rest/T-pose" in row for row in project.validate())

    project.rig.source_rest_fbx = str(tmp_path)
    with pytest.raises(FileNotFoundError, match="source_rest_fbx must be a valid FBX file"):
        project_builder.build_project(project)
