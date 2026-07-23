from __future__ import annotations

from pathlib import Path

from .mapped_rig import build_mapped_rig_anm2
from ..blender_mirror_wrapper import (
    BilateralSemanticPolicy,
    coerce_bilateral_semantic_policy,
    resolve_bilateral_semantic_decision,
    resolve_blender_lateral_mirror,
    should_swap_bilateral_rows,
    swapped_bilateral_source_name,
)
from ..bone_maps import BoneMapPair, GenericBoneMap, skeleton_signature
from ..fbx_preflight import (
    FbxPreflightReport,
    classify_target_compatibility,
    normalized_bone_name,
    preflight_fbx,
)
from ..fbx_core import FbxDocument
from ..fbx_anm2_export_behavior import (
    LEGACY_5_0,
    coerce_fbx_anm2_export_behavior,
)
from ..model_importer.fbx_model import FBX_Y_UP_TO_DYING_LIGHT


def build_exact_rig_anm2(
    animation_fbx,
    rig,
    *,
    fps=None,
    animation_stack=None,
    document_factory=FbxDocument,
    document=None,
    root_mapping=None,
    root_policy="bip01",
    root_motion=None,
    fbx_anm2_export_behavior: str = "current",
    bilateral_semantic_policy: str = (
        BilateralSemanticPolicy.PRESERVE_SOURCE_NAMES.value
    ),
    diagnostic_post_canonicalization_mirror_conjugation: bool = False,
    preflight: FbxPreflightReport | None = None,
    progress=None,
):
    fbx_anm2_export_behavior = coerce_fbx_anm2_export_behavior(
        fbx_anm2_export_behavior
    )
    bilateral_semantic_policy = coerce_bilateral_semantic_policy(
        bilateral_semantic_policy
    )
    document = document if document is not None else document_factory(Path(animation_fbx))
    selected_stack = getattr(document, "selected_animation_stack", None)
    selected_stack_name = str(getattr(selected_stack, "name", "") or "")
    if (
        animation_stack
        and selected_stack_name != animation_stack
    ) or (
        not animation_stack
        and len(getattr(document, "animation_stacks", ())) > 1
        and selected_stack is None
    ):
        document.select_animation_stack(animation_stack)
    source = Path(animation_fbx)
    current_stack = getattr(document, "selected_animation_stack", None)
    current_stack_name = str(getattr(current_stack, "name", "") or "")
    preflight_matches_document = (
        preflight is not None
        and preflight.purpose == "animation"
        and Path(preflight.path).resolve() == source.resolve()
        and str(preflight.inventory.get("selected_animation_stack", "") or "")
        == current_stack_name
    )
    report = preflight if preflight_matches_document else preflight_fbx(
        animation_fbx,
        purpose="animation",
        animation_stack=animation_stack,
        target_rig=rig,
        game_id=str(rig.extensions.get("game_id", "")),
        document_factory=document_factory,
        document=document,
    )
    report.require_buildable()
    compatibility = classify_target_compatibility(document, rig)
    if fbx_anm2_export_behavior == LEGACY_5_0:
        from .legacy_5_0 import build_legacy_5_0_anm2

        return build_legacy_5_0_anm2(
            animation_fbx,
            rig,
            fps=fps,
            animation_stack=animation_stack,
            document_factory=document_factory,
            document=document,
            preflight=report,
            root_mapping=root_mapping,
            requested_root_policy=root_policy,
            requested_root_motion=root_motion,
            progress=progress,
        )
    from .legacy_exact_rig import _is_dlr_native_export
    if _is_dlr_native_export(document):
        # Native ANM2->FBX exports carry an explicit basis/helper contract. Its
        # marker selects the metadata-aware inverse after hard transform
        # preflight. Current exports intentionally use child-facing Blender
        # display axes; the stored correction restores Chrome game-space axes.
        from .legacy_exact_rig import build_exact_rig_anm2 as build_legacy_exact
        result = build_legacy_exact(
            animation_fbx,
            rig,
            fps=fps,
            animation_stack=animation_stack,
            document_factory=document_factory,
            document=document,
        )
        result.report.update(
            {
                "fbx_anm2_export_behavior": fbx_anm2_export_behavior,
                "sampler_contract": "dlr_current_native_metadata_inverse_v1",
                "source_target_classification": compatibility["classification"],
                "bind_retained_bones": list(
                    compatibility.get("target_bind_bones", ()) or ()
                ),
            }
        )
        return result
    exact_target_subset = (
        compatibility.get("classification") == "exact_target_subset"
    )
    if compatibility["hierarchy_mismatches"] or (
        compatibility["required_missing_bones"] and not exact_target_subset
    ):
        rows = []
        if compatibility["required_missing_bones"]:
            rows.append("required target bones missing: " + ", ".join(compatibility["required_missing_bones"][:20]))
        for row in compatibility["hierarchy_mismatches"][:20]:
            rows.append(
                f"parent mismatch for {row['bone']!r}: expected "
                f"{row['expected_target_parent']!r}, found {row['source_target_ancestor']!r}"
            )
        raise ValueError(
            "Exact/subset rig is incompatible:\n- " + "\n- ".join(rows)
            + "\n\nUse Root & .crig Mapping to create a reviewed cross-rig map; no ANM2 output was created."
        )
    if not getattr(document, "bind_global_matrices", None):
        # Synthetic fixtures, legacy custom-rig FBXs, and native ANM2->FBX
        # round trips retain the previously validated helper/display-basis path.
        from .legacy_exact_rig import build_exact_rig_anm2 as build_legacy_exact
        result = build_legacy_exact(
            animation_fbx, rig, fps=fps, animation_stack=animation_stack,
            document_factory=document_factory, document=document,
        )
        result.report.update(
            {
                "fbx_anm2_export_behavior": fbx_anm2_export_behavior,
                "sampler_contract": "dlr_current_direct_local_fallback_v1",
                "source_target_classification": compatibility["classification"],
                "bind_retained_bones": list(
                    compatibility.get("target_bind_bones", ()) or ()
                ),
            }
        )
        return result
    source_hash = skeleton_signature(
        (name, document.parent_by_name.get(name)) for name in sorted(document.limb_models)
    )
    bone_map = GenericBoneMap.create(
        "Exact/subset global bind-basis map", rig.skeleton_hash, source_hash,
        source_rig_ref=rig.rig_id,
    )
    mirror_context = None
    if str(getattr(rig, "rig_id", "") or "") == "builtin:dl2_player_advanced":
        mirror_context = resolve_blender_lateral_mirror(
            document,
            source_basis_matrix=FBX_Y_UP_TO_DYING_LIGHT,
        )
    semantic_decision = resolve_bilateral_semantic_decision(
        document,
        rig,
        bilateral_semantic_policy,
        source_basis_matrix=FBX_Y_UP_TO_DYING_LIGHT,
        wrapper_context=mirror_context,
    )
    swap_bilateral = should_swap_bilateral_rows(
        bilateral_semantic_policy,
        semantic_decision,
    )
    by_normal = {normalized_bone_name(name): name for name in document.limb_models}
    bone_map.pairs = []
    # A source-superset FBX can contain authored helper/accessory pivots whose
    # local translations are not target skin-bone lengths. Keep the target
    # CRIG's T/S for automatic superset rows; the global-bind solver still
    # supplies the corrected rotation. Exact-identity model rigs retain the
    # complete authored local transform, including intentional mechanics.
    automatic_components = (
        "full_transform"
        if compatibility["classification"] == "exact_identity"
        else "rotation"
    )
    for bone in rig.bones:
        source_name = by_normal.get(normalized_bone_name(bone.name))
        if source_name is not None and swap_bilateral:
            source_name = swapped_bilateral_source_name(
                source_name, document.limb_models
            )
        if source_name is not None:
            bone_map.pairs.append(
                BoneMapPair(
                    bone.descriptor,
                    bone.name,
                    source_name,
                    1.0,
                    "exact_or_subset",
                    component_policy=automatic_components,
                )
            )
        else:
            bind_mode = "inherit_bind" if bone.parent_index >= 0 else "static_bind"
            bone_map.pairs.append(
                BoneMapPair(
                    bone.descriptor,
                    bone.name,
                    "",
                    1.0,
                    "exact_subset_target_bind",
                    transfer_policy="bind",
                    component_policy="rotation",
                    review_state="intentionally_unmapped",
                    notes="target-only row retains target bind-local transform",
                    extensions={
                        "mapping_mode": bind_mode,
                        "execution_mapping_mode": bind_mode,
                        "source_bones": [],
                    },
                )
            )
    swapped_row_count = sum(
        row.source_fbx_bone
        != by_normal.get(normalized_bone_name(row.target_rig_bone))
        for row in bone_map.pairs
        if row.source_fbx_bone
    )
    semantic_payload = semantic_decision.to_dict()
    semantic_payload["bilateral_swapped_row_count"] = swapped_row_count
    bone_map.extensions["bilateral_semantic_decision"] = semantic_payload
    if mirror_context is not None:
        mirror_payload = mirror_context.to_dict()
        mirror_payload["application"] = "diagnostic_only"
        mirror_payload["swapped_row_count"] = swapped_row_count
        bone_map.extensions["source_wrapper_mirror"] = mirror_payload
    transfer_policy = (
        "global_bind_basis_correction"
        if getattr(document, "bind_global_matrices", None) and hasattr(document, "global_matrices")
        else "mapped_local_rest_delta"
    )
    # Compatibility above has already proved that target ancestry is retained
    # in the source skeleton.  In that case the CRIG's declared root is more
    # authoritative than the generic humanoid name heuristic (which may choose
    # a child named ``pelvis`` ahead of an actual custom-model ``root``).  A
    # source-superset row intentionally preserves target T/S, so selecting that
    # child as the root-motion receiver would otherwise turn rotation of its
    # real parent into an unexpected local translation.  Keep an explicit user
    # choice unchanged; only replace the automatic exact/subset default.
    exact_root_mapping = root_mapping
    automatic_exact_root = False
    if exact_root_mapping is None:
        target_root = rig.bones[rig.root_index]
        source_root = by_normal.get(normalized_bone_name(target_root.name))
        if source_root is not None:
            exact_root_mapping = {
                "source_bone": source_root,
                "target_bone": target_root.name,
            }
            automatic_exact_root = True
    result = build_mapped_rig_anm2(
        animation_fbx,
        rig,
        bone_map,
        fps=fps,
        animation_stack=animation_stack,
        document_factory=document_factory,
        document=document,
        transfer_policy=transfer_policy,
        root_mapping=exact_root_mapping,
        root_policy=root_policy,
        root_motion=root_motion,
        fbx_anm2_export_behavior=fbx_anm2_export_behavior,
        bilateral_semantic_policy=bilateral_semantic_policy.value,
        diagnostic_post_canonicalization_mirror_conjugation=(
            diagnostic_post_canonicalization_mirror_conjugation
        ),
        preflight=report,
        progress=progress,
    )
    if automatic_exact_root:
        result.report["root_mapping"].update(
            {
                "source_method": "target_compatible_declared_crig_root",
                "target_method": "declared_crig_root",
            }
        )
    result.report.update(
        {
            "fbx_anm2_export_behavior": fbx_anm2_export_behavior,
            "sampler_contract": "dlr_current_normalized_global_v2",
            "bilateral_semantic_policy": bilateral_semantic_policy.value,
            "bilateral_semantic_decision": semantic_payload,
            "bilateral_swap_applied": semantic_decision.swap_applied,
            "bilateral_swapped_row_count": swapped_row_count,
            "post_canonicalization_mirror_conjugation_applied": bool(
                diagnostic_post_canonicalization_mirror_conjugation
                and mirror_context is not None
            ),
            "source_target_classification": compatibility["classification"],
            "bind_retained_bones": list(
                compatibility.get("target_bind_bones", ()) or ()
            ),
            "retarget_mode": (
                "exact"
                if compatibility["classification"] == "exact_identity"
                else "exact_target_subset"
                if compatibility["classification"] == "exact_target_subset"
                else "target_compatible_source_superset"
            ),
            "engine": "ExactRigRetargetEngine",
            "skeleton_classification": compatibility["classification"],
            **compatibility,
            "source_skeleton_hash": source_hash,
            "target_skeleton_hash": rig.skeleton_hash,
            "fbx_preflight": report.to_dict(),
        }
    )
    if mirror_context is not None:
        result.report["wrapper_reflection"] = dict(
            bone_map.extensions["source_wrapper_mirror"]
        )
    if semantic_decision.warning:
        warnings = result.report.setdefault("warnings", [])
        if semantic_decision.warning not in warnings:
            warnings.append(semantic_decision.warning)
    result.report["mapping"] = {
        "exact_target_subset_rows": int(
            compatibility.get("exact_target_subset_rows", len(bone_map.pairs))
            or 0
        ),
        "semantic_rows": 0,
        "manual_target_overrides": 0,
        "target_bind_rows": int(
            compatibility.get("target_bind_rows", 0) or 0
        ),
        "spatial_only_rows": 0,
    }
    return result


__all__ = ["build_exact_rig_anm2"]
