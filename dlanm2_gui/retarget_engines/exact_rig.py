from __future__ import annotations

from pathlib import Path

from .mapped_rig import build_mapped_rig_anm2
from ..bone_maps import BoneMapPair, GenericBoneMap, skeleton_signature
from ..fbx_preflight import classify_target_compatibility, normalized_bone_name, preflight_fbx
from ..fbx_core import FbxDocument


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
):
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
    report = preflight_fbx(
        animation_fbx,
        purpose="animation",
        animation_stack=animation_stack,
        target_rig=rig,
        game_id=str(rig.extensions.get("game_id", "")),
        document_factory=document_factory,
        document=document,
    )
    hard_findings = [
        row
        for row in report.findings
        if row.severity == "error" and not row.can_continue
    ]
    if hard_findings:
        raise ValueError(
            "Exact-rig FBX preflight blocked the build before ANM2 output:\n"
            + report.actionable_message(hard_findings)
        )
    from .legacy_exact_rig import _is_dlr_native_export
    if _is_dlr_native_export(document):
        # Native ANM2->FBX round trips deliberately use display-only parents for
        # zero-length twist bones. Their export marker selects the established
        # helper/display-basis solver after hard transform preflight.
        from .legacy_exact_rig import build_exact_rig_anm2 as build_legacy_exact
        return build_legacy_exact(
            animation_fbx,
            rig,
            fps=fps,
            animation_stack=animation_stack,
            document_factory=document_factory,
            document=document,
        )
    compatibility = classify_target_compatibility(document, rig)
    if compatibility["required_missing_bones"] or compatibility["hierarchy_mismatches"]:
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
        return build_legacy_exact(
            animation_fbx, rig, fps=fps, animation_stack=animation_stack,
            document_factory=document_factory, document=document,
        )
    source_hash = skeleton_signature(
        (name, document.parent_by_name.get(name)) for name in sorted(document.limb_models)
    )
    bone_map = GenericBoneMap.create(
        "Exact/subset global bind-basis map", rig.skeleton_hash, source_hash,
        source_rig_ref=rig.rig_id,
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
            "retarget_mode": "exact" if compatibility["classification"] == "exact_identity" else "target_compatible_source_superset",
            "engine": "ExactRigRetargetEngine",
            "skeleton_classification": compatibility["classification"],
            **compatibility,
            "source_skeleton_hash": source_hash,
            "target_skeleton_hash": rig.skeleton_hash,
            "fbx_preflight": report.to_dict(),
        }
    )
    return result


__all__ = ["build_exact_rig_anm2"]
