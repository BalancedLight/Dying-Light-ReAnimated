from __future__ import annotations

from pathlib import Path
from types import SimpleNamespace

import pytest

from dlanm2_gui.animation_targets import RetargetUiKind, retarget_ui_kind
from dlanm2_gui.chrome_rig import ChromeRig
from dlanm2_gui.game_profiles import (
    DL2_ADVANCED_RIG_REF,
    DL2_GAME_ID,
    DL2_LEGACY_RIG_REF,
)
from dlanm2_gui.retarget_profiles import SourceBoneMappingProfile
from dlanm2_gui.retarget_routing import select_exact_solver
from dlanm2_gui.project_builder import _resolve_bundled_dl2_semantic_map
from dlanm2_gui.semantic_retarget import (
    compile_bundled_semantic_profile,
    prepare_bundled_semantic_state,
)
from dlanm2_gui.target_retarget_policy import build_target_retarget_policy
from dlanm2_gui.workspace_project import DlReanimatedProject, ProjectAnimation


ROOT = Path(__file__).resolve().parents[1]


def _candidate(name: str, role: str) -> SimpleNamespace:
    side = (
        "left"
        if role.startswith(("left_", "l_"))
        else "right"
        if role.startswith(("right_", "r_"))
        else ""
    )
    return SimpleNamespace(
        bone_name=name,
        confidence=0.99,
        confidence_margin=0.3,
        side=side,
        evidence=("semantic_role", "topology"),
        ambiguous=False,
    )


def _semantic_analysis(policy, *, extra_bones: tuple[str, ...] = ()) -> SimpleNamespace:
    roles = tuple(dict.fromkeys(slot.semantic_role for slot in policy.direct_slots))
    names = tuple(f"source_{role}" for role in roles) + extra_bones
    parents = {
        name: (names[index - 1] if index else None)
        for index, name in enumerate(names)
    }
    nodes = tuple(
        SimpleNamespace(
            name=name,
            parent_name=parents[name],
            bind_position=(0.0, float(index), 0.0),
            endpoint_likelihood=0.0,
            side_conflict=False,
        )
        for index, name in enumerate(names)
    )
    return SimpleNamespace(
        limb_models={name: index for index, name in enumerate(names)},
        parent_by_name=parents,
        skeleton_hash="synthetic-semantic-source-v1",
        name_parent_hash="synthetic-name-parent-v1",
        bind_hash="synthetic-bind-v1",
        animation_hash="synthetic-animation-v1",
        nodes=nodes,
        semantic_roles={role: _candidate(f"source_{role}", role) for role in roles},
        animated_bones=frozenset(f"source_{role}" for role in roles),
        semantic_chains={},
        unresolved_animated_chains=(),
        archetype="humanoid",
        archetype_confidence=1.0,
        animation_domain="full_body",
        analyzer_version="test-analyzer-v1",
        semantic_lexicon_version="test-lexicon-v1",
        source_family_hints=("synthetic",),
        source_name_languages_or_scripts=("latin",),
        findings=(),
        animated_components={},
        selected_animation_stack="Take 001",
    )


def _exact_analysis(rig: ChromeRig) -> SimpleNamespace:
    names = tuple(bone.name for bone in rig.bones)
    parents = {
        bone.name: (
            rig.bones[bone.parent_index].name if bone.parent_index >= 0 else None
        )
        for bone in rig.bones
    }
    nodes = tuple(
        SimpleNamespace(
            name=bone.name,
            parent_name=parents[bone.name],
            bind_position=bone.bind_translation,
            endpoint_likelihood=0.0,
            side_conflict=False,
        )
        for bone in rig.bones
    )
    return SimpleNamespace(
        limb_models={name: index for index, name in enumerate(names)},
        parent_by_name=parents,
        skeleton_hash="exact-source-identity",
        name_parent_hash="exact-source-name-parent",
        bind_hash="exact-source-bind",
        animation_hash="exact-source-animation",
        nodes=nodes,
        semantic_roles={},
        animated_bones=frozenset(names),
        semantic_chains={},
        unresolved_animated_chains=(),
        archetype="humanoid",
        archetype_confidence=1.0,
        animation_domain="full_body",
        analyzer_version="test-analyzer-v1",
        semantic_lexicon_version="test-lexicon-v1",
        source_family_hints=("bundled",),
        source_name_languages_or_scripts=("latin",),
        findings=(),
        animated_components={},
        selected_animation_stack="Take 001",
    )


@pytest.mark.parametrize(
    ("rig_name", "rig_ref", "expected_rows", "certificate_format"),
    (
        ("player_skeleton.crig", DL2_ADVANCED_RIG_REF, 271, "dl2_advanced_body_bridge_v1"),
        ("player_shadow_caster.crig", DL2_LEGACY_RIG_REF, 81, "dl2_legacy_body_bridge_v1"),
    ),
)
def test_bundled_dl2_targets_share_52_role_editor_and_verified_compile(
    rig_name: str,
    rig_ref: str,
    expected_rows: int,
    certificate_format: str,
) -> None:
    rig = ChromeRig.load(ROOT / "reference" / "dl2" / rig_name)
    policy = build_target_retarget_policy(rig, game_id=DL2_GAME_ID)
    source = _semantic_analysis(policy)

    state = prepare_bundled_semantic_state(source, rig, policy)
    compiled, live, plan = compile_bundled_semantic_profile(
        source, rig, policy, state.profile
    )

    assert rig.rig_id == rig_ref
    assert len(state.rows) == 52
    assert state.validation.ok
    assert len(compiled.pairs) == expected_rows
    assert sum(bool(pair.source_fbx_bone) for pair in compiled.pairs) == 52
    assert live.ok and live.live_revalidated
    assert live.certificate_format == certificate_format
    assert plan.target_policy_id == certificate_format


def test_dl1_eye_helper_animation_compiles_without_dl2_eye_mappings() -> None:
    rig = ChromeRig.load(ROOT / "reference" / "dl2" / "player_skeleton.crig")
    policy = build_target_retarget_policy(rig, game_id=DL2_GAME_ID)
    eye_helpers = ("l_eye_pos", "r_eye", "r_eye_pos")
    source = _semantic_analysis(policy, extra_bones=eye_helpers)
    source.animated_bones = frozenset((*source.animated_bones, *eye_helpers))
    source.unresolved_animated_chains = eye_helpers

    state = prepare_bundled_semantic_state(source, rig, policy)
    compiled, live, plan = compile_bundled_semantic_profile(
        source, rig, policy, state.profile
    )
    left_eye_target = next(
        row
        for row in compiled.pairs
        if row.target_rig_bone == "l_eyeballaimbase_bone"
    )

    assert state.validation.ok
    assert live.ok and live.live_revalidated
    assert set(eye_helpers).issubset(plan.ignored_animated_source_bones)
    assert left_eye_target.source_fbx_bone == ""
    assert left_eye_target.transfer_policy == "bind"


def test_manual_semantic_role_override_changes_compiled_pair_and_solver() -> None:
    rig = ChromeRig.load(ROOT / "reference" / "dl2" / "player_skeleton.crig")
    policy = build_target_retarget_policy(rig, game_id=DL2_GAME_ID)
    source = _semantic_analysis(policy, extra_bones=("alternate_pelvis",))
    state = prepare_bundled_semantic_state(source, rig, policy)
    profile = state.profile
    profile.set_mapping(
        "hips",
        "alternate_pelvis",
        confidence=1.0,
        method="manual_override",
        mode="direct",
    )

    compiled, live, _plan = compile_bundled_semantic_profile(
        source, rig, policy, profile
    )
    pelvis = next(pair for pair in compiled.pairs if pair.target_rig_bone == "pelvis")
    selection = select_exact_solver(
        {"classification": "compatible"},
        compiled,
        automatic_verification=live,
    )

    assert pelvis.source_fbx_bone == "alternate_pelvis"
    assert compiled.extensions["semantic_manual_override_count"] == 1
    assert selection.build_allowed
    assert selection.selected_engine == "MappedRigRetargetEngine"


def test_arbitrary_target_override_is_compiled_and_freshly_certified() -> None:
    rig = ChromeRig.load(ROOT / "reference" / "dl2" / "player_skeleton.crig")
    policy = build_target_retarget_policy(rig, game_id=DL2_GAME_ID)
    source = _semantic_analysis(policy, extra_bones=("source_sole",))
    state = prepare_bundled_semantic_state(source, rig, policy)
    state.profile.set_target_bone_override(
        "l_sole_helper",
        mode="direct",
        source_bone="source_sole",
        transfer_policy="rest_relative",
        component_policy="rotation_translation",
    )

    compiled, live, plan = compile_bundled_semantic_profile(
        source, rig, policy, state.profile
    )
    row = next(
        pair for pair in compiled.pairs if pair.target_rig_bone == "l_sole_helper"
    )
    assert row.source_fbx_bone == "source_sole"
    assert row.transfer_policy == "rest_relative"
    assert row.component_policy == "rotation_translation"
    assert row.review_state == "manually_reviewed"
    assert live.ok and live.live_revalidated
    assert live.plan_hash == plan.plan_hash
    assert live.certificate["semantic_target_override_count"] == 1
    assert live.certificate["validation_kind"] == "deterministic_semantic_profile_compilation"


def test_target_override_clears_matching_unresolved_source_before_validation() -> None:
    rig = ChromeRig.load(ROOT / "reference" / "dl2" / "player_skeleton.crig")
    policy = build_target_retarget_policy(rig, game_id=DL2_GAME_ID)
    source = _semantic_analysis(policy, extra_bones=("source_sole",))
    source.unresolved_animated_chains = ("source_sole",)
    source.animated_bones = frozenset((*source.animated_bones, "source_sole"))
    state = prepare_bundled_semantic_state(source, rig, policy)
    assert state.validation.ok
    assert "source_sole" in state.plan.ignored_animated_source_bones

    state.profile.set_target_bone_override(
        "l_sole_helper",
        mode="direct",
        source_bone="source_sole",
    )
    refreshed = prepare_bundled_semantic_state(
        source,
        rig,
        policy,
        state.profile,
    )

    assert refreshed.plan.unresolved_animated_chains == ()
    assert refreshed.validation.ok
    decision = next(
        row
        for row in refreshed.plan.decisions
        if row.target_bone == "l_sole_helper"
    )
    assert decision.source_bones == ("source_sole",)
    assert any(
        evidence.kind == "manual_target_override"
        for evidence in decision.evidence
    )
    compiled, live, _plan = compile_bundled_semantic_profile(
        source,
        rig,
        policy,
        refreshed.profile,
    )
    pair = next(
        row for row in compiled.pairs if row.target_rig_bone == "l_sole_helper"
    )
    assert pair.source_fbx_bone == "source_sole"
    assert live.ok and live.live_revalidated


def test_exact_identity_still_has_semantic_profile_and_exact_execution() -> None:
    rig = ChromeRig.load(ROOT / "reference" / "dl2" / "player_skeleton.crig")
    policy = build_target_retarget_policy(rig, game_id=DL2_GAME_ID)
    source = _exact_analysis(rig)

    state = prepare_bundled_semantic_state(source, rig, policy)
    compiled, live, plan = compile_bundled_semantic_profile(
        source, rig, policy, state.profile
    )
    selection = select_exact_solver(
        {"classification": "exact"},
        compiled,
        automatic_verification=live,
    )

    assert plan.exact_identity
    assert len(state.rows) == 52
    assert state.profile.target_policy_id == "dl2_advanced_body_bridge_v1"
    assert selection.build_allowed
    assert selection.selected_engine == "ExactRigRetargetEngine"


def test_editor_ownership_uses_target_origin_not_exact_solver_mode() -> None:
    project = DlReanimatedProject.new("Ownership")
    project.game_id = DL2_GAME_ID
    project.rig.target_rig_ref = DL2_ADVANCED_RIG_REF
    project.rig.retarget_mode = "exact"
    animation = ProjectAnimation.create("source.fbx")

    assert retarget_ui_kind(project, animation) == RetargetUiKind.BUILTIN_HUMANOID

    project.rig.target_rig_ref = "custom:user-rig"
    project.rig.target_rig_path = "user.crig"
    assert retarget_ui_kind(project, animation) == RetargetUiKind.CUSTOM_CRIG

    project.rig.target_rig_ref = DL2_ADVANCED_RIG_REF
    project.rig.extensions["expert_crig_mapping_override"] = {
        "deliberate": True,
        "expose_crig_mapping": True,
    }
    assert retarget_ui_kind(project, animation) == RetargetUiKind.CUSTOM_CRIG


def test_build_resolution_recompiles_semantic_source_of_truth_and_rejects_stale_cache() -> None:
    rig = ChromeRig.load(ROOT / "reference" / "dl2" / "player_skeleton.crig")
    policy = build_target_retarget_policy(rig, game_id=DL2_GAME_ID)
    source = _semantic_analysis(policy)
    project = DlReanimatedProject.new("Semantic build")
    project.game_id = DL2_GAME_ID
    project.rig.target_rig_ref = DL2_ADVANCED_RIG_REF
    animation = ProjectAnimation.create("synthetic.fbx")
    project.animations = [animation]

    first, first_live, _plan, profile = _resolve_bundled_dl2_semantic_map(
        project, animation, source, rig
    )
    stale_id = first.profile_id
    stale_hash = animation.extensions["compiled_target_map_hash"]
    source.animation_hash = "changed-live-animation"

    second, second_live, _plan, second_profile = _resolve_bundled_dl2_semantic_map(
        project, animation, source, rig
    )

    assert animation.mapping_profile_id == profile.profile_id == second_profile.profile_id
    assert project.mapping_profiles[animation.mapping_profile_id]["format"] == (
        "dl-reanimated-retarget-profile"
    )
    assert animation.extensions["compiled_target_map_profile_id"] == second.profile_id
    assert second.profile_id != stale_id
    assert animation.extensions["compiled_target_map_hash"] != stale_hash
    assert first_live.certificate["source_animation_hash"] == "synthetic-animation-v1"
    assert second_live.certificate["source_animation_hash"] == "changed-live-animation"
