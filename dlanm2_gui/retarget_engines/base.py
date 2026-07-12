from __future__ import annotations
from dataclasses import dataclass
from typing import Any
@dataclass(slots=True)
class RetargetBuild:
 payload: bytes
 frame_count: int
 report: dict[str,Any]
