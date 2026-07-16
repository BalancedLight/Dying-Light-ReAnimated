# Chrome 6 custom model/rig documentation reconstructions

Status: documentation-only behavioral reconstruction.

These named C-like routines are neutral descriptions of the source-MSH, CRIG, and animation contracts used by DL ReAnimated. They are not recovered runtime symbols, addresses, ABI declarations, hooks, patches, or callable production code. Types are illustrative and error paths are intentionally explicit.

The validated Chrome 6 bind and palette rules represented here are:

```text
global_bind[node] = global_bind[parent] * local_bind[node]
reference[node]   = inverse(global_bind[node])

subset palette    = uint16 global source-MSH node indexes
vertex bone byte  = uint8 local index into the current subset palette
```

## Illustrative data types

```c
typedef struct {
    uint16_t global_node;
    float weight;
} Ce6GlobalInfluence;

typedef struct {
    uint8_t local_palette_index;
    float weight;
} Ce6LocalInfluence;

typedef struct {
    uint32_t source_triangle_index;
    uint16_t material_index;
    Ce6GlobalInfluence corner[3][4];
    VertexKey complete_corner_key[3];
} Ce6WeightedTriangle;

typedef struct {
    uint16_t material_index;
    uint16_t global_palette[256];
    uint32_t global_palette_count;
    Vertex vertices[65535];
    uint32_t vertex_count;
    uint32_t indices[];
} Ce6SkinSubset;
```

The complete vertex key includes position, normal, tangent/binormal identity, UV, color, normalized global influences, and morph-delta identity when present. Equal positions alone are never enough to merge a seam.

## `ce6_partition_skinned_mesh_by_subset_palette()`

Purpose: deterministically partition final weighted triangles into legal per-material source-MSH subsets without treating total hierarchy size as a byte-sized field.

```c
bool ce6_partition_skinned_mesh_by_subset_palette(
    const Ce6WeightedTriangle *triangles,
    uint32_t triangle_count,
    Ce6SkinSubsetList *output,
    Ce6PartitionReport *report,
    Ce6Error *error)
{
    group triangles by material_index;
    preserve material order and source triangle order;

    for each material group {
        WorkingSubset current = empty(material_index);

        for each triangle in stable source order {
            GlobalNodeSet tri_nodes =
                union_of_positive_top_four_global_influences(triangle.corner);

            if (tri_nodes.count > 12) {
                return fail(error,
                    triangle.source_triangle_index,
                    "a three-corner triangle cannot use more than 12 "
                    "distinct nodes after top-four normalization");
            }

            GlobalNodeSet candidate_palette =
                union(current.global_nodes, tri_nodes);
            VertexKeySet candidate_vertices =
                union(current.complete_vertex_keys,
                      triangle.complete_corner_key);

            if (!current.empty &&
                (candidate_palette.count > 256 ||
                 candidate_vertices.count > 65535)) {
                flush_subset_in_ascending_global_node_order(
                    current, output, report);
                current = empty(material_index);
                candidate_palette = tri_nodes;
                candidate_vertices = triangle.complete_corner_key;
            }

            if (candidate_palette.count > 256 ||
                candidate_vertices.count > 65535) {
                return fail(error,
                    triangle.source_triangle_index,
                    "one triangle cannot fit an otherwise empty legal subset");
            }

            append_triangle_without_reordering(current, triangle);
            current.global_nodes = candidate_palette;
            current.complete_vertex_keys = candidate_vertices;
        }

        if (!current.empty)
            flush_subset_in_ascending_global_node_order(
                current, output, report);
    }

    return true;
}
```

Properties:

- partitioning is independent per material;
- the stable greedy pass makes output reproducible;
- a partition flushes before palette or unique-vertex overflow;
- palette ordering is deterministic ascending global node index;
- no influence is dropped to force palette fit;
- total MSH hierarchy nodes are checked against actual node/parent/index fields separately;
- every report row records material, partition, global palette, triangle/vertex counts, maximum influences, weight/fallback totals, quantization error, and tangent policy.

## `ce6_resolve_vertex_local_palette_indexes()`

Purpose: perform the only global-to-local skin-index boundary, after the final subset palette is known.

```c
bool ce6_resolve_vertex_local_palette_indexes(
    const Ce6GlobalInfluence global_influences[4],
    const uint16_t *subset_global_palette,
    uint32_t palette_count,
    Ce6LocalInfluence local_influences[4],
    Ce6Error *error)
{
    if (palette_count == 0 || palette_count > 256)
        return fail(error, "subset palette must contain 1..256 entries");

    reject_duplicate_global_nodes(subset_global_palette, palette_count);

    for (uint32_t slot = 0; slot != 4; ++slot) {
        uint16_t global = global_influences[slot].global_node;
        int32_t local = find_global_in_palette(
            subset_global_palette, palette_count, global);

        if (local < 0)
            return fail(error,
                "weighted global node is absent from final subset palette");
        if (local > UINT8_MAX)
            return fail(error,
                "resolved local palette index does not fit uint8");

        local_influences[slot].local_palette_index = (uint8_t)local;
        local_influences[slot].weight = global_influences[slot].weight;

        /* Proof that no global node identity was stored/lost in the byte. */
        if (subset_global_palette[local] != global)
            return fail(error, "local/global palette round trip failed");
    }

    return true;
}
```

The byte is always local. A global node index is stored only in the subset's uint16 palette. Weight normalization/quantization is validated independently from index identity.

## `ce6_author_inverse_global_reference_matrices()`

Purpose: author the known Chrome 6 source-MSH local/global/reference relationship without changing the validated rule.

```c
bool ce6_author_inverse_global_reference_matrices(
    Ce6MshNode *nodes,
    uint32_t node_count,
    float tolerance,
    Ce6Error *error)
{
    for (uint32_t i = 0; i != node_count; ++i) {
        int32_t parent = nodes[i].parent_physical_index;

        if (parent >= (int32_t)i || parent < -1)
            return fail_node(error, i,
                "parents must precede children");

        Matrix4 local = finite_matrix4(nodes[i].local_matrix3x4);
        Matrix4 global =
            parent < 0 ? local : nodes[parent].global_bind * local;

        if (is_singular(global))
            return fail_node(error, i, "global bind is singular");

        Matrix4 reference = inverse(global);
        if (!approximately_identity(global * reference, tolerance))
            return fail_node(error, i,
                "global * inverse-global reference is not identity");

        nodes[i].global_bind = global;
        nodes[i].reference_matrix3x4 =
            store_affine_matrix3x4(reference);
    }

    return true;
}
```

This function never substitutes identity for a failed inversion and never builds a reference from an independently evaluated hierarchy.

## `ce6_build_model_rig_contract()`

Purpose: freeze the exact source-MSH hierarchy immediately before serialization so all downstream artifacts share one bind owner.

```c
bool ce6_build_model_rig_contract(
    const Ce6SourceMsh *authored_msh,
    const FbxTransformContract *coordinate_contract,
    const SourceIdentity *source,
    Ce6ModelRigContract *contract,
    Ce6Error *error)
{
    validate_source_msh_structure(authored_msh);

    contract->source_fbx_sha256 = source->sha256;
    contract->source_model_name = source->model_name;
    contract->authored_msh_resource_name = source->resource_name;
    contract->coordinate_contract = immutable_copy(coordinate_contract);

    for (uint32_t i = 0; i != authored_msh->node_count; ++i) {
        const Ce6MshNode *node = &authored_msh->nodes[i];
        RigNode row = {
            .physical_index = i,
            .name = immutable_utf8(node->name),
            .normalized_name = nfkc_casefold(node->name),
            .parent_physical_index = node->parent,
            .node_type = classify_bone_helper_or_mesh(node),
            .local = node->local_matrix3x4,
            .global = reconstruct_global_from_authored_locals(i),
            .inverse_global_reference = node->reference_matrix3x4,
            .descriptor = descriptor_if_animated(node),
            .deform = node_is_bone(node),
            .helper = node_is_helper(node)
        };
        append(contract->nodes, row);
    }

    validate_unique_normalized_animation_names(contract);
    validate_unique_animation_descriptors(contract);
    validate_every_global_and_reference_identity(contract);

    contract->skeleton_hash = hash_names_parents_types_and_roles(contract);
    contract->bind_hash =
        hash_all_locals_globals_and_inverse_global_references(contract);
    contract->descriptor_hash = hash_animation_descriptors(contract);
    contract->contract_id = derive_contract_id(contract->bind_hash);

    return true;
}
```

Source-MSH nodes, ASCR/BSCR animation entities, generated CRIG bones/descriptors, model report, and animation target metadata consume this immutable contract. The generated CRIG records the contract and full-bind hashes. Same names/parents with a different bind do not compare equal.

## `ce6_retarget_source_animation_to_custom_rig_contract()`

Purpose: route exact, source-superset, or explicitly reviewed cross-rig animation to one authored target without changing target proportions accidentally.

```c
bool ce6_retarget_source_animation_to_custom_rig_contract(
    const CanonicalFbxAnimation *source,
    const Ce6ModelRigContract *target,
    const ReviewedBoneMap *optional_map,
    const ClipRootPolicy *root_policy,
    Ce6AnimationTracks *output,
    Ce6Error *error)
{
    Relationship relation =
        classify_names_required_bones_and_target_ancestry(source, target);

    if (relation == INCOMPATIBLE && !map_is_explicitly_reviewed(optional_map))
        return fail(error,
            "review every required cross-rig row before build");

    for each frame {
        initialize_every_target_local_to_authored_bind(output, target, frame);

        for each ordinary target row {
            MappingRow row = resolve_mapping_row(
                relation, optional_map, target_row);

            if (row.intentionally_unmapped || row.transfer == BIND)
                continue;

            Matrix4 candidate;

            switch (row.transfer) {
            case GLOBAL_BIND_BASIS:
                correction =
                    inverse(source.bind_global[row.source]) *
                    target.bind_global[row.target];
                target_global =
                    source.animated_global[row.source][frame] * correction;
                candidate =
                    inverse(current_target_parent_global(row.target, frame)) *
                    target_global;
                break;

            case ROTATION_DELTA:
                candidate = target.bind_local[row.target];
                candidate.rotation =
                    source_rest_relative_rotation(row.source, frame) *
                    target.bind_local[row.target].rotation;
                /* Target translation and scale remain authored. */
                break;

            case REST_RELATIVE:
                candidate =
                    target.bind_local[row.target] *
                    source_rest_relative_local_delta(row.source, frame);
                break;

            case COPY_LOCAL:
                require_proven_identical_local_basis(row);
                candidate = source.local[row.source][frame];
                break;
            }

            merge_only_owned_components(
                output->local[row.target][frame],
                candidate,
                row.component_policy);
        }

        apply_reviewed_helper_fanout_after_primary_body(output, frame);
        apply_root_displacement_separately(output, root_policy, frame);
        reconstruct_and_validate_target_hierarchy(output, target, frame);
    }

    validate_unauthorized_non_root_translation_is_zero(output);
    validate_target_lengths_within_row_policies(output);
    validate_finite_tracks_and_non_singular_scales(output);
    return true;
}
```

Exact and source-superset rows use the same authoritative global bind-basis formula. Extra source face/cloth/accessory/camera/weapon/helper bones are ignored unless mapped and cannot remove required target tracks. Cross-rig deform rows normally use rotation-only ownership, preserving target translation and bind scale. Root motion is not an ordinary row policy.

## `ce6_validate_bind_pose_skin_identity()`

Purpose: prove that every emitted local palette byte resolves to the authored global node and that inverse-global references leave emitted positions unchanged at bind.

```c
bool ce6_validate_bind_pose_skin_identity(
    const Ce6SourceMsh *msh,
    const Ce6ModelRigContract *contract,
    float tolerance,
    Ce6BindSkinReport *report,
    Ce6Error *error)
{
    DeterministicSample sample =
        choose_vertices_from_every_skinned_partition(msh);

    for each sampled vertex {
        Vec3 original = vertex.position;
        Vec3 skinned = zero;
        float weight_sum = 0;

        for each positive influence slot {
            uint8_t local = vertex.bone_indices[slot];
            const Ce6SkinSubset *subset = vertex.drawing_subset;

            if (local >= subset->global_palette_count)
                return fail_vertex(error, vertex,
                    "local palette byte is outside current subset");

            uint16_t global = subset->global_palette[local];
            if (global >= contract->node_count)
                return fail_vertex(error, vertex,
                    "subset global node is outside authored hierarchy");

            Matrix4 skin_at_bind =
                contract->nodes[global].global *
                contract->nodes[global].inverse_global_reference;

            skinned += decode_quantized_weight(vertex, slot) *
                       transform_point(skin_at_bind, original);
            weight_sum += decode_quantized_weight(vertex, slot);

            report_palette_resolution(
                report, subset, vertex, local, global);
        }

        record_weight_sum_and_quantization_error(report, weight_sum);
        record_bind_position_error(report, vertex, skinned, original);

        if (distance(skinned, original) > tolerance)
            return fail_vertex(error, vertex,
                "CPU bind skin does not reproduce emitted position");
    }

    return true;
}
```

The report retains maximum unquantized/quantized bind error, worst partition and vertex, worst influence/local-to-global resolution, weight sums before/after quantization, and maximum quantization error.

## Boundaries

These reconstructions intentionally do not describe:

- an engine function address or binary patch point;
- an ABI, vtable, calling convention, or runtime hook;
- gameplay skin/CHR/physics/ragdoll authoring;
- native Dying Light 2 format-42 writing;
- editor/game validation.

Their purpose is to keep the observed Chrome 6 format rules and DL ReAnimated's offline invariants reviewable without moving reverse-engineering pseudocode into production modules.

