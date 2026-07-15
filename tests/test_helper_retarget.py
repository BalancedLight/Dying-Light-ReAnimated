from __future__ import annotations

import math

import numpy as np
import pytest

from dlanm2_gui.helper_retarget import (
    HelperRetargetRule,
    apply_helper_retarget_overrides,
    evaluate_helper_target_local,
    include_base_source_fanout,
    local_matrix_to_anm2_values,
    merge_helper_components,
)


def _translation(x: float, y: float, z: float) -> np.ndarray:
    result = np.eye(4, dtype=float)
    result[:3, 3] = (x, y, z)
    return result


def _rotation_z(degrees: float) -> np.ndarray:
    radians = math.radians(degrees)
    result = np.eye(4, dtype=float)
    result[0, 0] = result[1, 1] = math.cos(radians)
    result[0, 1] = -math.sin(radians)
    result[1, 0] = math.sin(radians)
    return result


@pytest.mark.parametrize(
    ("policy", "changed", "preserved"),
    [
        ("rotation", range(0, 3), range(3, 9)),
        ("translation", range(3, 6), (*range(0, 3), *range(6, 9))),
        ("rotation_translation", range(0, 6), range(6, 9)),
        ("scale", range(6, 9), range(0, 6)),
        ("full_transform", range(0, 9), ()),
    ],
)
def test_component_merge_replaces_only_selected_values(
    policy: str, changed: range, preserved: tuple[int, ...] | range
) -> None:
    existing = [float(index) for index in range(9)]
    candidate = [float(index + 20) for index in range(9)]
    merged = merge_helper_components(existing, candidate, policy)

    for index in changed:
        assert merged[index] == candidate[index]
    for index in preserved:
        assert merged[index] == existing[index]


def test_shared_source_keeps_distinct_target_bind_transforms() -> None:
    source_bind = np.eye(4)
    source_animated = _translation(0.25, 0.0, 0.0) @ _rotation_z(30.0)
    head_bind = _translation(0.0, 1.0, 0.0)
    camera_bind = _translation(0.1, 1.2, -0.05)

    head = evaluate_helper_target_local(head_bind, source_bind, source_animated)
    camera = evaluate_helper_target_local(camera_bind, source_bind, source_animated)

    assert not np.allclose(head, camera)
    assert np.allclose(camera_bind @ np.linalg.inv(head_bind) @ head, camera)


def test_rotation_delta_preserves_target_translation_and_scale() -> None:
    target = _translation(1.0, 2.0, 3.0)
    target[:3, :3] *= 1.25
    candidate = evaluate_helper_target_local(
        target,
        np.eye(4),
        _translation(10.0, 20.0, 30.0) @ _rotation_z(45.0),
        "rotation_delta",
    )
    values = local_matrix_to_anm2_values(candidate)

    assert values[3:6] == pytest.approx((1.0, 2.0, 3.0))
    assert values[6:9] == pytest.approx((1.25, 1.25, 1.25))


def test_apply_is_deterministic_and_leaves_unmapped_helper_unchanged() -> None:
    target_bind = {
        "head": _translation(0.0, 1.0, 0.0),
        "refcamera": _translation(0.1, 0.2, 0.3),
        "eyecamera": _translation(0.4, 0.5, 0.6),
    }
    indices = {"head": 0, "refcamera": 1, "eyecamera": 2}
    bind_rows = [local_matrix_to_anm2_values(target_bind[name]) for name in indices]
    rules = [
        HelperRetargetRule("refcamera", "Head", "rest_relative", "translation")
    ]
    source_frames = [
        {"Head": np.eye(4)},
        {"Head": _translation(0.2, 0.0, 0.0)},
    ]

    first = [[list(row) for row in bind_rows] for _ in range(2)]
    second = [[list(row) for row in bind_rows] for _ in range(2)]
    kwargs = dict(
        target_bind_local=target_bind,
        target_track_indices=indices,
        target_parents={"head": None, "refcamera": "head", "eyecamera": "head"},
        source_bind_local={"Head": np.eye(4)},
        source_animated_local_frames=source_frames,
    )
    report = apply_helper_retarget_overrides(first, rules, **kwargs)
    apply_helper_retarget_overrides(second, rules, **kwargs)

    assert first == second
    assert first[1][1][3] == pytest.approx(bind_rows[1][3] + 0.2)
    assert first[1][2] == bind_rows[2]
    assert report.helper_targets == ["refcamera"]


def test_non_finite_helper_output_is_rejected() -> None:
    animated = np.eye(4)
    animated[0, 0] = np.nan
    with pytest.raises(ValueError, match="finite"):
        evaluate_helper_target_local(np.eye(4), np.eye(4), animated)


def test_fanout_report_includes_existing_body_target() -> None:
    report = apply_helper_retarget_overrides(
        [[local_matrix_to_anm2_values(_translation(0.1, 0.2, 0.3))]],
        [HelperRetargetRule("refcamera", "Head", "rest_relative", "translation")],
        target_bind_local={"refcamera": _translation(0.1, 0.2, 0.3)},
        target_track_indices={"refcamera": 0},
        target_parents={"refcamera": None},
        source_bind_local={"Head": np.eye(4)},
        source_animated_local_frames=[{"Head": np.eye(4)}],
    )

    include_base_source_fanout(report, [HelperRetargetRule("refcamera", "Head")], {"Head": ["head"]})

    assert report.helper_source_fanout_count == 1
    assert report.shared_source_bones == {"Head": ["head", "refcamera"]}
