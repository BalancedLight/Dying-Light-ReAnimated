from __future__ import annotations

from pathlib import Path

import numpy as np
import pytest

from dlanm2_gui.chrome_rig import ChromeRig
from dlanm2_gui.fbx_core import FbxDocument
from dlanm2_gui.root_motion_basis import (
    actor_components,
    build_source_actor_frame,
    build_target_actor_frame,
    map_root_displacement_by_actor_frame,
)
from dlanm2_gui.skeleton_analysis import analyze_source_skeleton
from dlanm2_gui.target_retarget_policy import build_target_retarget_policy


ROOT = Path(__file__).resolve().parents[1]
PRIVATE_FIXTURE = Path(r"S:\Downloads\Thriller Combined - Parts 1-4.fbx")


@pytest.mark.skipif(not PRIVATE_FIXTURE.is_file(), reason="private Thriller FBX unavailable")
def test_private_thriller_forward_range_stays_planar_for_dl2_advanced() -> None:
    document = FbxDocument(PRIVATE_FIXTURE, purpose="animation")
    analysis = analyze_source_skeleton(document)
    source_frame = build_source_actor_frame(analysis)
    rig = ChromeRig.load(ROOT / "reference" / "dl2" / "player_skeleton.crig")
    target_frame = build_target_actor_frame(
        rig, build_target_retarget_policy(rig, game_id="dying_light_2")
    )
    source_root = analysis.body_frame.pelvis_bone
    frame_ticks = document.frame_ticks(fps=30)
    first = np.asarray(
        document.global_matrices(tick=frame_ticks[0], use_animation=True)[source_root],
        dtype=float,
    )
    last = np.asarray(
        document.global_matrices(tick=frame_ticks[-1], use_animation=True)[source_root],
        dtype=float,
    )
    scale = (
        document.meters_per_unit
        / document.wrapper_scale_normalization_factor(source_root)
    )
    raw_delta_m = (last[:3, 3] - first[:3, 3]) * scale
    mapped = map_root_displacement_by_actor_frame(
        raw_delta_m, source_frame, target_frame
    )
    source_actor = actor_components(raw_delta_m, source_frame)
    target_actor = actor_components(mapped, target_frame)

    assert raw_delta_m == pytest.approx(
        (-0.2661424948, -0.0727795410, 7.0856120944), abs=1.0e-7
    )
    assert target_actor == pytest.approx(source_actor, abs=1.0e-9)
    assert float(np.dot(mapped, target_frame.up)) == pytest.approx(
        raw_delta_m[1], abs=1.0e-9
    )
    assert abs(float(np.dot(mapped, target_frame.up))) < 0.25
    assert np.linalg.norm(mapped - target_frame.up * np.dot(mapped, target_frame.up)) > 7.0
    assert mapped == pytest.approx(
        (-0.2661450030, -0.0727795410, 7.0856119206), abs=1.0e-7
    )
