"""Deterministic inventories for the helper/all-target mapping UI."""

from __future__ import annotations

from typing import Iterable, Sequence

from .game_profiles import DL1_RIG_REF, DL2_ADVANCED_RIG_REF, DL2_LEGACY_RIG_REF
from .helper_profiles import recognized_helper_names


_DL2_ADVANCED_HELPERS = frozenset(
    {
        "l_sole_helper",
        "r_sole_helper",
        "refcamera",
        "eyecamera",
        "r_handholder",
        "l_shieldholder",
        "l_handholder",
        "headend",
    }
)
_DL2_LEGACY_HELPERS = frozenset(
    {
        "l_iktarget",
        "r_iktarget",
        "l_sole_helper",
        "r_sole_helper",
        "r_handholder",
        "l_handholder",
        "player_shadowcaster",
    }
)


def unique_target_names(values: Iterable[str]) -> tuple[str, ...]:
    rows = tuple(str(value) for value in values)
    if len(rows) != len(set(rows)):
        raise ValueError("Target skeleton contains duplicate bone names")
    return rows


def builtin_helper_target_names(
    target_rig_ref: str,
    target_names: Iterable[str],
) -> tuple[str, ...]:
    names = unique_target_names(target_names)
    ref = str(target_rig_ref or "")
    if ref == DL1_RIG_REF:
        return recognized_helper_names(names)
    recognized = (
        _DL2_ADVANCED_HELPERS
        if ref == DL2_ADVANCED_RIG_REF
        else _DL2_LEGACY_HELPERS
        if ref == DL2_LEGACY_RIG_REF
        else frozenset()
    )
    return tuple(name for name in names if name in recognized)


def visible_extra_target_names(
    target_names: Sequence[str],
    semantic_target_names: Iterable[str],
    *,
    target_rig_ref: str,
    show_helper_bones: bool,
    show_all_target_bones: bool,
) -> tuple[str, ...]:
    names = unique_target_names(target_names)
    semantic = set(str(value) for value in semantic_target_names)
    if show_all_target_bones:
        return tuple(name for name in names if name not in semantic)
    if show_helper_bones:
        return tuple(
            name
            for name in builtin_helper_target_names(target_rig_ref, names)
            if name not in semantic
        )
    return ()


__all__ = [
    "builtin_helper_target_names",
    "unique_target_names",
    "visible_extra_target_names",
]
