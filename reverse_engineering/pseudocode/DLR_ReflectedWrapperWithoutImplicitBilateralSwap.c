/*
 * C-like reconstruction of the corrected FBX -> ANM2 wrapper contract.
 * Geometry is canonicalized once; bilateral source ownership is resolved by a
 * separate policy.
 */

typedef enum DLR_BilateralSemanticPolicy {
    DLR_BILATERAL_AUTO,
    DLR_BILATERAL_PRESERVE_SOURCE_NAMES,
    DLR_BILATERAL_SWAP_EXPLICIT
} DLR_BilateralSemanticPolicy;

DLR_WrapperDiagnostics dlr_canonicalize_reflected_fbx_wrapper(
    const DLR_FbxDocument *document,
    DLR_SourceGlobals *out_canonical_globals)
{
    DLR_WrapperDiagnostics result = {0};
    if (!dlr_common_wrapper_is_static_uniform_reflection(document))
        return result;

    result.wrapper_reflection_detected = true;
    result.wrapper_canonicalized = true;
    result.wrapper_matrix = dlr_common_wrapper_matrix(document);
    dlr_left_multiply_bone_globals(
        out_canonical_globals,
        dlr_inverse(result.wrapper_matrix));
    return result;
}

bool dlr_should_swap_bilateral_animation_rows(
    DLR_BilateralSemanticPolicy policy,
    const DLR_BindSideConsensus *auto_consensus)
{
    if (policy == DLR_BILATERAL_SWAP_EXPLICIT)
        return true;
    if (policy == DLR_BILATERAL_PRESERVE_SOURCE_NAMES)
        return false;
    return auto_consensus->strong_opposite_side_agreement;
}

const char *dlr_resolve_exact_source_bone_name(
    const char *target_name,
    const DLR_SourceSkeleton *source,
    bool swap_bilateral)
{
    const char *same = dlr_find_normalized_exact_name(source, target_name);
    if (!same || !swap_bilateral)
        return same;
    return dlr_verified_bilateral_counterpart_or_self(source, same);
}

DLR_Matrix4 dlr_normalize_source_global_after_wrapper_canonicalization(
    DLR_Matrix4 canonical_global,
    const DLR_NormalizationContract *contract)
{
    DLR_Matrix4 normalized = dlr_apply_units_and_axis_once(
        canonical_global,
        contract->meters_per_unit,
        contract->proper_axis_basis);

    DLR_ASSERT(
        !(contract->wrapper_canonicalized_before_sampling &&
          contract->post_canonicalization_mirror_conjugation_applied));
    return normalized;
}

bool dlr_validate_asymmetric_bilateral_side_preservation(
    const DLR_DecodedAnimation *candidate,
    const DLR_DecodedAnimation *known_good)
{
    for (unsigned pair = 0; pair < DLR_TRUSTED_BILATERAL_PAIR_COUNT; ++pair) {
        float same = dlr_same_side_curve_correlation(candidate, known_good, pair);
        float cross = dlr_cross_side_curve_correlation(candidate, known_good, pair);
        if (cross > same + 0.05f)
            return false;
    }
    return true;
}

