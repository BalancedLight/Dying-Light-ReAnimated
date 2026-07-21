/*
 * Documentation-only, named C-like reconstruction of DL ReAnimated's offline
 * automatic-retarget policy. This file describes tool policy and invariants;
 * it is not recovered game source, a game ABI declaration, or callable engine
 * code. Names and structures below intentionally model the tool, not Techland
 * implementation details.
 */

#include <stdbool.h>
#include <stddef.h>
#include <stdint.h>

typedef enum {
    DLR_ARCHETYPE_EXACT,
    DLR_ARCHETYPE_HUMANOID,
    DLR_ARCHETYPE_GENERIC_OBJECT,
    DLR_ARCHETYPE_MECHANICAL,
    DLR_ARCHETYPE_ANIMAL,
    DLR_ARCHETYPE_UNKNOWN
} DlrSkeletonArchetype;

typedef enum {
    DLR_MAP_DIRECT,
    DLR_MAP_COMPOSED,
    DLR_MAP_DISTRIBUTED,
    DLR_MAP_INHERIT_BIND,
    DLR_MAP_STATIC_BIND,
    DLR_MAP_MANUAL_REQUIRED
} DlrMappingMode;

typedef enum {
    DLR_DOMAIN_FULL_BODY,
    DLR_DOMAIN_UPPER_BODY,
    DLR_DOMAIN_LOWER_BODY,
    DLR_DOMAIN_SINGLE_LIMB,
    DLR_DOMAIN_ROOT_MOTION,
    DLR_DOMAIN_FACIAL_ONLY,
    DLR_DOMAIN_STATIC_POSE,
    DLR_DOMAIN_UNKNOWN
} DlrAnimatedDomain;

typedef enum {
    DLR_READY_EXACT,
    DLR_READY_AUTOMATIC,
    DLR_READY_PARTIAL,
    DLR_NEEDS_ATTENTION,
    DLR_INCOMPATIBLE
} DlrReadiness;

typedef enum {
    DLR_PRESENT_INFO,
    DLR_PRESENT_ADVISORY,
    DLR_PRESENT_ACTION_REQUIRED,
    DLR_PRESENT_BLOCKING
} DlrPresentationSeverity;

typedef struct {
    float m[16];
} DlrMatrix4;

typedef struct {
    const char *original_utf8;
    const char *nfkc_casefold;
    const char *comparison_transliteration;
    const char **semantic_tokens;
    const char **scripts;
    const char *namespace_text;
    int side_hint;
    bool helper_hint;
    bool control_hint;
    bool end_hint;
    bool twist_hint;
} DlrNormalizedBoneName;

typedef struct {
    uint32_t source_index;
    int32_t parent_index;
    DlrMatrix4 bind_global;
    DlrMatrix4 bind_local;
    float normalized_joint_position[3];
    float normalized_length;
    uint64_t animated_component_mask;
    DlrNormalizedBoneName name;
} DlrAnalyzedBone;

typedef struct {
    DlrAnalyzedBone *bones;
    uint32_t bone_count;
    const char **wrapper_nodes;
    uint32_t wrapper_count;
    DlrSkeletonArchetype archetype;
    DlrAnimatedDomain animated_domain;
    const char **family_hints;
    const char *skeleton_signature;
    const char *bind_signature;
    const char *analyzer_version;
    float archetype_confidence;
} DlrAnalyzedSkeleton;

typedef struct {
    const char *role;
    int side;
    uint32_t source_index;
    float score;
    float runner_up_score;
    float confidence_margin;
    bool virtual_calculation_role;
} DlrSemanticRole;

typedef struct {
    const char *chain_name;
    const DlrSemanticRole *ordered_nodes;
    uint32_t node_count;
    bool animated;
} DlrSemanticChain;

typedef struct {
    uint32_t target_descriptor;
    const char *target_bone_name;
    DlrMappingMode mode;
    const uint32_t *source_indices;
    uint32_t source_count;
    const char *transfer_policy;
    const char *component_policy;
    float score;
    float runner_up_score;
    const char *evidence_summary;
} DlrRetargetRow;

typedef struct {
    DlrRetargetRow *rows;
    uint32_t row_count;
    uint32_t direct_count;
    uint32_t composed_count;
    uint32_t distributed_count;
    uint32_t inherit_bind_count;
    uint32_t static_bind_count;
    uint32_t manual_required_count;
    uint32_t spatial_only_count;
    uint32_t non_body_mapped_count;
} DlrRetargetPlan;

typedef struct {
    const char *analyzer_version;
    const char *semantic_policy_version;
    const char *source_skeleton_signature;
    const char *source_bind_signature;
    const char *target_rig_id;
    const char *target_full_skeleton_hash;
    const char *target_provenance_hash;
    const char *canonical_rows_hash;
    DlrAnimatedDomain animated_domain;
    uint32_t target_row_count;
    uint32_t mapped_body_count;
    uint32_t explicit_bind_count;
    uint32_t spatial_only_count;
    uint32_t non_body_mapped_count;
    bool animated_critical_chains_resolved;
} DlrMappingCertificate;

typedef struct {
    DlrReadiness readiness;
    DlrPresentationSeverity severity;
    const char *row_label;
    const char *single_action_label;
    uint32_t suppressed_information_count;
    bool show_import_modal;
    bool block_build;
} DlrReadinessResult;

/*
 * Normalize comparison evidence without destroying the original UTF-8 name.
 * The real implementation uses Unicode NFKC and casefolding, separates vendor
 * namespaces and side/helper tokens, and consults a versioned multilingual
 * anatomy lexicon. Transliteration is comparison-only and never becomes an
 * exported bone name.
 */
DlrNormalizedBoneName dlr_normalize_multilingual_bone_name(
    const char *original_utf8,
    const void *versioned_anatomy_lexicon)
{
    DlrNormalizedBoneName out = {0};
    out.original_utf8 = original_utf8;
    out.nfkc_casefold = unicode_nfkc_casefold(original_utf8);
    out.namespace_text = split_namespace_without_discarding_it(out.nfkc_casefold);
    tokenize_separators_camel_case_and_digits(&out);
    annotate_side_helper_control_end_and_twist_tokens(&out);
    annotate_detected_unicode_scripts(&out);
    out.comparison_transliteration = optional_comparison_transliteration(&out);
    out.semantic_tokens = multilingual_anatomy_tokens(
        &out, versioned_anatomy_lexicon);
    return out;
}

/*
 * Analyze graph, bind, skin, and motion evidence before role inference. Bone
 * names are one evidence channel, never the identity key. Non-bone armature
 * wrappers remain in the evaluated transform graph.
 */
DlrAnalyzedSkeleton dlr_analyze_source_skeleton_without_name_dependency(
    const FbxScene *scene,
    const FbxAnimationStack *stack,
    const void *versioned_anatomy_lexicon)
{
    DlrAnalyzedSkeleton out = {0};
    out.wrapper_nodes = collect_non_bone_wrapper_graph(scene, &out.wrapper_count);
    out.bones = collect_limb_nodes_and_parents(scene, &out.bone_count);

    for (uint32_t i = 0; i < out.bone_count; ++i) {
        DlrAnalyzedBone *bone = &out.bones[i];
        bone->name = dlr_normalize_multilingual_bone_name(
            fbx_original_utf8_name(scene, i), versioned_anatomy_lexicon);
        bone->bind_global = authoritative_bind_global(
            scene, i, /* priority: */ DLR_BINDPOSE_THEN_TRANSFORMLINK_THEN_MODEL);
        bone->bind_local = relative_to_evaluated_parent(out.bones, i);
        bone->animated_component_mask = changing_components(stack, i);
    }

    normalize_units_axis_and_joint_geometry_once(&out);
    measure_lengths_depth_branches_and_symmetry(&out);
    attach_skin_influence_regions_when_available(scene, &out);
    classify_helper_control_end_and_twist_likelihood(&out);
    out.family_hints = recognize_family_hints_only(&out);
    out.archetype = infer_archetype_from_all_evidence(&out);
    out.animated_domain = classify_observed_animation_domain(&out);
    out.skeleton_signature = hash_normalized_hierarchy(&out);
    out.bind_signature = hash_authoritative_bind_matrices(&out);
    out.analyzer_version = DLR_ANALYZER_VERSION;
    return out;
}

/*
 * Infer anatomy from topology, bind geometry, symmetry, skin regions, motion,
 * and multilingual names. A role is automatic only when its score and margin
 * pass policy and agree with its parent chain and side. A pelvis may be a
 * virtual calculation role; virtual roles never become output tracks.
 */
bool dlr_infer_humanoid_roles_from_topology_and_bind_geometry(
    const DlrAnalyzedSkeleton *source,
    DlrSemanticRole *out_roles,
    uint32_t *inout_role_capacity)
{
    if (source->archetype != DLR_ARCHETYPE_HUMANOID &&
        source->archetype != DLR_ARCHETYPE_EXACT)
        return false;

    score_pelvis_spine_neck_and_head_anchors(source, out_roles);
    score_bilateral_limb_chains(source, out_roles);
    score_optional_digit_chains(source, out_roles);
    infer_unambiguous_virtual_common_ancestor_roles(source, out_roles);
    reject_side_conflicts_duplicate_consumption_and_broken_ancestry(out_roles);
    mark_low_score_or_low_margin_animated_roles_manual(out_roles);
    *inout_role_capacity = count_roles_written(out_roles);
    return every_animated_critical_chain_is_resolved(out_roles);
}

/* Compose ordered source deltas when a source chain is longer than its target. */
DlrMatrix4 dlr_compose_source_chain_motion_for_shorter_target(
    const DlrAnalyzedSkeleton *source,
    const uint32_t *ordered_source_indices,
    uint32_t source_count,
    uint32_t frame)
{
    DlrMatrix4 composed = matrix_identity();
    for (uint32_t i = 0; i < source_count; ++i)
        composed = matrix_multiply(composed, source_bind_relative_delta(
            source, ordered_source_indices[i], frame));
    return remove_unowned_translation_and_scale(composed);
}

/*
 * Distribute one source segment across a longer target chain using stable,
 * bind-derived weights. Quaternion-log rotation distribution avoids Euler
 * order dependence; each target keeps its authored translation and scale.
 */
void dlr_distribute_source_segment_motion_across_target_chain(
    DlrMatrix4 source_delta,
    const DlrMatrix4 *target_bind_locals,
    uint32_t target_count,
    DlrMatrix4 *out_target_locals)
{
    const DlrQuaternionLog rotation = quaternion_log(rotation_of(source_delta));
    const float total_length = sum_target_bind_lengths(target_bind_locals, target_count);
    for (uint32_t i = 0; i < target_count; ++i) {
        const float weight = stable_length_weight(
            target_bind_locals, i, total_length, target_count);
        out_target_locals[i] = with_bind_translation_and_scale(
            target_bind_locals[i], quaternion_exp(scale(rotation, weight)));
    }
}

/*
 * Missing optional target bones are reconstructed under the animated target
 * parent, not frozen in global space. This is the inherit_bind rule:
 * target_global = animated_parent_global * target_bind_local.
 */
DlrMatrix4 dlr_keep_unmapped_target_bone_at_bind_under_animated_parent(
    DlrMatrix4 animated_target_parent_global,
    DlrMatrix4 target_bind_local)
{
    return matrix_multiply(animated_target_parent_global, target_bind_local);
}

/*
 * Align ordered semantic chains. Pure proximity may rank an already coherent
 * candidate, but cannot invent an anatomical relationship. Missing optional
 * or unanimated target rows become inherit_bind/static_bind; ambiguity in an
 * animated critical chain becomes manual_required.
 */
DlrRetargetPlan dlr_align_source_and_target_semantic_chains(
    const DlrAnalyzedSkeleton *source,
    const DlrSemanticChain *source_chains,
    uint32_t source_chain_count,
    const TargetRig *target)
{
    DlrRetargetPlan plan = allocate_one_explicit_row_per_target_bone(target);

    for (uint32_t c = 0; c < target->semantic_chain_count; ++c) {
        const DlrSemanticChain *target_chain = &target->semantic_chains[c];
        const DlrSemanticChain *source_chain = find_same_role_and_side_chain(
            source_chains, source_chain_count, target_chain);

        if (!source_chain) {
            set_chain_mode(&plan, target_chain,
                target_chain_is_optional_or_unanimated(target_chain, source)
                    ? DLR_MAP_INHERIT_BIND : DLR_MAP_MANUAL_REQUIRED);
        } else if (!chain_score_margin_and_ancestry_are_safe(
                       source, source_chain, target_chain)) {
            set_chain_mode(&plan, target_chain,
                target_chain->animated ? DLR_MAP_MANUAL_REQUIRED
                                       : DLR_MAP_STATIC_BIND);
        } else if (source_chain->node_count == target_chain->node_count) {
            align_chain_one_to_one(&plan, source_chain, target_chain,
                                   DLR_MAP_DIRECT);
        } else if (source_chain->node_count > target_chain->node_count) {
            align_ordered_chain_with_composition(&plan, source_chain,
                                                 target_chain,
                                                 DLR_MAP_COMPOSED);
        } else {
            align_ordered_chain_with_distribution(&plan, source_chain,
                                                  target_chain,
                                                  DLR_MAP_DISTRIBUTED);
        }
    }

    mark_independent_helpers_sockets_face_and_secondary_rows(
        &plan, DLR_MAP_STATIC_BIND);
    verify_no_cross_side_cross_domain_or_accidental_duplicate_mapping(&plan);
    recount_mapping_modes(&plan);
    return plan;
}

/*
 * Present one useful state instead of warning per accommodated bone. Optional
 * omissions, namespaces, multilingual recognition, ignored extras, and bind
 * rows are grouped in Details. Import modals are reserved for an unreadable
 * file, missing requested stack, or unusable skeleton; build-time hard faults
 * receive one focused blocker.
 */
DlrReadinessResult dlr_classify_retarget_readiness_without_warning_spam(
    const DlrAnalyzedSkeleton *source,
    const DlrRetargetPlan *plan,
    bool exact_skeleton_match,
    bool requested_stack_exists,
    bool source_is_readable)
{
    DlrReadinessResult out = {0};
    out.suppressed_information_count = count_normal_accommodations(plan);

    if (!source_is_readable || !requested_stack_exists || !source->bone_count) {
        out.readiness = DLR_INCOMPATIBLE;
        out.severity = DLR_PRESENT_BLOCKING;
        out.row_label = "Cannot import -- FBX has no usable requested animation";
        out.show_import_modal = true;
        out.block_build = true;
    } else if (exact_skeleton_match) {
        out.readiness = DLR_READY_EXACT;
        out.severity = DLR_PRESENT_INFO;
        out.row_label = "Ready -- exact skeleton match";
    } else if (plan->manual_required_count) {
        out.readiness = DLR_NEEDS_ATTENTION;
        out.severity = DLR_PRESENT_ACTION_REQUIRED;
        out.row_label = one_named_ambiguous_chain_message(plan);
        out.single_action_label = "Fix mapping...";
        out.block_build = true;
    } else if (source->animated_domain != DLR_DOMAIN_FULL_BODY) {
        out.readiness = DLR_READY_PARTIAL;
        out.severity = DLR_PRESENT_INFO;
        out.row_label = partial_domain_bind_summary(source, plan);
    } else {
        out.readiness = DLR_READY_AUTOMATIC;
        out.severity = DLR_PRESENT_INFO;
        out.row_label = "Ready -- automatically retargeted";
    }
    return out;
}

/*
 * Construct the only built-in automatic DL2 advanced body policy. Coherence
 * checks are exact: game, built-in rig ID, 271-row skeleton hash/provenance,
 * descriptor inventory, single pelvis root, and policy version. The canonical
 * result has 52 body rows and 219 explicit bind rows, with no spatial-only or
 * non-body mappings.
 */
bool dlr_dl2_advanced_build_verified_body_map(
    const DlrAnalyzedSkeleton *source,
    const TargetRig *target,
    DlrRetargetPlan *out_plan,
    DlrMappingCertificate *out_certificate)
{
    if (!target_is_exact_coherent_builtin_dl2_player_advanced(target) ||
        target->bone_count != 271u ||
        source->archetype != DLR_ARCHETYPE_HUMANOID ||
        source->animated_domain == DLR_DOMAIN_FACIAL_ONLY ||
        !all_advanced_body_source_roles_are_unambiguous(source))
        return false;

    *out_plan = allocate_one_explicit_row_per_target_bone(target);
    map_rotation_only_global_bind_basis(out_plan, "pelvis", role(source, "pelvis"));

    map_direct_spine_slot(out_plan, source, 1u, "spine");
    map_direct_spine_slot(out_plan, source, 2u, "spine2");
    map_direct_spine_slot(out_plan, source, 3u, "spine3");
    map_direct_body_limb_head_neck_rows(out_plan, source);

    for_each_side(side) {
        set_bind_held_digit_base(out_plan, side, "finger10");
        set_bind_held_digit_base(out_plan, side, "finger20");
        set_bind_held_digit_base(out_plan, side, "finger30");
        set_bind_held_digit_base(out_plan, side, "finger40");
        map_index_middle_ring_pinky_segments_1_2_3(out_plan, source, side);
        map_thumb_segments_1_2_3_to_finger01_02_03(out_plan, source, side);
        leave_terminal_source_digit_segment_4_unused(out_plan, source, side);
    }

    set_unassigned_body_subdivisions_to_inherit_bind(out_plan);
    set_face_secondary_collar_camera_attachment_helper_socket_twist_end_to_bind(
        out_plan);
    recount_mapping_modes(out_plan);

    if (out_plan->row_count != 271u || out_plan->direct_count != 52u ||
        out_plan->inherit_bind_count + out_plan->static_bind_count != 219u ||
        out_plan->composed_count != 0u || out_plan->distributed_count != 0u ||
        out_plan->manual_required_count != 0u ||
        out_plan->spatial_only_count != 0u ||
        out_plan->non_body_mapped_count != 0u)
        return false;

    *out_certificate = certificate_from_live_source_target_and_rows(
        source, target, out_plan);
    return true;
}

/*
 * A serialized automatic_verified origin is descriptive, not authority. Every
 * build recomputes signatures and hashes the canonical rows. Version drift or
 * any descriptor/mode/count/hierarchy mismatch regenerates the plan or fails
 * closed. Local recipes are likewise keyed by source skeleton, name/parent,
 * and bind hashes; target rig, full-skeleton, and policy identities; analyzer,
 * planner, semantic-policy, and lexicon versions; and clip domain.
 */
bool dlr_dl2_advanced_revalidate_mapping_certificate(
    const DlrMappingCertificate *saved,
    const DlrAnalyzedSkeleton *live_source,
    const TargetRig *live_target,
    const DlrRetargetPlan *live_plan)
{
    DlrMappingCertificate recomputed = certificate_from_live_source_target_and_rows(
        live_source, live_target, live_plan);
    return target_is_exact_coherent_builtin_dl2_player_advanced(live_target) &&
           constant_time_equal(saved->analyzer_version,
                               recomputed.analyzer_version) &&
           constant_time_equal(saved->semantic_policy_version,
                               recomputed.semantic_policy_version) &&
           constant_time_equal(saved->source_skeleton_signature,
                               recomputed.source_skeleton_signature) &&
           constant_time_equal(saved->source_bind_signature,
                               recomputed.source_bind_signature) &&
           constant_time_equal(saved->target_rig_id,
                               recomputed.target_rig_id) &&
           constant_time_equal(saved->target_full_skeleton_hash,
                               recomputed.target_full_skeleton_hash) &&
           constant_time_equal(saved->target_provenance_hash,
                               recomputed.target_provenance_hash) &&
           constant_time_equal(saved->canonical_rows_hash,
                               recomputed.canonical_rows_hash) &&
           recomputed.target_row_count == 271u &&
           recomputed.mapped_body_count == 52u &&
           recomputed.explicit_bind_count == 219u &&
           recomputed.spatial_only_count == 0u &&
           recomputed.non_body_mapped_count == 0u &&
           recomputed.animated_critical_chains_resolved;
}

/*
 * Solver routing is fail-closed. Exact identity uses the exact solver. A live,
 * revalidated DL2 certificate or an explicitly reviewed/imported custom map may
 * use the mapped solver. Ordinary automatic_repair remains blocked. Legacy
 * unreviewed DL2 automatic_repair data is regenerated with a migration audit;
 * manual/imported mappings are preserved and never overwritten by migration.
 */
int dlr_select_verified_dl2_advanced_solver(
    const char *mapping_origin,
    bool exact_skeleton_match,
    const DlrMappingCertificate *certificate,
    const DlrAnalyzedSkeleton *source,
    const TargetRig *target,
    const DlrRetargetPlan *plan)
{
    if (exact_skeleton_match)
        return DLR_SOLVER_EXACT_RIG;

    if (string_equal(mapping_origin, "automatic_repair")) {
        if (target_is_exact_coherent_builtin_dl2_player_advanced(target))
            record_regenerate_instead_of_promote_migration_audit(source, target);
        return DLR_SOLVER_BLOCKED;
    }

    if (string_equal(mapping_origin, "automatic_verified") &&
        certificate && dlr_dl2_advanced_revalidate_mapping_certificate(
            certificate, source, target, plan))
        return DLR_SOLVER_MAPPED_RIG;

    if (string_equal(mapping_origin, "manual_reviewed") ||
        string_equal(mapping_origin, "imported_reviewed"))
        return reviewed_map_passes_live_safety_checks(source, target, plan)
            ? DLR_SOLVER_MAPPED_RIG : DLR_SOLVER_BLOCKED;

    return DLR_SOLVER_BLOCKED;
}

/*
 * Writer boundary: these policies feed the existing explicitly labeled ANM2
 * format-1 compatibility writer. They do not implement a native DL2
 * Header_Version2 writer. Native v2 support remains read/decode/export-to-FBX.
 */
