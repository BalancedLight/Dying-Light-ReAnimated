"""GUI-facing resolution of project-default and per-animation rig targets."""

from __future__ import annotations

from dataclasses import dataclass
from typing import Any, Mapping

from .game_profiles import get_game_profile


@dataclass(frozen=True, slots=True)
class AnimationTargetSelection:
    rig_ref: str
    rig_path: str
    retarget_mode: str
    inherited: bool


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

    inherited = not bool(animation.target_rig_ref or animation.target_rig_path)
    rig_ref = str(animation.target_rig_ref or project.rig.target_rig_ref)
    rig_path = str(
        animation.target_rig_path
        or (
            project.rig.target_rig_path
            if not animation.target_rig_ref
            or animation.target_rig_ref == project.rig.target_rig_ref
            else ""
        )
        or dict(rig_paths or {}).get(rig_ref, "")
    )
    game_target = get_game_profile(project.game_id).target_rig_ref
    retarget_mode = (
        "humanoid"
        if project.rig.retarget_mode == "humanoid"
        and rig_ref == game_target
        and not animation.target_rig_path
        else "exact"
    )
    return AnimationTargetSelection(rig_ref, rig_path, retarget_mode, inherited)


__all__ = ["AnimationTargetSelection", "resolve_animation_target"]
