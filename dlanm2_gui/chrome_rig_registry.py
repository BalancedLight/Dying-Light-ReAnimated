"""Installed Chrome Rig discovery and safe import."""

from __future__ import annotations

from dataclasses import dataclass
import os
from pathlib import Path
import re
import shutil
import tempfile

from .chrome_rig import CRIG_EXTENSION, ChromeRig


BUILTIN_MALE_RIG_REF = "builtin:male_npc_infected"


@dataclass(frozen=True, slots=True)
class ChromeRigRecord:
    rig_ref: str
    display_name: str
    category: str
    path: str
    builtin: bool = False


class ChromeRigRegistry:
    def __init__(self, root: str | Path):
        self.root = Path(root)

    def records(self) -> list[ChromeRigRecord]:
        rows = [
            ChromeRigRecord(
                BUILTIN_MALE_RIG_REF,
                "Dying Light Male NPC / Infected (bundled)",
                "Humanoid",
                "",
                True,
            )
        ]
        if self.root.is_dir():
            for path in sorted(self.root.glob(f"*{CRIG_EXTENSION}"), key=lambda p: p.name.lower()):
                try:
                    rig = ChromeRig.load(path)
                except (OSError, ValueError):
                    continue
                rows.append(
                    ChromeRigRecord(rig.rig_id, rig.name, rig.category, str(path.resolve()))
                )
        return rows

    def import_rig(self, source: str | Path) -> ChromeRigRecord:
        source_path = Path(source)
        rig = ChromeRig.load(source_path)
        self.root.mkdir(parents=True, exist_ok=True)
        slug = re.sub(r"[^A-Za-z0-9_.-]+", "_", rig.name).strip("._") or "custom_rig"
        identity = re.sub(r"[^A-Fa-f0-9]+", "", rig.rig_id)[-8:] or rig.skeleton_hash[:8]
        destination = self.root / f"{slug}-{identity.lower()}{CRIG_EXTENSION}"
        handle, temporary = tempfile.mkstemp(
            prefix=destination.name + ".", suffix=".tmp", dir=destination.parent
        )
        os.close(handle)
        try:
            shutil.copyfile(source_path, temporary)
            ChromeRig.load(temporary)
            os.replace(temporary, destination)
        finally:
            if os.path.exists(temporary):
                os.unlink(temporary)
        return ChromeRigRecord(rig.rig_id, rig.name, rig.category, str(destination.resolve()))

    def resolve(self, rig_ref: str, explicit_path: str = "") -> Path | None:
        if rig_ref == BUILTIN_MALE_RIG_REF:
            return None
        if explicit_path:
            path = Path(explicit_path)
            if path.is_file():
                return path
        for row in self.records():
            if row.rig_ref == rig_ref and row.path:
                return Path(row.path)
        return None


__all__ = ["BUILTIN_MALE_RIG_REF", "ChromeRigRecord", "ChromeRigRegistry"]
