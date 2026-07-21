"""GUI-facing resolution of project-default and per-animation rig targets."""

from __future__ import annotations

from dataclasses import dataclass
from enum import Enum
from typing import Any, Mapping

from .game_profiles import get_game_profile


@dataclass(frozen=True, slots=True)
class AnimationTargetSelection:
    rig_ref: str
    rig_path: str
    retarget_mode: str
    inherited: bool


class RetargetUiKind(Enum):
    BUILTIN_HUMANOID = "builtin_humanoid"
    CUSTOM_CRIG = "custom_crig"
    UNKNOWN = "unknown"


def _deliberate_expert_crig_override(project: Any, animation: Any) -> bool:
    values = (
        getattr(animation, "extensions", {}).get("expert_crig_mapping_override")
        if animation is not None
        else None,
        getattr(getattr(project, "rig", None), "extensions", {}).get(
            "expert_crig_mapping_override"
        ),
        getattr(getattr(project, "rig", None), "extensions", {}).get(
            "expert_solver_override"
        ),
    )
    for value in values:
        if not isinstance(value, Mapping):
            continue
        deliberate = value.get("deliberate") is True
        exposes_crig = value.get("expose_crig_mapping") is True or str(
            value.get("ui_kind", "")
        ) == RetargetUiKind.CUSTOM_CRIG.value
        if deliberate and exposes_crig:
            return True
    return False


def resolve_animation_target(
    project: Any,
    animation: Any,
    *,
    rig_paths: Mapping[str, str] | None = None,
) -> AnimationTargetSelection:
    """Resolve one clip exactly as the project builder's target routing does.

    Empty clip fields inherit the project target. An explicit custom reference
    may resolve through the installed-rig inventory supplied by the GUI.
    """

    animation_ref = str(getattr(animation, "target_rig_ref", "") or "")
    animation_path = str(getattr(animation, "target_rig_path", "") or "")
    inherited = not bool(animation_ref or animation_path)
    rig_ref = str(animation_ref or project.rig.target_rig_ref)
    rig_path = str(
        animation_path
        or (
            project.rig.target_rig_path
            if not animation_ref
            or animation_ref == project.rig.target_rig_ref
            else ""
        )
        or dict(rig_paths or {}).get(rig_ref, "")
    )
    profile = get_game_profile(project.game_id)
    built_in_for_game = rig_ref in profile.compatible_builtin_rig_refs
    project_mode = str(project.rig.retarget_mode or "auto")
    if project_mode == "auto" and built_in_for_game:
        retarget_mode = "auto"
    elif (
        project_mode == "humanoid"
        and rig_ref == profile.default_target_rig_ref
        and not animation_path
    ):
        # Historical projects remain readable even though newly selected
        # built-in targets are stored as Auto.
        retarget_mode = "humanoid"
    else:
        retarget_mode = "exact"
    return AnimationTargetSelection(rig_ref, rig_path, retarget_mode, inherited)


def retarget_ui_kind(
    project: Any,
    animation: Any,
    *,
    rig_paths: Mapping[str, str] | None = None,
    selection: AnimationTargetSelection | None = None,
) -> RetargetUiKind:
    """Classify the editor by target ownership, never by solver selection."""

    selection = selection or resolve_animation_target(
        project, animation, rig_paths=rig_paths
    )
    if not selection.rig_ref and not selection.rig_path:
        return RetargetUiKind.UNKNOWN
    profile = get_game_profile(project.game_id)
    if selection.rig_ref in profile.compatible_builtin_rig_refs:
        if _deliberate_expert_crig_override(project, animation):
            return RetargetUiKind.CUSTOM_CRIG
        return RetargetUiKind.BUILTIN_HUMANOID
    return RetargetUiKind.CUSTOM_CRIG


__all__ = [
    "AnimationTargetSelection",
    "RetargetUiKind",
    "resolve_animation_target",
    "retarget_ui_kind",
]
