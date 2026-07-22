from __future__ import annotations

from types import SimpleNamespace
from pathlib import Path

from dlanm2_gui.chrome_rig import ChromeRig
from dlanm2_gui.fbx_preflight import classify_target_compatibility


ROOT = Path(__file__).resolve().parents[1]
RIG = ChromeRig.load(ROOT / "reference" / "dl2" / "player_skeleton.crig")


def test_disconnected_optional_exact_name_is_bind_held_not_blocking() -> None:
    document = SimpleNamespace(
        limb_models={"head": 1, "headend": 2},
        parent_by_name={"head": None, "headend": None},
    )

    result = classify_target_compatibility(document, RIG)

    assert result["hierarchy_mismatches"] == []
    assert result["optional_hierarchy_mismatches_held_at_bind"] == [
        {
            "bone": "headend",
            "expected_target_parent": "head",
            "source_target_ancestor": None,
        }
    ]
    assert result["exact_target_subset_mapping"] == {"head": "head"}
    assert "headend" in result["target_bind_bones"]


def test_required_deform_parent_mismatch_remains_action_required() -> None:
    document = SimpleNamespace(
        limb_models={"pelvis": 1, "l_thigh": 2},
        parent_by_name={"pelvis": None, "l_thigh": None},
    )

    result = classify_target_compatibility(document, RIG)

    assert result["optional_hierarchy_mismatches_held_at_bind"] == []
    assert result["hierarchy_mismatches"] == [
        {
            "bone": "l_thigh",
            "expected_target_parent": "pelvis",
            "source_target_ancestor": None,
        }
    ]
    assert result["classification"] == "incompatible"
