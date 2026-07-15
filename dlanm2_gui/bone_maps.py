"""Versioned generic target-rig-to-source-FBX bone maps.

The serialized field names predate the current UI and are intentionally kept
for compatibility.  In every :class:`BoneMapPair`:

``source_descriptor`` / ``source_bone``
    Identify the target ``.crig`` track.

``target_bone``
    Identifies the source FBX bone that drives that target track.

Consequently, a source FBX bone may legitimately appear in several rows while
the target descriptor/name must remain unique.
"""

from __future__ import annotations

from dataclasses import asdict, dataclass, field, fields
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
MAPPING_PROFILE_ORIGINS = frozenset(
    {"automatic_identity", "automatic_repair", "manually_reviewed", "imported_profile"}
)

MAPPING_KINDS = ("bone", "helper_override")
TRANSFER_POLICIES = (
    "default",
    "rest_relative",
    "rotation_delta",
    "global_bind_basis",
    "copy_local",
)
COMPONENT_POLICIES = (
    "rotation",
    "translation",
    "rotation_translation",
    "scale",
    "full_transform",
)


def normalize_bone_name(value: str) -> str:
    value = value.rsplit(":", 1)[-1]
    value = re.sub(r"([a-z0-9])([A-Z])", r"\1_\2", value)
    value = re.sub(r"[^a-zA-Z0-9]+", "_", value).strip("_").lower()
    for old, new in {
        "left": "l",
        "right": "r",
        "upperarm": "upper_arm",
        "lowerarm": "forearm",
        "lower_arm": "forearm",
        "upperleg": "thigh",
        "lowerleg": "calf",
    }.items():
        value = value.replace(old, new)
    return re.sub(r"_+", "_", value)


@dataclass(slots=True)
class BoneMapPair:
    # Historical names: source_* is the target .crig track; target_bone is the
    # source FBX bone.  Do not swap these serialized meanings.
    source_descriptor: int
    source_bone: str
    target_bone: str
    confidence: float = 1.0
    method: str = "manual"
    transfer_policy: str = "default"
    component_policy: str = "full_transform"
    mapping_kind: str = "bone"
    extensions: dict[str, Any] = field(default_factory=dict)

    @property
    def target_rig_bone(self) -> str:
        return self.source_bone

    @property
    def source_fbx_bone(self) -> str:
        return self.target_bone

    @classmethod
    def from_dict(cls, payload: Mapping[str, Any]) -> "BoneMapPair":
        row = dict(payload)
        allowed = {item.name for item in fields(cls)}
        unknown = {key: value for key, value in row.items() if key not in allowed}
        extensions = dict(row.get("extensions", {}) or {})
        if unknown:
            extensions.setdefault("unknown_fields", {}).update(unknown)
        return cls(
            source_descriptor=int(row.get("source_descriptor", 0)),
            source_bone=str(row.get("source_bone", "")),
            target_bone=str(row.get("target_bone", "")),
            confidence=float(row.get("confidence", 1.0)),
            method=str(row.get("method", "manual")),
            transfer_policy=str(row.get("transfer_policy", "default") or "default"),
            component_policy=str(
                row.get("component_policy", "full_transform") or "full_transform"
            ),
            mapping_kind=str(row.get("mapping_kind", "bone") or "bone"),
            extensions=extensions,
        )


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
        cls,
        name: str,
        source_hash: str,
        target_hash: str,
        *,
        source_rig_ref: str = "",
        origin: str = "",
    ) -> "GenericBoneMap":
        result = cls(str(uuid.uuid4()), name, source_hash, target_hash, source_rig_ref)
        if origin:
            set_mapping_profile_origin(result, origin)
        return result

    @property
    def base_pairs(self) -> list[BoneMapPair]:
        return [row for row in self.pairs if row.mapping_kind != "helper_override"]

    @property
    def helper_pairs(self) -> list[BoneMapPair]:
        return [row for row in self.pairs if row.mapping_kind == "helper_override"]

    def validate(self) -> list[str]:
        errors: list[str] = []
        if self.format != BONE_MAP_FORMAT or self.schema_version != BONE_MAP_SCHEMA_VERSION:
            errors.append("Unsupported generic bone-map format or schema version.")

        target_rig_names = [row.source_bone for row in self.pairs if row.source_bone]
        target_descriptors = [row.source_descriptor for row in self.pairs]
        if len(target_rig_names) != len(set(target_rig_names)):
            errors.append("A target rig bone may only be assigned once.")
        if len(target_descriptors) != len(set(target_descriptors)):
            errors.append("A target rig descriptor may only be mapped once.")

        invalid_kinds = sorted(
            {row.mapping_kind for row in self.pairs if row.mapping_kind not in MAPPING_KINDS}
        )
        invalid_transfers = sorted(
            {
                row.transfer_policy
                for row in self.pairs
                if row.transfer_policy not in TRANSFER_POLICIES
            }
        )
        invalid_components = sorted(
            {
                row.component_policy
                for row in self.pairs
                if row.component_policy not in COMPONENT_POLICIES
            }
        )
        if invalid_kinds:
            errors.append("Unsupported mapping kind(s): " + ", ".join(invalid_kinds))
        if invalid_transfers:
            errors.append("Unsupported transfer policy/policies: " + ", ".join(invalid_transfers))
        if invalid_components:
            errors.append(
                "Unsupported component policy/policies: " + ", ".join(invalid_components)
            )
        return errors

    def to_dict(self) -> dict[str, Any]:
        return asdict(self)

    @classmethod
    def from_dict(cls, payload: Mapping[str, Any]) -> "GenericBoneMap":
        if payload.get("format") != BONE_MAP_FORMAT:
            raise ValueError("Not a DL ReAnimated generic bone-map file.")
        if int(payload.get("schema_version", 0)) != BONE_MAP_SCHEMA_VERSION:
            raise ValueError("Unsupported generic bone-map schema version.")
        allowed = {item.name for item in fields(cls)}
        unknown = {key: value for key, value in payload.items() if key not in allowed}
        extensions = dict(payload.get("extensions", {}) or {})
        if unknown:
            extensions.setdefault("unknown_fields", {}).update(unknown)
        result = cls(
            str(payload.get("profile_id") or uuid.uuid4()),
            str(payload.get("name", "Generic bone map")),
            str(payload.get("source_skeleton_hash", "")),
            str(payload.get("target_skeleton_hash", "")),
            str(payload.get("source_rig_ref", "")),
            [BoneMapPair.from_dict(dict(row)) for row in payload.get("pairs", [])],
            extensions,
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
        result = cls.from_dict(json.loads(Path(path).read_text(encoding="utf-8-sig")))
        set_mapping_profile_origin(result, "imported_profile")
        return result


def mapping_profile_origin(profile: GenericBoneMap | None) -> str:
    """Return a safe origin for new and pre-origin map payloads."""

    if profile is None:
        return "none"
    declared = str(profile.extensions.get("origin", "") or "")
    if declared in MAPPING_PROFILE_ORIGINS:
        return declared
    methods = {str(row.method or "").casefold() for row in profile.pairs}
    if profile.pairs and all(
        method == "manual" or method.startswith("manual:") for method in methods
    ):
        return "manually_reviewed"
    automatic_methods = {
        "descriptor", "exact", "normalized", "normalized_suffix", "heuristic"
    }
    if "auto_map_summary" in profile.extensions or any(
        method.startswith("semantic:") or method in automatic_methods for method in methods
    ):
        return "automatic_repair"
    # A legacy profile without auto-map fingerprints most likely entered via
    # an explicit file import; preserve that deliberate user action.
    return "imported_profile"


def set_mapping_profile_origin(
    profile: GenericBoneMap, origin: str
) -> GenericBoneMap:
    value = str(origin)
    if value not in MAPPING_PROFILE_ORIGINS:
        raise ValueError(f"Unsupported mapping profile origin {value!r}")
    profile.extensions["origin"] = value
    return profile


def skeleton_signature(rows: Iterable[tuple[str, str | None]]) -> str:
    import hashlib

    return hashlib.sha256(
        "\n".join(f"{name}|{parent or ''}" for name, parent in rows).encode()
    ).hexdigest()


def auto_map_skeletons(
    source_rig: ChromeRig,
    target_names: Iterable[str],
    target_parents: Mapping[str, str | None],
    *,
    target_skeleton_hash: str = "",
) -> GenericBoneMap:
    names = list(target_names)
    normalized = {name: normalize_bone_name(name) for name in names}
    profile = GenericBoneMap.create(
        f"{source_rig.name} to target skeleton",
        source_rig.skeleton_hash,
        target_skeleton_hash
        or skeleton_signature((name, target_parents.get(name)) for name in names),
        source_rig_ref=source_rig.rig_id,
    )
    used: set[str] = set()

    def parent_name(bone: Any) -> str | None:
        return source_rig.bones[bone.parent_index].name if bone.parent_index >= 0 else None

    for bone in source_rig.bones:
        candidates: list[tuple[float, str, str]] = []
        source_normal = normalize_bone_name(bone.name)
        aliases = {
            source_normal,
            *(normalize_bone_name(value) for value in bone.aliases),
        }
        source_parent = parent_name(bone)
        mapped_parent = next(
            (row.target_bone for row in profile.pairs if row.source_bone == source_parent),
            None,
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
                target_normal.endswith("_" + alias)
                or alias.endswith("_" + target_normal)
                for alias in aliases
                if alias
            ):
                score, method = 0.86, "normalized_suffix"
            else:
                overlap = len(set(source_normal.split("_")) & set(target_normal.split("_")))
                score = 0.35 + min(0.35, overlap * 0.12) if overlap else 0.0
                if mapped_parent and target_parents.get(target) == mapped_parent:
                    score += 0.18
                if sum(
                    1 for row in source_rig.bones if row.parent_index == bone.index
                ) == sum(1 for row in names if target_parents.get(row) == target):
                    score += 0.08
            candidates.append((min(score, 1.0), target, method))
        candidates.sort(reverse=True)
        if not candidates:
            continue
        best = candidates[0]
        runner = candidates[1][0] if len(candidates) > 1 else 0.0
        if best[0] >= 0.85 or (best[0] >= 0.68 and best[0] - runner >= 0.12):
            profile.pairs.append(
                BoneMapPair(bone.descriptor, bone.name, best[1], best[0], best[2])
            )
            used.add(best[1])
    return profile


__all__ = [
    "BONE_MAP_EXTENSION",
    "BONE_MAP_FORMAT",
    "BONE_MAP_SCHEMA_VERSION",
    "COMPONENT_POLICIES",
    "MAPPING_PROFILE_ORIGINS",
    "MAPPING_KINDS",
    "TRANSFER_POLICIES",
    "BoneMapPair",
    "GenericBoneMap",
    "auto_map_skeletons",
    "mapping_profile_origin",
    "normalize_bone_name",
    "set_mapping_profile_origin",
    "skeleton_signature",
]
