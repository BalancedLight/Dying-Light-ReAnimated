"""Versioned generic source-to-target bone maps for reverse conversion."""

from __future__ import annotations

from dataclasses import asdict, dataclass, field
import json
import os
from pathlib import Path
import re
import tempfile
from typing import Any, Iterable, Mapping
import uuid

from .chrome_rig import ChromeRig
from .trackmap import dl_name_hash


BONE_MAP_FORMAT = "dl-reanimated-bone-map"
BONE_MAP_SCHEMA_VERSION = 1
BONE_MAP_EXTENSION = ".dlrbmap.json"


def normalize_bone_name(value: str) -> str:
    value = value.rsplit(":", 1)[-1]
    value = re.sub(r"([a-z0-9])([A-Z])", r"\1_\2", value)
    value = re.sub(r"[^a-zA-Z0-9]+", "_", value).strip("_").lower()
    replacements = {
        "left": "l", "right": "r", "upperarm": "upper_arm",
        "lowerarm": "forearm", "lower_arm": "forearm",
        "upperleg": "thigh", "lowerleg": "calf",
    }
    for old, new in replacements.items():
        value = value.replace(old, new)
    return re.sub(r"_+", "_", value)


@dataclass(slots=True)
class BoneMapPair:
    source_descriptor: int
    source_bone: str
    target_bone: str
    confidence: float = 1.0
    method: str = "manual"


@dataclass(slots=True)
class GenericBoneMap:
    profile_id: str
    name: str
    source_skeleton_hash: str
    target_skeleton_hash: str
    source_rig_ref: str = ""
    pairs: list[BoneMapPair] = field(default_factory=list)
    extensions: dict[str, Any] = field(default_factory=dict)
    format: str = BONE_MAP_FORMAT
    schema_version: int = BONE_MAP_SCHEMA_VERSION

    @classmethod
    def create(
        cls, name: str, source_hash: str, target_hash: str, *, source_rig_ref: str = ""
    ) -> "GenericBoneMap":
        return cls(str(uuid.uuid4()), name, source_hash, target_hash, source_rig_ref)

    def validate(self) -> list[str]:
        errors: list[str] = []
        if self.format != BONE_MAP_FORMAT or self.schema_version != BONE_MAP_SCHEMA_VERSION:
            errors.append("Unsupported generic bone-map format or schema version.")
        targets = [row.target_bone for row in self.pairs if row.target_bone]
        if len(targets) != len(set(targets)):
            errors.append("A target bone may only be assigned once.")
        descriptors = [row.source_descriptor for row in self.pairs]
        if len(descriptors) != len(set(descriptors)):
            errors.append("A source descriptor may only be mapped once.")
        return errors

    def to_dict(self) -> dict[str, Any]:
        return asdict(self)

    @classmethod
    def from_dict(cls, payload: Mapping[str, Any]) -> "GenericBoneMap":
        if payload.get("format") != BONE_MAP_FORMAT:
            raise ValueError("Not a DL ReAnimated generic bone-map file.")
        if int(payload.get("schema_version", 0)) != BONE_MAP_SCHEMA_VERSION:
            raise ValueError("Unsupported generic bone-map schema version.")
        allowed = set(cls.__dataclass_fields__)
        unknown = {key: value for key, value in payload.items() if key not in allowed}
        extensions = dict(payload.get("extensions", {}))
        if unknown:
            extensions.setdefault("unknown_fields", {}).update(unknown)
        result = cls(
            profile_id=str(payload.get("profile_id") or uuid.uuid4()),
            name=str(payload.get("name", "Generic bone map")),
            source_skeleton_hash=str(payload.get("source_skeleton_hash", "")),
            target_skeleton_hash=str(payload.get("target_skeleton_hash", "")),
            source_rig_ref=str(payload.get("source_rig_ref", "")),
            pairs=[BoneMapPair(**dict(row)) for row in payload.get("pairs", [])],
            extensions=extensions,
        )
        errors = result.validate()
        if errors:
            raise ValueError("Invalid generic bone map:\n- " + "\n- ".join(errors))
        return result

    def save(self, path: str | Path) -> Path:
        destination = Path(path)
        if not destination.name.lower().endswith(BONE_MAP_EXTENSION):
            destination = destination.with_name(destination.name + BONE_MAP_EXTENSION)
        destination.parent.mkdir(parents=True, exist_ok=True)
        text = json.dumps(self.to_dict(), indent=2, ensure_ascii=False) + "\n"
        handle, temporary = tempfile.mkstemp(
            prefix=destination.name + ".", suffix=".tmp", dir=destination.parent
        )
        try:
            with os.fdopen(handle, "w", encoding="utf-8", newline="\n") as stream:
                stream.write(text)
                stream.flush()
                os.fsync(stream.fileno())
            os.replace(temporary, destination)
        finally:
            if os.path.exists(temporary):
                os.unlink(temporary)
        return destination

    @classmethod
    def load(cls, path: str | Path) -> "GenericBoneMap":
        return cls.from_dict(json.loads(Path(path).read_text(encoding="utf-8")))


def skeleton_signature(rows: Iterable[tuple[str, str | None]]) -> str:
    import hashlib
    canonical = "\n".join(f"{name}|{parent or ''}" for name, parent in rows)
    return hashlib.sha256(canonical.encode("utf-8")).hexdigest()


def auto_map_skeletons(
    source_rig: ChromeRig,
    target_names: Iterable[str],
    target_parents: Mapping[str, str | None],
    *,
    target_skeleton_hash: str = "",
) -> GenericBoneMap:
    """Conservatively suggest a one-to-one generic map.

    Exact descriptor/name/alias matches are preferred. Structural/token guesses are
    accepted only when unique and sufficiently stronger than the next candidate.
    """

    names = list(target_names)
    normalized = {name: normalize_bone_name(name) for name in names}
    profile = GenericBoneMap.create(
        f"{source_rig.name} to target skeleton",
        source_rig.skeleton_hash,
        target_skeleton_hash or skeleton_signature((n, target_parents.get(n)) for n in names),
        source_rig_ref=source_rig.rig_id,
    )
    used: set[str] = set()
    source_by_name = {bone.name: bone for bone in source_rig.bones}

    def source_parent_name(bone) -> str | None:
        return source_rig.bones[bone.parent_index].name if bone.parent_index >= 0 else None

    for bone in source_rig.bones:
        candidates: list[tuple[float, str, str]] = []
        source_normal = normalize_bone_name(bone.name)
        aliases = {source_normal, *(normalize_bone_name(v) for v in bone.aliases)}
        source_parent = source_parent_name(bone)
        mapped_parent = next(
            (row.target_bone for row in profile.pairs if row.source_bone == source_parent), None
        )
        for target in names:
            if target in used:
                continue
            target_normal = normalized[target]
            score = 0.0
            method = "heuristic"
            if dl_name_hash(target) == bone.descriptor:
                score, method = 1.0, "descriptor"
            elif target == bone.name:
                score, method = 0.99, "exact"
            elif target_normal in aliases:
                score, method = 0.96, "normalized"
            elif any(
                target_normal.endswith("_" + alias) or alias.endswith("_" + target_normal)
                for alias in aliases if alias
            ):
                score, method = 0.86, "normalized_suffix"
            else:
                left = set(source_normal.split("_"))
                right = set(target_normal.split("_"))
                overlap = len(left & right)
                score = 0.35 + min(0.35, overlap * 0.12) if overlap else 0.0
                if mapped_parent and target_parents.get(target) == mapped_parent:
                    score += 0.18
                source_children = sum(1 for row in source_rig.bones if row.parent_index == bone.index)
                target_children = sum(1 for row in names if target_parents.get(row) == target)
                if source_children == target_children:
                    score += 0.08
            candidates.append((min(score, 1.0), target, method))
        candidates.sort(reverse=True)
        if not candidates:
            continue
        best = candidates[0]
        runner_up = candidates[1][0] if len(candidates) > 1 else 0.0
        if best[0] >= 0.85 or (best[0] >= 0.68 and best[0] - runner_up >= 0.12):
            profile.pairs.append(
                BoneMapPair(bone.descriptor, bone.name, best[1], best[0], best[2])
            )
            used.add(best[1])
    return profile


__all__ = [
    "BONE_MAP_EXTENSION", "BONE_MAP_FORMAT", "BONE_MAP_SCHEMA_VERSION",
    "BoneMapPair", "GenericBoneMap", "auto_map_skeletons", "normalize_bone_name",
    "skeleton_signature",
]
