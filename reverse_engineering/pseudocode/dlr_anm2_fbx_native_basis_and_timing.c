/*
 * DL ReAnimated ANM2/FBX native-basis and timing contract.
 *
 * This is implementation-oriented pseudocode, not game source.  It records
 * the invariants enforced by the production Python and Blender paths.
 */

struct FbxDeclaredTimebase {
    int time_mode;
    double declared_fps;
    double custom_frame_rate;
    const char *source;
    const char *confidence;
};

FbxDeclaredTimebase ResolveFbxTimebase(GlobalSettings settings,
                                       TickDeltaList key_deltas)
{
    /* Valid GlobalSettings always wins over curve-spacing inference. */
    if (settings.TimeMode == 14 && IsFinitePositive(settings.CustomFrameRate))
        return Declared(settings.TimeMode, settings.CustomFrameRate);

    switch (settings.TimeMode) {
    case 0:  return Declared(0, 30.0);
    case 1:  return Declared(1, 120.0);
    case 2:  return Declared(2, 100.0);
    case 3:  return Declared(3, 60.0);
    case 4:  return Declared(4, 50.0);
    case 5:  return Declared(5, 48.0);
    case 6:  return Declared(6, 30.0);
    case 7:  return Declared(7, 30.0);              /* drop-frame timecode */
    case 8:
    case 9:  return Declared(settings.TimeMode, 30000.0 / 1001.0);
    case 10: return Declared(10, 25.0);
    case 11: return Declared(11, 24.0);
    case 12: return Declared(12, 1000.0);
    case 13: return Declared(13, 24000.0 / 1001.0);
    case 15: return Declared(15, 96.0);
    case 16: return Declared(16, 72.0);
    case 17: return Declared(17, 60000.0 / 1001.0);
    case 18: return Declared(18, 120000.0 / 1001.0);
    }

    if (HasStableModalPositiveDelta(key_deltas))
        return InferredLowConfidence(FBX_TICKS_PER_SECOND /
                                     ModalPositiveDelta(key_deltas));
    return FallbackLowConfidence(30.0);
}

void CreateNativeEditBone(int i, Matrix4 converted_bind_global,
                          Vec3 child_pivot, Vec3 parent_pivot)
{
    Matrix3 native_rotation = ProperOrthonormalRotation(converted_bind_global);
    Vec3 head = Translation(converted_bind_global);

    /* Child/parent pivots select only a readable positive display length. */
    double length = UsefulDisplayLength(head, child_pivot, parent_pivot);
    Vec3 tail = head + ColumnY(native_rotation) * length;

    EditBone bone = NewEditBone(i, head, tail);
    AlignRoll(bone, ColumnZ(native_rotation));
    bone.parent = AuthoredCrigParent(i);  /* never display-grandparent twists */
    bone.use_connect = false;
}

void ValidateNativeRestBasis(Bone bone, Matrix4 converted_bind_global)
{
    double degrees = QuaternionAngularError(
        Normalize(Rotation(bone.matrix_local)),
        Normalize(Rotation(converted_bind_global)));

    RecordRestBasisError(bone.name, degrees);
    if (degrees > 0.05)
        FailBeforeFbxWrite("native rest basis exceeds hard tolerance");
    /* 0.01 < degrees <= 0.05 is retained as an explicit edge-case record. */
}

Matrix4 PoseBasisForFrame(int bone, Matrix4 desired_native_global)
{
    Matrix4 D = BlenderDisplayRestGlobal(bone);
    int parent = AuthoredArmatureParent(bone);

    /* Desired globals are native A, never A*C. */
    if (parent < 0)
        return Inverse(D) * desired_native_global;

    Matrix4 Drel = Inverse(BlenderDisplayRestGlobal(parent)) * D;
    Matrix4 Arel = Inverse(DesiredNativeGlobal(parent)) * desired_native_global;
    return Inverse(Drel) * Arel;
}

void AuditFinalRootActionEveryFrame(Action action, Matrix4 expected_native_root[])
{
    double max_angle = 0.0;
    double max_heading = 0.0;
    double max_translation = 0.0;

    for (int frame = 0; frame < FrameCount(action); ++frame) {
        EvaluateSceneOnce(frame);
        Matrix4 actual = EvaluatedPrimaryRootGlobal(action);
        max_angle = Max(max_angle, QuaternionAngularError(actual,
                                                          expected_native_root[frame]));
        max_heading = Max(max_heading, SwingTwistHeadingError(actual,
                                                               expected_native_root[frame],
                                                               BlenderUpAxis));
        max_translation = Max(max_translation, TranslationDistance(actual,
                                                                    expected_native_root[frame]));
    }

    if (max_angle > 0.05 || max_heading > 0.05 || max_translation > 1.0e-5)
        FailBeforeFbxWrite("native root parity failed");
}

Animation ResampleAnimation(Animation input, double input_fps, double output_fps)
{
    int Nin = input.frame_count;
    if (Nin == 1)
        return CopyWithCadence(input, output_fps);

    double duration = (Nin - 1) / input_fps;
    int Nout = Round(duration * output_fps) + 1;
    Nout = Max(Nout, 2);

    Animation output = Allocate(Nout, input.track_count, output_fps);
    for (int j = 0; j < Nout; ++j) {
        double position = j * input_fps / output_fps;
        if (j == Nout - 1)
            position = Nin - 1; /* exact final endpoint */

        int lo = FloorClamp(position, 0, Nin - 1);
        int hi = Min(lo + 1, Nin - 1);
        double alpha = position - lo;
        output.translation[j] = Lerp(input.translation[lo], input.translation[hi], alpha);
        output.scale[j] = Lerp(input.scale[lo], input.scale[hi], alpha);
        output.rotation[j] = ShortestHemisphereSlerpOrNlerp(
            Normalize(input.rotation[lo]), Normalize(input.rotation[hi]), alpha);
    }
    ForceExactEndpointsModuloQuaternionSign(output, input);
    MakeQuaternionHemisphereContinuous(output.rotation);
    return output;
}

/*
 * Every standalone/intermediate <name>.anm2 has a deterministic
 * <name>.anm2.dlrmeta.json.  Timing is used only after its ANM2 SHA-256 and
 * frame count match.  Valid provenance defaults reverse conversion to
 * sample_fps -> source_fbx_fps; otherwise reverse conversion remains 30->30.
 */
