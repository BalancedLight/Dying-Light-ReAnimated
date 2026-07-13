"""Declarative facial/mimic target profiles and conservative auto-mapping.

A mimic profile describes non-skeletal ANM2 descriptors whose ``tx`` component
is interpreted by Chrome Engine as a scalar morph weight.  Profiles contain no
game code or geometry and can be shared with custom ``.crig`` targets.
"""

from __future__ import annotations

from dataclasses import dataclass, field
import json
from pathlib import Path
import re
from typing import Any, Iterable, Mapping

from .runtime_paths import resource_root

MIMIC_PROFILE_FORMAT = "dl-reanimated-mimic-profile"
MIMIC_PROFILE_SCHEMA_VERSION = 1
BUILTIN_COMMON46_REF = "builtin:human_common46"


def _descriptor(value: int | str) -> int:
    if isinstance(value, int):
        return value & 0xFFFFFFFF
    text = str(value).strip()
    return int(text, 16) if text.lower().startswith("0x") else int(text)


def _normalize(value: str) -> str:
    text = value.split("\x00", 1)[0].split("::")[-1].split("|")[-1]
    text = re.sub(r"(?i)^(blendshape|shape|morph|bs|face)[_:\- ]+", "", text)
    text = re.sub(r"(?i)(left|_l|\.l)$", "_left", text)
    text = re.sub(r"(?i)(right|_r|\.r)$", "_right", text)
    return re.sub(r"[^a-z0-9]+", "", text.lower())


@dataclass(frozen=True, slots=True)
class MimicTarget:
    index: int
    descriptor: int
    name: str
    label: str
    semantic: str = "morph_scalar_tx"
    component: str = "tx"
    region: str = "unknown"
    side: str = "center"
    aliases: tuple[str, ...] = ()
    neutral: float = 0.0
    recommended_min: float = -1.5
    recommended_max: float = 1.5
    name_status: str = "unresolved"
    confidence: float = 0.0
    tags: tuple[str, ...] = ()

    @classmethod
    def from_dict(cls, row: Mapping[str, Any]) -> "MimicTarget":
        return cls(
            index=int(row["index"]),
            descriptor=_descriptor(row["descriptor"]),
            name=str(row.get("name", f"morph_{_descriptor(row['descriptor']):08X}")),
            label=str(row.get("label", row.get("name", "Unnamed morph"))),
            semantic=str(row.get("semantic", "morph_scalar_tx")),
            component=str(row.get("component", "tx")),
            region=str(row.get("region", "unknown")),
            side=str(row.get("side", "center")),
            aliases=tuple(str(value) for value in row.get("aliases", ())),
            neutral=float(row.get("neutral", 0.0)),
            recommended_min=float(row.get("recommended_min", -1.5)),
            recommended_max=float(row.get("recommended_max", 1.5)),
            name_status=str(row.get("name_status", "unresolved")),
            confidence=float(row.get("confidence", 0.0)),
            tags=tuple(str(value) for value in row.get("tags", ())),
        )

    def to_dict(self) -> dict[str, Any]:
        return {
            "index": self.index,
            "descriptor": f"0x{self.descriptor:08X}",
            "name": self.name,
            "label": self.label,
            "semantic": self.semantic,
            "component": self.component,
            "region": self.region,
            "side": self.side,
            "aliases": list(self.aliases),
            "neutral": self.neutral,
            "recommended_min": self.recommended_min,
            "recommended_max": self.recommended_max,
            "name_status": self.name_status,
            "confidence": self.confidence,
            "tags": list(self.tags),
        }

    @property
    def display_name(self) -> str:
        return f"{self.label}  [0x{self.descriptor:08X}]"

    def candidate_names(self) -> tuple[str, ...]:
        return (self.name, self.label, *self.aliases)


@dataclass(slots=True)
class MimicProfile:
    profile_id: str
    name: str
    targets: tuple[MimicTarget, ...]
    description: str = ""
    author: str = ""
    license: str = ""
    weight_component: str = "tx"
    extensions: dict[str, Any] = field(default_factory=dict)

    @classmethod
    def from_dict(cls, payload: Mapping[str, Any]) -> "MimicProfile":
        if payload.get("format") != MIMIC_PROFILE_FORMAT:
            raise ValueError("not a DL ReAnimated mimic profile")
        version = int(payload.get("schema_version", 0))
        if version > MIMIC_PROFILE_SCHEMA_VERSION:
            raise ValueError(
                f"mimic profile schema {version} is newer than supported schema "
                f"{MIMIC_PROFILE_SCHEMA_VERSION}"
            )
        targets = tuple(sorted(
            (MimicTarget.from_dict(row) for row in payload.get("tracks", ())),
            key=lambda row: row.index,
        ))
        if not targets:
            raise ValueError("mimic profile has no target tracks")
        if [row.index for row in targets] != list(range(len(targets))):
            raise ValueError("mimic profile track indexes must be contiguous and zero-based")
        descriptors = [row.descriptor for row in targets]
        if len(set(descriptors)) != len(descriptors):
            raise ValueError("mimic profile descriptors must be unique")
        if any(row.semantic != "morph_scalar_tx" or row.component != "tx" for row in targets):
            raise ValueError("prototype supports only morph_scalar_tx targets")
        return cls(
            profile_id=str(payload.get("profile_id", "custom:mimic")),
            name=str(payload.get("name", "Custom mimic profile")),
            targets=targets,
            description=str(payload.get("description", "")),
            author=str(payload.get("author", "")),
            license=str(payload.get("license", "")),
            weight_component=str(payload.get("weight_component", "tx")),
            extensions=dict(payload.get("extensions", {})),
        )

    @classmethod
    def load(cls, path: str | Path) -> "MimicProfile":
        return cls.from_dict(json.loads(Path(path).read_text(encoding="utf-8-sig")))

    def to_dict(self) -> dict[str, Any]:
        return {
            "format": MIMIC_PROFILE_FORMAT,
            "schema_version": MIMIC_PROFILE_SCHEMA_VERSION,
            "profile_id": self.profile_id,
            "name": self.name,
            "description": self.description,
            "author": self.author,
            "license": self.license,
            "track_count": len(self.targets),
            "weight_component": self.weight_component,
            "default_components": [0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 1.0, 1.0, 1.0],
            "tracks": [row.to_dict() for row in self.targets],
            "extensions": self.extensions,
        }

    def save(self, path: str | Path) -> Path:
        destination = Path(path)
        destination.parent.mkdir(parents=True, exist_ok=True)
        destination.write_text(json.dumps(self.to_dict(), indent=2, ensure_ascii=False) + "\n", encoding="utf-8")
        return destination

    @property
    def descriptors(self) -> tuple[int, ...]:
        return tuple(row.descriptor for row in self.targets)

    def by_descriptor(self) -> dict[int, MimicTarget]:
        return {row.descriptor: row for row in self.targets}

    def target(self, descriptor: int | str) -> MimicTarget | None:
        return self.by_descriptor().get(_descriptor(descriptor))


@dataclass(frozen=True, slots=True)
class MimicMappingRow:
    source: str
    target_descriptor: int
    weight: float = 1.0
    bias: float = 0.0
    enabled: bool = True
    confidence: float = 1.0
    method: str = "manual"

    @classmethod
    def from_dict(cls, payload: Mapping[str, Any]) -> "MimicMappingRow":
        return cls(
            source=str(payload["source"]),
            target_descriptor=_descriptor(payload["target_descriptor"]),
            weight=float(payload.get("weight", 1.0)),
            bias=float(payload.get("bias", 0.0)),
            enabled=bool(payload.get("enabled", True)),
            confidence=float(payload.get("confidence", 1.0)),
            method=str(payload.get("method", "manual")),
        )

    def to_dict(self) -> dict[str, Any]:
        return {
            "source": self.source,
            "target_descriptor": f"0x{self.target_descriptor:08X}",
            "weight": self.weight,
            "bias": self.bias,
            "enabled": self.enabled,
            "confidence": self.confidence,
            "method": self.method,
        }


def builtin_common46_path() -> Path:
    return resource_root() / "reference" / "mimic_profiles" / "human_common46.dlrmimic.json"


def builtin_common46_profile() -> MimicProfile:
    path = builtin_common46_path()
    if not path.is_file():
        raise FileNotFoundError(
            f"Bundled facial profile is missing: {path}. Re-apply the mimic prototype patch."
        )
    return MimicProfile.load(path)


def resolve_mimic_profile(project: Any) -> MimicProfile | None:
    """Resolve the project's target facial profile without hard-coding a mesh family."""

    extensions = getattr(project.rig, "extensions", {}) or {}
    ref = str(extensions.get("mimic_profile_ref", "auto") or "auto")
    path = str(extensions.get("mimic_profile_path", "") or "").strip()
    if ref == "none":
        return None
    if ref == "custom" and path:
        candidate = Path(path)
        if candidate.is_file():
            return MimicProfile.load(candidate)

    # Projects embed a copy of a selected custom profile so reopening or moving a
    # project does not depend on the original file still being present. The path
    # remains useful as an editable source, while this payload is the portable fallback.
    embedded_project_profile = extensions.get("mimic_profile_embedded")
    if ref == "custom" and isinstance(embedded_project_profile, Mapping):
        try:
            return MimicProfile.from_dict(embedded_project_profile)
        except (ValueError, KeyError, TypeError):
            if ref == "custom" and not path:
                raise

    if ref == "custom":
        candidate = Path(path) if path else None
        raise FileNotFoundError(
            f"Custom mimic profile does not exist and no embedded project copy is available: "
            f"{candidate or '(no path)'}"
        )

    # Custom .crig packages can embed a declarative profile in their extensions.
    target_rig_path = str(getattr(project.rig, "target_rig_path", "") or "")
    if target_rig_path and Path(target_rig_path).is_file():
        try:
            from .chrome_rig import ChromeRig

            rig = ChromeRig.load(target_rig_path)
            embedded = rig.extensions.get("mimic_profile")
            if isinstance(embedded, Mapping):
                return MimicProfile.from_dict(embedded)
        except (OSError, ValueError, KeyError, TypeError):
            pass

    target_ref = str(getattr(project.rig, "target_rig_ref", ""))
    if ref in {"auto", BUILTIN_COMMON46_REF} and (
        target_ref == "builtin:male_npc_infected" or ref == BUILTIN_COMMON46_REF
    ):
        return builtin_common46_profile()
    return None


def _alias_index(profile: MimicProfile) -> dict[str, list[MimicTarget]]:
    index: dict[str, list[MimicTarget]] = {}
    for target in profile.targets:
        for value in target.candidate_names():
            normalized = _normalize(value)
            if normalized:
                bucket = index.setdefault(normalized, [])
                if all(existing.descriptor != target.descriptor for existing in bucket):
                    bucket.append(target)
    return index


def auto_map_shapes(
    source_names: Iterable[str],
    profile: MimicProfile,
) -> list[MimicMappingRow]:
    """Conservatively map common FBX/ARKit/viseme names to profile descriptors.

    Multiple source curves may target the same Dying Light morph, which is the
    intended consolidation path when a source face has more controls than the
    destination.  Ambiguous names remain unmapped instead of being guessed.
    """

    alias_index = _alias_index(profile)
    result: list[MimicMappingRow] = []
    used_sources: set[str] = set()

    # Explicit semantic aliases not always present verbatim in stock names.
    semantic_aliases = {
        "eyeblinkleft": "morph_l_u_lid",
        "eyeblinkright": "morph_r_u_lid",
        "leftblink": "morph_l_u_lid",
        "rightblink": "morph_r_u_lid",
        "jawopen": "morph_jaw_open",
        "mouthopen": "morph_jaw_open",
        "visemeaa": "morph_jaw_open",
        "mouthsmileleft": "morph_lips_L_smile",
        "mouthsmileright": "morph_lips_R_smile",
        "mouthdimpleleft": "morph_lips_L_dimple",
        "mouthdimpleright": "morph_lips_R_dimple",
        "mouthfunnel": "morph_lips_funnel",
        "mouthpucker": "morph_lips_funnel",
        "mouthupperupleft": "morph_lips_U_up",
        "mouthupperupright": "morph_lips_U_up",
        "mouthlowerdownleft": "morph_lips_B_down",
        "mouthlowerdownright": "morph_lips_B_down",
        "visemepbm": "pbm",
        "visemefv": "fv",
        "visemew": "w",
        "visemewide": "wide",
        "visemeopen": "open",
    }
    by_name = {_normalize(target.name): target for target in profile.targets}

    for source in source_names:
        normalized = _normalize(source)
        candidates = alias_index.get(normalized, [])
        method = "exact_alias"
        confidence = 1.0
        semantic_target = None
        if normalized in semantic_aliases:
            semantic_target = by_name.get(_normalize(semantic_aliases[normalized]))
        if semantic_target is not None and len(candidates) != 1:
            # Common interchange names such as mouthOpen can legitimately appear
            # as aliases for both a speech target and a jaw target. Prefer the
            # explicit semantic rule rather than dropping an otherwise useful map.
            candidates = [semantic_target]
            method = "semantic_disambiguation"
            confidence = 0.92
        elif not candidates and semantic_target is not None:
            candidates = [semantic_target]
            method = "semantic_alias"
            confidence = 0.92
        if len(candidates) != 1:
            continue
        target = candidates[0]
        result.append(MimicMappingRow(
            source=source,
            target_descriptor=target.descriptor,
            weight=1.0,
            bias=0.0,
            enabled=True,
            confidence=confidence,
            method=method,
        ))
        used_sources.add(source)

    # Eye blinks benefit from a small lower-lid contribution.  This is an
    # intentional one-to-many default and stays editable in the mapping dialog.
    by_source = {row.source: row for row in result}
    for source, primary in list(by_source.items()):
        normalized = _normalize(source)
        if normalized not in {"eyeblinkleft", "leftblink", "eyeblinkright", "rightblink"}:
            continue
        lower_name = "morph_l_b_lid" if "left" in normalized else "morph_r_b_lid"
        lower = by_name.get(_normalize(lower_name))
        if lower is not None:
            result.append(MimicMappingRow(
                source=source,
                target_descriptor=lower.descriptor,
                weight=0.65,
                confidence=0.78,
                method="blink_lower_lid_companion",
            ))
    return result


def mapping_from_payload(rows: Iterable[Mapping[str, Any]]) -> list[MimicMappingRow]:
    return [MimicMappingRow.from_dict(row) for row in rows]


__all__ = [
    "BUILTIN_COMMON46_REF",
    "MIMIC_PROFILE_FORMAT",
    "MIMIC_PROFILE_SCHEMA_VERSION",
    "MimicMappingRow",
    "MimicProfile",
    "MimicTarget",
    "auto_map_shapes",
    "builtin_common46_path",
    "builtin_common46_profile",
    "mapping_from_payload",
    "resolve_mimic_profile",
]
