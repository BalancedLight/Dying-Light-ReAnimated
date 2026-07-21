"""Target-owned locomotion and optional IK/helper inventories."""

from __future__ import annotations

from dataclasses import asdict, dataclass

from .game_profiles import (
    DL1_GAME_ID,
    DL1_RIG_REF,
    DL2_ADVANCED_RIG_REF,
    DL2_GAME_ID,
    DL2_LEGACY_RIG_REF,
)


MOTION_ACCUMULATOR_DESCRIPTOR = 0xCCC3CDDF


@dataclass(frozen=True, slots=True)
class BuiltinLocomotionProfile:
    profile_id: str
    game_id: str
    target_rig_ref: str
    primary_root: str
    motion_accumulator_descriptor: int | None
    left_thigh: str
    left_calf: str
    left_foot: str
    left_sole_helper: str | None
    right_thigh: str
    right_calf: str
    right_foot: str
    right_sole_helper: str | None
    legacy_left_ik_root: str | None
    legacy_right_ik_root: str | None
    ik_owner: str = "runtime_consumer"

    def to_dict(self) -> dict[str, object]:
        return asdict(self)


_PROFILES = {
    (DL1_GAME_ID, DL1_RIG_REF): BuiltinLocomotionProfile(
        "dl1_player_locomotion_v1", DL1_GAME_ID, DL1_RIG_REF, "bip01",
        MOTION_ACCUMULATOR_DESCRIPTOR,
        "l_thigh", "l_calf", "l_foot", None,
        "r_thigh", "r_calf", "r_foot", None,
        None, None,
    ),
    (DL2_GAME_ID, DL2_ADVANCED_RIG_REF): BuiltinLocomotionProfile(
        "dl2_advanced_locomotion_v1", DL2_GAME_ID, DL2_ADVANCED_RIG_REF, "pelvis",
        MOTION_ACCUMULATOR_DESCRIPTOR,
        "l_thigh", "l_calf", "l_foot", "l_sole_helper",
        "r_thigh", "r_calf", "r_foot", "r_sole_helper",
        None, None,
    ),
    (DL2_GAME_ID, DL2_LEGACY_RIG_REF): BuiltinLocomotionProfile(
        "dl2_legacy_shadow_caster_locomotion_v1", DL2_GAME_ID, DL2_LEGACY_RIG_REF, "pelvis",
        MOTION_ACCUMULATOR_DESCRIPTOR,
        "l_thigh", "l_calf", "l_foot", "l_sole_helper",
        "r_thigh", "r_calf", "r_foot", "r_sole_helper",
        "l_iktarget", "r_iktarget",
    ),
}


def get_builtin_locomotion_profile(
    game_id: str,
    target_rig_ref: str,
) -> BuiltinLocomotionProfile:
    try:
        return _PROFILES[(str(game_id), str(target_rig_ref))]
    except KeyError as exc:
        raise ValueError(
            f"No bundled locomotion profile for {game_id!r} / {target_rig_ref!r}"
        ) from exc


__all__ = [
    "BuiltinLocomotionProfile",
    "MOTION_ACCUMULATOR_DESCRIPTOR",
    "get_builtin_locomotion_profile",
]
