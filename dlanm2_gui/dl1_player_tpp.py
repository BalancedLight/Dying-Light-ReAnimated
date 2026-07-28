"""Canonical Dying Light 1 ``player_1_tpp`` animation-node inventory."""

from __future__ import annotations

import hashlib
from pathlib import Path

from .chrome_rig import ChromeRig, ChromeRigBone
from .oracle.smd_bind_pose import (
    parse_smd_bind_pose,
    quaternion_wxyz_from_matrix,
    smd_extrinsic_xyz_matrix,
)
from .trackmap import dl_name_hash


DL1_PLAYER_TPP_HELPER_RIG_REF = "builtin:dl1_player_tpp_helpers"
DL1_PLAYER_TPP_HELPER_RIG_RELATIVE_PATH = (
    "reference/dl1/player_1_tpp_helpers.crig"
)

PLAYER_1_TPP_HELPER_NAMES = (
    "hspine",
    "hspine1",
    "refcamera",
    "eyecamera",
    "l_normal",
    "l_normal2",
    "l_handholder",
    "headend",
    "l_eye",
    "l_eye_pos",
    "r_eye",
    "r_eye_pos",
    "eyes",
    "r_normal",
    "r_normal2",
    "r_handholder",
    "propsholder1",
    "propsholder2",
)

PLAYER_1_TPP_MESH_ROOT_NAMES = (
    "sc_boots",
    "sc_hand_l",
    "sc_hand_r",
    "sc_head",
    "sc_shirt",
    "sc_trousers",
    "beard",
    "cult_arm_belt",
    "flashlight",
    "hair",
    "kevin_boots",
    "kevin_shirt",
    "kevin_trousers",
    "mask",
    "player_1_hand_l_tpp",
    "player_1_hand_r_tpp",
    "player_1_hip_bag",
    "player_4_head",
    "watch",
)

PLAYER_1_TPP_ANIMATION_NODE_COUNT = 87


def build_dl1_player_tpp_helper_rig(
    canonical_smd: str | Path,
    legacy_rig: ChromeRig,
    *,
    reference_anm2: str | Path,
) -> ChromeRig:
    """Build the deterministic 87-node helper-capable DL1 player rig."""

    canonical_smd = Path(canonical_smd)
    reference_anm2 = Path(reference_anm2)
    pose = parse_smd_bind_pose(canonical_smd)
    if not reference_anm2.is_file():
        raise FileNotFoundError(reference_anm2)
    if len(pose.bones) != (
        PLAYER_1_TPP_ANIMATION_NODE_COUNT + len(PLAYER_1_TPP_MESH_ROOT_NAMES)
    ):
        raise ValueError(
            "player_1_tpp.smd must contain exactly 87 animation nodes followed "
            "by the 19 known mesh-root slots"
        )
    animation_nodes = pose.bones[:PLAYER_1_TPP_ANIMATION_NODE_COUNT]
    trailing_names = tuple(
        bone.name for bone in pose.bones[PLAYER_1_TPP_ANIMATION_NODE_COUNT:]
    )
    if trailing_names != PLAYER_1_TPP_MESH_ROOT_NAMES:
        raise ValueError(
            "player_1_tpp.smd trailing nodes do not match the canonical "
            "19 mesh-root inventory"
        )
    if tuple(bone.index for bone in animation_nodes) != tuple(
        range(PLAYER_1_TPP_ANIMATION_NODE_COUNT)
    ):
        raise ValueError("player_1_tpp animation-node indices must be contiguous")

    helper_names = frozenset(PLAYER_1_TPP_HELPER_NAMES)
    actual_helper_names = tuple(
        bone.name for bone in animation_nodes if bone.name in helper_names
    )
    if set(actual_helper_names) != helper_names or len(actual_helper_names) != len(
        helper_names
    ):
        raise ValueError(
            "player_1_tpp.smd does not contain the canonical 18 helper nodes"
        )

    bones: list[ChromeRigBone] = []
    for bone in animation_nodes:
        is_helper = bone.name in helper_names
        quaternion = quaternion_wxyz_from_matrix(
            smd_extrinsic_xyz_matrix(bone.euler_xyz_radians)
        )
        bones.append(
            ChromeRigBone(
                index=bone.index,
                name=bone.name,
                parent_index=bone.parent_index,
                descriptor=dl_name_hash(bone.name),
                bind_translation=bone.translation,
                bind_rotation_wxyz=tuple(float(value) for value in quaternion),
                bind_scale=(1.0, 1.0, 1.0),
                deform=not is_helper,
                helper=is_helper,
                tags=("helper",) if is_helper else (),
            )
        )

    bone_descriptors = {bone.descriptor for bone in bones}
    legacy_order = tuple(int(value) for value in legacy_rig.descriptors)
    missing_bone_descriptors = tuple(
        bone.descriptor for bone in bones if bone.descriptor not in set(legacy_order)
    )
    track_descriptors = (*legacy_order, *missing_bone_descriptors)
    extra_descriptors = tuple(
        descriptor
        for descriptor in track_descriptors
        if descriptor not in bone_descriptors
    )
    roots = [bone.index for bone in bones if bone.parent_index < 0]
    if roots != [0, 85, 86]:
        raise ValueError(
            "player_1_tpp helper rig must have roots bip01, propsholder1 and "
            "propsholder2"
        )

    rig = ChromeRig(
        rig_id=DL1_PLAYER_TPP_HELPER_RIG_REF,
        name="Dying Light 1 Player TPP — Helper-capable",
        category="Humanoid",
        bones=tuple(bones),
        root_index=0,
        writer_profile=legacy_rig.writer_profile,
        extra_track_descriptors=extra_descriptors,
        track_descriptors=track_descriptors,
        description=(
            "Opt-in exact DL1 player rig with all 87 animation entities: "
            "69 deform bones and 18 editable transform helpers."
        ),
        source_model_name=canonical_smd.name,
        extensions={
            "game_id": "dying_light_1",
            "builder": "player_1_tpp_animation_nodes_v1",
            "primary_root": "bip01",
            "legacy_base_rig_id": legacy_rig.rig_id,
            "legacy_base_skeleton_hash": legacy_rig.skeleton_hash,
            "deform_bone_count": 69,
            "helper_bone_count": 18,
            "mesh_root_count_excluded": 19,
            "smd_animation_node_count": PLAYER_1_TPP_ANIMATION_NODE_COUNT,
            "smd_excluded_node_names": list(PLAYER_1_TPP_MESH_ROOT_NAMES),
            "smd_prefix_contract": "animation_nodes_then_mesh_roots_v1",
            "source_smd": canonical_smd.name,
            "source_smd_sha256": hashlib.sha256(
                canonical_smd.read_bytes()
            ).hexdigest(),
            "source_smd_semantic_sha256": hashlib.sha256(
                (
                    canonical_smd.read_text(encoding="utf-8-sig")
                    .replace("\r\n", "\n")
                    .replace("\r", "\n")
                    .rstrip("\n")
                    + "\n"
                ).encode("utf-8")
            ).hexdigest(),
            "source_reference_anm2": reference_anm2.name,
            "source_reference_anm2_sha256": hashlib.sha256(
                reference_anm2.read_bytes()
            ).hexdigest(),
            "semantic_retarget_engine": "exact",
            "roundtrip_contract_required": True,
        },
    )
    validation = rig.validate()
    validation.require_valid()
    if len(rig.bones) != 87 or len(rig.descriptors) != 88:
        raise ValueError("helper-capable rig must contain 87 bones and 88 tracks")
    return rig


__all__ = [
    "DL1_PLAYER_TPP_HELPER_RIG_REF",
    "DL1_PLAYER_TPP_HELPER_RIG_RELATIVE_PATH",
    "PLAYER_1_TPP_ANIMATION_NODE_COUNT",
    "PLAYER_1_TPP_HELPER_NAMES",
    "PLAYER_1_TPP_MESH_ROOT_NAMES",
    "build_dl1_player_tpp_helper_rig",
]
