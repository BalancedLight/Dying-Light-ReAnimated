using System.Collections.Immutable;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using ReAnimated.Codecs.Anm2;
using ReAnimated.Codecs.Fbx;
using ReAnimated.Codecs.Rp6l;
using ReAnimated.Core.Domain;
using ReAnimated.Core.Mathematics;
using ReAnimated.Evaluation;
using ReAnimated.Retargeting;
using ReAnimated.Retargeting.Mapping;

namespace ReAnimated.Tests;

public sealed class PythonSemanticParityTests
{
    private const string OracleFormat =
        "dl-reanimated-python-csharp-semantic-parity-oracle-v1";
    private const int MaximumOracleBytes = 2 * 1024 * 1024;

    [Fact]
    [Trait("Category", "PythonParity")]
    public void FbxTransformHierarchyAndCurveValuesMatchPythonOracle()
    {
        using JsonDocument document = OpenOracle();
        JsonElement root = document.RootElement;
        double tolerance = RequiredDouble(
            RequiredProperty(root, "scope"),
            "matrixAbsoluteTolerance");
        JsonElement oracle = RequiredProperty(root, "fbxTransformEvaluation");
        Vector3D angles = ReadVector3(
            RequiredProperty(oracle, "eulerAnglesDegrees"));

        foreach (JsonElement item in
                 RequiredProperty(oracle, "eulerOrders").EnumerateArray())
        {
            TransformMatrix actual = FbxTransformEvaluator.EvaluateEuler(
                angles,
                (FbxEulerOrder)RequiredInt32(item, "order"));
            AssertMatrixNear(
                ReadMatrix(RequiredProperty(item, "matrix")),
                actual,
                tolerance);
        }

        JsonElement pivot = RequiredProperty(oracle, "pivotCase");
        FbxModelObject pivotModel = ParseSingleModel(
            RequiredProperty(pivot, "properties"));
        AssertMatrixNear(
            ReadMatrix(RequiredProperty(pivot, "matrix")),
            FbxTransformEvaluator.EvaluateModelLocal(pivotModel),
            tolerance);

        JsonElement hierarchy = RequiredProperty(oracle, "hierarchyCase");
        var models = new List<FbxNode>();
        var connections = new List<FbxNode>();
        foreach (JsonElement model in
                 RequiredProperty(hierarchy, "models").EnumerateArray())
        {
            long objectId = RequiredInt64(model, "objectId");
            models.Add(
                Model(
                    objectId,
                    RequiredString(model, "name"),
                    RequiredString(model, "subtype"),
                    ReadProperties(
                        RequiredProperty(model, "properties"))));
            JsonElement parent = RequiredProperty(model, "parentObjectId");
            if (parent.ValueKind != JsonValueKind.Null)
            {
                connections.Add(
                    Connection("OO", objectId, parent.GetInt64()));
            }
        }

        FbxSemanticScene hierarchyScene = FbxSemanticScene.Parse(
            Document(models.ToArray(), connections.ToArray()));
        ImmutableDictionary<long, TransformMatrix> globals =
            FbxTransformEvaluator.EvaluateModelGlobals(
                hierarchyScene,
                0,
                useAnimation: false);
        foreach (JsonProperty expected in
                 RequiredProperty(hierarchy, "globalMatrices")
                     .EnumerateObject())
        {
            long objectId = long.Parse(
                expected.Name,
                NumberStyles.None,
                CultureInfo.InvariantCulture);
            AssertMatrixNear(
                ReadMatrix(expected.Value),
                globals[objectId],
                tolerance);
        }

        JsonElement curveOracle = RequiredProperty(oracle, "curveCase");
        long[] keyTimes = RequiredProperty(curveOracle, "keyTimes")
            .EnumerateArray()
            .Select(static value => value.GetInt64())
            .ToArray();
        double[] keyValues = RequiredProperty(curveOracle, "keyValues")
            .EnumerateArray()
            .Select(static value => value.GetDouble())
            .ToArray();
        FbxSemanticScene curveScene = FbxSemanticScene.Parse(
            AnimatedCurveDocument(keyTimes, keyValues));
        FbxAnimationCurve curve = Assert.Single(
            curveScene.ReadAnimationBindings(
                Assert.Single(curveScene.AnimationStacks))).Curve;
        long[] sampleTicks = RequiredProperty(curveOracle, "sampleTicks")
            .EnumerateArray()
            .Select(static value => value.GetInt64())
            .ToArray();
        double[] expectedSamples = RequiredProperty(curveOracle, "samples")
            .EnumerateArray()
            .Select(static value => value.GetDouble())
            .ToArray();
        Assert.Equal(sampleTicks.Length, expectedSamples.Length);
        for (var index = 0; index < sampleTicks.Length; index++)
        {
            AssertNear(
                expectedSamples[index],
                curve.Sample(sampleTicks[index]),
                tolerance);
        }
    }

    [Fact]
    [Trait("Category", "PythonParity")]
    public void BindBasisRetargetAndTargetBindOwnershipMatchPythonOracle()
    {
        using JsonDocument document = OpenOracle();
        JsonElement root = document.RootElement;
        double tolerance = RequiredDouble(
            RequiredProperty(root, "scope"),
            "matrixAbsoluteTolerance");
        JsonElement oracle = RequiredProperty(root, "bindBasisRetarget");
        JsonElement sourceBones = RequiredProperty(oracle, "sourceBones");
        JsonElement targetBones = RequiredProperty(oracle, "targetBones");
        RigDefinition sourceRig = ReadRig(
            "python-parity-source",
            sourceBones,
            "bind");
        RigDefinition targetRig = ReadRig(
            "python-parity-target",
            targetBones,
            "bind");
        var sourcePose = new SkeletonPose(
            sourceRig,
            sourceBones.EnumerateArray()
                .Select(bone => ReadTransform(
                    RequiredProperty(bone, "animated"))));
        var map = new RetargetMap(
            sourceRig.Id,
            targetRig.Id,
            RequiredProperty(oracle, "mappings")
                .EnumerateArray()
                .Select(row => new BoneMapEntry(
                    RequiredInt32(row, "sourceBoneIndex"),
                    RequiredInt32(row, "targetBoneIndex"),
                    BoneMappingMethod.Manual,
                    1.0)));
        int[] reviewed = RequiredProperty(
                oracle,
                "reviewedTargetBindBoneIndices")
            .EnumerateArray()
            .Select(static value => value.GetInt32())
            .ToArray();

        SkeletonPose actual = PoseRetargeter.Retarget(
            sourcePose,
            targetRig,
            map,
            reviewed);
        JsonElement[] expectedLocals = RequiredProperty(
                oracle,
                "expectedLocalMatrices")
            .EnumerateArray()
            .ToArray();
        JsonElement[] expectedGlobals = RequiredProperty(
                oracle,
                "expectedGlobalMatrices")
            .EnumerateArray()
            .ToArray();
        Assert.Equal(targetRig.BoneCount, expectedLocals.Length);
        Assert.Equal(targetRig.BoneCount, expectedGlobals.Length);
        for (var index = 0; index < targetRig.BoneCount; index++)
        {
            AssertMatrixNear(
                ReadMatrix(expectedLocals[index]),
                actual.LocalTransforms[index].ToMatrix(),
                tolerance);
            AssertMatrixNear(
                ReadMatrix(expectedGlobals[index]),
                actual.GlobalMatrices[index],
                tolerance);
        }

        Dl1AuthoringPolicy policy = Dl1AuthoringPolicy.Create(
            sourceRig,
            targetRig,
            map,
            AnimationRootMode.InPlace,
            "Bip01");
        foreach (JsonElement ownership in
                 RequiredProperty(oracle, "trackOwnership").EnumerateArray())
        {
            int targetIndex = RequiredInt32(
                ownership,
                "targetBoneIndex");
            Dl1TargetTrackPolicy track = policy.TargetTracks[targetIndex];
            Assert.Equal(
                RequiredString(ownership, "source"),
                track.Source == Dl1TargetTrackSource.Evaluated
                    ? "evaluated"
                    : "target_bind");
            JsonElement sourceIndex = RequiredProperty(
                ownership,
                "sourceBoneIndex");
            Assert.Equal(
                sourceIndex.ValueKind == JsonValueKind.Null
                    ? null
                    : sourceIndex.GetInt32(),
                track.SourceBoneIndex);
        }
    }

    [Fact]
    [Trait("Category", "PythonParity")]
    public void CameraHelperFanoutAndComponentOwnershipMatchPythonOracle()
    {
        using JsonDocument document = OpenOracle();
        JsonElement root = document.RootElement;
        double tolerance = RequiredDouble(
            RequiredProperty(root, "scope"),
            "matrixAbsoluteTolerance");
        JsonElement oracle = RequiredProperty(
            root,
            "helperFanoutRetarget");
        RigDefinition sourceRig = ReadRig(
            "python-helper-fanout-source",
            RequiredProperty(oracle, "sourceBones"),
            "bind");
        RigDefinition targetRig = ReadRig(
            "python-helper-fanout-target",
            RequiredProperty(oracle, "targetBones"),
            "bind");
        int[] reviewed = RequiredProperty(
                oracle,
                "reviewedTargetBindBoneIndices")
            .EnumerateArray()
            .Select(static value => value.GetInt32())
            .ToArray();
        var map = new RetargetMap(
            sourceRig.Id,
            targetRig.Id,
            RequiredProperty(oracle, "mappings")
                .EnumerateArray()
                .Select(row => new BoneMapEntry(
                    RequiredInt32(row, "sourceBoneIndex"),
                    RequiredInt32(row, "targetBoneIndex"),
                    BoneMappingMethod.Manual,
                    1.0,
                    isReviewed: true,
                    mappingKind:
                        Enum.Parse<RetargetMappingKind>(
                            RequiredString(row, "mappingKind")),
                    transferPolicy:
                        Enum.Parse<RetargetTransferPolicy>(
                            RequiredString(row, "transferPolicy")),
                    componentPolicy:
                        Enum.Parse<RetargetComponentPolicy>(
                            RequiredString(row, "componentPolicy")))),
            reviewed);

        Assert.Equal(4, map.Entries.Length);
        Assert.Equal(
            3,
            map.Entries.Count(static entry =>
                entry.SourceBoneIndex == 1));
        foreach (JsonElement frame in
                 RequiredProperty(oracle, "frames").EnumerateArray())
        {
            var sourcePose = new SkeletonPose(
                sourceRig,
                RequiredProperty(frame, "sourceLocals")
                    .EnumerateArray()
                    .Select(ReadTransform));
            SkeletonPose actual = PoseRetargeter.Retarget(
                sourcePose,
                targetRig,
                map);
            JsonElement[] expectedLocals = RequiredProperty(
                    frame,
                    "expectedLocalMatrices")
                .EnumerateArray()
                .ToArray();
            JsonElement[] expectedGlobals = RequiredProperty(
                    frame,
                    "expectedGlobalMatrices")
                .EnumerateArray()
                .ToArray();
            Assert.Equal(targetRig.BoneCount, expectedLocals.Length);
            Assert.Equal(targetRig.BoneCount, expectedGlobals.Length);
            for (var index = 0; index < targetRig.BoneCount; index++)
            {
                AssertMatrixNear(
                    ReadMatrix(expectedLocals[index]),
                    actual.LocalTransforms[index].ToMatrix(),
                    tolerance);
                AssertMatrixNear(
                    ReadMatrix(expectedGlobals[index]),
                    actual.GlobalMatrices[index],
                    tolerance);
            }

            TransformTRS targetRefBind =
                targetRig.Bones[2].LocalBindPose;
            TransformTRS targetEyeBind =
                targetRig.Bones[3].LocalBindPose;
            AssertQuaternionNear(
                targetRefBind.Rotation,
                actual.LocalTransforms[2].Rotation,
                tolerance);
            AssertVectorNear(
                targetRefBind.Scale,
                actual.LocalTransforms[2].Scale,
                tolerance);
            AssertVectorNear(
                targetEyeBind.Scale,
                actual.LocalTransforms[3].Scale,
                tolerance);
            AssertMatrixNear(
                targetRig.Bones[4].LocalBindPose.ToMatrix(),
                actual.LocalTransforms[4].ToMatrix(),
                tolerance);
        }
    }

    [Fact]
    [Trait("Category", "PythonParity")]
    public void RootAndMotionAccumulatorValuesMatchPythonOracle()
    {
        using JsonDocument document = OpenOracle();
        JsonElement root = document.RootElement;
        double tolerance = RequiredDouble(
            RequiredProperty(root, "scope"),
            "rootValueAbsoluteTolerance");
        JsonElement oracle = RequiredProperty(root, "rootHelperOwnership");
        uint rootDescriptor = ParseHexUInt32(
            RequiredString(oracle, "rootDescriptor"));
        uint accumulatorDescriptor = ParseHexUInt32(
            RequiredString(oracle, "motionAccumulatorDescriptor"));
        TransformTRS rootBind = ReadTransform(
            RequiredProperty(oracle, "rootBind"));
        var rig = new RigDefinition(
            "python-parity-root-policy",
            "Python parity root policy",
            [
                new BoneDefinition(
                    0,
                    "Bip01",
                    -1,
                    rootBind,
                    BoneKind.Root,
                    descriptorHash: rootDescriptor,
                    semanticRole: "root.skeletal"),
                new BoneDefinition(
                    1,
                    "DLR_OffsetHelper_CCC3CDDF",
                    -1,
                    TransformTRS.Identity,
                    BoneKind.Helper,
                    descriptorHash: accumulatorDescriptor),
            ]);
        TransformTRS[] inputs = RequiredProperty(
                oracle,
                "inputRootTransforms")
            .EnumerateArray()
            .Select(ReadTransform)
            .ToArray();
        var firstPose = new SkeletonPose(
            rig,
            [inputs[0], TransformTRS.Identity]);

        foreach (JsonElement modeOracle in
                 RequiredProperty(oracle, "modes").EnumerateArray())
        {
            AnimationRootMode mode = RequiredString(
                modeOracle,
                "legacyPolicy") switch
            {
                "inplace" => AnimationRootMode.InPlace,
                "bip01" => AnimationRootMode.Bip01,
                "motion" => AnimationRootMode.MotionAccumulator,
                string value => throw new InvalidDataException(
                    $"Unknown root-policy oracle value '{value}'."),
            };
            Dl1AuthoringPolicy policy = Dl1AuthoringPolicy.Create(
                rig,
                rig,
                null,
                mode,
                "Bip01");
            Assert.Equal(mode, policy.RootMotion.Mode);
            Assert.Equal(
                ExpectedResolvedRootMode(mode),
                RequiredString(modeOracle, "resolvedMotionMode"));
            Assert.Equal(
                ExpectedResolvedHeadingMode(mode),
                RequiredString(modeOracle, "resolvedHeadingMode"));

            JsonElement[] expectedRoot = RequiredProperty(
                    modeOracle,
                    "rootValues")
                .EnumerateArray()
                .ToArray();
            JsonElement[] expectedAccumulator = RequiredProperty(
                    modeOracle,
                    "motionAccumulatorValues")
                .EnumerateArray()
                .ToArray();
            Assert.Equal(inputs.Length, expectedRoot.Length);
            Assert.Equal(inputs.Length, expectedAccumulator.Length);
            for (var frameIndex = 0;
                 frameIndex < inputs.Length;
                 frameIndex++)
            {
                var pose = new SkeletonPose(
                    rig,
                    [inputs[frameIndex], TransformTRS.Identity]);
                Dl1AuthoringPolicyResult result =
                    Dl1AuthoringPolicyEvaluator.Apply(
                        rig,
                        pose,
                        firstPose,
                        policy);
                AssertValuesNear(
                    ReadDoubleArray(expectedRoot[frameIndex]),
                    ToAnm2Values(result.ExportablePose.LocalTransforms[0]),
                    tolerance);
                AssertValuesNear(
                    ReadDoubleArray(expectedAccumulator[frameIndex]),
                    ToAnm2Values(result.ExportablePose.LocalTransforms[1]),
                    tolerance);
            }
        }
    }

    [Fact]
    [Trait("Category", "PythonParity")]
    public void ConsolidatedMimicScalarValuesMatchPythonOracle()
    {
        using JsonDocument document = OpenOracle();
        JsonElement root = document.RootElement;
        double tolerance = RequiredDouble(
            RequiredProperty(root, "scope"),
            "mimicValueAbsoluteTolerance");
        JsonElement oracle = RequiredProperty(root, "mimicScalars");
        FrameRate frameRate = new(
            RequiredInt32(
                RequiredProperty(oracle, "frameRate"),
                "numerator"),
            RequiredInt32(
                RequiredProperty(oracle, "frameRate"),
                "denominator"));
        int frameCount = RequiredInt32(oracle, "frameCount");
        MorphChannelDefinition[] morphs = RequiredProperty(oracle, "targets")
            .EnumerateArray()
            .Select(row => new MorphChannelDefinition(
                RequiredInt32(row, "index"),
                RequiredString(row, "name"),
                ParseHexUInt32(
                    RequiredString(row, "descriptorHex")),
                minimumValue: -1.5,
                maximumValue: 1.5))
            .ToArray();
        var rig = new RigDefinition(
            "python-parity-mimic",
            "Python parity mimic",
            [
                new BoneDefinition(
                    0,
                    "Bip01",
                    -1,
                    TransformTRS.Identity,
                    BoneKind.Root,
                    descriptorHash: 0x01010101,
                    semanticRole: "root.skeletal"),
            ],
            morphs);
        ScalarTrack[] scalarTracks = RequiredProperty(
                oracle,
                "sourceCurves")
            .EnumerateArray()
            .Select(row => new ScalarTrack(
                RequiredString(row, "name"),
                RequiredProperty(row, "values")
                    .EnumerateArray()
                    .Select((value, frame) =>
                        new ScalarKeyframe(frame, value.GetDouble()))))
            .ToArray();
        MorphChannelBinding[] bindings = RequiredProperty(
                oracle,
                "mappings")
            .EnumerateArray()
            .Select(row => new MorphChannelBinding(
                RequiredString(row, "sourceChannel"),
                RequiredString(row, "targetMorph"),
                RequiredDouble(row, "weight")))
            .ToArray();
        var clip = new AnimationClip(
            "python-parity-mimic",
            frameRate,
            frameCount,
            scalarTracks: scalarTracks);
        var exporter = new Dl1AnimationExporter(
            new Anm2EvaluationAdapter(new AnimationEvaluator()));
        Dl1AnimationExportResult exported = exporter.Export(
            new Dl1AnimationExportRequest
            {
                Evaluation = new EvaluationRequest(
                    rig,
                    rig,
                    clip,
                    0,
                    PreviewProfile.RawAuthoring,
                    purpose: EvaluationPurpose.Export,
                    morphBindings: bindings),
                Parts = Dl1AnimationExportParts.Mimic,
                MimicDescriptorOrder = morphs
                    .Select(static morph =>
                        morph.DescriptorHash!.Value)
                    .ToImmutableArray(),
            });

        byte[] mimicBytes = Assert.IsType<byte[]>(exported.MimicAnm2);
        Anm2Clip mimic = Anm2Reader.Read(
            mimicBytes,
            "python-parity-mimic.anm2");
        Assert.Equal(frameCount, mimic.Header.FrameCount);
        Assert.Equal(
            morphs.Select(static morph =>
                morph.DescriptorHash!.Value),
            mimic.TrackDescriptors);
        ImmutableArray<Anm2Frame> frames =
            Anm2SemanticDecoder.DecodeAllFrames(mimic);
        JsonElement[] expected = RequiredProperty(
                oracle,
                "decodedTargetValues")
            .EnumerateArray()
            .ToArray();
        Assert.Equal(expected.Length, frames.Length);
        for (var frameIndex = 0;
             frameIndex < frames.Length;
             frameIndex++)
        {
            double[] expectedValues = ReadDoubleArray(expected[frameIndex]);
            Assert.Equal(expectedValues.Length, frames[frameIndex].Tracks.Length);
            for (var trackIndex = 0;
                 trackIndex < expectedValues.Length;
                 trackIndex++)
            {
                AssertNear(
                    expectedValues[trackIndex],
                    frames[frameIndex]
                        .Tracks[trackIndex]
                        .TranslationX,
                    tolerance);
            }
        }
    }

    [Fact]
    [Trait("Category", "PythonParity")]
    public void AnimationScrBytesValuesAndRejectionsMatchPythonOracle()
    {
        using JsonDocument document = OpenOracle();
        JsonElement oracle = RequiredProperty(
            document.RootElement,
            "animationScr");
        Assert.Equal(
            AnimationScrCodec.RecordSize,
            RequiredInt32(oracle, "recordSize"));
        AnimationScrSequence[] sourceSequences = RequiredProperty(
                oracle,
                "sourceSequences")
            .EnumerateArray()
            .Select(static row => new AnimationScrSequence(
                RequiredString(row, "name"),
                RequiredString(row, "anm2Name"),
                (float)RequiredDouble(row, "startFrame"),
                (float)RequiredDouble(row, "endFrame"),
                (float)RequiredDouble(row, "framesPerSecond"),
                RequiredInt32(row, "enabled"),
                (float)RequiredDouble(row, "blend")))
            .ToArray();

        AnimationScrSections built = AnimationScrCodec.Build(sourceSequences);
        AssertAnimationScrSections(
            RequiredProperty(oracle, "built"),
            built);

        AnimationScrSections patched = AnimationScrCodec.PatchRanges(
            built,
            new Dictionary<string, (float Start, float End, float FramesPerSecond)>
            {
                ["WALK_B"] = (1.5f, 42.5f, 24f),
            });
        AssertAnimationScrSections(
            RequiredProperty(oracle, "patched"),
            patched);

        AnimationScrSections appended = AnimationScrCodec.Append(
            patched,
            [
                new AnimationScrSequence(
                    "Attack_Z",
                    "attack_z.anm2",
                    0,
                    12,
                    30,
                    Blend: 0.75f),
                new AnimationScrSequence(
                    "attack_a",
                    "attack_a.anm2",
                    3,
                    18,
                    48,
                    Enabled: 0,
                    Blend: -0.125f),
            ]);
        AssertAnimationScrSections(
            RequiredProperty(oracle, "appended"),
            appended);

        JsonElement invalidMagic = RequiredProperty(
            oracle,
            "invalidMagicAcceptedWithSkippedRecord");
        ParsedAnimationScr skipped = AnimationScrCodec.Parse(
            ReadAnimationScrSections(invalidMagic));
        Assert.Equal(
            RequiredProperty(invalidMagic, "parsedSequenceNames")
                .EnumerateArray()
                .Select(static name =>
                    name.GetString() ??
                    throw new InvalidDataException(
                        "AnimationScr skipped-record name is null.")),
            skipped.Sequences.Select(static sequence => sequence.Name));

        foreach (JsonElement rejection in
                 RequiredProperty(oracle, "rejectedInputs").EnumerateArray())
        {
            string id = RequiredString(rejection, "id");
            string pythonExceptionType = RequiredString(
                rejection,
                "pythonExceptionType");
            Assert.True(
                pythonExceptionType is
                    "ValueError" or
                    "NotImplementedError");
            Assert.False(
                string.IsNullOrWhiteSpace(
                    RequiredString(rejection, "pythonMessage")));
            AnimationScrSections rejectedSections =
                ReadAnimationScrSections(rejection);
            Exception? exception = Record.Exception(() =>
            {
                switch (RequiredString(rejection, "operation"))
                {
                    case "parse":
                        AnimationScrCodec.Parse(rejectedSections);
                        break;
                    case "patch":
                        AnimationScrCodec.PatchRanges(
                            rejectedSections,
                            new Dictionary<string, (float, float, float)>
                            {
                                ["not_present"] = (0, 1, 30),
                            });
                        break;
                    case "append" when
                        id == "append-duplicate-sequence":
                        AnimationScrCodec.Append(
                            rejectedSections,
                            [
                                new AnimationScrSequence(
                                    "WALK_B",
                                    "walk_b.anm2",
                                    0,
                                    1,
                                    30),
                            ]);
                        break;
                    case "append" when
                        id == "append-auxiliary-payload":
                        AnimationScrCodec.Append(
                            rejectedSections,
                            [
                                new AnimationScrSequence(
                                    "new_clip",
                                    "new_clip.anm2",
                                    0,
                                    1,
                                    30),
                            ]);
                        break;
                    default:
                        throw new InvalidDataException(
                            $"Unknown AnimationScr rejection recipe '{id}'.");
                }
            });
            Assert.NotNull(exception);
            Assert.IsAssignableFrom<Exception>(exception);
            Assert.False(string.IsNullOrWhiteSpace(exception.Message));
            AssertAnimationScrDiagnostic(id, exception.Message);
        }
    }

    [Fact]
    [Trait("Category", "PythonParity")]
    public async Task CanonicalAnimationRpackBytesAndManifestMatchPythonOracle()
    {
        using JsonDocument document = OpenOracle();
        JsonElement oracle = RequiredProperty(
            document.RootElement,
            "canonicalAnimationRpack");
        Dictionary<string, byte[]> animations = RequiredProperty(
                oracle,
                "animations")
            .EnumerateArray()
            .ToDictionary(
                row => RequiredString(row, "name"),
                row => Convert.FromBase64String(
                    RequiredString(row, "payloadBase64")),
                StringComparer.Ordinal);
        Dictionary<string, Rp6lAnimationScript> scripts = RequiredProperty(
                oracle,
                "animationScripts")
            .EnumerateArray()
            .ToDictionary(
                row => RequiredString(row, "name"),
                row => new Rp6lAnimationScript(
                    Convert.FromBase64String(
                        RequiredString(row, "headerBase64")),
                    Convert.FromBase64String(
                        RequiredString(row, "bodyBase64"))),
                StringComparer.Ordinal);

        byte[] actual = Rp6lAnimationLibraryCodec.Build(
            animations,
            scripts);
        byte[] expected = Convert.FromBase64String(
            RequiredString(oracle, "containerBase64"));
        Assert.Equal(RequiredInt32(oracle, "byteLength"), actual.Length);
        Assert.Equal(expected, actual);
        Assert.Equal(
            RequiredString(oracle, "sha256"),
            Convert.ToHexString(SHA256.HashData(actual)));

        string temporaryDirectory = Path.Combine(
            Path.GetTempPath(),
            $"ReAnimated-PythonSemanticParity-{Guid.NewGuid():N}");
        string archivePath = Path.Combine(
            temporaryDirectory,
            "canonical.rpack");
        try
        {
            Directory.CreateDirectory(temporaryDirectory);
            await File.WriteAllBytesAsync(archivePath, actual);
            Rp6lArchive archive = await Rp6lArchive.OpenAsync(archivePath);
            JsonElement manifest = RequiredProperty(oracle, "manifest");
            Assert.Equal(
                RequiredInt32(manifest, "version"),
                archive.Header.Version);
            Assert.Equal(
                RequiredInt32(manifest, "chunkCount"),
                archive.Header.ChunkCount);
            Assert.Equal(
                RequiredInt32(manifest, "itemCount"),
                archive.Header.ItemCount);
            Assert.Equal(
                RequiredProperty(manifest, "names")
                    .EnumerateArray()
                    .Select(static value =>
                        value.GetString() ??
                        throw new InvalidDataException(
                            "RP6L oracle name is null.")),
                archive.Names);

            JsonElement[] expectedResources = RequiredProperty(
                    manifest,
                    "resources")
                .EnumerateArray()
                .ToArray();
            Assert.Equal(expectedResources.Length, archive.Resources.Count);
            for (var index = 0;
                 index < expectedResources.Length;
                 index++)
            {
                JsonElement expectedResource = expectedResources[index];
                Rp6lResourceDescriptor resource = archive.Resources[index];
                Assert.Equal(
                    RequiredString(expectedResource, "name"),
                    resource.Name);
                Assert.Equal(
                    RequiredInt32(expectedResource, "type"),
                    resource.ResourceType);
                Assert.Equal(
                    RequiredInt32(expectedResource, "itemCount"),
                    resource.ItemCount);
                Assert.Equal(
                    RequiredInt32(expectedResource, "firstItemIndex"),
                    resource.FirstItemIndex);
            }

            Rp6lAnimationLibrary extracted =
                await Rp6lAnimationLibraryCodec.ExtractAsync(archivePath);
            Assert.Equal(
                animations.Keys,
                extracted.Animations.Keys,
                StringComparer.Ordinal);
            foreach ((string name, byte[] payload) in animations)
            {
                Assert.Equal(payload, extracted.Animations[name]);
            }

            Assert.Equal(
                scripts.Keys,
                extracted.AnimationScripts.Keys,
                StringComparer.Ordinal);
            foreach ((string name, Rp6lAnimationScript script) in scripts)
            {
                Assert.Equal(
                    script.HeaderSection,
                    extracted.AnimationScripts[name].HeaderSection);
                Assert.Equal(
                    script.BodySection,
                    extracted.AnimationScripts[name].BodySection);
            }
        }
        finally
        {
            if (Directory.Exists(temporaryDirectory))
            {
                Directory.Delete(temporaryDirectory, recursive: true);
            }
        }
    }

    private static void AssertAnimationScrSections(
        JsonElement expected,
        AnimationScrSections actual)
    {
        byte[] expectedSection0 = Convert.FromBase64String(
            RequiredString(expected, "section0Base64"));
        byte[] expectedSection1 = Convert.FromBase64String(
            RequiredString(expected, "section1Base64"));
        Assert.Equal(expectedSection0, actual.RecordsAndNames);
        Assert.Equal(expectedSection1, actual.IndexAndNames);
        Assert.Equal(
            RequiredString(expected, "section0Sha256"),
            Convert.ToHexString(SHA256.HashData(actual.RecordsAndNames)));
        Assert.Equal(
            RequiredString(expected, "section1Sha256"),
            Convert.ToHexString(SHA256.HashData(actual.IndexAndNames)));

        ParsedAnimationScr parsed = AnimationScrCodec.Parse(actual);
        JsonElement expectedParsed = RequiredProperty(expected, "parsed");
        Assert.Equal(
            RequiredInt32(expectedParsed, "declaredSequenceCount"),
            parsed.DeclaredSequenceCount);
        Assert.Equal(
            RequiredInt32(expectedParsed, "nameTableOffset"),
            parsed.NameTableOffset);
        JsonElement[] expectedSequences = RequiredProperty(
                expectedParsed,
                "sequences")
            .EnumerateArray()
            .ToArray();
        Assert.Equal(expectedSequences.Length, parsed.Sequences.Length);
        for (var index = 0; index < expectedSequences.Length; index++)
        {
            JsonElement expectedSequence = expectedSequences[index];
            ParsedAnimationScrSequence actualSequence =
                parsed.Sequences[index];
            Assert.Equal(
                RequiredString(expectedSequence, "name"),
                actualSequence.Name);
            Assert.Equal(
                RequiredInt32(expectedSequence, "nameOffset"),
                actualSequence.NameOffset);
            Assert.Equal(
                RequiredInt32(expectedSequence, "recordOffset"),
                actualSequence.RecordOffset);
            Assert.Equal(
                RequiredInt32(expectedSequence, "enabled"),
                actualSequence.Enabled);
            Assert.Equal(
                (float)RequiredDouble(expectedSequence, "blend"),
                actualSequence.Blend);
            Assert.Equal(
                (float)RequiredDouble(
                    expectedSequence,
                    "framesPerSecond"),
                actualSequence.FramesPerSecond);
            Assert.Equal(
                (float)RequiredDouble(expectedSequence, "startFrame"),
                actualSequence.StartFrame);
            Assert.Equal(
                (float)RequiredDouble(expectedSequence, "endFrame"),
                actualSequence.EndFrame);
            Assert.Equal(
                RequiredInt32(expectedSequence, "eventCount"),
                actualSequence.EventCount);
        }
    }

    private static AnimationScrSections ReadAnimationScrSections(
        JsonElement element) =>
        new(
            Convert.FromBase64String(
                RequiredString(element, "section0Base64")),
            Convert.FromBase64String(
                RequiredString(element, "section1Base64")));

    private static void AssertAnimationScrDiagnostic(
        string caseId,
        string message)
    {
        string expectedFragment = caseId switch
        {
            "section1-too-small" => "section 1",
            "record-table-truncated" => "section 0",
            "name-table-missing" => "name",
            "patch-missing-sequence" => "missing",
            "append-duplicate-sequence" => "already contains",
            "append-auxiliary-payload" => "auxiliary/event",
            _ => throw new InvalidDataException(
                $"Unknown AnimationScr rejection recipe '{caseId}'."),
        };
        Assert.Contains(
            expectedFragment,
            message,
            StringComparison.OrdinalIgnoreCase);
    }

    private static JsonDocument OpenOracle()
    {
        string repository = FindRepositoryRoot();
        string path = Path.Combine(
            repository,
            "tests",
            "fixtures",
            "dl1_python_csharp_semantic_parity_v1.json");
        if (!File.Exists(path))
        {
            throw new FileNotFoundException(
                "Checked-in semantic compatibility fixture was not found.",
                path);
        }

        byte[] bytes = File.ReadAllBytes(path);
        Assert.True(
            bytes.Length <= MaximumOracleBytes,
            $"Semantic parity oracle is {bytes.Length} bytes; " +
            $"maximum is {MaximumOracleBytes}.");
        JsonDocument document = JsonDocument.Parse(bytes);
        try
        {
            Assert.Equal(
                OracleFormat,
                RequiredString(document.RootElement, "format"));
            return document;
        }
        catch
        {
            document.Dispose();
            throw;
        }
    }

    private static RigDefinition ReadRig(
        string id,
        JsonElement bones,
        string transformName)
    {
        BoneDefinition[] rows = bones.EnumerateArray()
            .Select((bone, index) => new BoneDefinition(
                index,
                RequiredString(bone, "name"),
                RequiredInt32(bone, "parentIndex"),
                ReadTransform(
                    RequiredProperty(bone, transformName)),
                Enum.Parse<BoneKind>(
                    RequiredString(bone, "kind"),
                    ignoreCase: false),
                descriptorHash: checked((uint)(0x10000000 + index)),
                semanticRole: RequiredString(bone, "name") == "Bip01"
                    ? "root.skeletal"
                    : null))
            .ToArray();
        return new RigDefinition(id, id, rows);
    }

    private static TransformTRS ReadTransform(JsonElement element) =>
        new(
            ReadVector3(RequiredProperty(element, "translation")),
            ReadQuaternion(
                RequiredProperty(element, "rotationXyzw")),
            ReadVector3(RequiredProperty(element, "scale")));

    private static Vector3D ReadVector3(JsonElement element)
    {
        double[] values = ReadDoubleArray(element);
        if (values.Length != 3)
        {
            throw new InvalidDataException(
                "Parity vector must have three components.");
        }

        return new Vector3D(values[0], values[1], values[2]);
    }

    private static QuaternionD ReadQuaternion(JsonElement element)
    {
        double[] values = ReadDoubleArray(element);
        if (values.Length != 4)
        {
            throw new InvalidDataException(
                "Parity quaternion must have four components.");
        }

        return new QuaternionD(
            values[0],
            values[1],
            values[2],
            values[3]);
    }

    private static TransformMatrix ReadMatrix(JsonElement element)
    {
        JsonElement[] rows = element.EnumerateArray().ToArray();
        if (rows.Length != 4 ||
            rows.Any(row => row.GetArrayLength() != 4))
        {
            throw new InvalidDataException(
                "Parity matrix must be a 4x4 array.");
        }

        double[] values = rows
            .SelectMany(static row => row.EnumerateArray())
            .Select(static value => value.GetDouble())
            .ToArray();
        return new TransformMatrix(
            values[0], values[1], values[2], values[3],
            values[4], values[5], values[6], values[7],
            values[8], values[9], values[10], values[11],
            values[12], values[13], values[14], values[15]);
    }

    private static double[] ToAnm2Values(TransformTRS transform)
    {
        Vector3D cayley =
            Anm2DomainAdapter.CayleyFromQuaternion(transform.Rotation);
        return
        [
            cayley.X,
            cayley.Y,
            cayley.Z,
            transform.Translation.X,
            transform.Translation.Y,
            transform.Translation.Z,
            transform.Scale.X,
            transform.Scale.Y,
            transform.Scale.Z,
        ];
    }

    private static string ExpectedResolvedRootMode(
        AnimationRootMode mode) =>
        mode switch
        {
            AnimationRootMode.InPlace => "inplace",
            AnimationRootMode.Bip01 => "skeletal_root",
            AnimationRootMode.MotionAccumulator =>
                "motion_accumulator",
            _ => throw new ArgumentOutOfRangeException(nameof(mode)),
        };

    private static string ExpectedResolvedHeadingMode(
        AnimationRootMode mode) =>
        mode switch
        {
            AnimationRootMode.InPlace => "lock_initial",
            AnimationRootMode.Bip01 => "preserve",
            AnimationRootMode.MotionAccumulator =>
                "to_motion_accumulator",
            _ => throw new ArgumentOutOfRangeException(nameof(mode)),
        };

    private static FbxModelObject ParseSingleModel(
        JsonElement properties)
    {
        FbxBinaryDocument document = Document(
            [
                Model(
                    1,
                    "root",
                    "LimbNode",
                    ReadProperties(properties)),
            ],
            []);
        return FbxSemanticScene.Parse(document).Models[1];
    }

    private static FbxNode[] ReadProperties(JsonElement properties) =>
        properties.EnumerateObject()
            .Select(property =>
                Property70(
                    property.Name,
                    ReadPropertyValues(property.Value)))
            .ToArray();

    private static object[] ReadPropertyValues(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Array)
        {
            return value.EnumerateArray()
                .Select(static component =>
                    component.TryGetInt32(out int integer)
                        ? (object)integer
                        : component.GetDouble())
                .ToArray();
        }

        return
        [
            value.TryGetInt32(out int integer)
                ? integer
                : value.GetDouble(),
        ];
    }

    private static FbxBinaryDocument AnimatedCurveDocument(
        long[] keyTimes,
        double[] keyValues)
    {
        const long modelId = 1;
        const long stackId = 40;
        const long layerId = 100;
        const long curveNodeId = 20;
        const long curveId = 30;
        FbxNode[] objects =
        [
            Model(modelId, "root", "LimbNode"),
            Stack(stackId, "Take", keyTimes[0], keyTimes[^1]),
            Layer(layerId, "Base"),
            Node(
                "AnimationCurveNode",
                [
                    curveNodeId,
                    "AnimationCurveNode::Lcl Translation",
                    string.Empty,
                ]),
            Curve(curveId, keyTimes, keyValues),
        ];
        FbxNode[] connections =
        [
            Connection("OO", layerId, stackId),
            Connection("OO", curveNodeId, layerId),
            Connection(
                "OP",
                curveNodeId,
                modelId,
                "Lcl Translation"),
            Connection("OP", curveId, curveNodeId, "d|X"),
        ];
        return Document(objects, connections);
    }

    private static FbxNode Model(
        long objectId,
        string name,
        string subtype,
        params FbxNode[] properties) =>
        Node(
            "Model",
            [objectId, $"Model::{name}", subtype],
            Node(
                "Properties70",
                [],
                properties.Length == 0
                    ?
                    [
                        Property70(
                            "Lcl Translation",
                            0.0,
                            0.0,
                            0.0),
                        Property70(
                            "Lcl Rotation",
                            0.0,
                            0.0,
                            0.0),
                        Property70(
                            "Lcl Scaling",
                            1.0,
                            1.0,
                            1.0),
                    ]
                    : properties));

    private static FbxNode Layer(long objectId, string name) =>
        Node(
            "AnimationLayer",
            [objectId, $"AnimLayer::{name}", string.Empty]);

    private static FbxNode Stack(
        long objectId,
        string name,
        long start,
        long stop) =>
        Node(
            "AnimationStack",
            [objectId, $"AnimStack::{name}", string.Empty],
            Node(
                "Properties70",
                [],
                Property70("LocalStart", start),
                Property70("LocalStop", stop)));

    private static FbxNode Curve(
        long objectId,
        long[] keyTimes,
        double[] keyValues) =>
        Node(
            "AnimationCurve",
            [
                objectId,
                $"AnimationCurve::{objectId}",
                string.Empty,
            ],
            Node(
                "KeyTime",
                [keyTimes.ToImmutableArray()]),
            Node(
                "KeyValueFloat",
                [keyValues.ToImmutableArray()]));

    private static FbxNode Property70(
        string name,
        params object[] values) =>
        Node(
            "P",
            [name, name, string.Empty, "A", .. values]);

    private static FbxNode Connection(
        string kind,
        long childId,
        long parentId,
        params object[] metadata) =>
        Node(
            "C",
            [kind, childId, parentId, .. metadata]);

    private static FbxBinaryDocument Document(
        FbxNode[] objects,
        FbxNode[] connections) =>
        new(
            7400,
            [
                Node("Objects", [], objects),
                Node("Connections", [], connections),
            ]);

    private static FbxNode Node(
        string name,
        object[] properties,
        params FbxNode[] children) =>
        new(
            name,
            properties.Select(Property).ToImmutableArray(),
            children.ToImmutableArray(),
            0,
            0);

    private static FbxProperty Property(object value) =>
        new(
            value switch
            {
                long => 'L',
                int => 'I',
                float => 'F',
                double => 'D',
                string => 'S',
                ImmutableArray<long> => 'l',
                ImmutableArray<double> => 'd',
                _ => 'R',
            },
            value);

    private static void AssertMatrixNear(
        TransformMatrix expected,
        TransformMatrix actual,
        double tolerance)
    {
        double[] expectedValues =
        [
            expected.M11, expected.M12, expected.M13, expected.M14,
            expected.M21, expected.M22, expected.M23, expected.M24,
            expected.M31, expected.M32, expected.M33, expected.M34,
            expected.M41, expected.M42, expected.M43, expected.M44,
        ];
        double[] actualValues =
        [
            actual.M11, actual.M12, actual.M13, actual.M14,
            actual.M21, actual.M22, actual.M23, actual.M24,
            actual.M31, actual.M32, actual.M33, actual.M34,
            actual.M41, actual.M42, actual.M43, actual.M44,
        ];
        AssertValuesNear(expectedValues, actualValues, tolerance);
    }

    private static void AssertVectorNear(
        Vector3D expected,
        Vector3D actual,
        double tolerance) =>
        AssertValuesNear(
            [expected.X, expected.Y, expected.Z],
            [actual.X, actual.Y, actual.Z],
            tolerance);

    private static void AssertQuaternionNear(
        QuaternionD expected,
        QuaternionD actual,
        double tolerance)
    {
        double dot = Math.Abs(
            QuaternionD.Dot(
                expected.Normalized(),
                actual.Normalized()));
        Assert.InRange(dot, 1.0 - tolerance, 1.0 + tolerance);
    }

    private static void AssertValuesNear(
        double[] expected,
        double[] actual,
        double tolerance)
    {
        Assert.Equal(expected.Length, actual.Length);
        for (var index = 0; index < expected.Length; index++)
        {
            AssertNear(expected[index], actual[index], tolerance);
        }
    }

    private static void AssertNear(
        double expected,
        double actual,
        double tolerance) =>
        Assert.InRange(Math.Abs(expected - actual), 0.0, tolerance);

    private static uint ParseHexUInt32(string value)
    {
        string text = value.StartsWith(
            "0x",
            StringComparison.OrdinalIgnoreCase)
            ? value[2..]
            : value;
        return uint.Parse(
            text,
            NumberStyles.AllowHexSpecifier,
            CultureInfo.InvariantCulture);
    }

    private static double[] ReadDoubleArray(JsonElement element) =>
        element.EnumerateArray()
            .Select(static value => value.GetDouble())
            .ToArray();

    private static JsonElement RequiredProperty(
        JsonElement element,
        string name) =>
        element.TryGetProperty(name, out JsonElement value)
            ? value
            : throw new InvalidDataException(
                $"Semantic parity oracle is missing '{name}'.");

    private static string RequiredString(
        JsonElement element,
        string name) =>
        RequiredProperty(element, name).GetString()
        ?? throw new InvalidDataException(
            $"Semantic parity oracle '{name}' is null.");

    private static double RequiredDouble(
        JsonElement element,
        string name) =>
        RequiredProperty(element, name).GetDouble();

    private static int RequiredInt32(
        JsonElement element,
        string name) =>
        RequiredProperty(element, name).GetInt32();

    private static long RequiredInt64(
        JsonElement element,
        string name) =>
        RequiredProperty(element, name).GetInt64();

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(
                    Path.Combine(
                        directory.FullName,
                        "DLReAnimated.slnx")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException(
            "Could not locate the DL ReAnimated repository root.");
    }
}
