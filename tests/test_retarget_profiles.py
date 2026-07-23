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
    profile.set_target_bone_override(
        "spine_helper",
        mode="direct",
        source_bone="Spine",
        transfer_policy="rotation_delta",
    )
    path = profile.save(tmp_path / "rig.dlrmap.json")
    loaded = SourceBoneMappingProfile.load(path)
    assert loaded.profile_id == profile.profile_id
    assert loaded.mapped_bone("hips") == "Pelvis"
    assert loaded.target_bone_overrides == profile.target_bone_overrides


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


def test_character_creator_and_dl_suffix_names_use_shared_scan() -> None:
    bones = [
        "root", "pelvis", "CC_Base_Pelvis",
        "spine_01", "spine_02", "spine_03", "neck_01", "head",
        "clavicle_l", "upperarm_l", "lowerarm_l", "hand_l",
        "clavicle_r", "upperarm_r", "lowerarm_r", "hand_r",
        "thigh_l", "calf_l", "foot_l", "ball_l",
        "thigh_r", "calf_r", "foot_r", "ball_r",
        "middle_01_l", "middle_02_l", "middle_03_l",
        "CC_Base_L_ForearmTwist01", "CC_Base_FacialBone",
    ]
    profile = auto_map_source_bones(bones)

    assert profile.mapped_bone("hips") == "pelvis"
    assert profile.mapped_bone("spine") == "spine_01"
    assert profile.mapped_bone("chest") == "spine_02"
    assert profile.mapped_bone("upper_chest") == "spine_03"
    assert profile.mapped_bone("left_lower_arm") == "lowerarm_l"
    assert profile.mapped_bone("left_middle_3") == "middle_03_l"
    assert profile.mapped_bone("right_toes") == "ball_r"
    assert "CC_Base_L_ForearmTwist01" in profile.ignored_bones
    assert not [error for error in profile.validate(bones) if "Required role" in error]


def test_mixamo_short_arm_and_leg_names_are_semantic_roles() -> None:
    from dlanm2_gui.retarget_mapping import scan_humanoid_bones

    scan = scan_humanoid_bones(["mixamorig:LeftArm", "mixamorig:LeftLeg"])
    assert scan["mixamorig:LeftArm"].role == "l_upperarm"
    assert scan["mixamorig:LeftLeg"].role == "l_calf"
