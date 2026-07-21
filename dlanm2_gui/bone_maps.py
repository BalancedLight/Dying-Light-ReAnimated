"""Versioned generic target-rig-to-source-FBX bone maps.

Schema v2 serializes explicit target/source field names and deterministically
migrates the historically reversed schema-v1 row names. A source FBX bone may
legitimately drive several target rows while each target descriptor/name stays
unique.
"""

from __future__ import annotations

from dataclasses import asdict, dataclass, field, fields
import json
import math
import os
from pathlib import Path
import re
import tempfile
from typing import Any, Iterable, Mapping
import uuid

from .chrome_rig import ChromeRig
from .trackmap import dl_name_hash


BONE_MAP_FORMAT = "dl-reanimated-bone-map"
BONE_MAP_SCHEMA_VERSION = 2
BONE_MAP_EXTENSION = ".dlrbmap.json"
MAPPING_PROFILE_ORIGINS = frozenset(
    {
        "automatic_identity",
        "automatic_repair",
        "automatic_verified",
        "manually_reviewed",
        "imported_profile",
    }
)

MAPPING_KINDS = ("bone", "helper_override")
TRANSFER_POLICIES = (
    "default",
    "rest_relative",
    "rotation_delta",
    "global_bind_basis",
    "copy_local",
    "bind",
)
COMPONENT_POLICIES = (
    "rotation",
    "translation",
    "rotation_translation",
    "scale",
    "full_transform",
)
REVIEW_STATES = (
    "automatic_unreviewed",
    "automatic_accepted",
    "manually_reviewed",
    "imported_reviewed",
    "intentionally_unmapped",
)


def _default_review_state(method: str, source_fbx_bone: str) -> str:
    """Return the deterministic review state for a newly-created v2 row."""

    if not source_fbx_bone:
        return "automatic_unreviewed"
    normalized = str(method or "").casefold()
    if normalized == "manual" or normalized.startswith("manual:"):
        return "manually_reviewed"
    if normalized in {"descriptor", "exact", "exact_or_subset"}:
        return "automatic_accepted"
    return "automatic_unreviewed"


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


@dataclass(slots=True, init=False)
class BoneMapPair:
    """One explicit target-rig <- source-FBX mapping row.

    Schema v1 serialized these values under the historically reversed names
    ``source_descriptor``/``source_bone``/``target_bone``. Schema v2 writes
    only the unambiguous names below. Compatibility properties and constructor
    keywords keep existing Python callers working while call sites migrate.
    """

    target_rig_descriptor: int
    target_rig_bone: str
    source_fbx_bone: str
    confidence: float = 1.0
    method: str = "manual"
    transfer_policy: str = "default"
    component_policy: str = "full_transform"
    mapping_kind: str = "bone"
    review_state: str = "manually_reviewed"
    notes: str = ""
    extensions: dict[str, Any] = field(default_factory=dict)

    def __init__(
        self,
        target_rig_descriptor: int | None = None,
        target_rig_bone: str | None = None,
        source_fbx_bone: str | None = None,
        confidence: float = 1.0,
        method: str = "manual",
        transfer_policy: str = "default",
        component_policy: str = "full_transform",
        mapping_kind: str = "bone",
        review_state: str = "",
        notes: str = "",
        extensions: Mapping[str, Any] | None = None,
        **legacy: Any,
    ) -> None:
        if target_rig_descriptor is None and "source_descriptor" in legacy:
            target_rig_descriptor = int(legacy.pop("source_descriptor"))
        if target_rig_bone is None and "source_bone" in legacy:
            target_rig_bone = str(legacy.pop("source_bone"))
        if source_fbx_bone is None and "target_bone" in legacy:
            source_fbx_bone = str(legacy.pop("target_bone"))
        if legacy:
            names = ", ".join(sorted(legacy))
            raise TypeError(f"Unexpected BoneMapPair argument(s): {names}")
        self.target_rig_descriptor = int(target_rig_descriptor or 0)
        self.target_rig_bone = str(target_rig_bone or "")
        self.source_fbx_bone = str(source_fbx_bone or "")
        self.confidence = float(confidence)
        self.method = str(method or "manual")
        self.transfer_policy = str(transfer_policy or "default")
        self.component_policy = str(component_policy or "full_transform")
        self.mapping_kind = str(mapping_kind or "bone")
        self.review_state = str(
            review_state or _default_review_state(self.method, self.source_fbx_bone)
        )
        self.notes = str(notes or "")
        self.extensions = dict(extensions or {})

    @property
    def source_descriptor(self) -> int:
        """Schema-v1 compatibility alias for ``target_rig_descriptor``."""

        return self.target_rig_descriptor

    @source_descriptor.setter
    def source_descriptor(self, value: int) -> None:
        self.target_rig_descriptor = int(value)

    @property
    def source_bone(self) -> str:
        """Schema-v1 compatibility alias for ``target_rig_bone``."""

        return self.target_rig_bone

    @source_bone.setter
    def source_bone(self, value: str) -> None:
        self.target_rig_bone = str(value)

    @property
    def target_bone(self) -> str:
        """Schema-v1 compatibility alias for ``source_fbx_bone``."""

        return self.source_fbx_bone

    @target_bone.setter
    def target_bone(self, value: str) -> None:
        self.source_fbx_bone = str(value)

    @classmethod
    def from_dict(
        cls,
        payload: Mapping[str, Any],
        *,
        schema_version: int = BONE_MAP_SCHEMA_VERSION,
        migrated_review_state: str = "",
    ) -> "BoneMapPair":
        row = dict(payload)
        allowed = {
            *(item.name for item in fields(cls)),
            "source_descriptor",
            "source_bone",
            "target_bone",
        }
        unknown = {key: value for key, value in row.items() if key not in allowed}
        extensions = dict(row.get("extensions", {}) or {})
        if unknown:
            extensions.setdefault("unknown_fields", {}).update(unknown)
        if schema_version <= 1:
            descriptor = row.get("source_descriptor", row.get("target_rig_descriptor", 0))
            target_rig_bone = row.get("source_bone", row.get("target_rig_bone", ""))
            source_fbx_bone = row.get("target_bone", row.get("source_fbx_bone", ""))
        else:
            descriptor = row.get("target_rig_descriptor", row.get("source_descriptor", 0))
            target_rig_bone = row.get("target_rig_bone", row.get("source_bone", ""))
            source_fbx_bone = row.get("source_fbx_bone", row.get("target_bone", ""))
        return cls(
            target_rig_descriptor=int(descriptor or 0),
            target_rig_bone=str(target_rig_bone or ""),
            source_fbx_bone=str(source_fbx_bone or ""),
            confidence=float(row.get("confidence", 1.0)),
            method=str(row.get("method", "manual")),
            transfer_policy=str(row.get("transfer_policy", "default") or "default"),
            component_policy=str(
                row.get("component_policy", "full_transform") or "full_transform"
            ),
            mapping_kind=str(row.get("mapping_kind", "bone") or "bone"),
            review_state=str(row.get("review_state", "") or migrated_review_state),
            notes=str(row.get("notes", "") or ""),
            extensions=extensions,
        )


@dataclass(slots=True)
class GenericBoneMap:
    profile_id: str
    name: str
    source_skeleton_hash: str
    target_skeleton_hash: str
    source_rig_ref: str = ""
    # Full target .crig fingerprint. In schema v1 the same value lived under
    # the historically reversed ``source_skeleton_hash`` name.
    target_bind_hash: str = ""
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
        result = cls(
            str(uuid.uuid4()),
            name,
            source_hash,
            target_hash,
            source_rig_ref,
            source_hash,
        )
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

        target_rig_names = [
            row.target_rig_bone for row in self.pairs if row.target_rig_bone
        ]
        target_descriptors = [row.target_rig_descriptor for row in self.pairs]
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
        invalid_reviews = sorted(
            {
                row.review_state
                for row in self.pairs
                if row.review_state not in REVIEW_STATES
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
        if invalid_reviews:
            errors.append(
                "Unsupported mapping review state(s): " + ", ".join(invalid_reviews)
            )
        for row in self.pairs:
            if not row.target_rig_bone:
                errors.append("Every mapping row needs a target rig bone.")
            if not math.isfinite(row.confidence) or not 0.0 <= row.confidence <= 1.0:
                errors.append(
                    f"Mapping confidence for {row.target_rig_bone!r} must be between 0 and 1."
                )
            if row.review_state == "intentionally_unmapped":
                if row.source_fbx_bone:
                    errors.append(
                        f"Intentionally unmapped target {row.target_rig_bone!r} cannot "
                        "name a source FBX bone."
                    )
                if row.transfer_policy != "bind":
                    errors.append(
                        f"Intentionally unmapped target {row.target_rig_bone!r} must "
                        "use bind transfer."
                    )
        return errors

    def to_dict(self) -> dict[str, Any]:
        return asdict(self)

    @classmethod
    def from_dict(cls, payload: Mapping[str, Any]) -> "GenericBoneMap":
        if payload.get("format") != BONE_MAP_FORMAT:
            raise ValueError("Not a DL ReAnimated generic bone-map file.")
        schema_version = int(payload.get("schema_version", 0))
        if schema_version not in {1, BONE_MAP_SCHEMA_VERSION}:
            raise ValueError("Unsupported generic bone-map schema version.")
        allowed = {item.name for item in fields(cls)}
        unknown = {key: value for key, value in payload.items() if key not in allowed}
        extensions = dict(payload.get("extensions", {}) or {})
        if unknown:
            extensions.setdefault("unknown_fields", {}).update(unknown)
        declared_origin = str(extensions.get("origin", "") or "")
        raw_pairs = [dict(row) for row in payload.get("pairs", [])]
        if declared_origin not in MAPPING_PROFILE_ORIGINS:
            methods = {
                str(row.get("method", "") or "").casefold() for row in raw_pairs
            }
            if raw_pairs and all(
                method == "manual" or method.startswith("manual:")
                for method in methods
            ):
                declared_origin = "manually_reviewed"
            elif "auto_map_summary" in extensions or any(
                method.startswith("semantic:")
                or method
                in {
                    "descriptor",
                    "exact",
                    "normalized",
                    "normalized_suffix",
                    "heuristic",
                }
                for method in methods
            ):
                declared_origin = "automatic_repair"
            else:
                declared_origin = "imported_profile"
            extensions["origin"] = declared_origin
        migrated_review = {
            "automatic_identity": "automatic_accepted",
            "automatic_repair": "automatic_unreviewed",
            "automatic_verified": "automatic_accepted",
            "manually_reviewed": "manually_reviewed",
            "imported_profile": "imported_reviewed",
        }[declared_origin]
        source_skeleton_hash = str(payload.get("source_skeleton_hash", ""))
        result = cls(
            profile_id=str(payload.get("profile_id") or uuid.uuid4()),
            name=str(payload.get("name", "Generic bone map")),
            source_skeleton_hash=source_skeleton_hash,
            target_skeleton_hash=str(payload.get("target_skeleton_hash", "")),
            source_rig_ref=str(payload.get("source_rig_ref", "")),
            target_bind_hash=str(
                payload.get("target_bind_hash", "") or source_skeleton_hash
            ),
            pairs=[
                BoneMapPair.from_dict(
                    row,
                    schema_version=schema_version,
                    migrated_review_state=migrated_review,
                )
                for row in raw_pairs
            ],
            extensions=extensions,
            schema_version=BONE_MAP_SCHEMA_VERSION,
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
        # ``from_dict`` performs the schema migration and preserves the
        # declared origin/review state. Loading a reviewed v2 map must not
        # silently turn its rows into imported-profile rows.
        return cls.from_dict(json.loads(Path(path).read_text(encoding="utf-8-sig")))


def mapping_profile_origin(profile: GenericBoneMap | None) -> str:
    """Return a safe origin for new and pre-origin map payloads."""

    if profile is None:
        return "none"
    declared = str(profile.extensions.get("origin", "") or "")
    if declared in MAPPING_PROFILE_ORIGINS:
        return declared
    certificate = profile.extensions.get("automatic_retarget_certificate")
    if (
        isinstance(certificate, Mapping)
        and certificate.get("format") == "dl2_advanced_body_bridge_v1"
        and certificate.get("status") == "pass"
    ):
        # This is only an origin classification. Solver routing must still
        # recompute and validate the certificate against live source/target
        # inputs before it can authorize an incompatible automatic map.
        return "automatic_verified"
    states = {row.review_state for row in profile.pairs}
    if states and states <= {"manually_reviewed", "intentionally_unmapped"}:
        return "manually_reviewed"
    if states and states <= {"imported_reviewed", "intentionally_unmapped"}:
        return "imported_profile"
    if states and states <= {"automatic_accepted", "intentionally_unmapped"}:
        return "automatic_identity"
    if "automatic_unreviewed" in states:
        return "automatic_repair"
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
    for row in profile.pairs:
        if row.review_state == "intentionally_unmapped":
            continue
        if value == "manually_reviewed":
            row.review_state = "manually_reviewed"
        elif value == "imported_profile":
            row.review_state = "imported_reviewed"
        elif value == "automatic_identity" and row.review_state == "automatic_unreviewed":
            row.review_state = "automatic_accepted"
        elif value == "automatic_verified":
            row.review_state = "automatic_accepted"
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
            (
                row.source_fbx_bone
                for row in profile.pairs
                if row.target_rig_bone == source_parent
            ),
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
    "REVIEW_STATES",
    "TRANSFER_POLICIES",
    "BoneMapPair",
    "GenericBoneMap",
    "auto_map_skeletons",
    "mapping_profile_origin",
    "normalize_bone_name",
    "set_mapping_profile_origin",
    "skeleton_signature",
]
