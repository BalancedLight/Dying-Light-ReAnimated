"""User-facing semantic profiles for bundled humanoid target rigs.

The semantic profile is the editable source of truth.  Target-sized CRIG maps
are compiled from it and live analyzer evidence only at validation/build time.
"""

from __future__ import annotations

from dataclasses import dataclass
import hashlib
import json
from typing import Any, Iterable, Mapping

from .automatic_retarget import (
    AutomaticRetargetPlan,
    AutomaticRetargetValidation,
    MappingDecision,
    RoleMappingOverride,
    build_automatic_retarget_plan,
    build_verified_dl2_advanced_body_map,
    classify_retarget_readiness,
    revalidate_verified_dl2_advanced_body_map,
    validate_automatic_retarget_plan,
)
from .bone_maps import GenericBoneMap, mapping_profile_origin
from .retarget_profiles import ROLE_BY_ID, SourceBoneMappingProfile


_POLICY_TO_PROFILE_ROLE = {
    "pelvis": "hips",
    "spine_1": "spine",
    "spine_2": "chest",
    "spine_3": "upper_chest",
    "neck_1": "neck",
    "head": "head",
    "left_clavicle": "left_shoulder",
    "left_upper_arm": "left_upper_arm",
    "left_forearm": "left_lower_arm",
    "left_hand": "left_hand",
    "right_clavicle": "right_shoulder",
    "right_upper_arm": "right_upper_arm",
    "right_forearm": "right_lower_arm",
    "right_hand": "right_hand",
    "left_thigh": "left_upper_leg",
    "left_calf": "left_lower_leg",
    "left_foot": "left_foot",
    "left_toe": "left_toes",
    "right_thigh": "right_upper_leg",
    "right_calf": "right_lower_leg",
    "right_foot": "right_foot",
    "right_toe": "right_toes",
}


def profile_role_for_semantic_role(semantic_role: str) -> str:
    value = str(semantic_role or "")
    if value in _POLICY_TO_PROFILE_ROLE:
        return _POLICY_TO_PROFILE_ROLE[value]
    if value.startswith("l_"):
        value = "left_" + value[2:]
    elif value.startswith("r_"):
        value = "right_" + value[2:]
    return value if value in ROLE_BY_ID else ""


@dataclass(frozen=True, slots=True)
class SemanticUiRow:
    group: str
    label: str
    profile_role: str
    semantic_role: str
    target_bone: str
    source_bones: tuple[str, ...]
    selected_mode: str
    plan_mode: str
    requirement: str
    confidence: float
    method: str
    result: str
    evidence: tuple[dict[str, Any], ...] = ()


@dataclass(frozen=True, slots=True)
class BundledSemanticState:
    profile: SourceBoneMappingProfile
    plan: AutomaticRetargetPlan
    validation: AutomaticRetargetValidation
    rows: tuple[SemanticUiRow, ...]


def semantic_role_overrides(
    profile: SourceBoneMappingProfile,
    policy: Any,
) -> tuple[RoleMappingOverride, ...]:
    rows: list[RoleMappingOverride] = []
    seen: set[str] = set()
    for slot in tuple(getattr(policy, "direct_slots", ()) or ()):
        semantic_role = str(slot.semantic_role)
        if semantic_role in seen:
            continue
        seen.add(semantic_role)
        profile_role = profile_role_for_semantic_role(semantic_role)
        if not profile_role:
            continue
        mode = profile.role_mode(profile_role)
        if mode == "auto":
            continue
        source_bone = str(profile.role_to_bone.get(profile_role, "") or "")
        if mode not in {"direct", "inherit_bind", "static_bind"}:
            mode = "inherit_bind"
            source_bone = ""
        elif mode == "direct" and not source_bone:
            mode = "inherit_bind"
        rows.append(
            RoleMappingOverride(
                semantic_role,
                mode,
                source_bone,
                profile_role,
            )
        )
    return tuple(rows)


def _decision_by_target(plan: AutomaticRetargetPlan) -> dict[str, MappingDecision]:
    return {row.target_bone: row for row in plan.decisions}


def _sync_profile_from_plan(
    profile: SourceBoneMappingProfile,
    plan: AutomaticRetargetPlan,
    policy: Any,
    source_bones: Iterable[str],
) -> None:
    profile.source_skeleton_hash = plan.source_skeleton_hash
    profile.source_name_parent_hash = plan.source_name_parent_hash
    profile.source_bind_hash = plan.source_bind_hash
    profile.source_animation_hash = plan.source_animation_hash
    profile.target_policy_id = plan.target_policy_id
    profile.target_rig_id = plan.target_rig_id
    profile.target_skeleton_hash = plan.target_skeleton_hash
    by_target = _decision_by_target(plan)
    used: set[str] = set()
    for slot in tuple(getattr(policy, "direct_slots", ()) or ()):
        decision = by_target.get(str(slot.target_bone))
        if decision is None:
            continue
        profile_role = profile_role_for_semantic_role(str(slot.semantic_role))
        if not profile_role:
            continue
        explicit_mode = profile.role_mode(profile_role)
        if explicit_mode == "auto":
            profile.role_modes[profile_role] = "auto"
            if decision.source_bones:
                source_name = decision.source_bones[0]
                profile.role_to_bone[profile_role] = source_name
                used.add(source_name)
            else:
                profile.role_to_bone.pop(profile_role, None)
            profile.confidence_by_role[profile_role] = float(decision.confidence)
            profile.method_by_role[profile_role] = str(decision.mode)
            profile.evidence_by_role[profile_role] = [
                row.to_dict() for row in decision.evidence
            ]
        else:
            used.update(
                [profile.role_to_bone[profile_role]]
                if profile.role_to_bone.get(profile_role)
                else []
            )
    profile.ignored_bones = [
        str(name) for name in sorted(set(source_bones), key=str.casefold) if name not in used
    ]
    profile.extensions["current_automatic_retarget_plan"] = plan.to_dict()
    profile.extensions["manual_override_count"] = profile.manual_override_count


def semantic_ui_rows(
    profile: SourceBoneMappingProfile,
    plan: AutomaticRetargetPlan,
    policy: Any,
) -> tuple[SemanticUiRow, ...]:
    by_target = _decision_by_target(plan)
    rows: list[SemanticUiRow] = []
    for slot in tuple(getattr(policy, "direct_slots", ()) or ()):
        semantic_role = str(slot.semantic_role)
        profile_role = profile_role_for_semantic_role(semantic_role)
        role = ROLE_BY_ID.get(profile_role)
        decision = by_target.get(str(slot.target_bone))
        if role is None or decision is None:
            continue
        if decision.mode == "inherit_bind":
            requirement = "Inherits parent"
        elif decision.mode == "static_bind":
            requirement = "Held at bind"
        elif decision.animated and decision.critical:
            requirement = "Animated critical"
        elif decision.source_bones:
            requirement = "Mapped"
        else:
            requirement = "Optional"
        method = (
            "manual override"
            if any(row.kind == "manual_override" for row in decision.evidence)
            else decision.reason or decision.mode
        )
        source_text = " + ".join(decision.source_bones)
        result = (
            f"{decision.mode}: {source_text}"
            if source_text
            else decision.mode.replace("_", " ")
        )
        rows.append(
            SemanticUiRow(
                role.group,
                role.label,
                profile_role,
                semantic_role,
                str(slot.target_bone),
                tuple(decision.source_bones),
                profile.role_mode(profile_role),
                decision.mode,
                requirement,
                float(decision.confidence),
                method,
                result,
                tuple(row.to_dict() for row in decision.evidence),
            )
        )
    return tuple(rows)


def migrate_generic_map_to_semantic_profile(
    bone_map: GenericBoneMap,
    source_bones: Iterable[str],
    parents: Mapping[str, str | None],
    policy: Any,
    *,
    name: str = "Migrated bundled humanoid mapping",
) -> SourceBoneMappingProfile:
    profile = SourceBoneMappingProfile.empty(source_bones, name=name, parents=parents)
    plan_payload = dict(bone_map.extensions.get("automatic_retarget_plan", {}) or {})
    decisions = list(plan_payload.get("decisions", ()) or ())
    if not decisions:
        decisions = [
            dict(pair.extensions.get("automatic_retarget_decision", {}) or {})
            for pair in bone_map.pairs
            if pair.extensions.get("automatic_retarget_decision")
        ]
    pair_by_target = {pair.target_rig_bone: pair for pair in bone_map.pairs}
    origin = mapping_profile_origin(bone_map)
    reviewed_origin = origin in {"manually_reviewed", "imported_profile"}
    for raw in decisions:
        semantic_role = str(raw.get("semantic_role", "") or "")
        profile_role = profile_role_for_semantic_role(semantic_role)
        if not profile_role:
            continue
        target = str(raw.get("target_bone", "") or "")
        pair = pair_by_target.get(target)
        sources = tuple(str(row) for row in raw.get("source_bones", ()) or ())
        if not sources and pair is not None and pair.source_fbx_bone:
            sources = (pair.source_fbx_bone,)
        reviewed_pair = bool(
            pair is not None
            and pair.review_state
            in {"manually_reviewed", "reviewed", "manual", "user_approved"}
        )
        manual = reviewed_origin or reviewed_pair
        if sources:
            profile.role_to_bone[profile_role] = sources[0]
            profile.role_modes[profile_role] = "direct" if manual else "auto"
            profile.confidence_by_role[profile_role] = float(
                raw.get("confidence", pair.confidence if pair is not None else 0.0)
                or 0.0
            )
            profile.method_by_role[profile_role] = (
                "manual_migrated" if manual else str(raw.get("mode", "auto"))
            )
            profile.evidence_by_role[profile_role] = [
                dict(row) for row in raw.get("evidence", ()) or ()
            ]
    profile.target_policy_id = str(getattr(policy, "policy_id", "") or "")
    profile.target_rig_id = str(getattr(policy, "target_rig_id", "") or "")
    profile.target_skeleton_hash = str(
        getattr(policy, "target_skeleton_hash", "") or ""
    )
    profile.extensions["migration_audit"] = {
        "format": "dl-reanimated-semantic-profile-migration-v1",
        "source_profile_id": bone_map.profile_id,
        "source_profile_origin": origin,
        "source_pair_count": len(bone_map.pairs),
        "source_profile_retained_for_audit": True,
        "manual_choices_preserved": sum(
            value == "direct" for value in profile.role_modes.values()
        ),
    }
    return profile


def prepare_bundled_semantic_state(
    source: Any,
    target_rig: Any,
    policy: Any,
    profile: SourceBoneMappingProfile | None = None,
    *,
    profile_name: str = "Automatic humanoid mapping",
) -> BundledSemanticState:
    source_bones = tuple(str(name) for name in source.limb_models)
    if profile is None:
        profile = SourceBoneMappingProfile.empty(
            source_bones,
            name=profile_name,
            parents=source.parent_by_name,
        )
    overrides = semantic_role_overrides(profile, policy)
    plan = build_automatic_retarget_plan(
        source,
        target_rig,
        policy,
        clip_domain="body",
        role_overrides=overrides,
        target_bone_overrides=profile.target_bone_overrides,
    )
    validation = validate_automatic_retarget_plan(
        plan, source, target_rig, policy
    )
    _sync_profile_from_plan(profile, plan, policy, source_bones)
    return BundledSemanticState(
        profile,
        plan,
        validation,
        semantic_ui_rows(profile, plan, policy),
    )


def compile_bundled_semantic_profile(
    source: Any,
    target_rig: Any,
    policy: Any,
    profile: SourceBoneMappingProfile,
) -> tuple[GenericBoneMap, AutomaticRetargetValidation, AutomaticRetargetPlan]:
    profile_diagnostics = profile.validate(source.limb_models)
    overrides = semantic_role_overrides(profile, policy)
    plan = build_automatic_retarget_plan(
        source,
        target_rig,
        policy,
        clip_domain="body",
        role_overrides=overrides,
        target_bone_overrides=profile.target_bone_overrides,
    )
    validation = validate_automatic_retarget_plan(plan, source, target_rig, policy)
    validation.require_valid()
    compiled = build_verified_dl2_advanced_body_map(
        source,
        target_rig,
        policy,
        role_overrides=overrides,
        target_bone_overrides=profile.target_bone_overrides,
    )
    live = revalidate_verified_dl2_advanced_body_map(
        compiled,
        source,
        target_rig,
        policy,
        role_overrides=overrides,
        target_bone_overrides=profile.target_bone_overrides,
    )
    live.require_valid()
    compiled.extensions["semantic_profile_id"] = profile.profile_id
    compiled.extensions["semantic_manual_override_count"] = (
        profile.manual_override_count
    )
    if profile_diagnostics:
        compiled.extensions["ignored_semantic_profile_diagnostics"] = list(
            profile_diagnostics
        )
    compiled.extensions["semantic_role_to_source"] = dict(profile.role_to_bone)
    compiled.extensions["semantic_root_motion"] = dict(profile.root_motion)
    compiled.extensions["semantic_locomotion"] = dict(profile.locomotion)
    serialized = json.dumps(
        compiled.to_dict(), sort_keys=True, ensure_ascii=False, separators=(",", ":")
    ).encode("utf-8")
    compiled_hash = hashlib.sha256(serialized).hexdigest()
    profile.compiled_map_cache_id = compiled.profile_id
    profile.extensions["compiled_map_hash"] = compiled_hash
    profile.extensions["compiled_validation"] = live.to_dict()
    profile.extensions["selected_engine_hint"] = (
        "MappedRigRetargetEngine"
        if profile.manual_override_count or not plan.exact_identity
        else "ExactRigRetargetEngine"
    )
    _sync_profile_from_plan(profile, plan, policy, source.limb_models)
    return compiled, live, plan


def readiness_for_state(state: BundledSemanticState) -> Any:
    return classify_retarget_readiness(state.plan)


__all__ = [
    "BundledSemanticState",
    "SemanticUiRow",
    "compile_bundled_semantic_profile",
    "migrate_generic_map_to_semantic_profile",
    "prepare_bundled_semantic_state",
    "profile_role_for_semantic_role",
    "readiness_for_state",
    "semantic_role_overrides",
    "semantic_ui_rows",
]
