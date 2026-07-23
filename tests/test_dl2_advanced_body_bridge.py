from __future__ import annotations

from copy import deepcopy
from dataclasses import replace
import hashlib
from pathlib import Path

import pytest

from dlanm2_gui.automatic_retarget import (
    DL2_ADVANCED_BODY_CERTIFICATE_FORMAT,
    build_automatic_retarget_plan,
    build_dl2_advanced_body_map_with_local_recipe,
    build_verified_dl2_advanced_body_map,
    revalidate_verified_dl2_advanced_body_map,
    validate_automatic_retarget_plan,
)
from dlanm2_gui.bone_maps import GenericBoneMap, mapping_profile_origin
from dlanm2_gui.chrome_rig import ChromeRig
from dlanm2_gui.fbx_core import FbxDocument
from dlanm2_gui.skeleton_analysis import SourceSkeletonAnalysis, analyze_source_skeleton
from dlanm2_gui.retarget_recipes import (
    RetargetRecipeStore,
    build_retarget_recipe,
    materialize_reviewed_retarget_recipe,
)
from dlanm2_gui.target_retarget_policy import (
    DL2_ADVANCED_BODY_DIRECT_SLOT_COUNT,
    TargetRetargetPolicy,
    build_target_retarget_policy,
)


ROOT = Path(__file__).resolve().parents[1]
ADVANCED_CRIG = ROOT / "reference" / "dl2" / "player_skeleton.crig"
PRIVATE_FBX = Path(r"S:\Downloads\Thriller Combined - Parts 1-4.fbx")
PRIVATE_FBX_SHA256 = (
    "c7d0041db80bbe63efff3934c01de9048f0bff61753ae280012b6a76a829d233"
)


@pytest.fixture(scope="module")
def advanced_rig() -> ChromeRig:
    return ChromeRig.load(ADVANCED_CRIG)


@pytest.fixture(scope="module")
def advanced_policy(advanced_rig: ChromeRig) -> TargetRetargetPolicy:
    policy = build_target_retarget_policy(advanced_rig, clip_domain="body")
    assert policy.automatic_routing_authorized, policy.coherence_errors
    return policy


@pytest.fixture(scope="module")
def mixamo_analysis() -> SourceSkeletonAnalysis:
    if not PRIVATE_FBX.is_file():
        pytest.skip("private Mixamo fixture is not available")
    digest = hashlib.sha256(PRIVATE_FBX.read_bytes()).hexdigest()
    if digest != PRIVATE_FBX_SHA256:
        pytest.skip("private Mixamo fixture hash differs from the reviewed input")
    return analyze_source_skeleton(FbxDocument(PRIVATE_FBX))


@pytest.fixture(scope="module")
def verified_profile(
    mixamo_analysis: SourceSkeletonAnalysis,
    advanced_rig: ChromeRig,
    advanced_policy: TargetRetargetPolicy,
) -> GenericBoneMap:
    return build_verified_dl2_advanced_body_map(
        mixamo_analysis, advanced_rig, advanced_policy
    )


def _pairs_by_target(profile: GenericBoneMap):
    return {row.target_rig_bone: row for row in profile.pairs}


def test_private_fixture_plan_is_complete_semantic_and_deterministic(
    mixamo_analysis: SourceSkeletonAnalysis,
    advanced_rig: ChromeRig,
    advanced_policy: TargetRetargetPolicy,
) -> None:
    plan = build_automatic_retarget_plan(
        mixamo_analysis, advanced_rig, advanced_policy
    )
    validation = validate_automatic_retarget_plan(
        plan, mixamo_analysis, advanced_rig, advanced_policy
    )

    assert validation.ok
    assert len(plan.decisions) == 271
    assert plan.mapping_modes["direct"] == DL2_ADVANCED_BODY_DIRECT_SLOT_COUNT == 52
    assert plan.mapping_modes["inherit_bind"] + plan.mapping_modes["static_bind"] == 219
    assert plan.mapping_modes["manual_required"] == 0
    assert plan.mapping_modes["composed"] == 0
    assert plan.mapping_modes["distributed"] == 0
    assert plan.warnings_shown_to_user == ()
    assert plan.observed_motion_domain == "full_body"
    assert not validation.live_revalidated


def test_verified_profile_materializes_exactly_52_body_rows_and_219_bind_rows(
    verified_profile: GenericBoneMap,
    advanced_policy: TargetRetargetPolicy,
) -> None:
    rows = _pairs_by_target(verified_profile)
    mapped = [row for row in verified_profile.pairs if row.source_fbx_bone]
    bind = [row for row in verified_profile.pairs if not row.source_fbx_bone]

    assert len(verified_profile.pairs) == 271
    assert len(rows) == 271
    assert len(mapped) == 52
    assert len(bind) == 219
    assert mapping_profile_origin(verified_profile) == "automatic_verified"
    assert all(row.transfer_policy == "global_bind_basis" for row in mapped)
    assert all(row.component_policy == "rotation" for row in mapped)
    assert all(row.review_state == "automatic_accepted" for row in mapped)
    assert all(row.transfer_policy == "bind" for row in bind)
    assert all(row.component_policy == "rotation" for row in bind)
    assert all(row.review_state == "intentionally_unmapped" for row in bind)
    assert not any("spatial" in row.method.casefold() for row in mapped)

    categories = {row.target_bone: row.target_category for row in advanced_policy.bones}
    assert not [row for row in mapped if categories[row.target_rig_bone] != "body"]


def test_reviewed_recipe_profile_preserves_dl2_execution_policies(
    mixamo_analysis: SourceSkeletonAnalysis,
    advanced_rig: ChromeRig,
    advanced_policy: TargetRetargetPolicy,
) -> None:
    plan = build_automatic_retarget_plan(
        mixamo_analysis, advanced_rig, advanced_policy
    )
    reviewed = tuple(
        replace(
            row,
            mode="inherit_bind",
            source_bones=(),
            confidence=1.0,
            confidence_margin=1.0,
            reason="reviewed correction",
        )
        if row.target_bone == "spine"
        else row
        for row in plan.decisions
    )
    recipe = build_retarget_recipe(
        plan,
        decisions=reviewed,
        created_by="manual_reviewed",
    )

    profile = materialize_reviewed_retarget_recipe(
        recipe,
        mixamo_analysis,
        advanced_rig,
        advanced_policy,
    )
    mapped = [row for row in profile.pairs if row.source_fbx_bone]
    bind = [row for row in profile.pairs if not row.source_fbx_bone]

    assert mapping_profile_origin(profile) == "manually_reviewed"
    assert all(row.component_policy == "rotation" for row in profile.pairs)
    assert all(row.transfer_policy == "global_bind_basis" for row in mapped)
    assert all(row.transfer_policy == "bind" for row in bind)
    provenance = profile.extensions["local_retarget_recipe"]
    assert provenance["recipe_id"] == recipe.recipe_id
    assert provenance["key_hash"] == recipe.key.key_hash
    assert provenance["decision_fingerprint"] == recipe.decision_fingerprint
    assert provenance["live_revalidated"] is True


def test_dl2_local_recipe_builder_separates_reviewed_and_verified_authority(
    tmp_path: Path,
    mixamo_analysis: SourceSkeletonAnalysis,
    advanced_rig: ChromeRig,
    advanced_policy: TargetRetargetPolicy,
) -> None:
    plan = build_automatic_retarget_plan(
        mixamo_analysis, advanced_rig, advanced_policy
    )
    reviewed_rows = tuple(
        replace(
            row,
            mode="inherit_bind",
            source_bones=(),
            confidence=1.0,
            confidence_margin=1.0,
            reason="reviewed correction",
        )
        if row.target_bone == "spine"
        else row
        for row in plan.decisions
    )
    recipe = build_retarget_recipe(
        plan,
        decisions=reviewed_rows,
        created_by="manual_reviewed",
    )
    reviewed_store = RetargetRecipeStore(tmp_path / "reviewed")
    reviewed_store.save(recipe)

    reviewed_profile = build_dl2_advanced_body_map_with_local_recipe(
        mixamo_analysis,
        advanced_rig,
        advanced_policy,
        recipe_store=reviewed_store,
    )

    assert len(reviewed_profile.pairs) == 271
    assert mapping_profile_origin(reviewed_profile) == "manually_reviewed"
    assert "local_retarget_recipe" in reviewed_profile.extensions
    assert "automatic_retarget_certificate" not in reviewed_profile.extensions

    empty_profile = build_dl2_advanced_body_map_with_local_recipe(
        mixamo_analysis,
        advanced_rig,
        advanced_policy,
        recipe_store=RetargetRecipeStore(tmp_path / "empty"),
    )
    assert mapping_profile_origin(empty_profile) == "automatic_verified"

    unreviewed_store = RetargetRecipeStore(tmp_path / "unreviewed")
    unreviewed_store.save(build_retarget_recipe(plan))
    unreviewed_profile = build_dl2_advanced_body_map_with_local_recipe(
        mixamo_analysis,
        advanced_rig,
        advanced_policy,
        recipe_store=unreviewed_store,
    )
    assert mapping_profile_origin(unreviewed_profile) == "automatic_verified"

    corrupt_store = RetargetRecipeStore(tmp_path / "corrupt")
    corrupt_path = corrupt_store.path_for_key(recipe.key)
    corrupt_path.parent.mkdir(parents=True, exist_ok=True)
    corrupt_path.write_text("not-json", encoding="utf-8")
    corrupt_profile = build_dl2_advanced_body_map_with_local_recipe(
        mixamo_analysis,
        advanced_rig,
        advanced_policy,
        recipe_store=corrupt_store,
    )
    assert mapping_profile_origin(corrupt_profile) == "automatic_verified"

    changed_bind = replace(mixamo_analysis, bind_hash="1" * 64)
    stale_profile = build_dl2_advanced_body_map_with_local_recipe(
        changed_bind,
        advanced_rig,
        advanced_policy,
        recipe_store=reviewed_store,
    )
    assert mapping_profile_origin(stale_profile) == "automatic_verified"


def test_spine_head_and_correct_shifted_finger_slots(
    verified_profile: GenericBoneMap,
) -> None:
    rows = _pairs_by_target(verified_profile)
    expected_core = {
        "spine": "mixamorig:Spine",
        "spine2": "mixamorig:Spine1",
        "spine3": "mixamorig:Spine2",
        "neck": "mixamorig:Neck",
        "head": "mixamorig:Head",
    }
    assert {
        target: rows[target].source_fbx_bone for target in expected_core
    } == expected_core

    for side, source_side in (("l", "Left"), ("r", "Right")):
        assert rows[f"{side}_finger01"].source_fbx_bone == f"mixamorig:{source_side}HandThumb1"
        assert rows[f"{side}_finger02"].source_fbx_bone == f"mixamorig:{source_side}HandThumb2"
        assert rows[f"{side}_finger03"].source_fbx_bone == f"mixamorig:{source_side}HandThumb3"
        for digit, name in ((1, "Index"), (2, "Middle"), (3, "Ring"), (4, "Pinky")):
            # DL2 x0 is a metacarpal/base row.  Source segment 1 begins at x1.
            assert rows[f"{side}_finger{digit}0"].source_fbx_bone == ""
            for segment in (1, 2, 3):
                assert rows[f"{side}_finger{digit}{segment}"].source_fbx_bone == (
                    f"mixamorig:{source_side}Hand{name}{segment}"
                )

    consumed = {row.source_fbx_bone for row in verified_profile.pairs if row.source_fbx_bone}
    assert not any(name.endswith("4") for name in consumed)
    assert "mixamorig:HeadTop_End" not in consumed


def test_known_legacy_spatial_filler_pairs_are_all_absent(
    verified_profile: GenericBoneMap,
) -> None:
    rows = _pairs_by_target(verified_profile)
    known_bad = {
        "l_leg_secanim_02_a": "mixamorig:LeftToe_End",
        "r_leg_secanim_02_a": "mixamorig:RightToe_End",
        "hspine1": "mixamorig:HeadTop_End",
        "r_foretwist": "mixamorig:RightHandIndex4",
        "r_foretwist1": "mixamorig:RightHandThumb4",
        "l_foretwist": "mixamorig:LeftHandIndex4",
        "l_foretwist1": "mixamorig:LeftHandThumb4",
        "l_ear_bone": "mixamorig:LeftHandPinky4",
        "l_headaddside1g_bone": "mixamorig:LeftHandMiddle4",
        "l_headaddside3d_bone": "mixamorig:LeftHandRing4",
        "r_ear_bone": "mixamorig:RightHandPinky4",
        "r_headaddside1g_bone": "mixamorig:RightHandMiddle4",
        "r_headaddside3d_bone": "mixamorig:RightHandRing4",
    }

    assert {
        target: rows[target].source_fbx_bone
        for target in known_bad
    } == {target: "" for target in known_bad}


def test_certificate_is_exhaustive_and_live_revalidated(
    verified_profile: GenericBoneMap,
    mixamo_analysis: SourceSkeletonAnalysis,
    advanced_rig: ChromeRig,
    advanced_policy: TargetRetargetPolicy,
) -> None:
    certificate = verified_profile.extensions["automatic_retarget_certificate"]
    assert certificate["format"] == DL2_ADVANCED_BODY_CERTIFICATE_FORMAT
    assert certificate["certificate_status"] == "pass"
    assert certificate["live_revalidated"] is True
    assert certificate["target_row_count"] == 271
    assert certificate["mapped_body_row_count"] == 52
    assert certificate["bind_row_count"] == 219
    assert certificate["mapping_modes"] == certificate["mapping_mode_counts"]
    assert certificate["spatial_only_row_count"] == 0
    assert certificate["mapped_non_body_target_count"] == 0
    assert certificate["source_endpoint_rows_consumed"] == []
    for field in (
        "source_skeleton_hash",
        "source_name_parent_hash",
        "source_bind_hash",
        "source_animation_hash",
        "target_skeleton_hash",
        "analyzer_version",
        "planner_version",
        "semantic_policy_version",
        "lexicon_version",
        "decision_fingerprint",
        "plan_hash",
    ):
        assert certificate[field]

    result = revalidate_verified_dl2_advanced_body_map(
        verified_profile, mixamo_analysis, advanced_rig, advanced_policy
    )
    assert result.ok and result.live_revalidated


def test_profile_json_round_trip_preserves_live_certificate(
    verified_profile: GenericBoneMap,
    mixamo_analysis: SourceSkeletonAnalysis,
    advanced_rig: ChromeRig,
    advanced_policy: TargetRetargetPolicy,
) -> None:
    loaded = GenericBoneMap.from_dict(verified_profile.to_dict())

    result = revalidate_verified_dl2_advanced_body_map(
        loaded, mixamo_analysis, advanced_rig, advanced_policy
    )

    assert mapping_profile_origin(loaded) == "automatic_verified"
    assert result.ok and result.live_revalidated


@pytest.mark.parametrize(
    "mutation",
    [
        "source_pair",
        "mapping_mode",
        "row_decision",
        "executable_source_bones",
        "distribution_weight",
        "semantic_chain_id",
        "certificate_version",
        "spatial_mapping",
        "facial_map",
    ],
)
def test_serialized_mapping_or_certificate_mutations_fail_closed(
    mutation: str,
    verified_profile: GenericBoneMap,
    mixamo_analysis: SourceSkeletonAnalysis,
    advanced_rig: ChromeRig,
    advanced_policy: TargetRetargetPolicy,
) -> None:
    changed = deepcopy(verified_profile)
    rows = _pairs_by_target(changed)
    if mutation == "source_pair":
        rows["pelvis"].source_fbx_bone = "mixamorig:Spine"
    elif mutation == "mapping_mode":
        rows["pelvis"].method = "automatic_verified:distributed"
    elif mutation == "row_decision":
        rows["pelvis"].extensions["mapping_mode"] = "composed"
    elif mutation == "executable_source_bones":
        rows["pelvis"].extensions["source_bones"] = [
            "mixamorig:Hips",
            "mixamorig:Spine",
        ]
    elif mutation == "distribution_weight":
        rows["pelvis"].extensions["distribution_weight"] = 0.5
    elif mutation == "semantic_chain_id":
        rows["pelvis"].extensions["semantic_chain_id"] = "forged-chain"
    elif mutation == "certificate_version":
        changed.extensions["automatic_retarget_certificate"]["planner_version"] = "stale"
    elif mutation == "spatial_mapping":
        rows["pelvis"].method = "spatial_bind"
    else:
        facial_target = next(
            row.target_bone
            for row in advanced_policy.bones
            if row.target_category == "facial"
        )
        rows[facial_target].source_fbx_bone = "mixamorig:Head"
        rows[facial_target].transfer_policy = "global_bind_basis"
        rows[facial_target].review_state = "automatic_accepted"

    result = revalidate_verified_dl2_advanced_body_map(
        changed, mixamo_analysis, advanced_rig, advanced_policy
    )

    assert not result.ok
    assert not result.live_revalidated
    assert result.errors


def test_live_source_bind_target_and_policy_changes_fail_closed(
    verified_profile: GenericBoneMap,
    mixamo_analysis: SourceSkeletonAnalysis,
    advanced_rig: ChromeRig,
    advanced_policy: TargetRetargetPolicy,
) -> None:
    changed_source = replace(mixamo_analysis, bind_hash="0" * 64)
    source_result = revalidate_verified_dl2_advanced_body_map(
        verified_profile, changed_source, advanced_rig, advanced_policy
    )
    assert not source_result.ok

    missing_roles = dict(mixamo_analysis.semantic_roles)
    missing_roles.pop("pelvis")
    missing_critical_role = replace(
        mixamo_analysis,
        semantic_roles=missing_roles,
    )
    missing_role_result = revalidate_verified_dl2_advanced_body_map(
        verified_profile,
        missing_critical_role,
        advanced_rig,
        advanced_policy,
    )
    assert not missing_role_result.ok

    changed_bone = replace(
        advanced_rig.bones[0],
        bind_translation=(0.001, *advanced_rig.bones[0].bind_translation[1:]),
    )
    changed_target = deepcopy(advanced_rig)
    changed_target.bones = (changed_bone, *advanced_rig.bones[1:])
    target_result = revalidate_verified_dl2_advanced_body_map(
        verified_profile, mixamo_analysis, changed_target, advanced_policy
    )
    assert not target_result.ok

    changed_policy = replace(advanced_policy, policy_version="stale-policy")
    policy_result = revalidate_verified_dl2_advanced_body_map(
        verified_profile, mixamo_analysis, advanced_rig, changed_policy
    )
    assert not policy_result.ok


def test_unauthorized_policy_cannot_materialize_verified_bridge(
    mixamo_analysis: SourceSkeletonAnalysis,
    advanced_rig: ChromeRig,
    advanced_policy: TargetRetargetPolicy,
) -> None:
    unauthorized = replace(
        advanced_policy,
        automatic_routing_authorized=False,
        coherence_errors=("forged or incoherent policy",),
    )

    with pytest.raises(ValueError, match="not coherent"):
        build_verified_dl2_advanced_body_map(
            mixamo_analysis, advanced_rig, unauthorized
        )
