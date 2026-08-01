using System.Collections.Immutable;
using System.Security.Cryptography;
using ReAnimated.Codecs.Anm2;
using ReAnimated.Codecs.Rp6l;
using ReAnimated.Core.Domain;
using ReAnimated.DL1.Assets.Catalog;
using ReAnimated.DL1.Assets.Discovery;
using ReAnimated.DL1.Assets.Meshes;
using ReAnimated.DL1.Assets.Providers;
using ReAnimated.Evaluation;
using ReAnimated.Retargeting.Mapping;
using ReAnimated.Tests.Fixtures;
using Xunit.Abstractions;

namespace ReAnimated.Tests;

public sealed class InstalledDl1AuthoringEndToEndTests
{
    private readonly ITestOutputHelper _output;

    public InstalledDl1AuthoringEndToEndTests(
        ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact(Timeout = 180_000)]
    [Trait("Gate", "DL1InstalledAuthoringEndToEnd")]
    public async Task InstalledRetailControlTraversesAuthoringAndRpackFlowWhenAvailable()
    {
        Dl1InstallLocation? install = SteamInstallDiscovery
            .Discover()
            .FirstOrDefault(static location => location.IsValid);
        if (install is null)
        {
            _output.WriteLine(
                "NOT EXERCISED: no complete Steam DL1 installation was discovered.");
            return;
        }

        string directory = RpackTestData.CreateTemporaryDirectory();
        try
        {
            await RunRegressionAsync(install, directory);
        }
        finally
        {
            RpackTestData.DeleteTemporaryDirectory(directory);
        }
    }

    private async Task RunRegressionAsync(
        Dl1InstallLocation install,
        string directory)
    {
        await using var cache = new Rp6lChunkCache(
            new Rp6lChunkCacheOptions
            {
                CacheDirectory = Path.Combine(directory, "cache"),
                MaximumMemoryBytes = 32 * 1024 * 1024,
                MaximumMemoryEntryBytes = 16 * 1024 * 1024,
                MaximumDiskBytes = 4L * 1024 * 1024 * 1024,
            });
        await using Dl1RetailProviderSet providers =
            Dl1RetailProviderSet.Create(
                install.InstallPath,
                cache);
        await using var index = new RetailAssetSqliteIndex(
            Path.Combine(directory, "retail-assets.sqlite"));
        RetailAssetCatalog catalog = await RetailAssetCatalog.BuildAsync(
            providers.Providers,
            index);
        Assert.Empty(providers.RpackProvider.SourceErrors);
        Assert.Equal(
            RetailAssetIdentity.CreateInstallId(install.InstallPath),
            catalog.Assets[0].Id.InstallId);

        InstalledRetailControl control =
            await SelectRetailControlAsync(
                providers.RpackProvider,
                catalog,
                cache);
        RetailAssetRecord asset = control.Asset;
        Dl1MeshData mesh = control.Mesh;
        Assert.Equal("dl1-rpacks", asset.Id.ProviderId);
        Assert.Equal(RetailAssetNamespace.RpackResource, asset.Id.Namespace);
        Assert.Equal(Rp6lResourceTypes.Mesh, asset.Id.ResourceType);
        Assert.Equal(
            asset.Source.ResourceIndex ??
                throw new InvalidDataException(
                    $"Retail catalog row '{asset.Id}' has no resource index."),
            asset.Id.SourceIndex);
        Assert.True(mesh.IsStructurallyValid);
        Assert.True(mesh.HasDecodedGeometry);
        Assert.True(mesh.IsSkinned);
        Assert.NotNull(mesh.Rig);
        Assert.NotEmpty(mesh.Rig!.Bones);
        Assert.NotEmpty(mesh.Rig.MorphChannels);
        Assert.Equal(
            Dl1MorphPayloadStatus.VertexDeltasDecoded,
            control.MorphTarget.PayloadStatus);
        Assert.NotEmpty(control.MorphTarget.DeltaBuffers);
        Assert.All(
            control.MorphTarget.Bindings,
            static binding =>
            {
                Assert.Equal(
                    Dl1MorphDeltaEncoding.SignedShort4Scale16384,
                    binding.DeltaEncoding);
                Assert.Equal(8, binding.DeltaByteStride);
                Assert.True(binding.VertexCount > 0);
                Assert.NotEmpty(binding.PositionDeltaSets);
                Assert.All(
                    binding.PositionDeltaSets,
                    deltaSet => Assert.Equal(
                        binding.VertexCount,
                        deltaSet.PositionDeltas.Count));
            });

        string contentSha256;
        await using (Stream exact = await catalog.OpenReadAsync(asset.Id))
        {
            contentSha256 = Convert.ToHexString(
                    await SHA256.HashDataAsync(exact))
                .ToLowerInvariant();
        }

        await using (Stream logical = await catalog.OpenReadAsync(
                         asset.Id.LogicalId))
        {
            Assert.Equal(
                contentSha256,
                Convert.ToHexString(
                        await SHA256.HashDataAsync(logical))
                    .ToLowerInvariant());
        }

        IReadOnlyList<RetailAssetRecord> persisted =
            await index.LoadAsync();
        Assert.Contains(
            persisted,
            row => row.Id == asset.Id);
        Assert.Equal(
            control.Archive.CacheIdentity.ToLowerInvariant(),
            asset.Id.SourceFingerprint);

        RigDefinition targetRig =
            InstalledDl1AuthoringEndToEndFixture.BindRetailIdentity(
                mesh,
                asset,
                install.InstallPath,
                contentSha256);
        Assert.Equal(
            asset.Id.StableKey,
            targetRig.SourceAssetFingerprint!.ResourceId);
        Assert.Equal(
            contentSha256.ToUpperInvariant(),
            targetRig.SourceAssetFingerprint.ContentSha256);
        BoneDefinition targetBone = SelectAnimatedBone(targetRig);
        MorphChannelDefinition targetMorph =
            Assert.Single(
                targetRig.MorphChannels,
                morph =>
                    morph.Index == control.MorphTarget.Index &&
                    string.Equals(
                        morph.Name,
                        control.MorphTarget.Name,
                        StringComparison.Ordinal));
        InstalledSyntheticAnimationImport imported =
            InstalledDl1AuthoringEndToEndFixture.CreateImportedAnimations(
                targetRig,
                targetBone,
                targetMorph);
        Assert.Empty(imported.Body.UnmappedDescriptors);
        Assert.Empty(imported.Body.BindFallbackBoneIndices);
        Assert.Single(imported.Body.Clip.TransformTracks);
        Assert.Single(imported.Mimic.ScalarTracks);
        Assert.NotEmpty(imported.SourceBodyBytes);
        Assert.NotEmpty(imported.SourceMimicBytes);
        Assert.Equal(
            imported.Body.Clip.FrameRate,
            imported.Mimic.FrameRate);
        Assert.Equal(
            imported.Body.Clip.FrameCount,
            imported.Mimic.FrameCount);

        var mapping = new RetargetMap(
            imported.SourceRig.Id,
            targetRig.Id,
            [
                new BoneMapEntry(
                    0,
                    targetBone.Index,
                    BoneMappingMethod.Manual,
                    1),
            ]);
        AnimationDocument baseline = CreateDocument(
            imported,
            targetRig,
            mapping,
            editLayers: [],
            morphEditLayers: []);
        AnimationDocument edited = CreateDocument(
            imported,
            targetRig,
            mapping,
            InstalledDl1AuthoringEndToEndFixture
                .CreateBoneEditLayers(targetBone.Index),
            InstalledDl1AuthoringEndToEndFixture
                .CreateMorphEditLayers(targetMorph.Name));
        Assert.Equal(
            contentSha256.ToUpperInvariant(),
            edited.MappingBinding.TargetAssetFingerprint);
        Assert.Equal(
            RetargetMapFingerprint.Compute(
                edited.MappingBinding.SourceRigSignature,
                edited.MappingBinding.TargetRigSignature,
                contentSha256.ToUpperInvariant(),
                mapping),
            edited.MappingBinding.MappingFingerprint);
        Assert.Single(edited.SynchronizedAnimation.TransformTracks);
        Assert.Single(edited.SynchronizedAnimation.ScalarTracks);

        var evaluator = new AnimationEvaluator();
        double middleTime =
            edited.SynchronizedAnimation.FrameRate.SecondsForFrame(1);
        EvaluationFrame baselineFrame = evaluator.Evaluate(
            baseline.CreateEvaluationRequest(
                middleTime,
                EvaluationPurpose.Export));
        EvaluationFrame editedFrame = evaluator.Evaluate(
            edited.CreateEvaluationRequest(
                middleTime,
                EvaluationPurpose.Export));
        Assert.True(editedFrame.Compatibility!.CanEvaluate);
        AssertNear(
            InstalledDl1AuthoringEndToEndFixture.AuthoredBoneOffset,
            editedFrame.AuthoredPose
                .LocalTransforms[targetBone.Index]
                .Translation.X -
            baselineFrame.AuthoredPose
                .LocalTransforms[targetBone.Index]
                .Translation.X);
        AssertNear(
            0.4 +
            InstalledDl1AuthoringEndToEndFixture.AuthoredMorphOffset,
            editedFrame.AuthoredMorphWeights[targetMorph.Name]);
        Assert.Equal(
            editedFrame.AuthoredPose.LocalTransforms[targetBone.Index],
            editedFrame.DisplayPose.LocalTransforms[targetBone.Index]);
        AssertNear(
            editedFrame.AuthoredMorphWeights[targetMorph.Name],
            editedFrame.DisplayMorphWeights[targetMorph.Name]);

        uint bodyDescriptor =
            targetBone.DescriptorHash ??
            throw new InvalidDataException(
                $"Retail bone '{targetBone.Name}' has no DL1 descriptor.");
        uint mimicDescriptor =
            targetMorph.DescriptorHash ??
            throw new InvalidDataException(
                $"Retail morph '{targetMorph.Name}' has no DL1 descriptor.");
        var exporter = new Dl1AnimationExporter(
            new Anm2EvaluationAdapter(evaluator));
        Dl1AnimationExportResult exported = exporter.Export(
            new Dl1AnimationExportRequest
            {
                Evaluation = edited.CreateEvaluationRequest(
                    0,
                    EvaluationPurpose.Preview),
                Parts = Dl1AnimationExportParts.BodyAndMimic,
                BodyDescriptorOrder = [bodyDescriptor],
                MimicDescriptorOrder = [mimicDescriptor],
            });
        byte[] bodyAnm2 = Assert.IsType<byte[]>(exported.BodyAnm2);
        byte[] mimicAnm2 = Assert.IsType<byte[]>(exported.MimicAnm2);
        Assert.Equal(3, exported.AuthoredSequence.Frames.Length);
        AssertReimportedValues(
            targetRig,
            targetBone,
            targetMorph,
            exported.AuthoredSequence,
            bodyAnm2,
            mimicAnm2,
            bodyDescriptor,
            mimicDescriptor);

        AnimationScrSections script =
            InstalledDl1AuthoringEndToEndFixture
                .CreateAnimationScript();
        byte[] libraryBytes = Rp6lAnimationLibraryCodec.Build(
            new Dictionary<string, byte[]>
            {
                [InstalledDl1AuthoringEndToEndFixture
                    .BodyAnimationName] = bodyAnm2,
                [InstalledDl1AuthoringEndToEndFixture
                    .MimicAnimationName] = mimicAnm2,
            },
            new Dictionary<string, Rp6lAnimationScript>
            {
                [InstalledDl1AuthoringEndToEndFixture
                    .AnimationScriptName] =
                    new(
                        script.RecordsAndNames,
                        script.IndexAndNames),
            });
        string outputPath = Path.Combine(
            directory,
            "common_anims_sp_pc.rpack");
        await Rp6lAnimationLibraryCodec.WriteAtomicAsync(
            outputPath,
            libraryBytes);
        Rp6lAnimationLibrary reopened =
            await Rp6lAnimationLibraryCodec.ExtractAsync(
                outputPath,
                cache);
        Assert.Equal(
            bodyAnm2,
            reopened.Animations[
                InstalledDl1AuthoringEndToEndFixture
                    .BodyAnimationName]);
        Assert.Equal(
            mimicAnm2,
            reopened.Animations[
                InstalledDl1AuthoringEndToEndFixture
                    .MimicAnimationName]);
        Rp6lAnimationScript reopenedScript =
            reopened.AnimationScripts[
                InstalledDl1AuthoringEndToEndFixture
                    .AnimationScriptName];
        ParsedAnimationScr parsed = AnimationScrCodec.Parse(
            new AnimationScrSections(
                reopenedScript.HeaderSection,
                reopenedScript.BodySection));
        ParsedAnimationScrSequence sequence =
            Assert.Single(parsed.Sequences);
        Assert.Equal(
            InstalledDl1AuthoringEndToEndFixture.BodyAnimationName,
            sequence.Name);
        Assert.Equal(0, sequence.StartFrame);
        Assert.Equal(2, sequence.EndFrame);
        Assert.Equal(30, sequence.FramesPerSecond);
        Assert.Empty(Directory.EnumerateFiles(directory, "*.tmp"));
        int positionDeltaCount = control.MorphTarget.Bindings.Sum(
            static binding => binding.PositionDeltaSets.Sum(
                static set => set.PositionDeltas.Count));
        _output.WriteLine(
            $"EXERCISED: {asset.DisplayName} from {Path.GetFileName(asset.Source.ContainerPath)}; " +
            $"{targetRig.BoneCount} bones, {targetRig.MorphChannels.Length} morph channels, " +
            $"{control.MorphTarget.Bindings.Count} decoded bindings and " +
            $"{positionDeltaCount} " +
            $"position deltas for '{control.MorphTarget.Name}', " +
            $"content SHA-256 {contentSha256}.");
    }

    private static AnimationDocument CreateDocument(
        InstalledSyntheticAnimationImport imported,
        RigDefinition targetRig,
        RetargetMap mapping,
        IEnumerable<BoneEditLayer> editLayers,
        IEnumerable<MorphEditLayer> morphEditLayers) =>
        new(
            Guid.Parse("09cc385d-7ab6-499a-a272-4271638d1ab9"),
            "Installed DL1 authoring regression",
            imported.SourceRig,
            targetRig,
            imported.Body.Clip,
            imported.Mimic,
            mapping,
            AnimationRootMode.Bip01,
            PreviewProfile.ThirdPersonAuthoring,
            editLayers,
            morphEditLayers: morphEditLayers);

    private static async Task<InstalledRetailControl>
        SelectRetailControlAsync(
            RpackAssetProvider provider,
            RetailAssetCatalog catalog,
            Rp6lChunkCache cache)
    {
        List<string> rejected = [];
        foreach (string name in InstalledDl1AuthoringEndToEndFixture
                     .PreferredControlNames)
        {
            RetailAssetRecord? asset = catalog.Resolve(
                RetailAssetLogicalId.Rpack(
                    Rp6lResourceTypes.Mesh,
                    name));
            if (asset is null)
            {
                rejected.Add($"{name}: absent from catalog");
                continue;
            }

            if (!string.Equals(
                    asset.Source.ProviderId,
                    provider.ProviderId,
                    StringComparison.Ordinal))
            {
                rejected.Add(
                    $"{name}: winner is not the production RPACK provider");
                continue;
            }

            Rp6lArchive archive = await provider.GetArchiveAsync(
                asset.Source.ContainerPath);
            int resourceIndex = asset.Source.ResourceIndex ??
                throw new InvalidDataException(
                    $"Retail catalog row '{asset.Id}' has no resource index.");
            Rp6lResourceDescriptor resource =
                archive.Resources[resourceIndex];
            Dl1MeshData mesh;
            try
            {
                mesh = await Dl1MeshResourceDecoder.DecodeAsync(
                    archive,
                    resource,
                    cache);
            }
            catch (Exception exception) when (
                exception is InvalidDataException or IOException)
            {
                rejected.Add(
                    $"{name}: {exception.GetType().Name}: {exception.Message}");
                continue;
            }

            if (!mesh.IsStructurallyValid ||
                !mesh.HasDecodedGeometry ||
                !mesh.IsSkinned ||
                mesh.Rig is null ||
                mesh.Rig.MorphChannels.Length == 0)
            {
                rejected.Add(
                    $"{name}: valid={mesh.IsStructurallyValid}, " +
                    $"geometry={mesh.HasDecodedGeometry}, " +
                    $"skinned={mesh.IsSkinned}, " +
                    $"bones={mesh.Rig?.BoneCount ?? 0}, " +
                    $"morphs={mesh.Rig?.MorphChannels.Length ?? 0}");
                continue;
            }

            Dl1MorphTarget? decodedMorph = mesh.MorphTargets
                .FirstOrDefault(static target =>
                    target.PayloadStatus ==
                        Dl1MorphPayloadStatus.VertexDeltasDecoded &&
                    target.Bindings.Count > 0 &&
                    target.Bindings.All(static binding =>
                        binding.PositionDeltaSets.Count > 0 &&
                        binding.PositionDeltaSets.All(set =>
                            set.PositionDeltas.Count ==
                                binding.VertexCount)));
            if (decodedMorph is null)
            {
                rejected.Add(
                    $"{name}: no fully decoded SHORT4 position-delta target");
                continue;
            }

            return new InstalledRetailControl(
                asset,
                archive,
                mesh,
                decodedMorph);
        }

        throw new InvalidDataException(
            "The complete DL1 installation has no decoded skinned/morph-capable " +
            "authoring control. " +
            string.Join("; ", rejected));
    }

    private static BoneDefinition SelectAnimatedBone(
        RigDefinition rig) =>
        rig.Bones.FirstOrDefault(static bone =>
            bone.Kind == BoneKind.Deform &&
            bone.DescriptorHash.HasValue) ??
        rig.Bones.First(static bone =>
            bone.DescriptorHash.HasValue);

    private static void AssertReimportedValues(
        RigDefinition targetRig,
        BoneDefinition targetBone,
        MorphChannelDefinition targetMorph,
        Dl1Anm2AuthoringSequence authored,
        byte[] bodyAnm2,
        byte[] mimicAnm2,
        uint bodyDescriptor,
        uint mimicDescriptor)
    {
        Anm2Clip encodedBody = Anm2Reader.Read(
            bodyAnm2,
            "installed-control-body");
        Anm2Clip encodedMimic = Anm2Reader.Read(
            mimicAnm2,
            "installed-control-mimic");
        Assert.Equal(
            [bodyDescriptor],
            encodedBody.TrackDescriptors.ToArray());
        Assert.Equal(
            [mimicDescriptor],
            encodedMimic.TrackDescriptors.ToArray());
        AnimationClip body = Anm2DomainAdapter.ImportBody(
                encodedBody,
                targetRig,
                authored.FrameRate)
            .Clip;
        AnimationClip mimic = Anm2DomainAdapter.ImportMimic(
            encodedMimic,
            targetRig,
            authored.FrameRate);
        for (int frameIndex = 0;
             frameIndex < authored.Frames.Length;
             frameIndex++)
        {
            Dl1Anm2AuthoringFrame expected = authored.Frames[frameIndex];
            Dl1Anm2TrackSample expectedBone = expected.Tracks.Single(
                sample =>
                    sample.BoneIndex == targetBone.Index &&
                    sample.DescriptorHash == bodyDescriptor);
            Dl1Anm2MorphSample expectedMorph = expected.Morphs.Single(
                sample =>
                    sample.MorphIndex == targetMorph.Index &&
                    sample.DescriptorHash == mimicDescriptor);
            double seconds =
                authored.FrameRate.SecondsForFrame(frameIndex);
            SkeletonPose bodyPose = body.SamplePose(
                targetRig,
                seconds);
            ImmutableDictionary<string, double> morphs =
                mimic.SampleScalars(seconds);
            AssertTransformNear(
                expectedBone.LocalTransform,
                bodyPose.LocalTransforms[targetBone.Index]);
            AssertNear(
                expectedMorph.Value,
                morphs[targetMorph.Name]);
        }
    }

    private static void AssertTransformNear(
        ReAnimated.Core.Mathematics.TransformTRS expected,
        ReAnimated.Core.Mathematics.TransformTRS actual)
    {
        AssertNear(expected.Translation.X, actual.Translation.X);
        AssertNear(expected.Translation.Y, actual.Translation.Y);
        AssertNear(expected.Translation.Z, actual.Translation.Z);
        double rotationDot = Math.Abs(
            (expected.Rotation.X * actual.Rotation.X) +
            (expected.Rotation.Y * actual.Rotation.Y) +
            (expected.Rotation.Z * actual.Rotation.Z) +
            (expected.Rotation.W * actual.Rotation.W));
        AssertNear(1, rotationDot);
        AssertNear(expected.Scale.X, actual.Scale.X);
        AssertNear(expected.Scale.Y, actual.Scale.Y);
        AssertNear(expected.Scale.Z, actual.Scale.Z);
    }

    private static void AssertNear(
        double expected,
        double actual,
        double tolerance = 0.0002) =>
        Assert.InRange(Math.Abs(actual - expected), 0, tolerance);

    private sealed record InstalledRetailControl(
        RetailAssetRecord Asset,
        Rp6lArchive Archive,
        Dl1MeshData Mesh,
        Dl1MorphTarget MorphTarget);
}
