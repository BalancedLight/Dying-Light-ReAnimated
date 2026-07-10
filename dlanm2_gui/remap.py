from __future__ import annotations

import hashlib
import re
from dataclasses import dataclass
from typing import Iterable, Sequence

from .models import BoneMapping, BoneRemapProfile, TargetSkeleton


PREFIXES = (
    "mixamorig",
    "bip001",
    "bip01",
    "armature",
    "skeleton",
    "def",
    "jnt",
    "bone",
)

SYNONYMS = {
    "hips": "pelvis",
    "hip": "pelvis",
    "root": "pelvis",
    "spine1": "spine",
    "spine2": "chest",
    "spine3": "upperchest",
    "upperarm": "arm",
    "forearm": "lowerarm",
    "calf": "lowerleg",
    "thigh": "upperleg",
    "toe": "toes",
}


@dataclass(slots=True)
class Candidate:
    target: str
    confidence: float
    method: str


def source_hash_for_bones(bones: Iterable[str]) -> str:
    joined = "\n".join(bones)
    return hashlib.sha256(joined.encode("utf-8")).hexdigest()


def normalize_bone_name(name: str) -> str:
    value = re.sub(r"([a-z0-9])([A-Z])", r"\1_\2", name).lower()
    value = value.replace("#", "")
    value = re.sub(r"[^a-z0-9]+", "_", value)
    parts = [part for part in value.split("_") if part]
    parts = [part for part in parts if part not in PREFIXES]
    side = ""
    stripped: list[str] = []
    for part in parts:
        if part in {"left", "l"}:
            side = "l"
            continue
        if part in {"right", "r"}:
            side = "r"
            continue
        stripped.append(SYNONYMS.get(part, part))
    normalized = "_".join(stripped)
    if side:
        normalized = f"{normalized}_{side}" if normalized else side
    return re.sub(r"_+", "_", normalized).strip("_")


def side_of(name: str) -> str:
    normalized = normalize_bone_name(name)
    if normalized.endswith("_l") or normalized == "l":
        return "left"
    if normalized.endswith("_r") or normalized == "r":
        return "right"
    return ""


def auto_map_bones(source_bones: Sequence[str], target: TargetSkeleton) -> BoneRemapProfile:
    target_names = target.bone_names
    target_by_normal = {normalize_bone_name(name): name for name in target_names}
    exact_lookup = {name.lower(): name for name in target_names}

    mappings: list[BoneMapping] = []
    ignored: list[str] = []
    for source in source_bones:
        candidate = _best_candidate(source, exact_lookup, target_by_normal, target_names)
        if candidate:
            mappings.append(
                BoneMapping(
                    source_bone=source,
                    target_bone=candidate.target,
                    confidence=candidate.confidence,
                    method=candidate.method,
                )
            )
        else:
            ignored.append(source)
    profile = BoneRemapProfile(
        source_hash=source_hash_for_bones(source_bones),
        target_skeleton_id=target.skeleton_id,
        mappings=mappings,
        ignored_tracks=ignored,
    )
    if ignored:
        profile.notes.append(f"{len(ignored)} source tracks were not mapped automatically.")
    return profile


def _best_candidate(
    source: str,
    exact_lookup: dict[str, str],
    target_by_normal: dict[str, str],
    target_names: Sequence[str],
) -> Candidate | None:
    if source.lower() in exact_lookup:
        return Candidate(exact_lookup[source.lower()], 1.0, "exact")

    normalized = normalize_bone_name(source)
    if normalized in target_by_normal:
        return Candidate(target_by_normal[normalized], 0.92, "normalized")

    source_side = side_of(source)
    source_tokens = set(normalized.split("_"))
    best: Candidate | None = None
    for target in target_names:
        target_normal = normalize_bone_name(target)
        target_tokens = set(target_normal.split("_"))
        overlap = len(source_tokens & target_tokens)
        if overlap == 0:
            continue
        confidence = min(0.82, 0.45 + overlap * 0.12)
        if source_side and source_side != side_of(target):
            confidence -= 0.25
        if confidence < 0.5:
            continue
        if best is None or confidence > best.confidence:
            best = Candidate(target, confidence, "token")
    return best
