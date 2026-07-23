from __future__ import annotations

import math
from pathlib import Path

import numpy as np
import pytest

from dlanm2_gui.anm2_components import decode_samples
from dlanm2_gui.bone_maps import BoneMapPair, GenericBoneMap, skeleton_signature
from dlanm2_gui.chrome_rig import ChromeRig, ChromeRigBone
from dlanm2_gui.fbx_core import FBX_TICKS_PER_SECOND
from dlanm2_gui.retarget_engines.exact_rig import build_exact_rig_anm2
from dlanm2_gui.retarget_engines.mapped_rig import build_mapped_rig_anm2
from dlanm2_gui.tools.compare_export_behaviors import compare_anm2_artifacts
from dlanm2_gui.trackmap import dl_name_hash
from dlanm2_gui.workspace_project import DlReanimatedProject


def _rotation_z(degrees: float) -> np.ndarray:
    angle = math.radians(degrees)
    matrix = np.eye(4, dtype=float)
    matrix[0, 0] = matrix[1, 1] = math.cos(angle)
    matrix[0, 1] = -math.sin(angle)
    matrix[1, 0] = math.sin(angle)
    return matrix


class _DirectCompatibleDocument:
    def __init__(self, _path: Path) -> None:
        self.limb_models = {"root": 1, "child": 2, "source_extra": 3}
        self.null_models = {}
        self.parent_by_name = {
            "root": None,
            "child": "root",
            "source_extra": "child",
        }
        self.meters_per_unit = 0.01
        self.animation_stacks = ()
        self.selected_animation_stack = None
        self.extensions = {}
        self.bind_local_matrices = {
            name: self._local_matrix(object_id, tick=0, use_animation=False)
            for name, object_id in self.limb_models.items()
        }
        self.bind_global_matrices = self.global_matrices(
            tick=0, use_animation=False
        )

    def frame_count(self, *, fps: float) -> int:
        return 3

    def frame_ticks(self, fps: float) -> list[int]:
        return [
            int(round(frame * FBX_TICKS_PER_SECOND / float(fps)))
            for frame in range(3)
        ]

    def _local_matrix(
        self, object_id: int, *, tick: int, use_animation: bool
    ) -> np.ndarray:
        frame = (
            int(round(tick * 30.0 / FBX_TICKS_PER_SECOND))
            if use_animation
            else 0
        )
        if object_id == 1:
            result = _rotation_z(30.0 + frame * 20.0)
            result[0, 3] = frame * 10.0
            return result
        if object_id == 2:
            result = _rotation_z(frame * 15.0)
            result[1, 3] = 100.0
            return result
        result = np.eye(4, dtype=float)
        result[2, 3] = 25.0
        return result

    def global_matrices(
        self, *, tick: int, use_animation: bool
    ) -> dict[str, np.ndarray]:
        root = self._local_matrix(1, tick=tick, use_animation=use_animation)
        child = self._local_matrix(2, tick=tick, use_animation=use_animation)
        extra = self._local_matrix(3, tick=tick, use_animation=use_animation)
        return {
            "root": root,
            "child": root @ child,
            "source_extra": root @ child @ extra,
        }


def _target_rig() -> ChromeRig:
    return ChromeRig(
        "test:legacy-5-direct",
        "Legacy 5 direct target",
        "Object",
        (
            ChromeRigBone(
                0,
                "root",
                -1,
                dl_name_hash("root"),
                (0.0, 0.0, 0.0),
                (1.0, 0.0, 0.0, 0.0),
                (1.0, 1.0, 1.0),
            ),
            ChromeRigBone(
                1,
                "child",
                0,
                dl_name_hash("child"),
                (0.0, 1.0, 0.0),
                (1.0, 0.0, 0.0, 0.0),
                (1.0, 1.0, 1.0),
            ),
            ChromeRigBone(
                2,
                "optional_helper",
                1,
                dl_name_hash("optional_helper"),
                (0.0, 0.2, 0.0),
                (1.0, 0.0, 0.0, 0.0),
                (1.0, 1.0, 1.0),
                deform=False,
                helper=True,
            ),
        ),
        0,
    )


def test_legacy_global_bind_basis_retains_target_scale_and_optional_bind(
    tmp_path: Path,
) -> None:
    source = tmp_path / "direct_compatible.fbx"
    document = _DirectCompatibleDocument(source)
    rig = _target_rig()
    factory = lambda _path: document

    legacy = build_exact_rig_anm2(
        source,
        rig,
        fps=30.0,
        document_factory=factory,
        fbx_anm2_export_behavior="legacy_5_0",
    )

    legacy_rows = decode_samples(legacy.payload, [0.0, 1.0, 2.0])
    root_index = rig.descriptors.index(dl_name_hash("root"))
    child_index = rig.descriptors.index(dl_name_hash("child"))
    helper_index = rig.descriptors.index(dl_name_hash("optional_helper"))
    assert legacy_rows.frames[0].tracks[root_index][:3] == pytest.approx(
        (0.0, 0.0, 0.0),
        abs=1.0e-5,
    )
    assert legacy_rows.frames[2].tracks[root_index][:3] == pytest.approx(
        (0.0, 0.0, math.tan(math.radians(40.0) / 4.0)),
        abs=1.0e-5,
    )
    assert legacy_rows.frames[2].tracks[root_index][3:6] == pytest.approx(
        (0.0, 0.0, 0.0),
        abs=1.0e-6,
    )
    # Source centimeters are used only for source-global correction. Target
    # joint geometry always stays at the CRIG's metre-scale bind translation.
    assert legacy_rows.frames[2].tracks[child_index][3:6] == pytest.approx(
        (0.0, 1.0, 0.0),
        abs=1.0e-6,
    )
    assert legacy_rows.frames[2].tracks[helper_index] == pytest.approx(
        rig.bind_track_values()[helper_index], abs=1.0e-6
    )
    assert (
        legacy.report["sampler_contract"]
        == "dlr_0_5_0_global_bind_basis_v1"
    )
    assert legacy.report["effective_post_wrapper_translation_scale"] == pytest.approx(
        0.01
    )
    assert legacy.report["bind_retained_bones"] == ["optional_helper"]
    assert legacy.report["source_extra_bones_ignored"] == ["source_extra"]
    assert legacy.report["modern_transform_repairs_applied"] is False
    assert legacy.report["engine"] == "Legacy50GlobalBindRetargetEngine"


def test_mapped_engine_rejects_legacy_behavior(tmp_path: Path) -> None:
    rig = _target_rig()
    document = _DirectCompatibleDocument(tmp_path / "cross_rig.fbx")
    mapping = GenericBoneMap.create(
        "Semantic map",
        rig.skeleton_hash,
        skeleton_signature(
            (name, document.parent_by_name.get(name))
            for name in sorted(document.limb_models)
        ),
        source_rig_ref=rig.rig_id,
    )
    mapping.pairs = [
        BoneMapPair(
            rig.bones[0].descriptor,
            "root",
            "root",
            method="manual",
        )
    ]

    with pytest.raises(ValueError, match="cannot be applied through a mapped"):
        build_mapped_rig_anm2(
            tmp_path / "cross_rig.fbx",
            rig,
            mapping,
            document=document,
            fbx_anm2_export_behavior="legacy_5_0",
        )


def test_project_roundtrips_fbx_anm2_export_behavior() -> None:
    project = DlReanimatedProject.new("Legacy behavior")
    project.rig.fbx_anm2_export_behavior = "legacy_5_0"

    loaded = DlReanimatedProject.from_dict(project.to_dict())

    assert loaded.rig.fbx_anm2_export_behavior == "legacy_5_0"
    loaded.rig.fbx_anm2_export_behavior = "invalid"
    assert any(
        "FBX-to-ANM2 export behavior" in error
        for error in loaded.validate()
    )


def test_project_bilateral_semantics_default_and_legacy_migration() -> None:
    new_project = DlReanimatedProject.new("Bilateral Auto")
    assert new_project.rig.bilateral_semantic_policy == "auto"
    new_project.rig.bilateral_semantic_policy = "swap_bilateral_explicit"
    reloaded = DlReanimatedProject.from_dict(new_project.to_dict())
    assert reloaded.rig.bilateral_semantic_policy == "swap_bilateral_explicit"

    payload = new_project.to_dict()
    payload["rig"].pop("bilateral_semantic_policy")
    migrated = DlReanimatedProject.from_dict(payload)
    assert migrated.rig.bilateral_semantic_policy == "preserve_source_names"
    assert migrated.rig.extensions["bilateral_semantic_policy_migration"][
        "to"
    ] == "preserve_source_names"

    migrated.rig.bilateral_semantic_policy = "invalid"
    assert any(
        "Bilateral semantic policy" in error for error in migrated.validate()
    )


def test_export_comparison_samples_first_middle_and_final_frames(
    tmp_path: Path,
) -> None:
    document = _DirectCompatibleDocument(tmp_path / "fixture.fbx")
    rig = _target_rig()
    current = build_exact_rig_anm2(
        tmp_path / "fixture.fbx",
        rig,
        fbx_anm2_export_behavior="current",
        document=document,
    )
    legacy = build_exact_rig_anm2(
        tmp_path / "fixture.fbx",
        rig,
        fbx_anm2_export_behavior="legacy_5_0",
        document=document,
    )
    current_path = tmp_path / "current.anm2"
    legacy_path = tmp_path / "legacy.anm2"
    current_path.write_bytes(current.payload)
    legacy_path.write_bytes(legacy.payload)

    report = compare_anm2_artifacts(current_path, legacy_path)

    assert report["artifacts_differ"]
    assert [row["frame"] for row in report["sampled_differences"]] == [0, 1, 2]
    assert any(
        row["differing_track_count"] > 0
        for row in report["sampled_differences"]
    )
