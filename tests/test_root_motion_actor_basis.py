from __future__ import annotations

from pathlib import Path

import numpy as np
import pytest

from dlanm2_gui.chrome_rig import ChromeRig
from dlanm2_gui.game_profiles import DL2_GAME_ID
from dlanm2_gui.root_motion import (
    RootHeadingMode,
    RootMotionMode,
    RootMotionSelection,
)
from dlanm2_gui.root_motion_basis import (
    ActorFrame,
    ActorFrameAmbiguityError,
    build_target_actor_frame,
    map_root_displacement_by_actor_frame,
    root_motion_basis_report,
)
from dlanm2_gui.target_retarget_policy import build_target_retarget_policy


ROOT = Path(__file__).resolve().parents[1]


def test_forward_displacement_maps_by_actor_axes_not_model_axis_conversion() -> None:
    source = ActorFrame(
        np.asarray((1.0, 0.0, 0.0)),
        np.asarray((0.0, 1.0, 0.0)),
        np.asarray((0.0, 0.0, 1.0)),
        "synthetic_source",
        1.0,
    )
    rig = ChromeRig.load(ROOT / "reference" / "dl2" / "player_skeleton.crig")
    target = build_target_actor_frame(
        rig, build_target_retarget_policy(rig, game_id=DL2_GAME_ID)
    )
    mapped = map_root_displacement_by_actor_frame((0.0, 0.0, 2.5), source, target)

    assert float(np.dot(mapped, target.forward)) == pytest.approx(2.5)
    assert float(np.dot(mapped, target.up)) == pytest.approx(0.0, abs=1.0e-9)
    report = root_motion_basis_report(source, target, (0.0, 0.0, 2.5))
    assert report["model_basis_used_for_root_vector"] is False
    assert report["target_net_actor_displacement_m"]["forward"] == pytest.approx(2.5)


def test_actor_frame_rejects_degenerate_or_non_finite_evidence() -> None:
    with pytest.raises(ActorFrameAmbiguityError, match="degenerate"):
        ActorFrame(
            np.zeros(3),
            np.asarray((0.0, 1.0, 0.0)),
            np.asarray((0.0, 0.0, 1.0)),
            "bad",
            0.0,
        )


@pytest.mark.parametrize(
    ("legacy", "motion", "heading"),
    (
        ("inplace", RootMotionMode.IN_PLACE.value, RootHeadingMode.LOCK_INITIAL.value),
        ("bip01", RootMotionMode.SKELETAL_ROOT.value, RootHeadingMode.PRESERVE.value),
        ("motion", RootMotionMode.MOTION_ACCUMULATOR.value, RootHeadingMode.TO_MOTION_ACCUMULATOR.value),
    ),
)
def test_legacy_root_policy_is_only_a_target_neutral_adapter(
    legacy: str, motion: str, heading: str
) -> None:
    selection = RootMotionSelection.from_legacy_policy(
        legacy, source_root_bone="SourcePelvis", target_root_bone="pelvis"
    )
    assert selection.motion_mode == motion
    assert selection.heading_mode == heading
    assert selection.target_root_bone == "pelvis"
    assert selection.legacy_serialized_policy == legacy

