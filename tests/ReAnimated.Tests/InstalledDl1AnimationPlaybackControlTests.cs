using System.Collections.Immutable;
using System.Numerics;
using ReAnimated.App.Infrastructure;
using ReAnimated.Codecs.Anm2;
using ReAnimated.Codecs.Rp6l;
using ReAnimated.Core.Domain;
using ReAnimated.Core.Mathematics;
using ReAnimated.DL1.Assets.Catalog;
using ReAnimated.DL1.Assets.Discovery;
using ReAnimated.DL1.Assets.Meshes;
using ReAnimated.Evaluation;
using ReAnimated.Renderer.D3D11;
using Xunit.Abstractions;

namespace ReAnimated.Tests;

public sealed class InstalledDl1AnimationPlaybackControlTests
{
    private const string RunEnvironmentVariable =
        "DLR_RUN_INSTALLED_ANIMATION_PLAYBACK";
    private const string ValidatedBuildFingerprint =
        "89f98e5c77a2eb36767a614acf894a4f18f55e6efa8f912ca7ef66b45a0dfa13";
    private readonly ITestOutputHelper _output;

    public InstalledDl1AnimationPlaybackControlTests(
        ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact(Timeout = 240_000)]
    [Trait("ValidationTier", "Release")]
    [Trait("Gate", "DL1InstalledAnimationPlayback")]
    public async Task Installed155NamedPlaybackControlsRemainCoherent()
    {
        if (!string.Equals(
                Environment.GetEnvironmentVariable(
                    RunEnvironmentVariable),
                "1",
                StringComparison.Ordinal))
        {
            _output.WriteLine(
                $"NOT EXERCISED: set {RunEnvironmentVariable}=1.");
            return;
        }

        Dl1InstallLocation install = SteamInstallDiscovery
            .Discover()
            .FirstOrDefault(static candidate => candidate.IsValid)
            ?? throw new InvalidOperationException(
                "No complete Steam Dying Light 1 installation was discovered.");
        Dl1InstalledBuildFingerprint fingerprint =
            await new Dl1InstalledBuildFingerprintService()
                .ReadAsync(install.InstallPath);
        Assert.Equal(
            ValidatedBuildFingerprint,
            fingerprint.BuildFingerprint,
            ignoreCase: true);

        string animationArchivePath = Path.Combine(
            install.DataPath,
            "common_anims_PC.rpack");
        string meshArchivePath = Path.Combine(
            install.DataPath,
            "common_meshes_PC.rpack");
        Assert.True(File.Exists(animationArchivePath));
        Assert.True(File.Exists(meshArchivePath));
        Rp6lArchive animations = await Rp6lArchive.OpenAsync(
            animationArchivePath);
        Rp6lArchive meshes = await Rp6lArchive.OpenAsync(
            meshArchivePath);
        string temporaryDirectory =
            RpackTestData.CreateTemporaryDirectory();
        try
        {
            await using var cache = new Rp6lChunkCache(
                new Rp6lChunkCacheOptions
                {
                    CacheDirectory = Path.Combine(
                        temporaryDirectory,
                        "cache"),
                    MaximumMemoryBytes = 64 * 1024 * 1024,
                    MaximumMemoryEntryBytes = 32 * 1024 * 1024,
                    MaximumDiskBytes = 2L * 1024 * 1024 * 1024,
                });

            Anm2Clip dancers = await DecodeAnimationAsync(
                animations,
                cache,
                "dncrs_0001_3dc_3p");
            AssertBulkAndRandomAccessAgree(dancers);
            await AssertExactAnimationScriptTimingAsync(
                temporaryDirectory,
                animationArchivePath,
                "armored_armstrike_left");

            Dl1MeshPreviewPayload armored = await DecodeMeshAsync(
                meshes,
                cache,
                "armored");
            Anm2Clip armoredStrike = await DecodeAnimationAsync(
                animations,
                cache,
                "armored_armstrike_left");
            AssertSameRigDirectPlayback(
                armored,
                armoredStrike,
                selectedFrame: 29);

            Dl1MeshPreviewPayload prime = await DecodeMeshAsync(
                meshes,
                cache,
                "zombie_prime");
            Anm2Clip primeSprint = await DecodeAnimationAsync(
                animations,
                cache,
                "prime_4leg_sprint");
            AssertPrimeSourceBinding(
                prime,
                armored,
                primeSprint);
            await AssertFacialControlsAsync(
                animations,
                cache,
                armored.Source.Rig!,
                prime.Source.Rig!);
        }
        finally
        {
            RpackTestData.DeleteTemporaryDirectory(
                temporaryDirectory);
        }
    }

    private async Task AssertExactAnimationScriptTimingAsync(
        string temporaryDirectory,
        string animationArchivePath,
        string animationName)
    {
        string databasePath = Path.Combine(
            temporaryDirectory,
            "timing-assets.sqlite3");
        string cachedDatabase = Path.Combine(
            Environment.GetFolderPath(
                Environment.SpecialFolder.LocalApplicationData),
            "DLReAnimated",
            "AssetCatalog",
            "dl1-assets.sqlite3");
        if (File.Exists(cachedDatabase))
        {
            File.Copy(cachedDatabase, databasePath);
        }

        await using var workspace = new Dl1AssetWorkspace(
            databasePath,
            Path.Combine(temporaryDirectory, "timing-cache"));
        Dl1AssetIndexResult indexed =
            await workspace.IndexSteamInstallAsync();
        RetailAssetRecord animation = indexed.Catalog.Assets.Single(
            asset =>
                asset.Id.ResourceType ==
                    Rp6lResourceTypes.Animation &&
                string.Equals(
                    asset.DisplayName,
                    animationName,
                    StringComparison.OrdinalIgnoreCase) &&
                string.Equals(
                    asset.Source.ContainerPath,
                    animationArchivePath,
                    StringComparison.OrdinalIgnoreCase));

        Dl1RetailAnimationPayload payload;
        try
        {
            payload = await workspace.DecodeAnimationAsync(animation);
        }
        catch (Dl1AnimationTimingConflictException conflict)
        {
            Assert.True(conflict.Choices.Length >= 2);
            Assert.All(
                conflict.Choices,
                static choice => Assert.Equal(
                    AnimationTimingProvenance.ExactRetailAnimationScript,
                    choice.Provenance));
            payload = await workspace.DecodeAnimationAsync(
                animation,
                conflict.Choices[0]);
        }

        Assert.Equal(
            AnimationTimingProvenance.ExactRetailAnimationScript,
            payload.Timing.Provenance);
        Assert.True(payload.Timing.EndFrame >= payload.Timing.StartFrame);
        Assert.True(payload.Timing.FrameRate.Numerator > 0);
        _output.WriteLine(
            $"Exact type-322 timing: {payload.Timing.SelectionLabel}");
    }

    private static void AssertBulkAndRandomAccessAgree(
        Anm2Clip clip)
    {
        ImmutableArray<Anm2Frame> bulk =
            Anm2SemanticDecoder.DecodeAllFrames(clip);
        Assert.Equal(clip.Header.FrameCount, bulk.Length);
        var pageBoundaries = new HashSet<int> { 0, bulk.Length - 1 };
        int cumulative = 0;
        foreach (ushort span in clip.PageFrameSpans)
        {
            cumulative += span;
            pageBoundaries.Add(Math.Clamp(cumulative - 1, 0, bulk.Length - 1));
            pageBoundaries.Add(Math.Clamp(cumulative, 0, bulk.Length - 1));
        }

        for (var frameIndex = 0;
             frameIndex < bulk.Length;
             frameIndex++)
        {
            Anm2DecodedSample random =
                Anm2SemanticDecoder.Sample(clip, frameIndex);
            Assert.Equal(frameIndex, random.EvaluatedFrame);
            AssertFramesBitExact(bulk[frameIndex], random.Frame);
        }

        Assert.All(
            pageBoundaries,
            frame => Assert.InRange(frame, 0, bulk.Length - 1));
    }

    private static void AssertSameRigDirectPlayback(
        Dl1MeshPreviewPayload model,
        Anm2Clip source,
        int selectedFrame)
    {
        RigDefinition rig = model.Source.Rig
            ?? throw new InvalidDataException(
                "The armored control has no decoded rig.");
        var rate = new FrameRate(30, 1);
        Anm2PartitionedImportResult imported =
            Anm2TrackPartitioner.Partition(source, rig, rate);
        Assert.False(imported.Partition.RequiresReview);
        Assert.NotEmpty(imported.Partition.BodyDescriptors);
        double time = rate.SecondsForFrame(
            Math.Min(selectedFrame, imported.CombinedClip.FrameCount - 1));
        SkeletonPose raw = imported.CombinedClip.SamplePose(rig, time);
        EvaluationFrame evaluated = new AnimationEvaluator().Evaluate(
            new EvaluationRequest(
                rig,
                rig,
                imported.CombinedClip,
                time,
                PreviewProfile.RawAuthoring,
                retargetMap: null,
                purpose: EvaluationPurpose.Preview));
        Assert.Equal(
            raw.LocalTransforms.ToArray(),
            evaluated.RawSourcePose.LocalTransforms.ToArray());
        Assert.Equal(
            raw.LocalTransforms.ToArray(),
            evaluated.AuthoredPose.LocalTransforms.ToArray());

        SkeletonRenderData rendered =
            CorePreviewAdapter.ToRenderSkeleton(
                evaluated.DisplayPose);
        MeshRenderData mesh = model.Meshes.First(static candidate =>
            candidate.IsSkinned &&
            candidate.Vertices.ToArray().Any(static vertex =>
                vertex.BoneWeights.X > 0.0f));
        Matrix4x4[] gpuPalette = GpuSkinningPalette.Build(
            mesh,
            rendered);
        int vertexIndex = Enumerable.Range(0, mesh.Vertices.Length)
            .First(index =>
                mesh.Vertices.Span[index].BoneWeights.X > 0.0f);
        MeshVertex vertex = mesh.Vertices.Span[vertexIndex];
        Vector3 shaderReference = EvaluateShaderPosition(
            vertex,
            gpuPalette);
        Vector3 cpuReference =
            CpuMeshDeformationEvaluator.Evaluate(
                mesh,
                rendered,
                [])[vertexIndex].Position;
        AssertVectorClose(shaderReference, cpuReference, 1.0e-5f);
    }

    private static void AssertPrimeSourceBinding(
        Dl1MeshPreviewPayload prime,
        Dl1MeshPreviewPayload armored,
        Anm2Clip source)
    {
        RigDefinition primeRig = prime.Source.Rig
            ?? throw new InvalidDataException(
                "The zombie_prime control has no decoded rig.");
        RigDefinition armoredRig = armored.Source.Rig
            ?? throw new InvalidDataException(
                "The armored control has no decoded rig.");
        Assert.NotEqual(
            RigSignature.Compute(primeRig),
            RigSignature.Compute(armoredRig));
        var rate = new FrameRate(30, 1);
        Anm2PartitionedImportResult correct =
            Anm2TrackPartitioner.Partition(source, primeRig, rate);
        Anm2PartitionedImportResult wrong =
            Anm2TrackPartitioner.Partition(source, armoredRig, rate);
        Assert.NotEmpty(correct.Partition.BodyDescriptors);
        Assert.NotEqual(
            correct.Partition.Fingerprint,
            wrong.Partition.Fingerprint);
        Assert.True(
            wrong.Partition.UnresolvedDescriptors.Length >
            correct.Partition.UnresolvedDescriptors.Length);

        IReadOnlyDictionary<string, TransformTRS> lockedJoints =
            new Dictionary<string, TransformTRS>(
                StringComparer.OrdinalIgnoreCase)
            {
                ["pelvis"] = new(
                    new Vector3D(
                        5.960464477539063E-08,
                        -1.1920928955078125E-07,
                        -5.960464477539063E-08),
                    new QuaternionD(
                        -0.4999995827672459,
                        -0.4999995827672459,
                        0.5000003874301902,
                        0.50000044703462),
                    new Vector3D(
                        1.0000001192092896,
                        0.9999997615814209,
                        0.9999999403953552)),
                ["spine"] = new(
                    Vector3D.Zero,
                    new QuaternionD(
                        -0.031212633595293592,
                        0.7313379209385804,
                        0.10429918013623735,
                        0.6732698552023472),
                    new Vector3D(
                        0.9999997615814209,
                        0.9999997615814209,
                        0.9999998807907104)),
                ["l_upperarm"] = new(
                    new Vector3D(
                        0.17176605761051178,
                        6.258487701416016E-07,
                        5.960464477539063E-08),
                    new QuaternionD(
                        -0.5602454225784975,
                        -0.19059123588181928,
                        -0.6014918071132597,
                        0.536663445057055),
                    new Vector3D(
                        1.0000001192092896,
                        1.0000003576278687,
                        1.000000238418579)),
                ["r_upperarm"] = new(
                    new Vector3D(
                        0.17176613211631775,
                        1.6540288925170898E-06,
                        8.940696716308594E-07),
                    new QuaternionD(
                        0.5966630885586109,
                        -0.31298068298202775,
                        0.298206515357922,
                        0.6760984580886782),
                    new Vector3D(
                        1.0000004768371582,
                        1.0000017881393433,
                        1.000001072883606)),
                ["l_thigh"] = new(
                    new Vector3D(
                        1.1362135410308838E-06,
                        0.10165080428123474,
                        5.960464477539063E-08),
                    new QuaternionD(
                        0.3734403999564469,
                        0.16640780497581245,
                        -0.9114634071493506,
                        -0.04566363488814842),
                    new Vector3D(
                        0.9999998211860657,
                        1.0000001192092896,
                        1.000000238418579)),
                ["r_thigh"] = new(
                    new Vector3D(
                        1.3783574104309082E-07,
                        -0.10165267437696457,
                        1.1920928955078125E-07),
                    new QuaternionD(
                        0.5553179204049649,
                        -0.11448105992531027,
                        -0.8198202362616612,
                        0.08006793622516147),
                    new Vector3D(
                        1.0000003576278687,
                        1,
                        1.0000005960464478)),
            };
        int frame = Math.Min(
            8,
            checked((int)correct.CombinedClip.FrameCount - 1));
        SkeletonPose pose = correct.CombinedClip.SamplePose(
            primeRig,
            rate.SecondsForFrame(frame));
        foreach ((string name, TransformTRS expected) in lockedJoints)
        {
            BoneDefinition bone = primeRig.Bones.First(
                candidate => string.Equals(
                    candidate.Name,
                    name,
                    StringComparison.OrdinalIgnoreCase));
            AssertTransformClose(
                expected,
                pose.LocalTransforms[bone.Index],
                translationTolerance: 2.0e-6,
                rotationTolerance: 2.0e-5,
                scaleTolerance: 2.0e-6);
        }
    }

    private static Vector3 EvaluateShaderPosition(
        MeshVertex vertex,
        Matrix4x4[] palette)
    {
        float sum = vertex.BoneWeights.X + vertex.BoneWeights.Y +
                    vertex.BoneWeights.Z + vertex.BoneWeights.W;
        Vector4 weights = vertex.BoneWeights / sum;
        int x = checked((int)vertex.BoneIndices.X);
        int y = checked((int)vertex.BoneIndices.Y);
        int z = checked((int)vertex.BoneIndices.Z);
        int w = checked((int)vertex.BoneIndices.W);
        return Vector3.Transform(vertex.Position, palette[x]) * weights.X +
               Vector3.Transform(vertex.Position, palette[y]) * weights.Y +
               Vector3.Transform(vertex.Position, palette[z]) * weights.Z +
               Vector3.Transform(vertex.Position, palette[w]) * weights.W;
    }

    private static async Task AssertFacialControlsAsync(
        Rp6lArchive archive,
        Rp6lChunkCache cache,
        RigDefinition armored,
        RigDefinition prime)
    {
        var rate = new FrameRate(30, 1);
        HashSet<uint> common46 = Dl1MimicProfileCodec
            .ReadBuiltInCommon46()
            .Targets
            .Select(static target => target.Descriptor)
            .ToHashSet();
        Anm2Clip pure = await DecodeAnimationAsync(
            archive,
            cache,
            "human_mimic_angry_01b");
        Assert.Equal(
            common46.Order().ToArray(),
            pure.TrackDescriptors.Order().ToArray());
        Anm2PartitionedImportResult purePartition =
            Anm2TrackPartitioner.Partition(pure, armored, rate);
        Assert.False(purePartition.Partition.RequiresReview);
        Assert.Empty(purePartition.Partition.BodyDescriptors);
        Assert.Equal(
            10,
            purePartition.Partition.MorphDescriptors.Length);
        Assert.Equal(
            36,
            purePartition.Partition.UnresolvedDescriptors.Length);

        Anm2Clip mixed = await DecodeAnimationAsync(
            archive,
            cache,
            "alberto_sitting_mimics_dialog_01");
        Assert.True(common46.IsSubsetOf(mixed.TrackDescriptors));
        Anm2PartitionedImportResult mixedPartition =
            Anm2TrackPartitioner.Partition(mixed, armored, rate);
        Assert.False(mixedPartition.Partition.RequiresReview);
        Assert.Equal(61, mixedPartition.Partition.BodyDescriptors.Length);
        Assert.Equal(10, mixedPartition.Partition.MorphDescriptors.Length);
        Assert.Single(mixedPartition.Partition.AuxiliaryDescriptors);
        Assert.NotEmpty(mixedPartition.Partition.UnresolvedDescriptors);
        Assert.NotEmpty(mixedPartition.BodyClip.TransformTracks);
        Assert.NotEmpty(mixedPartition.FacialClip.ScalarTracks);

        Anm2PartitionedImportResult unknownProfile =
            Anm2TrackPartitioner.Partition(pure, prime, rate);
        Assert.False(unknownProfile.Partition.RequiresReview);
        Assert.Empty(unknownProfile.Partition.BodyDescriptors);
        Assert.Empty(unknownProfile.Partition.MorphDescriptors);
        Assert.Equal(
            46,
            unknownProfile.Partition.UnresolvedDescriptors.Length);
    }

    private static void AssertFramesBitExact(
        Anm2Frame expected,
        Anm2Frame actual)
    {
        Assert.Equal(expected.Tracks.Length, actual.Tracks.Length);
        for (var track = 0; track < expected.Tracks.Length; track++)
        {
            for (var component = 0; component < 9; component++)
            {
                Assert.Equal(
                    BitConverter.SingleToInt32Bits(
                        expected.Tracks[track][component]),
                    BitConverter.SingleToInt32Bits(
                        actual.Tracks[track][component]));
            }
        }
    }

    private static void AssertVectorClose(
        Vector3 expected,
        Vector3 actual,
        float tolerance)
    {
        Assert.InRange(MathF.Abs(expected.X - actual.X), 0, tolerance);
        Assert.InRange(MathF.Abs(expected.Y - actual.Y), 0, tolerance);
        Assert.InRange(MathF.Abs(expected.Z - actual.Z), 0, tolerance);
    }

    private static void AssertTransformClose(
        TransformTRS expected,
        TransformTRS actual,
        double translationTolerance,
        double rotationTolerance,
        double scaleTolerance)
    {
        Assert.True(actual.IsFinite);
        Assert.InRange(
            (expected.Translation - actual.Translation).Length,
            0,
            translationTolerance);
        double direct = Math.Sqrt(
            Math.Pow(expected.Rotation.X - actual.Rotation.X, 2) +
            Math.Pow(expected.Rotation.Y - actual.Rotation.Y, 2) +
            Math.Pow(expected.Rotation.Z - actual.Rotation.Z, 2) +
            Math.Pow(expected.Rotation.W - actual.Rotation.W, 2));
        double negated = Math.Sqrt(
            Math.Pow(expected.Rotation.X + actual.Rotation.X, 2) +
            Math.Pow(expected.Rotation.Y + actual.Rotation.Y, 2) +
            Math.Pow(expected.Rotation.Z + actual.Rotation.Z, 2) +
            Math.Pow(expected.Rotation.W + actual.Rotation.W, 2));
        Assert.InRange(
            Math.Min(direct, negated),
            0,
            rotationTolerance);
        Assert.InRange(
            (expected.Scale - actual.Scale).Length,
            0,
            scaleTolerance);
    }

    private static async Task<Anm2Clip> DecodeAnimationAsync(
        Rp6lArchive archive,
        Rp6lChunkCache cache,
        string name)
    {
        Rp6lResourceDescriptor resource = archive.FindResource(
                Rp6lResourceTypes.Animation,
                name)
            ?? throw new InvalidDataException(
                $"Installed animation '{name}' was not found.");
        await using Stream stream = await archive.OpenResourceStreamAsync(
            resource,
            cache);
        using var output = new MemoryStream();
        await stream.CopyToAsync(output);
        return Anm2Reader.Read(output.ToArray(), name);
    }

    private static async Task<Dl1MeshPreviewPayload> DecodeMeshAsync(
        Rp6lArchive archive,
        Rp6lChunkCache cache,
        string name)
    {
        Rp6lResourceDescriptor resource = archive.FindResource(
                Rp6lResourceTypes.Mesh,
                name)
            ?? throw new InvalidDataException(
                $"Installed mesh '{name}' was not found.");
        Dl1MeshData mesh = await Dl1MeshResourceDecoder.DecodeAsync(
            archive,
            resource,
            cache);
        return Dl1MeshPreviewAdapter.Convert(mesh);
    }
}
