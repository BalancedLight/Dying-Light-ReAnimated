from __future__ import annotations

from pathlib import Path

from .mapped_rig import build_mapped_rig_anm2
from ..bone_maps import BoneMapPair, GenericBoneMap, skeleton_signature
from ..fbx_preflight import classify_target_compatibility, normalized_bone_name, preflight_fbx
from ..oracle.binary_fbx_mixamo import _FbxDocument


def build_exact_rig_anm2(
    animation_fbx,
    rig,
    *,
    fps=None,
    animation_stack=None,
    document_factory=_FbxDocument,
    root_mapping=None,
    root_policy="bip01",
):
    document = document_factory(Path(animation_fbx))
    if animation_stack or len(getattr(document, "animation_stacks", ())) > 1:
        document.select_animation_stack(animation_stack)
    from .legacy_exact_rig import _is_dlr_native_export
    if not getattr(document, "bind_global_matrices", None) or _is_dlr_native_export(document):
        # Synthetic fixtures, legacy custom-rig FBXs, and native ANM2->FBX
        # round trips retain the previously validated helper/display-basis path.
        from .legacy_exact_rig import build_exact_rig_anm2 as build_legacy_exact
        return build_legacy_exact(
            animation_fbx, rig, fps=fps, animation_stack=animation_stack,
            document_factory=document_factory,
        )
    report = preflight_fbx(
        animation_fbx,
        purpose="animation",
        animation_stack=animation_stack,
        target_rig=rig,
        game_id=str(rig.extensions.get("game_id", "")),
        document_factory=document_factory,
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
            + "\n\nUse Root & .crig Mapping to create a reviewed cross-rig map."
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
    for bone in rig.bones:
        source_name = by_normal.get(normalized_bone_name(bone.name))
        if source_name is not None:
            bone_map.pairs.append(BoneMapPair(bone.descriptor, bone.name, source_name, 1.0, "exact_or_subset"))
    transfer_policy = (
        "global_bind_basis_correction"
        if getattr(document, "bind_global_matrices", None) and hasattr(document, "global_matrices")
        else "mapped_local_rest_delta"
    )
    result = build_mapped_rig_anm2(
        animation_fbx,
        rig,
        bone_map,
        fps=fps,
        animation_stack=animation_stack,
        document_factory=document_factory,
        transfer_policy=transfer_policy,
        root_mapping=root_mapping,
        root_policy=root_policy,
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
