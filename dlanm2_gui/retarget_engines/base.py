"""Shared retarget-engine types."""

from __future__ import annotations

from dataclasses import dataclass
from typing import Any


@dataclass(frozen=True, slots=True)
class RetargetBuild:
    payload: bytes
    frame_count: int
    report: dict[str, Any]


__all__ = ["RetargetBuild"]
