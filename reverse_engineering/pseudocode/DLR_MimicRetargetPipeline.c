/*
 * Named C-like reconstruction notes for DL ReAnimated facial animation.
 * This is documentation, not game code.
 */

typedef struct DLR_MimicTarget {
    unsigned int descriptor;
    unsigned int track_index;
    float neutral;
    float recommended_min;
    float recommended_max;
} DLR_MimicTarget;

typedef struct DLR_MimicMapRow {
    const char* source_shape;
    unsigned int target_descriptor;
    float weight;
    float bias;
    int enabled;
} DLR_MimicMapRow;

float DLR_SampleFbxBlendShapePercent(
    const FbxBlendShapeCurve* curve,
    long long tick,
    float default_percent)
{
    float raw = FbxSampleCurve(curve, tick, default_percent);
    float scale = FbxCurveLooksLikePercent(curve, default_percent) ? 0.01f : 1.0f;
    return raw * scale;
}

void DLR_ConsolidateFacialCurves(
    float* target_weights,
    const DLR_MimicTarget* targets,
    unsigned int target_count,
    const DLR_MimicMapRow* mapping,
    unsigned int mapping_count,
    const FbxFaceSample* source_frame)
{
    unsigned int i;
    for (i = 0; i < target_count; ++i)
        target_weights[i] = targets[i].neutral;

    for (i = 0; i < mapping_count; ++i) {
        const DLR_MimicMapRow* row = &mapping[i];
        int target_index;
        float source_value;
        if (!row->enabled)
            continue;
        target_index = DLR_FindMimicTargetByDescriptor(
            targets, target_count, row->target_descriptor);
        if (target_index < 0)
            continue;
        source_value = DLR_FindSourceShapeWeight(source_frame, row->source_shape);
        target_weights[target_index] += source_value * row->weight + row->bias;
    }
}

void DLR_WriteMimicAnm2Track(
    float out_components[9],
    float morph_weight)
{
    out_components[0] = 0.0f;  /* rx */
    out_components[1] = 0.0f;  /* ry */
    out_components[2] = 0.0f;  /* rz */
    out_components[3] = morph_weight; /* tx is scalar weight for mimic tracks */
    out_components[4] = 0.0f;
    out_components[5] = 0.0f;
    out_components[6] = 1.0f;
    out_components[7] = 1.0f;
    out_components[8] = 1.0f;
}

int DLR_ChooseBodyAndMimicContent(
    int requested_mode,
    int target_has_mimic_profile,
    int fbx_has_animated_blendshapes)
{
    if (requested_mode != DLR_CONTENT_AUTO)
        return requested_mode;
    if (target_has_mimic_profile && fbx_has_animated_blendshapes)
        return DLR_CONTENT_BODY_AND_MIMIC;
    return DLR_CONTENT_BODY_ONLY;
}

void DLR_ApplyRootMotionSourceBoneOverride(
    Anm2Clip* body_clip,
    const FbxSkeletonAnimation* source,
    const char* source_motion_bone,
    int root_policy)
{
    /*
     * The target is still bip01. Only the source position used to author its
     * translation changes. OffsetHelper horizontal translation is updated for
     * accumulator mode. Existing stable body-frame yaw is preserved.
     */
    Transform source_bind = FbxGlobalBindTransform(source, source_motion_bone);
    Transform source_first = FbxGlobalAnimatedTransform(source, source_motion_bone, 0);
    unsigned int frame;

    for (frame = 0; frame < body_clip->frame_count; ++frame) {
        Transform animated = FbxGlobalAnimatedTransform(source, source_motion_bone, frame);
        Vec3 absolute = DLR_MapSourceVectorToTarget(
            animated.translation - source_bind.translation);

        if (root_policy == DLR_ROOT_BIP01) {
            Anm2SetTrackTranslation(body_clip, HASH_BIP01, frame, absolute);
        } else if (root_policy == DLR_ROOT_MOTION_ACCUMULATOR) {
            Vec3 accumulated = DLR_MapSourceVectorToTarget(
                animated.translation - source_first.translation);
            Vec3 horizontal = accumulated;
            horizontal.y = 0.0f;
            Anm2SetTrackTranslation(body_clip, HASH_OFFSET_HELPER, frame, horizontal);
            Anm2SetTrackTranslation(body_clip, HASH_BIP01, frame, absolute - horizontal);
        }
    }
}

void DLR_EmitSeparateMimicResource(
    RpackAnimationLibrary* library,
    AnimationScript* script,
    const char* body_resource_name,
    const Anm2Clip* mimic_clip)
{
    char mimic_name[160];
    FormatString(mimic_name, sizeof(mimic_name), "%s_mimic", body_resource_name);
    RpackAddAnimation(library, mimic_name, mimic_clip);
    AnimationScriptAddSequence(script, mimic_name, mimic_clip->frame_count, mimic_clip->fps);
}
