"""Project-builder wrapper adding separate mimic ANM2 resources and root overrides.

The existing body builder remains the authority for skeletal retargeting. This
wrapper plans body/mimic content per clip, delegates body work unchanged, then
adds morph-only resources and optional advanced root-translation overrides.
"""

from __future__ import annotations

from copy import deepcopy
from dataclasses import asdict
import json
from pathlib import Path
from typing import Any, Callable

from .animation_scr import AnimationScrSequence
from .anm2_provenance import build_anm2_provenance, write_anm2_provenance
from .fbx_blendshapes import FbxFacialScan, scan_fbx_blendshapes
from .mimic_builder import build_mimic_anm2
from .mimic_profiles import (
    MimicMappingRow,
    auto_map_shapes,
    mapping_from_payload,
    resolve_mimic_profile,
)
from .pack_manifest import (
    PackManifest,
    PackResourceManifest,
    manifest_path_for_pack,
    sha256_bytes,
)
from .root_motion_override import apply_root_motion_source_override
from .rp6l import AnimationLibrary, build_animation_library_rpack, extract_animation_library


_CONTENT_MODES = {"auto", "body_only", "mimic_only", "both"}


def _mimic_settings(animation: Any) -> dict[str, Any]:
    extensions = animation.extensions
    raw = extensions.get("mimic")
    if not isinstance(raw, dict):
        raw = {}
        extensions["mimic"] = raw
    raw.setdefault("mode", "auto")
    raw.setdefault("resource_name", "")
    raw.setdefault("mapping", [])
    raw.setdefault("clamp_mode", "none")
    return raw


def _model_facial_policy(project: Any) -> str:
    value = str((project.rig.extensions or {}).get("facial_animation_policy", "auto"))
    return value if value in {"auto", "yes", "no"} else "auto"


def _animation_stack(animation: Any) -> str:
    return str(getattr(animation, "source_animation_stack", "") or "")


def _scan_animation(animation: Any) -> FbxFacialScan:
    return scan_fbx_blendshapes(
        animation.source_fbx,
        fps=animation.resolved_sample_fps(),
        animation_stack=_animation_stack(animation) or None,
    )


def _effective_mode(
    project: Any,
    animation: Any,
    scan: FbxFacialScan,
    *,
    profile_available: bool,
) -> str:
    requested = str(_mimic_settings(animation).get("mode", "auto"))
    if requested not in _CONTENT_MODES:
        requested = "auto"
    model_policy = _model_facial_policy(project)
    if model_policy == "no":
        return "body_only"
    if requested == "auto":
        return "both" if profile_available and scan.has_facial_animation else "body_only"
    return requested


def _copy_mapping_state(source_project: Any, body_project: Any) -> None:
    source_project.mapping_profiles = deepcopy(body_project.mapping_profiles)
    for source_row in source_project.animations:
        body_row = body_project.animation_by_id(source_row.animation_id)
        if body_row is None:
            continue
        source_row.mapping_profile_id = body_row.mapping_profile_id
        source_row.source_fps = body_row.source_fps
        source_row.source_root_bone = body_row.source_root_bone
        source_row.target_root_bone = body_row.target_root_bone
        source_row.ik_preset = body_row.ik_preset
        source_row.extensions = deepcopy(body_row.extensions)


def _empty_or_append_library(project: Any, pb: Any, log: Callable[[str], None]):
    existing_library = AnimationLibrary({}, {})
    existing_manifest: PackManifest | None = None
    if project.export.mode == "append":
        existing_path = Path(project.export.existing_rpack)
        if not existing_path.is_file():
            raise FileNotFoundError(f"Existing RPack must be a file: {existing_path}")
        log(f"Loading existing tool RPack: {existing_path}")
        existing_library = extract_animation_library(existing_path.read_bytes())
        existing_manifest = PackManifest.load_for_pack(existing_path)
        if existing_manifest is not None and not existing_manifest.verify_pack_hash(existing_path):
            raise ValueError(
                "Existing RPack no longer matches its DL ReAnimated manifest. "
                "Refusing append to avoid overwriting unknown changes."
            )
    return existing_library, existing_manifest


def _mimic_resource_name(project: Any, animation: Any, pb: Any) -> str:
    settings = _mimic_settings(animation)
    explicit = str(settings.get("resource_name", "")).strip()
    body_name = pb._final_resource_name(project, animation)
    if not explicit:
        return body_name + "_mimic"
    value = pb._sanitize_resource_name(explicit)
    prefix = pb._sanitize_resource_name(project.export.resource_prefix)
    if prefix and not value.startswith(prefix + "_") and value != prefix:
        value = f"{prefix}_{value}"
    pb._validate_resource_name(value)
    return value


def _upsert_manifest(rows: list[PackResourceManifest], replacement: PackResourceManifest) -> None:
    for index, row in enumerate(rows):
        if row.resource_name == replacement.resource_name:
            rows[index] = replacement
            return
    rows.append(replacement)


def _update_body_manifest_hash(
    rows: list[PackResourceManifest],
    resource_name: str,
    payload: bytes,
    root_report: dict[str, Any],
) -> None:
    for row in rows:
        if row.resource_name != resource_name:
            continue
        row.sha256 = sha256_bytes(payload)
        row.extensions = dict(row.extensions)
        row.extensions["root_motion_source_override"] = root_report
        return


def _report_root_override(built: Any, root_report: dict[str, Any]) -> None:
    path = Path(built.retarget_report)
    if not path.is_file():
        return
    payload = json.loads(path.read_text(encoding="utf-8-sig"))
    payload["root_motion_source_override"] = root_report
    payload["anm2_sha256"] = built.sha256
    payload["anm2_page_count"] = built.page_count
    payload["anm2_page_frame_spans"] = list(built.page_frame_spans)
    path.write_text(json.dumps(payload, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")


def _make_built_animation(pb: Any, **values: Any) -> Any:
    """Construct the host build row while tolerating adjacent alpha schemas."""

    fields = getattr(pb.BuiltAnimation, "__dataclass_fields__", {})
    filtered = {key: value for key, value in values.items() if key in fields}
    return pb.BuiltAnimation(**filtered)


def build_project_with_mimics(
    project: Any,
    *,
    progress: Callable[[str], None] | None,
    body_builder: Callable[..., Any],
):
    """Build a project with per-clip Body/Face content selection."""

    from . import project_builder as pb  # Imported lazily after project_builder is initialized.

    log = progress or (lambda _message: None)
    errors = project.validate()
    if errors:
        raise ValueError("Project validation failed:\n- " + "\n- ".join(errors))
    enabled = [row for row in project.animations if row.enabled]
    if not enabled:
        raise ValueError("Project does not contain any enabled animations")

    profile = resolve_mimic_profile(project)
    scans: dict[str, FbxFacialScan] = {}
    effective_modes: dict[str, str] = {}
    warnings: list[str] = []
    for animation in enabled:
        try:
            scan = _scan_animation(animation)
        except Exception as exc:
            scan = FbxFacialScan(
                source_path=str(animation.source_fbx),
                animation_stack=_animation_stack(animation),
                fps=animation.resolved_sample_fps(),
                frame_count=1,
                curves=(),
                warnings=(f"Facial detection failed: {exc}",),
            )
        scans[animation.animation_id] = scan
        if scan.source_fps is not None:
            animation.source_fps = float(scan.source_fps)
        mode = _effective_mode(project, animation, scan, profile_available=profile is not None)
        effective_modes[animation.animation_id] = mode
        settings = _mimic_settings(animation)
        settings["last_detection"] = scan.summary()
        settings["effective_mode"] = mode
        if mode in {"mimic_only", "both"} and profile is None:
            raise ValueError(
                f"{animation.display_name}: facial export was requested but the target rig has no mimic profile"
            )
        if mode in {"mimic_only", "both"} and not scan.has_facial_animation:
            warnings.append(
                f"{animation.display_name}: facial export was requested, but no changing FBX "
                "BlendShapeChannel was found; no mimic resource will be written."
            )

    body_project = deepcopy(project)
    for row in body_project.animations:
        row.enabled = row.enabled and effective_modes.get(row.animation_id, "body_only") in {"body_only", "both"}
    has_body = any(row.enabled for row in body_project.animations)

    if has_body:
        body_result = body_builder(body_project, progress=progress)
        _copy_mapping_state(project, body_project)
        output_pack = Path(body_result.pack_path)
        library = extract_animation_library(output_pack.read_bytes())
        manifest = PackManifest.load_for_pack(output_pack)
        manifest_rows = list(manifest.animation_resources if manifest else [])
        built_rows = list(body_result.built_animations)
        warnings = [*body_result.warnings, *warnings]
        report_path = Path(body_result.report_path)
        if report_path.is_file():
            report = json.loads(report_path.read_text(encoding="utf-8-sig"))
        else:
            report = {}
        existing_manifest = manifest
    else:
        output_dir = Path(project.export.output_directory)
        output_dir.mkdir(parents=True, exist_ok=True)
        output_pack = output_dir / project.export.pack_filename
        existing_library, existing_manifest = _empty_or_append_library(project, pb, log)
        library = AnimationLibrary(
            dict(existing_library.animations),
            dict(existing_library.animation_scripts),
        )
        manifest_rows = list(existing_manifest.animation_resources if existing_manifest else [])
        built_rows = []
        report_path = output_dir / "dl_reanimated_build" / "build_report.json"
        report = {
            "status": "ok",
            "project_id": project.project_id,
            "project_name": project.name,
            "target_rig_ref": str(getattr(project.rig, "target_rig_ref", "builtin:male_npc_infected")),
            "target_rig_name": str(getattr(project.rig, "target_rig_name", "Dying Light male humanoid")),
            "retarget_mode": str(getattr(project.rig, "retarget_mode", "humanoid")),
            "build_mode": project.export.mode,
            "animations": [],
            "animation_scripts": [],
            "warnings": [],
        }

    registry = pb._project_script_registry(project)
    report_dir = Path(project.export.output_directory) / "dl_reanimated_build"
    animation_dir = report_dir / "animations"
    retarget_dir = report_dir / "retarget_reports"
    animation_dir.mkdir(parents=True, exist_ok=True)
    retarget_dir.mkdir(parents=True, exist_ok=True)

    # Advanced translation-source override is applied after the validated body build.
    for animation in enabled:
        mode = effective_modes[animation.animation_id]
        if mode not in {"body_only", "both"} or animation.root_policy not in {"bip01", "motion"}:
            continue
        source_bone = str(animation.extensions.get("root_motion_source_bone", "") or "").strip()
        if not source_bone:
            continue
        resource_name = pb._final_resource_name(project, animation)
        body_payload = library.animations.get(resource_name)
        if body_payload is None:
            continue
        body_row = body_project.animation_by_id(animation.animation_id)
        source_aliases = None
        if body_row is not None and body_row.mapping_profile_id:
            profile_payload = body_project.mapping_profiles.get(body_row.mapping_profile_id)
            if profile_payload:
                from .retarget_profiles import SourceBoneMappingProfile

                source_aliases = SourceBoneMappingProfile.from_dict(profile_payload).canonical_aliases()
        source_rest = (
            animation.source_fbx
            if project.rig.use_imported_animation_bind_pose
            else project.rig.source_rest_fbx
        )
        log(f"Applying motion source override for {animation.display_name}: {source_bone}")
        body_payload, root_report = apply_root_motion_source_override(
            body_payload,
            animation_fbx=animation.source_fbx,
            source_rest_fbx=source_rest,
            canonical_smd=project.rig.canonical_smd,
            source_bone=source_bone,
            root_policy=animation.root_policy,
            fps=animation.resolved_sample_fps(),
            animation_stack=_animation_stack(animation) or None,
            source_bone_aliases=source_aliases,
        )
        layout = pb._validate_generated_anm2_payload(body_payload, resource_name=resource_name)
        library.animations[resource_name] = body_payload
        built = next((row for row in built_rows if row.resource_name == resource_name), None)
        if built is not None:
            built.sha256 = sha256_bytes(body_payload)
            built.page_count = layout.page_count
            built.page_frame_spans = list(layout.page_frame_spans)
            if project.export.write_intermediate_anm2:
                intermediate = animation_dir / f"{resource_name}.anm2"
                intermediate.write_bytes(body_payload)
                motion_mode, heading_mode = pb._provenance_root_modes(animation)
                write_anm2_provenance(
                    intermediate,
                    build_anm2_provenance(
                        body_payload,
                        source_fbx=Path(animation.source_fbx).name,
                        source_fbx_sha256=sha256_bytes(
                            Path(animation.source_fbx).read_bytes()
                        ),
                        source_fbx_fps=float(built.source_fps),
                        sample_fps=float(built.sample_fps),
                        playback_fps=float(built.playback_fps),
                        source_duration_seconds=float(
                            built.source_duration_seconds
                        ),
                        frame_count=int(built.frame_count),
                        root_motion_mode=motion_mode,
                        root_heading_mode=heading_mode,
                        source_animation_stack=_animation_stack(animation),
                    ),
                )
                built.anm2_path = str(intermediate)
            _report_root_override(built, root_report)
        _update_body_manifest_hash(manifest_rows, resource_name, body_payload, root_report)

    mimic_sequences: dict[str, list[AnimationScrSequence]] = {}
    mimic_reports: list[dict[str, Any]] = []
    if profile is not None:
        for animation in enabled:
            mode = effective_modes[animation.animation_id]
            scan = scans[animation.animation_id]
            if mode not in {"mimic_only", "both"} or not scan.has_facial_animation:
                continue
            settings = _mimic_settings(animation)
            configured_mapping = settings.get("mapping")
            if configured_mapping:
                mapping = mapping_from_payload(configured_mapping)
            else:
                mapping = auto_map_shapes(scan.animated_shape_names, profile)
                settings["mapping"] = [row.to_dict() for row in mapping]
            build = build_mimic_anm2(
                scan,
                profile,
                mapping=mapping,
                clamp_mode=str(settings.get("clamp_mode", "none")),
            )
            sample_fps = float(animation.resolved_sample_fps())
            playback_fps = float(animation.resolved_playback_fps())
            body_timing = next(
                (
                    row
                    for row in built_rows
                    if row.animation_id == animation.animation_id
                ),
                None,
            )
            source_fps = float(
                scan.source_fps
                or getattr(body_timing, "source_fps", None)
                or animation.source_fps
                or sample_fps
            )
            source_duration_seconds = scan.source_duration_seconds
            if source_duration_seconds is None and body_timing is not None:
                source_duration_seconds = float(body_timing.source_duration_seconds)
            if source_duration_seconds is None:
                # Compatibility for adjacent-version facial scan providers
                # that do not expose the exact selected FBX tick span.
                source_duration_seconds = (
                    float(max(0, scan.frame_count - 1)) / sample_fps
                )
            source_duration_seconds = float(source_duration_seconds)
            animation.source_fps = source_fps
            resource_name = _mimic_resource_name(project, animation, pb)
            if resource_name in library.animations and project.export.collision_policy == "error":
                raise ValueError(
                    f"Mimic animation resource already exists in output library: {resource_name}. "
                    "Choose Replace collisions or rename the facial resource."
                )
            pb._validate_generated_anm2_payload(build.payload, resource_name=resource_name)
            library.animations[resource_name] = build.payload
            script_resource = pb._resolve_script_resource(project, animation, registry)
            start_frame = 0 if animation.start_frame is None else int(animation.start_frame)
            end_frame = build.frame_count - 1 if animation.end_frame is None else int(animation.end_frame)
            if start_frame < 0 or end_frame < start_frame or end_frame >= build.frame_count:
                raise ValueError(
                    f"Invalid facial frame range for {animation.display_name}: "
                    f"{start_frame}..{end_frame}, clip has {build.frame_count} frames"
                )
            mimic_sequences.setdefault(script_resource, []).append(AnimationScrSequence(
                name=resource_name,
                anm2_name=f"{resource_name}.anm2",
                start_frame=float(start_frame),
                end_frame=float(end_frame),
                fps=float(animation.resolved_playback_fps()),
                enabled=1,
                blend=0.5,
            ))
            intermediate_path = animation_dir / f"{resource_name}.anm2"
            mimic_provenance = build_anm2_provenance(
                build.payload,
                source_fbx=Path(animation.source_fbx).name,
                source_fbx_sha256=sha256_bytes(
                    Path(animation.source_fbx).read_bytes()
                ),
                source_fbx_fps=source_fps,
                sample_fps=sample_fps,
                playback_fps=playback_fps,
                source_duration_seconds=source_duration_seconds,
                frame_count=build.frame_count,
                root_motion_mode="mimic",
                root_heading_mode="not_applicable",
                source_animation_stack=_animation_stack(animation),
            )
            mimic_report = dict(build.report)
            mimic_report.update({
                "resource_name": resource_name,
                "script_resource": script_resource,
                "source_animation_id": animation.animation_id,
                "body_resource_name": pb._final_resource_name(project, animation),
                "content_mode": mode,
                "anm2_sha256": sha256_bytes(build.payload),
                "timing": {
                    "source_fps": source_fps,
                    "sample_fps": sample_fps,
                    "playback_fps": playback_fps,
                    "source_duration_seconds": source_duration_seconds,
                },
                "output_anm2": (
                    str(intermediate_path)
                    if project.export.write_intermediate_anm2
                    else f"rpack:{output_pack.name}#_ANIMATION_/{resource_name}"
                ),
            })
            mimic_report_path = retarget_dir / f"{resource_name}.json"
            mimic_report_path.write_text(json.dumps(mimic_report, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")
            if project.export.write_intermediate_anm2:
                intermediate_path.write_bytes(build.payload)
                write_anm2_provenance(intermediate_path, mimic_provenance)
            layout = pb._validate_generated_anm2_payload(build.payload, resource_name=resource_name)
            built = _make_built_animation(pb,
                animation_id=animation.animation_id,
                source_fbx=animation.source_fbx,
                source_animation_stack=_animation_stack(animation),
                resource_name=resource_name,
                script_resource=script_resource,
                root_policy="mimic",
                ik_preset="facial_morphs",
                mapping_profile_id=profile.profile_id,
                frame_count=build.frame_count,
                fps=playback_fps,
                source_fps=source_fps,
                sample_fps=sample_fps,
                playback_fps=playback_fps,
                source_duration_seconds=source_duration_seconds,
                target_rig_ref=str(getattr(project.rig, "target_rig_ref", "")),
                target_rig_name=str(getattr(project.rig, "target_rig_name", "")),
                target_skeleton_hash="",
                retarget_mode="mimic",
                page_count=layout.page_count,
                page_frame_spans=list(layout.page_frame_spans),
                anm2_path=mimic_report["output_anm2"],
                sha256=sha256_bytes(build.payload),
                retarget_report=str(mimic_report_path),
            )
            built_rows.append(built)
            _upsert_manifest(manifest_rows, PackResourceManifest(
                resource_name=resource_name,
                script_resource=script_resource,
                source_fbx=animation.source_fbx,
                root_policy="mimic",
                frame_count=build.frame_count,
                fps=playback_fps,
                source_fps=source_fps,
                sample_fps=sample_fps,
                playback_fps=playback_fps,
                source_duration_seconds=source_duration_seconds,
                sha256=built.sha256,
                mapping_profile_id=profile.profile_id,
                ik_preset="facial_morphs",
                extensions={
                    "resource_kind": "mimic",
                    "body_resource_name": pb._final_resource_name(project, animation),
                    "content_mode": mode,
                    "source_animation_stack": _animation_stack(animation),
                    "captured_source_activity_ratio": build.report["captured_source_activity_ratio"],
                    "unmapped_animated_shapes": build.report["unmapped_animated_shapes"],
                    "anm2_page_count": layout.page_count,
                    "anm2_page_frame_spans": list(layout.page_frame_spans),
                },
            ))
            mimic_reports.append(mimic_report)
            log(
                f"Facial: {animation.display_name} -> {resource_name} "
                f"({build.report['mapped_source_shape_count']}/"
                f"{build.report['animated_source_shape_count']} animated shapes mapped)"
            )
            for message in build.report.get("warnings", []):
                warnings.append(f"{animation.display_name} facial: {message}")

    for script_resource, sequences in mimic_sequences.items():
        library.animation_scripts[script_resource] = pb._merge_script_sequences(
            library.animation_scripts.get(script_resource),
            sequences,
            collision_policy=project.export.collision_policy,
        )

    if not library.animations:
        raise ValueError("No body or facial animation resource was generated")
    if not library.animation_scripts:
        raise ValueError("No animation-script resource was generated")

    pack_data = build_animation_library_rpack(
        animation_resources=sorted(library.animations.items()),
        animation_scripts={name: library.animation_scripts[name] for name in sorted(library.animation_scripts)},
    )
    pb._atomic_write_bytes(output_pack, pack_data)
    manifest_extensions = dict(existing_manifest.extensions if existing_manifest else {})
    manifest_extensions.update({
        "mimic_support": "local_prototype_v1",
        "mimic_profile_id": profile.profile_id if profile else "",
        "unmanaged_existing_resources": manifest_extensions.get("unmanaged_existing_resources", []),
    })
    final_manifest = PackManifest(
        pack_name=output_pack.name,
        pack_sha256=sha256_bytes(pack_data),
        project_id=project.project_id,
        animation_resources=manifest_rows,
        animation_scripts=sorted(library.animation_scripts),
        build_mode=project.export.mode,
        extensions=manifest_extensions,
    )
    manifest_path = final_manifest.save_for_pack(output_pack)

    report.update({
        "status": "ok",
        "pack_path": str(output_pack),
        "pack_sha256": final_manifest.pack_sha256,
        "animation_count": len(library.animations),
        "script_count": len(library.animation_scripts),
        "animations": [asdict(row) for row in built_rows],
        "animation_scripts": sorted(library.animation_scripts),
        "warnings": warnings,
        "mimic_prototype": {
            "profile_id": profile.profile_id if profile else "",
            "resource_count": len(mimic_reports),
            "resources": mimic_reports,
            "model_facial_policy": _model_facial_policy(project),
            "content_modes": effective_modes,
        },
    })
    report_path.parent.mkdir(parents=True, exist_ok=True)
    report_path.write_text(json.dumps(report, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")
    log(f"Build complete: {output_pack}")
    return pb.ProjectBuildResult(
        status="ok",
        pack_path=str(output_pack),
        manifest_path=str(manifest_path),
        report_path=str(report_path),
        build_mode=project.export.mode,
        pack_sha256=final_manifest.pack_sha256,
        animation_count=len(library.animations),
        script_count=len(library.animation_scripts),
        built_animations=built_rows,
        warnings=warnings,
    )


__all__ = ["build_project_with_mimics"]
