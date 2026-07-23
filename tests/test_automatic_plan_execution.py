from __future__ import annotations

from copy import deepcopy
import math
from pathlib import Path
from types import SimpleNamespace

import numpy as np
import pytest

from dlanm2_gui.anm2_components import decode_samples
from dlanm2_gui.automatic_retarget import (
    build_automatic_retarget_plan,
    materialize_automatic_retarget_plan,
    validate_automatic_retarget_plan,
)
from dlanm2_gui.bone_maps import set_mapping_profile_origin
from dlanm2_gui.chrome_rig import ChromeRig, ChromeRigBone
from dlanm2_gui.fbx_core import FBX_TICKS_PER_SECOND
from dlanm2_gui.retarget_engines.mapped_rig import (
    build_mapped_rig_anm2,
    mapped_local_from_composed_rotation_deltas,
    mapped_local_from_distributed_rotation_delta,
    reconstruct_target_globals,
)
from dlanm2_gui.trackmap import dl_name_hash


def _rotation_z(degrees: float) -> np.ndarray:
    radians = math.radians(degrees)
    result = np.eye(4, dtype=float)
    result[0, 0] = result[1, 1] = math.cos(radians)
    result[0, 1] = -math.sin(radians)
    result[1, 0] = math.sin(radians)
    return result


def _rotation_degrees(matrix: np.ndarray) -> float:
    return math.degrees(math.atan2(float(matrix[1, 0]), float(matrix[0, 0])))


class _SemanticExecutionDocument:
    def __init__(self) -> None:
        self.names = ("source_root", "compose_a", "compose_b", "distributed")
        self.limb_models = {name: index + 1 for index, name in enumerate(self.names)}
        self.parent_by_name = {
            "source_root": None,
            "compose_a": "source_root",
            "compose_b": "compose_a",
            "distributed": "source_root",
        }
        self.meters_per_unit = 1.0
        take = SimpleNamespace(name="SyntheticTake")
        self.animation_stacks = (take,)
        self.selected_animation_stack = take
        self.curves: dict[object, object] = {}
        self.bind_local_matrices = {
            name: self._matrix(name, animated=False) for name in self.names
        }
        self.bind_global_matrices = self.global_matrices(
            tick=0, use_animation=False
        )
        self.bind_source = "synthetic authoritative bind"
        self.bind_coverage = {"authoritative": 4, "total": 4}

    def frame_ticks(self, *, fps: int) -> list[int]:
        return [0, int(round(FBX_TICKS_PER_SECOND / fps))]

    def frame_count(self, *, fps: int) -> int:
        return 2

    def _matrix(self, name: str, *, animated: bool) -> np.ndarray:
        angle = (
            {"compose_a": 20.0, "compose_b": 30.0, "distributed": 60.0}.get(
                name, 0.0
            )
            if animated
            else 0.0
        )
        result = _rotation_z(angle)
        if name in {"compose_a", "compose_b"}:
            result[1, 3] = 1.0
        elif name == "distributed":
            result[0, 3] = 1.0
        return result

    def _local_matrix(
        self, object_id: int, *, tick: int, use_animation: bool
    ) -> np.ndarray:
        name = self.names[object_id - 1]
        return self._matrix(name, animated=bool(tick and use_animation))

    def skeletal_local_matrices(
        self,
        *,
        tick: int,
        use_animation: bool,
        globals_by_name=None,
    ) -> dict[str, np.ndarray]:
        return {
            name: self._matrix(name, animated=bool(tick and use_animation))
            for name in self.names
        }

    def global_matrices(
        self, *, tick: int, use_animation: bool
    ) -> dict[str, np.ndarray]:
        result: dict[str, np.ndarray] = {}
        for name in self.names:
            local = self._matrix(name, animated=bool(tick and use_animation))
            parent = self.parent_by_name[name]
            result[name] = result[parent] @ local if parent else local
        return result


def _target_rig() -> ChromeRig:
    specs = (
        ("target_root", -1, (0.0, 0.0, 0.0)),
        ("target_composed", 0, (0.0, 1.0, 0.0)),
        ("target_distributed_a", 0, (1.0, 0.0, 0.0)),
        ("target_distributed_b", 2, (1.0, 0.0, 0.0)),
        ("target_inherited", 1, (0.0, 1.0, 0.0)),
    )
    bones = tuple(
        ChromeRigBone(
            index=index,
            name=name,
            parent_index=parent,
            descriptor=dl_name_hash(name),
            bind_translation=translation,
            bind_rotation_wxyz=(1.0, 0.0, 0.0, 0.0),
            tags=("body",),
        )
        for index, (name, parent, translation) in enumerate(specs)
    )
    return ChromeRig("test:semantic-execution", "Semantic execution", "generic", bones, 0)


def _analysis_and_policy(rig: ChromeRig):
    nodes = (
        SimpleNamespace(name="source_root", parent_name=None, side_conflict=False),
        SimpleNamespace(
            name="compose_a", parent_name="source_root", side_conflict=False
        ),
        SimpleNamespace(
            name="compose_b", parent_name="compose_a", side_conflict=False
        ),
        SimpleNamespace(
            name="distributed", parent_name="source_root", side_conflict=False
        ),
    )
    candidate = SimpleNamespace(
        bone_name="source_root",
        confidence=1.0,
        confidence_margin=1.0,
        side="",
        evidence=("semantic_role", "topology"),
    )
    analysis = SimpleNamespace(
        skeleton_hash="semantic-source-v1",
        bind_hash="semantic-bind-v1",
        nodes=nodes,
        semantic_roles={"root": candidate},
        semantic_chains={
            "compose": SimpleNamespace(bone_names=("compose_a", "compose_b")),
            "distribution": SimpleNamespace(bone_names=("distributed",)),
        },
        animated_bones=frozenset(
            {"source_root", "compose_a", "compose_b", "distributed"}
        ),
        animated_components={},
        archetype="generic",
        archetype_confidence=1.0,
        animation_domain="mostly_static_pose",
        analyzer_version="execution-test-analyzer-v1",
        lexicon_version="execution-test-lexicon-v1",
        findings=(),
    )
    roles = {
        "target_root": "root",
        "target_inherited": "left_foot",
    }
    rows = tuple(
        SimpleNamespace(
            target_bone=bone.name,
            target_category="body",
            semantic_role=roles.get(bone.name, ""),
            helper=False,
        )
        for bone in rig.bones
    )
    policy = SimpleNamespace(
        policy_id="execution-test-policy-v1",
        policy_version="execution-test-policy-version-v1",
        target_archetype="generic",
        minimum_confidence=0.7,
        minimum_confidence_margin=0.08,
        bones=rows,
        semantic_chains={
            "compose": SimpleNamespace(
                target_bones=("target_composed",),
                force_chain_alignment=True,
                long_source_policy="composed",
            ),
            "distribution": SimpleNamespace(
                target_bones=("target_distributed_a", "target_distributed_b"),
                force_chain_alignment=True,
                short_source_policy="distributed",
            ),
        },
    )
    return analysis, policy


def _reviewed_executable_profile(rig: ChromeRig):
    analysis, policy = _analysis_and_policy(rig)
    plan = build_automatic_retarget_plan(analysis, rig, policy)
    validation = validate_automatic_retarget_plan(plan, analysis, rig, policy)
    assert validation.ok, validation.errors
    assert plan.mapping_modes["composed"] == 1
    assert plan.mapping_modes["distributed"] == 1
    assert plan.mapping_modes["direct"] == 2
    assert plan.mapping_modes["inherit_bind"] == 1
    profile = materialize_automatic_retarget_plan(plan, analysis, rig, policy)
    assert profile.extensions["origin"] == "automatic_repair"
    assert {
        row.review_state for row in profile.pairs if row.source_fbx_bone
    } == {"automatic_unreviewed"}
    set_mapping_profile_origin(profile, "manually_reviewed")
    return profile


def test_composed_and_fractional_rotation_helpers_preserve_target_geometry() -> None:
    target = np.eye(4, dtype=float)
    target[:3, 3] = (2.0, 3.0, 4.0)
    target[:3, :3] *= 1.25
    identity = np.eye(4, dtype=float)

    composed = mapped_local_from_composed_rotation_deltas(
        target,
        [identity, identity],
        [_rotation_z(20.0), _rotation_z(30.0)],
    )
    distributed = mapped_local_from_distributed_rotation_delta(
        target, identity, _rotation_z(60.0), 0.5
    )

    assert _rotation_degrees(composed) == pytest.approx(50.0)
    assert _rotation_degrees(distributed) == pytest.approx(30.0)
    assert composed[:3, 3] == pytest.approx(target[:3, 3])
    assert distributed[:3, 3] == pytest.approx(target[:3, 3])
    assert np.linalg.norm(composed[:3, :3], axis=0) == pytest.approx((1.25,) * 3)
    assert np.linalg.norm(distributed[:3, :3], axis=0) == pytest.approx((1.25,) * 3)


def test_materialized_plan_executes_composed_distributed_and_inherited_modes(
    tmp_path: Path,
) -> None:
    rig = _target_rig()
    profile = _reviewed_executable_profile(rig)
    document = _SemanticExecutionDocument()

    build = build_mapped_rig_anm2(
        tmp_path / "semantic-source.fbx",
        rig,
        profile,
        document=document,
        root_policy="inplace",
    )
    frame = list(decode_samples(build.payload, [1.0]).frames[0].tracks)
    globals_by_name = reconstruct_target_globals(rig, frame)
    indices = {
        bone.name: rig.descriptors.index(bone.descriptor) for bone in rig.bones
    }
    bind = rig.bind_track_values()

    assert _rotation_degrees(globals_by_name["target_composed"]) == pytest.approx(
        50.0, abs=0.05
    )
    assert _rotation_degrees(globals_by_name["target_distributed_a"]) == pytest.approx(
        30.0, abs=0.05
    )
    assert _rotation_degrees(globals_by_name["target_distributed_b"]) == pytest.approx(
        60.0, abs=0.05
    )
    inherited_track = frame[indices["target_inherited"]]
    assert inherited_track == pytest.approx(
        bind[indices["target_inherited"]], abs=1.0e-6
    )
    assert _rotation_degrees(globals_by_name["target_inherited"]) == pytest.approx(
        50.0, abs=0.05
    )
    assert build.report["base_execution_mapping_modes"] == {
        "target_composed": "composed",
        "target_distributed_a": "distributed",
        "target_distributed_b": "distributed",
        "target_inherited": "inherit_bind",
        "target_root": "direct",
    }
    assert build.report["preserve_target_translation"] is True
    assert build.report["preserve_target_scale"] is True
    assert build.report["mapping_certificate_status"] == "not_applicable"


def test_direct_engine_report_does_not_trust_non_live_certificate(
    tmp_path: Path,
) -> None:
    rig = _target_rig()
    profile = deepcopy(_reviewed_executable_profile(rig))
    profile.extensions["origin"] = "automatic_verified"
    profile.extensions["automatic_retarget_certificate"] = {
        "format": "dl2_advanced_body_bridge_v1",
        "status": "pass",
        "live_revalidated": False,
    }

    build = build_mapped_rig_anm2(
        tmp_path / "semantic-source.fbx",
        rig,
        profile,
        document=_SemanticExecutionDocument(),
        root_policy="inplace",
    )

    assert build.report["mapping_certificate_status"] == "failed"
    assert build.report["automatic_mapping_certificate_status"] == "failed"


def test_analyzed_bone_side_conflict_blocks_animated_critical_candidate() -> None:
    rig = _target_rig()
    analysis, policy = _analysis_and_policy(rig)
    conflicting = SimpleNamespace(
        name="source_root",
        parent_name=None,
        side_conflict=True,
    )
    candidate = SimpleNamespace(
        bone_name="source_root",
        confidence=1.0,
        confidence_margin=1.0,
        side="left",
        evidence=("semantic_role", "topology"),
    )
    analysis.nodes = (conflicting, *analysis.nodes[1:])
    analysis.semantic_roles = {"left_upper_arm": candidate}
    analysis.archetype = "humanoid"
    analysis.animation_domain = "full_body"
    policy.target_archetype = "humanoid"
    policy.bones = tuple(
        SimpleNamespace(
            target_bone=bone.name,
            target_category="body",
            semantic_role=(
                "left_upper_arm" if bone.name == "target_composed" else ""
            ),
            helper=False,
        )
        for bone in rig.bones
    )
    policy.semantic_chains = {}

    plan = build_automatic_retarget_plan(analysis, rig, policy)

    conflict_row = next(
        row for row in plan.decisions if row.target_bone == "target_composed"
    )
    assert conflict_row.mode == "inherit_bind"
    assert conflict_row.source_bones == ()
    assert "ambiguous" in conflict_row.reason
