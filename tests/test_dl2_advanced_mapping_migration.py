from __future__ import annotations

from copy import deepcopy
from dataclasses import replace
from pathlib import Path

import pytest

from dlanm2_gui.bone_maps import (
    BONE_MAP_FORMAT,
    GenericBoneMap,
    mapping_profile_origin,
    set_mapping_profile_origin,
)
from dlanm2_gui.automatic_retarget import build_automatic_retarget_plan
from dlanm2_gui.chrome_rig import ChromeRig
from dlanm2_gui.fbx_core import FbxDocument
from dlanm2_gui.project_builder import (
    _require_live_local_recipe_profile,
    _resolve_verified_dl2_advanced_map,
)
from dlanm2_gui.retarget_recipes import (
    RetargetRecipeStore,
    build_retarget_recipe,
)
from dlanm2_gui import retarget_recipes as recipe_module
from dlanm2_gui.skeleton_analysis import analyze_source_skeleton
from dlanm2_gui.target_retarget_policy import build_target_retarget_policy
from dlanm2_gui.workspace_project import DlReanimatedProject


ROOT = Path(__file__).resolve().parents[1]
PRIVATE_PROJECT = Path(r"S:\Downloads\dl2test.dlraproj")
PRIVATE_FBX = Path(r"S:\Downloads\Thriller Combined - Parts 1-4.fbx")
ADVANCED_CRIG = ROOT / "reference" / "dl2" / "player_skeleton.crig"


def _private_inputs():
    if not PRIVATE_PROJECT.is_file() or not PRIVATE_FBX.is_file():
        pytest.skip("private DL2 migration regression fixture is not available")
    project = DlReanimatedProject.load(PRIVATE_PROJECT)
    animation = project.animations[0]
    animation.source_fbx = str(PRIVATE_FBX)
    rig = ChromeRig.load(ADVANCED_CRIG)
    document = FbxDocument(
        PRIVATE_FBX,
        animation_stack="mixamo.com",
        purpose="animation",
        tolerance="recommended",
    )
    payload = dict(project.mapping_profiles[animation.mapping_profile_id])
    if payload.get("format") != BONE_MAP_FORMAT:
        pytest.skip(
            "private DL2 project no longer contains the legacy generic-map fixture"
        )
    return project, animation, rig, document, payload


def test_old_automatic_repair_is_replaced_without_promoting_or_deleting_it() -> None:
    project, animation, rig, document, payload = _private_inputs()
    old_id = animation.mapping_profile_id
    old = GenericBoneMap.from_dict(payload)
    assert mapping_profile_origin(old) == "automatic_repair"
    assert old.pairs

    replacement, verification = _resolve_verified_dl2_advanced_map(
        project,
        animation,
        document,
        rig,
        old,
        payload,
    )

    assert replacement is not None
    assert verification is not None
    assert verification.status == "pass"
    assert verification.live_revalidated
    assert mapping_profile_origin(replacement) == "automatic_verified"
    assert animation.mapping_profile_id == replacement.profile_id != old_id
    assert old_id in project.mapping_profiles
    assert project.mapping_profiles[old_id] == payload
    assert len(replacement.pairs) == 271
    certificate = replacement.extensions["automatic_retarget_certificate"]
    assert certificate["mapped_body_row_count"] == 52
    assert certificate["bind_row_count"] == 219
    audit = replacement.extensions["migration_audit"]
    assert audit["reason"] == "regenerated_legacy_automatic_repair"
    assert audit["old_profile_id"] == old_id
    assert audit["old_payload_retained_in_project"] is True


def test_manually_reviewed_advanced_map_is_never_overwritten() -> None:
    project, animation, rig, document, payload = _private_inputs()
    manual = GenericBoneMap.from_dict(payload)
    set_mapping_profile_origin(manual, "manually_reviewed")
    project.mapping_profiles[manual.profile_id] = manual.to_dict()
    animation.mapping_profile_id = manual.profile_id

    resolved, verification = _resolve_verified_dl2_advanced_map(
        project,
        animation,
        document,
        rig,
        manual,
        manual.to_dict(),
    )

    assert resolved is manual
    assert verification is None
    assert animation.mapping_profile_id == manual.profile_id
    assert mapping_profile_origin(resolved) == "manually_reviewed"


def test_stale_verified_profile_is_regenerated_not_trusted_or_mutated() -> None:
    project, animation, rig, document, payload = _private_inputs()
    old = GenericBoneMap.from_dict(payload)
    verified, first_validation = _resolve_verified_dl2_advanced_map(
        project,
        animation,
        document,
        rig,
        old,
        payload,
    )
    assert verified is not None and first_validation is not None

    stale = deepcopy(verified)
    stale.extensions["automatic_retarget_certificate"]["planner_version"] = "stale"
    project.mapping_profiles[stale.profile_id] = stale.to_dict()
    animation.mapping_profile_id = stale.profile_id
    stale_id = stale.profile_id

    replacement, verification = _resolve_verified_dl2_advanced_map(
        project,
        animation,
        document,
        rig,
        stale,
        stale.to_dict(),
    )

    assert replacement is not None and verification is not None
    assert verification.ok and verification.live_revalidated
    assert replacement.profile_id != stale_id
    assert animation.mapping_profile_id == replacement.profile_id
    assert project.mapping_profiles[stale_id]["extensions"][
        "automatic_retarget_certificate"
    ]["planner_version"] == "stale"
    assert replacement.extensions["migration_audit"]["reason"] == (
        "regenerated_stale_automatic_verified"
    )


@pytest.mark.parametrize("with_legacy_map", [False, True])
def test_project_builder_reuses_reviewed_local_recipe_without_certifying_it(
    tmp_path: Path,
    monkeypatch,
    with_legacy_map: bool,
) -> None:
    project, animation, rig, document, payload = _private_inputs()
    analysis = analyze_source_skeleton(document)
    policy = build_target_retarget_policy(
        rig,
        game_id=project.game_id,
        clip_domain="body",
    )
    plan = build_automatic_retarget_plan(analysis, rig, policy)
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
    store = RetargetRecipeStore(tmp_path / "recipes")
    store.save(recipe)
    monkeypatch.setattr(
        recipe_module,
        "default_retarget_recipe_store",
        lambda: store,
    )

    current = GenericBoneMap.from_dict(payload) if with_legacy_map else None
    current_payload = payload if with_legacy_map else {}
    old_id = animation.mapping_profile_id
    if not with_legacy_map:
        animation.mapping_profile_id = ""

    replacement, verification = _resolve_verified_dl2_advanced_map(
        project,
        animation,
        document,
        rig,
        current,
        current_payload,
    )

    assert replacement is not None
    assert verification is None
    assert mapping_profile_origin(replacement) == "manually_reviewed"
    assert len(replacement.pairs) == 271
    assert "automatic_retarget_certificate" not in replacement.extensions
    provenance = replacement.extensions["local_retarget_recipe"]
    assert provenance["recipe_id"] == recipe.recipe_id
    audit = replacement.extensions["migration_audit"]
    assert audit["new_profile_origin"] == "manually_reviewed"
    assert audit["certificate_status"] == "not_applicable_reviewed_recipe"
    assert audit["reason"] == (
        "reused_reviewed_local_recipe_over_legacy_automatic_repair"
        if with_legacy_map
        else "applied_reviewed_local_recipe"
    )
    if with_legacy_map:
        assert old_id in project.mapping_profiles
        assert project.mapping_profiles[old_id] == payload

    monkeypatch.setattr(
        recipe_module,
        "default_retarget_recipe_store",
        lambda: (_ for _ in ()).throw(
            AssertionError("applied-profile revalidation must not consult cache")
        ),
    )
    live = _require_live_local_recipe_profile(
        project,
        animation,
        analysis,
        rig,
        replacement,
    )
    assert live is not None and live.ok
    with pytest.raises(ValueError, match="needs attention"):
        _require_live_local_recipe_profile(
            project,
            animation,
            replace(analysis, bind_hash="2" * 64),
            rig,
            replacement,
        )
