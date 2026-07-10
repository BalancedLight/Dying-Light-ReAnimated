"""Runtime paths shared by source and PyInstaller builds."""

from __future__ import annotations

from pathlib import Path
import os
import sys


def is_frozen() -> bool:
    return bool(getattr(sys, "frozen", False))


def resource_root() -> Path:
    """Read-only root containing bundled docs/reference/example assets."""
    bundle = getattr(sys, "_MEIPASS", None)
    if bundle:
        return Path(bundle).resolve()
    return Path(__file__).resolve().parents[1]


def application_root() -> Path:
    """Writable application/project root.

    In a one-folder PyInstaller build this is the folder containing the EXE.
    In source/editable installs it is the repository root.
    """
    if is_frozen():
        return Path(sys.executable).resolve().parent
    return Path(__file__).resolve().parents[1]


def default_user_root() -> Path:
    """Fallback writable root when the executable folder is read-only."""
    local = os.environ.get("LOCALAPPDATA")
    if local:
        return Path(local) / "DL ReAnimated"
    return Path.home() / ".dl_reanimated"


def writable_application_root() -> Path:
    root = application_root()
    try:
        root.mkdir(parents=True, exist_ok=True)
        probe = root / ".dlr_write_probe"
        probe.write_text("ok", encoding="utf-8")
        probe.unlink(missing_ok=True)
        return root
    except OSError:
        fallback = default_user_root()
        fallback.mkdir(parents=True, exist_ok=True)
        return fallback
