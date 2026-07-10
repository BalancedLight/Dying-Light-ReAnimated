from __future__ import annotations

from pathlib import Path

from dlanm2_gui.retarget_profiles import (
    HUMANOID_ROLES,
    SourceBoneMappingProfile,
    auto_map_source_bones,
)


def test_standard_mixamo_skeleton_maps_exactly() -> None:
    bones = [role.canonical_source_name for role in HUMANOID_ROLES]
    profile = auto_map_source_bones(bones)
    assert not profile.validate(bones)
    assert profile.mapped_bone("hips") == "mixamorig:Hips"
    assert profile.mapped_bone("left_ring_1") == "mixamorig:LeftHandRing1"
    assert profile.mapped_bone("right_pinky_4") == "mixamorig:RightHandPinky4"
    aliases = profile.canonical_aliases()
    assert aliases["mixamorig:RightArm"] == "mixamorig:RightArm"


def test_profile_roundtrip(tmp_path: Path) -> None:
    profile = SourceBoneMappingProfile.empty(["Pelvis", "Spine"])
    profile.set_mapping("hips", "Pelvis", method="manual")
    path = profile.save(tmp_path / "rig.dlrmap.json")
    loaded = SourceBoneMappingProfile.load(path)
    assert loaded.profile_id == profile.profile_id
    assert loaded.mapped_bone("hips") == "Pelvis"


def test_common_non_mixamo_names_are_recognized() -> None:
    bones = [
        "Pelvis",
        "Spine",
        "Chest",
        "UpperChest",
        "Neck",
        "Head",
        "clavicle_l",
        "upper_arm_l",
        "lower_arm_l",
        "hand_l",
        "clavicle_r",
        "upper_arm_r",
        "lower_arm_r",
        "hand_r",
        "thigh_l",
        "calf_l",
        "foot_l",
        "toes_l",
        "thigh_r",
        "calf_r",
        "foot_r",
        "toes_r",
    ]
    profile = auto_map_source_bones(bones)
    required_errors = [error for error in profile.validate(bones) if "Required role" in error]
    assert not required_errors
