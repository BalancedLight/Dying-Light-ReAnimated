"""Conservative source-family hints and skeleton archetype routing."""

from __future__ import annotations

from dataclasses import dataclass
import re
from typing import Any, Iterable, Mapping

from .semantic_roles import normalize_bone_name


ARCHETYPE_CLASSIFIER_VERSION = "dlr-skeleton-archetypes-v1"


@dataclass(frozen=True, slots=True)
class ArchetypeClassification:
    archetype: str
    confidence: float
    evidence: tuple[str, ...]
    rejected_routes: tuple[str, ...] = ()

    def to_dict(self) -> dict[str, Any]:
        return {
            "archetype": self.archetype,
            "confidence": self.confidence,
            "evidence": list(self.evidence),
            "rejected_routes": list(self.rejected_routes),
            "classifier_version": ARCHETYPE_CLASSIFIER_VERSION,
        }


def detect_source_family_hints(
    names: Iterable[str], wrapper_names: Iterable[str] = ()
) -> tuple[str, ...]:
    """Return ordered vendor/DCC hints without selecting a retarget pipeline.

    These hints only contribute evidence.  All recognized families still pass
    through the same topology, bind, role, and chain analysis.
    """

    original = tuple(str(value) for value in names)
    wrappers = tuple(str(value) for value in wrapper_names)
    folded = tuple(value.casefold() for value in (*original, *wrappers))
    leaves = tuple(normalize_bone_name(value).comparison_name for value in original)
    joined = "\n".join(folded)
    leaf_set = set(leaves)
    hints: list[str] = []

    def add(value: str) -> None:
        if value not in hints:
            hints.append(value)

    if any("mixamorig:" in value for value in folded):
        add("Mixamo")
    if any(
        re.search(r"(^|[|:])(?:def|org|mch)[-_:]", value)
        or "rigify" in value
        for value in folded
    ):
        add("Blender Rigify")
    if any(
        "auto rig pro" in value or "auto_rig_pro" in value or "arp_" in value
        or "c_root_master" in value
        for value in folded
    ):
        add("Auto-Rig Pro")
    if any("humanik" in value or re.search(r"(^|[|:])hik[_:]", value) for value in folded):
        add("Maya HumanIK")
    if any(re.search(r"(^|[|:])bip\s*0*1(?:\D|$)", value) for value in folded):
        add("3ds Max Biped")
    if any(
        "catparent" in value or "catrig" in value or re.search(r"(^|[|:])cat[_:]", value)
        for value in folded
    ):
        add("3ds Max CAT")
    unreal_markers = {
        "pelvis", "spine 01", "spine 02", "clavicle l", "clavicle r",
        "upperarm l", "upperarm r", "thigh l", "thigh r",
    }
    if len(unreal_markers & leaf_set) >= 6 or "ue4 mannequin" in joined or "ue5 mannequin" in joined:
        add("Unreal Mannequin")
    unity_markers = {
        "left upper arm", "right upper arm", "left upper leg", "right upper leg",
    }
    if len(unity_markers & leaf_set) >= 3 or "unity humanoid" in joined:
        add("Unity Humanoid")
    if "motionbuilder" in joined or "character controls" in joined:
        add("MotionBuilder")
    if "rokoko" in joined or "smartsuit" in joined:
        add("Rokoko")
    if any(
        marker in joined
        for marker in ("accurig", "actorcore", "cc_base_", "rl_boneroot")
    ):
        add("AccuRig / ActorCore")
    if any(value.casefold() == "armature" for value in wrappers) or any(
        re.fullmatch(r"bone(?:[ ._-]*\d+)?", leaf) for leaf in leaves
    ):
        add("generic Blender")
    if any("|" in value or value.casefold().startswith("joint") for value in original):
        add("generic Maya")
    return tuple(hints)


def _role_keys(semantic_roles: Mapping[str, Any] | Iterable[str]) -> set[str]:
    if isinstance(semantic_roles, Mapping):
        return {str(value) for value in semantic_roles}
    return {str(value) for value in semantic_roles}


def _has_role(keys: set[str], suffix: str) -> bool:
    return suffix in keys or any(value.endswith("_" + suffix) for value in keys)


def classify_skeleton_archetype(
    nodes: Iterable[Any],
    semantic_roles: Mapping[str, Any] | Iterable[str] = (),
    *,
    body_frame: Any | None = None,
    quadruped_limb_count: int = 0,
    invalid_bind_count: int = 0,
) -> ArchetypeClassification:
    """Classify a graph only when role evidence agrees with structure.

    A collection of suggestive names is insufficient.  Humanoid acceptance
    requires a coherent connected hierarchy plus a recovered body frame or a
    comparably strong bilateral chain structure.
    """

    rows = tuple(nodes)
    count = len(rows)
    if not rows:
        return ArchetypeClassification(
            "unknown", 1.0, ("no LimbNode skeleton was available",)
        )
    parents = {
        str(getattr(row, "name", "")): getattr(row, "parent_name", None)
        for row in rows
    }
    edges = sum(parent in parents for parent in parents.values())
    roots = max(1, sum(parent not in parents for parent in parents.values()))
    connected_ratio = (edges + roots) / max(1, count)
    maximum_branch = max(
        (len(tuple(getattr(row, "children", ()) or ())) for row in rows),
        default=0,
    )
    role_names = _role_keys(semantic_roles)
    core = sum(
        (
            _has_role(role_names, "pelvis"),
            any(value.startswith("spine_") or value == "spine" for value in role_names),
            _has_role(role_names, "head"),
        )
    )
    bilateral_arms = all(
        _has_role(role_names, f"{side}_{role}")
        for side in ("left", "right")
        for role in ("upper_arm", "forearm")
    )
    bilateral_legs = all(
        _has_role(role_names, f"{side}_{role}")
        for side in ("left", "right")
        for role in ("thigh", "calf")
    )
    graph_support = connected_ratio >= 0.9 and edges >= min(8, max(1, count - 1))
    geometric_support = body_frame is not None and bool(
        getattr(body_frame, "quality", 0.0) >= 0.45
    )
    bilateral_support = bilateral_arms and bilateral_legs and maximum_branch >= 3
    humanoid_supported = core == 3 and graph_support and (
        geometric_support or bilateral_support
    )
    if humanoid_supported:
        confidence = 0.66
        confidence += 0.12 if geometric_support else 0.0
        confidence += 0.12 if bilateral_arms else 0.0
        confidence += 0.10 if bilateral_legs else 0.0
        confidence -= min(0.25, invalid_bind_count / max(1, count))
        return ArchetypeClassification(
            "humanoid",
            max(0.0, min(1.0, confidence)),
            tuple(
                value
                for enabled, value in (
                    (True, "pelvis, axial chain, and head anchors agree"),
                    (graph_support, "roles form a coherent connected hierarchy"),
                    (geometric_support, "bind-space body frame is recoverable"),
                    (bilateral_arms, "left/right arm chains agree"),
                    (bilateral_legs, "left/right leg chains agree"),
                )
                if enabled
            ),
        )

    raw_names = " ".join(str(getattr(row, "name", "")).casefold() for row in rows)
    quadruped_tokens = any(
        token in raw_names
        for token in ("paw", "hoof", "tail", "hock", "frontleg", "hindleg", "quadruped")
    )
    four_limb_structure = quadruped_limb_count >= 4 or maximum_branch >= 5
    if four_limb_structure and graph_support and (quadruped_tokens or quadruped_limb_count >= 4):
        return ArchetypeClassification(
            "quadruped",
            0.72 if quadruped_limb_count >= 4 else 0.62,
            (
                "four coherent limb chains leave an axial body chain",
                "quadruped anatomy evidence agrees with hierarchy",
            ),
            ("humanoid route rejected: bilateral biped anchors were not established",),
        )

    if connected_ratio >= 0.8 and invalid_bind_count <= max(1, count // 4):
        rejected = ()
        if any(
            token in raw_names for token in ("head", "leg", "arm", "pelvis", "spine")
        ):
            rejected = (
                "humanoid route rejected: names were not backed by a complete coherent body graph",
            )
        return ArchetypeClassification(
            "generic",
            0.62 if count > 1 else 0.5,
            ("usable conservative direct skeleton graph",),
            rejected,
        )
    return ArchetypeClassification(
        "unknown",
        0.75,
        ("skeleton graph or bind evidence is insufficient for semantic routing",),
        ("humanoid route rejected", "quadruped route rejected"),
    )


__all__ = [
    "ARCHETYPE_CLASSIFIER_VERSION",
    "ArchetypeClassification",
    "classify_skeleton_archetype",
    "detect_source_family_hints",
]
