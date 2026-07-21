"""Versioned, fail-closed storage for accepted automatic retarget decisions.

A recipe is keyed by structural source/target identity and every policy version
that can change semantic interpretation.  Animation identity is recorded for
auditability but intentionally excluded from the key: recipes are useful only
when the same bound skeleton can be reused across clips. Application always
reruns the planner, merges explicitly reviewed structural decisions onto live
target rows, and validates the merged candidate before returning it.
"""

from __future__ import annotations

from dataclasses import asdict, dataclass, field, replace
import json
import os
from pathlib import Path
import tempfile
from typing import Any, Iterable, Mapping

from .automatic_retarget import (
    AUTOMATIC_RETARGET_PLAN_FORMAT,
    AutomaticRetargetPlan,
    MappingDecision,
    MappingEvidence,
    PLANNER_VERSION,
    build_automatic_retarget_plan,
    materialize_automatic_retarget_plan,
    validate_automatic_retarget_plan,
)


RETARGET_RECIPE_FORMAT = "dl-reanimated-retarget-recipe-v1"
RETARGET_RECIPE_VALIDATION_FORMAT = (
    "dl-reanimated-retarget-recipe-validation-v1"
)
RETARGET_RECIPE_SCHEMA_VERSION = 1

_REVIEWED_RECIPE_CREATORS = frozenset(
    {
        "human_review",
        "human_reviewed",
        "manual_review",
        "manual_reviewed",
        "manually_reviewed",
        "operator_reviewed",
        "reviewed",
        "user_review",
        "user_reviewed",
    }
)

_TARGET_IDENTITY_FIELDS = (
    "target_bone",
    "target_descriptor",
    "target_category",
    "parent_target_bone",
)


def _plain(value: Any) -> Any:
    if isinstance(value, Mapping):
        return {str(key): _plain(item) for key, item in value.items()}
    if isinstance(value, (tuple, list, set, frozenset)):
        return [_plain(item) for item in value]
    if hasattr(value, "to_dict") and callable(value.to_dict):
        return _plain(value.to_dict())
    return value


def _object_value(value: Any, name: str, default: Any = None) -> Any:
    if isinstance(value, Mapping):
        return value.get(name, default)
    return getattr(value, name, default)


def _stable_hash(value: Any) -> str:
    import hashlib

    payload = json.dumps(
        _plain(value),
        ensure_ascii=False,
        sort_keys=True,
        separators=(",", ":"),
    ).encode("utf-8")
    return hashlib.sha256(payload).hexdigest()


@dataclass(frozen=True, slots=True)
class RetargetRecipeKey:
    source_skeleton_hash: str
    source_name_parent_hash: str
    source_bind_hash: str
    target_rig_id: str
    target_skeleton_hash: str
    target_policy_id: str
    analyzer_version: str
    planner_version: str
    semantic_policy_version: str
    lexicon_version: str
    clip_domain: str = "body"

    @property
    def key_hash(self) -> str:
        return _stable_hash(asdict(self))

    @property
    def source_signature(self) -> str:
        return self.source_name_parent_hash

    def to_dict(self) -> dict[str, Any]:
        result = asdict(self)
        result["source_signature"] = self.source_signature
        result["key_hash"] = self.key_hash
        return result

    @classmethod
    def from_dict(cls, payload: Mapping[str, Any]) -> "RetargetRecipeKey":
        return cls(
            source_skeleton_hash=str(payload.get("source_skeleton_hash", "")),
            source_name_parent_hash=str(
                payload.get("source_name_parent_hash")
                or payload.get("source_signature", "")
            ),
            source_bind_hash=str(payload.get("source_bind_hash", "")),
            target_rig_id=str(payload.get("target_rig_id", "")),
            target_skeleton_hash=str(payload.get("target_skeleton_hash", "")),
            target_policy_id=str(payload.get("target_policy_id", "")),
            analyzer_version=str(payload.get("analyzer_version", "")),
            planner_version=str(payload.get("planner_version", "")),
            semantic_policy_version=str(
                payload.get("semantic_policy_version", "")
            ),
            lexicon_version=str(payload.get("lexicon_version", "")),
            clip_domain=str(payload.get("clip_domain", "body") or "body"),
        )


def _structural_decision_payload(decisions: Iterable[MappingDecision]) -> list[dict[str, Any]]:
    """Fields that must remain stable when a recipe is reused for another clip."""

    return [
        {
            "target_bone": row.target_bone,
            "target_descriptor": row.target_descriptor,
            "target_category": row.target_category,
            "mode": row.mode,
            "source_bones": list(row.source_bones),
            "semantic_role": row.semantic_role,
            "confidence": row.confidence,
            "confidence_margin": row.confidence_margin,
            "evidence": [item.to_dict() for item in row.evidence],
            "critical": row.critical,
            "parent_target_bone": row.parent_target_bone,
        }
        for row in decisions
    ]


def _has_reviewed_provenance(created_by: str) -> bool:
    token = str(created_by).strip().casefold().replace("-", "_").replace(" ", "_")
    return token in _REVIEWED_RECIPE_CREATORS


def _target_inventory_errors(
    recorded: tuple[MappingDecision, ...],
    live: tuple[MappingDecision, ...],
) -> tuple[str, ...]:
    """Require a complete, ordered, and immutable target identity inventory."""

    errors: list[str] = []
    if len(recorded) != len(live):
        errors.append(
            f"recipe target row inventory changed: {len(recorded)}/{len(live)}"
        )
    recorded_names = [row.target_bone for row in recorded]
    live_names = [row.target_bone for row in live]
    if len(recorded_names) != len(set(recorded_names)):
        errors.append("recipe target row inventory contains duplicates")
    if recorded_names != live_names:
        errors.append("recipe target row order or names changed")
    for recorded_row, live_row in zip(recorded, live):
        for field_name in _TARGET_IDENTITY_FIELDS:
            if getattr(recorded_row, field_name) != getattr(live_row, field_name):
                errors.append(
                    f"recipe target identity field {field_name!r} changed "
                    f"at {live_row.target_bone!r}"
                )
    return tuple(dict.fromkeys(errors))


def _merge_reviewed_decisions(
    recorded: tuple[MappingDecision, ...],
    live: tuple[MappingDecision, ...],
) -> tuple[MappingDecision, ...]:
    """Keep reviewed structure while refreshing clip-dependent row fields."""

    merged: list[MappingDecision] = []
    for stored, current in zip(recorded, live):
        merged.append(
            MappingDecision(
                target_bone=current.target_bone,
                target_descriptor=current.target_descriptor,
                target_category=current.target_category,
                mode=stored.mode,
                source_bones=stored.source_bones,
                semantic_role=stored.semantic_role,
                confidence=stored.confidence,
                confidence_margin=stored.confidence_margin,
                evidence=stored.evidence,
                reason=stored.reason,
                critical=current.critical,
                animated=current.animated,
                parent_target_bone=current.parent_target_bone,
            )
        )
    return tuple(merged)


def _remaining_unresolved_chains(
    fresh: AutomaticRetargetPlan,
    decisions: tuple[MappingDecision, ...],
) -> tuple[str, ...]:
    consumed = {
        source_name
        for row in decisions
        if row.mode in {"direct", "composed", "distributed"}
        for source_name in row.source_bones
    }
    return tuple(
        name
        for name in fresh.unresolved_animated_chains
        if name not in consumed
    )


@dataclass(frozen=True, slots=True)
class RetargetRecipe:
    key: RetargetRecipeKey
    decisions: tuple[MappingDecision, ...]
    baseline_animation_hash: str
    source_archetype: str
    source_archetype_confidence: float
    source_family_hints: tuple[str, ...] = ()
    created_by: str = "automatic_planner"
    notes: str = ""
    extensions: tuple[tuple[str, Any], ...] = ()
    format: str = RETARGET_RECIPE_FORMAT
    schema_version: int = RETARGET_RECIPE_SCHEMA_VERSION

    @property
    def decision_fingerprint(self) -> str:
        return _stable_hash(_structural_decision_payload(self.decisions))

    @property
    def recipe_id(self) -> str:
        return _stable_hash(
            {
                "key_hash": self.key.key_hash,
                "decision_fingerprint": self.decision_fingerprint,
            }
        )

    def to_dict(self) -> dict[str, Any]:
        return {
            "format": self.format,
            "schema_version": self.schema_version,
            "recipe_id": self.recipe_id,
            "key": self.key.to_dict(),
            "decisions": [row.to_dict() for row in self.decisions],
            "decision_fingerprint": self.decision_fingerprint,
            "baseline_animation_hash": self.baseline_animation_hash,
            "source_archetype": self.source_archetype,
            "source_archetype_confidence": self.source_archetype_confidence,
            "source_family_hints": list(self.source_family_hints),
            "created_by": self.created_by,
            "notes": self.notes,
            "extensions": {key: _plain(value) for key, value in self.extensions},
        }

    @classmethod
    def from_dict(cls, payload: Mapping[str, Any]) -> "RetargetRecipe":
        if payload.get("format") != RETARGET_RECIPE_FORMAT:
            raise ValueError("Not a DL ReAnimated retarget recipe.")
        if int(payload.get("schema_version", 0)) != RETARGET_RECIPE_SCHEMA_VERSION:
            raise ValueError("Unsupported retarget recipe schema version.")
        decisions = tuple(_decision_from_dict(row) for row in payload.get("decisions", ()))
        extensions = payload.get("extensions", {}) or {}
        recipe = cls(
            key=RetargetRecipeKey.from_dict(dict(payload.get("key", {}) or {})),
            decisions=decisions,
            baseline_animation_hash=str(payload.get("baseline_animation_hash", "")),
            source_archetype=str(payload.get("source_archetype", "unknown")),
            source_archetype_confidence=float(
                payload.get("source_archetype_confidence", 0.0) or 0.0
            ),
            source_family_hints=tuple(
                str(value) for value in payload.get("source_family_hints", ())
            ),
            created_by=str(payload.get("created_by", "automatic_planner")),
            notes=str(payload.get("notes", "")),
            extensions=tuple(
                sorted(
                    ((str(key), value) for key, value in dict(extensions).items()),
                    key=lambda row: row[0],
                )
            ),
        )
        declared_fingerprint = str(payload.get("decision_fingerprint", ""))
        if declared_fingerprint and declared_fingerprint != recipe.decision_fingerprint:
            raise ValueError("Retarget recipe decision fingerprint is invalid.")
        declared_id = str(payload.get("recipe_id", ""))
        if declared_id and declared_id != recipe.recipe_id:
            raise ValueError("Retarget recipe ID is invalid.")
        return recipe


@dataclass(frozen=True, slots=True)
class RetargetRecipeValidation:
    status: str
    errors: tuple[str, ...] = ()
    warnings: tuple[str, ...] = ()
    live_key_hash: str = ""
    live_decision_fingerprint: str = ""
    recipe_id: str = ""
    live_revalidated: bool = False
    format: str = RETARGET_RECIPE_VALIDATION_FORMAT
    fresh_plan: AutomaticRetargetPlan | None = field(default=None, repr=False, compare=False)

    @property
    def ok(self) -> bool:
        return self.status == "pass" and not self.errors and self.live_revalidated

    def require_valid(self) -> None:
        if self.ok:
            return
        raise ValueError(
            "Retarget recipe verification failed:\n- "
            + "\n- ".join(self.errors or ("live revalidation did not pass",))
        )

    def to_dict(self) -> dict[str, Any]:
        return {
            "format": self.format,
            "status": self.status,
            "errors": list(self.errors),
            "warnings": list(self.warnings),
            "live_key_hash": self.live_key_hash,
            "live_decision_fingerprint": self.live_decision_fingerprint,
            "recipe_id": self.recipe_id,
            "live_revalidated": self.live_revalidated,
        }


def _decision_from_dict(payload: Mapping[str, Any]) -> MappingDecision:
    evidence = tuple(
        MappingEvidence(
            kind=str(item.get("kind", "evidence")),
            score=float(item.get("score", 0.0) or 0.0),
            detail=str(item.get("detail", "")),
            source=str(item.get("source", "analyzer")),
        )
        for item in payload.get("evidence", ())
    )
    return MappingDecision(
        target_bone=str(payload.get("target_bone", "")),
        target_descriptor=int(payload.get("target_descriptor", 0) or 0),
        target_category=str(payload.get("target_category", "body")),
        mode=str(payload.get("mode", "manual_required")),
        source_bones=tuple(str(value) for value in payload.get("source_bones", ())),
        semantic_role=str(payload.get("semantic_role", "")),
        confidence=float(payload.get("confidence", 0.0) or 0.0),
        confidence_margin=float(payload.get("confidence_margin", 0.0) or 0.0),
        evidence=evidence,
        reason=str(payload.get("reason", "")),
        critical=bool(payload.get("critical", False)),
        animated=bool(payload.get("animated", False)),
        parent_target_bone=str(payload.get("parent_target_bone", "")),
    )


def recipe_key_for_plan(plan: AutomaticRetargetPlan) -> RetargetRecipeKey:
    return RetargetRecipeKey(
        source_skeleton_hash=plan.source_skeleton_hash,
        source_name_parent_hash=plan.source_name_parent_hash,
        source_bind_hash=plan.source_bind_hash,
        target_rig_id=plan.target_rig_id,
        target_skeleton_hash=plan.target_skeleton_hash,
        target_policy_id=plan.target_policy_id,
        analyzer_version=plan.analyzer_version,
        planner_version=plan.planner_version,
        semantic_policy_version=plan.semantic_policy_version,
        lexicon_version=plan.lexicon_version,
        clip_domain=plan.clip_domain,
    )


def build_retarget_recipe(
    plan: AutomaticRetargetPlan,
    *,
    decisions: Iterable[MappingDecision] | None = None,
    created_by: str = "automatic_planner",
    notes: str = "",
    extensions: Mapping[str, Any] | None = None,
) -> RetargetRecipe:
    """Capture accepted structural decisions from a valid planner result."""

    if plan.format != AUTOMATIC_RETARGET_PLAN_FORMAT:
        raise ValueError("Unsupported automatic retarget plan format.")
    if plan.planner_version != PLANNER_VERSION:
        raise ValueError("Cannot store a recipe produced by a stale planner.")
    rows = tuple(decisions) if decisions is not None else plan.decisions
    inventory_errors = _target_inventory_errors(rows, plan.decisions)
    if inventory_errors:
        raise ValueError(
            "A retarget recipe must preserve the exact target inventory:\n- "
            + "\n- ".join(inventory_errors)
        )
    if any(row.mode == "manual_required" for row in rows):
        raise ValueError("Resolve animated critical rows before storing a recipe.")
    decisions_differ = _stable_hash(
        _structural_decision_payload(rows)
    ) != _stable_hash(_structural_decision_payload(plan.decisions))
    if decisions_differ and not _has_reviewed_provenance(created_by):
        raise ValueError(
            "Differing retarget decisions require explicit reviewed provenance "
            "(for example, created_by='manual_reviewed')."
        )
    return RetargetRecipe(
        key=recipe_key_for_plan(plan),
        decisions=rows,
        baseline_animation_hash=plan.source_animation_hash,
        source_archetype=plan.source_archetype,
        source_archetype_confidence=plan.source_archetype_confidence,
        source_family_hints=plan.source_family_hints,
        created_by=str(created_by),
        notes=str(notes),
        extensions=tuple(
            sorted(
                ((str(key), value) for key, value in dict(extensions or {}).items()),
                key=lambda row: row[0],
            )
        ),
    )


def validate_retarget_recipe(
    recipe: RetargetRecipe,
    source: Any,
    target_rig: Any,
    target_policy: Any = None,
    *,
    clip_domain: str | None = None,
) -> RetargetRecipeValidation:
    """Recompute live identities and safely reapply any reviewed decisions."""

    errors: list[str] = []
    domain = str(clip_domain or recipe.key.clip_domain)
    try:
        fresh = build_automatic_retarget_plan(
            source, target_rig, target_policy, clip_domain=domain
        )
    except (TypeError, ValueError) as exc:
        return RetargetRecipeValidation(
            "fail",
            (str(exc),),
            recipe_id=recipe.recipe_id,
            live_revalidated=False,
        )
    live_key = recipe_key_for_plan(fresh)
    recorded_key = recipe.key.to_dict()
    current_key = live_key.to_dict()
    for name in (
        "source_skeleton_hash",
        "source_name_parent_hash",
        "source_bind_hash",
        "target_rig_id",
        "target_skeleton_hash",
        "target_policy_id",
        "analyzer_version",
        "planner_version",
        "semantic_policy_version",
        "lexicon_version",
        "clip_domain",
    ):
        if recorded_key[name] != current_key[name]:
            errors.append(f"recipe key field {name!r} changed")
    errors.extend(_target_inventory_errors(recipe.decisions, fresh.decisions))

    fresh_fingerprint = _stable_hash(
        _structural_decision_payload(fresh.decisions)
    )
    decisions_differ = recipe.decision_fingerprint != fresh_fingerprint
    reviewed_override = decisions_differ and _has_reviewed_provenance(
        recipe.created_by
    )
    candidate: AutomaticRetargetPlan | None = None
    warnings: list[str] = []
    if decisions_differ and not reviewed_override:
        errors.append("live structural mapping decisions changed")
    if not errors:
        candidate_decisions = (
            _merge_reviewed_decisions(recipe.decisions, fresh.decisions)
            if reviewed_override
            else fresh.decisions
        )
        candidate = replace(
            fresh,
            decisions=candidate_decisions,
            unresolved_animated_chains=_remaining_unresolved_chains(
                fresh, candidate_decisions
            ),
        )
        candidate_validation = validate_automatic_retarget_plan(
            candidate,
            source,
            target_rig,
            target_policy,
        )
        if not candidate_validation.ok:
            errors.extend(candidate_validation.errors)
        elif reviewed_override:
            warnings.append(
                "Explicitly reviewed structural decisions were applied to the "
                "live source and target identities."
            )

    live_fingerprint = _stable_hash(
        _structural_decision_payload(
            candidate.decisions if candidate is not None else fresh.decisions
        )
    )
    status = "pass" if not errors else "fail"
    return RetargetRecipeValidation(
        status=status,
        errors=tuple(dict.fromkeys(errors)),
        warnings=tuple(warnings),
        live_key_hash=live_key.key_hash,
        live_decision_fingerprint=live_fingerprint,
        recipe_id=recipe.recipe_id,
        live_revalidated=status == "pass",
        fresh_plan=candidate,
    )


def apply_retarget_recipe(
    recipe: RetargetRecipe,
    source: Any,
    target_rig: Any,
    target_policy: Any = None,
    *,
    clip_domain: str | None = None,
) -> AutomaticRetargetPlan:
    """Return the live plan, including valid reviewed decisions, after checks."""

    result = validate_retarget_recipe(
        recipe,
        source,
        target_rig,
        target_policy,
        clip_domain=clip_domain,
    )
    result.require_valid()
    assert result.fresh_plan is not None
    return result.fresh_plan


def save_retarget_recipe(recipe: RetargetRecipe, path: str | Path) -> Path:
    destination = Path(path)
    destination.parent.mkdir(parents=True, exist_ok=True)
    handle, temporary_name = tempfile.mkstemp(
        prefix=f".{destination.name}.",
        suffix=".tmp",
        dir=destination.parent,
    )
    try:
        with os.fdopen(handle, "w", encoding="utf-8", newline="\n") as stream:
            json.dump(recipe.to_dict(), stream, indent=2, ensure_ascii=False)
            stream.write("\n")
            stream.flush()
            os.fsync(stream.fileno())
        os.replace(temporary_name, destination)
    finally:
        if os.path.exists(temporary_name):
            os.unlink(temporary_name)
    return destination


def load_retarget_recipe(path: str | Path) -> RetargetRecipe:
    return RetargetRecipe.from_dict(
        json.loads(Path(path).read_text(encoding="utf-8-sig"))
    )


class RetargetRecipeStore:
    """Small deterministic on-disk cache keyed by :class:`RetargetRecipeKey`."""

    def __init__(self, root: str | Path) -> None:
        self.root = Path(root)

    def path_for_key(self, key: RetargetRecipeKey) -> Path:
        return self.root / f"{key.key_hash}.dlrrecipe.json"

    def save(self, recipe: RetargetRecipe) -> Path:
        return save_retarget_recipe(recipe, self.path_for_key(recipe.key))

    def load(self, key: RetargetRecipeKey) -> RetargetRecipe | None:
        path = self.path_for_key(key)
        if not path.is_file():
            return None
        recipe = load_retarget_recipe(path)
        if recipe.key != key:
            raise ValueError("Stored retarget recipe key does not match its cache path.")
        return recipe


@dataclass(frozen=True, slots=True)
class LocalRetargetRecipeResolution:
    fresh_plan: AutomaticRetargetPlan
    plan: AutomaticRetargetPlan
    recipe: RetargetRecipe | None = None
    validation: RetargetRecipeValidation | None = None

    @property
    def applied(self) -> bool:
        return (
            self.recipe is not None
            and self.validation is not None
            and self.validation.ok
        )


def retarget_recipe_has_reviewed_provenance(recipe: RetargetRecipe) -> bool:
    """Return whether a typed recipe records an explicit review action."""

    return _has_reviewed_provenance(recipe.created_by)


def default_retarget_recipe_store(
    application_root: str | Path | None = None,
) -> RetargetRecipeStore:
    """Return the deterministic per-user recipe cache."""

    if application_root is None:
        from .runtime_paths import default_user_root

        application_root = default_user_root()
    return RetargetRecipeStore(Path(application_root) / "retarget_recipes")


def resolve_local_retarget_recipe(
    fresh: AutomaticRetargetPlan,
    source: Any,
    target_rig: Any,
    target_policy: Any = None,
    *,
    store: RetargetRecipeStore | None = None,
) -> LocalRetargetRecipeResolution:
    """Resolve a reviewed cache entry without weakening the fresh fallback."""

    active_store = store or default_retarget_recipe_store()
    key = recipe_key_for_plan(fresh)
    try:
        recipe = active_store.load(key)
    except (OSError, TypeError, ValueError):
        return LocalRetargetRecipeResolution(fresh, fresh)
    if (
        recipe is None
        or recipe.key != key
        or not retarget_recipe_has_reviewed_provenance(recipe)
    ):
        return LocalRetargetRecipeResolution(fresh, fresh)
    validation = validate_retarget_recipe(
        recipe,
        source,
        target_rig,
        target_policy,
        clip_domain=fresh.clip_domain,
    )
    if not validation.ok or validation.fresh_plan is None:
        return LocalRetargetRecipeResolution(
            fresh, fresh, recipe, validation
        )
    return LocalRetargetRecipeResolution(
        fresh,
        validation.fresh_plan,
        recipe,
        validation,
    )


def apply_local_retarget_recipe(
    fresh: AutomaticRetargetPlan,
    source: Any,
    target_rig: Any,
    target_policy: Any = None,
    *,
    store: RetargetRecipeStore | None = None,
) -> AutomaticRetargetPlan:
    """Apply a matching reviewed local recipe, otherwise retain ``fresh``.

    Cache content is never authority by itself. The typed recipe key must match
    the freshly computed key, the provenance must be explicitly reviewed, and
    the normal live recipe validator must pass before a correction is reused.
    Corrupt, stale, unreviewed, or invalid cache entries leave the fresh plan
    unchanged so its ordinary readiness/attention state remains authoritative.
    """

    return resolve_local_retarget_recipe(
        fresh,
        source,
        target_rig,
        target_policy,
        store=store,
    ).plan


def build_retarget_plan_with_local_recipe(
    source: Any,
    target_rig: Any,
    target_policy: Any = None,
    *,
    clip_domain: str = "body",
    store: RetargetRecipeStore | None = None,
) -> AutomaticRetargetPlan:
    """Build a live plan and reuse only a matching, reviewed local recipe."""

    fresh = build_automatic_retarget_plan(
        source,
        target_rig,
        target_policy,
        clip_domain=clip_domain,
    )
    return apply_local_retarget_recipe(
        fresh,
        source,
        target_rig,
        target_policy,
        store=store,
    )


def build_reviewed_retarget_recipe_from_profile(
    fresh: AutomaticRetargetPlan,
    profile: Any,
    source: Any,
    target_rig: Any,
    target_policy: Any = None,
    *,
    notes: str = "",
) -> RetargetRecipe:
    """Convert an explicitly reviewed mapping profile into a live-safe recipe.

    Recipe rows represent source assignment and bind inheritance only. Manual
    mapped rows therefore must use a supported rotation-only global-bind or
    rotation-delta policy; transfer/component choices that cannot round-trip
    are rejected.
    """

    extensions = dict(_object_value(profile, "extensions", {}) or {})
    if str(extensions.get("origin", "")) != "manually_reviewed":
        raise ValueError(
            "Exporting a retarget recipe requires explicitly reviewed manual corrections."
        )

    pairs = tuple(_object_value(profile, "pairs", ()) or ())
    pairs_by_target: dict[str, Any] = {}
    for pair in pairs:
        target_name = str(_object_value(pair, "target_rig_bone", "") or "")
        if not target_name:
            raise ValueError("Reviewed mapping contains a row without a target bone.")
        if target_name in pairs_by_target:
            raise ValueError(
                f"Reviewed mapping contains duplicate target {target_name!r}."
            )
        pairs_by_target[target_name] = pair

    expected = {row.target_bone: row for row in fresh.decisions}
    unknown = sorted(set(pairs_by_target) - set(expected), key=str.casefold)
    if unknown:
        raise ValueError(
            "Reviewed mapping targets bones outside the live target inventory: "
            + ", ".join(unknown[:12])
        )

    decisions: list[MappingDecision] = []
    for current in fresh.decisions:
        pair = pairs_by_target.get(current.target_bone)
        if pair is None:
            if current.mode in {"inherit_bind", "static_bind", "ignored_source"}:
                decisions.append(current)
            else:
                decisions.append(
                    replace(
                        current,
                        mode=(
                            "inherit_bind"
                            if current.parent_target_bone
                            else "static_bind"
                        ),
                        source_bones=(),
                        confidence=1.0,
                        confidence_margin=1.0,
                        evidence=(
                            MappingEvidence(
                                "manual_review",
                                1.0,
                                "reviewer intentionally held this target at bind",
                                "reviewer",
                            ),
                        ),
                        reason="reviewed local correction: hold target at bind",
                    )
                )
            continue

        descriptor = int(_object_value(pair, "target_rig_descriptor", 0) or 0)
        if descriptor != current.target_descriptor:
            raise ValueError(
                f"Reviewed mapping target descriptor changed at {current.target_bone!r}."
            )
        source_name = str(_object_value(pair, "source_fbx_bone", "") or "")
        transfer = str(
            _object_value(pair, "transfer_policy", "default") or "default"
        )
        components = str(
            _object_value(pair, "component_policy", "full_transform")
            or "full_transform"
        )
        if source_name:
            if transfer not in {
                "default",
                "global_bind_basis",
                "rotation_delta",
            } or components != "rotation":
                raise ValueError(
                    f"{current.target_bone}: recipe export supports only rotation-only "
                    "global-bind or rotation-delta manual mappings."
                )
        elif transfer != "bind" or components != "rotation":
            raise ValueError(
                f"{current.target_bone}: an explicit bind recipe row must use "
                "bind transfer with rotation components."
            )
        pair_extensions = dict(_object_value(pair, "extensions", {}) or {})
        serialized = pair_extensions.get("automatic_retarget_decision")
        stored: MappingDecision | None = None
        if isinstance(serialized, Mapping):
            try:
                parsed = _decision_from_dict(serialized)
            except (TypeError, ValueError):
                parsed = None
            if (
                parsed is not None
                and parsed.target_bone == current.target_bone
                and parsed.target_descriptor == current.target_descriptor
                and (
                    (parsed.source_bones[0] if parsed.source_bones else "")
                    == source_name
                )
            ):
                stored = parsed
        if stored is not None:
            decisions.append(
                replace(
                    stored,
                    target_category=current.target_category,
                    critical=current.critical,
                    animated=current.animated,
                    parent_target_bone=current.parent_target_bone,
                )
            )
            continue

        if source_name:
            decisions.append(
                replace(
                    current,
                    mode="direct",
                    source_bones=(source_name,),
                    confidence=1.0,
                    confidence_margin=1.0,
                    evidence=(
                        MappingEvidence(
                            "manual_review",
                            1.0,
                            "reviewer selected the source assignment",
                            "reviewer",
                        ),
                    ),
                    reason="reviewed local source assignment",
                )
            )
        else:
            decisions.append(
                replace(
                    current,
                    mode=(
                        "inherit_bind"
                        if current.parent_target_bone
                        else "static_bind"
                    ),
                    source_bones=(),
                    confidence=1.0,
                    confidence_margin=1.0,
                    evidence=(
                        MappingEvidence(
                            "manual_review",
                            1.0,
                            "reviewer intentionally held this target at bind",
                            "reviewer",
                        ),
                    ),
                    reason="reviewed local correction: hold target at bind",
                )
            )

    reviewed_decisions = tuple(decisions)
    candidate = replace(
        fresh,
        decisions=reviewed_decisions,
        unresolved_animated_chains=_remaining_unresolved_chains(
            fresh, reviewed_decisions
        ),
    )
    validate_automatic_retarget_plan(
        candidate,
        source,
        target_rig,
        target_policy,
    ).require_valid()
    return build_retarget_recipe(
        fresh,
        decisions=reviewed_decisions,
        created_by="manual_reviewed",
        notes=notes,
    )


def materialize_reviewed_retarget_recipe(
    recipe: RetargetRecipe,
    source: Any,
    target_rig: Any,
    target_policy: Any = None,
    *,
    clip_domain: str | None = None,
    profile_name: str = "Reviewed local retarget recipe",
) -> Any:
    """Live-validate and materialize a recipe as a reviewed, non-certified map."""

    if not retarget_recipe_has_reviewed_provenance(recipe):
        raise ValueError(
            "A local retarget recipe requires explicit reviewed provenance."
        )
    validation = validate_retarget_recipe(
        recipe,
        source,
        target_rig,
        target_policy,
        clip_domain=clip_domain,
    )
    validation.require_valid()
    assert validation.fresh_plan is not None
    profile = materialize_automatic_retarget_plan(
        validation.fresh_plan,
        source,
        target_rig,
        target_policy,
        profile_name=profile_name,
    )
    if (
        validation.fresh_plan.target_policy_id
        == "dl2_advanced_body_bridge_v1"
        and str(_object_value(target_rig, "rig_id", ""))
        == "builtin:dl2_player_advanced"
    ):
        decisions_by_target = {
            row.target_bone: row for row in validation.fresh_plan.decisions
        }
        for pair in profile.pairs:
            decision = decisions_by_target[pair.target_rig_bone]
            pair.transfer_policy = (
                "rotation_delta"
                if decision.mode in {"composed", "distributed"}
                else "global_bind_basis"
                if decision.mode == "direct"
                else "bind"
            )
            pair.component_policy = "rotation"
    from .bone_maps import set_mapping_profile_origin

    set_mapping_profile_origin(profile, "manually_reviewed")
    profile.extensions["local_retarget_recipe"] = {
        "format": "dl-reanimated-applied-retarget-recipe-v1",
        "recipe_id": recipe.recipe_id,
        "key_hash": recipe.key.key_hash,
        "decision_fingerprint": recipe.decision_fingerprint,
        "created_by": recipe.created_by,
        "baseline_animation_hash": recipe.baseline_animation_hash,
        "live_animation_hash": validation.fresh_plan.source_animation_hash,
        "live_revalidated": True,
    }
    return profile


def revalidate_materialized_retarget_recipe(
    profile: Any,
    source: Any,
    target_rig: Any,
    target_policy: Any = None,
    *,
    clip_domain: str = "body",
) -> RetargetRecipeValidation:
    """Revalidate an applied recipe profile without consulting the cache."""

    profile_extensions = dict(
        _object_value(profile, "extensions", {}) or {}
    )
    provenance = dict(
        profile_extensions.get("local_retarget_recipe", {}) or {}
    )
    errors: list[str] = []
    if provenance.get("format") != "dl-reanimated-applied-retarget-recipe-v1":
        errors.append("applied retarget recipe provenance format is missing or invalid")
    if not _has_reviewed_provenance(str(provenance.get("created_by", ""))):
        errors.append("applied retarget recipe has no reviewed provenance")
    try:
        fresh = build_automatic_retarget_plan(
            source,
            target_rig,
            target_policy,
            clip_domain=clip_domain,
        )
        reconstructed = build_reviewed_retarget_recipe_from_profile(
            fresh,
            profile,
            source,
            target_rig,
            target_policy,
            notes="cache-independent live reconstruction",
        )
        validation = validate_retarget_recipe(
            reconstructed,
            source,
            target_rig,
            target_policy,
            clip_domain=clip_domain,
        )
    except (OSError, TypeError, ValueError) as exc:
        return RetargetRecipeValidation(
            "fail",
            tuple(dict.fromkeys((*errors, str(exc)))),
            recipe_id=str(provenance.get("recipe_id", "")),
            live_revalidated=False,
        )

    if provenance.get("key_hash") != reconstructed.key.key_hash:
        errors.append("applied retarget recipe source/target/policy key changed")
    if (
        provenance.get("decision_fingerprint")
        != reconstructed.decision_fingerprint
    ):
        errors.append("applied retarget recipe decisions changed")
    if provenance.get("recipe_id") != reconstructed.recipe_id:
        errors.append("applied retarget recipe ID changed")
    errors.extend(validation.errors)
    status = "pass" if not errors and validation.ok else "fail"
    return RetargetRecipeValidation(
        status,
        tuple(dict.fromkeys(errors)),
        validation.warnings,
        live_key_hash=reconstructed.key.key_hash,
        live_decision_fingerprint=reconstructed.decision_fingerprint,
        recipe_id=reconstructed.recipe_id,
        live_revalidated=status == "pass",
        fresh_plan=(validation.fresh_plan if status == "pass" else None),
    )


# Concise aliases for integration call sites.
validate_recipe = validate_retarget_recipe
apply_recipe = apply_retarget_recipe


__all__ = [
    "RETARGET_RECIPE_FORMAT",
    "RETARGET_RECIPE_SCHEMA_VERSION",
    "RETARGET_RECIPE_VALIDATION_FORMAT",
    "LocalRetargetRecipeResolution",
    "RetargetRecipe",
    "RetargetRecipeKey",
    "RetargetRecipeStore",
    "RetargetRecipeValidation",
    "apply_local_retarget_recipe",
    "apply_recipe",
    "apply_retarget_recipe",
    "build_retarget_plan_with_local_recipe",
    "build_reviewed_retarget_recipe_from_profile",
    "build_retarget_recipe",
    "default_retarget_recipe_store",
    "load_retarget_recipe",
    "materialize_reviewed_retarget_recipe",
    "recipe_key_for_plan",
    "revalidate_materialized_retarget_recipe",
    "retarget_recipe_has_reviewed_provenance",
    "resolve_local_retarget_recipe",
    "save_retarget_recipe",
    "validate_recipe",
    "validate_retarget_recipe",
]
