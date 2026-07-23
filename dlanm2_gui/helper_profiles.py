"""Dying Light 1 helper-track inventory and compatibility helpers.

The selected target rig owns the helper inventory.  The GUI exposes recognized
helper nodes directly from its canonical SMD, and the builder appends only the
helpers that the user explicitly maps.  The older named helper profiles remain
loadable solely for project compatibility; they are no longer a GUI gate.
"""

from __future__ import annotations

from dataclasses import dataclass
from typing import Iterable, Sequence

from .trackmap import dl_name_hash


LEGACY_HELPER_PROFILE_ID = "dl1_legacy_70_track"
DL1_TPP_HELPER_PROFILE_ID = "dl1_player_tpp_helpers"
DL1_FPP_HELPER_PROFILE_ID = "dl1_player_fpp_helpers"


@dataclass(frozen=True, slots=True)
class HelperTargetProfile:
    profile_id: str
    display_name: str
    helper_names: tuple[str, ...]
    description: str
    advanced: bool = True
    experimental: bool = True


_TWIST_HELPERS = (
    "l_foretwist",
    "l_foretwist1",
    "l_foretwistt",
    "r_foretwist",
    "r_foretwist1",
    "r_foretwistt",
    "l_uparmtwist",
    "r_uparmtwist",
    "l_thightwist",
    "r_thightwist",
)

RECOGNIZED_DL1_HELPERS = (
    "refcamera",
    "eyecamera",
    "headend",
    "eyes",
    "l_eye",
    "l_eye_pos",
    "r_eye",
    "r_eye_pos",
    "l_handholder",
    "r_handholder",
    "l_normal",
    "l_normal2",
    "r_normal",
    "r_normal2",
    "l_finger01extra",
    "r_finger01extra",
    *_TWIST_HELPERS,
    "propsholder1",
    "propsholder2",
    "flashlight",
)

HELPER_TARGET_PROFILES: dict[str, HelperTargetProfile] = {
    LEGACY_HELPER_PROFILE_ID: HelperTargetProfile(
        LEGACY_HELPER_PROFILE_ID,
        "Dying Light Male NPC / Infected — legacy 70-track",
        (),
        "Existing validated descriptor order and output behavior.",
        advanced=False,
        experimental=False,
    ),
    DL1_TPP_HELPER_PROFILE_ID: HelperTargetProfile(
        DL1_TPP_HELPER_PROFILE_ID,
        "Dying Light 1 Player TPP — helper capable",
        RECOGNIZED_DL1_HELPERS,
        "Player TPP camera, eye, holder, and twist nodes from player_1_tpp.smd.",
    ),
    DL1_FPP_HELPER_PROFILE_ID: HelperTargetProfile(
        DL1_FPP_HELPER_PROFILE_ID,
        "Dying Light 1 Player FPP — camera/weapon helpers",
        (
            "refcamera",
            "eyecamera",
            "headend",
            "eyes",
            "l_handholder",
            "r_handholder",
            *_TWIST_HELPERS,
            "propsholder1",
            "propsholder2",
        ),
        "Camera, hand-holder, twist, and generic prop-holder nodes available in the bundled player model.",
    ),
}


HELPER_SUGGESTIONS: dict[str, tuple[tuple[str, ...], str]] = {
    "refcamera": (("RefCamera", "Camera", "Head"), "translation"),
    "eyecamera": (("EyeCamera", "Camera", "Head"), "rotation_translation"),
    "headend": (("HeadEnd", "Head"), "rotation"),
    "eyes": (("Eyes", "Head"), "rotation_translation"),
    "l_handholder": (("LeftHandHolder", "LeftHand"), "rotation_translation"),
    "r_handholder": (("RightHandHolder", "RightHand"), "rotation_translation"),
    "l_normal": (("LeftShoulder", "LeftArm"), "rotation"),
    "l_normal2": (("LeftShoulder", "LeftArm"), "rotation"),
    "r_normal": (("RightShoulder", "RightArm"), "rotation"),
    "r_normal2": (("RightShoulder", "RightArm"), "rotation"),
    "l_finger01extra": (("LeftHandThumb1", "LeftHand"), "rotation"),
    "r_finger01extra": (("RightHandThumb1", "RightHand"), "rotation"),
    "l_foretwist": (("LeftForeArm",), "rotation"),
    "l_foretwist1": (("LeftForeArm",), "rotation"),
    "l_foretwistt": (("LeftForeArm",), "rotation"),
    "r_foretwist": (("RightForeArm",), "rotation"),
    "r_foretwist1": (("RightForeArm",), "rotation"),
    "r_foretwistt": (("RightForeArm",), "rotation"),
    "l_uparmtwist": (("LeftArm",), "rotation"),
    "r_uparmtwist": (("RightArm",), "rotation"),
    "l_thightwist": (("LeftUpLeg",), "rotation"),
    "r_thightwist": (("RightUpLeg",), "rotation"),
    "propsholder1": (("PropHolder1", "RightHand", "LeftHand"), "rotation_translation"),
    "propsholder2": (("PropHolder2", "LeftHand", "RightHand"), "rotation_translation"),
    "flashlight": (("Flashlight", "RightHand"), "rotation_translation"),
}


def helper_target_profile(profile_id: str | None) -> HelperTargetProfile:
    value = str(profile_id or LEGACY_HELPER_PROFILE_ID)
    try:
        return HELPER_TARGET_PROFILES[value]
    except KeyError as exc:
        raise ValueError(f"Unknown helper target profile {value!r}.") from exc


def available_helper_names(
    profile_id: str | None,
    available_target_names: Iterable[str],
) -> tuple[str, ...]:
    profile = helper_target_profile(profile_id)
    available = set(available_target_names)
    return tuple(name for name in profile.helper_names if name in available)


def recognized_helper_names(
    available_target_names: Iterable[str],
) -> tuple[str, ...]:
    """Return helpers exposed by the selected target, in target hierarchy order."""

    recognized = set(RECOGNIZED_DL1_HELPERS)
    return tuple(
        str(name) for name in available_target_names if str(name) in recognized
    )


def extend_track_descriptors_for_helpers(
    base_descriptors: Sequence[int],
    selected_helper_names: Iterable[str],
    target_names: Iterable[str],
) -> list[int]:
    """Append only explicitly mapped helper tracks in target hierarchy order."""

    result = [int(value) for value in base_descriptors]
    names = tuple(str(name) for name in target_names)
    selected = set(str(name) for name in selected_helper_names)
    # Every explicitly selected row must belong to the target hierarchy.  The
    # GUI's helper view is a convenience subset; Show all target bones is
    # allowed to author any descriptor-backed target row.
    unknown = sorted(selected - set(names), key=str.casefold)
    if unknown:
        raise ValueError(
            "Mapped target bone(s) are unavailable in the selected target SMD: "
            + ", ".join(unknown)
        )

    target_name_by_descriptor: dict[int, str] = {}
    for name in names:
        descriptor = dl_name_hash(name)
        previous = target_name_by_descriptor.get(descriptor)
        if previous is not None and previous != name:
            raise ValueError(
                f"Descriptor collision: {previous!r} and {name!r} both use "
                f"0x{descriptor:08X}."
            )
        target_name_by_descriptor[descriptor] = name

    for name in names:
        if name not in selected:
            continue
        descriptor = dl_name_hash(name)
        if descriptor not in result:
            result.append(descriptor)
    if len(result) != len(set(result)):
        raise ValueError("Mapped helper targets produced a descriptor collision.")
    return result


def extend_track_descriptors(
    base_descriptors: Sequence[int],
    profile_id: str | None,
    target_names: Iterable[str],
) -> list[int]:
    """Append the selected profile's known descriptors without reordering legacy tracks."""

    result = [int(value) for value in base_descriptors]
    names = tuple(str(name) for name in target_names)
    target_name_by_descriptor: dict[int, str] = {}
    for name in names:
        descriptor = dl_name_hash(name)
        previous = target_name_by_descriptor.get(descriptor)
        if previous is not None and previous != name:
            raise ValueError(
                f"Descriptor collision: {previous!r} and {name!r} both use 0x{descriptor:08X}."
            )
        target_name_by_descriptor[descriptor] = name
    for name in available_helper_names(profile_id, names):
        descriptor = dl_name_hash(name)
        if descriptor not in result:
            result.append(descriptor)
    if len(result) != len(set(result)):
        raise ValueError("Helper target profile produced a descriptor collision.")
    return result


def _normalized(value: str) -> str:
    return "".join(character for character in value.casefold() if character.isalnum())


def suggested_helper_source(
    target_name: str,
    source_names: Iterable[str],
) -> tuple[str, str] | None:
    suggestion = HELPER_SUGGESTIONS.get(target_name)
    if suggestion is None:
        return None
    candidates, components = suggestion
    by_normalized = {_normalized(name): name for name in source_names}
    for candidate in candidates:
        match = by_normalized.get(_normalized(candidate))
        if match:
            return match, components
    return None


__all__ = [
    "DL1_FPP_HELPER_PROFILE_ID",
    "DL1_TPP_HELPER_PROFILE_ID",
    "HELPER_SUGGESTIONS",
    "HELPER_TARGET_PROFILES",
    "LEGACY_HELPER_PROFILE_ID",
    "RECOGNIZED_DL1_HELPERS",
    "HelperTargetProfile",
    "available_helper_names",
    "extend_track_descriptors",
    "extend_track_descriptors_for_helpers",
    "helper_target_profile",
    "recognized_helper_names",
    "suggested_helper_source",
]
