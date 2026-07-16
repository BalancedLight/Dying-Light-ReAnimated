from __future__ import annotations

import io
import math
from pathlib import Path
from types import SimpleNamespace
import zipfile

import numpy as np
import pytest

from dlanm2_gui.anm2_components import decode_samples
from dlanm2_gui.anm2_writer import build_payload_from_values
from dlanm2_gui.bone_maps import BoneMapPair, GenericBoneMap, skeleton_signature
from dlanm2_gui.chrome_rig import ChromeRig
from dlanm2_gui.chrome_rig_builder import (
    build_chrome_rig_from_fbx,
    build_chrome_rig_from_smd_template,
)
from dlanm2_gui.oracle.binary_fbx_mixamo import FBX_TICKS_PER_SECOND
from dlanm2_gui.retarget_engines.exact_rig import build_exact_rig_anm2
from dlanm2_gui.retarget_engines.mapped_rig import build_mapped_rig_anm2
from dlanm2_gui.retarget_engines.base import RetargetBuild
from dlanm2_gui.retarget_mapping import auto_map_crig_to_fbx
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


def test_exact_engine_reuses_one_canonical_document_across_legacy_adapter(
    tmp_path: Path,
) -> None:
    names = ("root", "bone_door")
    rig = build_chrome_rig_from_fbx(
        tmp_path / "object.fbx",
        document_factory=_factory(names),
    )
    created: list[_ObjectFbx] = []

    def counting_factory(path: Path) -> _ObjectFbx:
        document = _ObjectFbx(path, names)
        created.append(document)
        return document

    build_exact_rig_anm2(
        tmp_path / "animation.fbx",
        rig,
        document_factory=counting_factory,
    )

    assert len(created) == 1


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


def test_crig_marks_only_skinned_bones_and_their_ancestors_as_required(
    tmp_path: Path,
) -> None:
    document = _ObjectFbx(tmp_path / "model.fbx", ("root", "deform", "end_marker"))
    parents = {1: None, 2: 1, 3: 2}
    document.scene = SimpleNamespace(
        geometries=(
            SimpleNamespace(clusters=(SimpleNamespace(bone_id=2),)),
        ),
        model_parent_id=lambda object_id: parents[object_id],
    )

    rig = build_chrome_rig_from_fbx(
        tmp_path / "model.fbx", document_factory=lambda _path: document
    )

    by_name = {bone.name: bone for bone in rig.bones}
    assert by_name["root"].deform and not by_name["root"].helper
    assert by_name["deform"].deform and not by_name["deform"].helper
    assert not by_name["end_marker"].deform and by_name["end_marker"].helper
    assert rig.extensions["deform_classification"] == "skin_cluster_ancestry"


def test_exact_rig_default_pose_mismatch_warns_and_exports(tmp_path: Path) -> None:
    rig = build_chrome_rig_from_fbx(
        tmp_path / "door.fbx",
        document_factory=_factory(("root", "bone_door")),
    )
    animation = _ObjectFbx(tmp_path / "animation.fbx", ("root", "bone_door"))
    original_local_matrix = animation._local_matrix

    def mismatched_local_matrix(object_id: int, *, tick: int, use_animation: bool) -> np.ndarray:
        if object_id == 2 and not use_animation:
            matrix = _rotation_z(12.0)
            matrix[1, 3] = 10.0
            return matrix
        return original_local_matrix(object_id, tick=tick, use_animation=use_animation)

    animation._local_matrix = mismatched_local_matrix  # type: ignore[method-assign]
    build = build_exact_rig_anm2(
        tmp_path / "animation.fbx",
        rig,
        document_factory=lambda _path: animation,
    )
    compatibility = build.report["bind_compatibility"]
    assert compatibility["status"] == "warning"
    assert compatibility["default_pose_mismatch_count"] == 1
    assert compatibility["default_pose_mismatches"][0]["bone"] == "bone_door"
    assert build.report["warnings"]


def test_exact_rig_roundtrips_synthetic_motion_helper(tmp_path: Path) -> None:
    rig = build_chrome_rig_from_fbx(
        tmp_path / "door.fbx",
        document_factory=_factory(("root", "bone_door")),
    )
    rig.extra_track_descriptors = (0xCCC3CDDF,)
    rig.track_descriptors = tuple([bone.descriptor for bone in rig.bones] + [0xCCC3CDDF])
    exported = _ObjectFbx(
        tmp_path / "exported.fbx",
        ("DLR_OffsetHelper_CCC3CDDF", "root", "bone_door"),
    )
    build = build_exact_rig_anm2(
        tmp_path / "exported.fbx", rig, document_factory=lambda _path: exported
    )
    decoded = decode_samples(build.payload, [2.0])
    helper_index = rig.descriptors.index(0xCCC3CDDF)
    assert decoded.frames[0].tracks[helper_index][3] == pytest.approx(0.02, abs=1e-4)


def test_rotation_delta_manual_root_mapping_keeps_root_displacement(
    tmp_path: Path,
) -> None:
    names = ("root", "bone_door")
    rig = build_chrome_rig_from_fbx(
        tmp_path / "door.fbx",
        document_factory=_factory(names),
    )
    document = _ObjectFbx(tmp_path / "animated.fbx", names)
    bone_map = GenericBoneMap.create(
        "Manual same-name map",
        rig.skeleton_hash,
        skeleton_signature(
            (name, document.parent_by_name.get(name))
            for name in sorted(document.limb_models)
        ),
        source_rig_ref=rig.rig_id,
    )
    bone_map.pairs = [
        BoneMapPair(bone.descriptor, bone.name, bone.name, 1.0, "manual")
        for bone in rig.bones
    ]

    build = build_mapped_rig_anm2(
        tmp_path / "animated.fbx",
        rig,
        bone_map,
        document_factory=lambda _path: document,
        transfer_policy="mapped_local_rotation_delta",
        root_policy="bip01",
    )
    decoded = decode_samples(build.payload, [0.0, 2.0])
    root_index = rig.descriptors.index(rig.bones[0].descriptor)

    assert decoded.frames[0].tracks[root_index][3] == pytest.approx(0.0, abs=1e-5)
    assert decoded.frames[1].tracks[root_index][3] == pytest.approx(0.02, abs=1e-5)


def test_mapped_rig_helper_fanout_is_applied_after_base_solver(tmp_path: Path) -> None:
    names = ("root", "camera_helper")
    rig = build_chrome_rig_from_fbx(
        tmp_path / "target.fbx", document_factory=_factory(names)
    )
    document = _ObjectFbx(tmp_path / "animated.fbx", names)
    bone_map = GenericBoneMap.create(
        "Helper fanout",
        rig.skeleton_hash,
        skeleton_signature(
            (name, document.parent_by_name.get(name))
            for name in sorted(document.limb_models)
        ),
        source_rig_ref=rig.rig_id,
    )
    bone_map.pairs = [
        BoneMapPair(rig.bones[0].descriptor, "root", "root"),
        BoneMapPair(
            rig.bones[1].descriptor,
            "camera_helper",
            "root",
            mapping_kind="helper_override",
            transfer_policy="rest_relative",
            component_policy="translation",
        ),
    ]

    build = build_mapped_rig_anm2(
        tmp_path / "animated.fbx",
        rig,
        bone_map,
        document_factory=lambda _path: document,
        transfer_policy="mapped_local_rotation_delta",
        root_policy="bip01",
    )
    decoded = decode_samples(build.payload, [0.0, 2.0])
    helper_index = rig.descriptors.index(rig.bones[1].descriptor)

    assert decoded.frames[1].tracks[helper_index][3] == pytest.approx(0.02, abs=1e-5)
    assert build.report["base_mapped_bone_count"] == 1
    assert build.report["helper_override_count"] == 1
    assert build.report["main_transfer_policy"] == "mapped_local_rotation_delta"


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
    assert packaged.to_bytes() == (root / "reference/male_npc_infected.crig").read_bytes()


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
    assert CURRENT_PROJECT_SCHEMA_VERSION >= 5
    assert project.schema_version == CURRENT_PROJECT_SCHEMA_VERSION
    assert project.rig.target_rig_ref == "builtin:male_npc_infected"
    assert project.rig.retarget_mode == "humanoid"
    assert "legacy_target_files" in project.rig.extensions


def test_project_builder_keeps_automatic_identity_superset_on_exact_engine(
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

    document = _ObjectFbx(
        source, ("root", "bone_door", "bone_handle", "source_extra")
    )
    monkeypatch.setattr(project_builder, "_FbxDocument", lambda _path: document)
    monkeypatch.setattr(
        project_builder,
        "build_exact_rig_anm2",
        lambda path, selected_rig, **_kwargs: RetargetBuild(
            payload,
            2,
            {
                "retarget_mode": "exact",
                "frame_count": 2,
                "candidate_path": None,
            },
        ),
    )
    monkeypatch.setattr(
        project_builder,
        "build_mapped_rig_anm2",
        lambda *_args, **_kwargs: pytest.fail(
            "an automatic identity map must not change the exact solver"
        ),
    )

    project = DlReanimatedProject.new("Door")
    project.rig.target_rig_ref = rig.rig_id
    project.rig.target_rig_path = str(rig_path)
    project.rig.retarget_mode = "exact"
    project.export.output_directory = str(tmp_path / "build")
    project.export.pack_filename = "door.rpack"
    project.export.include_validation_controls = True
    profile = auto_map_crig_to_fbx(
        rig, document.limb_models.keys(), document.parent_by_name
    )
    assert profile.extensions["origin"] == "automatic_identity"
    animation = ProjectAnimation.create(str(source), resource_name="open_door")
    animation.mapping_profile_id = profile.profile_id
    project.animations.append(animation)
    project.mapping_profiles[profile.profile_id] = profile.to_dict()

    result = project_builder.build_project(project)
    library = extract_animation_library(Path(result.pack_path).read_bytes())
    assert "dl_reanimated_open_door" in library.animations
    assert result.built_animations[0].mapping_profile_id == profile.profile_id
    report = __import__("json").loads(
        Path(result.built_animations[0].retarget_report).read_text(encoding="utf-8")
    )
    assert report["solver_selection"]["selected_engine"] == "ExactRigRetargetEngine"
    assert report["solver_selection"]["mapping_profile_changed_solver"] is False
    assert any("not compatible with custom Chrome Rigs" in row for row in result.warnings)


def test_project_builder_uses_reviewed_crig_map_instead_of_strict_exact_engine(
    tmp_path: Path, monkeypatch: pytest.MonkeyPatch
) -> None:
    from dlanm2_gui import project_builder

    source = tmp_path / "different_skeleton.fbx"
    source.write_bytes(b"fixture")
    rig = build_chrome_rig_from_fbx(
        tmp_path / "door_model.fbx",
        document_factory=_factory(("root", "bone_door")),
    )
    rig_path = rig.save(tmp_path / "door.crig")
    values = [rig.bind_track_values(), rig.bind_track_values()]
    payload = build_payload_from_values(
        rig.make_header(frame_count=2),
        rig.descriptors,
        values,
        [[False] * 9 for _ in rig.descriptors],
    )
    document = _ObjectFbx(source, ("source_root", "source_hinge"))
    monkeypatch.setattr(project_builder, "_FbxDocument", lambda _path: document)

    called: dict[str, object] = {}

    def mapped_builder(path, selected_rig, bone_map, **kwargs):
        called.update(path=path, rig=selected_rig, map=bone_map, kwargs=kwargs)
        return RetargetBuild(
            payload,
            2,
            {
                "retarget_mode": "mapped_crig",
                "frame_count": 2,
                "candidate_path": None,
            },
        )

    monkeypatch.setattr(project_builder, "build_mapped_rig_anm2", mapped_builder)
    monkeypatch.setattr(
        project_builder,
        "build_exact_rig_anm2",
        lambda *_args, **_kwargs: pytest.fail("strict exact engine should not be used"),
    )

    profile = GenericBoneMap.create(
        "Reviewed door map",
        rig.skeleton_hash,
        skeleton_signature(
            (name, document.parent_by_name.get(name))
            for name in sorted(document.limb_models)
        ),
        source_rig_ref=rig.rig_id,
        origin="manually_reviewed",
    )
    profile.pairs = [
        BoneMapPair(rig.bones[0].descriptor, "root", "source_root"),
        BoneMapPair(rig.bones[1].descriptor, "bone_door", "source_hinge"),
    ]

    project = DlReanimatedProject.new("Mapped door")
    project.rig.target_rig_ref = rig.rig_id
    project.rig.target_rig_path = str(rig_path)
    project.rig.retarget_mode = "exact"
    project.export.output_directory = str(tmp_path / "build")
    project.export.pack_filename = "door.rpack"
    animation = ProjectAnimation.create(str(source), resource_name="open_door")
    animation.mapping_profile_id = profile.profile_id
    project.animations.append(animation)
    project.mapping_profiles[profile.profile_id] = profile.to_dict()

    result = project_builder.build_project(project)

    assert isinstance(called["map"], GenericBoneMap)
    assert called["kwargs"]["transfer_policy"] == "mapped_local_rotation_delta"
    assert result.built_animations[0].mapping_profile_id == profile.profile_id
    report = __import__("json").loads(
        Path(result.built_animations[0].retarget_report).read_text(encoding="utf-8")
    )
    assert report["solver_selection"]["selected_engine"] == "MappedRigRetargetEngine"
    assert report["solver_selection"]["mapping_profile_origin"] == "manually_reviewed"


def test_reviewed_mapping_identity_rejects_cross_wired_pairs() -> None:
    from dlanm2_gui.project_builder import _reviewed_mapping_is_name_identity

    profile = GenericBoneMap.create("Identity check", "target", "source")
    profile.pairs = [
        BoneMapPair(1, "root", "root"),
        BoneMapPair(2, "left_arm", "left_arm"),
    ]
    assert _reviewed_mapping_is_name_identity(profile)

    profile.pairs[1] = BoneMapPair(2, "left_arm", "right_arm")
    assert not _reviewed_mapping_is_name_identity(profile)
