using System.Collections.Immutable;
using System.Globalization;
using ReAnimated.Core.Domain;
using ReAnimated.Core.Mathematics;
using ReAnimated.Core.ModelAuthoring;

namespace ReAnimated.Retargeting.Conformance;

/// <summary>
/// One emitted bone of the conformed rig, positioned in source model space.
/// </summary>
/// <remarks>
/// Chrome's local +X frames are authored later by the retail-validated
/// <c>Dl1CustomModelRigPreparer</c>; <see cref="Orientation"/> only supplies
/// the roll about that aim axis.
/// </remarks>
public sealed record RigConformedBone
{
    public required int Index { get; init; }

    public required string Name { get; init; }

    public required int ParentIndex { get; init; }

    public required BoneKind Kind { get; init; }

    public required bool IsDeform { get; init; }

    public required RigBoneDisposition Disposition { get; init; }

    /// <summary>Source rig bone index, or -1 for a synthesized entity.</summary>
    public required int SourceBoneIndex { get; init; }

    /// <summary>Template entity index, or -1 for a retained extra bone.</summary>
    public required int TemplateIndex { get; init; }

    public required Vector3D Position { get; init; }

    /// <summary>
    /// Rotation-only reference frame supplying the roll about the emitted aim
    /// axis. Template entities take DL1's own rest frame; retained extras take
    /// the frame they end up in after the rest-pose transfer.
    /// </summary>
    public required TransformMatrix Orientation { get; init; }

    /// <summary>
    /// How far the rest-pose transfer moved this joint from where the source
    /// model had it, in meters. This is the number that says whether the
    /// conversion altered the character, not whether the rig is correct.
    /// </summary>
    public required double OffsetFromSourceJoint { get; init; }

    /// <summary>
    /// Source segment length divided by DL1's at the solved scale. 1.0 means
    /// the model already agrees with DL1's proportion; far from 1.0 means this
    /// limb is stretched or compressed to reach it.
    /// </summary>
    public required double SegmentRatio { get; init; }
}

public sealed record RigConformanceOptions
{
    /// <summary>
    /// Blends segment lengths between the model's own and DL1's. 1.0, the
    /// default, matches DL1 exactly so stock clips play undistorted; 0.0 keeps
    /// the model's proportions and accepts that clips will stretch its limbs.
    /// Bone <em>directions</em> always come from DL1's rest pose - that is what
    /// makes the bind pose animate correctly, and it is not negotiable.
    /// </summary>
    public double ConformanceStrength { get; init; } = 1.0;

    /// <summary>
    /// Manual world-space placements keyed by emitted bone name, applied before
    /// the rest-pose transfer so the mesh follows a moved joint.
    /// </summary>
    public ImmutableDictionary<string, Vector3D> PositionOverrides { get; init; } =
        ImmutableDictionary<string, Vector3D>.Empty;
}

/// <summary>
/// Reports a limb whose proportion is far enough from DL1's that reaching it
/// visibly changes the character.
/// </summary>
public sealed record RigConformanceWarning(
    string BoneName,
    double SegmentRatio,
    double OffsetFromSourceJoint,
    string Message);

public sealed class RigConformanceResult
{
    public RigConformanceResult(
        Dl1RigTemplate template,
        RigLandmarkSolution landmark,
        double conformanceStrength,
        IEnumerable<RigConformedBone> bones,
        IEnumerable<RigConformanceWarning> warnings,
        RigRestPoseTransferResult restPoseTransfer)
    {
        ArgumentNullException.ThrowIfNull(template);
        ArgumentNullException.ThrowIfNull(landmark);
        ArgumentNullException.ThrowIfNull(bones);
        ArgumentNullException.ThrowIfNull(warnings);
        ArgumentNullException.ThrowIfNull(restPoseTransfer);

        TemplateId = template.TemplateId;
        Landmark = landmark;
        ConformanceStrength = conformanceStrength;
        Bones = bones.ToImmutableArray();
        Warnings = warnings.ToImmutableArray();
        RestPoseTransfer = restPoseTransfer;
    }

    public string TemplateId { get; }

    public RigLandmarkSolution Landmark { get; }

    public double ConformanceStrength { get; }

    public ImmutableArray<RigConformedBone> Bones { get; }

    public ImmutableArray<RigConformanceWarning> Warnings { get; }

    /// <summary>
    /// The source rig posed into this skeleton's rest pose, and the rigid
    /// per-bone transforms that carry the mesh with it.
    /// </summary>
    public RigRestPoseTransferResult RestPoseTransfer { get; }

    /// <summary>
    /// Largest distance the rest-pose transfer moved any joint from where the
    /// model had it.
    /// </summary>
    public double MaximumJointDisplacement =>
        Bones.Length == 0 ? 0.0 : Bones.Max(static bone => bone.OffsetFromSourceJoint);
}

/// <summary>
/// Places a DL1 target skeleton onto a specific model, then poses the model's
/// own rig into it.
/// </summary>
/// <remarks>
/// <para>
/// Bone <em>directions</em> come from DL1's rest pose, always. Dying Light
/// couples a bone's inverse bind to its place in the hierarchy and its clips
/// key per-bone translation, so a bind pose that keeps the model's own joint
/// angles cannot play stock animation cleanly no matter how the lengths are
/// chosen. On a Character Creator export those angles differ from DL1's by 22
/// degrees on average and 57 at the forearm.
/// </para>
/// <para>
/// Segment <em>lengths</em> are the negotiable part, blended by
/// <see cref="RigConformanceOptions.ConformanceStrength"/> between DL1's and
/// the model's own. The mesh is then carried into the resulting rest pose by
/// <see cref="RigRestPoseTransfer"/>, which is what keeps the bones inside the
/// geometry.
/// </para>
/// <para>
/// The template is anchored at the pelvis rather than the root because DL1's
/// <c>bip01</c> is co-located with <c>pelvis</c> at hip height, while many
/// source rigs put their root on the floor.
/// </para>
/// </remarks>
public static class RigConformanceSolver
{
    /// <summary>
    /// A limb whose proportion is off by more than this fraction is reported:
    /// reaching DL1's length visibly lengthens or shortens it.
    /// </summary>
    private const double WarningRatioThreshold = 0.15;

    /// <summary>
    /// Template offsets at or below this are a declared coincidence rather than
    /// a real segment - <c>bip01</c> with <c>pelvis</c>, a twist helper with its
    /// joint - and are preserved at any strength.
    /// </summary>
    private const double CoincidentThreshold = 1e-6;

    public static RigConformanceResult Solve(
        Dl1RigTemplate template,
        RigDefinition sourceRig,
        RigCorrespondence correspondence,
        RigLandmarkSolution landmark,
        RigConformanceOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(template);
        ArgumentNullException.ThrowIfNull(sourceRig);
        ArgumentNullException.ThrowIfNull(correspondence);
        ArgumentNullException.ThrowIfNull(landmark);
        options ??= new RigConformanceOptions();

        double strength = Math.Clamp(options.ConformanceStrength, 0.0, 1.0);
        double scale = landmark.UniformScale;
        ImmutableArray<TransformMatrix> sourceGlobals =
            sourceRig.CreateBindPose().GlobalMatrices;

        ImmutableArray<RigCorrespondenceRow> templateRows = correspondence.Rows
            .Where(static row => row.TemplateIndex >= 0)
            .OrderBy(static row => row.TemplateIndex)
            .ToImmutableArray();
        if (templateRows.Length != template.EntityCount)
        {
            throw new InvalidOperationException(
                "The correspondence does not cover every template entity exactly once.");
        }

        ImmutableArray<RigCorrespondenceRow> extraRows = correspondence.Rows
            .Where(static row =>
                row.Disposition == RigBoneDisposition.Extra &&
                row.SourceBoneIndex >= 0)
            .OrderBy(static row => row.SourceBoneIndex)
            .ToImmutableArray();

        // Emitted order: template entities in template order, then retained
        // extras in source order. Both halves stay parents-first.
        var emittedIndexByTemplate = new int[template.EntityCount];
        var emittedIndexBySource = new Dictionary<int, int>();
        for (int index = 0; index < templateRows.Length; index++)
        {
            emittedIndexByTemplate[templateRows[index].TemplateIndex] = index;
            if (templateRows[index].SourceBoneIndex >= 0)
            {
                emittedIndexBySource[templateRows[index].SourceBoneIndex] = index;
            }
        }

        for (int index = 0; index < extraRows.Length; index++)
        {
            emittedIndexBySource[extraRows[index].SourceBoneIndex] =
                templateRows.Length + index;
        }

        Vector3D templatePelvis = ResolveTemplatePelvis(template, templateRows);
        var templatePositions = new Vector3D[template.EntityCount];
        var segmentRatios = new double[template.EntityCount];
        Array.Fill(segmentRatios, 1.0);

        // 1. Place the target skeleton. Directions are DL1's; only lengths blend.
        for (int index = 0; index < templateRows.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RigCorrespondenceRow row = templateRows[index];
            Dl1RigTemplateEntity entity = template[row.TemplateIndex];
            Vector3D position;

            if (entity.ParentIndex < 0)
            {
                // The root is placed so the pelvis lands on the solved anchor,
                // never at the source root's own position.
                position = landmark.PelvisAnchor +
                    ((entity.GlobalRestMatrix.Translation - templatePelvis) * scale);
            }
            else
            {
                Vector3D parentPosition = templatePositions[entity.ParentIndex];
                Vector3D templateOffset =
                    entity.GlobalRestMatrix.Translation -
                    template[entity.ParentIndex].GlobalRestMatrix.Translation;
                double templateLength = templateOffset.Length * scale;

                if (templateLength <= CoincidentThreshold ||
                    !templateOffset.TryNormalize(out Vector3D direction, 1e-12))
                {
                    position = parentPosition;
                }
                else
                {
                    double sourceLength = MeasureSourceSegment(
                        row,
                        templateRows[emittedIndexByTemplate[entity.ParentIndex]],
                        sourceGlobals) ?? templateLength;
                    segmentRatios[row.TemplateIndex] = templateLength > 1e-9
                        ? sourceLength / templateLength
                        : 1.0;
                    double blended =
                        (sourceLength * (1.0 - strength)) + (templateLength * strength);
                    position = parentPosition + (direction * blended);
                }
            }

            templatePositions[row.TemplateIndex] =
                ApplyOverride(options, entity.Name, position);
        }

        // 2. Pose the model's own rig into that skeleton, carrying the mesh.
        var targetPositions = ImmutableArray.CreateBuilder<Vector3D?>(sourceRig.BoneCount);
        targetPositions.Count = sourceRig.BoneCount;
        foreach (RigCorrespondenceRow row in templateRows)
        {
            if (row.Disposition != RigBoneDisposition.Mapped ||
                row.SourceBoneIndex < 0)
            {
                continue;
            }

            // The root is a placement convention, not an anatomical joint:
            // DL1's bip01 sits at hip height while a Character Creator
            // RL_BoneRoot sits on the floor. Pinning one onto the other would
            // translate the whole character up by the hip height, so the root
            // is left to follow its own hierarchy and the pelvis carries the
            // placement.
            if (string.Equals(row.Role, "body.root", StringComparison.Ordinal))
            {
                continue;
            }

            targetPositions[row.SourceBoneIndex] =
                templatePositions[row.TemplateIndex];
        }

        RigRestPoseTransferResult transfer = RigRestPoseTransfer.Solve(
            sourceRig,
            sourceGlobals,
            targetPositions.MoveToImmutable(),
            cancellationToken);

        // 3. Emit. Template entities take DL1's own rest frame; retained extras
        //    take the frame the transfer put them in.
        int total = templateRows.Length + extraRows.Length;
        var bones = ImmutableArray.CreateBuilder<RigConformedBone>(total);
        var warnings = ImmutableArray.CreateBuilder<RigConformanceWarning>();

        for (int index = 0; index < templateRows.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RigCorrespondenceRow row = templateRows[index];
            Dl1RigTemplateEntity entity = template[row.TemplateIndex];
            Vector3D position = templatePositions[row.TemplateIndex];
            double offset = row.SourceBoneIndex >= 0
                ? Vector3D.Distance(
                    position,
                    sourceGlobals[row.SourceBoneIndex].Translation)
                : 0.0;
            double ratio = segmentRatios[row.TemplateIndex];

            bones.Add(new RigConformedBone
            {
                Index = index,
                Name = entity.Name,
                ParentIndex = entity.ParentIndex < 0
                    ? -1
                    : emittedIndexByTemplate[entity.ParentIndex],
                Kind = entity.Kind,
                IsDeform = entity.IsDeform,
                Disposition = row.Disposition,
                SourceBoneIndex = row.SourceBoneIndex,
                TemplateIndex = row.TemplateIndex,
                Position = position,
                Orientation = RotationOnly(entity.GlobalRestMatrix),
                OffsetFromSourceJoint = offset,
                SegmentRatio = ratio,
            });

            if (row.Disposition == RigBoneDisposition.Mapped &&
                Math.Abs(ratio - 1.0) > WarningRatioThreshold)
            {
                warnings.Add(new RigConformanceWarning(
                    entity.Name,
                    ratio,
                    offset,
                    string.Create(
                        CultureInfo.InvariantCulture,
                        $"'{entity.Name}' is {ratio:P0} of DL1's rest length at this scale, " +
                        $"so conforming moves it {offset * 100.0:F1} cm from the model's own joint " +
                        $"and changes that limb's proportion.")));
            }
        }

        for (int index = 0; index < extraRows.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RigCorrespondenceRow row = extraRows[index];
            BoneDefinition bone = sourceRig.Bones[row.SourceBoneIndex];
            int parentEmitted = ResolveExtraParent(
                sourceRig,
                bone,
                emittedIndexBySource);
            TransformMatrix posed = transfer.PosedGlobals[row.SourceBoneIndex];

            bones.Add(new RigConformedBone
            {
                Index = templateRows.Length + index,
                Name = bone.Name,
                ParentIndex = parentEmitted,
                Kind = bone.Kind == BoneKind.Root ? BoneKind.Helper : bone.Kind,
                IsDeform = bone.Kind is BoneKind.Deform,
                Disposition = RigBoneDisposition.Extra,
                SourceBoneIndex = row.SourceBoneIndex,
                TemplateIndex = -1,
                Position = ApplyOverride(options, bone.Name, posed.Translation),
                Orientation = RotationOnly(posed),
                OffsetFromSourceJoint = 0.0,
                SegmentRatio = 1.0,
            });
        }

        return new RigConformanceResult(
            template,
            landmark,
            strength,
            bones.MoveToImmutable(),
            warnings.ToImmutable(),
            transfer);
    }

    /// <summary>
    /// The model's own distance between two mapped joints, or null when either
    /// end is synthesized and there is nothing to measure.
    /// </summary>
    private static double? MeasureSourceSegment(
        RigCorrespondenceRow row,
        RigCorrespondenceRow parentRow,
        ImmutableArray<TransformMatrix> sourceGlobals)
    {
        if (row.Disposition != RigBoneDisposition.Mapped ||
            parentRow.Disposition != RigBoneDisposition.Mapped ||
            row.SourceBoneIndex < 0 ||
            parentRow.SourceBoneIndex < 0)
        {
            return null;
        }

        return Vector3D.Distance(
            sourceGlobals[parentRow.SourceBoneIndex].Translation,
            sourceGlobals[row.SourceBoneIndex].Translation);
    }

    private static Vector3D ApplyOverride(
        RigConformanceOptions options,
        string name,
        Vector3D position) =>
        options.PositionOverrides.TryGetValue(name, out Vector3D value) && value.IsFinite
            ? value
            : position;

    private static Vector3D ResolveTemplatePelvis(
        Dl1RigTemplate template,
        ImmutableArray<RigCorrespondenceRow> templateRows)
    {
        foreach (RigCorrespondenceRow row in templateRows)
        {
            if (string.Equals(row.Role, "body.pelvis", StringComparison.Ordinal))
            {
                return template[row.TemplateIndex].GlobalRestMatrix.Translation;
            }
        }

        return template[0].GlobalRestMatrix.Translation;
    }

    /// <summary>
    /// Reparents a retained extra bone onto the closest source ancestor that
    /// survives into the emitted rig, so a dropped or unmapped intermediate row
    /// never orphans it.
    /// </summary>
    private static int ResolveExtraParent(
        RigDefinition sourceRig,
        BoneDefinition bone,
        Dictionary<int, int> emittedIndexBySource)
    {
        int current = bone.ParentIndex;
        while (current >= 0)
        {
            if (emittedIndexBySource.TryGetValue(current, out int emitted))
            {
                return emitted;
            }

            current = sourceRig.Bones[current].ParentIndex;
        }

        return -1;
    }

    private static TransformMatrix RotationOnly(TransformMatrix value) =>
        new(
            value.M11, value.M12, value.M13, 0.0,
            value.M21, value.M22, value.M23, 0.0,
            value.M31, value.M32, value.M33, 0.0,
            0.0, 0.0, 0.0, 1.0);
}
