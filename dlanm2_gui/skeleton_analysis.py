"""Generic, name-independent source-skeleton analysis.

The analyzer consumes the public ``FbxDocument`` shape but intentionally uses
duck typing so deterministic in-memory test scenes and future import adapters
share the same route.  Names, hierarchy, bind geometry, skin ownership, and
changing animation channels are recorded separately; no single signal can
force an anatomical archetype.
"""

from __future__ import annotations

from dataclasses import dataclass, replace
from hashlib import sha256
import json
import math
from types import MappingProxyType
from typing import Any, Iterable, Mapping, Sequence

import numpy as np

from .semantic_roles import (
    NAME_NORMALIZER_VERSION,
    SEMANTIC_LEXICON_VERSION,
    NormalizedBoneName,
    normalize_bone_name,
    preferred_anatomical_role,
)
from .skeleton_archetypes import (
    ARCHETYPE_CLASSIFIER_VERSION,
    classify_skeleton_archetype,
    detect_source_family_hints,
)


SOURCE_SKELETON_ANALYZER_VERSION = "dlr-source-skeleton-analysis-v1"


def _vector_tuple(value: Sequence[float] | np.ndarray | None) -> tuple[float, float, float] | None:
    if value is None:
        return None
    array = np.asarray(value, dtype=float).reshape(-1)
    if array.size < 3 or not np.isfinite(array[:3]).all():
        return None
    return tuple(float(round(item, 12)) for item in array[:3])


def _matrix_tuple(
    value: Any,
) -> tuple[tuple[float, float, float, float], ...] | None:
    try:
        matrix = np.asarray(value, dtype=float)
    except (TypeError, ValueError):
        return None
    if matrix.shape != (4, 4) or not np.isfinite(matrix).all():
        return None
    return tuple(
        tuple(float(round(item, 12)) for item in row)
        for row in matrix
    )


@dataclass(frozen=True, slots=True)
class AnalysisFinding:
    code: str
    severity: str
    message: str
    bone_names: tuple[str, ...] = ()
    evidence: tuple[str, ...] = ()

    def to_dict(self) -> dict[str, Any]:
        return {
            "code": self.code,
            "severity": self.severity,
            "message": self.message,
            "bone_names": list(self.bone_names),
            "evidence": list(self.evidence),
        }


@dataclass(frozen=True, slots=True)
class BodyFrame:
    origin: tuple[float, float, float]
    right_axis: tuple[float, float, float]
    up_axis: tuple[float, float, float]
    forward_axis: tuple[float, float, float]
    extent: float
    handedness: str
    quality: float
    pelvis_bone: str
    evidence: tuple[str, ...]

    def to_dict(self) -> dict[str, Any]:
        return {
            "origin": list(self.origin),
            "right_axis": list(self.right_axis),
            "up_axis": list(self.up_axis),
            "forward_axis": list(self.forward_axis),
            "extent": self.extent,
            "handedness": self.handedness,
            "quality": self.quality,
            "pelvis_bone": self.pelvis_bone,
            "evidence": list(self.evidence),
        }


@dataclass(frozen=True, slots=True)
class RoleCandidate:
    role: str
    bone_name: str
    confidence: float
    confidence_margin: float
    side: str
    evidence: tuple[str, ...]
    source: str

    def to_dict(self) -> dict[str, Any]:
        return {
            "role": self.role,
            "bone_name": self.bone_name,
            "confidence": self.confidence,
            "confidence_margin": self.confidence_margin,
            "side": self.side,
            "evidence": list(self.evidence),
            "source": self.source,
        }


@dataclass(frozen=True, slots=True)
class SemanticChain:
    name: str
    bone_names: tuple[str, ...]
    semantic_roles: tuple[str, ...]
    side: str
    confidence: float
    evidence: tuple[str, ...]

    def to_dict(self) -> dict[str, Any]:
        return {
            "name": self.name,
            "bone_names": list(self.bone_names),
            "semantic_roles": list(self.semantic_roles),
            "side": self.side,
            "confidence": self.confidence,
            "evidence": list(self.evidence),
        }


@dataclass(frozen=True, slots=True)
class SourceModelNode:
    object_id: int
    name: str
    subtype: str
    parent_name: str | None
    skeleton_member: bool
    wrapper_ancestor: bool

    def to_dict(self) -> dict[str, Any]:
        return {
            "object_id": self.object_id,
            "name": self.name,
            "subtype": self.subtype,
            "parent_name": self.parent_name,
            "skeleton_member": self.skeleton_member,
            "wrapper_ancestor": self.wrapper_ancestor,
        }


@dataclass(frozen=True, slots=True)
class AnalyzedBone:
    object_id: int
    name: str
    parent_name: str | None
    immediate_parent_name: str | None
    wrapper_ancestors: tuple[str, ...]
    children: tuple[str, ...]
    depth: int
    child_count: int
    descendant_count: int
    bind_global_matrix: tuple[tuple[float, float, float, float], ...] | None
    bind_local_matrix: tuple[tuple[float, float, float, float], ...] | None
    bind_source: str
    bind_position: tuple[float, float, float] | None
    normalized_body_position: tuple[float, float, float] | None
    segment_length: float | None
    symmetry_partner: str
    symmetry_score: float
    skin_weight: float
    skin_influence_centroid: tuple[float, float, float] | None
    skin_influence_bounds: tuple[tuple[float, float, float], tuple[float, float, float]] | None
    animated_components: frozenset[str]
    helper_likelihood: float
    control_likelihood: float
    endpoint_likelihood: float
    twist_likelihood: float
    deform_likelihood: float
    inferred_side: str
    side_conflict: bool
    semantic_role: str
    name_evidence: NormalizedBoneName

    def to_dict(self) -> dict[str, Any]:
        return {
            "object_id": self.object_id,
            "name": self.name,
            "parent_name": self.parent_name,
            "immediate_parent_name": self.immediate_parent_name,
            "wrapper_ancestors": list(self.wrapper_ancestors),
            "children": list(self.children),
            "depth": self.depth,
            "child_count": self.child_count,
            "descendant_count": self.descendant_count,
            "bind_global_matrix": (
                [list(row) for row in self.bind_global_matrix]
                if self.bind_global_matrix is not None else None
            ),
            "bind_local_matrix": (
                [list(row) for row in self.bind_local_matrix]
                if self.bind_local_matrix is not None else None
            ),
            "bind_source": self.bind_source,
            "bind_position": list(self.bind_position) if self.bind_position else None,
            "normalized_body_position": (
                list(self.normalized_body_position)
                if self.normalized_body_position else None
            ),
            "segment_length": self.segment_length,
            "symmetry_partner": self.symmetry_partner,
            "symmetry_score": self.symmetry_score,
            "skin_weight": self.skin_weight,
            "skin_influence_centroid": (
                list(self.skin_influence_centroid)
                if self.skin_influence_centroid else None
            ),
            "skin_influence_bounds": (
                [list(row) for row in self.skin_influence_bounds]
                if self.skin_influence_bounds else None
            ),
            "animated_components": sorted(self.animated_components),
            "helper_likelihood": self.helper_likelihood,
            "control_likelihood": self.control_likelihood,
            "endpoint_likelihood": self.endpoint_likelihood,
            "twist_likelihood": self.twist_likelihood,
            "deform_likelihood": self.deform_likelihood,
            "inferred_side": self.inferred_side,
            "side_conflict": self.side_conflict,
            "semantic_role": self.semantic_role,
            "name_evidence": self.name_evidence.to_dict(),
        }


@dataclass(frozen=True, slots=True)
class SourceSkeletonAnalysis:
    skeleton_hash: str
    bind_hash: str
    animation_hash: str
    roots: tuple[str, ...]
    nodes: tuple[AnalyzedBone, ...]
    body_frame: BodyFrame | None
    archetype: str
    archetype_confidence: float
    semantic_roles: Mapping[str, RoleCandidate]
    semantic_chains: Mapping[str, SemanticChain]
    animated_bones: frozenset[str]
    animated_components: Mapping[str, frozenset[str]]
    source_family_hints: tuple[str, ...]
    findings: tuple[AnalysisFinding, ...]
    animation_domain: str
    animated_chains_detected: tuple[str, ...]
    unresolved_animated_chains: tuple[str, ...]
    source_name_languages_or_scripts: tuple[str, ...]
    model_graph: tuple[SourceModelNode, ...]
    wrapper_models: tuple[str, ...]
    meters_per_unit: float
    axis_settings: Mapping[str, int | float | None]
    handedness: str
    selected_animation_stack: str
    bind_coverage: Mapping[str, int]
    analyzer_version: str = SOURCE_SKELETON_ANALYZER_VERSION

    @property
    def observed_motion_domain(self) -> str:
        return self.animation_domain

    @property
    def clip_domain(self) -> str:
        return "facial" if self.animation_domain == "facial_only" else "body"

    @property
    def lexicon_version(self) -> str:
        return SEMANTIC_LEXICON_VERSION

    @property
    def name_parent_hash(self) -> str:
        return self.skeleton_hash

    @property
    def hierarchy_hash(self) -> str:
        return self.skeleton_hash

    def to_dict(self) -> dict[str, Any]:
        return {
            "analyzer_version": self.analyzer_version,
            "name_normalizer_version": NAME_NORMALIZER_VERSION,
            "semantic_lexicon_version": SEMANTIC_LEXICON_VERSION,
            "archetype_classifier_version": ARCHETYPE_CLASSIFIER_VERSION,
            "skeleton_hash": self.skeleton_hash,
            "bind_hash": self.bind_hash,
            "animation_hash": self.animation_hash,
            "name_parent_hash": self.name_parent_hash,
            "hierarchy_hash": self.hierarchy_hash,
            "roots": list(self.roots),
            "nodes": [row.to_dict() for row in self.nodes],
            "body_frame": self.body_frame.to_dict() if self.body_frame else None,
            "archetype": self.archetype,
            "archetype_confidence": self.archetype_confidence,
            "semantic_roles": {
                key: self.semantic_roles[key].to_dict()
                for key in sorted(self.semantic_roles)
            },
            "semantic_chains": {
                key: self.semantic_chains[key].to_dict()
                for key in sorted(self.semantic_chains)
            },
            "animated_bones": sorted(self.animated_bones),
            "animated_components": {
                key: sorted(self.animated_components[key])
                for key in sorted(self.animated_components)
            },
            "source_family_hints": list(self.source_family_hints),
            "findings": [row.to_dict() for row in self.findings],
            "animation_domain": self.animation_domain,
            "observed_motion_domain": self.observed_motion_domain,
            "clip_domain": self.clip_domain,
            "animated_chains_detected": list(self.animated_chains_detected),
            "unresolved_animated_chains": list(self.unresolved_animated_chains),
            "source_name_languages_or_scripts": list(
                self.source_name_languages_or_scripts
            ),
            "model_graph": [row.to_dict() for row in self.model_graph],
            "wrapper_models": list(self.wrapper_models),
            "meters_per_unit": self.meters_per_unit,
            "axis_settings": dict(self.axis_settings),
            "handedness": self.handedness,
            "selected_animation_stack": self.selected_animation_stack,
            "bind_coverage": dict(self.bind_coverage),
        }


def _depth(name: str, parents: Mapping[str, str | None]) -> tuple[int, bool]:
    seen: set[str] = set()
    cursor: str | None = name
    value = 0
    while cursor in parents and parents.get(cursor) in parents:
        if cursor in seen:
            return value, True
        seen.add(cursor)
        cursor = parents.get(cursor)
        value += 1
    return value, False


def _descendants(name: str, children: Mapping[str, tuple[str, ...]]) -> int:
    seen: set[str] = set()
    stack = list(children.get(name, ()))
    while stack:
        current = stack.pop()
        if current in seen:
            continue
        seen.add(current)
        stack.extend(children.get(current, ()))
    return len(seen)


def _unit(value: np.ndarray) -> np.ndarray | None:
    length = float(np.linalg.norm(value))
    if not math.isfinite(length) or length <= 1.0e-10:
        return None
    return value / length


@dataclass(frozen=True, slots=True)
class _BodyAnchors:
    pelvis: str
    spine_child: str
    leg_children: tuple[str, ...]


def _select_leg_pair(
    pelvis: str,
    child_names: Sequence[str],
    positions: Mapping[str, np.ndarray],
    name_rows: Mapping[str, NormalizedBoneName],
) -> tuple[tuple[str, str], str, float] | None:
    if pelvis not in positions:
        return None
    origin = positions[pelvis]
    valid = [name for name in child_names if name in positions]
    if len(valid) < 3:
        return None
    best: tuple[float, tuple[str, str], str] | None = None
    for spine in valid:
        up = _unit(positions[spine] - origin)
        if up is None:
            continue
        others = [name for name in valid if name != spine]
        for first_index, first in enumerate(others):
            for second in others[first_index + 1:]:
                left = positions[first] - origin
                right = positions[second] - origin
                first_ortho = left - up * float(np.dot(left, up))
                second_ortho = right - up * float(np.dot(right, up))
                first_length = float(np.linalg.norm(first_ortho))
                second_length = float(np.linalg.norm(second_ortho))
                if min(first_length, second_length) <= 1.0e-10:
                    continue
                opposing = -float(np.dot(first_ortho, second_ortho)) / (
                    first_length * second_length
                )
                balance = 1.0 - abs(first_length - second_length) / max(
                    first_length, second_length
                )
                projection_balance = 1.0 - min(
                    1.0,
                    abs(float(np.dot(left, up) - np.dot(right, up)))
                    / max(float(np.linalg.norm(left)), float(np.linalg.norm(right)), 1.0e-10),
                )
                role_bonus = 0.0
                spine_role = preferred_anatomical_role(name_rows[spine])
                pair_roles = tuple(
                    preferred_anatomical_role(name_rows[name])
                    for name in (first, second)
                )
                if spine_role in {
                    "clavicle", "upper_arm", "forearm", "hand",
                    "thigh", "calf", "foot", "toe",
                }:
                    continue
                if any(
                    role in {"clavicle", "upper_arm", "forearm", "hand"}
                    for role in pair_roles
                ):
                    continue
                named_leg_pair = all(
                    role in {"thigh", "calf"} for role in pair_roles
                )
                first_down = -float(np.dot(left, up)) / max(
                    float(np.linalg.norm(left)), 1.0e-10
                )
                second_down = -float(np.dot(right, up)) / max(
                    float(np.linalg.norm(right)), 1.0e-10
                )
                # An arm pair at a chest branch is nearly perpendicular to the
                # axial child.  Anonymous leg roots must point materially down;
                # explicit thigh/calf names remain corroborating evidence.
                if not named_leg_pair and min(first_down, second_down) < 0.25:
                    continue
                if opposing < 0.35 or balance < 0.35 or projection_balance < 0.5:
                    continue
                if spine_role in {"spine", "chest"}:
                    role_bonus += 0.6
                if named_leg_pair:
                    role_bonus += 0.6
                score = opposing + balance + projection_balance + role_bonus
                if best is None or score > best[0]:
                    best = (score, (first, second), spine)
    if best is None:
        return None
    return best[1], best[2], max(0.0, min(1.0, best[0] / 3.6))


def _infer_body_frame(
    names: Sequence[str],
    parents: Mapping[str, str | None],
    children: Mapping[str, tuple[str, ...]],
    positions: Mapping[str, np.ndarray],
    name_rows: Mapping[str, NormalizedBoneName],
) -> tuple[BodyFrame | None, _BodyAnchors | None]:
    if len(positions) < 6:
        return None, None
    named_pelvis = [
        name for name in names
        if preferred_anatomical_role(name_rows[name]) == "pelvis"
        and name in positions
    ]
    candidates = [
        name
        for name in names
        if name in positions
        and len(children.get(name, ())) >= 3
        and preferred_anatomical_role(name_rows[name])
        not in {
            "spine", "chest", "neck", "head", "clavicle",
            "upper_arm", "forearm", "hand", "thigh", "calf", "foot", "toe",
        }
    ]
    ordered = tuple(dict.fromkeys((*named_pelvis, *candidates)))
    best: tuple[float, str, tuple[str, str], str, float] | None = None
    for pelvis in ordered:
        selection = _select_leg_pair(
            pelvis, children.get(pelvis, ()), positions, name_rows
        )
        if selection is None:
            continue
        legs, spine, quality = selection
        bonus = 0.15 if pelvis in named_pelvis else 0.0
        score = quality + bonus
        if best is None or score > best[0]:
            best = (score, pelvis, legs, spine, quality)
    if best is None:
        return None, None
    _score, pelvis, legs, spine, topology_quality = best
    origin = positions[pelvis]
    up = _unit(positions[spine] - origin)
    lateral = _unit(positions[legs[1]] - positions[legs[0]])
    if up is None or lateral is None:
        return None, None
    lateral = lateral - up * float(np.dot(lateral, up))
    lateral = _unit(lateral)
    if lateral is None:
        return None, None

    # Start from a stable coordinate sign, then use a majority of independent
    # side labels when available.  One swapped pair therefore becomes a
    # conflict instead of redefining the body frame.
    dominant = int(np.argmax(np.abs(lateral)))
    if lateral[dominant] < 0.0:
        lateral = -lateral
    side_votes = []
    for name in names:
        side = name_rows[name].side
        if side not in {"left", "right"} or name not in positions:
            continue
        coordinate = float(np.dot(positions[name] - origin, lateral))
        if abs(coordinate) <= 1.0e-8:
            continue
        expected = 1.0 if side == "right" else -1.0
        side_votes.append(math.copysign(1.0, coordinate) == expected)
    if len(side_votes) >= 3 and sum(side_votes) < len(side_votes) / 2:
        lateral = -lateral
    forward = _unit(np.cross(lateral, up))
    if forward is None:
        return None, None
    up = _unit(np.cross(forward, lateral))
    if up is None:
        return None, None
    extent = max(
        (float(np.linalg.norm(position - origin)) for position in positions.values()),
        default=0.0,
    )
    if extent <= 1.0e-10:
        return None, None
    quality = max(0.0, min(1.0, topology_quality + (0.1 if named_pelvis else 0.0)))
    frame = BodyFrame(
        origin=_vector_tuple(origin) or (0.0, 0.0, 0.0),
        right_axis=_vector_tuple(lateral) or (1.0, 0.0, 0.0),
        up_axis=_vector_tuple(up) or (0.0, 1.0, 0.0),
        forward_axis=_vector_tuple(forward) or (0.0, 0.0, 1.0),
        extent=float(extent),
        handedness="right_handed",
        quality=quality,
        pelvis_bone=pelvis,
        evidence=(
            "pelvis has a central axial child and a mirrored child pair",
            "body axes are derived from bind-space topology and symmetry",
            "side labels only orient a majority-consistent lateral axis",
        ),
    )
    return frame, _BodyAnchors(pelvis, spine, legs)


def _infer_partial_upper_body_frame(
    names: Sequence[str],
    children: Mapping[str, tuple[str, ...]],
    positions: Mapping[str, np.ndarray],
    name_rows: Mapping[str, NormalizedBoneName],
) -> tuple[BodyFrame | None, _BodyAnchors | None]:
    """Recover a conservative body frame when both physical leg chains are absent."""

    pelvis_candidates = [
        name
        for name in names
        if name in positions
        and preferred_anatomical_role(name_rows[name]) == "pelvis"
    ]
    if len(pelvis_candidates) != 1:
        return None, None
    pelvis = pelvis_candidates[0]
    axial_children = [
        name
        for name in children.get(pelvis, ())
        if name in positions
        and preferred_anatomical_role(name_rows[name]) in {"spine", "chest"}
    ]
    if not axial_children:
        return None, None
    spine = axial_children[0]
    origin = positions[pelvis]
    up = _unit(positions[spine] - origin)
    if up is None:
        return None, None

    side_pairs: list[tuple[int, str, str]] = []
    for priority, role in enumerate(("clavicle", "upper_arm", "forearm", "hand")):
        left = [
            name
            for name in names
            if name in positions
            and name_rows[name].side == "left"
            and preferred_anatomical_role(name_rows[name]) == role
        ]
        right = [
            name
            for name in names
            if name in positions
            and name_rows[name].side == "right"
            and preferred_anatomical_role(name_rows[name]) == role
        ]
        if len(left) == 1 and len(right) == 1:
            side_pairs.append((priority, left[0], right[0]))
    if not side_pairs:
        return None, None
    _priority, left_name, right_name = min(side_pairs)
    lateral = positions[right_name] - positions[left_name]
    lateral = lateral - up * float(np.dot(lateral, up))
    lateral = _unit(lateral)
    if lateral is None:
        return None, None
    forward = _unit(np.cross(lateral, up))
    if forward is None:
        return None, None
    up = _unit(np.cross(forward, lateral))
    if up is None:
        return None, None
    extent = max(
        (float(np.linalg.norm(position - origin)) for position in positions.values()),
        default=0.0,
    )
    if extent <= 1.0e-10:
        return None, None
    frame = BodyFrame(
        origin=_vector_tuple(origin) or (0.0, 0.0, 0.0),
        right_axis=_vector_tuple(lateral) or (1.0, 0.0, 0.0),
        up_axis=_vector_tuple(up) or (0.0, 1.0, 0.0),
        forward_axis=_vector_tuple(forward) or (0.0, 0.0, 1.0),
        extent=float(extent),
        handedness="right_handed",
        quality=0.72,
        pelvis_bone=pelvis,
        evidence=(
            "named pelvis and central axial child establish the upper-body origin",
            "bilateral named arm anchors establish the lateral axis",
            "physically absent leg chains are not synthesized",
        ),
    )
    return frame, _BodyAnchors(pelvis, spine, ())


def _semantic_hierarchy(
    names: Sequence[str],
    parents: Mapping[str, str | None],
    name_rows: Mapping[str, NormalizedBoneName],
) -> tuple[tuple[str, ...], dict[str, str | None], dict[str, tuple[str, ...]]]:
    """Collapse helper/control/twist LimbNodes out of anatomical topology."""

    semantic_names = tuple(name for name in names if not name_rows[name].likely_helper)
    semantic_set = set(semantic_names)
    semantic_parents: dict[str, str | None] = {}
    for name in semantic_names:
        parent = parents.get(name)
        visited: set[str] = set()
        while parent is not None and parent not in semantic_set and parent not in visited:
            visited.add(parent)
            parent = parents.get(parent)
        semantic_parents[name] = parent if parent in semantic_set else None
    child_lists: dict[str, list[str]] = {name: [] for name in semantic_names}
    for name in semantic_names:
        parent = semantic_parents[name]
        if parent in child_lists:
            child_lists[parent].append(name)
    return (
        semantic_names,
        semantic_parents,
        {name: tuple(values) for name, values in child_lists.items()},
    )


def _body_coordinates(
    positions: Mapping[str, np.ndarray], body_frame: BodyFrame | None
) -> dict[str, np.ndarray]:
    if body_frame is None:
        return {}
    origin = np.asarray(body_frame.origin, dtype=float)
    axes = np.column_stack(
        (
            np.asarray(body_frame.right_axis, dtype=float),
            np.asarray(body_frame.up_axis, dtype=float),
            np.asarray(body_frame.forward_axis, dtype=float),
        )
    )
    return {
        name: (axes.T @ (position - origin)) / max(body_frame.extent, 1.0e-12)
        for name, position in positions.items()
    }


def _primary_chain(
    start: str,
    children: Mapping[str, tuple[str, ...]],
    positions: Mapping[str, np.ndarray],
    *,
    body_coordinates: Mapping[str, np.ndarray],
    direction: str,
) -> tuple[str, ...]:
    result: list[str] = []
    current: str | None = start
    visited: set[str] = set()
    while current is not None and current not in visited:
        visited.add(current)
        result.append(current)
        options = list(children.get(current, ()))
        if not options:
            break
        if len(options) == 1:
            current = options[0]
            continue
        if direction == "lateral":
            current = max(
                options,
                key=lambda name: abs(float(body_coordinates.get(name, np.zeros(3))[0])),
            )
        elif direction == "down":
            current = min(
                options,
                key=lambda name: float(body_coordinates.get(name, np.zeros(3))[1]),
            )
        else:
            current = max(
                options,
                key=lambda name: float(body_coordinates.get(name, np.zeros(3))[1]),
            )
        # A hand/facial branch point is an anchor, not an arbitrary choice of
        # one finger or control chain.
        if len(options) >= 3 and direction == "lateral":
            break
    return tuple(result)


def _add_role(
    roles: dict[str, RoleCandidate],
    role: str,
    bone: str,
    confidence: float,
    evidence: Iterable[str],
    source: str,
    side: str = "",
    confidence_margin: float | None = None,
) -> None:
    candidate = RoleCandidate(
        role,
        bone,
        max(0.0, min(1.0, float(confidence))),
        (
            float(confidence_margin)
            if confidence_margin is not None
            else 0.15 if source == "topology_bind"
            else 0.12 if confidence >= 0.85
            else 0.08
        ),
        side,
        tuple(dict.fromkeys(str(value) for value in evidence)),
        source,
    )
    previous = roles.get(role)
    if previous is None:
        roles[role] = candidate
        return

    # Preserve a real runner-up margin instead of silently discarding a second
    # bone that claims the same anatomical role.  Topology/bind evidence may
    # still resolve a weaker name-only duplicate later, but equally plausible
    # candidates remain fail-closed for the planner.
    candidate_wins = (
        candidate.confidence > previous.confidence
        or (
            candidate.confidence == previous.confidence
            and candidate.bone_name.casefold() < previous.bone_name.casefold()
        )
    )
    winner, runner_up = (
        (candidate, previous) if candidate_wins else (previous, candidate)
    )
    margin = min(
        winner.confidence_margin,
        abs(winner.confidence - runner_up.confidence),
    )
    roles[role] = replace(
        winner,
        confidence_margin=margin,
        evidence=tuple(
            dict.fromkeys(
                (
                    *winner.evidence,
                    f"competing role candidate {runner_up.bone_name}",
                )
            )
        ),
    )


def _name_roles(
    names: Sequence[str], name_rows: Mapping[str, NormalizedBoneName]
) -> dict[str, RoleCandidate]:
    roles: dict[str, RoleCandidate] = {}
    spine_index = 0
    neck_index = 0
    for name in names:
        evidence = name_rows[name]
        # Rig-family prefixes are removed from comparison text, but ORG/MCH,
        # control, IK, and similar markers remain explicit helper evidence.
        # Such controls must not compete with deform bones for anatomical roles.
        if evidence.likely_helper:
            continue
        base = preferred_anatomical_role(evidence)
        if not base:
            continue
        side = evidence.side
        if base in {"spine", "chest"}:
            spine_index += 1
            key = f"spine_{evidence.ordinal or spine_index}"
        elif base == "neck":
            neck_index += 1
            key = f"neck_{evidence.ordinal or neck_index}"
        elif side and base in {
            "clavicle", "upper_arm", "forearm", "hand", "thigh", "calf",
            "foot", "toe", "finger",
        }:
            if base == "finger" and evidence.finger and evidence.ordinal is not None:
                key = (
                    f"{side}_{evidence.finger}_{evidence.ordinal}"
                    if evidence.ordinal <= 3
                    else f"{side}_{evidence.finger}_endpoint_{evidence.ordinal}"
                )
            else:
                key = f"{side}_{base}"
                if base == "finger" and evidence.ordinal is not None:
                    key += f"_{evidence.ordinal}"
        else:
            key = base
        _add_role(
            roles,
            key,
            name,
            (
                0.88
                if base == "finger" and evidence.finger and evidence.ordinal is not None
                else 0.62 if side or base in {"pelvis", "head", "root"}
                else 0.55
            ),
            (
                f"multilingual/name token matched {base}",
                *(f"side evidence {value}" for value in evidence.side_evidence),
            ),
            "name_evidence",
            side,
        )
    return roles


def _arm_roles_for_chain(
    chain: Sequence[str],
    name_rows: Mapping[str, NormalizedBoneName],
) -> tuple[str, ...]:
    canonical = ("clavicle", "upper_arm", "forearm", "hand")
    named = tuple(preferred_anatomical_role(name_rows[name]) for name in chain)
    recognized = [
        (position, role)
        for position, role in enumerate(named)
        if role in canonical
    ]
    if recognized:
        indices = [canonical.index(role) for _position, role in recognized]
        contiguous = indices == list(range(indices[0], indices[0] + len(indices)))
        offsets = {
            role_index - position
            for (position, _role), role_index in zip(recognized, indices)
        }
        if contiguous and len(offsets) == 1:
            first_index = offsets.pop()
            if 0 <= first_index and first_index + len(chain) <= len(canonical):
                return canonical[first_index : first_index + len(chain)]

        # Explicitly named non-contiguous anchors describe a physical gap, not
        # a shorter uniformly packed arm.  Keep each named bone on its actual
        # semantic role and leave unrecognized positions empty so a missing
        # animated core segment fails closed in the planner.
        return tuple(role if role in canonical else "" for role in named)
    return canonical[: len(chain)] if len(chain) >= 4 else canonical[1 : 1 + len(chain)]


def _infer_topology_roles(
    names: Sequence[str],
    parents: Mapping[str, str | None],
    children: Mapping[str, tuple[str, ...]],
    positions: Mapping[str, np.ndarray],
    name_rows: Mapping[str, NormalizedBoneName],
    body_frame: BodyFrame | None,
    anchors: _BodyAnchors | None,
) -> tuple[dict[str, RoleCandidate], dict[str, SemanticChain]]:
    roles = _name_roles(names, name_rows)
    chains: dict[str, SemanticChain] = {}
    if body_frame is None or anchors is None:
        return roles, chains
    coords = _body_coordinates(positions, body_frame)
    has_leg_anchors = bool(anchors.leg_children)
    _add_role(
        roles,
        "pelvis",
        anchors.pelvis,
        0.94 if has_leg_anchors else 0.88,
        (
            (
                "three-way pelvis branch"
                if has_leg_anchors
                else "named pelvis anchors a partial upper-body hierarchy"
            ),
            *(
                ("mirrored leg roots",)
                if has_leg_anchors
                else ("physical leg chains are absent rather than synthesized",)
            ),
            "central axial child",
        ),
        "topology_bind",
    )

    leg_rows: list[tuple[str, tuple[str, ...]]] = []
    for start in anchors.leg_children:
        coordinate = float(coords.get(start, np.zeros(3))[0])
        side = "right" if coordinate > 0.0 else "left"
        chain = _primary_chain(
            start, children, positions, body_coordinates=coords, direction="down"
        )
        major = ("thigh", "calf", "foot", "toe")
        assigned = chain[: len(major)]
        for role, bone in zip(major, assigned):
            _add_role(
                roles, f"{side}_{role}", bone, 0.86,
                ("ordered pelvis-to-terminal leg chain", "bind-space side and symmetry"),
                "topology_bind", side,
            )
        chain_roles = tuple(f"{side}_{role}" for role in major[: len(assigned)])
        chains[f"{side}_leg"] = SemanticChain(
            f"{side}_leg", chain, chain_roles, side, 0.86,
            ("ordered topology and bind direction",),
        )
        leg_rows.append((side, chain))

    spine_nodes: list[str] = []
    arm_starts: tuple[str, str] | None = None
    head_start: str | None = None
    current: str | None = anchors.spine_child
    visited: set[str] = set()
    while current is not None and current not in visited:
        visited.add(current)
        spine_nodes.append(current)
        options = list(children.get(current, ()))
        if not options:
            break
        lateral = [
            name for name in options
            if abs(float(coords.get(name, np.zeros(3))[0])) >= 0.02
        ]
        positive = [name for name in lateral if coords[name][0] > 0.0]
        negative = [name for name in lateral if coords[name][0] < 0.0]
        if positive and negative:
            right = max(positive, key=lambda name: abs(float(coords[name][0])))
            left = min(negative, key=lambda name: float(coords[name][0]))
            arm_starts = (left, right)
            central = [name for name in options if name not in {left, right}]
            if central:
                head_start = min(central, key=lambda name: abs(float(coords[name][0])))
            break
        current = max(
            options,
            key=lambda name: (
                -abs(float(coords.get(name, np.zeros(3))[0])),
                float(coords.get(name, np.zeros(3))[1]),
            ),
        )

    for index, bone in enumerate(spine_nodes, 1):
        _add_role(
            roles, f"spine_{index}", bone, 0.84,
            ("ordered central pelvis-to-chest chain",), "topology_bind",
        )
    chains["spine"] = SemanticChain(
        "spine", tuple(spine_nodes),
        tuple(f"spine_{index}" for index in range(1, len(spine_nodes) + 1)),
        "", 0.84, ("central axial topology",),
    )

    if arm_starts is not None:
        for start in arm_starts:
            coordinate = float(coords.get(start, np.zeros(3))[0])
            side = "right" if coordinate > 0.0 else "left"
            chain = _primary_chain(
                start, children, positions, body_coordinates=coords, direction="lateral"
            )
            major = _arm_roles_for_chain(chain, name_rows)
            assigned = tuple(
                (bone, role)
                for bone, role in zip(chain, major)
                if role
            )
            for bone, role in assigned:
                _add_role(
                    roles, f"{side}_{role}", bone, 0.84,
                    ("ordered chest-to-hand chain", "bind-space lateral direction"),
                    "topology_bind", side,
                )
            chain_bones = tuple(bone for bone, _role in assigned)
            chain_roles = tuple(f"{side}_{role}" for _bone, role in assigned)
            chains[f"{side}_arm"] = SemanticChain(
                f"{side}_arm", chain_bones, chain_roles, side, 0.84,
                ("ordered topology and bind direction",),
            )

    if head_start is not None:
        head_chain = _primary_chain(
            head_start, children, positions, body_coordinates=coords, direction="up"
        )
        named_head = next(
            (name for name in head_chain if preferred_anatomical_role(name_rows[name]) == "head"),
            None,
        )
        head = named_head or (head_chain[-2] if len(head_chain) >= 3 else head_chain[-1])
        before_head = head_chain[: head_chain.index(head)]
        for index, bone in enumerate(before_head, 1):
            _add_role(
                roles, f"neck_{index}", bone, 0.82,
                ("central chest-to-head chain",), "topology_bind",
            )
        _add_role(
            roles, "head", head, 0.9,
            ("terminal central axial anchor", "above chest in body frame"),
            "topology_bind",
        )
        chain_roles = tuple(
            [*(f"neck_{index}" for index in range(1, len(before_head) + 1)), "head"]
        )
        chains["neck_head"] = SemanticChain(
            "neck_head", head_chain, chain_roles, "", 0.84,
            ("ordered central topology",),
        )
    return roles, chains


def _normalized_bind_globals(
    document: Any,
    names: Sequence[str],
    findings: list[AnalysisFinding],
) -> dict[str, np.ndarray]:
    raw = dict(getattr(document, "bind_global_matrices", {}) or {})
    if not raw and hasattr(document, "global_matrices"):
        try:
            raw = dict(document.global_matrices(tick=0, use_animation=False) or {})
        except (TypeError, ValueError, np.linalg.LinAlgError) as exc:
            findings.append(AnalysisFinding(
                "bind_evaluation_failed", "blocking",
                f"The source bind hierarchy could not be evaluated: {exc}",
            ))
    meters = float(getattr(document, "meters_per_unit", 1.0) or 1.0)
    result: dict[str, np.ndarray] = {}
    for name in names:
        value = raw.get(name)
        if value is None:
            continue
        try:
            matrix = np.asarray(value, dtype=float).copy()
        except (TypeError, ValueError):
            continue
        if matrix.shape != (4, 4) or not np.isfinite(matrix).all():
            findings.append(AnalysisFinding(
                "invalid_bind_matrix", "blocking",
                f"Bone {name!r} has a malformed or non-finite bind matrix.", (name,),
            ))
            continue
        try:
            if hasattr(document, "normalized_matrix_to_target_space"):
                matrix = np.asarray(
                    document.normalized_matrix_to_target_space(name, matrix), dtype=float
                )
            else:
                matrix[:3, 3] *= meters
        except (TypeError, ValueError, np.linalg.LinAlgError) as exc:
            findings.append(AnalysisFinding(
                "bind_normalization_failed", "blocking",
                f"Bone {name!r} could not be normalized: {exc}", (name,),
            ))
            continue
        if matrix.shape != (4, 4) or not np.isfinite(matrix).all() or abs(float(np.linalg.det(matrix[:3, :3]))) <= 1.0e-12:
            findings.append(AnalysisFinding(
                "singular_bind_matrix", "blocking",
                f"Bone {name!r} has a singular normalized bind matrix.", (name,),
            ))
            continue
        result[name] = matrix
    return result


def _skin_evidence(
    document: Any,
) -> tuple[
    dict[str, float],
    dict[str, tuple[float, float, float]],
    dict[str, tuple[tuple[float, float, float], tuple[float, float, float]]],
]:
    scene = getattr(document, "scene", None)
    if scene is None:
        return {}, {}, {}
    model_names = dict(getattr(scene, "model_names", {}) or {})
    totals: dict[str, float] = {}
    weighted_points: dict[str, np.ndarray] = {}
    bounds: dict[str, tuple[np.ndarray, np.ndarray]] = {}
    seen_cluster_ids: set[int] = set()

    def cluster_name(cluster: Any) -> str:
        value = str(getattr(cluster, "bone_name", "") or "")
        bone_id = getattr(cluster, "bone_id", None)
        return value or str(model_names.get(bone_id, "") or "")

    for geometry in tuple(getattr(scene, "geometries", ()) or ()):
        points = np.asarray(getattr(geometry, "control_points", ()), dtype=float)
        for cluster in tuple(getattr(geometry, "clusters", ()) or ()):
            seen_cluster_ids.add(id(cluster))
            name = cluster_name(cluster)
            if not name:
                continue
            indices = tuple(getattr(cluster, "indices", ()) or ())
            weights = tuple(getattr(cluster, "weights", ()) or ())
            for index, weight in zip(indices, weights):
                value = float(weight)
                if not math.isfinite(value) or value <= 0.0:
                    continue
                totals[name] = totals.get(name, 0.0) + value
                if points.ndim != 2 or points.shape[1] < 3 or not 0 <= int(index) < len(points):
                    continue
                point = np.asarray(points[int(index), :3], dtype=float)
                if not np.isfinite(point).all():
                    continue
                weighted_points[name] = weighted_points.get(name, np.zeros(3)) + point * value
                if name not in bounds:
                    bounds[name] = (point.copy(), point.copy())
                else:
                    minimum, maximum = bounds[name]
                    bounds[name] = (np.minimum(minimum, point), np.maximum(maximum, point))
    for cluster in tuple(getattr(scene, "skin_clusters", ()) or ()):
        if id(cluster) in seen_cluster_ids:
            continue
        name = cluster_name(cluster)
        if not name:
            continue
        total = sum(
            float(value) for value in tuple(getattr(cluster, "weights", ()) or ())
            if math.isfinite(float(value)) and float(value) > 0.0
        )
        totals[name] = totals.get(name, 0.0) + total
    centroids = {
        name: _vector_tuple(value / totals[name]) or (0.0, 0.0, 0.0)
        for name, value in weighted_points.items()
        if totals.get(name, 0.0) > 0.0
    }
    tuple_bounds = {
        name: (
            _vector_tuple(value[0]) or (0.0, 0.0, 0.0),
            _vector_tuple(value[1]) or (0.0, 0.0, 0.0),
        )
        for name, value in bounds.items()
    }
    return totals, centroids, tuple_bounds


def _animated_components(
    document: Any,
    names_by_id: Mapping[int, str],
) -> dict[str, frozenset[str]]:
    result: dict[str, set[str]] = {}
    curves = dict(getattr(document, "curves", {}) or {})
    for key, curve in curves.items():
        if not isinstance(key, tuple) or len(key) < 2:
            continue
        object_key = key[0]
        name = names_by_id.get(int(object_key), "") if isinstance(object_key, (int, np.integer)) else str(object_key)
        if not name:
            continue
        try:
            _times, values = curve
            finite = [float(value) for value in values if math.isfinite(float(value))]
        except (TypeError, ValueError):
            continue
        if len(finite) <= 1 or max(finite) - min(finite) <= 1.0e-8:
            continue
        property_name = str(key[1]).casefold()
        if "translation" in property_name or "position" in property_name:
            component = "translation"
        elif "rotation" in property_name:
            component = "rotation"
        elif "scal" in property_name:
            component = "scale"
        else:
            component = "other"
        result.setdefault(name, set()).add(component)
    if not result:
        supplied = dict(getattr(document, "animated_components", {}) or {})
        for name, components in supplied.items():
            result[str(name)] = {str(value) for value in components}
    return {name: frozenset(values) for name, values in result.items()}


def _animation_fingerprint(
    document: Any,
    components: Mapping[str, frozenset[str]],
    selected_stack: str,
) -> str:
    digest = sha256()
    digest.update(SOURCE_SKELETON_ANALYZER_VERSION.encode("ascii"))
    digest.update(selected_stack.encode("utf-8"))
    curves = dict(getattr(document, "curves", {}) or {})
    if curves:
        for key in sorted(curves, key=lambda value: repr(value)):
            digest.update(repr(key).encode("utf-8"))
            try:
                times, values = curves[key]
                digest.update(np.asarray(tuple(times), dtype="<i8").tobytes())
                digest.update(np.asarray(tuple(values), dtype="<f8").tobytes())
            except (TypeError, ValueError, OverflowError):
                digest.update(repr(curves[key]).encode("utf-8"))
    else:
        payload = {
            name: sorted(values) for name, values in sorted(components.items())
        }
        digest.update(json.dumps(
            payload, ensure_ascii=False, sort_keys=True, separators=(",", ":")
        ).encode("utf-8"))
    return digest.hexdigest()


def _animation_domain(
    animated: Mapping[str, frozenset[str]],
    roles: Mapping[str, RoleCandidate],
    roots: Sequence[str],
) -> str:
    if not animated:
        return "mostly_static_pose"
    role_by_bone = {candidate.bone_name: role for role, candidate in roles.items()}
    animated_roles = {role_by_bone.get(name, "") for name in animated}
    meaningful = {value for value in animated_roles if value}
    if set(animated) <= set(roots) and any(
        "translation" in components for components in animated.values()
    ):
        return "root_motion_clip"
    facial = {value for value in meaningful if "face" in value or any(token in value for token in ("jaw", "brow", "eye", "lip", "tongue"))}
    if facial and len(facial) >= max(1, len(meaningful) // 2):
        return "facial_only"
    upper = {
        value for value in meaningful
        if value.startswith(("spine_", "neck_", "left_upper", "right_upper", "left_fore", "right_fore", "left_hand", "right_hand", "left_clavicle", "right_clavicle"))
        or value == "head"
    }
    has_physical_leg_roles = any(
        role.startswith(f"{side}_{part}")
        for role in roles
        for side in ("left", "right")
        for part in ("thigh", "calf", "foot", "toe")
    )
    lower = {
        value for value in meaningful
        if (value == "pelvis" and has_physical_leg_roles) or any(
            value.startswith(f"{side}_{part}")
            for side in ("left", "right")
            for part in ("thigh", "calf", "foot", "toe")
        )
    }
    if upper and lower:
        return "full_body"
    if upper:
        sides = {value.split("_", 1)[0] for value in upper if value.startswith(("left_", "right_"))}
        return "single_limb" if len(sides) == 1 and not any(value.startswith("spine_") for value in upper) else "upper_body"
    if lower:
        sides = {value.split("_", 1)[0] for value in lower if value.startswith(("left_", "right_"))}
        return "single_limb" if len(sides) == 1 and "pelvis" not in lower else "lower_body"
    return "single_limb" if len(animated) <= 4 else "unknown_motion"


def _model_graph(
    document: Any,
    limb_ids: set[int],
) -> tuple[tuple[SourceModelNode, ...], tuple[str, ...], dict[str, tuple[str, ...]], dict[str, str | None]]:
    scene = getattr(document, "scene", None)
    if scene is None:
        return (), (), {}, {}
    model_ids = tuple(getattr(scene, "model_ids", ()) or ())
    model_names = dict(getattr(scene, "model_names", {}) or {})
    model_subtypes = dict(getattr(scene, "model_subtypes", {}) or {})
    wrapper_ids: set[int] = set()
    wrappers_by_bone: dict[str, tuple[str, ...]] = {}
    immediate_by_bone: dict[str, str | None] = {}
    for bone_id in limb_ids:
        name = str(model_names.get(bone_id, bone_id))
        parent_id = scene.model_parent_id(bone_id) if hasattr(scene, "model_parent_id") else None
        immediate_by_bone[name] = str(model_names.get(parent_id, parent_id)) if parent_id in model_names else None
        wrappers: list[str] = []
        visited: set[int] = set()
        while parent_id in model_names and parent_id not in visited and parent_id not in limb_ids:
            visited.add(int(parent_id))
            wrapper_ids.add(int(parent_id))
            wrappers.append(str(model_names[parent_id]))
            parent_id = scene.model_parent_id(parent_id)
        wrappers_by_bone[name] = tuple(wrappers)
    rows = []
    for object_id in model_ids:
        parent_id = scene.model_parent_id(object_id) if hasattr(scene, "model_parent_id") else None
        rows.append(SourceModelNode(
            int(object_id),
            str(model_names.get(object_id, object_id)),
            str(model_subtypes.get(object_id, "")),
            str(model_names.get(parent_id, parent_id)) if parent_id in model_names else None,
            int(object_id) in limb_ids,
            int(object_id) in wrapper_ids,
        ))
    wrapper_names = tuple(str(model_names[value]) for value in model_ids if value in wrapper_ids)
    return tuple(rows), wrapper_names, wrappers_by_bone, immediate_by_bone


def _handedness(document: Any) -> str:
    try:
        if hasattr(document, "target_basis_matrix"):
            determinant = float(np.linalg.det(np.asarray(document.target_basis_matrix(), dtype=float)[:3, :3]))
            return "left_handed" if determinant < 0.0 else "right_handed"
    except (TypeError, ValueError, np.linalg.LinAlgError):
        pass
    contract = getattr(document, "contract", None)
    reflected = tuple(getattr(contract, "reflected_or_negative_scale_nodes", ()) or ())
    return "reflected_or_mixed" if reflected else "right_handed"


def analyze_source_skeleton(
    document: Any,
    animation_stack: str | None = None,
) -> SourceSkeletonAnalysis:
    """Analyze one source document without assuming a vendor or language."""

    findings: list[AnalysisFinding] = []
    if animation_stack is not None and hasattr(document, "select_animation_stack"):
        document.select_animation_stack(animation_stack)
    limb_models = dict(getattr(document, "limb_models", {}) or {})
    names = tuple(str(name) for name in limb_models)
    if not names:
        findings.append(AnalysisFinding(
            "no_usable_skeleton", "blocking", "No FBX LimbNode skeleton was found."
        ))
    names_by_id = {int(object_id): str(name) for name, object_id in limb_models.items()}
    parents = {
        name: (
            str(parent) if parent in limb_models else None
        )
        for name, parent in dict(getattr(document, "parent_by_name", {}) or {}).items()
        if name in limb_models
    }
    for name in names:
        parents.setdefault(name, None)
    child_lists: dict[str, list[str]] = {name: [] for name in names}
    for name, parent in parents.items():
        if parent in child_lists:
            child_lists[parent].append(name)
    children = {name: tuple(values) for name, values in child_lists.items()}

    model_graph, wrapper_names, wrappers_by_bone, immediate_by_bone = _model_graph(
        document, set(names_by_id)
    )
    if wrapper_names:
        findings.append(AnalysisFinding(
            "collapsed_non_bone_wrappers", "info",
            f"{len(wrapper_names)} non-bone wrapper Model(s) participate in transforms and were collapsed only for semantic chains.",
            wrapper_names,
        ))

    name_rows = {name: normalize_bone_name(name) for name in names}
    semantic_names, semantic_parents, semantic_children = _semantic_hierarchy(
        names, parents, name_rows
    )
    excluded_semantic_helpers = tuple(
        name for name in names if name not in set(semantic_names)
    )
    if excluded_semantic_helpers:
        findings.append(
            AnalysisFinding(
                "collapsed_helper_limbnodes",
                "info",
                f"{len(excluded_semantic_helpers)} helper/control/twist LimbNode(s) were excluded from semantic topology.",
                excluded_semantic_helpers,
            )
        )
    collision_groups: dict[str, list[str]] = {}
    for name, row in name_rows.items():
        collision_groups.setdefault(row.normalized_unicode_name, []).append(name)
    for values in collision_groups.values():
        if len(values) > 1:
            findings.append(AnalysisFinding(
                "normalized_name_collision", "action_required",
                "Different source bones collapse to the same Unicode NFKC/casefold form.",
                tuple(values),
                ("original names remain preserved; automatic identity is not assumed",),
            ))

    bind_globals = _normalized_bind_globals(document, names, findings)
    bind_locals: dict[str, np.ndarray] = {}
    for name, matrix in bind_globals.items():
        parent = parents.get(name)
        if parent in bind_globals:
            try:
                bind_locals[name] = np.linalg.inv(bind_globals[parent]) @ matrix
            except np.linalg.LinAlgError:
                findings.append(AnalysisFinding(
                    "singular_parent_bind", "blocking",
                    f"Parent bind for {name!r} is singular.", (name, str(parent)),
                ))
        else:
            bind_locals[name] = matrix.copy()
    positions = {name: matrix[:3, 3].copy() for name, matrix in bind_globals.items()}
    semantic_positions = {
        name: positions[name] for name in semantic_names if name in positions
    }
    body_frame, anchors = _infer_body_frame(
        semantic_names,
        semantic_parents,
        semantic_children,
        semantic_positions,
        name_rows,
    )
    if body_frame is None:
        body_frame, anchors = _infer_partial_upper_body_frame(
            semantic_names,
            semantic_children,
            semantic_positions,
            name_rows,
        )
    body_coords = _body_coordinates(positions, body_frame)
    roles, semantic_chains = _infer_topology_roles(
        semantic_names,
        semantic_parents,
        semantic_children,
        semantic_positions,
        name_rows,
        body_frame,
        anchors,
    )
    for role, candidate in sorted(roles.items()):
        if candidate.confidence_margin < 0.08:
            findings.append(
                AnalysisFinding(
                    "ambiguous_semantic_role",
                    "action_required",
                    f"Semantic role {role!r} has two nearly equal source candidates.",
                    (candidate.bone_name,),
                    candidate.evidence,
                )
            )

    skin_weights, skin_centroids, skin_bounds = _skin_evidence(document)
    components = _animated_components(document, names_by_id)
    animated_bones = frozenset(components)
    bind_sources = dict(getattr(document, "bind_source_by_bone", {}) or {})
    fallback_source = str(getattr(document, "bind_source", "") or "")

    role_by_bone: dict[str, tuple[str, float]] = {}
    for role, candidate in roles.items():
        previous = role_by_bone.get(candidate.bone_name)
        if previous is None or candidate.confidence > previous[1]:
            role_by_bone[candidate.bone_name] = (role, candidate.confidence)

    symmetry: dict[str, tuple[str, float]] = {}
    if body_coords:
        for name, coordinate in body_coords.items():
            if abs(float(coordinate[0])) < 0.025:
                continue
            reflected = coordinate.copy()
            reflected[0] *= -1.0
            candidates = [
                other for other in names
                if other != name and other in body_coords
                and float(body_coords[other][0]) * float(coordinate[0]) < 0.0
            ]
            if not candidates:
                continue
            other = min(candidates, key=lambda value: float(np.linalg.norm(body_coords[value] - reflected)))
            distance = float(np.linalg.norm(body_coords[other] - reflected))
            score = max(0.0, 1.0 - distance / 0.25)
            if score >= 0.25:
                symmetry[name] = (other, score)

    analyzed_nodes: list[AnalyzedBone] = []
    cycle_names: list[str] = []
    for name in names:
        depth, cyclic = _depth(name, parents)
        if cyclic:
            cycle_names.append(name)
        position = positions.get(name)
        parent = parents.get(name)
        length = (
            float(np.linalg.norm(position - positions[parent]))
            if position is not None and parent in positions else None
        )
        evidence = name_rows[name]
        helper_tokens = set(evidence.helper_tokens)
        endpoint = not children.get(name)
        endpoint_likelihood = 0.9 if helper_tokens & {"end", "tip", "nub", "effector"} else (0.55 if endpoint else 0.05)
        control_likelihood = 0.95 if helper_tokens & {"control", "ctrl", "controller", "ik", "fk", "pole", "target", "mch", "org"} else 0.05
        twist_likelihood = 0.9 if helper_tokens & {"twist", "roll"} else 0.05
        helper_likelihood = max(
            control_likelihood,
            0.95 if helper_tokens & {"helper", "socket", "holder", "attachment", "camera", "cam"} else 0.0,
            endpoint_likelihood * (0.7 if name not in animated_bones and skin_weights.get(name, 0.0) <= 0.0 else 0.25),
        )
        role = role_by_bone.get(name, ("", 0.0))[0]
        deform_likelihood = (
            1.0 if skin_weights.get(name, 0.0) > 0.0
            else 0.82 if role and not helper_tokens
            else 0.25 if name in animated_bones
            else max(0.0, 0.45 - helper_likelihood * 0.4)
        )
        coordinate = body_coords.get(name)
        geometric_side = ""
        if coordinate is not None and abs(float(coordinate[0])) >= 0.025:
            geometric_side = "right" if coordinate[0] > 0.0 else "left"
        side_conflict = bool(
            evidence.side and geometric_side and evidence.side != geometric_side
        )
        if side_conflict:
            findings.append(AnalysisFinding(
                "side_name_geometry_conflict", "action_required",
                f"Bone {name!r} has {evidence.side} name evidence but lies on the {geometric_side} bind-space side.",
                (name,),
                ("the name token does not override contradictory geometry",),
            ))
        partner, symmetry_score = symmetry.get(name, ("", 0.0))
        analyzed_nodes.append(AnalyzedBone(
            object_id=int(limb_models[name]),
            name=name,
            parent_name=parent,
            immediate_parent_name=immediate_by_bone.get(name, parent),
            wrapper_ancestors=wrappers_by_bone.get(name, ()),
            children=children.get(name, ()),
            depth=depth,
            child_count=len(children.get(name, ())),
            descendant_count=_descendants(name, children),
            bind_global_matrix=_matrix_tuple(bind_globals.get(name)),
            bind_local_matrix=_matrix_tuple(bind_locals.get(name)),
            bind_source=str(bind_sources.get(name, fallback_source)),
            bind_position=_vector_tuple(position),
            normalized_body_position=_vector_tuple(coordinate),
            segment_length=(float(length) if length is not None else None),
            symmetry_partner=partner,
            symmetry_score=float(symmetry_score),
            skin_weight=float(skin_weights.get(name, 0.0)),
            skin_influence_centroid=skin_centroids.get(name),
            skin_influence_bounds=skin_bounds.get(name),
            animated_components=components.get(name, frozenset()),
            helper_likelihood=float(helper_likelihood),
            control_likelihood=float(control_likelihood),
            endpoint_likelihood=float(endpoint_likelihood),
            twist_likelihood=float(twist_likelihood),
            deform_likelihood=float(deform_likelihood),
            inferred_side=evidence.side or geometric_side,
            side_conflict=side_conflict,
            semantic_role=role,
            name_evidence=evidence,
        ))
    if cycle_names:
        findings.append(AnalysisFinding(
            "skeleton_hierarchy_cycle", "blocking",
            "The collapsed LimbNode hierarchy contains a cycle.", tuple(cycle_names),
        ))

    invalid_bind_count = len(names) - len(bind_globals)
    classification = classify_skeleton_archetype(
        analyzed_nodes,
        roles,
        body_frame=body_frame,
        invalid_bind_count=invalid_bind_count,
    )
    for reason in classification.rejected_routes:
        findings.append(AnalysisFinding(
            "conservative_archetype_rejection", "info", reason
        ))

    roots = tuple(name for name in names if parents.get(name) not in limb_models)
    skeleton_payload = [
        {"name": name, "parent": parents.get(name)}
        for name in sorted(names, key=lambda value: (name_rows[value].normalized_unicode_name, value))
    ]
    skeleton_hash = sha256(json.dumps(
        skeleton_payload, sort_keys=True, separators=(",", ":"), ensure_ascii=False
    ).encode("utf-8")).hexdigest()
    bind_payload = {
        "skeleton_hash": skeleton_hash,
        "matrices": {
            name: _matrix_tuple(bind_globals[name])
            for name in sorted(bind_globals)
        },
    }
    bind_hash = sha256(json.dumps(
        bind_payload, sort_keys=True, separators=(",", ":"), ensure_ascii=False
    ).encode("utf-8")).hexdigest()

    all_language_script = sorted({
        *(f"language:{value}" for row in name_rows.values() for value in row.languages),
        *(f"script:{value}" for row in name_rows.values() for value in row.scripts),
    })
    family_hints = detect_source_family_hints(names, wrapper_names)
    selected_stack = getattr(document, "selected_animation_stack", None)
    stack_name = str(getattr(selected_stack, "name", "") or animation_stack or "")
    animation_hash = _animation_fingerprint(document, components, stack_name)
    animated_chains_detected = tuple(sorted(
        name
        for name, chain in semantic_chains.items()
        if any(bone in animated_bones for bone in chain.bone_names)
    ))
    role_bones = {candidate.bone_name for candidate in roles.values()}
    unresolved_animated_chains = tuple(sorted(
        name for name in animated_bones
        if name not in role_bones
        and name not in roots
        and not name_rows[name].likely_helper
    ))
    scene = getattr(document, "scene", None)
    axis_settings = dict(getattr(scene, "axis_settings", {}) or {})
    bind_coverage = dict(getattr(document, "bind_coverage", {}) or {})
    if not bind_coverage:
        bind_coverage = {
            "authoritative": sum(bool(bind_sources.get(name)) for name in bind_globals),
            "total": len(names),
        }
    return SourceSkeletonAnalysis(
        skeleton_hash=skeleton_hash,
        bind_hash=bind_hash,
        animation_hash=animation_hash,
        roots=roots,
        nodes=tuple(analyzed_nodes),
        body_frame=body_frame,
        archetype=classification.archetype,
        archetype_confidence=classification.confidence,
        semantic_roles=MappingProxyType(dict(sorted(roles.items()))),
        semantic_chains=MappingProxyType(dict(sorted(semantic_chains.items()))),
        animated_bones=animated_bones,
        animated_components=MappingProxyType(dict(sorted(components.items()))),
        source_family_hints=family_hints,
        findings=tuple(findings),
        animation_domain=_animation_domain(components, roles, roots),
        animated_chains_detected=animated_chains_detected,
        unresolved_animated_chains=unresolved_animated_chains,
        source_name_languages_or_scripts=tuple(all_language_script),
        model_graph=model_graph,
        wrapper_models=wrapper_names,
        meters_per_unit=float(getattr(document, "meters_per_unit", 1.0) or 1.0),
        axis_settings=MappingProxyType(axis_settings),
        handedness=_handedness(document),
        selected_animation_stack=stack_name,
        bind_coverage=MappingProxyType({str(key): int(value) for key, value in bind_coverage.items()}),
    )


__all__ = [
    "AnalysisFinding",
    "AnalyzedBone",
    "BodyFrame",
    "RoleCandidate",
    "SOURCE_SKELETON_ANALYZER_VERSION",
    "SemanticChain",
    "SourceModelNode",
    "SourceSkeletonAnalysis",
    "analyze_source_skeleton",
]
