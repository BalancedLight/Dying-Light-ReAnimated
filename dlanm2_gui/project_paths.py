from __future__ import annotations

import os
import warnings
from pathlib import Path
from typing import Iterable


ENV_ROOT = "DL_REANIMATED_ROOT"
ENV_COMMON_ANIMS_PC = "DL_REANIMATED_COMMON_ANIMS_PC"
ENV_EXTERNAL_DUMP = "DL_REANIMATED_EXTERNAL_DUMP"
ENV_WORKSHOP_DATA = "DL_REANIMATED_WORKSHOP_DATA"


def _env_path(name: str) -> Path | None:
    value = os.environ.get(name)
    if not value:
        return None
    return Path(value).expanduser()


def repo_root() -> Path:
    return (_env_path(ENV_ROOT) or Path(__file__).resolve().parents[1]).resolve()


def common_anims_pc_dir() -> Path:
    return _env_path(ENV_COMMON_ANIMS_PC) or repo_root() / "common_anims_PC"


def normalized_dir() -> Path:
    return repo_root() / "normalized"


def exports_dir() -> Path:
    return repo_root() / "exports"


def reverse_notes_dir() -> Path:
    return repo_root() / "docs" / "reverse_notes"


def optional_external_dump_dir() -> Path | None:
    return _env_path(ENV_EXTERNAL_DUMP)


def external_dump_path(*parts: str) -> Path:
    subpath = Path(*parts) if parts else Path()
    return (optional_external_dump_dir() or repo_root() / "external") / subpath


def workshop_data_dir() -> Path | None:
    return _env_path(ENV_WORKSHOP_DATA)


def common_anim_path(name: str) -> Path:
    return common_anims_pc_dir() / "Animation" / name


def animation_scr_dir(name: str | None = None) -> Path:
    root = common_anims_pc_dir() / "AnimationScr"
    return root / name if name else root


def dump_rp6_exe() -> Path:
    external = optional_external_dump_dir()
    if external is not None:
        candidates = [
            external / "DumpRP6.exe",
            external / "RP6Dumper" / "DumpRP6.exe",
        ]
        found = resolve_existing_path(candidates)
        if found is not None:
            return found
        return candidates[0]
    return repo_root() / "external" / "RP6Dumper" / "DumpRP6.exe"


def data0_pak_path() -> Path:
    external = optional_external_dump_dir()
    if external is not None:
        candidates = [
            external / "DW" / "Data0.pak",
            external / "Data0.pak",
        ]
        found = resolve_existing_path(candidates)
        if found is not None:
            return found
        return candidates[0]
    return repo_root() / "external" / "Dying Light" / "DW" / "Data0.pak"


def resolve_existing_path(
    candidates: Iterable[str | Path | None],
    *,
    env_var: str | None = None,
    label: str = "path",
    required: bool = False,
    warn: bool = False,
) -> Path | None:
    paths: list[Path] = []
    if env_var:
        env_path = _env_path(env_var)
        if env_path is not None:
            paths.append(env_path)
    paths.extend(Path(candidate) for candidate in candidates if candidate is not None)

    for path in paths:
        if path.exists():
            return path

    if required:
        checked = ", ".join(str(path) for path in paths) or "<none>"
        raise FileNotFoundError(f"Required {label} was not found. Checked: {checked}")
    if warn and paths:
        checked = ", ".join(str(path) for path in paths)
        warnings.warn(f"Optional {label} was not found. Checked: {checked}", stacklevel=2)
    return None
