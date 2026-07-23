"""DLR 0.5.0 compatible-rig FBX to ANM2 sampling.

Version 0.5.0 did not copy FBX locals into ANM2 tracks.  It sampled normalized
source globals, corrected them into the target bind basis, then rebuilt target
locals while retaining the target rig's authored translations and scales.
Keep that contract isolated from the modern mapped-rig pipeline.
"""

from __future__ import annotations

import math
from pathlib import Path
from typing import Any, Callable, Mapping

import numpy as np

from ..anm2_components import decode_samples
from ..anm2_writer import build_payload_from_values
from ..chrome_rig_builder import decompose_local_matrix
from ..fbx_anm2_export_behavior import LEGACY_5_0
from ..fbx_core import FBX_TICKS_PER_SECOND, FbxDocument
from ..fbx_preflight import (
    FbxPreflightReport,
    classify_target_compatibility,
    preflight_fbx,
)
from ..model_importer.fbx_model import FBX_Y_UP_TO_DYING_LIGHT
from ..oracle.smd_bind_pose import anm2_cayley_vector_from_quaternion
from ..root_mapping import RootMappingSelection, choose_hierarchy_root, resolve_source_root
from ..root_motion import RootMotionSelection, resolve_root_motion_selection
from .base import RetargetBuild
from .mapped_rig import (
    SourceGlobalNormalization,
    _orthonormal_rotation,
    _target_uses_dying_light_basis,
    compose_local_matrix,
    corrected_target_global,
    global_bind_basis_correction,
    target_bind_local_matrix,
    validate_hierarchy_safety,
)


_SUPPORTED_COMPATIBILITY = {
    "exact_identity",
    "exact_target_subset",
    "target_compatible_source_superset",
}
_SAMPLER_CONTRACT = "dlr_0_5_0_global_bind_basis_v1"
_MOTION_ACCUMULATOR_DESCRIPTOR = 0xCCC3CDDF


def _selected_stack_name(document: Any) -> str:
    stack = getattr(document, "selected_animation_stack", None)
    return str(getattr(stack, "name", "") or "")


def _select_stack(document: Any, requested: str | None) -> None:
    selected = _selected_stack_name(document)
    if (requested and selected != requested) or (
        not requested
        and len(getattr(document, "animation_stacks", ())) > 1
        and getattr(document, "selected_animation_stack", None) is None
    ):
        document.select_animation_stack(requested)


def _historical_document_view(
    source_document: Any,
    *,
    animation_stack: str | None,
) -> Any:
    """Re-evaluate one parsed scene with the 0.5.0 wrapper policy.

    Synthetic test/custom document adapters have no parsed ``scene``.  Those
    are already responsible for presenting the requested transform view.
    """

    scene = getattr(source_document, "scene", None)
    if scene is None:
        return source_document
    return FbxDocument.from_scene(
        scene,
        animation_stack=animation_stack or _selected_stack_name(source_document) or None,
        orientation_policy=str(
            getattr(source_document, "requested_orientation_policy", "auto")
            or "auto"
        ),
        purpose=getattr(source_document, "load_purpose", "animation"),
        tolerance=getattr(source_document, "import_tolerance", "recommended"),
        wrapper_sampling_policy=FbxDocument.LEGACY_5_0_WRAPPER_SAMPLING,
    )


def _root_selection(
    value: RootMappingSelection | Mapping[str, Any] | None,
) -> RootMappingSelection:
    if isinstance(value, RootMappingSelection):
        return value
    payload = dict(value or {})
    return RootMappingSelection(
        source_bone=str(payload.get("source_bone", "") or ""),
        target_bone=str(payload.get("target_bone", "") or ""),
    )


def _legacy_wrapper_details(
    document: Any,
    source_root_name: str,
) -> tuple[float, bool, str, list[list[float]] | None]:
    """Return the wrapper scale cancellation and retained axis classification."""

    scale_factor = 1.0
    if hasattr(document, "wrapper_scale_normalization_factor"):
        scale_factor = float(
            document.wrapper_scale_normalization_factor(source_root_name)
        )
    elif hasattr(document, "_scene_scale_normalizer"):
        adjustment = np.asarray(
            document._scene_scale_normalizer(
                document.limb_models[source_root_name]
            ),
            dtype=float,
        )
        scales = np.linalg.norm(adjustment[:3, :3], axis=0)
        if (
            np.isfinite(scales).all()
            and min(scales) > 1.0e-12
            and max(scales) - min(scales) <= 1.0e-6
        ):
            scale_factor = float(np.mean(scales))

    scene = getattr(document, "scene", None)
    if scene is None:
        retained = bool(
            getattr(document, "legacy_wrapper_axis_conversion", False)
        )
        return scale_factor, retained, "", None

    bone_id = int(document.limb_models[source_root_name])
    wrapper_id = (
        document._wrapper_id_for_bone(bone_id)
        if hasattr(document, "_wrapper_id_for_bone")
        else None
    )
    if wrapper_id is None:
        return scale_factor, False, "", None
    wrapper = np.asarray(scene.model_global_matrix(wrapper_id), dtype=float)
    scales = np.linalg.norm(wrapper[:3, :3], axis=0)
    if (
        not np.isfinite(wrapper).all()
        or not np.isfinite(scales).all()
        or min(scales) <= 1.0e-12
    ):
        return scale_factor, False, str(scene.model_names.get(wrapper_id, "")), None
    rotation = wrapper[:3, :3] / scales
    retained_axis = bool(
        np.allclose(
            rotation,
            FBX_Y_UP_TO_DYING_LIGHT[:3, :3],
            atol=1.0e-5,
            rtol=1.0e-5,
        )
    )
    return (
        scale_factor,
        retained_axis,
        str(scene.model_names.get(wrapper_id, "")),
        wrapper.tolist(),
    )


def _target_bind_globals(rig: Any) -> tuple[dict[str, np.ndarray], dict[str, np.ndarray]]:
    locals_by_name = {
        bone.name: target_bind_local_matrix(bone) for bone in rig.bones
    }
    globals_by_name: dict[str, np.ndarray] = {}
    visiting: set[int] = set()

    def resolve(index: int) -> np.ndarray:
        bone = rig.bones[index]
        if bone.name in globals_by_name:
            return globals_by_name[bone.name]
        if index in visiting:
            raise ValueError(
                f"Target rig hierarchy contains a cycle at {bone.name!r}"
            )
        visiting.add(index)
        local = locals_by_name[bone.name]
        value = (
            resolve(bone.parent_index) @ local
            if bone.parent_index >= 0
            else local.copy()
        )
        visiting.remove(index)
        globals_by_name[bone.name] = value
        return value

    for bone in rig.bones:
        resolve(bone.index)
    return locals_by_name, globals_by_name


def _legacy_root_policy(
    values: list[list[list[float]]],
    rig: Any,
    target_root_name: str,
    policy: str,
) -> None:
    """The literal root policy implementation shipped in tag 0.5.0."""

    if policy not in {"inplace", "bip01", "motion"}:
        raise ValueError(f"Unsupported root-motion policy {policy!r}")
    root_bone = next(bone for bone in rig.bones if bone.name == target_root_name)
    root_track = rig.descriptors.index(root_bone.descriptor)
    first_translation = np.asarray(values[0][root_track][3:6], dtype=float)
    bind_translation = np.asarray(root_bone.bind_translation, dtype=float)
    if policy == "inplace":
        for frame in values:
            frame[root_track][3:6] = list(map(float, bind_translation))
    elif policy == "motion":
        if _MOTION_ACCUMULATOR_DESCRIPTOR not in rig.descriptors:
            raise ValueError(
                "Motion-accumulator root policy requires descriptor "
                "0xCCC3CDDF in the target .crig."
            )
        motion_track = rig.descriptors.index(_MOTION_ACCUMULATOR_DESCRIPTOR)
        motion_base = np.asarray(values[0][motion_track][3:6], dtype=float)
        for frame in values:
            current = np.asarray(frame[root_track][3:6], dtype=float)
            horizontal = current - first_translation
            horizontal[1] = 0.0
            frame[root_track][3:6] = list(map(float, current - horizontal))
            frame[motion_track][3:6] = list(map(float, motion_base + horizontal))


def _packed_flags(values: list[list[list[float]]], track_count: int) -> list[list[bool]]:
    result: list[list[bool]] = []
    for track_index in range(track_count):
        flags = [
            max(frame[track_index][component] for frame in values)
            - min(frame[track_index][component] for frame in values)
            > 1.0e-8
            for component in range(9)
        ]
        if any(flags[6:9]):
            flags[6:9] = [True, True, True]
        result.append(flags)
    return result


def build_legacy_5_0_anm2(
    animation_fbx: str | Path,
    rig: Any,
    *,
    fps: float | None = None,
    animation_stack: str | None = None,
    document_factory: Any = FbxDocument,
    document: Any | None = None,
    preflight: FbxPreflightReport | None = None,
    root_mapping: RootMappingSelection | Mapping[str, Any] | None = None,
    requested_root_policy: str = "",
    requested_root_motion: RootMotionSelection | Mapping[str, Any] | None = None,
    progress: Callable[[str], None] | None = None,
) -> RetargetBuild:
    """Sample a compatible source with the exact DLR 0.5.0 transform contract."""

    rig.validate().require_valid()
    source = Path(animation_fbx)
    source_document = (
        document if document is not None else document_factory(source)
    )
    _select_stack(source_document, animation_stack)
    current_stack_name = _selected_stack_name(source_document)
    preflight_matches = (
        preflight is not None
        and preflight.purpose == "animation"
        and Path(preflight.path).resolve() == source.resolve()
        and str(preflight.inventory.get("selected_animation_stack", "") or "")
        == current_stack_name
    )
    checked = preflight if preflight_matches else preflight_fbx(
        source,
        purpose="animation",
        animation_stack=animation_stack,
        target_rig=rig,
        game_id=str(rig.extensions.get("game_id", "")),
        document_factory=document_factory,
        document=source_document,
    )
    checked.require_buildable()

    compatibility = classify_target_compatibility(source_document, rig)
    classification = str(compatibility.get("classification", "incompatible"))
    required_missing = tuple(
        compatibility.get("required_missing_bones", ()) or ()
    )
    hierarchy_mismatches = tuple(
        compatibility.get("hierarchy_mismatches", ()) or ()
    )
    if (
        classification not in _SUPPORTED_COMPATIBILITY
        or required_missing
        or hierarchy_mismatches
    ):
        details: list[str] = []
        if classification not in _SUPPORTED_COMPATIBILITY:
            details.append(f"classification is {classification!r}")
        if required_missing:
            details.append(
                "required target bones are missing: "
                + ", ".join(map(str, required_missing[:12]))
            )
        if hierarchy_mismatches:
            details.append(
                f"{len(hierarchy_mismatches)} target hierarchy mismatch(es) were found"
            )
        raise ValueError(
            "Legacy 5.0 FBX-to-ANM2 export supports only target-compatible "
            "direct-name skeletons. "
            + "; ".join(details)
            + ". Choose Current normalized sampling for semantic or cross-rig retargeting."
        )

    historical = _historical_document_view(
        source_document,
        animation_stack=animation_stack,
    )
    _select_stack(historical, animation_stack)
    if not getattr(historical, "bind_global_matrices", None) or not hasattr(
        historical, "global_matrices"
    ):
        raise ValueError(
            "Legacy 5.0 global bind-basis export requires FBX bind globals and "
            "global animation sampling."
        )

    mapped = dict(compatibility["exact_target_subset_mapping"])
    bind_retained = sorted(
        set(compatibility.get("target_bind_bones", ()) or ()),
        key=str.casefold,
    )
    source_extras = sorted(
        set(compatibility.get("extra_source_bones", ()) or ()),
        key=str.casefold,
    )
    selection = _root_selection(root_mapping)
    target_names = [bone.name for bone in rig.bones]
    target_parents = {
        bone.name: (
            rig.bones[bone.parent_index].name
            if bone.parent_index >= 0
            else None
        )
        for bone in rig.bones
    }
    if selection.target_bone:
        if selection.target_bone not in target_names:
            raise ValueError(
                f"Selected target root {selection.target_bone!r} is not present "
                f"in .crig target {rig.name!r}."
            )
        target_root_name = selection.target_bone
        target_root_method = "manual"
    else:
        target_root_name = choose_hierarchy_root(target_names, target_parents)
        target_root_method = "automatic"
    source_root_name, source_root_method = resolve_source_root(
        historical.limb_models.keys(),
        historical.parent_by_name,
        requested_bone=selection.source_bone,
    )
    target_root_bone = next(
        bone for bone in rig.bones if bone.name == target_root_name
    )

    root_motion = resolve_root_motion_selection(
        requested_root_motion
        if requested_root_motion is not None
        else str(requested_root_policy or "bip01"),
        source_root_bone=source_root_name,
        target_root_bone=target_root_name,
    )
    historical_root_policy = root_motion.legacy_serialized_policy
    sample_fps = float(fps or rig.writer_profile.default_fps)
    if not math.isfinite(sample_fps) or not 1.0 <= sample_fps <= 240.0:
        raise ValueError("Legacy 5.0 sample FPS must be between 1 and 240")

    wrapper_scale, wrapper_axis_retained, wrapper_name, wrapper_matrix = (
        _legacy_wrapper_details(historical, source_root_name)
    )
    meters_per_unit = float(historical.meters_per_unit)
    global_normalization = SourceGlobalNormalization(
        meters_per_unit=meters_per_unit,
        convert_y_up_to_dying_light=(
            _target_uses_dying_light_basis(rig)
            and not wrapper_axis_retained
        ),
        wrapper_scale_normalization_factor=wrapper_scale,
        wrapper_axis_conversion=wrapper_axis_retained,
        wrapper_policy="retained_and_scale_normalized",
    )
    target_bind, target_bind_global = _target_bind_globals(rig)
    source_bind_globals = {
        name: global_normalization.apply(matrix)
        for name, matrix in historical.bind_global_matrices.items()
    }
    corrections = {
        target_name: global_bind_basis_correction(
            source_bind_globals[source_name],
            target_bind_global[target_name],
        )
        for target_name, source_name in mapped.items()
    }

    source_root_bind = source_bind_globals[source_root_name]
    ticks = (
        list(historical.frame_ticks(fps=sample_fps))
        if hasattr(historical, "frame_ticks")
        else [
            int(round(frame * FBX_TICKS_PER_SECOND / sample_fps))
            for frame in range(
                max(1, int(historical.frame_count(fps=sample_fps)))
            )
        ]
    )
    if len(ticks) == 1:
        ticks.append(ticks[0])
    if progress is not None:
        progress("Sampling Legacy 5.0 global bind-basis transforms")

    values: list[list[list[float]]] = []
    source_root_displacements: list[np.ndarray] = []
    for tick in ticks:
        raw_globals = historical.global_matrices(
            tick=tick,
            use_animation=True,
        )
        source_globals = {
            name: global_normalization.apply(matrix)
            for name, matrix in raw_globals.items()
        }
        root_displacement_global = (
            source_globals[source_root_name][:3, 3]
            - source_root_bind[:3, 3]
        )
        if target_root_bone.parent_index >= 0:
            parent_name = rig.bones[target_root_bone.parent_index].name
            root_displacement_local = (
                np.linalg.inv(target_bind_global[parent_name][:3, :3])
                @ root_displacement_global
            )
        else:
            root_displacement_local = root_displacement_global
        source_root_displacements.append(root_displacement_local)

        rows_by_descriptor: dict[int, list[float]] = {}
        animated_target_globals: dict[str, np.ndarray] = {}
        for bone in rig.bones:
            parent_name = (
                rig.bones[bone.parent_index].name
                if bone.parent_index >= 0
                else None
            )
            source_name = mapped.get(bone.name)
            if source_name is None:
                local = target_bind[bone.name]
            else:
                desired_global = corrected_target_global(
                    source_globals[source_name],
                    corrections[bone.name],
                )
                desired_rotation = _orthonormal_rotation(
                    desired_global,
                    f"Legacy 5.0 corrected global {bone.name}",
                )
                local_rotation = (
                    desired_rotation
                    if parent_name is None
                    else _orthonormal_rotation(
                        animated_target_globals[parent_name],
                        f"Legacy 5.0 animated parent {parent_name}",
                    ).T
                    @ desired_rotation
                )
                local = np.eye(4, dtype=float)
                local[:3, :3] = local_rotation @ np.diag(
                    np.asarray(bone.bind_scale, dtype=float)
                )
                local[:3, 3] = np.asarray(
                    bone.bind_translation,
                    dtype=float,
                )
            animated_target_globals[bone.name] = (
                animated_target_globals[parent_name] @ local
                if parent_name is not None
                else local.copy()
            )
            translation, quaternion, scale = decompose_local_matrix(local)
            rows_by_descriptor[bone.descriptor] = [
                *map(float, anm2_cayley_vector_from_quaternion(quaternion)),
                *map(float, translation),
                *map(float, scale),
            ]
        values.append(
            [
                rows_by_descriptor.get(
                    descriptor,
                    [0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 1.0, 1.0, 1.0],
                )
                for descriptor in rig.descriptors
            ]
        )

    if target_root_name not in mapped:
        root_track = rig.descriptors.index(target_root_bone.descriptor)
        root_bind_translation = np.asarray(
            target_root_bone.bind_translation,
            dtype=float,
        )
        for frame, displacement in zip(values, source_root_displacements):
            frame[root_track][3:6] = list(
                map(float, root_bind_translation + displacement)
            )
    _legacy_root_policy(
        values,
        rig,
        target_root_name,
        historical_root_policy,
    )

    flags = _packed_flags(values, len(rig.descriptors))
    payload = build_payload_from_values(
        rig.make_header(frame_count=len(values)),
        rig.descriptors,
        values,
        flags,
    )
    sample_frames = sorted({0, len(values) // 2, len(values) - 1})
    decoded = decode_samples(payload, list(map(float, sample_frames)))
    maximum_error = max(
        abs(float(actual) - float(expected))
        for decoded_frame, frame_index in zip(decoded.frames, sample_frames)
        for actual_track, expected_track in zip(
            decoded_frame.tracks,
            values[frame_index],
        )
        for actual, expected in zip(actual_track, expected_track)
    )
    hierarchy_safety = validate_hierarchy_safety(
        rig,
        values,
        preserve_non_root_translations=True,
    )
    normalization_report = global_normalization.to_report()
    normalization_report.update(
        {
            "historical_wrapper_name": wrapper_name,
            "historical_wrapper_matrix": wrapper_matrix,
        }
    )
    warnings = [
        "Legacy 5.0 compatibility used the historical global bind-basis "
        "sampler. Modern heading, mirror repair, bilateral swaps, and root-basis "
        "corrections were intentionally bypassed."
    ]
    if bind_retained:
        warnings.append(
            f"Retained {len(bind_retained)} optional target bone(s) at CRIG bind pose."
        )
    return RetargetBuild(
        payload=payload,
        frame_count=len(values),
        report={
            "fbx_anm2_export_behavior": LEGACY_5_0,
            "sampler_contract": _SAMPLER_CONTRACT,
            "engine": "Legacy50GlobalBindRetargetEngine",
            "retarget_mode": "legacy_5_0_global_bind_basis",
            "source_fbx": str(source),
            "source_animation_stack": _selected_stack_name(historical),
            "target_rig_id": rig.rig_id,
            "target_rig_name": rig.name,
            "target_skeleton_hash": rig.skeleton_hash,
            "source_target_classification": classification,
            "skeleton_classification": classification,
            "bind_retained_bones": bind_retained,
            "bind_retained_row_count": len(bind_retained),
            "source_extra_bones_ignored": source_extras,
            "source_extra_bone_count": len(source_extras),
            "mapped_bone_count": len(mapped),
            "frame_count": len(values),
            "fps": sample_fps,
            "track_count": len(rig.descriptors),
            "sample_frames": sample_frames,
            "decoded_max_component_error": maximum_error,
            "root_mapping": {
                "source_bone": source_root_name,
                "source_method": source_root_method,
                "target_bone": target_root_name,
                "target_method": target_root_method,
            },
            "root_policy": historical_root_policy,
            "root_motion_policy_requested": str(
                requested_root_policy or historical_root_policy
            ),
            "root_motion_policy_applied": historical_root_policy,
            "requested_root_motion": root_motion.to_dict(),
            "source_unit_meters": meters_per_unit,
            "wrapper_scale_normalization_factor": wrapper_scale,
            "effective_post_wrapper_translation_scale": (
                meters_per_unit / wrapper_scale
            ),
            "source_global_normalization": normalization_report,
            "basis_correction_policy": "global_bind_basis_correction",
            "preserves_target_non_root_translation_and_scale": True,
            "hierarchy_safety": hierarchy_safety,
            "modern_transform_repairs_applied": False,
            "wrapper_canonicalization": {
                "applied": False,
                "policy": "v0.5_retained_basis_scale_normalized",
                "wrapper": wrapper_name,
                "wrapper_axis_conversion_retained": wrapper_axis_retained,
            },
            "mirror_repair": None,
            "ignored_modern_policies": [
                "root_heading",
                "mirror_repair",
                "bilateral_bone_swap",
                "current_root_basis_correction",
            ],
            "warnings": warnings,
            "fbx_preflight": checked.to_dict(),
            **compatibility,
        },
    )


__all__ = ["build_legacy_5_0_anm2"]
