"""Deterministic, inspectable automatic retarget plans and certificates.

This module deliberately sits above the legacy global one-to-one suggestion
mapper.  It consumes the immutable result of ``analyze_source_skeleton`` and a
target-domain policy, produces one decision for every target bone, and only
materializes a build-authorizing ``GenericBoneMap`` for a narrowly verified
built-in bridge.

The analyzer and target-policy modules are imported lazily.  Keeping the
boundary duck typed makes the planner usable by compact synthetic tests and by
future analyzers without teaching it vendor-specific FBX names.
"""

from __future__ import annotations

from dataclasses import asdict, dataclass, field, is_dataclass
import hashlib
import json
import math
import re
import unicodedata
from typing import Any, Iterable, Mapping, Sequence

from .bone_maps import (
    BoneMapPair,
    GenericBoneMap,
    skeleton_signature,
)


AUTOMATIC_RETARGET_PLAN_FORMAT = "dl-reanimated-automatic-retarget-plan-v1"
AUTOMATIC_RETARGET_VALIDATION_FORMAT = (
    "dl-reanimated-automatic-retarget-validation-v1"
)
DL2_ADVANCED_BODY_CERTIFICATE_FORMAT = "dl2_advanced_body_bridge_v1"
DL2_ADVANCED_RIG_ID = "builtin:dl2_player_advanced"
DL2_LEGACY_BODY_CERTIFICATE_FORMAT = "dl2_legacy_body_bridge_v1"
DL2_LEGACY_RIG_ID = "builtin:dl2_player_shadow_caster"
DL2_BUNDLED_BODY_CERTIFICATE_FORMATS = frozenset(
    {DL2_ADVANCED_BODY_CERTIFICATE_FORMAT, DL2_LEGACY_BODY_CERTIFICATE_FORMAT}
)

PLANNER_VERSION = "automatic-retarget-planner-v1"
DEFAULT_ANALYZER_VERSION = "source-skeleton-analyzer-v1"
DEFAULT_SEMANTIC_POLICY_VERSION = "semantic-chain-policy-v1"
DEFAULT_LEXICON_VERSION = "multilingual-anatomy-lexicon-v1"

MAPPING_MODES = frozenset(
    {
        "direct",
        "composed",
        "distributed",
        "inherit_bind",
        "static_bind",
        "ignored_source",
        "manual_required",
    }
)
BUILDABLE_MAPPING_MODES = frozenset(
    {"direct", "composed", "distributed", "inherit_bind", "static_bind"}
)

_NON_BODY_CATEGORIES = frozenset(
    {"facial", "secondary_animation", "collar", "camera", "attachment"}
)
_OPTIONAL_ROLE_SUFFIXES = (
    "_hand",
    "_foot",
    "_toe",
    "_clavicle",
    "_neck",
    "_head",
)
_CRITICAL_ROLES = frozenset(
    {
        "pelvis",
        "spine_1",
        "spine_2",
        "spine_3",
        "head",
        "l_upperarm",
        "l_forearm",
        "r_upperarm",
        "r_forearm",
        "l_thigh",
        "l_calf",
        "r_thigh",
        "r_calf",
        "left_upper_arm",
        "left_forearm",
        "right_upper_arm",
        "right_forearm",
        "left_thigh",
        "left_calf",
        "right_thigh",
        "right_calf",
    }
)
_SPATIAL_ONLY_EVIDENCE = frozenset(
    {"spatial", "spatial_bind", "nearest", "nearest_position", "bind_pivot"}
)


def _plain(value: Any) -> Any:
    """Return a stable JSON-native value without discarding Unicode."""

    if is_dataclass(value):
        return _plain(asdict(value))
    if isinstance(value, Mapping):
        return {str(key): _plain(item) for key, item in value.items()}
    if isinstance(value, (tuple, list, set, frozenset)):
        return [_plain(item) for item in value]
    if isinstance(value, float):
        if not math.isfinite(value):
            return str(value)
        return round(value, 12)
    return value


def _stable_hash(payload: Any) -> str:
    encoded = json.dumps(
        _plain(payload),
        sort_keys=True,
        ensure_ascii=False,
        separators=(",", ":"),
    ).encode("utf-8")
    return hashlib.sha256(encoded).hexdigest()


def _value(value: Any, name: str, default: Any = None) -> Any:
    if isinstance(value, Mapping):
        return value.get(name, default)
    return getattr(value, name, default)


def _first_value(value: Any, names: Iterable[str], default: Any = None) -> Any:
    for name in names:
        found = _value(value, name, None)
        if found is not None and found != "":
            return found
    return default


def _tuple(value: Any) -> tuple[Any, ...]:
    if value is None:
        return ()
    if isinstance(value, tuple):
        return value
    if isinstance(value, (str, bytes)):
        return (value,)
    if isinstance(value, Mapping):
        return tuple(value)
    try:
        return tuple(value)
    except TypeError:
        return (value,)


@dataclass(frozen=True, slots=True)
class MappingEvidence:
    kind: str
    score: float = 0.0
    detail: str = ""
    source: str = "analyzer"

    def to_dict(self) -> dict[str, Any]:
        return asdict(self)


@dataclass(frozen=True, slots=True)
class RoleMappingOverride:
    """One explicit semantic choice supplied by the visible Retargeting UI."""

    semantic_role: str
    mode: str = "auto"
    source_bone: str = ""
    profile_role: str = ""

    def __post_init__(self) -> None:
        if self.mode not in {"auto", "direct", "inherit_bind", "static_bind"}:
            raise ValueError(f"unsupported role override mode: {self.mode}")
        if self.mode == "direct" and not self.source_bone:
            raise ValueError(
                f"direct role override {self.semantic_role!r} has no source bone"
            )

    def to_dict(self) -> dict[str, Any]:
        return asdict(self)


@dataclass(frozen=True, slots=True)
class MappingDecision:
    target_bone: str
    target_descriptor: int
    target_category: str
    mode: str
    source_bones: tuple[str, ...] = ()
    semantic_role: str = ""
    confidence: float = 0.0
    confidence_margin: float = 0.0
    evidence: tuple[MappingEvidence, ...] = ()
    reason: str = ""
    critical: bool = False
    animated: bool = False
    parent_target_bone: str = ""

    def to_dict(self) -> dict[str, Any]:
        return _plain(asdict(self))


@dataclass(frozen=True, slots=True)
class AutomaticRetargetPlan:
    source_skeleton_hash: str
    source_name_parent_hash: str
    source_bind_hash: str
    source_animation_hash: str
    target_rig_id: str
    target_skeleton_hash: str
    target_policy_id: str
    clip_domain: str
    source_archetype: str
    source_archetype_confidence: float
    decisions: tuple[MappingDecision, ...]
    analyzer_version: str = DEFAULT_ANALYZER_VERSION
    planner_version: str = PLANNER_VERSION
    semantic_policy_version: str = DEFAULT_SEMANTIC_POLICY_VERSION
    lexicon_version: str = DEFAULT_LEXICON_VERSION
    source_family_hints: tuple[str, ...] = ()
    source_name_languages_or_scripts: tuple[str, ...] = ()
    animated_chains_detected: tuple[str, ...] = ()
    unresolved_animated_chains: tuple[str, ...] = ()
    optional_missing_source_roles: tuple[str, ...] = ()
    findings: tuple[dict[str, Any], ...] = ()
    warnings_shown_to_user: tuple[str, ...] = ()
    diagnostic_findings_suppressed_from_basic_ui: int = 0
    exact_identity: bool = False
    observed_motion_domain: str = "body"
    manual_override_count: int = 0
    role_overrides: tuple[dict[str, Any], ...] = ()
    format: str = AUTOMATIC_RETARGET_PLAN_FORMAT

    @property
    def mapping_modes(self) -> dict[str, int]:
        return {
            mode: sum(row.mode == mode for row in self.decisions)
            for mode in sorted(MAPPING_MODES)
        }

    @property
    def target_row_count(self) -> int:
        return len(self.decisions)

    @property
    def unresolved_required_roles(self) -> tuple[str, ...]:
        return tuple(
            row.semantic_role or row.target_bone
            for row in self.decisions
            if row.mode == "manual_required"
        )

    @property
    def plan_hash(self) -> str:
        payload = self.to_dict()
        payload.pop("plan_hash", None)
        return _stable_hash(payload)

    def to_dict(self) -> dict[str, Any]:
        payload = _plain(asdict(self))
        payload["mapping_modes"] = self.mapping_modes
        payload["target_row_count"] = self.target_row_count
        payload["unresolved_required_roles"] = list(
            self.unresolved_required_roles
        )
        payload["plan_hash"] = _stable_hash(payload)
        return payload


@dataclass(frozen=True, slots=True)
class AutomaticRetargetValidation:
    status: str
    errors: tuple[str, ...] = ()
    warnings: tuple[str, ...] = ()
    certificate: dict[str, Any] = field(default_factory=dict)
    plan_hash: str = ""
    # A plan-only validation deliberately leaves this false.  Routing may rely
    # on a certificate only after the serialized map was compared with freshly
    # recomputed source/target identities and decisions.
    live_revalidated: bool = False
    format: str = AUTOMATIC_RETARGET_VALIDATION_FORMAT

    @property
    def ok(self) -> bool:
        return self.status == "pass" and not self.errors

    @property
    def certificate_format(self) -> str:
        return str(self.certificate.get("format", "") or "")

    @property
    def revalidated(self) -> bool:
        return self.live_revalidated

    def require_valid(self) -> None:
        if self.ok:
            return
        raise ValueError(
            "Automatic retarget plan verification failed:\n- "
            + "\n- ".join(self.errors or ("unknown verification failure",))
        )

    def to_dict(self) -> dict[str, Any]:
        payload = _plain(asdict(self))
        payload["certificate_format"] = self.certificate_format
        payload["revalidated"] = self.revalidated
        return payload


@dataclass(frozen=True, slots=True)
class RetargetReadiness:
    state: str
    severity: str
    label: str
    reason: str
    action: str = ""
    details: tuple[str, ...] = ()

    @property
    def ready(self) -> bool:
        return self.state == "ready"

    def to_dict(self) -> dict[str, Any]:
        return asdict(self)


@dataclass(frozen=True, slots=True)
class _FallbackTargetPolicy:
    policy_id: str
    version: str = DEFAULT_SEMANTIC_POLICY_VERSION
    archetype: str = "humanoid"
    minimum_confidence: float = 0.70
    minimum_confidence_margin: float = 0.08
    clip_domain: str = "body"


@dataclass(frozen=True, slots=True)
class _Candidate:
    bone_name: str
    confidence: float
    margin: float
    side: str
    evidence: tuple[MappingEvidence, ...]
    endpoint: bool
    ambiguous: bool
    spatial_only: bool


def _looks_like_analysis(value: Any) -> bool:
    return bool(
        _first_value(value, ("skeleton_hash", "source_skeleton_hash"), "")
        and _value(value, "semantic_roles", None) is not None
        and _value(value, "nodes", None) is not None
    )


def _coerce_analysis(source: Any) -> Any:
    if _looks_like_analysis(source):
        return source
    try:
        from .skeleton_analysis import analyze_source_skeleton
    except ImportError as exc:  # pragma: no cover - exercised during integration
        raise TypeError(
            "Expected SourceSkeletonAnalysis or an FBX document consumable by "
            "analyze_source_skeleton()."
        ) from exc
    stack = _first_value(
        _value(source, "selected_animation_stack", None), ("name",), ""
    )
    try:
        return analyze_source_skeleton(source, animation_stack=stack or None)
    except TypeError:
        return analyze_source_skeleton(source)


def _coerce_policy(target_rig: Any, target_policy: Any, clip_domain: str) -> Any:
    if target_policy is not None:
        return target_policy
    try:
        from .target_retarget_policy import build_target_retarget_policy

        try:
            return build_target_retarget_policy(
                target_rig, clip_domain=clip_domain
            )
        except TypeError:
            return build_target_retarget_policy(target_rig)
    except ImportError:
        return _FallbackTargetPolicy(
            policy_id=(
                DL2_ADVANCED_BODY_CERTIFICATE_FORMAT
                if str(_value(target_rig, "rig_id", "")) == DL2_ADVANCED_RIG_ID
                else DL2_LEGACY_BODY_CERTIFICATE_FORMAT
                if str(_value(target_rig, "rig_id", "")) == DL2_LEGACY_RIG_ID
                else "generic_humanoid_target_v1"
            ),
            clip_domain=clip_domain,
        )


def _policy_identity(policy: Any) -> tuple[str, str, str, float, float]:
    policy_id = str(
        _first_value(policy, ("policy_id", "target_policy_id", "id"), "")
    )
    declared_minimum = _value(policy, "minimum_confidence", None)
    if declared_minimum is None:
        # The built-in policy is paired with an analyzer whose topology-backed
        # neck/arm assignments intentionally bottom out at 0.55/0.62.
        declared_minimum = (
            0.50
            if policy_id in DL2_BUNDLED_BODY_CERTIFICATE_FORMATS
            else 0.70
        )
    return (
        policy_id,
        str(
            _first_value(
                policy,
                ("version", "policy_version", "semantic_policy_version"),
                DEFAULT_SEMANTIC_POLICY_VERSION,
            )
        ),
        str(_first_value(policy, ("archetype", "target_archetype"), "humanoid")),
        float(declared_minimum),
        float(_value(policy, "minimum_confidence_margin", 0.08)),
    )


def _node_name(node: Any) -> str:
    return str(
        _first_value(
            node,
            ("original_name", "bone_name", "source_bone", "name"),
            "",
        )
    )


def _node_parent(node: Any) -> str:
    parent = _first_value(
        node,
        ("parent_name", "parent_bone", "parent", "parent_original_name"),
        "",
    )
    if not isinstance(parent, (str, int, float, type(None))):
        parent = _node_name(parent)
    return str(parent or "")


def _node_endpoint(node: Any) -> bool:
    if bool(
        _first_value(
            node,
            ("is_endpoint", "endpoint", "terminal_helper", "is_end_bone"),
            False,
        )
    ):
        return True
    likelihood = float(_value(node, "endpoint_likelihood", 0.0) or 0.0)
    name = unicodedata.normalize("NFKC", _node_name(node)).casefold()
    tokens = set(re.split(r"[^\w]+", name))
    return likelihood >= 0.75 or bool(
        {"end", "nub", "tip", "effector"}.intersection(tokens)
    )


def _analysis_nodes(analysis: Any) -> tuple[Any, ...]:
    return _tuple(_value(analysis, "nodes", ()))


def _analysis_node_map(analysis: Any) -> dict[str, Any]:
    return {
        name: node
        for node in _analysis_nodes(analysis)
        if (name := _node_name(node))
    }


def _profile_source_skeleton_hash(analysis: Any) -> str:
    """Return the legacy GenericBoneMap source identity used by mapped builds."""

    return skeleton_signature(
        (_node_name(node), _node_parent(node) or None)
        for node in sorted(_analysis_nodes(analysis), key=_node_name)
    )


def _matrix_payload(value: Any) -> Any:
    try:
        rows = value.tolist()
    except AttributeError:
        rows = value
    return _plain(rows)


def _analysis_hashes(analysis: Any) -> tuple[str, str, str, str]:
    nodes = _analysis_nodes(analysis)
    name_parent_rows = sorted(
        ((_node_name(node), _node_parent(node) or None) for node in nodes),
        key=lambda row: (unicodedata.normalize("NFKC", row[0]).casefold(), row[0]),
    )
    name_parent_hash = str(
        _first_value(
            analysis,
            ("name_parent_hash", "hierarchy_hash", "source_name_parent_hash"),
            "",
        )
        or skeleton_signature(name_parent_rows)
    )
    skeleton_hash = str(
        _first_value(
            analysis,
            ("skeleton_hash", "source_skeleton_hash"),
            "",
        )
        or name_parent_hash
    )
    bind_hash = str(_value(analysis, "bind_hash", "") or "")
    if not bind_hash:
        bind_rows = []
        for node in nodes:
            bind_rows.append(
                {
                    "name": _node_name(node),
                    "parent": _node_parent(node),
                    "bind_global": _matrix_payload(
                        _first_value(
                            node,
                            (
                                "bind_global",
                                "bind_global_matrix",
                                "global_bind",
                                "bind_matrix",
                                "bind_position",
                            ),
                            None,
                        )
                    ),
                }
            )
        bind_hash = _stable_hash(bind_rows)
    animation_hash = str(_value(analysis, "animation_hash", "") or "")
    if not animation_hash:
        components = _value(analysis, "animated_components", {}) or {}
        animation_hash = _stable_hash(
            {
                "stack": str(
                    _first_value(
                        analysis,
                        ("animation_stack", "selected_animation_stack"),
                        "",
                    )
                ),
                "animated_bones": sorted(
                    str(value)
                    for value in _tuple(_value(analysis, "animated_bones", ()))
                ),
                "components": {
                    str(key): sorted(str(item) for item in _tuple(value))
                    for key, value in dict(components).items()
                },
            }
        )
    return skeleton_hash, name_parent_hash, bind_hash, animation_hash


def _evidence_rows(value: Any, role: str) -> tuple[MappingEvidence, ...]:
    rows: list[MappingEvidence] = []
    for item in _tuple(value):
        if isinstance(item, Mapping):
            rows.append(
                MappingEvidence(
                    str(
                        _first_value(
                            item, ("kind", "type", "category", "method"), "evidence"
                        )
                    ),
                    float(_value(item, "score", _value(item, "weight", 0.0)) or 0.0),
                    str(_first_value(item, ("detail", "reason", "description"), "")),
                    str(_value(item, "source", "analyzer")),
                )
            )
        elif is_dataclass(item) or hasattr(item, "kind"):
            rows.append(
                MappingEvidence(
                    str(_first_value(item, ("kind", "type", "method"), "evidence")),
                    float(_value(item, "score", 0.0) or 0.0),
                    str(_first_value(item, ("detail", "reason"), "")),
                    str(_value(item, "source", "analyzer")),
                )
            )
        elif item:
            rows.append(MappingEvidence(str(item), 0.0, "", "analyzer"))
    if not rows:
        rows.append(
            MappingEvidence(
                "semantic_role", 1.0, f"analyzer assigned {role}", "analyzer"
            )
        )
    return tuple(rows)


def _candidate_objects(value: Any) -> tuple[Any, ...]:
    if value is None:
        return ()
    nested = _value(value, "candidates", None)
    if nested is not None:
        return _tuple(nested)
    if isinstance(value, Sequence) and not isinstance(value, (str, bytes)):
        return tuple(value)
    return (value,)


def _role_aliases(role: str) -> tuple[str, ...]:
    """Return lossless policy/analyzer spellings for one semantic role."""

    value = str(role or "")
    aliases = [value]
    if value.startswith("left_"):
        aliases.append("l_" + value[5:])
    elif value.startswith("right_"):
        aliases.append("r_" + value[6:])
    elif value.startswith("l_"):
        aliases.append("left_" + value[2:])
    elif value.startswith("r_"):
        aliases.append("right_" + value[2:])
    expanded: list[str] = []
    for item in aliases:
        expanded.append(item)
        if "upper_arm" in item:
            expanded.append(item.replace("upper_arm", "upperarm"))
        elif "upperarm" in item:
            expanded.append(item.replace("upperarm", "upper_arm"))
    return tuple(dict.fromkeys(expanded))


def _coerce_role_overrides(
    value: Mapping[str, Any] | Iterable[RoleMappingOverride] | None,
) -> dict[str, RoleMappingOverride]:
    if value is None:
        return {}
    rows: list[RoleMappingOverride] = []
    if isinstance(value, Mapping):
        for semantic_role, raw in value.items():
            if isinstance(raw, RoleMappingOverride):
                row = raw
            elif isinstance(raw, str):
                row = RoleMappingOverride(str(semantic_role), "direct", raw)
            elif isinstance(raw, Mapping):
                row = RoleMappingOverride(
                    str(raw.get("semantic_role", semantic_role)),
                    str(raw.get("mode", "auto") or "auto"),
                    str(raw.get("source_bone", "") or ""),
                    str(raw.get("profile_role", "") or ""),
                )
            else:
                raise TypeError(
                    f"unsupported role override for {semantic_role!r}: "
                    f"{type(raw).__name__}"
                )
            rows.append(row)
    else:
        for raw in value:
            rows.append(
                raw
                if isinstance(raw, RoleMappingOverride)
                else RoleMappingOverride(**dict(raw))
            )
    result: dict[str, RoleMappingOverride] = {}
    for row in rows:
        if row.mode == "auto":
            continue
        for alias in _role_aliases(row.semantic_role):
            result[alias] = row
    return result


def _role_override(
    overrides: Mapping[str, RoleMappingOverride], role: str
) -> RoleMappingOverride | None:
    for alias in _role_aliases(role):
        if alias in overrides:
            return overrides[alias]
    return None


def _candidate_for_role(
    analysis: Any, role: str, node_by_name: Mapping[str, Any]
) -> _Candidate | None:
    roles = _value(analysis, "semantic_roles", {}) or {}
    raw = None
    if isinstance(roles, Mapping):
        for alias in _role_aliases(role):
            if alias in roles:
                raw = roles[alias]
                break
    candidates: list[_Candidate] = []
    raw_items = _candidate_objects(raw)
    scored: list[tuple[float, Any]] = []
    for item in raw_items:
        name = (
            str(item)
            if isinstance(item, str)
            else str(
                _first_value(
                    item,
                    (
                        "bone_name",
                        "source_bone",
                        "source_name",
                        "original_name",
                        "name",
                    ),
                    "",
                )
            )
        )
        if not name:
            continue
        confidence = float(_value(item, "confidence", 1.0) or 0.0)
        scored.append((confidence, item))
    scored.sort(
        key=lambda row: (
            -row[0],
            unicodedata.normalize(
                "NFKC",
                str(
                    _first_value(
                        row[1],
                        ("bone_name", "source_bone", "source_name", "name"),
                        row[1] if isinstance(row[1], str) else "",
                    )
                ),
            ).casefold(),
        )
    )
    for index, (confidence, item) in enumerate(scored):
        name = (
            str(item)
            if isinstance(item, str)
            else str(
                _first_value(
                    item,
                    (
                        "bone_name",
                        "source_bone",
                        "source_name",
                        "original_name",
                        "name",
                    ),
                    "",
                )
            )
        )
        runner = scored[index + 1][0] if index + 1 < len(scored) else 0.0
        margin = float(
            _value(
                item,
                "confidence_margin",
                _value(item, "margin", confidence - runner),
            )
            or 0.0
        )
        evidence = _evidence_rows(_value(item, "evidence", ()), role)
        kinds = {row.kind.casefold() for row in evidence}
        method = str(_value(item, "method", "") or "").casefold()
        node = node_by_name.get(name)
        side = str(_value(item, "side", "") or "")
        candidates.append(
            _Candidate(
                name,
                confidence,
                margin,
                side,
                evidence,
                bool(_value(item, "endpoint", False))
                or (node is not None and _node_endpoint(node)),
                bool(_value(item, "ambiguous", False))
                or bool(node is not None and _value(node, "side_conflict", False)),
                bool(_value(item, "spatial_only", False))
                or method in _SPATIAL_ONLY_EVIDENCE
                or bool(kinds) and kinds <= _SPATIAL_ONLY_EVIDENCE,
            )
        )
    return candidates[0] if candidates else None


def _target_category(bone: Any, info: Any = None) -> str:
    explicit = str(
        _first_value(info, ("category", "domain", "target_category"), "")
        or ""
    ).casefold()
    if explicit:
        return explicit
    tags = {str(value).casefold() for value in _tuple(_value(bone, "tags", ())) }
    for category in (
        "facial",
        "secondary_animation",
        "collar",
        "camera",
        "attachment",
        "body",
    ):
        if category in tags:
            return category
    return "helper" if bool(_value(bone, "helper", False)) else "body"


def _target_info(policy: Any, bone: Any, clip_domain: str) -> Any:
    classifier = _value(policy, "classify_bone", None)
    if callable(classifier):
        for args in ((bone, clip_domain), (bone,), (str(_value(bone, "name", "")),)):
            try:
                return classifier(*args)
            except TypeError:
                continue
    name = str(_value(bone, "name", ""))
    for field_name in ("rows", "bone_policies", "target_rows", "bones"):
        values = _value(policy, field_name, None)
        if isinstance(values, Mapping) and name in values:
            return values[name]
        for item in _tuple(values):
            if str(
                _first_value(item, ("target_bone", "bone_name", "name"), "")
            ) == name:
                return item
    return None


def _fallback_target_role(name: str, rig_id: str) -> str:
    value = unicodedata.normalize("NFKC", str(name)).casefold()
    if rig_id in {DL2_ADVANCED_RIG_ID, DL2_LEGACY_RIG_ID}:
        fixed = {
            "pelvis": "pelvis",
            "spine": "spine_1",
            "spine2": "spine_2",
            "spine3": "spine_3",
            "neck": "neck_1",
            "head": "head",
            "l_thigh": "l_thigh",
            "l_calf": "l_calf",
            "l_foot": "l_foot",
            "l_toebase": "l_toe",
            "r_thigh": "r_thigh",
            "r_calf": "r_calf",
            "r_foot": "r_foot",
            "r_toebase": "r_toe",
            "l_clavicle": "l_clavicle",
            "l_upperarm": "l_upperarm",
            "l_forearm": "l_forearm",
            "l_hand": "l_hand",
            "r_clavicle": "r_clavicle",
            "r_upperarm": "r_upperarm",
            "r_forearm": "r_forearm",
            "r_hand": "r_hand",
        }
        if value in fixed:
            return fixed[value]
        finger = re.fullmatch(r"([lr])_finger([0-4])([0-4])", value)
        if finger:
            side, digit_text, segment_text = finger.groups()
            digit = int(digit_text)
            segment = int(segment_text)
            names = ("thumb", "index", "middle", "ring", "pinky")
            if digit == 0 and 1 <= segment <= 3:
                return f"{side}_{names[digit]}_{segment}"
            # DL2 finger10/20/30/40 are metacarpal/base rows.  Source phalanx
            # 1 starts at target 11/21/31/41 and endpoint phalanx 4 is unused.
            if digit > 0 and 1 <= segment <= 3:
                return f"{side}_{names[digit]}_{segment}"
            return ""
        return ""
    try:
        from .retarget_mapping import canonical_humanoid_role

        return str(canonical_humanoid_role(name) or "")
    except (ImportError, ValueError):
        return ""


def _target_role(policy: Any, bone: Any, info: Any, rig_id: str) -> str:
    explicit = str(
        _first_value(info, ("semantic_role", "role", "humanoid_role"), "")
        or ""
    )
    return explicit or _fallback_target_role(str(_value(bone, "name", "")), rig_id)


def _target_parent_name(target_rig: Any, bone: Any) -> str:
    parent_index = int(_value(bone, "parent_index", -1))
    bones = _tuple(_value(target_rig, "bones", ()))
    if 0 <= parent_index < len(bones):
        return str(_value(bones[parent_index], "name", ""))
    return ""


def _canonical_side(value: str) -> str:
    side = str(value or "").strip().casefold()
    if side in {"l", "left", "lhs"}:
        return "l"
    if side in {"r", "right", "rhs"}:
        return "r"
    return ""


def _role_side(role: str) -> str:
    value = str(role or "").casefold()
    if value.startswith(("l_", "left_")):
        return "l"
    if value.startswith(("r_", "right_")):
        return "r"
    return ""


def _role_chain(role: str) -> str:
    value = str(role or "").casefold()
    side = _role_side(value)
    if side:
        if any(token in value for token in ("thigh", "calf", "foot", "toe")):
            return f"{side}_leg"
        if any(
            token in value
            for token in ("clavicle", "upperarm", "upper_arm", "forearm", "hand")
        ):
            return f"{side}_arm"
        for finger in ("thumb", "index", "middle", "ring", "pinky"):
            if finger in value:
                return f"{side}_{finger}"
    if value.startswith("spine") or value == "pelvis":
        return "spine"
    if value in {"neck_1", "head"}:
        return "neck_head"
    return value


def _canonical_chain(value: str) -> str:
    chain = str(value or "").strip().casefold()
    if chain.startswith("left_"):
        return "l_" + chain[5:]
    if chain.startswith("right_"):
        return "r_" + chain[6:]
    return chain


def _role_is_critical(role: str) -> bool:
    return any(alias in _CRITICAL_ROLES for alias in _role_aliases(role))


def _role_animated(analysis: Any, role: str, candidate: _Candidate | None) -> bool:
    animated = {
        str(value) for value in _tuple(_value(analysis, "animated_bones", ()))
    }
    if candidate is not None and candidate.bone_name in animated:
        return True
    animated_chains = {
        str(value)
        for value in _tuple(
            _first_value(
                analysis,
                ("animated_chains_detected", "animated_chains"),
                (),
            )
        )
    }
    canonical_chains = {_canonical_chain(value) for value in animated_chains}
    if (
        any(alias in animated_chains for alias in _role_aliases(role))
        or _canonical_chain(_role_chain(role)) in canonical_chains
    ):
        return True
    observed = str(
        _first_value(
            analysis,
            ("observed_motion_domain", "animation_domain", "clip_domain"),
            "",
        )
        or ""
    ).casefold()
    if observed in {"full_body", "body"}:
        return _role_is_critical(role)
    if observed == "upper_body":
        return _role_chain(role) in {
            "spine",
            "neck_head",
            "l_arm",
            "r_arm",
        }
    if observed == "lower_body":
        return _role_chain(role) in {"l_leg", "r_leg"} or role == "pelvis"
    return False


def _exact_identity(
    analysis: Any, target_rig: Any
) -> tuple[bool, dict[str, str]]:
    nodes = _analysis_node_map(analysis)
    targets = _tuple(_value(target_rig, "bones", ()))
    if not nodes or not targets:
        return False, {}
    normalized_source: dict[str, list[str]] = {}
    for name in nodes:
        key = unicodedata.normalize("NFKC", name).casefold()
        normalized_source.setdefault(key, []).append(name)
    matched: dict[str, str] = {}
    for bone in targets:
        target_name = str(_value(bone, "name", ""))
        key = unicodedata.normalize("NFKC", target_name).casefold()
        candidates = normalized_source.get(key, ())
        if len(candidates) != 1:
            return False, {}
        matched[target_name] = candidates[0]
    for bone in targets:
        target_name = str(_value(bone, "name", ""))
        expected_parent = _target_parent_name(target_rig, bone)
        actual_parent = _node_parent(nodes[matched[target_name]])
        normalized_actual = (
            unicodedata.normalize("NFKC", actual_parent).casefold()
            if actual_parent
            else ""
        )
        normalized_expected = (
            unicodedata.normalize("NFKC", matched[expected_parent]).casefold()
            if expected_parent
            else ""
        )
        if normalized_actual != normalized_expected:
            return False, {}
    return True, matched


def _bind_mode(category: str, parent: str) -> str:
    if category in _NON_BODY_CATEGORIES or category in {"helper", "socket"}:
        return "static_bind"
    return "inherit_bind" if parent else "static_bind"


def _decision(
    bone: Any,
    target_rig: Any,
    *,
    category: str,
    mode: str,
    role: str = "",
    sources: Iterable[str] = (),
    confidence: float = 0.0,
    margin: float = 0.0,
    evidence: Iterable[MappingEvidence] = (),
    reason: str,
    critical: bool = False,
    animated: bool = False,
) -> MappingDecision:
    return MappingDecision(
        target_bone=str(_value(bone, "name", "")),
        target_descriptor=int(_value(bone, "descriptor", 0)),
        target_category=category,
        mode=mode,
        source_bones=tuple(str(value) for value in sources if str(value)),
        semantic_role=role,
        confidence=float(confidence),
        confidence_margin=float(margin),
        evidence=tuple(evidence),
        reason=reason,
        critical=critical,
        animated=animated,
        parent_target_bone=_target_parent_name(target_rig, bone),
    )


def _chain_bone_names(value: Any) -> tuple[str, ...]:
    raw = _first_value(
        value,
        ("target_bones", "source_bones", "bone_names", "bones", "nodes"),
        value if isinstance(value, Sequence) and not isinstance(value, str) else (),
    )
    result: list[str] = []
    for item in _tuple(raw):
        name = str(item) if isinstance(item, str) else _node_name(item)
        if name:
            result.append(name)
    return tuple(result)


def _decision_distribution_metadata(
    row: MappingDecision,
) -> tuple[float, str]:
    evidence = next(
        (
            item
            for item in row.evidence
            if item.kind.casefold() == "semantic_chain_distribution"
        ),
        None,
    )
    if evidence is None:
        return 0.0, ""
    return float(evidence.score), str(evidence.detail)


def _apply_declared_chain_alignment(
    decisions: list[MappingDecision],
    analysis: Any,
    policy: Any,
) -> list[MappingDecision]:
    """Apply explicit generic chain policies without inventing anatomy.

    Analyzer/policy chain identities are prerequisites.  Geometry alone never
    enters this function.  Default shortening keeps extra target rows at bind;
    a policy must explicitly opt into distribution.
    """

    source_values = _value(analysis, "semantic_chains", {}) or {}
    target_values = _value(policy, "semantic_chains", {}) or {}

    def indexed(values: Any) -> dict[str, Any]:
        if isinstance(values, Mapping):
            return {str(key): item for key, item in values.items()}
        result: dict[str, Any] = {}
        for item in _tuple(values):
            name = str(_first_value(item, ("chain_id", "name", "id"), ""))
            if name:
                result[name] = item
        return result

    source_chains = indexed(source_values)
    target_chains = indexed(target_values)
    if not source_chains or not target_chains:
        return decisions
    by_target = {row.target_bone: row for row in decisions}
    for chain_name in sorted(set(source_chains).intersection(target_chains)):
        source_chain = source_chains[chain_name]
        target_chain = target_chains[chain_name]
        sources = _chain_bone_names(source_chain)
        targets = _chain_bone_names(target_chain)
        if not sources or not targets or any(name not in by_target for name in targets):
            continue
        force = bool(_value(target_chain, "force_chain_alignment", False))
        explicitly_adaptive = force or any(
            _value(target_chain, name, None) is not None
            for name in ("short_source_policy", "distribution_policy", "long_source_policy")
        )
        if not explicitly_adaptive:
            continue
        distribute = str(
            _first_value(
                target_chain,
                ("short_source_policy", "distribution_policy"),
                "inherit_bind",
            )
        ) in {"distributed", "distribute"}
        if len(sources) > len(targets):
            # Partition ordered source segments over the ordered target chain.
            for index, target_name in enumerate(targets):
                current = by_target[target_name]
                if current.mode == "direct" and not force:
                    continue
                start = round(index * len(sources) / len(targets))
                stop = round((index + 1) * len(sources) / len(targets))
                group = sources[start : max(start + 1, stop)]
                by_target[target_name] = MappingDecision(
                    **{
                        **current.to_dict(),
                        "mode": "composed" if len(group) > 1 else "direct",
                        "source_bones": tuple(group),
                        "confidence": 0.9,
                        "confidence_margin": 0.2,
                        "evidence": (
                            MappingEvidence(
                                "semantic_chain_composition",
                                1.0,
                                chain_name,
                            ),
                        ),
                        "reason": "source chain segments composed between semantic anchors",
                    }
                )
        elif len(sources) < len(targets):
            source_indices = (
                [
                    min(
                        len(sources) - 1,
                        int(index * len(sources) / len(targets)),
                    )
                    for index in range(len(targets))
                ]
                if distribute
                else [min(index, len(sources) - 1) for index in range(len(targets))]
            )
            fanout = {
                source_index: source_indices.count(source_index)
                for source_index in set(source_indices)
            }
            for index, target_name in enumerate(targets):
                current = by_target[target_name]
                if current.mode == "direct" and not force:
                    continue
                source_index = source_indices[index]
                distribution_count = fanout[source_index]
                participates_in_distribution = distribute and distribution_count > 1
                first_for_source = source_index not in source_indices[:index]
                mode = (
                    "direct"
                    if participates_in_distribution and first_for_source
                    else "distributed"
                    if participates_in_distribution
                    else "direct"
                    if index < len(sources)
                    else "inherit_bind"
                )
                weight = (
                    1.0 / distribution_count
                    if participates_in_distribution
                    else 1.0
                )
                by_target[target_name] = MappingDecision(
                    **{
                        **current.to_dict(),
                        "mode": mode,
                        "source_bones": (sources[source_index],) if mode != "inherit_bind" else (),
                        "confidence": 0.88 if mode != "inherit_bind" else 0.0,
                        "confidence_margin": 0.18 if mode != "inherit_bind" else 0.0,
                        "evidence": (
                            MappingEvidence(
                                (
                                    "semantic_chain_distribution"
                                    if participates_in_distribution
                                    else "semantic_chain_alignment"
                                ),
                                weight,
                                chain_name,
                            ),
                        ),
                        "reason": (
                            "source segment distributed across target subdivisions"
                            if participates_in_distribution
                            else "optional target subdivision inherits bind-local parent motion"
                            if mode == "inherit_bind"
                            else "ordered semantic chain alignment"
                        ),
                    }
                )
    return [by_target[row.target_bone] for row in decisions]


def build_automatic_retarget_plan(
    source: Any,
    target_rig: Any,
    target_policy: Any,
    clip_domain: str = "body",
    *,
    role_overrides: Mapping[str, Any] | Iterable[RoleMappingOverride] | None = None,
) -> AutomaticRetargetPlan:
    """Build a complete target-row plan from generic analyzer evidence."""

    analysis = _coerce_analysis(source)
    policy = _coerce_policy(target_rig, target_policy, clip_domain)
    policy_id, policy_version, target_archetype, minimum, minimum_margin = (
        _policy_identity(policy)
    )
    rig_id = str(_value(target_rig, "rig_id", ""))
    target_hash = str(_value(target_rig, "skeleton_hash", ""))
    source_hash, name_parent_hash, bind_hash, animation_hash = _analysis_hashes(
        analysis
    )
    nodes = _analysis_node_map(analysis)
    source_archetype = str(_value(analysis, "archetype", "unknown") or "unknown")
    archetype_confidence = float(
        _value(analysis, "archetype_confidence", 0.0) or 0.0
    )
    exact, exact_names = _exact_identity(analysis, target_rig)
    overrides = _coerce_role_overrides(role_overrides)
    animated_bones = {
        str(value) for value in _tuple(_value(analysis, "animated_bones", ()))
    }
    unresolved_chains = tuple(
        str(value)
        for value in _tuple(_value(analysis, "unresolved_animated_chains", ()))
    )
    observed_domain = str(
        _first_value(
            analysis,
            ("observed_motion_domain", "animation_domain", "clip_domain"),
            clip_domain,
        )
    )

    decisions: list[MappingDecision] = []
    used_sources: set[str] = set()
    optional_missing: set[str] = set()
    for bone in _tuple(_value(target_rig, "bones", ())):
        info = _target_info(policy, bone, clip_domain)
        category = _target_category(bone, info)
        role = _target_role(policy, bone, info, rig_id)
        parent = _target_parent_name(target_rig, bone)
        critical = bool(_value(info, "critical", _role_is_critical(role)))
        override = _role_override(overrides, role) if role else None
        if exact and override is None:
            source_name = exact_names[str(_value(bone, "name", ""))]
            decisions.append(
                _decision(
                    bone,
                    target_rig,
                    category=category,
                    mode="direct",
                    role=role,
                    sources=(source_name,),
                    confidence=1.0,
                    margin=1.0,
                    evidence=(
                        MappingEvidence(
                            "exact_identity", 1.0, "name and target ancestry match"
                        ),
                    ),
                    reason="exact target skeleton identity",
                    critical=critical,
                    animated=source_name in animated_bones,
                )
            )
            used_sources.add(source_name)
            continue

        candidate = _candidate_for_role(analysis, role, nodes) if role else None
        animated = _role_animated(analysis, role, candidate) if role else False
        safe_domain = bool(
            _value(
                info,
                "safe_automatic_mapping",
                category == clip_domain and category not in _NON_BODY_CATEGORIES,
            )
        )
        helper_like = bool(_value(bone, "helper", False)) or any(
            token in str(_value(bone, "name", "")).casefold()
            for token in ("twist", "helper", "socket", "iktarget")
        )
        if override is not None and role and safe_domain and not helper_like:
            if override.mode in {"inherit_bind", "static_bind"}:
                decisions.append(
                    _decision(
                        bone,
                        target_rig,
                        category=category,
                        mode=override.mode,
                        role=role,
                        evidence=(
                            MappingEvidence(
                                "manual_override",
                                1.0,
                                f"user selected {override.mode}",
                                "semantic_profile",
                            ),
                        ),
                        reason=f"explicit semantic {override.mode} override",
                        critical=critical,
                        animated=False,
                    )
                )
                continue

            source_name = override.source_bone
            source_node = nodes.get(source_name)
            source_side = str(_value(source_node, "side", "") or "")
            role_side = _role_side(role)
            unsafe_reasons: list[str] = []
            if source_node is None:
                unsafe_reasons.append("selected source bone does not exist")
            elif bool(_value(source_node, "endpoint", False)):
                unsafe_reasons.append("selected source is an endpoint")
            elif max(
                float(_value(source_node, "helper_likelihood", 0.0) or 0.0),
                float(_value(source_node, "control_likelihood", 0.0) or 0.0),
                float(_value(source_node, "twist_likelihood", 0.0) or 0.0),
            ) >= 0.75:
                unsafe_reasons.append("selected source is a helper/control/twist")
            if (
                source_node is not None
                and role_side
                and source_side
                and _canonical_side(source_side) != role_side
            ):
                unsafe_reasons.append("selected source conflicts with target side")
            if source_name in used_sources:
                unsafe_reasons.append("selected source bone is already consumed")
            if unsafe_reasons:
                decisions.append(
                    _decision(
                        bone,
                        target_rig,
                        category=category,
                        mode="manual_required",
                        role=role,
                        sources=(source_name,) if source_name else (),
                        confidence=0.0,
                        evidence=(
                            MappingEvidence(
                                "manual_override_rejected",
                                0.0,
                                "; ".join(unsafe_reasons),
                                "semantic_profile",
                            ),
                        ),
                        reason="; ".join(unsafe_reasons),
                        critical=critical,
                        animated=True,
                    )
                )
                continue
            decisions.append(
                _decision(
                    bone,
                    target_rig,
                    category=category,
                    mode="direct",
                    role=role,
                    sources=(source_name,),
                    confidence=1.0,
                    margin=1.0,
                    evidence=(
                        MappingEvidence(
                            "manual_override",
                            1.0,
                            "validated source-bone assignment",
                            "semantic_profile",
                        ),
                    ),
                    reason="validated manual semantic override",
                    critical=critical,
                    animated=source_name in animated_bones,
                )
            )
            used_sources.add(source_name)
            continue
        if not safe_domain or helper_like or not role:
            mode = _bind_mode(category, parent)
            decisions.append(
                _decision(
                    bone,
                    target_rig,
                    category=category,
                    mode=mode,
                    role=role,
                    reason=(
                        f"{category} is bind-default in {clip_domain} clips"
                        if category != "body"
                        else "optional helper/twist/base row inherits target bind"
                    ),
                    critical=False,
                    animated=False,
                )
            )
            continue

        archetype_compatible = (
            target_archetype != "humanoid"
            or source_archetype == "humanoid"
            and archetype_confidence >= float(
                _value(policy, "minimum_archetype_confidence", 0.60)
            )
        )
        invalid_candidate = (
            candidate is None
            or candidate.bone_name not in nodes
            or candidate.endpoint
            or candidate.spatial_only
            or candidate.ambiguous
            or (
                candidate.bone_name in nodes
                and max(
                    float(
                        _value(nodes[candidate.bone_name], "helper_likelihood", 0.0)
                        or 0.0
                    ),
                    float(
                        _value(nodes[candidate.bone_name], "control_likelihood", 0.0)
                        or 0.0
                    ),
                    float(
                        _value(nodes[candidate.bone_name], "twist_likelihood", 0.0)
                        or 0.0
                    ),
                )
                >= 0.75
            )
            or candidate.confidence < minimum
            or candidate.margin < minimum_margin
        )
        side = _role_side(role)
        side_conflict = bool(
            candidate is not None
            and side
            and candidate.side
            and _canonical_side(candidate.side) != side
        )
        duplicate = bool(
            candidate is not None and candidate.bone_name in used_sources
        )
        chain_unresolved = bool(
            any(alias in unresolved_chains for alias in _role_aliases(role))
            or _canonical_chain(_role_chain(role))
            in {_canonical_chain(value) for value in unresolved_chains}
        )
        if (
            archetype_compatible
            and candidate is not None
            and not invalid_candidate
            and not side_conflict
            and not duplicate
        ):
            decisions.append(
                _decision(
                    bone,
                    target_rig,
                    category=category,
                    mode="direct",
                    role=role,
                    sources=(candidate.bone_name,),
                    confidence=candidate.confidence,
                    margin=candidate.margin,
                    evidence=candidate.evidence,
                    reason="deterministic semantic role and chain evidence",
                    critical=critical,
                    animated=animated,
                )
            )
            used_sources.add(candidate.bone_name)
            continue

        # Reaching this branch means no safe deterministic assignment exists.
        # Only a genuinely animated core role blocks; optional absent anatomy
        # remains a quiet bind/inherit decision.
        manual = bool(animated and critical)
        if manual:
            reasons = []
            if not archetype_compatible:
                reasons.append("source archetype is not safely humanoid")
            if candidate is not None and candidate.endpoint:
                reasons.append("only candidate is an endpoint/helper")
            if candidate is not None and candidate.spatial_only:
                reasons.append("candidate has spatial-only evidence")
            if candidate is not None and (
                candidate.ambiguous or candidate.margin < minimum_margin
            ):
                reasons.append("candidate confidence margin is ambiguous")
            if side_conflict:
                reasons.append("left/right evidence conflicts")
            if duplicate:
                reasons.append("source bone is already consumed")
            if chain_unresolved:
                reasons.append("animated semantic chain is unresolved")
            decisions.append(
                _decision(
                    bone,
                    target_rig,
                    category=category,
                    mode="manual_required",
                    role=role,
                    sources=(candidate.bone_name,) if candidate else (),
                    confidence=candidate.confidence if candidate else 0.0,
                    margin=candidate.margin if candidate else 0.0,
                    evidence=candidate.evidence if candidate else (),
                    reason="; ".join(reasons) or "animated critical chain is ambiguous",
                    critical=critical,
                    animated=True,
                )
            )
        else:
            optional_missing.add(role)
            decisions.append(
                _decision(
                    bone,
                    target_rig,
                    category=category,
                    mode=_bind_mode(category, parent),
                    role=role,
                    reason=(
                        "optional source role is absent; retain bind-local transform "
                        "under the animated target parent"
                    ),
                    critical=critical,
                    animated=False,
                )
            )

    decisions = _apply_declared_chain_alignment(decisions, analysis, policy)
    consumed_source_bones = {
        source_name
        for row in decisions
        if row.mode in {"direct", "composed", "distributed"}
        for source_name in row.source_bones
    }
    remaining_unresolved_chains = tuple(
        sorted(
            name
            for name in unresolved_chains
            if name not in consumed_source_bones
        )
    )
    findings = tuple(
        _plain(item)
        for item in _tuple(_value(analysis, "findings", ()))
        if item is not None
    )
    analyzer_version = str(
        _value(analysis, "analyzer_version", DEFAULT_ANALYZER_VERSION)
        or DEFAULT_ANALYZER_VERSION
    )
    serialized_analysis = {}
    serializer = _value(analysis, "to_dict", None)
    if callable(serializer):
        try:
            serialized_analysis = dict(serializer())
        except (TypeError, ValueError):
            serialized_analysis = {}
    lexicon_version = str(
        _first_value(
            analysis,
            ("lexicon_version", "semantic_lexicon_version"),
            serialized_analysis.get(
                "semantic_lexicon_version", DEFAULT_LEXICON_VERSION
            ),
        )
        or DEFAULT_LEXICON_VERSION
    )
    animated_chains = tuple(
        sorted(
            str(value)
            for value in _tuple(
                _first_value(
                    analysis,
                    ("animated_chains_detected", "animated_chains"),
                    (),
                )
            )
        )
    )
    return AutomaticRetargetPlan(
        source_skeleton_hash=source_hash,
        source_name_parent_hash=name_parent_hash,
        source_bind_hash=bind_hash,
        source_animation_hash=animation_hash,
        target_rig_id=rig_id,
        target_skeleton_hash=target_hash,
        target_policy_id=policy_id,
        clip_domain=str(clip_domain),
        source_archetype=source_archetype,
        source_archetype_confidence=archetype_confidence,
        decisions=tuple(decisions),
        analyzer_version=analyzer_version,
        semantic_policy_version=policy_version,
        lexicon_version=lexicon_version,
        source_family_hints=tuple(
            str(value)
            for value in _tuple(_value(analysis, "source_family_hints", ()))
        ),
        source_name_languages_or_scripts=tuple(
            str(value)
            for value in _tuple(
                _first_value(
                    analysis,
                    ("source_name_languages_or_scripts", "name_scripts"),
                    (),
                )
            )
        ),
        animated_chains_detected=animated_chains,
        unresolved_animated_chains=remaining_unresolved_chains,
        optional_missing_source_roles=tuple(
            sorted(value for value in optional_missing if value)
        ),
        findings=findings,
        warnings_shown_to_user=(),
        diagnostic_findings_suppressed_from_basic_ui=sum(
            row.mode in {"inherit_bind", "static_bind"} for row in decisions
        ),
        exact_identity=exact,
        observed_motion_domain=observed_domain,
        manual_override_count=len(tuple(dict.fromkeys(overrides.values()))),
        role_overrides=tuple(
            row.to_dict() for row in dict.fromkeys(overrides.values())
        ),
    )


def _certificate_for_plan(
    plan: AutomaticRetargetPlan,
    target_rig: Any,
    *,
    status: str,
) -> dict[str, Any]:
    modes = plan.mapping_modes
    direct_modes = {"direct", "composed", "distributed"}
    mapped_rows = [row for row in plan.decisions if row.mode in direct_modes]
    bind_rows = [
        row
        for row in plan.decisions
        if row.mode in {"inherit_bind", "static_bind"}
    ]
    mapped_non_body = [
        row.target_bone
        for row in mapped_rows
        if row.target_category != plan.clip_domain
    ]
    endpoint_rows = [
        row.target_bone
        for row in mapped_rows
        if any(
            evidence.kind.casefold() in {"endpoint", "end_bone"}
            for evidence in row.evidence
        )
    ]
    spatial_rows = [
        row.target_bone
        for row in mapped_rows
        if row.evidence
        and {item.kind.casefold() for item in row.evidence}
        <= _SPATIAL_ONLY_EVIDENCE
    ]
    category_inventory: dict[str, int] = {}
    for row in plan.decisions:
        category_inventory[row.target_category] = (
            category_inventory.get(row.target_category, 0) + 1
        )
    return {
        "format": plan.target_policy_id,
        "policy": plan.target_policy_id,
        "analyzer_version": plan.analyzer_version,
        "planner_version": plan.planner_version,
        "semantic_policy_version": plan.semantic_policy_version,
        "lexicon_version": plan.lexicon_version,
        "target_policy_id": plan.target_policy_id,
        "target_rig_id": plan.target_rig_id,
        "target_skeleton_hash": plan.target_skeleton_hash,
        "source_skeleton_hash": plan.source_skeleton_hash,
        "source_name_parent_hash": plan.source_name_parent_hash,
        "source_bind_hash": plan.source_bind_hash,
        "source_animation_hash": plan.source_animation_hash,
        "source_archetype": plan.source_archetype,
        "source_archetype_confidence": plan.source_archetype_confidence,
        "source_family_hints": list(plan.source_family_hints),
        "source_name_languages_or_scripts": list(
            plan.source_name_languages_or_scripts
        ),
        "clip_domain": plan.clip_domain,
        "target_row_count": len(plan.decisions),
        "mapped_body_row_count": len(mapped_rows),
        "bind_row_count": len(bind_rows),
        "bind_default_row_count": len(bind_rows),
        "mapping_modes": modes,
        "mapping_mode_counts": modes,
        "target_category_inventory": category_inventory,
        "spatial_only_row_count": len(spatial_rows),
        "spatial_only_mapping_count": len(spatial_rows),
        "spatial_only_target_bones": spatial_rows,
        "mapped_non_body_target_count": len(mapped_non_body),
        "mapped_non_body_targets": mapped_non_body,
        "source_endpoint_rows_consumed": endpoint_rows,
        "unresolved_required_roles": list(plan.unresolved_required_roles),
        "animated_chains_detected": list(plan.animated_chains_detected),
        "unresolved_animated_chains": list(plan.unresolved_animated_chains),
        "optional_missing_source_roles": list(
            plan.optional_missing_source_roles
        ),
        "warnings_shown_to_user": list(plan.warnings_shown_to_user),
        "diagnostic_findings_suppressed_from_basic_ui": (
            plan.diagnostic_findings_suppressed_from_basic_ui
        ),
        "preserves_target_non_root_translation": True,
        "preserves_target_non_root_scale": True,
        "plan_hash": plan.plan_hash,
        "decision_fingerprint": _stable_hash(
            [row.to_dict() for row in plan.decisions]
        ),
        "certificate_status": status,
        "status": status,
    }


def validate_automatic_retarget_plan(
    plan: AutomaticRetargetPlan,
    source: Any,
    target_rig: Any,
    target_policy: Any,
) -> AutomaticRetargetValidation:
    """Recompute live identities and validate every planner invariant."""

    analysis = _coerce_analysis(source)
    policy = _coerce_policy(target_rig, target_policy, plan.clip_domain)
    policy_id, policy_version, _archetype, _minimum, _margin = _policy_identity(
        policy
    )
    errors: list[str] = []
    live_hashes = _analysis_hashes(analysis)
    recorded_hashes = (
        plan.source_skeleton_hash,
        plan.source_name_parent_hash,
        plan.source_bind_hash,
        plan.source_animation_hash,
    )
    labels = (
        "source skeleton hash",
        "source name/parent hash",
        "source bind hash",
        "source animation hash",
    )
    for label, recorded, live in zip(labels, recorded_hashes, live_hashes):
        if recorded != live:
            errors.append(f"{label} changed: plan={recorded}, live={live}")
    if plan.target_rig_id != str(_value(target_rig, "rig_id", "")):
        errors.append("target rig ID changed")
    if plan.target_skeleton_hash != str(_value(target_rig, "skeleton_hash", "")):
        errors.append("target skeleton hash changed")
    if plan.target_policy_id != policy_id:
        errors.append("target retarget policy changed")
    if plan.semantic_policy_version != policy_version:
        errors.append("semantic policy version changed")
    live_analyzer_version = str(
        _value(analysis, "analyzer_version", DEFAULT_ANALYZER_VERSION)
        or DEFAULT_ANALYZER_VERSION
    )
    serialized_analysis = {}
    serializer = _value(analysis, "to_dict", None)
    if callable(serializer):
        try:
            serialized_analysis = dict(serializer())
        except (TypeError, ValueError):
            serialized_analysis = {}
    live_lexicon_version = str(
        _first_value(
            analysis,
            ("lexicon_version", "semantic_lexicon_version"),
            serialized_analysis.get(
                "semantic_lexicon_version", DEFAULT_LEXICON_VERSION
            ),
        )
        or DEFAULT_LEXICON_VERSION
    )
    if plan.analyzer_version != live_analyzer_version:
        errors.append("source analyzer version changed")
    if plan.lexicon_version != live_lexicon_version:
        errors.append("multilingual lexicon version changed")
    if plan.planner_version != PLANNER_VERSION:
        errors.append("automatic retarget planner version is stale")

    live_unresolved_chains = tuple(
        sorted(
            str(value)
            for value in _tuple(
                _value(analysis, "unresolved_animated_chains", ())
            )
        )
    )
    mapped_source_bones = {
        source_name
        for row in plan.decisions
        if row.mode in {"direct", "composed", "distributed"}
        for source_name in row.source_bones
    }
    expected_unresolved_chains = tuple(
        name
        for name in live_unresolved_chains
        if name not in mapped_source_bones
    )
    if plan.unresolved_animated_chains != expected_unresolved_chains:
        errors.append("unresolved animated source-chain inventory changed")
    if expected_unresolved_chains:
        errors.append(
            "unresolved animated source chains require attention: "
            + ", ".join(expected_unresolved_chains)
        )

    bones = _tuple(_value(target_rig, "bones", ()))
    if len(plan.decisions) != len(bones):
        errors.append(
            f"target row inventory is incomplete: {len(plan.decisions)}/{len(bones)}"
        )
    expected = {
        str(_value(bone, "name", "")): int(_value(bone, "descriptor", 0))
        for bone in bones
    }
    seen: set[str] = set()
    used_sources: set[str] = set()
    distribution_totals: dict[tuple[str, str], float] = {}
    source_nodes = _analysis_node_map(analysis)
    for row in plan.decisions:
        if row.mode not in MAPPING_MODES:
            errors.append(f"{row.target_bone}: unsupported mapping mode {row.mode!r}")
        if row.target_bone in seen:
            errors.append(f"duplicate target decision for {row.target_bone!r}")
        seen.add(row.target_bone)
        if row.target_bone not in expected:
            errors.append(f"decision targets unknown bone {row.target_bone!r}")
        elif expected[row.target_bone] != row.target_descriptor:
            errors.append(f"target descriptor changed for {row.target_bone!r}")
        if row.mode in {"direct", "composed", "distributed"}:
            if not row.source_bones:
                errors.append(f"{row.target_bone}: mapped decision has no source")
            if (
                not plan.exact_identity
                and plan.clip_domain == "body"
                and row.target_category in _NON_BODY_CATEGORIES
            ):
                errors.append(
                    f"{row.target_bone}: non-body target mapped in body domain"
                )
            kinds = {item.kind.casefold() for item in row.evidence}
            if kinds and kinds <= _SPATIAL_ONLY_EVIDENCE:
                errors.append(f"{row.target_bone}: spatial-only mapping is forbidden")
            for source_name in row.source_bones:
                if source_name not in source_nodes:
                    errors.append(
                        f"{row.target_bone}: source bone {source_name!r} is missing"
                    )
                elif _node_endpoint(source_nodes[source_name]):
                    errors.append(
                        f"{row.target_bone}: source endpoint {source_name!r} was consumed"
                    )
                if row.mode == "direct" and source_name in used_sources:
                    errors.append(
                        f"{row.target_bone}: duplicate direct source consumption {source_name!r}"
                    )
                if row.mode == "direct":
                    used_sources.add(source_name)
            if row.mode == "composed" and len(row.source_bones) < 2:
                errors.append(
                    f"{row.target_bone}: composed mapping requires two or more ordered sources"
                )
            weight, chain_id = _decision_distribution_metadata(row)
            if row.mode == "distributed":
                if len(row.source_bones) != 1:
                    errors.append(
                        f"{row.target_bone}: distributed mapping requires exactly one source"
                    )
                if not chain_id or not 0.0 < weight <= 1.0:
                    errors.append(
                        f"{row.target_bone}: distributed mapping has invalid chain/weight metadata"
                    )
            if chain_id and row.source_bones:
                key = (chain_id, row.source_bones[0])
                distribution_totals[key] = distribution_totals.get(key, 0.0) + weight
        elif row.source_bones and row.mode not in {"manual_required", "ignored_source"}:
            errors.append(
                f"{row.target_bone}: bind decision unexpectedly names source bones"
            )
        if row.mode == "manual_required":
            errors.append(
                f"animated critical chain requires attention at {row.target_bone!r}: {row.reason}"
            )
    missing_targets = sorted(set(expected) - seen, key=str.casefold)
    if missing_targets:
        errors.append(
            "target decisions are missing: " + ", ".join(missing_targets[:12])
        )
    for (chain_id, source_name), total in distribution_totals.items():
        if not math.isclose(total, 1.0, rel_tol=0.0, abs_tol=1.0e-9):
            errors.append(
                f"distributed chain {chain_id!r} source {source_name!r} weights total {total:.12g}, not 1"
            )
    for bone in bones:
        values = (
            *_tuple(_value(bone, "bind_translation", ())),
            *_tuple(_value(bone, "bind_rotation_wxyz", ())),
            *_tuple(_value(bone, "bind_scale", ())),
        )
        if values and not all(math.isfinite(float(value)) for value in values):
            errors.append(f"target bind is non-finite at {_value(bone, 'name', '')!r}")

    status = "pass" if not errors else "fail"
    certificate = _certificate_for_plan(plan, target_rig, status=status)
    return AutomaticRetargetValidation(
        status,
        tuple(dict.fromkeys(errors)),
        (),
        certificate,
        plan.plan_hash,
    )


def _require_coherent_dl2_advanced_target(target_rig: Any, policy: Any) -> None:
    """Require one of the two coherent bundled DL2 body targets.

    The historical function name remains public-by-use for compatibility, but
    authorization is now target-policy driven for both bundled player rigs.
    """

    errors: list[str] = []
    rig_id = str(_value(target_rig, "rig_id", ""))
    extensions = dict(_value(target_rig, "extensions", {}) or {})
    bones = _tuple(_value(target_rig, "bones", ()))
    expected = {
        DL2_ADVANCED_RIG_ID: (DL2_ADVANCED_BODY_CERTIFICATE_FORMAT, 271),
        DL2_LEGACY_RIG_ID: (DL2_LEGACY_BODY_CERTIFICATE_FORMAT, 81),
    }.get(rig_id)
    if expected is None:
        errors.append("target rig is not a bundled DL2 player target")
        expected_policy_id, expected_rows = "", 0
    else:
        expected_policy_id, expected_rows = expected
    if str(extensions.get("game_id", "")) != "dying_light_2":
        errors.append("target CRIG game_id is not dying_light_2")
    if expected_rows and len(bones) != expected_rows:
        errors.append(
            f"bundled target must contain {expected_rows} rows, found {len(bones)}"
        )
    if not extensions.get("source_smd_sha256"):
        errors.append("target CRIG has no embedded source-SMD hash")
    if not extensions.get("source_reference_anm2_sha256"):
        errors.append("target CRIG has no embedded reference-ANM2 hash")
    policy_id = str(
        _first_value(policy, ("policy_id", "target_policy_id", "id"), "")
    )
    if policy_id != expected_policy_id:
        errors.append("target policy is not the matching built-in DL2 body policy")
    authorized = bool(
        _first_value(
            policy,
            ("automatic_routing_authorized", "verified_automatic_routing"),
            False,
        )
    )
    if not authorized:
        errors.append("target policy has not authorized automatic routing")
    policy_target_id = str(_value(policy, "target_rig_id", ""))
    if policy_target_id != rig_id:
        errors.append("target policy rig ID does not match the selected target")
    policy_target_hash = str(_value(policy, "target_skeleton_hash", ""))
    target_hash = str(_value(target_rig, "skeleton_hash", ""))
    if policy_target_hash != target_hash:
        errors.append("target policy skeleton hash does not match the selected target")
    if str(_value(policy, "game_id", "")) != "dying_light_2":
        errors.append("target policy game ID is not dying_light_2")
    if str(_value(policy, "clip_domain", "")).casefold() != "body":
        errors.append("target policy is not the body-domain policy")
    if expected_rows and int(_value(policy, "target_row_count", 0) or 0) != expected_rows:
        errors.append(
            f"target policy does not inventory all {expected_rows} target rows"
        )
    if int(_value(policy, "direct_slot_count", 0) or 0) != 52:
        errors.append("target policy does not declare exactly 52 direct body slots")
    coherence_errors = tuple(_value(policy, "coherence_errors", ()) or ())
    if coherence_errors:
        errors.append("selected built-in target package provenance failed")
    coherence = str(
        _first_value(policy, ("package_coherence_status", "coherence_status"), "")
        or ""
    )
    if coherence and coherence != "pass":
        errors.append("selected built-in target package provenance failed")
    if errors:
        raise ValueError("Bundled DL2 target is not coherent:\n- " + "\n- ".join(errors))


def _materialize_verified_map(
    plan: AutomaticRetargetPlan,
    validation: AutomaticRetargetValidation,
    target_rig: Any,
    analysis: Any,
) -> GenericBoneMap:
    validation.require_valid()
    profile = GenericBoneMap.create(
        "Verified bundled DL2 body map",
        str(_value(target_rig, "skeleton_hash", "")),
        _profile_source_skeleton_hash(analysis),
        source_rig_ref=str(_value(target_rig, "rig_id", "")),
        origin="automatic_verified",
    )
    profile.target_bind_hash = str(_value(target_rig, "skeleton_hash", ""))
    profile.pairs = []
    for row in plan.decisions:
        mapped = row.mode in {"direct", "composed", "distributed"}
        weight, chain_id = _decision_distribution_metadata(row)
        execution_mode = (
            "distributed"
            if chain_id
            else "static_bind"
            if row.mode == "ignored_source"
            else row.mode
        )
        profile.pairs.append(
            BoneMapPair(
                target_rig_descriptor=row.target_descriptor,
                target_rig_bone=row.target_bone,
                source_fbx_bone=row.source_bones[0] if mapped else "",
                confidence=row.confidence if mapped else 1.0,
                method=f"automatic_verified:{row.mode}",
                transfer_policy=(
                    "rotation_delta"
                    if execution_mode in {"composed", "distributed"}
                    else "global_bind_basis"
                    if mapped
                    else "bind"
                ),
                component_policy="rotation",
                mapping_kind="bone",
                review_state=(
                    "automatic_accepted" if mapped else "intentionally_unmapped"
                ),
                notes=row.reason,
                extensions={
                    "automatic_retarget_decision": row.to_dict(),
                    "mapping_mode": row.mode,
                    "execution_mapping_mode": execution_mode,
                    "source_bones": list(row.source_bones),
                    "distribution_weight": weight,
                    "semantic_chain_id": chain_id,
                },
            )
        )
    profile.extensions["automatic_retarget_plan"] = plan.to_dict()
    profile.extensions["automatic_retarget_certificate"] = dict(
        validation.certificate
    )
    profile.extensions["verified_mapping_certificate"] = dict(
        validation.certificate
    )
    errors = profile.validate()
    if errors:
        raise ValueError("Verified map materialization failed:\n- " + "\n- ".join(errors))
    return profile


def materialize_automatic_retarget_plan(
    plan: AutomaticRetargetPlan,
    source: Any,
    target_rig: Any,
    target_policy: Any,
    *,
    profile_name: str = "Automatic retarget plan",
) -> GenericBoneMap:
    """Materialize a generic plan without granting verified build authority.

    The resulting ``automatic_repair`` profile preserves executable composed
    source lists and distributed weights.  Its mapped rows remain
    ``automatic_unreviewed`` so existing routing still requires explicit user
    review; only the separately certified built-in DL2 bridge self-authorizes.
    """

    analysis = _coerce_analysis(source)
    policy = _coerce_policy(target_rig, target_policy, plan.clip_domain)
    validation = validate_automatic_retarget_plan(
        plan, analysis, target_rig, policy
    )
    validation.require_valid()
    profile = GenericBoneMap.create(
        str(profile_name),
        str(_value(target_rig, "skeleton_hash", "")),
        _profile_source_skeleton_hash(analysis),
        source_rig_ref=str(_value(target_rig, "rig_id", "")),
        origin="automatic_repair",
    )
    profile.target_bind_hash = str(_value(target_rig, "skeleton_hash", ""))
    pairs: list[BoneMapPair] = []
    for row in plan.decisions:
        mapped = row.mode in {"direct", "composed", "distributed"}
        weight, chain_id = _decision_distribution_metadata(row)
        execution_mode = (
            "distributed"
            if chain_id
            else "static_bind"
            if row.mode == "ignored_source"
            else row.mode
        )
        pairs.append(
            BoneMapPair(
                target_rig_descriptor=row.target_descriptor,
                target_rig_bone=row.target_bone,
                source_fbx_bone=row.source_bones[0] if mapped else "",
                confidence=row.confidence if mapped else 1.0,
                method=f"automatic_plan:{row.mode}",
                transfer_policy="rotation_delta" if mapped else "bind",
                component_policy="rotation",
                mapping_kind="bone",
                review_state=(
                    "automatic_unreviewed" if mapped else "intentionally_unmapped"
                ),
                notes=row.reason,
                extensions={
                    "automatic_retarget_decision": row.to_dict(),
                    "mapping_mode": row.mode,
                    "execution_mapping_mode": execution_mode,
                    "source_bones": list(row.source_bones),
                    "distribution_weight": weight,
                    "semantic_chain_id": chain_id,
                },
            )
        )
    profile.pairs = pairs
    profile.extensions["automatic_retarget_plan"] = plan.to_dict()
    profile.extensions["automatic_retarget_plan_validation"] = validation.to_dict()
    errors = profile.validate()
    if errors:
        raise ValueError(
            "Automatic plan materialization failed:\n- " + "\n- ".join(errors)
        )
    return profile


def build_verified_dl2_advanced_body_map(
    document_or_analysis: Any,
    target_rig: Any,
    target_policy: Any = None,
    *,
    role_overrides: Mapping[str, Any] | Iterable[RoleMappingOverride] | None = None,
) -> GenericBoneMap:
    """Build the certified bridge; accept both source-first and rig-first order."""

    first_is_rig = bool(
        _value(document_or_analysis, "rig_id", "")
        and _value(document_or_analysis, "bones", None) is not None
    )
    second_is_rig = bool(
        _value(target_rig, "rig_id", "")
        and _value(target_rig, "bones", None) is not None
    )
    if first_is_rig and not second_is_rig:
        document_or_analysis, target_rig = target_rig, document_or_analysis

    analysis = _coerce_analysis(document_or_analysis)
    policy = _coerce_policy(target_rig, target_policy, "body")
    _require_coherent_dl2_advanced_target(target_rig, policy)
    plan = build_automatic_retarget_plan(
        analysis,
        target_rig,
        policy,
        clip_domain="body",
        role_overrides=role_overrides,
    )
    verification = validate_automatic_retarget_plan(
        plan, analysis, target_rig, policy
    )
    verification.require_valid()
    profile = _materialize_verified_map(plan, verification, target_rig, analysis)
    # Exercise the same serialized-row/live-identity check used by build
    # routing before returning a newly generated profile.
    live = revalidate_verified_dl2_advanced_body_map(
        profile,
        analysis,
        target_rig,
        policy,
        role_overrides=role_overrides,
    )
    live.require_valid()
    profile.extensions["automatic_retarget_certificate"] = dict(
        live.certificate
    )
    profile.extensions["verified_mapping_certificate"] = dict(
        live.certificate
    )
    return profile


def build_dl2_advanced_body_map_with_local_recipe(
    document_or_analysis: Any,
    target_rig: Any,
    target_policy: Any = None,
    *,
    recipe_store: Any = None,
) -> GenericBoneMap:
    """Build the verified bridge or a live-reviewed local recipe override.

    The built-in deterministic plan alone receives ``automatic_verified``
    provenance. A matching reviewed local correction is materialized as a
    complete ``manually_reviewed`` map, so serialized recipe content can never
    impersonate the built-in certificate route.
    """

    first_is_rig = bool(
        _value(document_or_analysis, "rig_id", "")
        and _value(document_or_analysis, "bones", None) is not None
    )
    second_is_rig = bool(
        _value(target_rig, "rig_id", "")
        and _value(target_rig, "bones", None) is not None
    )
    if first_is_rig and not second_is_rig:
        document_or_analysis, target_rig = target_rig, document_or_analysis

    analysis = _coerce_analysis(document_or_analysis)
    policy = _coerce_policy(target_rig, target_policy, "body")
    _require_coherent_dl2_advanced_target(target_rig, policy)
    fresh = build_automatic_retarget_plan(
        analysis, target_rig, policy, clip_domain="body"
    )
    from .retarget_recipes import (
        materialize_reviewed_retarget_recipe,
        resolve_local_retarget_recipe,
    )

    resolution = resolve_local_retarget_recipe(
        fresh,
        analysis,
        target_rig,
        policy,
        store=recipe_store,
    )
    if not resolution.applied:
        return build_verified_dl2_advanced_body_map(
            analysis, target_rig, policy
        )
    assert resolution.recipe is not None
    return materialize_reviewed_retarget_recipe(
        resolution.recipe,
        analysis,
        target_rig,
        policy,
        clip_domain="body",
        profile_name="Reviewed local DL2 body recipe",
    )


def _profile_value(profile: Any, name: str, default: Any = None) -> Any:
    return _value(profile, name, default)


def _profile_extensions(profile: Any) -> dict[str, Any]:
    return dict(_profile_value(profile, "extensions", {}) or {})


def _profile_pairs(profile: Any) -> tuple[Any, ...]:
    return _tuple(_profile_value(profile, "pairs", ()))


def _pair_value(pair: Any, name: str, legacy: str = "", default: Any = None) -> Any:
    found = _value(pair, name, None)
    if found is None and legacy:
        found = _value(pair, legacy, None)
    return default if found is None else found


def revalidate_verified_dl2_advanced_body_map(
    profile: GenericBoneMap | Mapping[str, Any],
    document_or_analysis: Any,
    target_rig: Any,
    target_policy: Any = None,
    *,
    role_overrides: Mapping[str, Any] | Iterable[RoleMappingOverride] | None = None,
) -> AutomaticRetargetValidation:
    """Rebuild the expected plan and compare every live row/certificate field."""

    analysis = _coerce_analysis(document_or_analysis)
    policy = _coerce_policy(target_rig, target_policy, "body")
    errors: list[str] = []
    try:
        _require_coherent_dl2_advanced_target(target_rig, policy)
    except ValueError as exc:
        errors.append(str(exc))
    plan = build_automatic_retarget_plan(
        analysis,
        target_rig,
        policy,
        clip_domain="body",
        role_overrides=role_overrides,
    )
    base = validate_automatic_retarget_plan(plan, analysis, target_rig, policy)
    errors.extend(base.errors)
    expected = {
        row.target_bone: row for row in plan.decisions
    }
    actual_rows = _profile_pairs(profile)
    if len(actual_rows) != len(expected):
        errors.append(
            f"serialized target row count changed: {len(actual_rows)}/{len(expected)}"
        )
    actual_targets: set[str] = set()
    for pair in actual_rows:
        target = str(
            _pair_value(pair, "target_rig_bone", "source_bone", "")
        )
        actual_targets.add(target)
        decision = expected.get(target)
        if decision is None:
            errors.append(f"serialized map contains unknown target {target!r}")
            continue
        descriptor = int(
            _pair_value(pair, "target_rig_descriptor", "source_descriptor", 0)
        )
        source_name = str(
            _pair_value(pair, "source_fbx_bone", "target_bone", "")
        )
        transfer = str(_pair_value(pair, "transfer_policy", default="default"))
        component = str(
            _pair_value(pair, "component_policy", default="full_transform")
        )
        review = str(_pair_value(pair, "review_state", default=""))
        method = str(_pair_value(pair, "method", default=""))
        mapped = decision.mode in {"direct", "composed", "distributed"}
        weight, chain_id = _decision_distribution_metadata(decision)
        execution_mode = (
            "distributed"
            if chain_id
            else "static_bind"
            if decision.mode == "ignored_source"
            else decision.mode
        )
        expected_source = decision.source_bones[0] if mapped else ""
        if descriptor != decision.target_descriptor:
            errors.append(f"target descriptor changed for {target!r}")
        if source_name != expected_source:
            errors.append(
                f"deterministic source pair changed for {target!r}: "
                f"expected {expected_source!r}, found {source_name!r}"
            )
        expected_transfer = (
            "rotation_delta"
            if execution_mode in {"composed", "distributed"}
            else "global_bind_basis"
            if mapped
            else "bind"
        )
        if transfer != expected_transfer:
            errors.append(f"transfer policy changed for {target!r}")
        if component != "rotation":
            errors.append(f"component policy changed for {target!r}")
        if review != (
            "automatic_accepted" if mapped else "intentionally_unmapped"
        ):
            errors.append(f"review state changed for {target!r}")
        expected_method = f"automatic_verified:{decision.mode}"
        if method != expected_method:
            errors.append(f"verified mapping mode changed for {target!r}")
        if method.casefold() == "spatial_bind":
            errors.append(f"spatial-only row appeared at {target!r}")
        row_extensions = dict(_pair_value(pair, "extensions", default={}) or {})
        if row_extensions.get("mapping_mode") != decision.mode:
            errors.append(f"serialized mapping-mode marker changed for {target!r}")
        if row_extensions.get("execution_mapping_mode") != execution_mode:
            errors.append(f"serialized execution-mode marker changed for {target!r}")
        if row_extensions.get("automatic_retarget_decision") != decision.to_dict():
            errors.append(f"serialized decision identity changed for {target!r}")
        if row_extensions.get("source_bones") != list(decision.source_bones):
            errors.append(f"executable source inventory changed for {target!r}")
        if row_extensions.get("distribution_weight") != weight:
            errors.append(f"executable distribution weight changed for {target!r}")
        if row_extensions.get("semantic_chain_id") != chain_id:
            errors.append(f"executable semantic chain identity changed for {target!r}")
    missing = sorted(set(expected) - actual_targets, key=str.casefold)
    if missing:
        errors.append("serialized rows are missing: " + ", ".join(missing[:12]))

    extensions = _profile_extensions(profile)
    if str(extensions.get("origin", "")) != "automatic_verified":
        errors.append("mapping origin is not automatic_verified")
    certificate = dict(
        extensions.get("automatic_retarget_certificate")
        or extensions.get("verified_mapping_certificate")
        or {}
    )
    expected_certificate = dict(base.certificate)
    required_certificate_fields = (
        "format",
        "policy",
        "analyzer_version",
        "planner_version",
        "semantic_policy_version",
        "lexicon_version",
        "target_policy_id",
        "target_rig_id",
        "target_skeleton_hash",
        "source_skeleton_hash",
        "source_name_parent_hash",
        "source_bind_hash",
        "source_animation_hash",
        "clip_domain",
        "target_row_count",
        "mapped_body_row_count",
        "bind_row_count",
        "mapping_mode_counts",
        "spatial_only_row_count",
        "mapped_non_body_target_count",
        "plan_hash",
        "decision_fingerprint",
        "certificate_status",
    )
    for name in required_certificate_fields:
        if name not in certificate:
            errors.append(f"mapping certificate is missing {name!r}")
        elif certificate[name] != expected_certificate[name]:
            errors.append(f"mapping certificate field {name!r} is stale or changed")
    target_hash = str(_value(target_rig, "skeleton_hash", ""))
    expected_bind_hash = str(
        _profile_value(profile, "target_bind_hash", "")
        or _profile_value(profile, "source_skeleton_hash", "")
    )
    if expected_bind_hash != target_hash:
        errors.append("mapping target full-bind hash changed")
    recorded_source_hierarchy = str(
        _profile_value(profile, "target_skeleton_hash", "")
    )
    if recorded_source_hierarchy != _profile_source_skeleton_hash(analysis):
        errors.append("mapping source name/parent signature changed")
    source_rig_ref = str(_profile_value(profile, "source_rig_ref", ""))
    if source_rig_ref != str(_value(target_rig, "rig_id", "")):
        errors.append("mapping target rig reference changed")

    status = "pass" if not errors else "fail"
    result_certificate = dict(expected_certificate)
    result_certificate["certificate_status"] = status
    result_certificate["status"] = status
    result_certificate["live_revalidated"] = status == "pass"
    return AutomaticRetargetValidation(
        status,
        tuple(dict.fromkeys(errors)),
        (),
        result_certificate,
        plan.plan_hash,
        status == "pass",
    )


def classify_retarget_readiness(
    value: AutomaticRetargetPlan | AutomaticRetargetValidation,
) -> RetargetReadiness:
    if isinstance(value, AutomaticRetargetValidation):
        if value.ok and value.live_revalidated:
            return RetargetReadiness(
                "ready",
                "info",
                "Ready — automatically retargeted",
                "The automatic retarget certificate passed live revalidation.",
            )
        if value.ok:
            return RetargetReadiness(
                "needs_verification",
                "info",
                "Checking automatic retarget certificate…",
                "The plan passed, but the serialized rows still need live revalidation.",
            )
        return RetargetReadiness(
            "needs_attention",
            "action_required",
            "Needs attention — automatic mapping changed",
            value.errors[0] if value.errors else "Automatic verification failed.",
            "Fix mapping…",
            value.errors,
        )
    unresolved = tuple(dict.fromkeys(value.unresolved_animated_chains))
    if unresolved:
        return RetargetReadiness(
            "needs_attention",
            "action_required",
            f"Needs attention — {unresolved[0]} is unresolved",
            (
                f"{len(unresolved)} animated source chain(s) have no "
                "deterministic target assignment."
            ),
            "Fix mapping…",
            tuple(
                f"{name}: animated source chain has no deterministic target assignment"
                for name in unresolved
            ),
        )
    manual = [row for row in value.decisions if row.mode == "manual_required"]
    if manual:
        chains = sorted(
            {row.semantic_role or row.target_bone for row in manual}
        )
        return RetargetReadiness(
            "needs_attention",
            "action_required",
            f"Needs attention — {chains[0]} is ambiguous",
            f"{len(chains)} animated critical chain(s) need a focused mapping decision.",
            "Fix mapping…",
            tuple(row.reason for row in manual),
        )
    if value.exact_identity:
        return RetargetReadiness(
            "ready",
            "info",
            "Ready — exact skeleton match",
            "Source names and target ancestry match exactly.",
        )
    inherited = value.mapping_modes.get("inherit_bind", 0)
    if value.observed_motion_domain == "upper_body":
        return RetargetReadiness(
            "ready",
            "info",
            "Ready — upper-body clip; lower body held at bind",
            "Unanimated lower-body rows retain bind-local parent inheritance.",
        )
    if inherited:
        return RetargetReadiness(
            "ready",
            "info",
            f"Ready — partial skeleton; {inherited} target bones inherit parent motion",
            "Missing optional/terminal bones are normal automatic accommodations.",
        )
    return RetargetReadiness(
        "ready",
        "info",
        "Ready — automatically retargeted",
        "All animated critical chains have deterministic assignments.",
    )


def format_retarget_readiness(
    value: AutomaticRetargetPlan | AutomaticRetargetValidation | RetargetReadiness,
) -> str:
    readiness = (
        value if isinstance(value, RetargetReadiness) else classify_retarget_readiness(value)
    )
    return readiness.label


# Compatibility aliases used by integration code and policy reconstructions.
validate_verified_mapping_certificate = revalidate_verified_dl2_advanced_body_map
format_automatic_retarget_readiness = format_retarget_readiness


__all__ = [
    "AUTOMATIC_RETARGET_PLAN_FORMAT",
    "AUTOMATIC_RETARGET_VALIDATION_FORMAT",
    "BUILDABLE_MAPPING_MODES",
    "DEFAULT_ANALYZER_VERSION",
    "DEFAULT_LEXICON_VERSION",
    "DEFAULT_SEMANTIC_POLICY_VERSION",
    "DL2_ADVANCED_BODY_CERTIFICATE_FORMAT",
    "DL2_ADVANCED_RIG_ID",
    "DL2_BUNDLED_BODY_CERTIFICATE_FORMATS",
    "DL2_LEGACY_BODY_CERTIFICATE_FORMAT",
    "DL2_LEGACY_RIG_ID",
    "MAPPING_MODES",
    "PLANNER_VERSION",
    "AutomaticRetargetPlan",
    "AutomaticRetargetValidation",
    "MappingDecision",
    "MappingEvidence",
    "RoleMappingOverride",
    "RetargetReadiness",
    "build_automatic_retarget_plan",
    "build_dl2_advanced_body_map_with_local_recipe",
    "build_verified_dl2_advanced_body_map",
    "classify_retarget_readiness",
    "format_automatic_retarget_readiness",
    "format_retarget_readiness",
    "materialize_automatic_retarget_plan",
    "revalidate_verified_dl2_advanced_body_map",
    "validate_automatic_retarget_plan",
    "validate_verified_mapping_certificate",
]
