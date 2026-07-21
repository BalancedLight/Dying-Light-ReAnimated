/*
 * Reconstruction-oriented tool contract, not a claim of native DL2 writer
 * behavior. Legacy names are adapters at serialization boundaries only.
 */

typedef struct DLR_ActorFrame {
    Vec3 right;
    Vec3 up;
    Vec3 forward;
    float confidence;
} DLR_ActorFrame;

Vec3 dlr_map_root_vector_in_actor_basis(
    Vec3 raw_source_delta,
    float meters_per_source_unit_after_wrapper,
    DLR_ActorFrame source,
    DLR_ActorFrame target)
{
    Vec3 meters = raw_source_delta * meters_per_source_unit_after_wrapper;
    float lateral = dot(meters, source.right);
    float vertical = dot(meters, source.up);
    float forward = dot(meters, source.forward);
    return lateral * target.right + vertical * target.up + forward * target.forward;
}

void dlr_apply_target_neutral_root_ownership(
    Animation* clip,
    Rig* target,
    Bone* selected_root,
    int translation_mode,
    int heading_mode)
{
    Transform first = target_global(clip, selected_root, 0);
    for (unsigned frame = 0; frame < clip->frame_count; ++frame) {
        Transform current = target_global(clip, selected_root, frame);
        Quat delta = current.rotation * inverse(first.rotation);
        Quat heading = swing_twist_about_axis(delta, target->world_up).twist;

        if (heading_mode != DLR_HEADING_PRESERVE)
            current.rotation = inverse(heading) * current.rotation;
        if (translation_mode == DLR_ROOT_IN_PLACE)
            current.position = selected_root->bind_global.position;
        if (translation_mode == DLR_ROOT_TO_ACCUMULATOR) {
            Vec3 planar = reject(current.position - first.position, target->world_up);
            current.position -= planar;
            set_accumulator_translation(clip, frame, planar);
            set_accumulator_heading(clip, frame, heading);
        }
        set_target_global_as_local_through_live_parent(clip, selected_root, frame, current);
    }
}

void dlr_register_target_owned_locomotion_profiles(void)
{
    register_locomotion("dl1", "bip01", "l_foot", "r_foot", 0, 0, 0, 0);
    register_locomotion("dl2_advanced", "pelvis", "l_foot", "r_foot",
                        "l_sole_helper", "r_sole_helper", 0, 0);
    register_locomotion("dl2_legacy", "pelvis", "l_foot", "r_foot",
                        "l_sole_helper", "r_sole_helper", "l_iktarget", "r_iktarget");
}
