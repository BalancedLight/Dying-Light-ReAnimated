from __future__ import annotations

import struct
import zipfile
from dataclasses import dataclass
from pathlib import Path

from .models import Bone, TargetSkeleton, identity_matrix
from .paths import DEFAULT_DATA0_PAK, DEFAULT_GAME_ROOT


HUMANOID_TEMPLATE_BONES = [
    "pelvis",
    "spine",
    "spine_1",
    "spine_2",
    "neck",
    "head",
    "clavicle_l",
    "upperarm_l",
    "lowerarm_l",
    "hand_l",
    "clavicle_r",
    "upperarm_r",
    "lowerarm_r",
    "hand_r",
    "upperleg_l",
    "lowerleg_l",
    "foot_l",
    "toes_l",
    "upperleg_r",
    "lowerleg_r",
    "foot_r",
    "toes_r",
]


@dataclass(frozen=True, slots=True)
class BuiltinSkeletonSpec:
    skeleton_id: str
    family: str
    display_name: str
    candidates: tuple[str, ...]


BUILTIN_SPECS = (
    BuiltinSkeletonSpec(
        "player_fpp",
        "player",
        "Player FPP",
        (
            "data/characters/heroes/player_man_01_fpp/player_man_01_fpp.chr",
            "data/characters/heroes/player_1_fpp/player_1_fpp.chr",
        ),
    ),
    BuiltinSkeletonSpec(
        "player_tpp",
        "player",
        "Player TPP",
        (
            "data/characters/heroes/player_man_01_tpp/player_man_01_tpp.chr",
            "data/characters/heroes/player_zombie_01_tpp/player_zombie_tpp.chr",
        ),
    ),
    BuiltinSkeletonSpec(
        "npc_zombie",
        "npc_zombie",
        "NPC / Zombie",
        (
            "data/characters/men/survivor_a/survivor_a.chr",
            "data/characters/men/zombie_man_a/zombie_man_a.chr",
            "data/characters/woman/survivor_woman_a/survivor_woman_a.chr",
        ),
    ),
)


def load_builtin_skeletons(game_root: str | Path = DEFAULT_GAME_ROOT) -> list[TargetSkeleton]:
    root = Path(game_root)
    paks = _candidate_paks(root)
    skeletons: list[TargetSkeleton] = []
    for spec in BUILTIN_SPECS:
        skeleton = _load_spec_from_paks(spec, paks)
        if skeleton is None:
            skeleton = _fallback_skeleton(spec)
        skeletons.append(skeleton)
    return skeletons


def read_chr_bytes(data: bytes, name: str) -> TargetSkeleton:
    structured = _try_read_structured_chr(data)
    if structured:
        bones = [Bone(name=bone_name, parent_index=-1) for bone_name in structured]
        return TargetSkeleton(name, "custom", name, bones=bones)
    names = _heuristic_chr_names(data)
    bones = [Bone(name=bone_name, parent_index=-1) for bone_name in names]
    return TargetSkeleton(name, "custom", name, bones=bones, warnings=["CHR parsed by heuristic string extraction."])


def _candidate_paks(root: Path) -> list[Path]:
    preferred = [root / "DW" / "Data0.pak"]
    preferred.extend(sorted((root / "DW").glob("*.pak")) if (root / "DW").exists() else [])
    seen: set[Path] = set()
    result: list[Path] = []
    for pak in preferred:
        if pak.exists() and pak not in seen:
            seen.add(pak)
            result.append(pak)
    return result


def _load_spec_from_paks(spec: BuiltinSkeletonSpec, paks: list[Path]) -> TargetSkeleton | None:
    for pak in paks:
        try:
            with zipfile.ZipFile(pak) as archive:
                by_lower = {name.lower(): name for name in archive.namelist()}
                for candidate in spec.candidates:
                    actual = by_lower.get(candidate.lower())
                    if not actual:
                        continue
                    data = archive.read(actual)
                    parsed = read_chr_bytes(data, spec.skeleton_id)
                    parsed.family = spec.family
                    parsed.display_name = spec.display_name
                    parsed.source_asset_path = f"{pak}!{actual}"
                    if not parsed.bones:
                        parsed.warnings.append("No bones were found in CHR; using template fallback.")
                        return _fallback_skeleton(spec, parsed.source_asset_path, parsed.warnings)
                    return parsed
        except zipfile.BadZipFile:
            continue
    return None


def _fallback_skeleton(
    spec: BuiltinSkeletonSpec,
    source_asset_path: str = "builtin:humanoid_template",
    warnings: list[str] | None = None,
) -> TargetSkeleton:
    bones = [Bone(name=name, parent_index=index - 1 if index else -1, rest_matrix=identity_matrix()) for index, name in enumerate(HUMANOID_TEMPLATE_BONES)]
    all_warnings = list(warnings or [])
    all_warnings.append("Using unverified humanoid template because stock CHR bone order was not resolved.")
    return TargetSkeleton(
        skeleton_id=spec.skeleton_id,
        family=spec.family,
        display_name=spec.display_name,
        bones=bones,
        source_asset_path=source_asset_path,
        warnings=all_warnings,
    )


def _try_read_structured_chr(data: bytes) -> list[str]:
    if len(data) < 6:
        return []
    version, object_count = struct.unpack_from("<HH", data, 0)
    if version != 4 or object_count <= 0 or object_count > 10000:
        return []
    offset = 6
    names: list[str] = []
    for index in range(object_count):
        if offset + 2 > len(data):
            return []
        length = struct.unpack_from("<H", data, offset)[0]
        offset += 2
        if length > 256 or offset + length > len(data):
            return []
        value = data[offset : offset + length].decode("ascii", errors="ignore").strip()
        offset += length
        names.append(value or f"object_{index:03d}")
    return names


def _heuristic_chr_names(data: bytes) -> list[str]:
    names: list[str] = []
    start = -1
    for index in range(len(data) + 1):
        printable = index < len(data) and 32 <= data[index] <= 126
        if printable and start < 0:
            start = index
        elif not printable and start >= 0:
            length = index - start
            if 2 <= length <= 96:
                text = data[start:index].decode("ascii", errors="ignore").strip()
                if _looks_like_bone_name(text) and text.lower() not in {name.lower() for name in names}:
                    names.append(text)
            start = -1
    return names


def _looks_like_bone_name(value: str) -> bool:
    if not value or "/" in value or "\\" in value:
        return False
    if "." in value and "_" not in value:
        return False
    return any(ch.isalpha() for ch in value) and all(ch.isalnum() or ch in "_-.:#" for ch in value)
