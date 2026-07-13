"""Build versioned GUI projects into new or appended animation RPacks."""

from __future__ import annotations

from copy import deepcopy
from dataclasses import asdict, dataclass, field
import json
import os
from pathlib import Path
import re
import shutil
import tempfile
from typing import Any, Callable

from . import anm2
from .animation_scr import (
    AnimationScrSequence,
    append_animation_scr_sequences,
    build_animation_scr_sections,
    parse_animation_scr_sections,
    patch_animation_scr_sequence_ranges,
)
from .fbx_pipeline import FbxAnimationClip, build_fbx_rpack
from .chrome_rig import ChromeRig
from .chrome_rig_builder import build_chrome_rig_from_smd_template
from .oracle.binary_fbx_mixamo import _FbxDocument
from .pack_manifest import (
    PackManifest,
    PackResourceManifest,
    manifest_path_for_pack,
    sha256_bytes,
)
from .retarget_profiles import SourceBoneMappingProfile, auto_map_source_bones
from .retarget_engines.exact_rig import build_exact_rig_anm2
from .runtime_paths import resource_root
from .rp6l import (
    AnimationLibrary,
    build_animation_library_rpack,
    extract_animation_library,
)
from .script_targets import AnimationScriptTarget, ScriptTargetRegistry
from .workspace_project import DlReanimatedProject, ProjectAnimation
from .fbx_preflight import preflight_fbx
from .game_profiles import DL2_GAME_ID, get_game_profile
from .root_mapping import RootMappingSelection

# DLR_MIMIC_PROTOTYPE_BODY_CORE


ProgressCallback = Callable[[str], None]


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
    frame_count: int
    fps: int
    page_count: int
    page_frame_spans: list[int]
    anm2_path: str
    sha256: str
    retarget_report: str


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
            exported.append(target)
        log(f"ANM2 export complete: {len(exported)} file(s) written to {destination}")
        return exported


def _build_body_project(
    project: DlReanimatedProject,
    *,
    progress: ProgressCallback | None = None,
) -> ProjectBuildResult:
    """Build all enabled project animations and write one tool-owned RPack."""

    log = progress or (lambda _message: None)
    errors = project.validate()
    if errors:
        raise ValueError("Project validation failed:\n- " + "\n- ".join(errors))

    enabled = [row for row in project.animations if row.enabled]
    if not enabled:
        raise ValueError("Project does not contain any enabled animations")

    retarget_mode = project.rig.retarget_mode
    rig_paths: dict[str, Path] = {}
    exact_rig: ChromeRig | None = None
    target_rig_definition: ChromeRig | None = None
    if retarget_mode == "humanoid":
        rig_paths = {
            "canonical_smd": Path(project.rig.canonical_smd),
            "target_template_anm2": Path(project.rig.target_template_anm2),
            "stock_writer_control_anm2": Path(project.rig.stock_writer_control_anm2),
        }
        for label, path in rig_paths.items():
            if not path.is_file():
                raise FileNotFoundError(f"{label} must be a file: {path}")
    elif retarget_mode == "exact":
        rig_path = Path(project.rig.target_rig_path)
        if not rig_path.is_file():
            raise FileNotFoundError(f"target_rig_path must be a .crig file: {rig_path}")
        exact_rig = ChromeRig.load(rig_path)
        target_rig_definition = exact_rig
    else:
        raise ValueError(f"Unsupported retarget mode: {retarget_mode!r}")

    explicit_source_rest = None
    if retarget_mode == "humanoid" and not project.rig.use_imported_animation_bind_pose:
        explicit_source_rest = Path(project.rig.source_rest_fbx)
        if not project.rig.source_rest_fbx.strip() or not explicit_source_rest.is_file():
            raise FileNotFoundError(
                "source_rest_fbx must be a valid FBX file when embedded bind pose is disabled: "
                f"{explicit_source_rest}"
            )

    trusted_path = None
    if (
        retarget_mode == "humanoid"
        and not project.rig.use_imported_animation_bind_pose
        and project.rig.trusted_source_rest_json
    ):
        trusted_path = Path(project.rig.trusted_source_rest_json)
        if not trusted_path.is_file():
            raise FileNotFoundError(
                f"trusted_source_rest_json must be a file: {trusted_path}"
            )

    if retarget_mode == "humanoid":
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

    registry = _project_script_registry(project)
    existing_library = AnimationLibrary({}, {})
    warnings: list[str] = []
    if retarget_mode == "exact" and project.export.include_validation_controls:
        warnings.append(
            "Bundled humanoid writer/bind controls are not compatible with custom Chrome "
            "Rigs and were omitted. Exact-rig output is decoded and sampled during validation."
        )
    existing_manifest: PackManifest | None = None
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

    final_animations = dict(existing_library.animations)
    final_scripts = dict(existing_library.animation_scripts)
    sequences_by_script: dict[str, list[AnimationScrSequence]] = {}
    built_rows: list[BuiltAnimation] = []
    manifest_rows: list[PackResourceManifest] = []
    controls_added = False

    for index, animation in enumerate(enabled, start=1):
        source_path = Path(animation.source_fbx)
        if not source_path.is_file():
            raise FileNotFoundError(f"Animation FBX must be a file: {source_path}")
        log(f"[{index}/{len(enabled)}] Reading skeleton: {source_path.name}")
        preflight = None
        if source_path.read_bytes()[:18] == b"Kaydara FBX Binary":
            preflight = preflight_fbx(
                source_path, purpose="animation",
                animation_stack=animation.source_animation_stack or None,
                game_id=project.game_id,
            )
            preflight.require_buildable()
        profile: SourceBoneMappingProfile | None = None
        if retarget_mode == "humanoid":
            document = _FbxDocument(source_path)
            if animation.source_animation_stack:
                document.select_animation_stack(animation.source_animation_stack)
            profile = _mapping_profile_for_animation(project, animation, document)
            mapping_errors = profile.validate(document.limb_models)
            if mapping_errors:
                raise ValueError(
                    f"Retarget mapping for {animation.display_name!r} is incomplete:\n- "
                    + "\n- ".join(mapping_errors)
                )

        script_resource = _resolve_script_resource(project, animation, registry)
        resource_name = _final_resource_name(project, animation)
        _validate_resource_name(resource_name)
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
        if retarget_mode == "exact":
            assert exact_rig is not None
            source_rest_for_clip = source_path
            source_rest_policy = "exact_same_rig"
            clip_out.mkdir(parents=True, exist_ok=True)
            exact_build = build_exact_rig_anm2(
                source_path,
                exact_rig,
                fps=animation.fps,
                animation_stack=animation.source_animation_stack or None,
                root_mapping=RootMappingSelection.from_animation(animation),
                root_policy=animation.root_policy,
            )
            payload = exact_build.payload
            candidate_path = clip_out / f"{resource_name}.anm2"
            candidate_path.write_bytes(payload)
            retarget_report = dict(exact_build.report)
            retarget_report["candidate_path"] = str(candidate_path)
            retarget_report["requested_project_root_policy"] = animation.root_policy
        else:
            assert profile is not None
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
            )
            reports = json.loads(
                (clip_out / "retarget_candidate_summary.json").read_text(encoding="utf-8")
            )
            if len(reports) != 1:
                raise ValueError(
                    f"Expected one retarget candidate for {animation.display_name}, got {len(reports)}"
                )
            retarget_report = reports[0]
            candidate_path = Path(retarget_report["candidate_path"])
            payload = candidate_path.read_bytes()
        retarget_report["game_id"] = project.game_id
        if preflight is not None:
            retarget_report["fbx_preflight"] = preflight.to_dict()
        retarget_report["output_anm2_format"] = (
            "format 1 compatibility" if project.game_id == DL2_GAME_ID else "format 1"
        )
        retarget_report["output_validation_status"] = (
            "experimental" if project.game_id == DL2_GAME_ID else "validated"
        )
        for message in retarget_report.get("warnings", []):
            rendered = f"{animation.display_name}: {message}"
            warnings.append(rendered)
            log(f"WARNING: {rendered}")
        page_layout = _validate_generated_anm2_payload(
            payload,
            resource_name=resource_name,
        )
        final_animations[resource_name] = payload

        frame_count = int(retarget_report["frame_count"])
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
            fps=float(animation.fps),
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
            exported_anm2_value = str(exported_anm2)
        else:
            exported_anm2_value = (
                f"rpack:{output_pack.name}#_ANIMATION_/{resource_name}"
            )

        persisted_retarget_report = dict(retarget_report)
        persisted_retarget_report.update(
            {
                "resource_name": resource_name,
                "script_resource": script_resource,
                "mapping_profile_id": profile.profile_id if profile is not None else "",
                "root_policy": animation.root_policy,
                "ik_preset": animation.ik_preset,
                "source_fbx": str(source_path),
                "source_animation_stack": (
                    animation.source_animation_stack
                    or str(retarget_report.get("source_animation_stack", ""))
                ),
                "source_rest_policy": source_rest_policy,
                "source_rest_fbx": str(source_rest_for_clip),
                "target_rig_ref": project.rig.target_rig_ref,
                "target_rig_name": target_rig_definition.name if target_rig_definition else "",
                "target_skeleton_hash": (
                    target_rig_definition.skeleton_hash if target_rig_definition else ""
                ),
                "retarget_mode": retarget_mode,
                "anm2_sha256": sha256_bytes(payload),
                "anm2_page_count": page_layout.page_count,
                "anm2_page_frame_spans": list(page_layout.page_frame_spans),
                "output_anm2": exported_anm2_value,
            }
        )
        # The low-level candidate path lives in the temporary work tree. Do not
        # leave a dangling path in a persisted project report when intermediates
        # are intentionally removed.
        if not project.export.write_intermediate_anm2:
            persisted_retarget_report["candidate_path"] = None
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
            mapping_profile_id=profile.profile_id if profile is not None else "",
            frame_count=frame_count,
            fps=animation.fps,
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
                fps=animation.fps,
                sha256=built.sha256,
                mapping_profile_id=profile.profile_id if profile is not None else "",
                ik_preset=animation.ik_preset,
                extensions={
                    "source_rest_policy": source_rest_policy,
                    "source_animation_stack": (
                        animation.source_animation_stack
                        or str(retarget_report.get("source_animation_stack", ""))
                    ),
                    "source_rest_fbx": str(source_rest_for_clip),
                    "target_rig_ref": project.rig.target_rig_ref,
                    "retarget_mode": retarget_mode,
                    "anm2_page_count": page_layout.page_count,
                    "anm2_page_frame_spans": list(page_layout.page_frame_spans),
                },
            )
        )

        if (
            retarget_mode == "humanoid"
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
    pack_data = build_animation_library_rpack(
        animation_resources=sorted(final_animations.items()),
        animation_scripts={name: final_scripts[name] for name in sorted(final_scripts)},
    )
    _atomic_write_bytes(output_pack, pack_data)

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

    game_profile = get_game_profile(project.game_id)
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
        "target_rig_name": target_rig_definition.name if target_rig_definition else "",
        "target_skeleton_hash": (
            target_rig_definition.skeleton_hash if target_rig_definition else ""
        ),
        "retarget_mode": retarget_mode,
        "build_mode": project.export.mode,
        "pack_path": str(output_pack),
        "pack_sha256": manifest.pack_sha256,
        "animation_count": len(final_animations),
        "script_count": len(final_scripts),
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
            (clip_out / "release_candidate_test_manifest.json").read_text(encoding="utf-8")
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
