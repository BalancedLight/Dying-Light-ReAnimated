using System.Collections.Immutable;
using ReAnimated.Core.Domain;
using ReAnimated.Core.Mathematics;

namespace ReAnimated.Codecs.Fbx;

public sealed record FbxCoreAnimationImportOptions
{
    public string RigId { get; init; } = "fbx:source";

    public string RigDisplayName { get; init; } = "FBX Source Rig";

    public string? AnimationStackName { get; init; }

    public FrameRate? SamplingFrameRate { get; init; }

    public bool ConvertUnitsToMeters { get; init; } = true;

    public bool ApplyGlobalSettingsAxisConversion { get; init; } = true;

    public TransformMatrix AdditionalSceneToCoreBasis { get; init; } =
        TransformMatrix.Identity;

    public int MaximumSampleFrames { get; init; } = 1_000_000;

    public int MaximumSampledTransformKeys { get; init; } = 1_000_000;

    /// <summary>
    /// Allows the custom-model workflow to project affine shear caused by
    /// inherited non-uniform scale to a stable TRS preview. Strict animation
    /// imports keep this disabled and continue to fail closed.
    /// </summary>
    public bool ProjectAffineShearToTrs { get; init; }

    public SourceAssetFingerprint? SourceAssetFingerprint { get; init; }
}

public sealed record FbxAnimationImportNotice(
    string Code,
    int AffectedObjectCount,
    string Summary,
    string Detail);

public sealed record FbxCoreAnimationImportResult(
    FbxSemanticScene Scene,
    FbxAnimationStackInfo AnimationStack,
    FbxDeclaredTimebase DeclaredTimebase,
    TransformMatrix SceneToCoreBasis,
    double MetersPerUnit,
    ImmutableArray<long> SampleTicks,
    RigDefinition Rig,
    AnimationClip Clip)
{
    public ImmutableArray<FbxAnimationStackActivity>
        AnimationStackActivities =>
            Scene.AnalyzeAnimationStacks();

    public ImmutableArray<FbxNode> SkippedModelDomainPayloads =>
        Scene.Document.SkippedObjectPayloads;

    public ImmutableArray<FbxNode> SkippedGeometryPayloads =>
        SkippedModelDomainPayloads
            .Where(static node =>
                string.Equals(
                    node.Name,
                    "Geometry",
                    StringComparison.Ordinal))
            .ToImmutableArray();

    public ImmutableArray<FbxAnimationImportNotice> DomainNotices =>
        SkippedModelDomainPayloads.Length == 0
            ? []
            :
            [
                new(
                    "fbx_model_domains_excluded_from_animation_import",
                    SkippedModelDomainPayloads.Length,
                    $"{SkippedModelDomainPayloads.Length:N0} model-domain object payloads were excluded from animation import",
                    "Skeleton, bind, stack, animation-curve, and BlendShapeChannel domains were decoded. Vertex, polygon, material, texture, skin-topology, and shape-delta arrays remain unrequested; use whole-document inspection or a model workflow when those domains must be validated."),
            ];
}

/// <summary>
/// Converts a validated binary FBX semantic scene into the immutable Core
/// rig/clip domain while preserving the explicit column-vector convention.
/// </summary>
public static class FbxCoreAnimationAdapter
{
    public static FbxCoreAnimationImportResult Import(
        FbxBinaryDocument document,
        FbxCoreAnimationImportOptions? options = null) =>
        Import(document, options, CancellationToken.None);

    public static FbxCoreAnimationImportResult Import(
        FbxBinaryDocument document,
        FbxCoreAnimationImportOptions? options,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(document);
        options ??= new FbxCoreAnimationImportOptions();
        ValidateOptions(options);
        cancellationToken.ThrowIfCancellationRequested();

        FbxSemanticScene scene =
            FbxSemanticScene.Parse(document, cancellationToken);
        FbxAnimationStackInfo stack = scene.SelectAnimationStackForImport(
            options.AnimationStackName,
            cancellationToken);
        ImmutableArray<FbxAnimationCurveBinding> bindings =
            scene.ReadAnimationBindings(stack, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        FbxDeclaredTimebase declaredTimebase =
            scene.ResolveDeclaredTimebase(
                bindings,
                cancellationToken);
        FrameRate samplingFrameRate =
            options.SamplingFrameRate ?? declaredTimebase.FrameRate;
        ImmutableArray<long> ticks = BuildSampleTicks(
            stack,
            bindings,
            samplingFrameRate,
            options.MaximumSampleFrames,
            cancellationToken);

        TransformMatrix globalSettingsBasis =
            options.ApplyGlobalSettingsAxisConversion
                ? BuildGlobalSettingsBasis(scene.GlobalSettings)
                : TransformMatrix.Identity;
        TransformMatrix basis =
            options.AdditionalSceneToCoreBasis * globalSettingsBasis;
        TransformMatrix inverseBasis;
        try
        {
            inverseBasis = basis.InvertedAffine();
        }
        catch (InvalidOperationException exception)
        {
            throw new InvalidDataException(
                "The FBX scene-to-Core basis is singular or non-affine.",
                exception);
        }

        double metersPerUnit = options.ConvertUnitsToMeters
            ? scene.MetersPerUnit
            : 1.0;
        ImmutableArray<long> orderedBones =
            BuildOrderedLimbModels(scene, cancellationToken);
        long sampledTransformKeyCount = checked(
            (long)orderedBones.Length * ticks.Length);
        if (sampledTransformKeyCount > options.MaximumSampledTransformKeys)
        {
            throw new InvalidDataException(
                $"FBX animation stack '{stack.Name}' would require " +
                $"{sampledTransformKeyCount:N0} sampled transform keys " +
                $"({orderedBones.Length:N0} bones x {ticks.Length:N0} frames); " +
                $"the configured limit is {options.MaximumSampledTransformKeys:N0}.");
        }

        ImmutableDictionary<long, int> boneIndexByModel = orderedBones
            .Select((modelId, index) => (modelId, index))
            .ToImmutableDictionary(static pair => pair.modelId, static pair => pair.index);
        ImmutableDictionary<long, long?> limbParentByModel = orderedBones
            .ToImmutableDictionary(
                static modelId => modelId,
                scene.GetNearestLimbParentId);

        ImmutableDictionary<long, TransformMatrix> rawModelBindGlobals =
            FbxTransformEvaluator.EvaluateModelGlobals(
                scene,
                tick: 0,
                bindings: null,
                useAnimation: false,
                cancellationToken: cancellationToken);
        ImmutableDictionary<long, TransformMatrix> bindPoseGlobals =
            scene.ReadBindPoseGlobals(cancellationToken);
        ImmutableDictionary<long, TransformMatrix> rawBindGlobals =
            rawModelBindGlobals.SetItems(
                bindPoseGlobals.Where(
                    pair => boneIndexByModel.ContainsKey(pair.Key)));
        ImmutableDictionary<long, TransformMatrix> bindGlobals =
            NormalizeGlobals(
                rawBindGlobals,
                orderedBones,
                metersPerUnit,
                basis,
                inverseBasis);
        var bones = ImmutableArray.CreateBuilder<BoneDefinition>(
            orderedBones.Length);
        foreach (long modelId in orderedBones)
        {
            cancellationToken.ThrowIfCancellationRequested();
            FbxModelObject model = scene.Models[modelId];
            long? parentModelId = limbParentByModel[modelId];
            TransformMatrix local = parentModelId.HasValue
                ? MakeRelative(
                    bindGlobals[parentModelId.Value],
                    bindGlobals[modelId],
                    model.Name,
                    "bind")
                : bindGlobals[modelId];
            int parentIndex = parentModelId.HasValue
                ? boneIndexByModel[parentModelId.Value]
                : -1;
            bones.Add(
                new BoneDefinition(
                    boneIndexByModel[modelId],
                    model.Name,
                    parentIndex,
                    DecomposeForImport(local, model.Name, "bind", options.ProjectAffineShearToTrs),
                    parentIndex < 0 ? BoneKind.Root : BoneKind.Deform));
        }

        var rig = new RigDefinition(
            options.RigId,
            options.RigDisplayName,
            bones.MoveToImmutable(),
            sourceAssetFingerprint: options.SourceAssetFingerprint);
        var keysByBone = Enumerable.Range(0, rig.BoneCount)
            .Select(_ => ImmutableArray.CreateBuilder<TransformKeyframe>(ticks.Length))
            .ToArray();
        for (int frameIndex = 0; frameIndex < ticks.Length; frameIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            long tick = ticks[frameIndex];
            ImmutableDictionary<long, TransformMatrix> rawGlobals =
                FbxTransformEvaluator.EvaluateModelGlobals(
                    scene,
                    tick,
                    bindings,
                    useAnimation: true,
                    cancellationToken: cancellationToken);
            ImmutableDictionary<long, TransformMatrix> globals =
                NormalizeGlobals(
                    rawGlobals,
                    orderedBones,
                    metersPerUnit,
                    basis,
                    inverseBasis);

            foreach (long modelId in orderedBones)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int boneIndex = boneIndexByModel[modelId];
                long? parentModelId = limbParentByModel[modelId];
                TransformMatrix local = parentModelId.HasValue
                    ? MakeRelative(
                        globals[parentModelId.Value],
                        globals[modelId],
                        scene.Models[modelId].Name,
                        $"frame {frameIndex}")
                    : globals[modelId];
                keysByBone[boneIndex].Add(
                    new TransformKeyframe(
                        frameIndex,
                        DecomposeForImport(
                            local,
                            scene.Models[modelId].Name,
                            $"frame {frameIndex}",
                            options.ProjectAffineShearToTrs)));
            }
        }

        ImmutableArray<TransformTrack> tracks = keysByBone
            .Select(
                (keys, boneIndex) =>
                    new TransformTrack(
                        boneIndex,
                        keys.MoveToImmutable()))
            .ToImmutableArray();
        cancellationToken.ThrowIfCancellationRequested();
        var clip = new AnimationClip(
            stack.Name,
            samplingFrameRate,
            ticks.Length,
            tracks);
        return new FbxCoreAnimationImportResult(
            scene,
            stack,
            declaredTimebase,
            basis,
            metersPerUnit,
            ticks,
            rig,
            clip);
    }

    private static void ValidateOptions(FbxCoreAnimationImportOptions options)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(options.RigId);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.RigDisplayName);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(options.MaximumSampleFrames);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(
            options.MaximumSampledTransformKeys);
        if (!options.AdditionalSceneToCoreBasis.IsFinite)
        {
            throw new ArgumentException(
                "The additional scene-to-Core basis must be finite.",
                nameof(options));
        }

        if (options.SamplingFrameRate is FrameRate frameRate &&
            (frameRate.Numerator <= 0 || frameRate.Denominator <= 0))
        {
            throw new ArgumentException("The sampling frame rate is invalid.", nameof(options));
        }
    }

    internal static ImmutableArray<long> BuildOrderedLimbModels(
        FbxSemanticScene scene,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        long[] limbs = scene.ModelOrder
            .Where(modelId => scene.Models[modelId].IsLimb)
            .ToArray();
        if (limbs.Length == 0)
        {
            throw new InvalidDataException(
                "FBX animation import requires at least one LimbNode Model.");
        }

        HashSet<long> limbSet = limbs.ToHashSet();
        var depths = new Dictionary<long, int>();
        var visiting = new HashSet<long>();

        int ResolveDepth(long modelId)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (depths.TryGetValue(modelId, out int cached))
            {
                return cached;
            }

            if (!visiting.Add(modelId))
            {
                throw new InvalidDataException(
                    $"FBX skeletal hierarchy contains a cycle at '{scene.Models[modelId].Name}'.");
            }

            long? parent = scene.GetNearestLimbParentId(modelId);
            int depth = parent.HasValue && limbSet.Contains(parent.Value)
                ? checked(ResolveDepth(parent.Value) + 1)
                : 0;
            visiting.Remove(modelId);
            depths.Add(modelId, depth);
            return depth;
        }

        Dictionary<long, int> sourceOrder = limbs
            .Select((modelId, index) => (modelId, index))
            .ToDictionary(static pair => pair.modelId, static pair => pair.index);
        return limbs
            .OrderBy(ResolveDepth)
            .ThenBy(modelId => sourceOrder[modelId])
            .ToImmutableArray();
    }

    private static ImmutableArray<long> BuildSampleTicks(
        FbxAnimationStackInfo stack,
        ImmutableArray<FbxAnimationCurveBinding> bindings,
        FrameRate frameRate,
        int maximumFrames,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        long start = stack.StartTick;
        long stop = stack.StopTick;
        bool hasCurveTime = false;
        long minimumCurveTime = long.MaxValue;
        long maximumCurveTime = long.MinValue;
        foreach (FbxAnimationCurveBinding binding in bindings)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ImmutableArray<long> keyTimes = binding.Curve.KeyTimes;
            if (keyTimes.IsDefaultOrEmpty)
            {
                continue;
            }

            hasCurveTime = true;
            minimumCurveTime = Math.Min(
                minimumCurveTime,
                keyTimes[0]);
            maximumCurveTime = Math.Max(
                maximumCurveTime,
                keyTimes[^1]);
        }

        if (hasCurveTime)
        {
            if (start == 0 && stop == 0)
            {
                start = minimumCurveTime;
            }

            stop = Math.Max(stop, maximumCurveTime);
        }

        if (stop < start)
        {
            throw new InvalidDataException(
                $"FBX animation stack '{stack.Name}' stops before it starts.");
        }

        decimal durationTicks = (decimal)stop - start;
        decimal frameCountValue = decimal.Ceiling(
            durationTicks *
            frameRate.Numerator /
            ((decimal)FbxBinaryDocument.TicksPerSecond *
             frameRate.Denominator)) + 1m;
        if (frameCountValue < 1m ||
            frameCountValue > maximumFrames)
        {
            throw new InvalidDataException(
                $"FBX animation stack '{stack.Name}' would require {frameCountValue:N0} samples; " +
                $"the configured limit is {maximumFrames:N0}.");
        }

        int frameCount = checked((int)frameCountValue);
        var result = ImmutableArray.CreateBuilder<long>(frameCount);
        for (int frameIndex = 0; frameIndex < frameCount; frameIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            decimal tickOffset = decimal.Round(
                (decimal)frameIndex *
                FbxBinaryDocument.TicksPerSecond *
                frameRate.Denominator /
                frameRate.Numerator,
                0,
                MidpointRounding.ToEven);
            long tick = decimal.ToInt64(
                Math.Min((decimal)stop, start + tickOffset));
            result.Add(Math.Min(stop, tick));
        }

        return result.MoveToImmutable();
    }

    internal static ImmutableDictionary<long, TransformMatrix> NormalizeGlobals(
        ImmutableDictionary<long, TransformMatrix> rawGlobals,
        ImmutableArray<long> selectedModelIds,
        double metersPerUnit,
        TransformMatrix basis,
        TransformMatrix inverseBasis)
    {
        var result = ImmutableDictionary.CreateBuilder<long, TransformMatrix>();
        foreach (long modelId in selectedModelIds)
        {
            TransformMatrix raw = rawGlobals[modelId];
            TransformMatrix scaled = raw with
            {
                M14 = raw.M14 * metersPerUnit,
                M24 = raw.M24 * metersPerUnit,
                M34 = raw.M34 * metersPerUnit,
            };
            TransformMatrix converted = basis * scaled * inverseBasis;
            if (!converted.IsFinite)
            {
                throw new InvalidDataException(
                    $"FBX Model {modelId} became non-finite at the Core transform boundary.");
            }

            result.Add(modelId, converted);
        }

        return result.ToImmutable();
    }

    internal static TransformMatrix BuildGlobalSettingsBasis(
        ImmutableDictionary<string, ImmutableArray<object>> settings)
    {
        string[] axisNames = ["CoordAxis", "UpAxis", "FrontAxis"];
        string[] signNames = ["CoordAxisSign", "UpAxisSign", "FrontAxisSign"];
        if (axisNames.All(name => !settings.ContainsKey(name)))
        {
            return TransformMatrix.Identity;
        }

        var rows = new double[3, 3];
        var usedAxes = new HashSet<int>();
        for (int row = 0; row < 3; row++)
        {
            int? axis = FbxSemanticValues.TryGetInt32(settings, axisNames[row]);
            int? sign = FbxSemanticValues.TryGetInt32(settings, signNames[row]);
            if (axis is not (0 or 1 or 2) ||
                sign is not (-1 or 1) ||
                !usedAxes.Add(axis.Value))
            {
                throw new InvalidDataException(
                    "FBX GlobalSettings axes must form a complete signed permutation.");
            }

            rows[row, axis.Value] = sign.Value;
        }

        return new TransformMatrix(
            rows[0, 0], rows[0, 1], rows[0, 2], 0.0,
            rows[1, 0], rows[1, 1], rows[1, 2], 0.0,
            rows[2, 0], rows[2, 1], rows[2, 2], 0.0,
            0.0, 0.0, 0.0, 1.0);
    }

    internal static TransformMatrix MakeRelative(
        TransformMatrix parentGlobal,
        TransformMatrix childGlobal,
        string modelName,
        string context)
    {
        try
        {
            return parentGlobal.InvertedAffine() * childGlobal;
        }
        catch (InvalidOperationException exception)
        {
            throw new InvalidDataException(
                $"FBX Model '{modelName}' has a singular parent transform at {context}.",
                exception);
        }
    }

    internal static TransformTRS DecomposeChecked(
        TransformMatrix matrix,
        string modelName,
        string context)
    {
        try
        {
            return matrix.Decompose();
        }
        catch (InvalidOperationException exception)
        {
            throw new InvalidDataException(
                $"FBX Model '{modelName}' cannot be represented as Core TRS at {context}.",
                exception);
        }
    }

    private static TransformTRS DecomposeForImport(
        TransformMatrix matrix,
        string modelName,
        string context,
        bool projectAffineShear)
    {
        try
        {
            return matrix.Decompose();
        }
        catch (InvalidOperationException) when (projectAffineShear)
        {
            return ProjectAffineToTrs(matrix, modelName, context);
        }
        catch (InvalidOperationException exception)
        {
            throw new InvalidDataException(
                $"FBX Model '{modelName}' cannot be represented as Core TRS at {context}.",
                exception);
        }
    }

    internal static TransformTRS ProjectAffineToTrs(
        TransformMatrix matrix,
        string modelName,
        string context)
    {
        Vector3D columnX = new(matrix.M11, matrix.M21, matrix.M31);
        Vector3D columnY = new(matrix.M12, matrix.M22, matrix.M32);
        Vector3D columnZ = new(matrix.M13, matrix.M23, matrix.M33);
        double scaleX = columnX.Length;
        double scaleY = columnY.Length;
        double scaleZ = columnZ.Length;
        if (!double.IsFinite(scaleX) || !double.IsFinite(scaleY) || !double.IsFinite(scaleZ) ||
            scaleX <= 1e-12 || scaleY <= 1e-12 || scaleZ <= 1e-12)
        {
            throw new InvalidDataException(
                $"FBX Model '{modelName}' has a singular affine basis at {context}.");
        }

        Vector3D axisX = columnX / scaleX;
        Vector3D yCandidate = columnY - (axisX * Vector3D.Dot(columnY, axisX));
        if (!yCandidate.TryNormalize(out Vector3D axisY))
        {
            throw new InvalidDataException(
                $"FBX Model '{modelName}' has a collinear affine basis at {context}.");
        }

        Vector3D axisZ = Vector3D.Cross(axisX, axisY).Normalized();
        if (Vector3D.Dot(axisZ, columnZ) < 0.0)
        {
            axisX = -axisX;
            scaleX = -scaleX;
            axisZ = Vector3D.Cross(axisX, axisY).Normalized();
        }

        var rotation = new TransformMatrix(
            axisX.X, axisY.X, axisZ.X, 0.0,
            axisX.Y, axisY.Y, axisZ.Y, 0.0,
            axisX.Z, axisY.Z, axisZ.Z, 0.0,
            0.0, 0.0, 0.0, 1.0);
        return new TransformTRS(
            matrix.Translation,
            QuaternionD.FromRotationMatrix(rotation),
            new Vector3D(scaleX, scaleY, scaleZ));
    }
}
