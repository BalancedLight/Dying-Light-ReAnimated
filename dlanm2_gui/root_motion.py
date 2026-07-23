"""Target-neutral root-motion and heading selections.

Projects historically serialize ``inplace``, ``bip01`` and ``motion``.  Those
values remain a stable file/API compatibility layer; runtime code resolves them
to target-neutral motion and heading ownership before touching target tracks.
"""

from __future__ import annotations

from dataclasses import asdict, dataclass
from enum import Enum
from typing import Any, Mapping


class RootMotionMode(str, Enum):
    IN_PLACE = "inplace"
    SKELETAL_ROOT = "skeletal_root"
    MOTION_ACCUMULATOR = "motion_accumulator"


class RootHeadingMode(str, Enum):
    LOCK_INITIAL = "lock_initial"
    PRESERVE = "preserve"
    TO_MOTION_ACCUMULATOR = "to_motion_accumulator"


LEGACY_ROOT_POLICIES = ("inplace", "bip01", "motion")
ROOT_MOTION_EXTENSION_KEY = "root_motion_v2"


@dataclass(frozen=True, slots=True)
class RootMotionSelection:
    source_root_bone: str = ""
    target_root_bone: str = ""
    motion_mode: str = RootMotionMode.IN_PLACE.value
    heading_mode: str = RootHeadingMode.LOCK_INITIAL.value

    def __post_init__(self) -> None:
        RootMotionMode(self.motion_mode)
        RootHeadingMode(self.heading_mode)
        if self.motion_mode == RootMotionMode.IN_PLACE.value and self.heading_mode != RootHeadingMode.LOCK_INITIAL.value:
            raise ValueError("In-place root motion must lock accumulated heading")
        if (
            self.motion_mode == RootMotionMode.MOTION_ACCUMULATOR.value
            and self.heading_mode != RootHeadingMode.TO_MOTION_ACCUMULATOR.value
        ):
            raise ValueError("Motion-accumulator mode must transfer heading to the accumulator")

    @classmethod
    def from_legacy_policy(
        cls,
        policy: str,
        *,
        source_root_bone: str = "",
        target_root_bone: str = "",
        heading_mode: str | None = None,
    ) -> "RootMotionSelection":
        value = str(policy or "inplace")
        mapping = {
            "inplace": (
                RootMotionMode.IN_PLACE.value,
                RootHeadingMode.LOCK_INITIAL.value,
            ),
            "bip01": (
                RootMotionMode.SKELETAL_ROOT.value,
                RootHeadingMode.PRESERVE.value,
            ),
            "motion": (
                RootMotionMode.MOTION_ACCUMULATOR.value,
                RootHeadingMode.TO_MOTION_ACCUMULATOR.value,
            ),
            RootMotionMode.SKELETAL_ROOT.value: (
                RootMotionMode.SKELETAL_ROOT.value,
                RootHeadingMode.PRESERVE.value,
            ),
            RootMotionMode.MOTION_ACCUMULATOR.value: (
                RootMotionMode.MOTION_ACCUMULATOR.value,
                RootHeadingMode.TO_MOTION_ACCUMULATOR.value,
            ),
        }
        try:
            motion, default_heading = mapping[value]
        except KeyError as exc:
            raise ValueError(f"Unsupported root-motion policy {value!r}") from exc
        return cls(
            str(source_root_bone or ""),
            str(target_root_bone or ""),
            motion,
            str(heading_mode or default_heading),
        )

    @classmethod
    def from_dict(
        cls,
        payload: Mapping[str, Any] | None,
        *,
        legacy_policy: str = "inplace",
        source_root_bone: str = "",
        target_root_bone: str = "",
    ) -> "RootMotionSelection":
        row = dict(payload or {})
        if row.get("motion_mode"):
            motion_mode = str(row["motion_mode"])
            default_heading = {
                RootMotionMode.IN_PLACE.value: RootHeadingMode.LOCK_INITIAL.value,
                RootMotionMode.SKELETAL_ROOT.value: RootHeadingMode.PRESERVE.value,
                RootMotionMode.MOTION_ACCUMULATOR.value: RootHeadingMode.TO_MOTION_ACCUMULATOR.value,
            }.get(motion_mode, RootHeadingMode.PRESERVE.value)
            return cls(
                str(row.get("source_root_bone", source_root_bone) or ""),
                str(row.get("target_root_bone", target_root_bone) or ""),
                motion_mode,
                str(row.get("heading_mode") or default_heading),
            )
        return cls.from_legacy_policy(
            legacy_policy,
            source_root_bone=str(row.get("source_root_bone", source_root_bone) or ""),
            target_root_bone=str(row.get("target_root_bone", target_root_bone) or ""),
            heading_mode=str(row.get("heading_mode", "") or "") or None,
        )

    @classmethod
    def from_animation(cls, animation: Any) -> "RootMotionSelection":
        extensions = dict(getattr(animation, "extensions", {}) or {})
        payload = extensions.get(ROOT_MOTION_EXTENSION_KEY, {})
        if not isinstance(payload, Mapping):
            payload = {}
        return cls.from_dict(
            payload,
            legacy_policy=str(getattr(animation, "root_policy", "inplace") or "inplace"),
            source_root_bone=str(getattr(animation, "source_root_bone", "") or ""),
            target_root_bone=str(getattr(animation, "target_root_bone", "") or ""),
        )

    @property
    def legacy_serialized_policy(self) -> str:
        if self.motion_mode == RootMotionMode.IN_PLACE.value:
            return "inplace"
        if self.motion_mode == RootMotionMode.MOTION_ACCUMULATOR.value:
            return "motion"
        return "bip01"

    def to_dict(self) -> dict[str, str]:
        return asdict(self)

    def store(self, animation: Any) -> None:
        extensions = dict(getattr(animation, "extensions", {}) or {})
        extensions[ROOT_MOTION_EXTENSION_KEY] = self.to_dict()
        animation.extensions = extensions
        if hasattr(animation, "root_policy"):
            animation.root_policy = self.legacy_serialized_policy
        if hasattr(animation, "source_root_bone"):
            animation.source_root_bone = self.source_root_bone
        if hasattr(animation, "target_root_bone"):
            animation.target_root_bone = self.target_root_bone


def resolve_root_motion_selection(
    value: RootMotionSelection | Mapping[str, Any] | str,
    *,
    source_root_bone: str = "",
    target_root_bone: str = "",
    heading_mode: str | None = None,
) -> RootMotionSelection:
    if isinstance(value, RootMotionSelection):
        return value
    if isinstance(value, Mapping):
        return RootMotionSelection.from_dict(
            value,
            source_root_bone=source_root_bone,
            target_root_bone=target_root_bone,
        )
    return RootMotionSelection.from_legacy_policy(
        str(value),
        source_root_bone=source_root_bone,
        target_root_bone=target_root_bone,
        heading_mode=heading_mode,
    )


__all__ = [
    "LEGACY_ROOT_POLICIES",
    "ROOT_MOTION_EXTENSION_KEY",
    "RootHeadingMode",
    "RootMotionMode",
    "RootMotionSelection",
    "resolve_root_motion_selection",
]
