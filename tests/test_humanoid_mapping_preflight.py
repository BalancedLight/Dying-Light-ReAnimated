from __future__ import annotations

from types import SimpleNamespace

import numpy as np
import pytest

from dlanm2_gui.model_importer.msh_builder import (
    _BuildVertex,
    _MeshChunk,
    _bind_topology_preflight,
    _effective_humanoid_targets,
    _fit_dying_light_target_bind,
    _humanoid_bind_compatibility,
    _remap_vertex_influences,
    _validate_bind_topology_preflight,
    _validate_humanoid_weighted_coverage,
    humanoid_bone_mapping,
)


class _MappingScene:
    def __init__(self, names: list[str], parents: dict[int, int | None] | None = None):
        self.model_names = dict(enumerate(names))
        self._parents = parents or {}

    def model_parent_id(self, bone_id: int) -> int | None:
        return self._parents.get(bone_id)


def test_model_mapping_prefers_the_weighted_deformation_pelvis() -> None:
    scene = _MappingScene(["root", "pelvis", "CC_Base_Hip"])
    targets = [SimpleNamespace(name="bip01"), SimpleNamespace(name="pelvis")]

    mapping, report = humanoid_bone_mapping(
        scene,
        [0, 1, 2],
        targets,
        source_weight_totals={1: 1.0, 2: 99.0},
    )

    assert mapping[0] == 0
    assert mapping[1] is None
    assert mapping[2] == 1
    row = next(row for row in report["rows"] if row["source_bone"] == "CC_Base_Hip")
    assert row["role"] == "pelvis"
    assert row["source_skin_weight"] == 99.0


def test_manual_mapping_reports_cross_role_finger_shift_without_overriding_user() -> None:
    scene = _MappingScene(["CC_Base_L_Index2"])
    targets = [SimpleNamespace(name="l_finger11"), SimpleNamespace(name="l_finger12")]

    mapping, report = humanoid_bone_mapping(
        scene,
        [0],
        targets,
        manual_mapping={"CC_Base_L_Index2": "l_finger11"},
        source_weight_totals={0: 1.0},
    )

    assert mapping[0] == 0
    assert report["manual_role_mismatches"] == [
        {
            "source_bone": "CC_Base_L_Index2",
            "source_role": "l_index_2",
            "target_bone": "l_finger11",
            "target_role": "l_index_1",
        }
    ]


def test_model_mapping_uses_available_dying_light_twist_and_neck_chains() -> None:
    source_names = [
        "CC_Base_L_UpperarmTwist01",
        "CC_Base_L_UpperarmTwist02",
        "CC_Base_L_ForearmTwist01",
        "CC_Base_L_ForearmTwist02",
        "CC_Base_L_ThighTwist01",
        "CC_Base_NeckTwist01",
        "CC_Base_NeckTwist02",
    ]
    scene = _MappingScene(source_names)
    targets = [
        SimpleNamespace(name=name)
        for name in (
            "l_uparmtwist",
            "l_foretwist",
            "l_foretwist1",
            "l_thightwist",
            "neck",
            "neck1",
        )
    ]

    mapping, _report = humanoid_bone_mapping(
        scene,
        list(range(len(source_names))),
        targets,
        source_weight_totals={index: 10.0 - index for index in range(len(source_names))},
    )
    mapped = {
        source_names[source]: targets[target].name
        for source, target in mapping.items()
        if target is not None
    }

    assert mapped["CC_Base_L_UpperarmTwist01"] == "l_uparmtwist"
    assert "CC_Base_L_UpperarmTwist02" not in mapped
    assert mapped["CC_Base_L_ForearmTwist01"] == "l_foretwist"
    assert mapped["CC_Base_L_ForearmTwist02"] == "l_foretwist1"
    assert mapped["CC_Base_L_ThighTwist01"] == "l_thightwist"
    assert mapped["CC_Base_NeckTwist01"] == "neck"
    assert mapped["CC_Base_NeckTwist02"] == "neck1"


def test_model_mapping_keeps_cc_spine_and_middle_finger_pivots_distinct() -> None:
    source_names = ["CC_Base_Spine01", "CC_Base_L_Mid1", "CC_Base_L_Mid2"]
    scene = _MappingScene(source_names)
    targets = [
        SimpleNamespace(name="spine1"),
        SimpleNamespace(name="l_finger21"),
        SimpleNamespace(name="l_finger22"),
    ]

    mapping, report = humanoid_bone_mapping(
        scene,
        [0, 1, 2],
        targets,
        source_weight_totals={0: 10.0, 1: 3.0, 2: 2.0},
    )

    assert mapping == {0: 0, 1: 1, 2: 2}
    assert all(row["method"] == "native_model_alias" for row in report["rows"])


def test_fitted_bind_interpolates_unanchored_nodes_between_source_pivots() -> None:
    scene = _MappingScene(["source_root", "source_tip"])
    targets = [
        SimpleNamespace(index=index, parent_index=index - 1, name=f"target_{index}")
        for index in range(5)
    ]
    targets[0].parent_index = -1
    stock_globals: dict[int, np.ndarray] = {}
    for index in range(5):
        value = np.eye(4, dtype=float)
        value[1, 3] = float(index)
        stock_globals[index] = value
    source_root = np.eye(4, dtype=float)
    source_tip = np.eye(4, dtype=float)
    source_tip[1, 3] = 1.0

    result = _fit_dying_light_target_bind(
        scene,
        [0, 1],
        targets,
        {0: source_root, 1: source_tip},
        stock_globals,
        {0: 0, 1: 4},
        {0: 0, 1: 4},
        {"bone_weight_totals": {0: 1.0, 1: 1.0}},
    )

    positions = [matrix[1, 3] for matrix in result["global_matrices"]]
    assert positions == pytest.approx([0.0, 0.25, 0.5, 0.75, 1.0])
    assert result["report"]["interpolated_hierarchy_node_count"] == 3


def test_effective_mapping_distinguishes_ancestor_and_root_fallback() -> None:
    scene = _MappingScene(
        ["root", "upperarm_l", "upperarm_twist_l", "detached_accessory"],
        {0: None, 1: 0, 2: 1, 3: None},
    )
    effective, methods = _effective_humanoid_targets(
        scene,
        [0, 1, 2, 3],
        {0: 0, 1: 4, 2: None, 3: None},
        fallback_target=1,
    )

    assert effective == {0: 0, 1: 4, 2: 4, 3: 1}
    assert methods == {
        0: "direct",
        1: "direct",
        2: "ancestor_fallback",
        3: "root_fallback",
    }


def _vertex(source: tuple[float, float, float], output: tuple[float, float, float]) -> _BuildVertex:
    return _BuildVertex(
        position=np.asarray(output, dtype=float),
        source_position=np.asarray(source, dtype=float),
        normal=np.asarray((0.0, 0.0, 1.0), dtype=float),
        uv=(0.0, 0.0),
        color=(255, 255, 255, 255),
        influences=[(0, 1.0)],
    )


def test_bind_topology_preflight_rejects_an_exploded_triangle() -> None:
    chunk = _MeshChunk(
        "body",
        0,
        vertices=[
            _vertex((0, 0, 0), (0, 0, 0)),
            _vertex((1, 0, 0), (100, 0, 0)),
            _vertex((0, 1, 0), (0, 1, 0)),
        ],
    )

    report = _bind_topology_preflight([chunk])

    assert report["status"] == "fail"
    assert report["edge_distortion_ratio"]["p95"] > 4.0
    with pytest.raises(ValueError, match="bind/topology preflight failed"):
        _validate_bind_topology_preflight(report)


def test_weighted_coverage_error_names_the_highest_fallback_bone() -> None:
    report = {
        "total_normalized_weight": 100.0,
        "direct_weight_fraction": 0.40,
        "root_fallback_weight_fraction": 0.0,
        "top_fallback_bones": [
            {"source_bone": "CC_Base_Hip", "source_weight_fraction": 0.45}
        ],
    }

    with pytest.raises(ValueError, match=r"40\.0%.*CC_Base_Hip"):
        _validate_humanoid_weighted_coverage(report)


def test_humanoid_weight_remap_combines_targets_without_changing_bind_surface() -> None:
    mapped = _remap_vertex_influences(
        [(10, 0.55), (11, 0.25), (12, 0.20)],
        {10: 3, 11: 3, 12: 7},
        fallback_target=1,
    )

    assert [row[0] for row in mapped] == [3, 7]
    assert [row[1] for row in mapped] == pytest.approx([0.80, 0.20])

    triangle = _MeshChunk(
        "preserved",
        0,
        vertices=[
            _vertex((0, 0, 0), (0, 0, 0)),
            _vertex((1, 0, 0), (1, 0, 0)),
            _vertex((0, 1, 0), (0, 1, 0)),
        ],
    )
    report = _bind_topology_preflight([triangle])
    assert report["status"] == "pass"
    assert report["source_bounds_diagonal"] == pytest.approx(
        report["output_bounds_diagonal"]
    )
    assert report["edge_distortion_ratio"]["max"] == pytest.approx(1.0)


def test_bind_compatibility_warns_about_distant_animation_pivots_without_warping() -> None:
    scene = _MappingScene(["pelvis", "head"])
    targets = [SimpleNamespace(name="pelvis"), SimpleNamespace(name="head")]
    identity = np.eye(4, dtype=float)
    source_head = identity.copy()
    source_head[1, 3] = 1.0
    target_head = identity.copy()
    target_head[1, 3] = 1.6

    report = _humanoid_bind_compatibility(
        scene,
        [0, 1],
        targets,
        {0: identity, 1: source_head},
        {0: identity, 1: target_head},
        {0: 0, 1: 1},
        {"bone_weight_totals": {0: 1.0, 1: 9.0}},
    )

    assert report["status"] == "review"
    assert report["weighted_mean_pivot_distance_m"] == pytest.approx(0.54)
    assert report["weighted_p95_pivot_distance_m"] == pytest.approx(0.6)
    assert report["source_weighted_bone_bounds_diagonal_m"] == pytest.approx(1.0)
    assert report["target_mapped_bone_bounds_diagonal_m"] == pytest.approx(1.6)
