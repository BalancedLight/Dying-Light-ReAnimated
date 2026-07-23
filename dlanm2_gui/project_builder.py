"""Build versioned GUI projects into new or appended animation RPacks."""

from __future__ import annotations

from copy import deepcopy
from dataclasses import asdict, dataclass, field
import json
import math
import os
from pathlib import Path
import re
import shutil
import tempfile
import time
from typing import Any, Callable

from . import anm2
from .animation_scr import (
    AnimationScrSequence,
    append_animation_scr_sequences,
    build_animation_scr_sections,
    parse_animation_scr_sections,
    patch_animation_scr_sequence_ranges,
)
from .animation_targets import RetargetUiKind, retarget_ui_kind
from .fbx_pipeline import FbxAnimationClip, build_fbx_rpack
from .chrome_rig import ChromeRig
from .chrome_rig_builder import build_chrome_rig_from_smd_template
from .fbx_core import FbxDocument
from .fbx_anm2_export_behavior import (
    LEGACY_5_0,
    coerce_fbx_anm2_export_behavior,
)
from .model_importer.fbx_model import FBX_TICKS_PER_SECOND
from .anm2_provenance import (
    anm2_provenance_path,
    build_anm2_provenance,
    write_anm2_provenance,
)

# Backward-compatible factory seam used by project-builder tests and external
# integrations.  The implementation itself is the public production class.
_FbxDocument = FbxDocument
from .pack_manifest import (
    PackManifest,
    PackResourceManifest,
    manifest_path_for_pack,
    sha256_bytes,
)
from .retarget_profiles import SourceBoneMappingProfile, auto_map_source_bones
from .semantic_retarget import (
    compile_bundled_semantic_profile,
    migrate_generic_map_to_semantic_profile,
    prepare_bundled_semantic_state,
)
from .retarget_engines.exact_rig import build_exact_rig_anm2
from .retarget_engines.mapped_rig import build_mapped_rig_anm2
from .bone_maps import GenericBoneMap, mapping_profile_origin
from .runtime_paths import resource_root
from .runtime_paths import writable_application_root
from .chrome_rig_registry import BUILTIN_MALE_RIG_REF, ChromeRigRegistry
from .rp6l import (
    AnimationLibrary,
    build_animation_library_rpack,
    extract_animation_library,
)
from .script_targets import AnimationScriptTarget, ScriptTargetRegistry
from .workspace_project import DlReanimatedProject, ProjectAnimation
from .fbx_preflight import (
    classify_target_compatibility,
    normalized_bone_name,
    preflight_fbx,
)
from .game_profiles import (
    DL1_GAME_ID,
    DL2_ADVANCED_RIG_REF,
    DL2_GAME_ID,
    get_game_profile,
)
from .helper_retarget import helper_rules_from_dicts, helper_rules_to_dicts
from .root_mapping import RootMappingSelection
from .root_motion import ROOT_MOTION_EXTENSION_KEY, RootMotionSelection
from .retarget_routing import select_exact_solver
from .target_package import validate_target_package

# DLR_MIMIC_PROTOTYPE_BODY_CORE


ProgressCallback = Callable[[str], None]


def _provenance_root_modes(animation: ProjectAnimation) -> tuple[str, str]:
    selection = RootMotionSelection.from_animation(animation)
    return (
        {
            "inplace": "in_place",
            "skeletal_root": "skeletal_root",
            "motion_accumulator": "motion_accumulator",
        }[selection.motion_mode],
        {
            "lock_initial": "lock_initial_heading",
            "preserve": "preserve",
            "to_motion_accumulator": "to_motion_accumulator",
        }[selection.heading_mode],
    )


def _resolve_bundled_dl2_semantic_map(
    project: DlReanimatedProject,
    animation: ProjectAnimation,
    document: Any,
    rig: ChromeRig,
) -> tuple[GenericBoneMap, Any, Any, SourceBoneMappingProfile]:
    """Live-compile the visible semantic profile into a target-sized map."""

    from .target_retarget_policy import build_target_retarget_policy

    policy = build_target_retarget_policy(
        rig, game_id=project.game_id, clip_domain="body"
    )
    payload = dict(
        project.mapping_profiles.get(str(animation.mapping_profile_id or ""), {})
        or {}
    )
    profile: SourceBoneMappingProfile | None = None
    migrated_from = ""
    if payload.get("format") == "dl-reanimated-retarget-profile":
        profile = SourceBoneMappingProfile.from_dict(payload)
    elif payload.get("format") == "dl-reanimated-bone-map":
        old_map = GenericBoneMap.from_dict(payload)
        profile = migrate_generic_map_to_semantic_profile(
            old_map,
            document.limb_models,
            document.parent_by_name,
            policy,
            name=f"Bundled humanoid mapping: {animation.display_name}",
        )
        migrated_from = old_map.profile_id
    state = prepare_bundled_semantic_state(
        document,
        rig,
        policy,
        profile,
        bilateral_semantic_policy=project.rig.bilateral_semantic_policy,
        profile_name=f"Bundled humanoid mapping: {animation.display_name}",
    )
    if state.profile.root_motion:
        RootMotionSelection.from_dict(
            state.profile.root_motion,
            legacy_policy=animation.root_policy,
            source_root_bone=animation.source_root_bone,
            target_root_bone=animation.target_root_bone,
        ).store(animation)
    if state.profile.locomotion.get("ik_preset"):
        animation.ik_preset = str(state.profile.locomotion["ik_preset"])
    project.mapping_profiles[state.profile.profile_id] = state.profile.to_dict()
    animation.mapping_profile_id = state.profile.profile_id
    if migrated_from:
        animation.extensions["semantic_profile_migration"] = dict(
            state.profile.extensions.get("migration_audit", {}) or {}
        )
        animation.extensions["legacy_target_map_profile_id"] = migrated_from
    compiled, live, plan = compile_bundled_semantic_profile(
        document,
        rig,
        policy,
        state.profile,
        bilateral_semantic_policy=project.rig.bilateral_semantic_policy,
        state=state,
    )
    project.mapping_profiles[compiled.profile_id] = compiled.to_dict()
    project.mapping_profiles[state.profile.profile_id] = state.profile.to_dict()
    animation.extensions["compiled_target_map_profile_id"] = compiled.profile_id
    animation.extensions["compiled_target_map_hash"] = str(
        state.profile.extensions.get("compiled_map_hash", "")
    )
    animation.extensions["compiled_target_map_live_validation"] = live.to_dict()
    animation.extensions.pop("automatic_retarget_generation_failure", None)
    return compiled, live, plan, state.profile


def _reviewed_mapping_is_name_identity(bone_map: GenericBoneMap) -> bool:
    """Return true only when every reviewed row preserves bone identity.

    A source skeleton can contain the complete target hierarchy while a user
    deliberately maps different anatomical bones.  Global bind correction is
    safe only for identity/name-equivalent rows; cross-wired rows must retain
    target pivots and use the local rotation-delta solver.
    """

    base_pairs = bone_map.base_pairs
    return bool(base_pairs) and all(
        normalized_bone_name(row.source_bone)
        == normalized_bone_name(row.target_bone)
        for row in base_pairs
    )


def _resolve_verified_dl2_advanced_map(
    project: DlReanimatedProject,
    animation: ProjectAnimation,
    document: Any,
    rig: ChromeRig,
    current: GenericBoneMap | None,
    current_payload: dict[str, Any],
) -> tuple[GenericBoneMap | None, Any | None]:
    """Generate or live-revalidate the one authorized automatic cross-rig map.

    Legacy/manual maps are never promoted.  A generated replacement receives a
    new profile ID while the complete old payload remains in
    ``project.mapping_profiles`` and a stable migration record is attached to
    the replacement and animation.
    """

    if (
        project.game_id != DL2_GAME_ID
        or rig.rig_id != DL2_ADVANCED_RIG_REF
    ):
        return current, None

    from .automatic_retarget import (
        build_dl2_advanced_body_map_with_local_recipe,
        revalidate_verified_dl2_advanced_body_map,
    )
    from .target_retarget_policy import build_target_retarget_policy

    policy = build_target_retarget_policy(
        rig,
        game_id=project.game_id,
        clip_domain="body",
    )
    origin = mapping_profile_origin(current)
    stale_verification = None
    if origin == "automatic_verified" and current is not None:
        role_overrides = None
        target_bone_overrides = None
        semantic_profile_id = str(
            current.extensions.get("semantic_profile_id", "") or ""
        )
        semantic_payload = dict(
            project.mapping_profiles.get(semantic_profile_id, {}) or {}
        )
        if semantic_payload.get("format") == "dl-reanimated-retarget-profile":
            from .semantic_retarget import semantic_role_overrides

            semantic_profile = SourceBoneMappingProfile.from_dict(semantic_payload)
            role_overrides = semantic_role_overrides(semantic_profile, policy)
            target_bone_overrides = semantic_profile.target_bone_overrides
        verification = revalidate_verified_dl2_advanced_body_map(
            current,
            document,
            rig,
            policy,
            bilateral_semantic_policy=project.rig.bilateral_semantic_policy,
            role_overrides=role_overrides,
            target_bone_overrides=target_bone_overrides,
        )
        if verification.ok and verification.live_revalidated:
            animation.extensions.setdefault("retarget_domain", "body")
            return current, verification
        # A serialized verified map is never repaired/promoted in place.  If
        # analyzer/policy versions or any live source/target invariant changed,
        # build a fresh deterministic profile and retain the stale payload for
        # audit just like the legacy automatic_repair migration.
        stale_verification = verification
    if origin in {
        "manually_reviewed",
        "imported_profile",
        "automatic_identity",
    }:
        return current, None
    if current is not None and origin not in {
        "automatic_repair",
        "automatic_verified",
    }:
        return current, None

    try:
        replacement = build_dl2_advanced_body_map_with_local_recipe(
            document,
            rig,
            policy,
            bilateral_semantic_policy=project.rig.bilateral_semantic_policy,
        )
        replacement_origin = mapping_profile_origin(replacement)
        if replacement_origin == "automatic_verified":
            verification = revalidate_verified_dl2_advanced_body_map(
                replacement,
                document,
                rig,
                policy,
                bilateral_semantic_policy=project.rig.bilateral_semantic_policy,
            )
            verification.require_valid()
        elif (
            replacement_origin == "manually_reviewed"
            and isinstance(
                replacement.extensions.get("local_retarget_recipe"), dict
            )
            and replacement.extensions["local_retarget_recipe"].get(
                "live_revalidated"
            )
            is True
        ):
            verification = None
        else:
            raise ValueError(
                "automatic map generation returned an unauthorized profile origin"
            )
        old_profile_id = str(animation.mapping_profile_id or "")
        old_payload_hash = (
            sha256_bytes(
                json.dumps(
                    current_payload,
                    sort_keys=True,
                    ensure_ascii=False,
                    separators=(",", ":"),
                ).encode("utf-8")
            )
            if current_payload
            else ""
        )
        migration = {
            "format": "dl-reanimated-automatic-mapping-migration-v1",
            "reason": (
                "reused_reviewed_local_recipe_over_legacy_automatic_repair"
                if replacement_origin == "manually_reviewed"
                and origin == "automatic_repair"
                else "applied_reviewed_local_recipe"
                if replacement_origin == "manually_reviewed"
                else "regenerated_legacy_automatic_repair"
                if origin == "automatic_repair"
                else "regenerated_stale_automatic_verified"
                if origin == "automatic_verified"
                else "generated_missing_verified_map"
            ),
            "old_profile_id": old_profile_id,
            "old_profile_origin": origin,
            "old_profile_sha256": old_payload_hash,
            "old_pair_count": len(current.pairs) if current is not None else 0,
            "old_payload_retained_in_project": bool(old_profile_id),
            "new_profile_id": replacement.profile_id,
            "new_profile_origin": replacement_origin,
            "target_rig_id": rig.rig_id,
            "target_skeleton_hash": rig.skeleton_hash,
            "certificate_status": (
                verification.status
                if verification is not None
                else "not_applicable_reviewed_recipe"
            ),
            "local_retarget_recipe": dict(
                replacement.extensions.get("local_retarget_recipe", {}) or {}
            ),
        }
        replacement.extensions["migration_audit"] = migration
        project.mapping_profiles[replacement.profile_id] = replacement.to_dict()
        animation.mapping_profile_id = replacement.profile_id
        animation.extensions["retarget_domain"] = "body"
        animation.extensions["automatic_retarget_migration"] = migration
        animation.extensions.pop("automatic_retarget_generation_failure", None)
        return replacement, verification
    except (TypeError, ValueError) as exc:
        # Preserve the existing no-map/automatic-repair path so the ordinary
        # review gate remains the sole fallback; never promote the old map.
        animation.extensions["automatic_retarget_generation_failure"] = {
            "policy": "dl2_advanced_body_bridge_v1",
            "error": str(exc),
            "prior_revalidation_errors": (
                list(stale_verification.errors)
                if stale_verification is not None
                else []
            ),
            "action": "Open Retargeting details",
        }
        return current, stale_verification


def _resolve_local_reviewed_recipe_map(
    project: DlReanimatedProject,
    animation: ProjectAnimation,
    document: Any,
    rig: ChromeRig,
    current: GenericBoneMap | None,
) -> GenericBoneMap | None:
    """Reuse a reviewed local recipe for a generic/custom exact target."""

    if (
        project.game_id == DL2_GAME_ID
        and rig.rig_id == DL2_ADVANCED_RIG_REF
    ):
        return current
    if mapping_profile_origin(current) in {
        "manually_reviewed",
        "imported_profile",
        "automatic_identity",
    }:
        return current
    from .automatic_retarget import build_automatic_retarget_plan
    from .retarget_recipes import (
        materialize_reviewed_retarget_recipe,
        resolve_local_retarget_recipe,
    )
    from .target_retarget_policy import build_target_retarget_policy

    try:
        policy = build_target_retarget_policy(
            rig,
            game_id=project.game_id,
            clip_domain="body",
        )
        fresh = build_automatic_retarget_plan(
            document,
            rig,
            policy,
            clip_domain="body",
            bilateral_semantic_policy=project.rig.bilateral_semantic_policy,
        )
        local = resolve_local_retarget_recipe(
            fresh,
            document,
            rig,
            policy,
        )
        if not local.applied or local.recipe is None:
            return current
        replacement = materialize_reviewed_retarget_recipe(
            local.recipe,
            document,
            rig,
            policy,
            clip_domain="body",
            profile_name="Reviewed local retarget recipe",
        )
    except (OSError, TypeError, ValueError):
        return current

    project.mapping_profiles[replacement.profile_id] = replacement.to_dict()
    animation.mapping_profile_id = replacement.profile_id
    animation.extensions["local_retarget_recipe"] = dict(
        replacement.extensions.get("local_retarget_recipe", {}) or {}
    )
    return replacement


def _require_live_local_recipe_profile(
    project: DlReanimatedProject,
    animation: ProjectAnimation,
    document: Any,
    rig: ChromeRig,
    bone_map: GenericBoneMap | None,
) -> Any | None:
    """Enforce cache-independent applied-recipe identity at the build gate."""

    if (
        bone_map is None
        or not isinstance(
            bone_map.extensions.get("local_retarget_recipe"), dict
        )
    ):
        return None
    from .retarget_recipes import revalidate_materialized_retarget_recipe
    from .target_retarget_policy import build_target_retarget_policy

    recipe_policy = build_target_retarget_policy(
        rig,
        game_id=project.game_id,
        clip_domain="body",
    )
    recipe_validation = revalidate_materialized_retarget_recipe(
        bone_map,
        document,
        rig,
        recipe_policy,
        clip_domain="body",
    )
    animation.extensions["local_retarget_recipe_validation"] = (
        recipe_validation.to_dict()
    )
    if not recipe_validation.ok:
        raise ValueError(
            f"Reviewed retarget recipe for {animation.display_name!r} "
            "needs attention:\n- "
            + "\n- ".join(
                recipe_validation.errors
                or ("live recipe revalidation did not pass",)
            )
        )
    return recipe_validation


@dataclass(slots=True)
class BuiltAnimation:
    animation_id: str
    source_fbx: str
    source_animation_stack: str
    resource_name: str
    script_resource: str
    root_policy: str
    ik_preset: str
    mapping_profile_id: str
    target_rig_ref: str
    target_rig_name: str
    target_skeleton_hash: str
    retarget_mode: str
    frame_count: int
    fps: float
    source_fps: float
    sample_fps: float
    playback_fps: float
    source_duration_seconds: float
    page_count: int
    page_frame_spans: list[int]
    anm2_path: str
    sha256: str
    retarget_report: str


@dataclass(frozen=True, slots=True)
class _AnimationTargetContext:
    rig_ref: str
    rig_path: str
    retarget_mode: str
    execution_mode: str
    rig: ChromeRig | None


def _animation_target_context(
    project: DlReanimatedProject,
    animation: ProjectAnimation,
    *,
    game_default_target_rig_ref: str,
    cache: dict[str, ChromeRig],
) -> _AnimationTargetContext:
    rig_ref = str(animation.target_rig_ref or project.rig.target_rig_ref)
    rig_path_value = str(
        animation.target_rig_path
        or (
            project.rig.target_rig_path
            if not animation.target_rig_ref
            or animation.target_rig_ref == project.rig.target_rig_ref
            else ""
        )
    )
    game_profile = get_game_profile(project.game_id)
    project_mode = str(project.rig.retarget_mode or "auto")
    if project_mode == "auto" and rig_ref in game_profile.compatible_builtin_rig_refs:
        clip_mode = "auto"
        execution_mode = "humanoid" if project.game_id == DL1_GAME_ID else "exact"
    elif (
        project_mode == "humanoid"
        and rig_ref == game_default_target_rig_ref
        and not animation.target_rig_path
    ):
        clip_mode = "humanoid"
        execution_mode = "humanoid"
    else:
        clip_mode = "exact"
        execution_mode = "exact"
    if execution_mode == "humanoid":
        return _AnimationTargetContext(
            rig_ref, rig_path_value, clip_mode, execution_mode, None
        )

    candidate: Path | None = None
    registry = ChromeRigRegistry(writable_application_root() / "rigs")
    bundled_candidate: Path | None = None
    if rig_ref == BUILTIN_MALE_RIG_REF:
        bundled = resource_root() / "reference" / "male_npc_infected.crig"
        bundled_candidate = bundled if bundled.is_file() else None
    elif rig_ref.startswith("builtin:"):
        bundled_candidate = registry.resolve(rig_ref)
    if rig_path_value:
        path = Path(rig_path_value)
        if path.is_file():
            candidate = path
        elif bundled_candidate is not None:
            # Portable project paths can become stale when a project is moved.
            # A stable built-in ID still identifies its immutable bundled CRIG.
            candidate = bundled_candidate
        else:
            raise FileNotFoundError(
                f"Animation {animation.display_name!r} selects target rig {rig_ref!r}, "
                f"but its CRIG path is not a file: {path}. Re-select the generated rig or "
                "choose Inherit project target."
            )
    if candidate is None:
        candidate = bundled_candidate or registry.resolve(rig_ref)
    if candidate is None or not candidate.is_file():
        raise FileNotFoundError(
            f"Animation {animation.display_name!r} selects target rig {rig_ref!r}, but no "
            "installed or portable CRIG path could be resolved. Re-import the model's "
            "generated CRIG, set this animation's target path, or inherit a valid project target."
        )
    resolved = str(candidate.resolve())
    rig = cache.get(resolved)
    if rig is None:
        rig = ChromeRig.load(candidate)
        cache[resolved] = rig
    if rig_ref and not rig_ref.startswith("builtin:") and rig.rig_id != rig_ref:
        raise ValueError(
            f"Animation {animation.display_name!r} selects rig reference {rig_ref!r}, but "
            f"{candidate.name!r} identifies itself as {rig.rig_id!r}. Re-select the intended "
            "CRIG; a stale same-named bind is not accepted."
        )
    rig_game = str(rig.extensions.get("game_id", "") or "")
    if rig_game and rig_game != project.game_id:
        raise ValueError(
            f"Animation {animation.display_name!r} targets CRIG {rig.name!r} for game profile "
            f"{rig_game!r}, while the project uses {project.game_id!r}. Choose a CRIG for the "
            "selected game profile."
        )
    return _AnimationTargetContext(
        rig_ref or rig.rig_id, resolved, clip_mode, execution_mode, rig
    )


@dataclass(slots=True)
class ProjectBuildResult:
    status: str
    pack_path: str
    manifest_path: str
    report_path: str
    build_mode: str
    pack_sha256: str
    animation_count: int
    script_count: int
    built_animations: list[BuiltAnimation] = field(default_factory=list)
    warnings: list[str] = field(default_factory=list)

    def to_dict(self) -> dict[str, Any]:
        return asdict(self)


def export_project_anm2_files(
    project: DlReanimatedProject,
    output_directory: str | Path,
    *,
    progress: ProgressCallback | None = None,
    warning: ProgressCallback | None = None,
) -> list[Path]:
    """Retarget enabled clips and export their ANM2 payloads without pack sidecars."""

    log = progress or (lambda _message: None)
    destination = Path(output_directory)
    destination.mkdir(parents=True, exist_ok=True)
    with tempfile.TemporaryDirectory(prefix="dl_reanimated_anm2_") as temp_name:
        export_project = deepcopy(project)
        export_project.export.mode = "new"
        export_project.export.output_directory = temp_name
        export_project.export.pack_filename = "anm2_export_work.rpack"
        export_project.export.existing_rpack = ""
        export_project.export.include_validation_controls = False
        export_project.export.write_intermediate_anm2 = True

        def report_generation(message: str) -> None:
            if message.startswith("Writing ") or message.startswith("Build complete:"):
                return
            log(message)

        result = build_project(export_project, progress=report_generation)
        for message in getattr(result, "warnings", []):
            if warning is not None:
                warning(message)

        exported: list[Path] = []
        for animation in result.built_animations:
            source = Path(animation.anm2_path)
            target = destination / f"{animation.resource_name}.anm2"
            _atomic_write_bytes(target, source.read_bytes())
            source_sidecar = anm2_provenance_path(source)
            if source_sidecar.is_file():
                _atomic_write_bytes(
                    anm2_provenance_path(target), source_sidecar.read_bytes()
                )
            exported.append(target)
        log(f"ANM2 export complete: {len(exported)} file(s) written to {destination}")
        return exported


def _build_body_project(
    project: DlReanimatedProject,
    *,
    progress: ProgressCallback | None = None,
) -> ProjectBuildResult:
    """Build all enabled project animations and write one tool-owned RPack."""

    build_started = time.perf_counter()
    log = progress or (lambda _message: None)
    errors = project.validate()
    if errors:
        raise ValueError("Project validation failed:\n- " + "\n- ".join(errors))

    enabled = [row for row in project.animations if row.enabled]
    if not enabled:
        raise ValueError("Project does not contain any enabled animations")

    retarget_mode = project.rig.retarget_mode
    fbx_anm2_export_behavior = coerce_fbx_anm2_export_behavior(
        project.rig.fbx_anm2_export_behavior
    )
    game_profile = get_game_profile(project.game_id)
    rig_cache: dict[str, ChromeRig] = {}
    target_contexts = {
        animation.animation_id: _animation_target_context(
            project,
            animation,
            game_default_target_rig_ref=game_profile.default_target_rig_ref,
            cache=rig_cache,
        )
        for animation in enabled
    }
    uses_humanoid = any(
        row.execution_mode == "humanoid" for row in target_contexts.values()
    )
    uses_exact = any(
        row.execution_mode == "exact" for row in target_contexts.values()
    )
    target_package_coherences: dict[str, dict[str, Any]] = {}
    immutable_contexts = {
        (context.rig_ref, context.rig_path): context
        for context in target_contexts.values()
    }
    for (rig_ref, _resolved_path), context in sorted(immutable_contexts.items()):
        package = game_profile.package_for_rig_ref(rig_ref)
        if package is None or not package.rig_relative_path:
            continue
        package_paths = game_profile.paths(resource_root(), rig_ref=rig_ref)
        if rig_ref == project.rig.target_rig_ref:
            stored_smd = Path(str(project.rig.canonical_smd or ""))
            stored_reference = Path(str(project.rig.target_template_anm2 or ""))
            smd_path = (
                str(stored_smd)
                if stored_smd.is_file()
                else package_paths["canonical_smd"]
            )
            crig_path = context.rig_path or project.rig.target_rig_path
            reference_path = (
                str(stored_reference)
                if stored_reference.is_file()
                else package_paths["target_template_anm2"]
            )
        else:
            smd_path = package_paths["canonical_smd"]
            crig_path = context.rig_path or package_paths["target_rig_path"]
            reference_path = package_paths["target_template_anm2"]
        coherence = validate_target_package(
            game_profile,
            rig_ref=rig_ref,
            smd_path=smd_path,
            crig_path=crig_path,
            reference_anm2_path=reference_path,
        )
        coherence.require_valid(game_profile.display_name)
        target_package_coherences[rig_ref] = coherence.to_dict()
    target_package_coherence = target_package_coherences.get(
        str(project.rig.target_rig_ref), {}
    )
    rig_paths: dict[str, Path] = {}
    target_rig_definition: ChromeRig | None = None
    if uses_humanoid:
        rig_paths = {
            "canonical_smd": Path(project.rig.canonical_smd),
            "target_template_anm2": Path(project.rig.target_template_anm2),
            "stock_writer_control_anm2": Path(project.rig.stock_writer_control_anm2),
        }
        for label, path in rig_paths.items():
            if not path.is_file():
                raise FileNotFoundError(f"{label} must be a file: {path}")
    elif not uses_exact:
        raise ValueError(f"Unsupported retarget mode: {retarget_mode!r}")

    explicit_source_rest = None
    if uses_humanoid and not project.rig.use_imported_animation_bind_pose:
        explicit_source_rest = Path(project.rig.source_rest_fbx)
        if not project.rig.source_rest_fbx.strip() or not explicit_source_rest.is_file():
            raise FileNotFoundError(
                "source_rest_fbx must be a valid FBX file when embedded bind pose is disabled: "
                f"{explicit_source_rest}"
            )

    trusted_path = None
    if (
        uses_humanoid
        and not project.rig.use_imported_animation_bind_pose
        and project.rig.trusted_source_rest_json
    ):
        trusted_path = Path(project.rig.trusted_source_rest_json)
        if not trusted_path.is_file():
            raise FileNotFoundError(
                f"trusted_source_rest_json must be a file: {trusted_path}"
            )

    if uses_humanoid:
        try:
            bundled_reference = resource_root() / "reference"
            bundled_crig = bundled_reference / "male_npc_infected.crig"
            using_bundled_assets = (
                rig_paths["canonical_smd"].resolve()
                == (bundled_reference / "player_1_tpp.smd").resolve()
                and rig_paths["target_template_anm2"].resolve()
                == (bundled_reference / "infected_turn_90r.template.anm2").resolve()
            )
            target_rig_definition = (
                ChromeRig.load(bundled_crig)
                if using_bundled_assets and bundled_crig.is_file()
                else build_chrome_rig_from_smd_template(
                    rig_paths["canonical_smd"], rig_paths["target_template_anm2"]
                )
            )
        except ValueError:
            # The legacy build path performs its own authoritative asset checks.
            # Keeping this metadata conversion non-blocking also allows isolated
            # project-builder tests to use intentionally minimal file stubs.
            target_rig_definition = None

    # Resolve, preflight and route every clip before creating any output.  A
    # later unsupported file must not leave earlier candidate ANM2/MSH/RPack
    # artifacts behind.
    preflight_by_animation: dict[str, Any] = {}
    mapping_document_by_animation: dict[str, Any] = {}
    bone_map_by_animation: dict[str, GenericBoneMap | None] = {}
    automatic_verification_by_animation: dict[str, Any] = {}
    compatibility_by_animation: dict[str, dict[str, Any]] = {}
    solver_by_animation: dict[str, Any] = {}
    humanoid_profile_by_animation: dict[str, SourceBoneMappingProfile] = {}
    expected_frame_count_by_animation: dict[str, int] = {}
    timing_by_animation: dict[str, dict[str, Any]] = {}
    stage_timings_by_animation: dict[str, dict[str, float]] = {}
    import_tolerance = str(
        project.extensions.get("import_tolerance", "recommended") or "recommended"
    )
    for import_index, animation in enumerate(enabled, start=1):
        source_path = Path(animation.source_fbx)
        if not source_path.is_file():
            raise FileNotFoundError(
                f"Animation {animation.display_name!r} cannot be built because its source "
                f"FBX is missing or unreadable: {source_path}. The clip cannot be preflighted "
                "or sampled safely. Re-select a supported binary FBX and retry; Exact Rig is "
                "viable only when that FBX contains the selected target hierarchy. No ANM2 or "
                "RPack output was created."
            )
        context = target_contexts[animation.animation_id]
        log(f"[{import_index}/{len(enabled)}] Parsing FBX: {source_path.name}")
        parse_started = time.perf_counter()
        document = (
            _FbxDocument(
                source_path,
                purpose="animation",
                tolerance=import_tolerance,
            )
            if _FbxDocument is FbxDocument
            else _FbxDocument(source_path)
        )
        stage_timings = {
            "parse_seconds": time.perf_counter() - parse_started,
        }
        transform_validation_started = time.perf_counter()
        preflight = None
        log(
            f"[{import_index}/{len(enabled)}] Transform validation: "
            f"{source_path.name}"
        )
        if source_path.read_bytes()[:18] == b"Kaydara FBX Binary":
            preflight = preflight_fbx(
                source_path,
                purpose="animation",
                animation_stack=animation.source_animation_stack or None,
                target_rig=context.rig if context.execution_mode == "exact" else None,
                game_id=project.game_id,
                document=document,
                tolerance=import_tolerance,
            )
            preflight.require_buildable()
            preflight_by_animation[animation.animation_id] = preflight
        selected_stack = getattr(document, "selected_animation_stack", None)
        selected_stack_name = str(getattr(selected_stack, "name", "") or "")
        if (
            animation.source_animation_stack
            and selected_stack_name != animation.source_animation_stack
        ):
            document.select_animation_stack(animation.source_animation_stack)
        stage_timings["transform_validation_seconds"] = (
            time.perf_counter() - transform_validation_started
        )
        mapping_document_by_animation[animation.animation_id] = document
        sample_fps = float(animation.resolved_sample_fps())
        playback_fps = float(animation.resolved_playback_fps())
        declared_timebase = getattr(document, "declared_timebase", None)
        source_fps = float(
            getattr(declared_timebase, "declared_fps", 0.0)
            or animation.resolved_source_fps()
            or sample_fps
        )
        if not all(
            math.isfinite(value) and value > 0.0
            for value in (sample_fps, playback_fps, source_fps)
        ):
            raise ValueError(
                f"Animation {animation.display_name!r} has invalid timing. "
                "Choose positive source, sample, and playback rates and rebuild; "
                "no ANM2 or RPack output was created."
            )
        # The parsed FBX is authoritative. Refresh persisted provenance even
        # when a row already carried timing from a previous file or stack.
        animation.source_fps = source_fps
        if declared_timebase is not None and hasattr(declared_timebase, "to_dict"):
            extensions = dict(animation.extensions)
            extensions["timing_origin_v10"] = declared_timebase.to_dict()
            animation.extensions = extensions
        has_source_tick_span = hasattr(document, "animation_start_tick") and hasattr(
            document, "animation_stop_tick"
        )
        start_tick = int(getattr(document, "animation_start_tick", 0) or 0)
        stop_tick = max(
            start_tick,
            int(getattr(document, "animation_stop_tick", start_tick) or start_tick),
        )
        predicted_frame_count: int | None = None
        if hasattr(document, "frame_ticks"):
            predicted_frame_count = len(document.frame_ticks(fps=sample_fps))
        elif hasattr(document, "frame_count"):
            predicted_frame_count = int(document.frame_count(fps=sample_fps))
        source_duration_seconds = (
            float(stop_tick - start_tick) / float(FBX_TICKS_PER_SECOND)
            if has_source_tick_span
            else float(max(0, (predicted_frame_count or 1) - 1)) / sample_fps
        )
        timing_by_animation[animation.animation_id] = {
            "source_fps": source_fps,
            "sample_fps": sample_fps,
            "playback_fps": playback_fps,
            "source_duration_seconds": source_duration_seconds,
            "source_fbx_sha256": sha256_bytes(source_path.read_bytes()),
            "declared_timebase": (
                declared_timebase.to_dict()
                if declared_timebase is not None
                and hasattr(declared_timebase, "to_dict")
                else None
            ),
        }
        if predicted_frame_count is not None:
            if context.execution_mode == "exact":
                predicted_frame_count = max(2, predicted_frame_count)
            expected_frame_count_by_animation[animation.animation_id] = (
                predicted_frame_count
            )
        start_frame = 0 if animation.start_frame is None else int(animation.start_frame)
        end_frame = (
            (predicted_frame_count - 1)
            if animation.end_frame is None and predicted_frame_count is not None
            else int(animation.end_frame)
            if animation.end_frame is not None
            else None
        )
        invalid_frame_range = start_frame < 0 or (
            end_frame is not None
            and (
                end_frame < start_frame
                or (
                    predicted_frame_count is not None
                    and end_frame >= predicted_frame_count
                )
            )
        )
        if invalid_frame_range:
            count_text = (
                f", canonical FBX sampling produces {predicted_frame_count} frames"
                if predicted_frame_count is not None
                else ""
            )
            raise ValueError(
                f"Animation {animation.display_name!r} has invalid configured frame range "
                f"{start_frame}..{end_frame}{count_text}. Choose a range inside the sampled "
                "clip and retry; Exact Rig does not make an out-of-range selection viable. "
                "No ANM2 or RPack output was created."
            )
        planning_started = time.perf_counter()
        if context.execution_mode == "humanoid":
            profile = _mapping_profile_for_animation(project, animation, document)
            mapping_errors = profile.validate(document.limb_models)
            if mapping_errors:
                raise ValueError(
                    f"Retarget mapping for {animation.display_name!r} is incomplete:\n- "
                    + "\n- ".join(mapping_errors)
                )
            humanoid_profile_by_animation[animation.animation_id] = profile
            stage_timings["planning_seconds"] = time.perf_counter() - planning_started
            stage_timings_by_animation[animation.animation_id] = stage_timings
            continue
        assert context.rig is not None
        bundled_dl2_semantic = bool(
            project.game_id == DL2_GAME_ID
            and retarget_ui_kind(project, animation)
            == RetargetUiKind.BUILTIN_HUMANOID
        )
        if bundled_dl2_semantic:
            compatibility = classify_target_compatibility(document, context.rig)
            try:
                bone_map, automatic_verification, semantic_plan, semantic_profile = (
                    _resolve_bundled_dl2_semantic_map(
                        project, animation, document, context.rig
                    )
                )
            except (TypeError, ValueError) as exc:
                animation.extensions["automatic_retarget_generation_failure"] = {
                    "status": "needs_attention",
                    "reason": "The target rig or retarget data is invalid.",
                    "diagnostic": str(exc),
                    "action": "Verify the source file and selected target rig",
                }
                raise ValueError(
                    f"Animation {animation.display_name!r} cannot build against the selected "
                    f"target rig: {exc}"
                ) from exc
            solver = select_exact_solver(
                compatibility,
                bone_map,
                automatic_verification=automatic_verification,
            )
            if not solver.build_allowed:
                raise ValueError(
                    f"Animation {animation.display_name!r} cannot compile its semantic "
                    f"profile: {solver.blocking_error}"
                )
            animation.extensions["semantic_retarget_summary"] = {
                "semantic_profile_id": semantic_profile.profile_id,
                "manual_override_count": semantic_profile.manual_override_count,
                "role_to_source": dict(semantic_profile.role_to_bone),
                "mapping_mode_counts": semantic_plan.mapping_modes,
                "mapped_target_count": sum(
                    row.mode in {"direct", "composed", "distributed"}
                    for row in semantic_plan.decisions
                ),
                "bind_default_target_count": sum(
                    row.mode in {"inherit_bind", "static_bind"}
                    for row in semantic_plan.decisions
                ),
                "ignored_animated_source_count": len(
                    semantic_plan.ignored_animated_source_bones
                ),
                "ignored_animated_source_bones": list(
                    semantic_plan.ignored_animated_source_bones
                ),
                "compiled_internal_map_id": bone_map.profile_id,
                "compiled_internal_map_hash": animation.extensions.get(
                    "compiled_target_map_hash", ""
                ),
                "target_policy_id": semantic_profile.target_policy_id,
                "selected_engine": solver.selected_engine,
                "selected_engine_reason": solver.selection_reason,
                "live_validation_status": automatic_verification.status,
            }
            bone_map_by_animation[animation.animation_id] = bone_map
            automatic_verification_by_animation[animation.animation_id] = (
                automatic_verification
            )
            compatibility_by_animation[animation.animation_id] = compatibility
            solver_by_animation[animation.animation_id] = solver
            stage_timings["planning_seconds"] = time.perf_counter() - planning_started
            stage_timings_by_animation[animation.animation_id] = stage_timings
            continue
        mapping_payload = project.mapping_profiles.get(
            str(animation.mapping_profile_id or ""), {}
        )
        if animation.mapping_profile_id and not mapping_payload:
            raise ValueError(
                f"Animation {animation.display_name!r} references missing mapping profile "
                f"{animation.mapping_profile_id}. Create/review a map for target "
                f"{context.rig.name!r}."
            )
        if animation.mapping_profile_id and mapping_payload.get("format") != "dl-reanimated-bone-map":
            raise ValueError(
                f"Animation {animation.display_name!r} references a mapping that is not a "
                "generic CRIG bone map. Create a reviewed map for its selected target rig."
            )
        bone_map = (
            GenericBoneMap.from_dict(mapping_payload)
            if mapping_payload.get("format") == "dl-reanimated-bone-map"
            else None
        )
        compatibility = classify_target_compatibility(document, context.rig)
        incompatible = bool(
            compatibility.get("required_missing_bones")
            or compatibility.get("hierarchy_mismatches")
        )
        if incompatible or mapping_profile_origin(bone_map) == "automatic_verified":
            bone_map, automatic_verification = _resolve_verified_dl2_advanced_map(
                project,
                animation,
                document,
                context.rig,
                bone_map,
                dict(mapping_payload),
            )
            if (
                project.game_id != DL2_GAME_ID
                or context.rig.rig_id != DL2_ADVANCED_RIG_REF
            ):
                bone_map = _resolve_local_reviewed_recipe_map(
                    project,
                    animation,
                    document,
                    context.rig,
                    bone_map,
                )
        else:
            automatic_verification = None
        _require_live_local_recipe_profile(
            project,
            animation,
            document,
            context.rig,
            bone_map,
        )
        solver = select_exact_solver(
            compatibility,
            bone_map,
            automatic_verification=automatic_verification,
        )
        if not solver.build_allowed:
            generation_failure = dict(
                animation.extensions.get(
                    "automatic_retarget_generation_failure", {}
                )
                or {}
            )
            if generation_failure:
                failed_invariant = str(
                    generation_failure.get("error", "")
                    or "the verified DL2 body-map invariants did not pass"
                )
                raise ValueError(
                    f"Animation {animation.display_name!r} could not generate the safe "
                    f"DL2 advanced body bridge: {failed_invariant}. "
                    "Open Retargeting details."
                )
            raise ValueError(
                f"Animation {animation.display_name!r} cannot target {context.rig.name!r}: "
                + solver.blocking_error
            )
        bone_map_by_animation[animation.animation_id] = bone_map
        if automatic_verification is not None:
            automatic_verification_by_animation[animation.animation_id] = (
                automatic_verification
            )
        compatibility_by_animation[animation.animation_id] = compatibility
        solver_by_animation[animation.animation_id] = solver
        stage_timings["planning_seconds"] = time.perf_counter() - planning_started
        stage_timings_by_animation[animation.animation_id] = stage_timings

    # Resolve naming, script routing, and append-pack provenance while the
    # operation is still read-only. An invalid late clip or stale append pack
    # must not leave an otherwise empty output/build tree behind.
    registry = _project_script_registry(project)
    resource_name_by_animation: dict[str, str] = {}
    script_resource_by_animation: dict[str, str] = {}
    for animation in enabled:
        resource_name = _final_resource_name(project, animation)
        _validate_resource_name(resource_name)
        resource_name_by_animation[animation.animation_id] = resource_name
        script_resource_by_animation[animation.animation_id] = (
            _resolve_script_resource(project, animation, registry)
        )

    existing_library = AnimationLibrary({}, {})
    existing_manifest: PackManifest | None = None
    warnings: list[str] = []
    if project.export.mode == "append":
        existing_path = Path(project.export.existing_rpack)
        if not existing_path.is_file():
            raise FileNotFoundError(f"Existing RPack must be a file: {existing_path}")
        log(f"Loading existing tool RPack: {existing_path}")
        existing_library = extract_animation_library(existing_path.read_bytes())
        existing_manifest = PackManifest.load_for_pack(existing_path)
        if existing_manifest is None:
            warnings.append(
                "Existing pack has no .dlrmanifest.json sidecar. It was parseable as a "
                "tool-owned animation library, but provenance could not be verified."
            )
        elif not existing_manifest.verify_pack_hash(existing_path):
            raise ValueError(
                "Existing RPack no longer matches its DL ReAnimated manifest. "
                "Refusing append to avoid overwriting unknown changes."
            )

    if project.export.collision_policy == "error":
        collisions = sorted(
            {
                resource_name_by_animation[row.animation_id]
                for row in enabled
                if resource_name_by_animation[row.animation_id]
                in existing_library.animations
            },
            key=str.casefold,
        )
        if collisions:
            raise ValueError(
                "Append preflight found animation resources that already exist in the "
                "selected RPack: " + ", ".join(collisions)
                + ". Choose Replace collisions or rename the affected animations; no ANM2 "
                "or RPack output was created."
            )

    if not project.export.output_directory.strip():
        raise ValueError("Choose an output folder before building")
    output_dir = Path(project.export.output_directory)
    output_dir.mkdir(parents=True, exist_ok=True)
    output_pack = output_dir / project.export.pack_filename
    report_dir = output_dir / "dl_reanimated_build"
    work_dir = report_dir / "intermediate"
    if report_dir.exists():
        shutil.rmtree(report_dir)
    work_dir.mkdir(parents=True, exist_ok=True)

    if uses_exact and project.export.include_validation_controls:
        warnings.append(
            "Bundled humanoid writer/bind controls are not compatible with custom Chrome "
            "Rigs and were omitted. Exact-rig output is decoded and sampled during validation."
        )
    final_animations = dict(existing_library.animations)
    final_scripts = dict(existing_library.animation_scripts)
    sequences_by_script: dict[str, list[AnimationScrSequence]] = {}
    built_rows: list[BuiltAnimation] = []
    manifest_rows: list[PackResourceManifest] = []
    solver_selection_rows: list[dict[str, Any]] = []
    normalization_rows: list[dict[str, Any]] = []
    hierarchy_safety_rows: list[dict[str, Any]] = []
    controls_added = False

    for index, animation in enumerate(enabled, start=1):
        source_path = Path(animation.source_fbx)
        context = target_contexts[animation.animation_id]
        clip_retarget_mode = context.retarget_mode
        clip_execution_mode = context.execution_mode
        clip_target_rig = context.rig
        timing = timing_by_animation[animation.animation_id]
        source_fps = float(timing["source_fps"])
        sample_fps = float(timing["sample_fps"])
        playback_fps = float(timing["playback_fps"])
        source_duration_seconds = float(timing["source_duration_seconds"])
        log(f"[{index}/{len(enabled)}] Reading skeleton: {source_path.name}")
        preflight = preflight_by_animation.get(animation.animation_id)
        profile: SourceBoneMappingProfile | None = humanoid_profile_by_animation.get(
            animation.animation_id
        )

        script_resource = script_resource_by_animation[animation.animation_id]
        resource_name = resource_name_by_animation[animation.animation_id]
        if resource_name in final_animations and project.export.collision_policy == "error":
            raise ValueError(
                f"Animation resource already exists in output library: {resource_name}. "
                "Choose Replace collisions or rename the animation."
            )

        clip_out = work_dir / f"{index:03d}_{animation.animation_id}"
        log(
            f"[{index}/{len(enabled)}] Retargeting {animation.display_name} "
            f"({animation.root_policy}, {script_resource})"
        )
        retarget_started = time.perf_counter()
        candidate_write_seconds = 0.0
        if clip_execution_mode == "exact":
            assert clip_target_rig is not None
            source_rest_for_clip = source_path
            clip_out.mkdir(parents=True, exist_ok=True)
            bone_map = bone_map_by_animation.get(animation.animation_id)
            compatibility = compatibility_by_animation[animation.animation_id]
            solver_selection = solver_by_animation[animation.animation_id]
            if fbx_anm2_export_behavior == LEGACY_5_0:
                source_rest_policy = "legacy_5_0_global_bind_basis"
                exact_build = build_exact_rig_anm2(
                    source_path,
                    clip_target_rig,
                    fps=sample_fps,
                    animation_stack=animation.source_animation_stack or None,
                    root_mapping=RootMappingSelection.from_animation(animation),
                    root_policy=animation.root_policy,
                    root_motion=RootMotionSelection.from_animation(animation),
                    fbx_anm2_export_behavior=fbx_anm2_export_behavior,
                    bilateral_semantic_policy=project.rig.bilateral_semantic_policy,
                    document=mapping_document_by_animation[animation.animation_id],
                    preflight=preflight,
                    progress=lambda update, clip_index=index: log(
                        f"[{clip_index}/{len(enabled)}] {update}"
                    ),
                )
            elif solver_selection.selected_engine == "MappedRigRetargetEngine":
                assert bone_map is not None
                source_rest_policy = "reviewed_mapped_crig"
                exact_build = build_mapped_rig_anm2(
                    source_path,
                    clip_target_rig,
                    bone_map,
                    fps=sample_fps,
                    animation_stack=animation.source_animation_stack or None,
                    root_mapping=RootMappingSelection.from_animation(animation),
                    transfer_policy=solver_selection.selected_policy,
                    root_policy=animation.root_policy,
                    root_motion=RootMotionSelection.from_animation(animation),
                    fbx_anm2_export_behavior=fbx_anm2_export_behavior,
                    bilateral_semantic_policy=project.rig.bilateral_semantic_policy,
                    document=mapping_document_by_animation[animation.animation_id],
                    preflight=preflight,
                    progress=lambda update, clip_index=index: log(
                        f"[{clip_index}/{len(enabled)}] {update}"
                    ),
                )
                exact_build.report["mapping_transfer_selection"] = {
                    "policy": solver_selection.selected_policy,
                    "reason": solver_selection.selection_reason,
                    "source_target_classification": compatibility.get(
                        "classification", "cross_rig"
                    ),
                    "required_missing_bone_count": len(
                        compatibility.get("required_missing_bones", ())
                    ),
                    "hierarchy_mismatch_count": len(
                        compatibility.get("hierarchy_mismatches", ())
                    ),
                    "identity_mapping": _reviewed_mapping_is_name_identity(bone_map),
                }
            else:
                source_rest_policy = "exact_same_rig"
                exact_build = build_exact_rig_anm2(
                    source_path,
                    clip_target_rig,
                    fps=sample_fps,
                    animation_stack=animation.source_animation_stack or None,
                    root_mapping=RootMappingSelection.from_animation(animation),
                    root_policy=animation.root_policy,
                    root_motion=RootMotionSelection.from_animation(animation),
                    fbx_anm2_export_behavior=fbx_anm2_export_behavior,
                    bilateral_semantic_policy=project.rig.bilateral_semantic_policy,
                    document=mapping_document_by_animation[animation.animation_id],
                    preflight=preflight,
                    progress=lambda update, clip_index=index: log(
                        f"[{clip_index}/{len(enabled)}] {update}"
                    ),
                )
            exact_build.report["solver_selection"] = (
                {
                    "selected_engine": "Legacy50GlobalBindRetargetEngine",
                    "selected_policy": "legacy_5_0_global_bind_basis",
                    "selection_reason": (
                        "Project selected Legacy 5.0 FBX-to-ANM2 export behavior."
                    ),
                    "modern_solver_bypassed": solver_selection.to_dict(),
                }
                if fbx_anm2_export_behavior == LEGACY_5_0
                else solver_selection.to_dict()
            )
            automatic_verification = automatic_verification_by_animation.get(
                animation.animation_id
            )
            if automatic_verification is not None:
                exact_build.report["automatic_retarget_verification"] = (
                    automatic_verification.to_dict()
                    if hasattr(automatic_verification, "to_dict")
                    else dict(automatic_verification)
                )
            payload = exact_build.payload
            candidate_path = clip_out / f"{resource_name}.anm2"
            candidate_write_started = time.perf_counter()
            candidate_path.write_bytes(payload)
            candidate_write_seconds = time.perf_counter() - candidate_write_started
            retarget_report = dict(exact_build.report)
            retarget_report["candidate_path"] = str(candidate_path)
            retarget_report["requested_project_root_policy"] = animation.root_policy
        else:
            assert profile is not None
            root_motion_selection = RootMotionSelection.from_animation(animation)
            role_to_bone = dict(getattr(profile, "role_to_bone", {}) or {})
            source_root_bone = (
                root_motion_selection.source_root_bone
                or role_to_bone.get("hips", "")
                or "mixamorig:Hips"
            )
            target_root_bone = (
                root_motion_selection.target_root_bone or "bip01"
            )
            helper_rules = helper_rules_from_dicts(
                animation.extensions.get("helper_retarget_rules", ()) or ()
            )
            source_rest_for_clip = (
                source_path
                if project.rig.use_imported_animation_bind_pose
                else explicit_source_rest
            )
            source_rest_policy = (
                "embedded_animation_fbx"
                if project.rig.use_imported_animation_bind_pose
                else "explicit_rest_fbx"
            )
            build_fbx_rpack(
                animation_clips=[
                    FbxAnimationClip(source_path, animation.source_animation_stack)
                ],
                source_rest_fbx=source_rest_for_clip,
                trusted_source_rest_json=trusted_path,
                canonical_smd=rig_paths["canonical_smd"],
                target_template_anm2=rig_paths["target_template_anm2"],
                stock_writer_control_anm2=rig_paths["stock_writer_control_anm2"],
                out_dir=clip_out,
                root_policies=(animation.root_policy,),
                ik_authoring_preset=animation.ik_preset,
                source_bone_aliases=profile.canonical_aliases(),
                animation_script_resource_name=script_resource,
                include_controls=(
                    project.export.include_validation_controls and not controls_added
                ),
                helper_rules=helper_rules_to_dicts(helper_rules),
                source_root_bone=source_root_bone,
                target_root_bone=target_root_bone,
                root_heading_modes=(
                    {
                        animation.root_policy: root_motion_selection.heading_mode,
                    }
                    if ROOT_MOTION_EXTENSION_KEY in animation.extensions
                    else None
                ),
                sample_fps=sample_fps,
            )
            reports = json.loads(
                (clip_out / "retarget_candidate_summary.json").read_text(encoding="utf-8-sig")
            )
            if len(reports) != 1:
                raise ValueError(
                    f"Expected one retarget candidate for {animation.display_name}, got {len(reports)}"
                )
            retarget_report = reports[0]
            candidate_path = Path(retarget_report["candidate_path"])
            payload = candidate_path.read_bytes()
        stage_timings = dict(
            stage_timings_by_animation.get(animation.animation_id, {})
        )
        stage_timings["retarget_seconds"] = time.perf_counter() - retarget_started
        stage_timings["candidate_write_seconds"] = candidate_write_seconds
        retarget_report["game_id"] = project.game_id
        retarget_report["requested_retarget_mode"] = clip_retarget_mode
        retarget_report["resolved_execution_mode"] = clip_execution_mode
        semantic_summary = dict(
            animation.extensions.get("semantic_retarget_summary", {}) or {}
        )
        if semantic_summary:
            retarget_report["semantic_retarget"] = semantic_summary
        if context.rig_ref in target_package_coherences:
            retarget_report["target_package_coherence"] = target_package_coherences[
                context.rig_ref
            ]
        if preflight is not None:
            retarget_report["fbx_preflight"] = preflight.to_dict()
        retarget_report.setdefault("preflight_policy", "export_first_v1")
        preflight_inventory = (
            dict(preflight.inventory or {}) if preflight is not None else {}
        )
        transform_contract = dict(
            preflight_inventory.get("transform_contract", {}) or {}
        )
        wrappers = list(transform_contract.get("common_wrapper_models", ()) or ())
        retarget_report.setdefault(
            "wrapper_canonicalization",
            {
                "applied": bool(
                    transform_contract.get(
                        "canonicalized_wrapper_reflection", False
                    )
                ),
                "wrapper": wrappers[0] if len(wrappers) == 1 else wrappers,
                "matrix": transform_contract.get("common_wrapper_matrix"),
                "uniform": bool(
                    transform_contract.get("common_wrapper_is_uniform", False)
                ),
                "static": bool(
                    transform_contract.get("common_wrapper_is_static", False)
                ),
                "reflected": bool(
                    transform_contract.get("common_wrapper_is_reflected", False)
                ),
            },
        )
        retarget_report.setdefault(
            "canonical_transform_validation",
            dict(transform_contract.get("canonical_transform_validation", {}) or {}),
        )
        compatibility = dict(
            preflight_inventory.get("target_compatibility", {}) or {}
        )
        retarget_report.setdefault(
            "fbx_anm2_export_behavior", fbx_anm2_export_behavior
        )
        retarget_report.setdefault(
            "sampler_contract",
            (
                "dlr_0_5_0_global_bind_basis_v1"
                if fbx_anm2_export_behavior == LEGACY_5_0
                else "dlr_current_normalized_global_v2"
            ),
        )
        retarget_report.setdefault(
            "bilateral_semantic_policy",
            project.rig.bilateral_semantic_policy,
        )
        retarget_report.setdefault(
            "source_target_classification",
            str(
                retarget_report.get("skeleton_classification", "")
                or compatibility.get("classification", "")
            ),
        )
        retarget_report.setdefault(
            "bind_retained_bones",
            list(compatibility.get("target_bind_bones", ()) or ()),
        )
        retarget_report.setdefault(
            "source_animation_stack",
            animation.source_animation_stack,
        )
        certificate = dict(
            retarget_report.get("automatic_retarget_certificate", {}) or {}
        )
        retarget_report.setdefault(
            "mapping",
            {
                "exact_target_subset_rows": int(
                    certificate.get(
                        "exact_target_subset_rows",
                        compatibility.get("exact_target_subset_rows", 0),
                    )
                    or 0
                ),
                "semantic_rows": int(certificate.get("semantic_rows", 0) or 0),
                "manual_target_overrides": int(
                    certificate.get("manual_override_rows", 0) or 0
                ),
                "target_bind_rows": int(
                    certificate.get(
                        "target_bind_rows",
                        compatibility.get("target_bind_rows", 0),
                    )
                    or 0
                ),
                "spatial_only_rows": int(
                    certificate.get("spatial_only_row_count", 0) or 0
                ),
            },
        )
        coherence_payload = dict(
            target_package_coherences.get(context.rig_ref, {}) or {}
        )
        raw_hash_mismatches: list[str] = []
        if coherence_payload and not coherence_payload.get(
            "source_smd_hash_match", False
        ):
            raw_hash_mismatches.append("source_smd")
        if coherence_payload and not coherence_payload.get(
            "reference_anm2_hash_match", False
        ):
            raw_hash_mismatches.append("reference_anm2")
        retarget_report["provenance"] = {
            "raw_hash_mismatches": raw_hash_mismatches,
            "semantic_hash_matches": bool(
                coherence_payload.get("source_smd_semantic_hash_match", True)
                and coherence_payload.get("reference_anm2_format_match", True)
            ),
            "smd_raw_sha256": coherence_payload.get("smd_raw_sha256", ""),
            "smd_semantic_sha256": coherence_payload.get(
                "smd_semantic_sha256", ""
            ),
            "reference_anm2_raw_sha256": coherence_payload.get(
                "reference_anm2_raw_sha256", ""
            ),
            "warnings": list(coherence_payload.get("warnings", ()) or ()),
        }
        retarget_report["output_anm2_format"] = (
            "format 1 compatibility" if project.game_id == DL2_GAME_ID else "format 1"
        )
        retarget_report["output_validation_status"] = (
            "experimental" if project.game_id == DL2_GAME_ID else "validated"
        )
        retarget_report["timing"] = {
            "source_fps": source_fps,
            "sample_fps": sample_fps,
            "playback_fps": playback_fps,
            "source_duration_seconds": source_duration_seconds,
            "declared_timebase": timing.get("declared_timebase"),
        }
        retarget_report["source_fps"] = source_fps
        retarget_report["sample_fps"] = sample_fps
        retarget_report["playback_fps"] = playback_fps
        retarget_report["source_duration_seconds"] = source_duration_seconds
        report_identity = {
            "animation_id": animation.animation_id,
            "animation_name": animation.display_name,
            "resource_name": resource_name,
        }
        if retarget_report.get("solver_selection"):
            solver_selection_rows.append(
                {**report_identity, **retarget_report["solver_selection"]}
            )
        if retarget_report.get("source_global_normalization"):
            normalization_rows.append(
                {**report_identity, **retarget_report["source_global_normalization"]}
            )
        if retarget_report.get("hierarchy_safety"):
            hierarchy_safety_rows.append(
                {**report_identity, **retarget_report["hierarchy_safety"]}
            )
        for message in retarget_report.get("warnings", []):
            rendered = f"{animation.display_name}: {message}"
            warnings.append(rendered)
            log(f"WARNING: {rendered}")
        output_validation_started = time.perf_counter()
        page_layout = _validate_generated_anm2_payload(
            payload,
            resource_name=resource_name,
        )
        stage_timings["project_output_validation_seconds"] = (
            time.perf_counter() - output_validation_started
        )
        performance = dict(retarget_report.get("performance", {}) or {})
        performance.update(
            {
                name: round(value, 6)
                for name, value in stage_timings.items()
            }
        )
        retarget_report["performance"] = performance
        final_animations[resource_name] = payload

        frame_count = int(retarget_report["frame_count"])
        root_motion_mode, root_heading_mode = _provenance_root_modes(animation)
        provenance_payload = build_anm2_provenance(
            payload,
            source_fbx=source_path.name,
            source_fbx_sha256=str(timing["source_fbx_sha256"]),
            source_fbx_fps=source_fps,
            sample_fps=sample_fps,
            playback_fps=playback_fps,
            source_duration_seconds=source_duration_seconds,
            frame_count=frame_count,
            root_motion_mode=root_motion_mode,
            root_heading_mode=root_heading_mode,
            source_animation_stack=(
                animation.source_animation_stack
                or str(retarget_report.get("source_animation_stack", ""))
            ),
            fbx_anm2_export_behavior=fbx_anm2_export_behavior,
            sampler_contract=str(
                retarget_report.get("sampler_contract", "") or ""
            ),
            source_target_compatibility_class=str(
                retarget_report.get("source_target_classification", "") or ""
            ),
            bind_retained_bones=list(
                retarget_report.get("bind_retained_bones", ()) or ()
            ),
            wrapper_reflection_detected=bool(
                retarget_report.get("wrapper_reflection_detected", False)
            ),
            wrapper_canonicalized=bool(
                retarget_report.get("wrapper_canonicalized", False)
            ),
            wrapper_matrix=retarget_report.get("wrapper_matrix"),
            bilateral_semantic_policy=str(
                retarget_report.get("bilateral_semantic_policy", "") or ""
            ),
            bilateral_swap_applied=bool(
                retarget_report.get("bilateral_swap_applied", False)
            ),
            bilateral_swapped_row_count=int(
                retarget_report.get("bilateral_swapped_row_count", 0) or 0
            ),
            post_canonicalization_mirror_conjugation_applied=bool(
                retarget_report.get(
                    "post_canonicalization_mirror_conjugation_applied",
                    False,
                )
            ),
        )
        candidate_provenance_path = write_anm2_provenance(
            candidate_path, provenance_payload
        )
        start_frame = 0 if animation.start_frame is None else int(animation.start_frame)
        end_frame = (
            frame_count - 1 if animation.end_frame is None else int(animation.end_frame)
        )
        if start_frame < 0 or end_frame < start_frame or end_frame >= frame_count:
            raise ValueError(
                f"Invalid frame range for {animation.display_name}: "
                f"{start_frame}..{end_frame}, clip has {frame_count} frames"
            )
        sequence = AnimationScrSequence(
            name=resource_name,
            anm2_name=f"{resource_name}.anm2",
            start_frame=float(start_frame),
            end_frame=float(end_frame),
            fps=playback_fps,
            enabled=1,
            blend=0.5,
        )
        sequences_by_script.setdefault(script_resource, []).append(sequence)

        animation_dir = report_dir / "animations"
        retarget_dir = report_dir / "retarget_reports"
        animation_dir.mkdir(parents=True, exist_ok=True)
        retarget_dir.mkdir(parents=True, exist_ok=True)
        exported_anm2 = animation_dir / f"{resource_name}.anm2"
        if project.export.write_intermediate_anm2:
            exported_anm2.write_bytes(payload)
            exported_provenance_path = write_anm2_provenance(
                exported_anm2, provenance_payload
            )
            exported_anm2_value = str(exported_anm2)
        else:
            exported_provenance_path = None
            exported_anm2_value = (
                f"rpack:{output_pack.name}#_ANIMATION_/{resource_name}"
            )

        persisted_retarget_report = dict(retarget_report)
        selected_rig_name = (
            clip_target_rig.name
            if clip_target_rig is not None
            else target_rig_definition.name
            if target_rig_definition is not None
            else ""
        )
        selected_skeleton_hash = (
            clip_target_rig.skeleton_hash
            if clip_target_rig is not None
            else target_rig_definition.skeleton_hash
            if target_rig_definition is not None
            else ""
        )
        persisted_retarget_report.update(
            {
                "resource_name": resource_name,
                "script_resource": script_resource,
                "mapping_profile_id": animation.mapping_profile_id,
                "root_policy": animation.root_policy,
                "ik_preset": animation.ik_preset,
                "source_fbx": str(source_path),
                "source_animation_stack": (
                    animation.source_animation_stack
                    or str(retarget_report.get("source_animation_stack", ""))
                ),
                "source_rest_policy": source_rest_policy,
                "source_rest_fbx": str(source_rest_for_clip),
                "target_rig_ref": context.rig_ref,
                "target_rig_path": context.rig_path,
                "target_rig_name": selected_rig_name,
                "target_skeleton_hash": selected_skeleton_hash,
                "retarget_mode": clip_retarget_mode,
                "anm2_sha256": sha256_bytes(payload),
                "anm2_page_count": page_layout.page_count,
                "anm2_page_frame_spans": list(page_layout.page_frame_spans),
                "output_anm2": exported_anm2_value,
                "candidate_anm2_provenance": str(candidate_provenance_path),
                "output_anm2_provenance": (
                    str(exported_provenance_path)
                    if exported_provenance_path is not None
                    else None
                ),
            }
        )
        # The low-level candidate path lives in the temporary work tree. Do not
        # leave a dangling path in a persisted project report when intermediates
        # are intentionally removed.
        if not project.export.write_intermediate_anm2:
            persisted_retarget_report["candidate_path"] = None
            persisted_retarget_report["candidate_anm2_provenance"] = None
        retarget_report_path = retarget_dir / f"{resource_name}.json"
        retarget_report_path.write_text(
            json.dumps(persisted_retarget_report, indent=2, ensure_ascii=False) + "\n",
            encoding="utf-8",
        )

        built = BuiltAnimation(
            animation_id=animation.animation_id,
            source_fbx=str(source_path),
            source_animation_stack=(
                animation.source_animation_stack
                or str(retarget_report.get("source_animation_stack", ""))
            ),
            resource_name=resource_name,
            script_resource=script_resource,
            root_policy=animation.root_policy,
            ik_preset=animation.ik_preset,
            mapping_profile_id=animation.mapping_profile_id,
            target_rig_ref=context.rig_ref,
            target_rig_name=selected_rig_name,
            target_skeleton_hash=selected_skeleton_hash,
            retarget_mode=clip_retarget_mode,
            frame_count=frame_count,
            fps=playback_fps,
            source_fps=source_fps,
            sample_fps=sample_fps,
            playback_fps=playback_fps,
            source_duration_seconds=source_duration_seconds,
            page_count=page_layout.page_count,
            page_frame_spans=list(page_layout.page_frame_spans),
            anm2_path=exported_anm2_value,
            sha256=sha256_bytes(payload),
            retarget_report=str(retarget_report_path),
        )
        built_rows.append(built)
        manifest_rows.append(
            PackResourceManifest(
                resource_name=resource_name,
                script_resource=script_resource,
                source_fbx=str(source_path),
                root_policy=animation.root_policy,
                frame_count=frame_count,
                fps=playback_fps,
                source_fps=source_fps,
                sample_fps=sample_fps,
                playback_fps=playback_fps,
                source_duration_seconds=source_duration_seconds,
                sha256=built.sha256,
                mapping_profile_id=animation.mapping_profile_id,
                ik_preset=animation.ik_preset,
                extensions={
                    "source_rest_policy": source_rest_policy,
                    "source_animation_stack": (
                        animation.source_animation_stack
                        or str(retarget_report.get("source_animation_stack", ""))
                    ),
                    "source_rest_fbx": str(source_rest_for_clip),
                    "target_rig_ref": context.rig_ref,
                    "target_rig_path": context.rig_path,
                    "target_rig_name": selected_rig_name,
                    "target_skeleton_hash": selected_skeleton_hash,
                    "retarget_mode": clip_retarget_mode,
                    "anm2_page_count": page_layout.page_count,
                    "anm2_page_frame_spans": list(page_layout.page_frame_spans),
                },
            )
        )

        if (
            clip_execution_mode == "humanoid"
            and project.export.include_validation_controls
            and not controls_added
        ):
            _merge_validation_controls(
                clip_out=clip_out,
                generated_script_resource=script_resource,
                final_animations=final_animations,
                sequences_by_script=sequences_by_script,
                collision_policy=project.export.collision_policy,
            )
            controls_added = True

    for script_resource, sequences in sequences_by_script.items():
        final_scripts[script_resource] = _merge_script_sequences(
            final_scripts.get(script_resource),
            sequences,
            collision_policy=project.export.collision_policy,
        )

    log(
        f"Writing {len(final_animations)} animations and "
        f"{len(final_scripts)} animation scripts"
    )
    pack_writing_started = time.perf_counter()
    pack_data = build_animation_library_rpack(
        animation_resources=sorted(final_animations.items()),
        animation_scripts={name: final_scripts[name] for name in sorted(final_scripts)},
    )
    _atomic_write_bytes(output_pack, pack_data)
    pack_writing_seconds = time.perf_counter() - pack_writing_started

    all_manifest_rows: list[PackResourceManifest] = []
    if existing_manifest is not None:
        by_name = {
            row.resource_name: row for row in existing_manifest.animation_resources
        }
        for row in manifest_rows:
            by_name[row.resource_name] = row
        all_manifest_rows = list(by_name.values())
    else:
        all_manifest_rows = manifest_rows

    manifest = PackManifest(
        pack_name=output_pack.name,
        pack_sha256=sha256_bytes(pack_data),
        project_id=project.project_id,
        animation_resources=all_manifest_rows,
        animation_scripts=sorted(final_scripts),
        build_mode=project.export.mode,
        extensions={
            "game_id": project.game_id,
            "default_target_rig_ref": project.rig.target_rig_ref,
            "target_rig_refs": sorted(
                {row.target_rig_ref for row in built_rows if row.target_rig_ref}
            ),
            "output_anm2_format": (
                "format 1 compatibility" if project.game_id == DL2_GAME_ID else "format 1"
            ),
            "unmanaged_existing_resources": (
                sorted(set(existing_library.animations) - {row.resource_name for row in all_manifest_rows})
                if existing_manifest is None and existing_library.animations
                else []
            )
        },
    )
    manifest_path = manifest.save_for_pack(output_pack)

    target_rig_groups: list[dict[str, Any]] = []
    grouped: dict[tuple[str, str], list[BuiltAnimation]] = {}
    for row in built_rows:
        grouped.setdefault((row.target_rig_ref, row.target_skeleton_hash), []).append(row)
    for (rig_ref, skeleton_hash), rows in sorted(grouped.items()):
        target_rig_groups.append(
            {
                "target_rig_ref": rig_ref,
                "target_rig_name": rows[0].target_rig_name,
                "target_skeleton_hash": skeleton_hash,
                "retarget_modes": sorted({row.retarget_mode for row in rows}),
                "animation_count": len(rows),
                "animation_ids": [row.animation_id for row in rows],
                "resource_names": [row.resource_name for row in rows],
            }
        )
    built_modes = sorted({row.retarget_mode for row in built_rows})
    effective_project_mode = built_modes[0] if len(built_modes) == 1 else "mixed"
    report = {
        "status": "ok",
        "project_id": project.project_id,
        "project_name": project.name,
        "game_id": project.game_id,
        "selected_game": game_profile.display_name,
        "expected_native_anm2_format": game_profile.anm2_format_label,
        "actual_output_anm2_format": (
            "format 1 compatibility" if project.game_id == DL2_GAME_ID else "format 1"
        ),
        "output_validation_status": (
            "experimental" if project.game_id == DL2_GAME_ID else "validated"
        ),
        "native_dl2_format42_write": False if project.game_id == DL2_GAME_ID else None,
        "target_rig_ref": project.rig.target_rig_ref,
        "default_target_rig_ref": project.rig.target_rig_ref,
        "default_target_rig_path": project.rig.target_rig_path,
        "target_rig_name": target_rig_definition.name if target_rig_definition else "",
        "target_skeleton_hash": (
            target_rig_definition.skeleton_hash if target_rig_definition else ""
        ),
        "retarget_mode": effective_project_mode,
        "target_rig_group_count": len(target_rig_groups),
        "target_rig_groups": target_rig_groups,
        "target_package_coherence": target_package_coherence or None,
        "target_package_coherences": target_package_coherences,
        "solver_selection": (
            solver_selection_rows[0] if len(solver_selection_rows) == 1 else None
        ),
        "solver_selections": solver_selection_rows,
        "source_global_normalization": (
            normalization_rows[0] if len(normalization_rows) == 1 else None
        ),
        "source_global_normalizations": normalization_rows,
        "hierarchy_safety": (
            hierarchy_safety_rows[0] if len(hierarchy_safety_rows) == 1 else None
        ),
        "hierarchy_safety_results": hierarchy_safety_rows,
        "build_mode": project.export.mode,
        "pack_path": str(output_pack),
        "pack_sha256": manifest.pack_sha256,
        "animation_count": len(final_animations),
        "script_count": len(final_scripts),
        "performance": {
            "pack_writing_seconds": round(pack_writing_seconds, 6),
            "total_seconds": round(time.perf_counter() - build_started, 6),
        },
        "animations": [asdict(row) for row in built_rows],
        "animation_scripts": sorted(final_scripts),
        "warnings": warnings,
    }
    report_path = report_dir / "build_report.json"
    report_path.parent.mkdir(parents=True, exist_ok=True)
    report_path.write_text(json.dumps(report, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")

    if not project.export.write_intermediate_anm2:
        shutil.rmtree(work_dir, ignore_errors=True)
    log(f"Build complete: {output_pack}")
    return ProjectBuildResult(
        status="ok",
        pack_path=str(output_pack),
        manifest_path=str(manifest_path),
        report_path=str(report_path),
        build_mode=project.export.mode,
        pack_sha256=manifest.pack_sha256,
        animation_count=len(final_animations),
        script_count=len(final_scripts),
        built_animations=built_rows,
        warnings=warnings,
    )


def _mapping_profile_for_animation(
    project: DlReanimatedProject,
    animation: ProjectAnimation,
    document: _FbxDocument,
) -> SourceBoneMappingProfile:
    if animation.mapping_profile_id:
        payload = project.mapping_profiles.get(animation.mapping_profile_id)
        if payload is None:
            raise ValueError(
                f"Animation {animation.display_name!r} references missing mapping profile "
                f"{animation.mapping_profile_id}"
            )
        return SourceBoneMappingProfile.from_dict(payload)
    profile = auto_map_source_bones(
        document.limb_models,
        parents=document.parent_by_name,
        profile_name=f"Auto map: {animation.display_name}",
    )
    project.mapping_profiles[profile.profile_id] = profile.to_dict()
    animation.mapping_profile_id = profile.profile_id
    return profile


def _project_script_registry(project: DlReanimatedProject) -> ScriptTargetRegistry:
    targets: list[AnimationScriptTarget] = []
    for row in project.user_script_targets:
        targets.append(AnimationScriptTarget(**row))
    return ScriptTargetRegistry(targets)


def _resolve_script_resource(
    project: DlReanimatedProject,
    animation: ProjectAnimation,
    registry: ScriptTargetRegistry,
) -> str:
    value = animation.script_target or project.export.default_script_target
    if value == "custom":
        value = project.export.custom_script_resource
    resolved = registry.resolve_resource_name(value)
    if not resolved:
        resolved = project.export.custom_script_resource.strip()
    if not resolved:
        raise ValueError(
            f"Animation {animation.display_name!r} does not have an animation-script target"
        )
    _validate_resource_name(resolved)
    return resolved


def _final_resource_name(
    project: DlReanimatedProject,
    animation: ProjectAnimation,
) -> str:
    base = _sanitize_resource_name(animation.resource_name)
    prefix = _sanitize_resource_name(project.export.resource_prefix)
    if prefix and not base.startswith(prefix + "_") and base != prefix:
        return f"{prefix}_{base}"
    return base


def _sanitize_resource_name(value: str) -> str:
    normalized = re.sub(r"[^A-Za-z0-9_]+", "_", value.strip()).strip("_")
    normalized = re.sub(r"_+", "_", normalized)
    return normalized.lower()


def _validate_resource_name(name: str) -> None:
    if not name:
        raise ValueError("resource name cannot be empty")
    if len(name) > 120:
        raise ValueError(f"resource name is too long: {name}")
    if not re.fullmatch(r"[A-Za-z0-9_]+", name):
        raise ValueError(
            f"resource name contains unsupported characters: {name!r}"
        )


def _merge_script_sequences(
    existing: tuple[bytes, bytes] | None,
    sequences: list[AnimationScrSequence],
    *,
    collision_policy: str,
) -> tuple[bytes, bytes]:
    if existing is None:
        return build_animation_scr_sections(sequences)
    parsed = parse_animation_scr_sections(existing)
    existing_names = set(parsed.by_name())
    replacements = [row for row in sequences if row.normalized_name in existing_names]
    additions = [row for row in sequences if row.normalized_name not in existing_names]
    updated = existing
    if replacements:
        if collision_policy == "error":
            raise ValueError(
                "Animation script already contains sequence(s): "
                + ", ".join(row.normalized_name for row in replacements)
            )
        updated = patch_animation_scr_sequence_ranges(
            updated,
            {
                row.normalized_name: (row.start_frame, row.end_frame, row.fps)
                for row in replacements
            },
        )
    if additions:
        updated = append_animation_scr_sequences(updated, additions)
    return updated


def _merge_validation_controls(
    *,
    clip_out: Path,
    generated_script_resource: str,
    final_animations: dict[str, bytes],
    sequences_by_script: dict[str, list[AnimationScrSequence]],
    collision_policy: str,
) -> None:
    library = extract_animation_library((clip_out / "common_anims_sp_pc.rpack").read_bytes())
    candidate_names = {
        row["resource_name"]
        for row in json.loads(
            (clip_out / "release_candidate_test_manifest.json").read_text(encoding="utf-8-sig")
        )["resources"]
        if row.get("root_policy")
    }
    parsed = parse_animation_scr_sections(library.animation_scripts[generated_script_resource])
    by_name = parsed.by_name()
    for name, payload in library.animations.items():
        if name in candidate_names:
            continue
        if name in final_animations and collision_policy == "error":
            continue
        final_animations[name] = payload
        sequence = by_name.get(name.lower())
        if sequence is not None:
            sequences_by_script.setdefault(generated_script_resource, []).append(
                AnimationScrSequence(
                    name=name,
                    anm2_name=f"{name}.anm2",
                    start_frame=sequence.start_frame,
                    end_frame=sequence.end_frame,
                    fps=sequence.fps,
                    enabled=sequence.enabled,
                    blend=sequence.blend,
                )
            )


def _validate_generated_anm2_payload(
    payload: bytes,
    *,
    resource_name: str,
) -> anm2.Anm2V1Layout:
    """Reject malformed one-page/oversized ANM2 output before RPack packaging."""

    header = anm2.Anm2Header.parse(payload)
    layout = anm2.probe_v1_layout(header, payload)
    if layout is None:
        raise ValueError(
            f"Generated animation {resource_name!r} is not a supported ANM2 v1 clip"
        )
    if layout.validation_errors:
        details = "; ".join(layout.validation_errors)
        raise ValueError(
            f"Generated animation {resource_name!r} has an invalid ANM2 page layout: {details}"
        )
    expected_span = max(0, header.frame_count - 1)
    if sum(layout.page_frame_spans) != expected_span:
        raise ValueError(
            f"Generated animation {resource_name!r} page spans cover "
            f"{sum(layout.page_frame_spans)} frames, expected {expected_span}"
        )
    return layout


def _atomic_write_bytes(path: Path, data: bytes) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    handle, temp_name = tempfile.mkstemp(prefix=path.name + ".", suffix=".tmp", dir=path.parent)
    try:
        with os.fdopen(handle, "wb") as stream:
            stream.write(data)
            stream.flush()
            os.fsync(stream.fileno())
        os.replace(temp_name, path)
    finally:
        if os.path.exists(temp_name):
            os.unlink(temp_name)


def build_project(
    project: DlReanimatedProject,
    *,
    progress: ProgressCallback | None = None,
) -> ProjectBuildResult:
    """Build body plus optional mimic/root-override resources through one API."""

    from .mimic_project_builder import build_project_with_mimics

    return build_project_with_mimics(
        project,
        progress=progress,
        body_builder=_build_body_project,
    )


__all__ = [
    "BuiltAnimation",
    "ProjectBuildResult",
    "build_project",
    "export_project_anm2_files",
]
