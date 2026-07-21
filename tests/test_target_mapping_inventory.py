from __future__ import annotations

from pathlib import Path

from dlanm2_gui.chrome_rig import ChromeRig
from dlanm2_gui.game_profiles import DL2_ADVANCED_RIG_REF, DL2_LEGACY_RIG_REF
from dlanm2_gui.target_mapping_inventory import (
    builtin_helper_target_names,
    visible_extra_target_names,
)
from dlanm2_gui.target_retarget_policy import build_target_retarget_policy


ROOT = Path(__file__).resolve().parents[1]


def test_advanced_all_target_inventory_is_exactly_271_unique_rows() -> None:
    rig = ChromeRig.load(ROOT / "reference" / "dl2" / "player_skeleton.crig")
    policy = build_target_retarget_policy(rig, game_id="dying_light_2")
    semantic = tuple(slot.target_bone for slot in policy.direct_slots)
    extras = visible_extra_target_names(
        tuple(bone.name for bone in rig.bones),
        semantic,
        target_rig_ref=DL2_ADVANCED_RIG_REF,
        show_helper_bones=False,
        show_all_target_bones=True,
    )
    combined = (*semantic, *extras)
    assert len(semantic) == 52
    assert len(combined) == 271
    assert len(set(combined)) == 271


def test_helper_inventory_is_target_owned_for_advanced_and_legacy() -> None:
    advanced = ChromeRig.load(ROOT / "reference" / "dl2" / "player_skeleton.crig")
    advanced_helpers = builtin_helper_target_names(
        DL2_ADVANCED_RIG_REF, (bone.name for bone in advanced.bones)
    )
    assert {"l_sole_helper", "r_sole_helper", "refcamera", "eyecamera", "headend"} <= set(advanced_helpers)
    assert "l_iktarget" not in advanced_helpers

    legacy = ChromeRig.load(ROOT / "reference" / "dl2" / "player_shadow_caster.crig")
    legacy_helpers = builtin_helper_target_names(
        DL2_LEGACY_RIG_REF, (bone.name for bone in legacy.bones)
    )
    assert {"l_iktarget", "r_iktarget", "player_shadowcaster"} <= set(legacy_helpers)
