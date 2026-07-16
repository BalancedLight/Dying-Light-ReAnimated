from __future__ import annotations

from copy import deepcopy
import math
from pathlib import Path
from types import SimpleNamespace

import numpy as np
import pytest

from dlanm2_gui.anm2_components import decode_samples
from dlanm2_gui.bone_maps import (
    BONE_MAP_FORMAT,
    BONE_MAP_SCHEMA_VERSION,
    BoneMapPair,
    GenericBoneMap,
    skeleton_signature,
)
from dlanm2_gui.chrome_rig_builder import build_chrome_rig_from_fbx
from dlanm2_gui.fbx_core import FBX_TICKS_PER_SECOND
from dlanm2_gui.retarget_mapping import auto_map_crig_to_fbx
from dlanm2_gui.retarget_engines.mapped_rig import (
    build_mapped_rig_anm2,
    validate_hierarchy_safety,
)


def _rotation_z(degrees: float) -> np.ndarray:
    radians = math.radians(degrees)
    result = np.eye(4, dtype=float)
    result[0, 0] = result[1, 1] = math.cos(radians)
    result[0, 1] = -math.sin(radians)
    result[1, 0] = math.sin(radians)
    return result


class _PolicyFbx:
    def __init__(self, _path: Path, *, target: bool) -> None:
        prefix = "target" if target else "source"
        suffixes = ("root", "global", "component", "cross", "unmapped")
        self.names = tuple(f"{prefix}_{suffix}" for suffix in suffixes)
        self.limb_models = {
            name: index + 1 for index, name in enumerate(self.names)
        }
        self.parent_by_name = {
            name: (None if index == 0 else self.names[index - 1])
            for index, name in enumerate(self.names)
        }
        self.meters_per_unit = 0.01
        self.bind_global_matrices = self.global_matrices(
            tick=0, use_animation=False
        )

    def frame_count(self, *, fps: int) -> int:
        assert fps == 30
        return 3

    def _local_matrix(
        self, object_id: int, *, tick: int, use_animation: bool
    ) -> np.ndarray:
        frame = (
            int(round(tick * 30 / FBX_TICKS_PER_SECOND))
            if use_animation
            else 0
        )
        result = np.eye(4, dtype=float)
        if object_id == 1:
            return result

        result[1, 3] = 100.0
        if not frame:
            return result

        # Every source row contains all three animated components so the map's
        # per-row transfer/component choices, rather than the fixture, decide
        # what reaches the target track.
        rotations = (0.0, 0.0, 15.0, 25.0, 35.0, 45.0)
        translations = (0.0, 0.0, 10.0, 12.0, 14.0, 16.0)
        scales = (1.0, 1.0, 1.05, 1.10, 1.15, 1.20)
        result = _rotation_z(rotations[object_id] * frame)
        result[:3, :3] *= scales[object_id] ** frame
        result[1, 3] = 100.0 + translations[object_id] * frame
        return result

    def global_matrices(
        self, *, tick: int, use_animation: bool
    ) -> dict[str, np.ndarray]:
        result: dict[str, np.ndarray] = {}
        for name in self.names:
            local = self._local_matrix(
                self.limb_models[name], tick=tick, use_animation=use_animation
            )
            parent = self.parent_by_name[name]
            result[name] = result[parent] @ local if parent is not None else local
        return result


class _CollapsedAncestorFbx:
    """Two LimbNodes with a transformed non-Limb Model between them."""

    def __init__(self, _path: Path, *, target: bool) -> None:
        prefix = "target" if target else "source"
        self.names = (f"{prefix}_root", f"{prefix}_child")
        self.limb_models = {self.names[0]: 1, self.names[1]: 2}
        self.parent_by_name = {self.names[0]: None, self.names[1]: self.names[0]}
        self.meters_per_unit = 0.01
        take = SimpleNamespace(name="SyntheticTake")
        self.animation_stacks: tuple[object, ...] = (take,)
        self.selected_animation_stack = take
        self.curves: dict[object, object] = {}
        bind_child = np.eye(4, dtype=float)
        bind_child[1, 3] = 100.0
        self.bind_local_matrices = {
            self.names[0]: np.eye(4, dtype=float),
            self.names[1]: bind_child,
        }
        self.bind_global_matrices = self.global_matrices(
            tick=0,
            use_animation=False,
        )
        self.canonical_local_calls = 0

    def frame_ticks(self, *, fps: int) -> list[int]:
        return [0, int(round(FBX_TICKS_PER_SECOND / fps))]

    def _local_matrix(
        self, object_id: int, *, tick: int, use_animation: bool
    ) -> np.ndarray:
        # This is the immediate LimbNode local and deliberately omits the Null
        # ancestor. A solver using it would lose the child animation entirely.
        return self.bind_local_matrices[self.names[object_id - 1]].copy()

    def skeletal_local_matrices(
        self,
        *,
        tick: int,
        use_animation: bool,
        globals_by_name=None,
    ) -> dict[str, np.ndarray]:
        self.canonical_local_calls += 1
        child = self.bind_local_matrices[self.names[1]].copy()
        if use_animation and tick:
            animated = _rotation_z(45.0)
            animated[1, 3] = 100.0
            child = animated
        return {self.names[0]: np.eye(4, dtype=float), self.names[1]: child}

    def global_matrices(
        self, *, tick: int, use_animation: bool
    ) -> dict[str, np.ndarray]:
        child = self.bind_local_matrices[self.names[1]].copy()
        if use_animation and tick:
            animated = _rotation_z(45.0)
            animated[1, 3] = 100.0
            child = animated
        return {self.names[0]: np.eye(4, dtype=float), self.names[1]: child}

def _policy_fixture(tmp_path: Path):
    target_document = _PolicyFbx(tmp_path / "target.fbx", target=True)
    rig = build_chrome_rig_from_fbx(
        tmp_path / "target.fbx",
        document_factory=lambda _path: target_document,
    )
    source_document = _PolicyFbx(tmp_path / "source.fbx", target=False)
    profile = GenericBoneMap.create(
        "Per-row policies",
        rig.skeleton_hash,
        skeleton_signature(
            (name, source_document.parent_by_name[name])
            for name in sorted(source_document.names)
        ),
        source_rig_ref=rig.rig_id,
        origin="manually_reviewed",
    )
    target_by_suffix = {
        bone.name.removeprefix("target_"): bone for bone in rig.bones
    }
    profile.pairs = [
        BoneMapPair(
            target_by_suffix["root"].descriptor,
            "target_root",
            "source_root",
            transfer_policy="rotation_delta",
            component_policy="full_transform",
        ),
        BoneMapPair(
            target_by_suffix["global"].descriptor,
            "target_global",
            "source_global",
            transfer_policy="global_bind_basis",
            component_policy="full_transform",
        ),
        BoneMapPair(
            target_by_suffix["component"].descriptor,
            "target_component",
            "source_component",
            transfer_policy="rest_relative",
            component_policy="translation",
        ),
        BoneMapPair(
            target_by_suffix["cross"].descriptor,
            "target_cross",
            "source_cross",
            transfer_policy="rotation_delta",
            component_policy="full_transform",
        ),
        BoneMapPair(
            target_by_suffix["unmapped"].descriptor,
            "target_unmapped",
            "",
            transfer_policy="bind",
            component_policy="full_transform",
            review_state="intentionally_unmapped",
            notes="The source helper is deliberately ignored.",
        ),
    ]
    return rig, source_document, profile


def test_v1_map_migrates_to_explicit_v2_without_losing_meaning_or_unknowns(
    tmp_path: Path,
) -> None:
    profile = GenericBoneMap.from_dict(
        {
            "format": BONE_MAP_FORMAT,
            "schema_version": 1,
            "profile_id": "legacy-profile",
            "name": "Legacy reviewed map",
            "source_skeleton_hash": "target-full-bind",
            "target_skeleton_hash": "source-topology",
            "source_rig_ref": "legacy-rig",
            "future_profile_field": {"kept": True},
            "extensions": {"origin": "manually_reviewed", "vendor": "kept"},
            "pairs": [
                {
                    "source_descriptor": 0x12345678,
                    "source_bone": "TargetBone",
                    "target_bone": "SourceBone",
                    "confidence": 0.75,
                    "method": "manual:legacy",
                    "transfer_policy": "rotation_delta",
                    "component_policy": "rotation",
                    "mapping_kind": "bone",
                    "future_row_field": [1, 2, 3],
                }
            ],
        }
    )

    row = profile.pairs[0]
    assert profile.schema_version == BONE_MAP_SCHEMA_VERSION == 2
    assert profile.target_bind_hash == "target-full-bind"
    assert row.target_rig_descriptor == 0x12345678
    assert row.target_rig_bone == "TargetBone"
    assert row.source_fbx_bone == "SourceBone"
    assert row.review_state == "manually_reviewed"
    assert row.source_descriptor == row.target_rig_descriptor
    assert row.source_bone == row.target_rig_bone
    assert row.target_bone == row.source_fbx_bone
    assert profile.extensions["unknown_fields"]["future_profile_field"] == {
        "kept": True
    }
    assert row.extensions["unknown_fields"]["future_row_field"] == [1, 2, 3]

    serialized_row = profile.to_dict()["pairs"][0]
    assert serialized_row["target_rig_bone"] == "TargetBone"
    assert serialized_row["source_fbx_bone"] == "SourceBone"
    assert "source_bone" not in serialized_row
    assert "target_bone" not in serialized_row

    loaded = GenericBoneMap.load(profile.save(tmp_path / "migrated"))
    assert loaded.pairs[0].review_state == "manually_reviewed"
    assert loaded.extensions == profile.extensions


def test_base_rows_honor_transfer_and_component_policies_and_bind_unmapped(
    tmp_path: Path,
) -> None:
    rig, source_document, profile = _policy_fixture(tmp_path)
    build = build_mapped_rig_anm2(
        tmp_path / "source.fbx",
        rig,
        profile,
        document_factory=lambda _path: source_document,
        transfer_policy="mapped_local_rotation_delta",
        root_policy="inplace",
    )
    decoded = decode_samples(build.payload, [2.0]).frames[0].tracks
    bind = rig.bind_track_values()
    indices = {
        bone.name: rig.descriptors.index(bone.descriptor) for bone in rig.bones
    }

    global_row = decoded[indices["target_global"]]
    global_bind = bind[indices["target_global"]]
    assert global_row[0:3] != pytest.approx(global_bind[0:3], abs=1.0e-5)
    assert global_row[3:6] != pytest.approx(global_bind[3:6], abs=1.0e-5)
    assert global_row[6:9] != pytest.approx(global_bind[6:9], abs=1.0e-5)

    component_row = decoded[indices["target_component"]]
    component_bind = bind[indices["target_component"]]
    assert component_row[0:3] == pytest.approx(component_bind[0:3], abs=1.0e-5)
    assert component_row[3:6] != pytest.approx(component_bind[3:6], abs=1.0e-5)
    assert component_row[6:9] == pytest.approx(component_bind[6:9], abs=1.0e-5)

    cross_row = decoded[indices["target_cross"]]
    cross_bind = bind[indices["target_cross"]]
    assert cross_row[0:3] != pytest.approx(cross_bind[0:3], abs=1.0e-5)
    assert cross_row[3:6] == pytest.approx(cross_bind[3:6], abs=1.0e-5)
    assert cross_row[6:9] == pytest.approx(cross_bind[6:9], abs=1.0e-5)

    unmapped_index = indices["target_unmapped"]
    assert decoded[unmapped_index] == pytest.approx(bind[unmapped_index], abs=1.0e-5)
    assert build.report["base_transfer_policies"] == {
        "target_component": "rest_relative",
        "target_cross": "rotation_delta",
        "target_global": "global_bind_basis",
        "target_root": "rotation_delta",
        "target_unmapped": "bind",
    }
    assert build.report["base_component_policies"]["target_component"] == "translation"
    assert build.report["intentionally_unmapped_target_bones"] == [
        "target_unmapped"
    ]


def test_reviewed_spatial_map_builds_without_changing_target_lengths_translation_or_scale(
    tmp_path: Path,
) -> None:
    target_document = _PolicyFbx(tmp_path / "target.fbx", target=True)
    rig = build_chrome_rig_from_fbx(
        tmp_path / "target.fbx",
        document_factory=lambda _path: target_document,
    )
    source_document = _PolicyFbx(tmp_path / "source.fbx", target=False)
    profile = auto_map_crig_to_fbx(
        rig,
        source_document.names,
        source_document.parent_by_name,
        source_bind_globals=source_document.bind_global_matrices,
        source_deform_bones=source_document.names,
    )
    assert {
        row.target_rig_bone: row.source_fbx_bone for row in profile.pairs
    } == {
        target.name: source
        for target, source in zip(rig.bones, source_document.names)
    }
    with pytest.raises(ValueError, match="unreviewed automatic suggestion"):
        build_mapped_rig_anm2(
            tmp_path / "source.fbx",
            rig,
            profile,
            document_factory=lambda _path: source_document,
            root_policy="inplace",
        )
    for row in profile.pairs:
        row.method = "manual:reviewed_spatial_suggestion"
        row.review_state = "manually_reviewed"
        row.transfer_policy = "rotation_delta"
        row.component_policy = "rotation"
    profile.extensions["origin"] = "manually_reviewed"

    build = build_mapped_rig_anm2(
        tmp_path / "source.fbx",
        rig,
        profile,
        document_factory=lambda _path: source_document,
        root_policy="inplace",
    )
    decoded = decode_samples(build.payload, [0.0, 1.0, 2.0])
    bind = rig.bind_track_values()
    changed_rotation = False
    for frame in decoded.frames:
        for bone in rig.bones[1:]:
            track_index = rig.descriptors.index(bone.descriptor)
            row = frame.tracks[track_index]
            assert row[3:6] == pytest.approx(bind[track_index][3:6], abs=1.0e-5)
            assert row[6:9] == pytest.approx(bind[track_index][6:9], abs=1.0e-5)
            changed_rotation |= row[0:3] != pytest.approx(
                bind[track_index][0:3], abs=1.0e-5
            )
    assert changed_rotation
    safety = build.report["hierarchy_safety"]
    assert safety["maximum_non_root_translation_delta_meters"] == pytest.approx(0.0)
    assert safety["maximum_parent_child_length_ratio"] == pytest.approx(1.0)
    assert safety["minimum_parent_child_length_ratio"] == pytest.approx(1.0)


def test_stale_target_full_bind_hash_fails_before_build(tmp_path: Path) -> None:
    rig, source_document, profile = _policy_fixture(tmp_path)
    profile.target_bind_hash = "stale-full-bind"

    with pytest.raises(ValueError, match="different target full bind"):
        build_mapped_rig_anm2(
            tmp_path / "source.fbx",
            rig,
            profile,
            document_factory=lambda _path: source_document,
            root_policy="inplace",
        )


def test_reviewed_profile_requires_an_explicit_row_for_every_deform_target(
    tmp_path: Path,
) -> None:
    rig, source_document, profile = _policy_fixture(tmp_path)
    missing = next(
        bone for bone in rig.bones if bone.deform and bone.name == "target_cross"
    )
    profile.pairs = [
        row for row in profile.pairs if row.target_rig_bone != missing.name
    ]

    with pytest.raises(ValueError, match="Required target deform bones.*target_cross"):
        build_mapped_rig_anm2(
            tmp_path / "source.fbx",
            rig,
            profile,
            document_factory=lambda _path: source_document,
            root_policy="inplace",
        )


def test_mapped_rotation_uses_collapsed_non_limb_ancestor_local(
    tmp_path: Path,
) -> None:
    target_document = _CollapsedAncestorFbx(tmp_path / "target.fbx", target=True)
    rig = build_chrome_rig_from_fbx(
        tmp_path / "target.fbx",
        document_factory=lambda _path: target_document,
    )
    source_document = _CollapsedAncestorFbx(tmp_path / "source.fbx", target=False)
    profile = GenericBoneMap.create(
        "Collapsed ancestor",
        rig.skeleton_hash,
        skeleton_signature(
            (name, source_document.parent_by_name[name])
            for name in source_document.names
        ),
        source_rig_ref=rig.rig_id,
        origin="manually_reviewed",
    )
    profile.pairs = [
        BoneMapPair(
            target.descriptor,
            target.name,
            source,
            transfer_policy="rotation_delta",
            component_policy="rotation",
        )
        for target, source in zip(rig.bones, source_document.names)
    ]

    build = build_mapped_rig_anm2(
        tmp_path / "source.fbx",
        rig,
        profile,
        document_factory=lambda _path: source_document,
        root_policy="inplace",
    )
    decoded = decode_samples(build.payload, [1.0]).frames[0].tracks
    child = rig.bones[1]
    child_track = rig.descriptors.index(child.descriptor)

    assert source_document.canonical_local_calls >= 1
    assert np.linalg.norm(decoded[child_track][0:3]) > 0.1
    assert decoded[child_track][3:6] == pytest.approx(
        rig.bind_track_values()[child_track][3:6],
        abs=1.0e-5,
    )


def test_translation_owned_length_changes_remain_model_relative(tmp_path: Path) -> None:
    rig, _source_document, _profile = _policy_fixture(tmp_path)
    bone = next(row for row in rig.bones if row.name == "target_component")
    track = rig.descriptors.index(bone.descriptor)

    translated = [deepcopy(rig.bind_track_values())]
    translated[0][track][3] += 0.25
    with pytest.raises(ValueError, match="failed safety validation"):
        validate_hierarchy_safety(
            rig,
            translated,
            preserve_non_root_translations=True,
        )
    report = validate_hierarchy_safety(
        rig,
        translated,
        preserve_non_root_translations=True,
        allowed_non_root_translation_bones={bone.name},
    )
    assert report["maximum_non_root_translation_delta_meters"] == pytest.approx(0.0)

    catastrophic = [deepcopy(rig.bind_track_values())]
    catastrophic[0][track][3] += 10.0
    with pytest.raises(ValueError, match="failed safety validation"):
        validate_hierarchy_safety(
            rig,
            catastrophic,
            preserve_non_root_translations=True,
            allowed_non_root_translation_bones={bone.name},
        )
