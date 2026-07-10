"""Animation-script target presets used by the release GUI and project files.

The value that ultimately matters to RP6L is the `_ANIMATION_SCR_` resource
name.  Presets are conveniences only: the GUI always leaves the resource name
editable so projects are not locked to the small built-in list.
"""

from __future__ import annotations

from dataclasses import asdict, dataclass
import json
from pathlib import Path
from typing import Iterable


@dataclass(frozen=True, slots=True)
class AnimationScriptTarget:
    target_id: str
    display_name: str
    resource_name: str
    description: str
    mode: str = "additive"
    family: str = "custom"
    default_pack_name: str = "common_anims_sp_pc.rpack"

    def to_dict(self) -> dict[str, str]:
        return asdict(self)


BUILTIN_SCRIPT_TARGETS: tuple[AnimationScriptTarget, ...] = (
    AnimationScriptTarget(
        target_id="npc_male_dlc60",
        display_name="Male NPC / infected (DLC60 additive)",
        resource_name="anims_man_all_DLC60",
        description=(
            "Known-working additive animation-script resource used by the "
            "editor test packs. Recommended for new standalone packs."
        ),
        mode="additive",
        family="npc_male",
    ),
    AnimationScriptTarget(
        target_id="npc_male_base",
        display_name="Male NPC / infected (base override)",
        resource_name="anims_man_all",
        description=(
            "Overrides/imports into the base male NPC animation script. Use "
            "append mode with a preserved script whenever possible."
        ),
        mode="override",
        family="npc_male",
    ),
    AnimationScriptTarget(
        target_id="player_male",
        display_name="Male player (DLC60 additive)",
        resource_name="anims_player_dlc60",
        description=(
            "Male player DLC60 animation script. The target rig/template must also "
            "match the player skeleton selected in the project."
        ),
        mode="additive",
        family="player_male",
    ),
    AnimationScriptTarget(
        target_id="npc_female",
        display_name="Female NPC",
        resource_name="anims_woman_all",
        description=(
            "Female NPC animation script. Select a compatible female target "
            "rig or mapping profile before export."
        ),
        mode="override",
        family="npc_female",
    ),
)


class ScriptTargetRegistry:
    """Small extensible registry with JSON user-preset support."""

    def __init__(self, targets: Iterable[AnimationScriptTarget] = ()) -> None:
        self._targets: dict[str, AnimationScriptTarget] = {
            target.target_id: target for target in BUILTIN_SCRIPT_TARGETS
        }
        for target in targets:
            self._targets[target.target_id] = target

    @property
    def targets(self) -> tuple[AnimationScriptTarget, ...]:
        return tuple(self._targets.values())

    def by_id(self, target_id: str) -> AnimationScriptTarget | None:
        return self._targets.get(target_id)

    def by_resource_name(self, resource_name: str) -> AnimationScriptTarget | None:
        lowered = resource_name.strip().lower()
        return next(
            (
                target
                for target in self._targets.values()
                if target.resource_name.lower() == lowered
            ),
            None,
        )

    def resolve_resource_name(self, value: str) -> str:
        target = self.by_id(value)
        return target.resource_name if target is not None else value.strip()

    def add(self, target: AnimationScriptTarget) -> None:
        if not target.target_id.strip():
            raise ValueError("script target id cannot be empty")
        if not target.resource_name.strip():
            raise ValueError("animation script resource name cannot be empty")
        self._targets[target.target_id] = target

    @classmethod
    def load(cls, path: str | Path) -> "ScriptTargetRegistry":
        source = Path(path)
        if not source.exists():
            return cls()
        payload = json.loads(source.read_text(encoding="utf-8"))
        rows = payload.get("targets", payload) if isinstance(payload, dict) else payload
        if not isinstance(rows, list):
            raise ValueError("script target file must contain a list of targets")
        targets: list[AnimationScriptTarget] = []
        for row in rows:
            if not isinstance(row, dict):
                raise ValueError("each script target must be an object")
            targets.append(AnimationScriptTarget(**row))
        return cls(targets)

    def save_user_targets(self, path: str | Path) -> Path:
        destination = Path(path)
        destination.parent.mkdir(parents=True, exist_ok=True)
        builtins = {target.target_id for target in BUILTIN_SCRIPT_TARGETS}
        rows = [
            target.to_dict()
            for target in self._targets.values()
            if target.target_id not in builtins
        ]
        destination.write_text(
            json.dumps({"schema_version": 1, "targets": rows}, indent=2) + "\n",
            encoding="utf-8",
        )
        return destination


DEFAULT_SCRIPT_TARGET_ID = "npc_male_dlc60"


__all__ = [
    "AnimationScriptTarget",
    "BUILTIN_SCRIPT_TARGETS",
    "DEFAULT_SCRIPT_TARGET_ID",
    "ScriptTargetRegistry",
]
