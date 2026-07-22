from __future__ import annotations

import math
from pathlib import Path

import numpy as np
import pytest

from dlanm2_gui.anm2_fbx import (
    AnimationScene,
    SceneBone,
    build_sparse_fbx_job,
    resample_animation_scene,
)


def _scene(frame_count: int = 381) -> AnimationScene:
    bone = SceneBone(
        "root", -1, 1, (0.0, 0.0, 0.0), (1.0, 0.0, 0.0, 0.0),
        (1.0, 1.0, 1.0),
    )
    translations = np.zeros((frame_count, 1, 3), dtype=float)
    translations[:, 0, 0] = np.linspace(0.0, 10.0, frame_count)
    scales = np.ones((frame_count, 1, 3), dtype=float)
    scales[:, 0, 1] = np.linspace(1.0, 2.0, frame_count)
    rotations = np.zeros((frame_count, 1, 4), dtype=float)
    angles = np.linspace(0.0, math.pi, frame_count)
    rotations[:, 0, 0] = np.cos(angles / 2.0)
    rotations[:, 0, 2] = np.sin(angles / 2.0)
    return AnimationScene(
        "timing", 30.0, [bone], translations, rotations, scales,
        primary_root_index=0,
    )


def test_381_at_30_resamples_to_305_at_24_with_exact_endpoints(tmp_path: Path) -> None:
    source = _scene()
    result = resample_animation_scene(source, input_fps=30.0, output_fps=24.0)
    assert result.frame_count == 305
    assert result.fps == 24.0
    assert result.anm2_input_fps == 30.0
    assert result.translations[[0, -1]] == pytest.approx(source.translations[[0, -1]])
    assert result.scales[[0, -1]] == pytest.approx(source.scales[[0, -1]])
    endpoint_dots = np.abs(np.sum(
        result.rotations_wxyz[[0, -1]] * source.rotations_wxyz[[0, -1]], axis=-1
    ))
    assert endpoint_dots == pytest.approx(np.ones((2, 1)), abs=1.0e-12)
    assert np.min(np.sum(
        result.rotations_wxyz[1:] * result.rotations_wxyz[:-1], axis=-1
    )) >= 0.0

    job = build_sparse_fbx_job(
        result, tmp_path / "out.fbx", tmp_path / "arrays.npz"
    )
    assert len(job.arrays["frames"]) == 305
    assert job.metadata["anm2_input_fps"] == 30.0
    assert job.metadata["fbx_output_fps"] == 24.0


def test_shortest_hemisphere_slerp_and_single_frame() -> None:
    source = _scene(2)
    source.rotations_wxyz[0, 0] = (1.0, 0.0, 0.0, 0.0)
    source.rotations_wxyz[1, 0] = (-math.cos(math.pi / 4), 0.0, -math.sin(math.pi / 4), 0.0)
    result = resample_animation_scene(source, input_fps=1.0, output_fps=2.0)
    assert result.frame_count == 3
    expected_mid = np.asarray((math.cos(math.pi / 8), 0.0, math.sin(math.pi / 8), 0.0))
    assert abs(float(result.rotations_wxyz[1, 0] @ expected_mid)) == pytest.approx(1.0)

    one = _scene(1)
    one_result = resample_animation_scene(one, input_fps=30.0, output_fps=24.0)
    assert one_result.frame_count == 1
    assert one_result.translations == pytest.approx(one.translations)
