from __future__ import annotations

import io
import math
from pathlib import Path
import zipfile

import numpy as np
import pytest

from dlanm2_gui.anm2_components import decode_samples
from dlanm2_gui.anm2_writer import build_payload_from_values
from dlanm2_gui.chrome_rig import ChromeRig
from dlanm2_gui.chrome_rig_builder import (
    build_chrome_rig_from_fbx,
    build_chrome_rig_from_smd_template,
)
from dlanm2_gui.oracle.binary_fbx_mixamo import FBX_TICKS_PER_SECOND
from dlanm2_gui.retarget_engines.exact_rig import build_exact_rig_anm2
from dlanm2_gui.retarget_engines.base import RetargetBuild
from dlanm2_gui.rp6l import extract_animation_library
from dlanm2_gui.workspace_project import (
    CURRENT_PROJECT_SCHEMA_VERSION,
    DlReanimatedProject,
    ProjectAnimation,
)


def _rotation_z(degrees: float) -> np.ndarray:
    angle = math.radians(degrees)
    matrix = np.eye(4)
    matrix[0, 0] = matrix[1, 1] = math.cos(angle)
    matrix[0, 1] = -math.sin(angle)
    matrix[1, 0] = math.sin(angle)
    return matrix


class _ObjectFbx:
    def __init__(self, _path: Path, names: tuple[str, ...] = ("root", "bone_door", "bone_handle")):
        self.names = names
        self.limb_models = {name: index + 1 for index, name in enumerate(names)}
        self.parent_by_name = {
            name: (None if index == 0 else names[index - 1])
            for index, name in enumerate(names)
        }
        self.meters_per_unit = 0.01

    def frame_count(self, *, fps: int) -> int:
        assert fps == 30
        return 3

    def _local_matrix(self, object_id: int, *, tick: int, use_animation: bool) -> np.ndarray:
        frame = int(round(tick * 30 / FBX_TICKS_PER_SECOND)) if use_animation else 0
        matrix = np.eye(4)
        if object_id == 1:
            matrix[0, 3] = float(frame)
        elif object_id == 2:
            matrix = _rotation_z(frame * 45.0)
            matrix[1, 3] = 10.0
        else:
            matrix[2, 3] = 5.0
        return matrix


def _factory(names: tuple[str, ...]):
    return lambda path: _ObjectFbx(path, names)


@pytest.mark.parametrize("names", [("root",), ("root", "bone_door", "bone_handle")])
def test_model_only_crig_and_exact_rig_object_export(names: tuple[str, ...], tmp_path: Path) -> None:
    rig = build_chrome_rig_from_fbx(
        tmp_path / "object.fbx",
        document_factory=_factory(names),
    )
    assert [bone.name for bone in rig.bones] == list(names)
    assert rig.validate().ok

    first = rig.to_bytes()
    second = rig.to_bytes()
    assert first == second
    path = rig.save(tmp_path / "object.crig")
    loaded = ChromeRig.load(path)
    assert loaded.skeleton_hash == rig.skeleton_hash
    assert loaded.descriptors == rig.descriptors

    build = build_exact_rig_anm2(
        tmp_path / "object_animation.fbx",
        loaded,
        document_factory=_factory(names),
    )
    decoded = decode_samples(build.payload, [0.0, 2.0])
    assert decoded.track_count == len(names)
    assert decoded.frames[1].tracks[0][3] == pytest.approx(0.02, abs=1.0e-5)
    assert build.report["retarget_mode"] == "exact"
    assert build.report["decoded_max_component_error"] < 1.0e-4


def test_exact_rig_rejects_parent_mismatch(tmp_path: Path) -> None:
    rig = build_chrome_rig_from_fbx(
        tmp_path / "door.fbx",
        document_factory=_factory(("root", "bone_door", "bone_handle")),
    )
    broken = _ObjectFbx(tmp_path / "broken.fbx")
    broken.parent_by_name["bone_handle"] = "root"
    with pytest.raises(ValueError, match="parent mismatch"):
        build_exact_rig_anm2(
            tmp_path / "broken.fbx", rig, document_factory=lambda _path: broken
        )


def test_crig_rejects_unsafe_or_executable_members() -> None:
    output = io.BytesIO()
    with zipfile.ZipFile(output, "w") as archive:
        for name in (
            "manifest.json",
            "skeleton.json",
            "writer_profile.json",
            "validation.json",
        ):
            archive.writestr(name, b"{}")
        archive.writestr("payload.py", b"pass")
    with pytest.raises(ValueError, match="Executable content"):
        ChromeRig.from_bytes(output.getvalue())


def test_builtin_humanoid_is_represented_as_a_chrome_rig() -> None:
    root = Path(__file__).resolve().parents[1]
    rig = build_chrome_rig_from_smd_template(
        root / "reference/player_1_tpp.smd",
        root / "reference/infected_turn_90r.template.anm2",
    )
    assert rig.rig_id == "builtin:male_npc_infected"
    assert len(rig.bones) == 69
    assert len(rig.descriptors) == 70
    assert rig.descriptors[1] == 0xCCC3CDDF
    assert rig.validate().ok
    packaged = ChromeRig.load(root / "reference/male_npc_infected.crig")
    assert packaged.skeleton_hash == rig.skeleton_hash
    assert packaged.to_bytes(
        optional_members={
            "README.md": b"Bundled DL ReAnimated male NPC/infected target rig.\n"
        }
    ) == (root / "reference/male_npc_infected.crig").read_bytes()


def test_project_v2_migrates_to_crig_aware_schema_v3() -> None:
    project = DlReanimatedProject.from_dict(
        {
            "format": "dl-reanimated-project",
            "schema_version": 2,
            "minimum_reader_version": 1,
            "name": "Legacy humanoid",
            "rig": {"use_imported_animation_bind_pose": True},
        }
    )
    assert CURRENT_PROJECT_SCHEMA_VERSION == 3
    assert project.schema_version == 3
    assert project.rig.target_rig_ref == "builtin:male_npc_infected"
    assert project.rig.retarget_mode == "humanoid"
    assert "legacy_target_files" in project.rig.extensions


def test_project_builder_dispatches_exact_rig_without_humanoid_mapping(
    tmp_path: Path, monkeypatch: pytest.MonkeyPatch
) -> None:
    from dlanm2_gui import project_builder

    source = tmp_path / "door_animation.fbx"
    source.write_bytes(b"fixture")
    rig = build_chrome_rig_from_fbx(
        tmp_path / "door_model.fbx",
        document_factory=_factory(("root", "bone_door", "bone_handle")),
    )
    rig_path = rig.save(tmp_path / "door.crig")
    values = [rig.bind_track_values(), rig.bind_track_values()]
    payload = build_payload_from_values(
        rig.make_header(frame_count=2),
        rig.descriptors,
        values,
        [[False] * 9 for _ in rig.descriptors],
    )

    monkeypatch.setattr(
        project_builder,
        "_FbxDocument",
        lambda path: _ObjectFbx(path),
    )
    monkeypatch.setattr(
        project_builder,
        "build_exact_rig_anm2",
        lambda path, selected_rig: RetargetBuild(
            payload,
            2,
            {
                "retarget_mode": "exact",
                "frame_count": 2,
                "candidate_path": None,
            },
        ),
    )

    project = DlReanimatedProject.new("Door")
    project.rig.target_rig_ref = rig.rig_id
    project.rig.target_rig_path = str(rig_path)
    project.rig.retarget_mode = "exact"
    project.export.output_directory = str(tmp_path / "build")
    project.export.pack_filename = "door.rpack"
    project.export.include_validation_controls = True
    project.animations.append(ProjectAnimation.create(str(source), resource_name="open_door"))

    result = project_builder.build_project(project)
    library = extract_animation_library(Path(result.pack_path).read_bytes())
    assert "dl_reanimated_open_door" in library.animations
    assert result.built_animations[0].mapping_profile_id == ""
    assert any("not compatible with custom Chrome Rigs" in row for row in result.warnings)
