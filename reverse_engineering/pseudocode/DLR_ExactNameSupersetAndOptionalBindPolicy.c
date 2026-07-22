/*
 * Named C-like reconstruction of DL ReAnimated's export-first mapping policy.
 * Documentation only: this is not compiled game or application code.
 */

typedef struct DLR_ExactNameOverlap {
    unsigned target_bone_count;
    unsigned matched_row_count;
    int matched_primary_root;
    int all_matched_parent_bases_agree;
} DLR_ExactNameOverlap;

int DLR_ShouldAcceptExactNameSourceSupersetRows(
    const DLR_ExactNameOverlap *overlap)
{
    unsigned minimum_overlap;

    if (overlap == 0 || !overlap->all_matched_parent_bases_agree)
        return 0;

    minimum_overlap = (overlap->target_bone_count + 4u) / 5u;
    if (minimum_overlap < 8u)
        minimum_overlap = 8u;

    return overlap->matched_primary_root
        && overlap->matched_row_count >= minimum_overlap;
}

int DLR_ShouldBindHoldOptionalHierarchyMismatch(
    int target_is_deform,
    int target_is_helper,
    int exact_name_parent_basis_agrees)
{
    if (exact_name_parent_basis_agrees)
        return 0;

    /* A helper/endpoint follows its mapped target parent at target bind. */
    return target_is_helper || !target_is_deform;
}
