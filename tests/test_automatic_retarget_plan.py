from __future__ import annotations

from dataclasses import dataclass, replace
from types import SimpleNamespace

import pytest

from dlanm2_gui.automatic_retarget import (
    MappingDecision,
    MappingEvidence,
    _mirror_automatic_decisions,
    build_automatic_retarget_plan,
    classify_retarget_readiness,
    resolve_source_analysis,
    validate_automatic_retarget_plan,
)
from dlanm2_gui.blender_mirror_wrapper import BlenderLateralMirrorContext
import dlanm2_gui.skeleton_analysis as skeleton_analysis
from dlanm2_gui.bone_maps import mapping_profile_origin
from dlanm2_gui.chrome_rig import ChromeRig, ChromeRigBone
from dlanm2_gui.retarget_recipes import (
    RetargetRecipe,
    RetargetRecipeStore,
    apply_retarget_recipe,
    build_retarget_plan_with_local_recipe,
    build_reviewed_retarget_recipe_from_profile,
    build_retarget_recipe,
    load_retarget_recipe,
    materialize_reviewed_retarget_recipe,
    recipe_key_for_plan,
    revalidate_materialized_retarget_recipe,
    save_retarget_recipe,
    validate_retarget_recipe,
)


@dataclass(frozen=True)
class Node:
    name: str
    parent_name: str | None
    bind_position: tuple[float, float, float] = (0.0, 0.0, 0.0)
    endpoint_likelihood: float = 0.0


@dataclass(frozen=True)
class Candidate:
    bone_name: str
    confidence: float = 0.95
    confidence_margin: float = 0.25
    side: str = ""
    evidence: tuple[str, ...] = ("semantic_role", "topology")
    ambiguous: bool = False


@dataclass(frozen=True)
class Analysis:
    skeleton_hash: str
    bind_hash: str
    nodes: tuple[Node, ...]
    semantic_roles: dict[str, Candidate]
    animated_bones: frozenset[str] = frozenset()
    unresolved_animated_chains: tuple[str, ...] = ()
    semantic_chains: object = None
    archetype: str = "humanoid"
    archetype_confidence: float = 1.0
    animation_domain: str = "full_body"
    analyzer_version: str = "test-analyzer-v1"
    semantic_lexicon_version: str = "test-lexicon-v1"
    source_family_hints: tuple[str, ...] = ()
    source_name_languages_or_scripts: tuple[str, ...] = ()
    findings: tuple[object, ...] = ()
    animated_components: object = None
    selected_animation_stack: str = "Take 001"

    def to_dict(self) -> dict[str, object]:
        return {"semantic_lexicon_version": self.semantic_lexicon_version}


def test_source_analysis_is_scoped_to_document_and_selected_stack(
    monkeypatch: pytest.MonkeyPatch,
) -> None:
    calls: list[str] = []

    def analyze(source, *, animation_stack=None):
        calls.append(str(animation_stack or ""))
        return object()

    monkeypatch.setattr(skeleton_analysis, "analyze_source_skeleton", analyze)
    source = SimpleNamespace(
        selected_animation_stack=SimpleNamespace(name="Walk"),
    )

    walk_first = resolve_source_analysis(source)
    walk_second = resolve_source_analysis(source)
    source.selected_animation_stack = SimpleNamespace(name="Run")
    run = resolve_source_analysis(source)
    other_document = SimpleNamespace(
        selected_animation_stack=SimpleNamespace(name="Run"),
    )
    other = resolve_source_analysis(other_document)

    assert walk_first is walk_second
    assert run is not walk_first
    assert other is not run
    assert calls == ["Walk", "Run", "Run"]


def _rig(names: tuple[str, ...], parents: tuple[int, ...] | None = None) -> ChromeRig:
    parents = parents or tuple([-1, *range(len(names) - 1)])
    bones = tuple(
        ChromeRigBone(
            index=index,
            name=name,
            parent_index=parents[index],
            descriptor=1000 + index,
            bind_translation=(0.0, float(index), 0.0),
            bind_rotation_wxyz=(1.0, 0.0, 0.0, 0.0),
            tags=("body",),
        )
        for index, name in enumerate(names)
    )
    return ChromeRig("test:target", "Test target", "humanoid", bones, 0)


def _policy(
    rig: ChromeRig,
    roles: dict[str, str],
    *,
    archetype: str = "humanoid",
    semantic_chains: object = None,
) -> SimpleNamespace:
    rows = tuple(
        SimpleNamespace(
            target_bone=bone.name,
            target_category="body",
            semantic_role=roles.get(bone.name, ""),
            helper=False,
        )
        for bone in rig.bones
    )
    return SimpleNamespace(
        policy_id="test-policy-v1",
        policy_version="test-semantic-policy-v1",
        target_archetype=archetype,
        minimum_confidence=0.70,
        minimum_confidence_margin=0.08,
        bones=rows,
        semantic_chains=semantic_chains or {},
    )


def _analysis_for_roles(
    roles: dict[str, str],
    *,
    missing: frozenset[str] = frozenset(),
    ambiguous: frozenset[str] = frozenset(),
    archetype: str = "humanoid",
    animation_domain: str = "full_body",
) -> Analysis:
    parent: str | None = None
    nodes: list[Node] = []
    candidates: dict[str, Candidate] = {}
    animated: set[str] = set()
    for index, role in enumerate(dict.fromkeys(roles.values())):
        if not role or role in missing:
            continue
        name = f"源骨_{index:02d}"
        nodes.append(Node(name, parent))
        parent = name
        side = "left" if role.startswith(("left_", "l_")) else (
            "right" if role.startswith(("right_", "r_")) else ""
        )
        candidates[role] = Candidate(
            name,
            confidence_margin=0.01 if role in ambiguous else 0.25,
            side=side,
            ambiguous=role in ambiguous,
        )
        animated.add(name)
    return Analysis(
        skeleton_hash="source-skeleton-v1",
        bind_hash="source-bind-v1",
        nodes=tuple(nodes),
        semantic_roles=candidates,
        animated_bones=frozenset(animated),
        semantic_chains={},
        archetype=archetype,
        animation_domain=animation_domain,
    )


def test_plan_hash_tracks_semantic_policy_independently_of_wrapper_context() -> None:
    rig = _rig(("pelvis", "l_upperarm", "r_upperarm"), (-1, 0, 0))
    roles = {
        "pelvis": "pelvis",
        "l_upperarm": "left_upper_arm",
        "r_upperarm": "right_upper_arm",
    }
    policy = _policy(rig, roles)
    analysis = _analysis_for_roles(roles)

    preserved = build_automatic_retarget_plan(
        analysis,
        rig,
        policy,
        bilateral_semantic_policy="preserve_source_names",
    )
    automatic = build_automatic_retarget_plan(
        analysis,
        rig,
        policy,
        bilateral_semantic_policy="auto",
    )
    explicit = build_automatic_retarget_plan(
        analysis,
        rig,
        policy,
        bilateral_semantic_policy="swap_bilateral_explicit",
    )

    assert preserved.source_wrapper_mirror == automatic.source_wrapper_mirror == {}
    assert len({preserved.plan_hash, automatic.plan_hash, explicit.plan_hash}) == 3
    assert preserved.bilateral_semantic_policy == "preserve_source_names"
    assert automatic.bilateral_semantic_policy == "auto"
    assert explicit.bilateral_semantic_policy == "swap_bilateral_explicit"


def test_exact_name_and_ancestry_identity_maps_every_target() -> None:
    rig = _rig(("root", "pelvis", "spine"))
    analysis = Analysis(
        skeleton_hash="same-structure",
        bind_hash="same-bind",
        nodes=(Node("root", None), Node("pelvis", "root"), Node("spine", "pelvis")),
        semantic_roles={},
        animated_bones=frozenset({"pelvis", "spine"}),
        semantic_chains={},
    )
    policy = _policy(rig, {})

    plan = build_automatic_retarget_plan(analysis, rig, policy)

    assert plan.exact_identity
    assert len(plan.decisions) == len(rig.bones)
    assert {row.mode for row in plan.decisions} == {"direct"}
    assert validate_automatic_retarget_plan(plan, analysis, rig, policy).ok


def test_exact_identity_consumes_nonsemantic_animated_source_bones() -> None:
    rig = _rig(("root", "pelvis", "mystery_driver"))
    analysis = Analysis(
        skeleton_hash="same-structure",
        bind_hash="same-bind",
        nodes=(
            Node("root", None),
            Node("pelvis", "root"),
            Node("mystery_driver", "pelvis"),
        ),
        semantic_roles={},
        animated_bones=frozenset({"mystery_driver"}),
        unresolved_animated_chains=("mystery_driver",),
        semantic_chains={},
    )
    policy = _policy(rig, {})

    plan = build_automatic_retarget_plan(analysis, rig, policy)

    assert plan.exact_identity
    assert plan.unresolved_animated_chains == ()
    assert classify_retarget_readiness(plan).ready
    assert validate_automatic_retarget_plan(plan, analysis, rig, policy).ok


def test_dl1_eye_helpers_are_ignored_without_blocking_export() -> None:
    rig = _rig(("root", "pelvis", "spine"))
    roles = {"pelvis": "pelvis", "spine": "spine_1"}
    policy = _policy(rig, roles)
    base = _analysis_for_roles(roles)
    eye_helpers = ("l_eye_pos", "r_eye", "r_eye_pos")
    analysis = replace(
        base,
        nodes=base.nodes
        + tuple(Node(name, base.nodes[-1].name) for name in eye_helpers),
        animated_bones=frozenset((*base.animated_bones, *eye_helpers)),
        unresolved_animated_chains=eye_helpers,
    )

    plan = build_automatic_retarget_plan(analysis, rig, policy)
    validation = validate_automatic_retarget_plan(plan, analysis, rig, policy)

    assert validation.ok
    assert plan.unresolved_required_roles == ()
    assert set(eye_helpers).issubset(plan.ignored_animated_source_bones)
    assert validation.certificate["ignored_animated_source_count"] == len(
        plan.ignored_animated_source_bones
    )
    assert classify_retarget_readiness(plan).ready


def test_stale_manual_source_assignments_fall_back_to_bind() -> None:
    rig = _rig(("root", "pelvis", "spine"))
    roles = {"pelvis": "pelvis", "spine": "spine_1"}
    policy = _policy(rig, roles)
    analysis = _analysis_for_roles(roles)

    plan = build_automatic_retarget_plan(
        analysis,
        rig,
        policy,
        role_overrides={
            "spine_1": {"mode": "direct", "source_bone": "deleted_spine"}
        },
        target_bone_overrides={
            "pelvis": {"mode": "direct", "source_bone": "deleted_pelvis"},
            "deleted_target": {"mode": "direct", "source_bone": "deleted_source"},
        },
    )
    by_target = {row.target_bone: row for row in plan.decisions}

    assert by_target["pelvis"].mode == "inherit_bind"
    assert by_target["spine"].mode == "inherit_bind"
    assert not by_target["pelvis"].source_bones
    assert not by_target["spine"].source_bones
    assert validate_automatic_retarget_plan(plan, analysis, rig, policy).ok
    assert any("deleted_target" in warning for warning in plan.warnings_shown_to_user)


def test_missing_optional_limbs_quietly_inherit_bind() -> None:
    names = (
        "root",
        "pelvis",
        "l_thigh",
        "l_calf",
        "l_foot",
        "l_toe",
        "l_clavicle",
        "l_upperarm",
        "l_forearm",
        "l_hand",
    )
    roles = {
        "pelvis": "pelvis",
        "l_thigh": "left_thigh",
        "l_calf": "left_calf",
        "l_foot": "left_foot",
        "l_toe": "left_toe",
        "l_clavicle": "left_clavicle",
        "l_upperarm": "left_upper_arm",
        "l_forearm": "left_forearm",
        "l_hand": "left_hand",
    }
    rig = _rig(names)
    policy = _policy(rig, roles)
    optional = frozenset(
        {"left_foot", "left_toe", "left_clavicle", "left_hand"}
    )
    analysis = _analysis_for_roles(roles, missing=optional)

    plan = build_automatic_retarget_plan(analysis, rig, policy)
    validation = validate_automatic_retarget_plan(plan, analysis, rig, policy)

    by_role = {row.semantic_role: row for row in plan.decisions if row.semantic_role}
    assert all(by_role[role].mode == "inherit_bind" for role in optional)
    assert not plan.unresolved_required_roles
    assert plan.warnings_shown_to_user == ()
    assert validation.ok
    readiness = classify_retarget_readiness(plan)
    assert readiness.ready
    assert "partial skeleton" in readiness.label.lower()


def test_ambiguous_animated_roles_use_nonblocking_bind_fallbacks() -> None:
    rig = _rig(("root", "upperarm", "foot"))
    roles = {"upperarm": "left_upper_arm", "foot": "left_foot"}
    policy = _policy(rig, roles)
    analysis = _analysis_for_roles(
        roles,
        ambiguous=frozenset({"left_upper_arm", "left_foot"}),
    )

    plan = build_automatic_retarget_plan(analysis, rig, policy)
    by_role = {row.semantic_role: row for row in plan.decisions if row.semantic_role}

    assert by_role["left_upper_arm"].mode == "inherit_bind"
    assert by_role["left_foot"].mode == "inherit_bind"
    assert not plan.unresolved_required_roles
    assert validate_automatic_retarget_plan(plan, analysis, rig, policy).ok


def test_unknown_nonhumanoid_source_exports_with_bind_fallbacks() -> None:
    rig = _rig(("root", "pelvis", "upperarm"))
    roles = {"pelvis": "pelvis", "upperarm": "left_upper_arm"}
    policy = _policy(rig, roles)
    analysis = _analysis_for_roles(roles, archetype="generic")

    plan = build_automatic_retarget_plan(analysis, rig, policy)

    assert all(row.mode != "manual_required" for row in plan.decisions)
    assert any(row.mode in {"inherit_bind", "static_bind"} for row in plan.decisions)
    assert validate_automatic_retarget_plan(plan, analysis, rig, policy).ok


@pytest.mark.parametrize(
    ("source_names", "target_names", "short_policy", "expected"),
    [
        (("a", "b", "c"), ("t0", "t1"), "inherit_bind", {"composed", "direct"}),
        (("a",), ("t0", "t1", "t2"), "distributed", {"direct", "distributed"}),
    ],
)
def test_explicit_semantic_chain_policy_supports_composed_and_distributed_modes(
    source_names: tuple[str, ...],
    target_names: tuple[str, ...],
    short_policy: str,
    expected: set[str],
) -> None:
    rig = _rig(target_names)
    source_chain = SimpleNamespace(name="chain", bone_names=source_names)
    target_chain = SimpleNamespace(
        chain_id="chain",
        target_bones=target_names,
        force_chain_alignment=True,
        short_source_policy=short_policy,
    )
    policy = _policy(
        rig,
        {},
        archetype="generic",
        semantic_chains={"chain": target_chain},
    )
    nodes = tuple(
        Node(name, source_names[index - 1] if index else None)
        for index, name in enumerate(source_names)
    )
    analysis = Analysis(
        "chain-source",
        "chain-bind",
        nodes,
        {},
        semantic_chains={"chain": source_chain},
        archetype="generic",
        animation_domain="mostly_static_pose",
    )

    plan = build_automatic_retarget_plan(analysis, rig, policy)

    assert {row.mode for row in plan.decisions} == expected
    assert validate_automatic_retarget_plan(plan, analysis, rig, policy).ok


def test_recipe_round_trip_and_live_revalidation(tmp_path) -> None:
    rig = _rig(("root", "pelvis", "spine"))
    roles = {"pelvis": "pelvis", "spine": "spine_1"}
    policy = _policy(rig, roles)
    analysis = _analysis_for_roles(roles)
    plan = build_automatic_retarget_plan(analysis, rig, policy)
    recipe = build_retarget_recipe(plan)
    path = save_retarget_recipe(recipe, tmp_path / "unicode-recipe.json")

    loaded = load_retarget_recipe(path)
    validation = validate_retarget_recipe(loaded, analysis, rig, policy)

    assert loaded == RetargetRecipe.from_dict(recipe.to_dict())
    assert validation.ok and validation.live_revalidated
    assert apply_retarget_recipe(loaded, analysis, rig, policy).decisions == plan.decisions

    changed_bind = replace(analysis, bind_hash="changed-bind")
    rejected = validate_retarget_recipe(loaded, changed_bind, rig, policy)
    assert not rejected.ok
    assert any("source_bind_hash" in error for error in rejected.errors)
    with pytest.raises(ValueError, match="verification failed"):
        apply_retarget_recipe(loaded, changed_bind, rig, policy)

    drifted_roles = dict(analysis.semantic_roles)
    drifted_roles["spine_1"] = replace(
        drifted_roles["spine_1"], confidence=0.50
    )
    drifted_analysis = replace(analysis, semantic_roles=drifted_roles)
    automatic_rejected = validate_retarget_recipe(
        loaded, drifted_analysis, rig, policy
    )
    assert not automatic_rejected.ok
    assert "live structural mapping decisions changed" in automatic_rejected.errors


def test_reviewed_recipe_store_reuses_correction_for_same_skeleton_new_clip(
    tmp_path,
) -> None:
    rig = _rig(("root", "pelvis", "spine"))
    roles = {"pelvis": "pelvis", "spine": "spine_1"}
    policy = _policy(rig, roles)
    analysis = _analysis_for_roles(roles)
    baseline = build_automatic_retarget_plan(analysis, rig, policy)
    reviewed_rows = tuple(
        replace(
            row,
            mode="inherit_bind",
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
            reason="reviewed correction",
        )
        if row.target_bone == "spine"
        else row
        for row in baseline.decisions
    )

    with pytest.raises(ValueError, match="explicit reviewed provenance"):
        build_retarget_recipe(baseline, decisions=reviewed_rows)

    recipe = build_retarget_recipe(
        baseline,
        decisions=reviewed_rows,
        created_by="manual_reviewed",
    )
    store = RetargetRecipeStore(tmp_path / "recipes")
    store.save(recipe)

    baseline_spine = next(
        row for row in baseline.decisions if row.target_bone == "spine"
    )
    spine_source = baseline_spine.source_bones[0]
    second_analysis = replace(
        analysis,
        selected_animation_stack="Take 002",
        animation_domain="mostly_static_pose",
        animated_bones=frozenset(
            name for name in analysis.animated_bones if name != spine_source
        ),
    )
    fresh_second = build_automatic_retarget_plan(
        second_analysis, rig, policy
    )
    assert fresh_second.source_animation_hash != baseline.source_animation_hash
    assert recipe_key_for_plan(fresh_second) == recipe.key

    loaded = store.load(recipe_key_for_plan(fresh_second))
    assert loaded is not None
    validation = validate_retarget_recipe(
        loaded, second_analysis, rig, policy
    )
    reapplied = apply_retarget_recipe(loaded, second_analysis, rig, policy)
    automatic_reapplied = build_retarget_plan_with_local_recipe(
        second_analysis,
        rig,
        policy,
        store=store,
    )
    reviewed_profile = materialize_reviewed_retarget_recipe(
        loaded,
        second_analysis,
        rig,
        policy,
    )
    reapplied_spine = next(
        row for row in reapplied.decisions if row.target_bone == "spine"
    )
    live_spine = next(
        row for row in fresh_second.decisions if row.target_bone == "spine"
    )
    assert validation.ok
    assert validation.warnings
    assert reapplied.source_animation_hash == fresh_second.source_animation_hash
    assert reapplied_spine.mode == "inherit_bind"
    assert reapplied_spine.source_bones == ()
    assert reapplied_spine.animated == live_spine.animated is False
    assert reapplied_spine.reason == "reviewed correction"
    assert automatic_reapplied.decisions == reapplied.decisions
    assert mapping_profile_origin(reviewed_profile) == "manually_reviewed"
    assert reviewed_profile.extensions["local_retarget_recipe"][
        "recipe_id"
    ] == recipe.recipe_id
    # Cache-independent validation remains valid even when no store is passed.
    assert revalidate_materialized_retarget_recipe(
        reviewed_profile,
        second_analysis,
        rig,
        policy,
    ).ok
    assert not revalidate_materialized_retarget_recipe(
        reviewed_profile,
        replace(second_analysis, bind_hash="source-bind-v2"),
        rig,
        policy,
    ).ok
    changed_hierarchy = replace(
        second_analysis,
        skeleton_hash="source-skeleton-v2",
        nodes=tuple(
            replace(node, parent_name=None)
            if index == len(second_analysis.nodes) - 1
            else node
            for index, node in enumerate(second_analysis.nodes)
        ),
    )
    assert not revalidate_materialized_retarget_recipe(
        reviewed_profile,
        changed_hierarchy,
        rig,
        policy,
    ).ok
    live_changed_policy = SimpleNamespace(**vars(policy))
    live_changed_policy.policy_id = "test-policy-v2"
    assert not revalidate_materialized_retarget_recipe(
        reviewed_profile,
        second_analysis,
        rig,
        live_changed_policy,
    ).ok
    assert reapplied.unresolved_animated_chains == ()
    assert reapplied.warnings_shown_to_user == ()

    changed_bind = replace(second_analysis, bind_hash="source-bind-v2")
    changed_plan = build_automatic_retarget_plan(changed_bind, rig, policy)
    assert recipe_key_for_plan(changed_plan) != recipe.key
    assert store.load(recipe_key_for_plan(changed_plan)) is None
    rejected = validate_retarget_recipe(loaded, changed_bind, rig, policy)
    assert not rejected.ok
    assert any("source_bind_hash" in error for error in rejected.errors)
    changed_bind_result = build_retarget_plan_with_local_recipe(
        changed_bind,
        rig,
        policy,
        store=store,
    )
    changed_bind_spine = next(
        row for row in changed_bind_result.decisions if row.target_bone == "spine"
    )
    assert changed_bind_spine.mode == "direct"

    changed_rig = _rig(("root", "pelvis", "spine"))
    changed_rig.rig_id = "test:other-target"
    changed_target_policy = _policy(changed_rig, roles)
    changed_target_result = build_retarget_plan_with_local_recipe(
        second_analysis,
        changed_rig,
        changed_target_policy,
        store=store,
    )
    assert next(
        row
        for row in changed_target_result.decisions
        if row.target_bone == "spine"
    ).mode == "direct"

    changed_policy = SimpleNamespace(**vars(policy))
    changed_policy.policy_id = "test-policy-v2"
    changed_policy_result = build_retarget_plan_with_local_recipe(
        second_analysis,
        rig,
        changed_policy,
        store=store,
    )
    assert next(
        row
        for row in changed_policy_result.decisions
        if row.target_bone == "spine"
    ).mode == "direct"

    wrong_identity = tuple(
        replace(row, target_descriptor=row.target_descriptor + 1)
        if row.target_bone == "spine"
        else row
        for row in reviewed_rows
    )
    with pytest.raises(ValueError, match="target identity"):
        build_retarget_recipe(
            baseline,
            decisions=wrong_identity,
            created_by="manual_reviewed",
        )


def test_recipe_replay_ignores_newly_animated_unmapped_source_chain() -> None:
    rig = _rig(("root", "pelvis", "spine"))
    roles = {"pelvis": "pelvis", "spine": "spine_1"}
    policy = _policy(rig, roles)
    analysis = _analysis_for_roles(roles)
    baseline_analysis = replace(
        analysis,
        nodes=(
            *analysis.nodes,
            Node("mystery_driver", analysis.nodes[-1].name),
        ),
    )
    baseline = build_automatic_retarget_plan(
        baseline_analysis, rig, policy
    )
    recipe = build_retarget_recipe(baseline)

    next_clip = replace(
        baseline_analysis,
        selected_animation_stack="Take 002",
        animated_bones=frozenset(
            {*baseline_analysis.animated_bones, "mystery_driver"}
        ),
        unresolved_animated_chains=("mystery_driver",),
    )
    fresh = build_automatic_retarget_plan(next_clip, rig, policy)

    assert fresh.source_animation_hash != baseline.source_animation_hash
    assert recipe_key_for_plan(fresh) == recipe.key
    assert fresh.unresolved_animated_chains == ("mystery_driver",)
    assert fresh.ignored_animated_source_bones == ("mystery_driver",)
    assert classify_retarget_readiness(fresh).ready
    fresh_validation = validate_automatic_retarget_plan(
        fresh, next_clip, rig, policy
    )
    assert fresh_validation.ok
    assert any(
        "ignored unmapped animated source chains" in warning
        for warning in fresh_validation.warnings
    )

    validation = validate_retarget_recipe(
        recipe, next_clip, rig, policy
    )

    assert validation.ok
    assert validation.fresh_plan is not None
    assert validation.fresh_plan.unresolved_animated_chains == (
        "mystery_driver",
    )
    assert classify_retarget_readiness(validation.fresh_plan).ready
    assert apply_retarget_recipe(recipe, next_clip, rig, policy).source_animation_hash == (
        fresh.source_animation_hash
    )


def test_reviewed_recipe_can_resolve_newly_animated_unknown_source(
    tmp_path,
) -> None:
    rig = _rig(("root", "pelvis", "spine"))
    roles = {"pelvis": "pelvis", "spine": "spine_1"}
    policy = _policy(rig, roles)
    analysis = _analysis_for_roles(roles)
    baseline_analysis = replace(
        analysis,
        nodes=(
            *analysis.nodes,
            Node("mystery_driver", analysis.nodes[-1].name),
        ),
    )
    baseline = build_automatic_retarget_plan(
        baseline_analysis, rig, policy
    )
    reviewed_rows = tuple(
        replace(
            row,
            mode="direct",
            source_bones=("mystery_driver",),
            confidence=1.0,
            confidence_margin=1.0,
            evidence=(
                MappingEvidence(
                    "manual_review",
                    1.0,
                    "reviewer assigned the unknown animated driver",
                    "reviewer",
                ),
            ),
            reason="reviewed mystery-driver correction",
        )
        if row.target_bone == "spine"
        else row
        for row in baseline.decisions
    )
    recipe = build_retarget_recipe(
        baseline,
        decisions=reviewed_rows,
        created_by="manual_reviewed",
    )
    store = RetargetRecipeStore(tmp_path / "recipes")
    store.save(recipe)
    next_clip = replace(
        baseline_analysis,
        selected_animation_stack="Take 002",
        animated_bones=frozenset(
            {*baseline_analysis.animated_bones, "mystery_driver"}
        ),
        unresolved_animated_chains=("mystery_driver",),
    )

    fresh = build_automatic_retarget_plan(next_clip, rig, policy)
    applied = build_retarget_plan_with_local_recipe(
        next_clip,
        rig,
        policy,
        store=store,
    )
    applied_spine = next(
        row for row in applied.decisions if row.target_bone == "spine"
    )

    assert fresh.source_animation_hash != baseline.source_animation_hash
    assert fresh.unresolved_animated_chains == ("mystery_driver",)
    assert applied_spine.source_bones == ("mystery_driver",)
    assert applied.unresolved_animated_chains == ()
    assert validate_automatic_retarget_plan(
        applied, next_clip, rig, policy
    ).ok
    assert classify_retarget_readiness(applied).ready


def test_profile_recipe_export_requires_reviewed_provenance() -> None:
    rig = _rig(("root", "pelvis", "spine"))
    roles = {"pelvis": "pelvis", "spine": "spine_1"}
    policy = _policy(rig, roles)
    analysis = _analysis_for_roles(roles)
    fresh = build_automatic_retarget_plan(analysis, rig, policy)
    pairs = tuple(
        SimpleNamespace(
            target_rig_bone=row.target_bone,
            target_rig_descriptor=row.target_descriptor,
            source_fbx_bone=row.source_bones[0],
            transfer_policy="global_bind_basis",
            component_policy="rotation",
            extensions={},
        )
        for row in fresh.decisions
        if row.source_bones
    )
    unreviewed = SimpleNamespace(
        extensions={"origin": "automatic_verified"},
        pairs=pairs,
    )

    with pytest.raises(ValueError, match="explicitly reviewed"):
        build_reviewed_retarget_recipe_from_profile(
            fresh, unreviewed, analysis, rig, policy
        )

    first = pairs[0]
    fresh_row = next(
        row
        for row in fresh.decisions
        if row.target_bone == first.target_rig_bone
    )
    unsafe_serialized_pair = SimpleNamespace(
        **{
            **vars(first),
            "transfer_policy": "copy_local",
            "component_policy": "full_transform",
            "extensions": {
                "automatic_retarget_decision": fresh_row.to_dict()
            },
        }
    )
    unsafe = SimpleNamespace(
        extensions={"origin": "manually_reviewed"},
        pairs=(unsafe_serialized_pair, *pairs[1:]),
    )
    with pytest.raises(ValueError, match="rotation-only"):
        build_reviewed_retarget_recipe_from_profile(
            fresh, unsafe, analysis, rig, policy
        )

    reviewed = SimpleNamespace(
        extensions={"origin": "manually_reviewed"},
        pairs=pairs,
    )
    recipe = build_reviewed_retarget_recipe_from_profile(
        fresh, reviewed, analysis, rig, policy
    )

    assert recipe.created_by == "manual_reviewed"


def test_recipe_accepts_ambiguous_rows_as_bind_fallbacks() -> None:
    rig = _rig(("root", "upperarm"))
    roles = {"upperarm": "left_upper_arm"}
    policy = _policy(rig, roles)
    analysis = _analysis_for_roles(
        roles, ambiguous=frozenset({"left_upper_arm"})
    )
    plan = build_automatic_retarget_plan(analysis, rig, policy)

    assert any(isinstance(row, MappingDecision) for row in plan.decisions)
    legacy_rows = tuple(
        replace(
            row,
            mode="manual_required",
            source_bones=("源骨_00",),
        )
        if row.target_bone == "upperarm"
        else row
        for row in plan.decisions
    )
    legacy_plan = replace(plan, decisions=legacy_rows)
    recipe = build_retarget_recipe(legacy_plan)
    assert all(row.mode != "manual_required" for row in recipe.decisions)
    assert next(
        row for row in recipe.decisions if row.target_bone == "upperarm"
    ).mode == "inherit_bind"


def test_reflected_wrapper_preserves_automatic_bilateral_rows_by_default() -> None:
    context = BlenderLateralMirrorContext(
        "Armature",
        ((-1.0, 0.0, 0.0, 0.0), (0.0, 1.0, 0.0, 0.0), (0.0, 0.0, 1.0, 0.0), (0.0, 0.0, 0.0, 1.0)),
    )
    automatic = MappingDecision(
        "l_upperarm", 1, "body", "direct", ("l_upperarm",), animated=False
    )
    suffix_pair = MappingDecision(
        "player_collar_bn_l", 2, "collar", "direct", ("player_collar_bn_l",)
    )
    manual = MappingDecision(
        "r_upperarm",
        3,
        "body",
        "direct",
        ("r_upperarm",),
        evidence=(MappingEvidence("manual_target_override", 1.0),),
    )

    rows, provenance = _mirror_automatic_decisions(
        (automatic, suffix_pair, manual),
        source_names=(
            "l_upperarm", "r_upperarm", "player_collar_bn_l", "player_collar_bn_r"
        ),
        animated_bones={"r_upperarm"},
        context=context,
    )

    assert rows[0].source_bones == ("l_upperarm",)
    assert not rows[0].animated
    assert rows[1].source_bones == ("player_collar_bn_l",)
    assert rows[2].source_bones == ("r_upperarm",)
    assert provenance["bilateral_swapped_row_count"] == 0
    assert not provenance["bilateral_swap_applied"]


def test_explicit_bilateral_swap_changes_only_automatic_bilateral_rows() -> None:
    context = BlenderLateralMirrorContext(
        "Armature",
        ((-1.0, 0.0, 0.0, 0.0), (0.0, 1.0, 0.0, 0.0), (0.0, 0.0, 1.0, 0.0), (0.0, 0.0, 0.0, 1.0)),
    )
    automatic = MappingDecision(
        "l_upperarm", 1, "body", "direct", ("l_upperarm",), animated=False
    )
    suffix_pair = MappingDecision(
        "player_collar_bn_l", 2, "collar", "direct", ("player_collar_bn_l",)
    )
    manual = MappingDecision(
        "r_upperarm",
        3,
        "body",
        "direct",
        ("r_upperarm",),
        evidence=(MappingEvidence("manual_target_override", 1.0),),
    )

    rows, provenance = _mirror_automatic_decisions(
        (automatic, suffix_pair, manual),
        source_names=(
            "l_upperarm", "r_upperarm", "player_collar_bn_l", "player_collar_bn_r"
        ),
        animated_bones={"r_upperarm"},
        context=context,
        bilateral_semantic_policy="swap_bilateral_explicit",
    )

    assert rows[0].source_bones == ("r_upperarm",)
    assert rows[0].animated
    assert rows[1].source_bones == ("player_collar_bn_r",)
    assert rows[2].source_bones == ("r_upperarm",)
    assert provenance["bilateral_swapped_row_count"] == 2
    assert provenance["bilateral_swap_applied"]


def test_asymmetric_motion_stays_named_by_default_and_crosses_only_explicitly() -> None:
    rows = (
        MappingDecision(
            "l_upperarm",
            1,
            "body",
            "direct",
            ("l_upperarm",),
            animated=True,
        ),
        MappingDecision(
            "r_upperarm",
            2,
            "body",
            "direct",
            ("r_upperarm",),
            animated=False,
        ),
    )
    preserved, preserve_report = _mirror_automatic_decisions(
        rows,
        source_names=("l_upperarm", "r_upperarm"),
        animated_bones={"l_upperarm"},
        context=None,
    )
    assert preserved[0].source_bones == ("l_upperarm",)
    assert preserved[0].animated
    assert preserved[1].source_bones == ("r_upperarm",)
    assert not preserved[1].animated
    assert not preserve_report["bilateral_swap_applied"]

    swapped, swap_report = _mirror_automatic_decisions(
        rows,
        source_names=("l_upperarm", "r_upperarm"),
        animated_bones={"l_upperarm"},
        context=None,
        bilateral_semantic_policy="swap_bilateral_explicit",
    )
    assert swapped[0].source_bones == ("r_upperarm",)
    assert not swapped[0].animated
    assert swapped[1].source_bones == ("l_upperarm",)
    assert swapped[1].animated
    assert swap_report["bilateral_swap_applied"]
    assert swap_report["bilateral_swapped_row_count"] == 2
