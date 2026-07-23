from __future__ import annotations

from types import SimpleNamespace
from pathlib import Path

from dlanm2_gui.automatic_retarget import _exact_identity
from dlanm2_gui.chrome_rig import ChromeRig


ROOT = Path(__file__).resolve().parents[1]
RIG = ChromeRig.load(ROOT / "reference" / "dl2" / "player_skeleton.crig")


def _node(name: str, parent: str = "") -> SimpleNamespace:
    return SimpleNamespace(original_name=name, parent_name=parent)


def test_large_structurally_anchored_exact_source_superset_keeps_rows() -> None:
    selected = RIG.bones[:90]
    nodes = []
    for bone in selected:
        parent = RIG.bones[bone.parent_index].name if bone.parent_index >= 0 else ""
        nodes.append(_node(bone.name, parent))
    nodes.extend(_node(f"outfit_extra_{index}") for index in range(200))

    exact, matched, source_is_target_subset = _exact_identity(
        SimpleNamespace(nodes=tuple(nodes)), RIG
    )

    assert not exact
    assert not source_is_target_subset
    assert len(matched) >= 80
    assert matched["pelvis"] == "pelvis"
    assert matched["l_thigh"] == "l_thigh"


def test_tiny_coincidental_exact_overlap_still_uses_semantic_fallback() -> None:
    analysis = SimpleNamespace(
        nodes=(
            _node("pelvis"),
            _node("spine1", "pelvis"),
            _node("foreign_arm", "spine1"),
            _node("foreign_leg", "pelvis"),
        )
    )

    exact, matched, source_is_target_subset = _exact_identity(analysis, RIG)

    assert not exact
    assert not source_is_target_subset
    assert matched == {}
