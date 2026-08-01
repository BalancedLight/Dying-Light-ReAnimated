using System.Collections.Immutable;
using System.Security.Cryptography;
using ReAnimated.Codecs.Anm2;
using ReAnimated.Codecs.Rp6l;
using ReAnimated.Core.Domain;
using ReAnimated.Core.Mathematics;
using ReAnimated.DL1.Assets.Catalog;
using ReAnimated.DL1.Assets.Meshes;
using ReAnimated.DL1.Assets.Providers;
using ReAnimated.Evaluation;
using ReAnimated.Retargeting.Mapping;
using ReAnimated.Tests.Fixtures;

namespace ReAnimated.Tests;

public sealed class Dl1AuthoringEndToEndTests
{
    [Fact]
    [Trait("Gate", "DL1AuthoringEndToEnd")]
    public async Task RedistributableFixtureTraversesRetailToRpackAuthoringFlow()
    {
        string directory = RpackTestData.CreateTemporaryDirectory();
        try
        {
            await RunRegressionAsync(directory);
        }
        finally
        {
            RpackTestData.DeleteTemporaryDirectory(directory);
        }
    }

    private static async Task RunRegressionAsync(string directory)
    {
        string basePack = await WriteRetailPackAsync(
            Path.Combine(directory, "base"));
        string dlcPack = await WriteRetailPackAsync(
            Path.Combine(directory, "dlc"));
        string cachePath = Path.Combine(directory, "cache");
        await using var cache = new Rp6lChunkCache(
            new Rp6lChunkCacheOptions
            {
                CacheDirectory = cachePath,
                MaximumMemoryBytes = 8 * 1024 * 1024,
                MaximumMemoryEntryBytes = 8 * 1024 * 1024,
                MaximumDiskBytes = 32 * 1024 * 1024,
            });
        const string installId = "redistributable-dl1-fixture-v1";
        await using var baseProvider = new RpackAssetProvider(
            "base",
            [new RpackSource(basePack, 10)],
            cache,
            installId: installId);
        await using var dlcProvider = new RpackAssetProvider(
            "dlc",
            [new RpackSource(dlcPack, 20)],
            cache,
            installId: installId);
        await using var index = new RetailAssetSqliteIndex(
            Path.Combine(directory, "assets.sqlite"));

        RetailAssetCatalog catalog = await RetailAssetCatalog.BuildAsync(
            [baseProvider, dlcProvider],
            index);
        RetailAssetLogicalId logicalId = RetailAssetLogicalId.Rpack(
            Rp6lResourceTypes.Mesh,
            Dl1AuthoringEndToEndFixture.RetailResourceName);
        RetailAssetRecord winner = Assert.IsType<RetailAssetRecord>(
            catalog.Resolve(logicalId));
        Assert.Equal("dlc", winner.Id.ProviderId);
        Assert.Equal(20, winner.Id.Precedence);
        Assert.Equal(installId, winner.Id.InstallId);
        Assert.Equal(Rp6lResourceTypes.Mesh, winner.Id.ResourceType);
        Assert.Equal(0, winner.Id.SourceIndex);
        Assert.Null(winner.Id.ContentFingerprint);
        Assert.Equal(winner.Id.SourceFingerprint.ToLowerInvariant(),
            winner.Id.SourceFingerprint);
        Assert.Contains("content:pending", winner.Id.StableKey);
        Assert.Equal(2, catalog.GetCandidates(logicalId).Count);
        RetailAssetConflict conflict = Assert.Single(catalog.Conflicts);
        Assert.Equal(winner.Id, conflict.Winner.Id);
        Assert.Equal("base", Assert.Single(conflict.Shadowed).Id.ProviderId);
        IReadOnlyList<RetailAssetRecord> persisted = await index.LoadAsync();
        Assert.Equal(
            ["base", "dlc"],
            persisted
                .Select(static asset => asset.Id.ProviderId)
                .Order(StringComparer.Ordinal)
                .ToArray());

        string contentSha256;
        await using (Stream exactAsset = await catalog.OpenReadAsync(winner.Id))
        {
            contentSha256 = Convert.ToHexString(
                    await SHA256.HashDataAsync(exactAsset))
                .ToLowerInvariant();
        }

        Rp6lArchive retailArchive = await dlcProvider.GetArchiveAsync(
            winner.Source.ContainerPath);
        Rp6lResourceDescriptor retailResource =
            retailArchive.Resources[
                winner.Source.ResourceIndex ??
                throw new InvalidDataException(
                    "The generated catalog row has no resource index.")];
        Assert.Equal(winner.DisplayName, retailResource.Name);
        Dl1MeshData decoded = await Dl1MeshResourceDecoder.DecodeAsync(
            retailArchive,
            retailResource,
            cache);
        Assert.True(decoded.HasDecodedGeometry);
        Assert.True(decoded.IsStructurallyValid);
        Assert.Equal(3, Assert.Single(decoded.Surfaces).Vertices.Count);
        Assert.Equal(3, Assert.Single(decoded.Surfaces).Indices.Count);
        Assert.Equal(2, Assert.Single(
            Assert.Single(decoded.Surfaces).Submeshes).MaterialSlotIndex);
        Assert.Equal(
            Dl1MorphPayloadStatus.VertexDeltasDecoded,
            Assert.Single(decoded.MorphTargets).PayloadStatus);

        RigDefinition targetRig =
            Dl1AuthoringEndToEndFixture.BindRetailIdentity(
                decoded,
                contentSha256,
                winner.Id.StableKey);
        Assert.Equal(contentSha256.ToUpperInvariant(),
            targetRig.SourceAssetFingerprint!.ContentSha256);
        Assert.Equal(winner.Id.StableKey,
            targetRig.SourceAssetFingerprint.ResourceId);
        Assert.Single(targetRig.Bones);
        Assert.Equal("root", targetRig.Bones[0].Name);
        Assert.Equal("smile", Assert.Single(targetRig.MorphChannels).Name);

        SyntheticAnimationImport imported =
            Dl1AuthoringEndToEndFixture.CreateImportedAnimations(
                targetRig);
        Assert.Empty(imported.Body.UnmappedDescriptors);
        Assert.Empty(imported.Body.BindFallbackBoneIndices);
        Assert.Single(imported.Body.Clip.TransformTracks);
        Assert.Single(imported.Mimic.ScalarTracks);
        AssertNear(
            1,
            imported.Body.Clip.SamplePose(
                imported.SourceRig,
                imported.Body.Clip.FrameRate.SecondsForFrame(1))
                .LocalTransforms[0].Translation.X,
            0.0001);
        AssertNear(
            0.6,
            imported.Mimic.SampleScalars(
                imported.Mimic.FrameRate.SecondsForFrame(1))["smile"],
            0.0001);

        var map = new RetargetMap(
            imported.SourceRig.Id,
            targetRig.Id,
            [
                new BoneMapEntry(
                    0,
                    0,
                    BoneMappingMethod.Manual,
                    1),
            ]);
        var document = new AnimationDocument(
            Guid.Parse("13f032b1-a156-4594-b2a6-5c923c77d1ba"),
            "Redistributable DL1 authoring regression",
            imported.SourceRig,
            targetRig,
            imported.Body.Clip,
            imported.Mimic,
            map,
            AnimationRootMode.Bip01,
            Dl1AuthoringEndToEndFixture.CreatePreviewProfile(),
            editLayers:
                Dl1AuthoringEndToEndFixture.CreateBoneEditLayers(),
            morphEditLayers:
                Dl1AuthoringEndToEndFixture.CreateMorphEditLayers());
        Assert.Equal(contentSha256.ToUpperInvariant(),
            document.MappingBinding.TargetAssetFingerprint);
        Assert.Equal(
            RetargetMapFingerprint.Compute(
                document.MappingBinding.SourceRigSignature,
                document.MappingBinding.TargetRigSignature,
                contentSha256.ToUpperInvariant(),
                map),
            document.MappingBinding.MappingFingerprint);
        Assert.Single(document.SynchronizedAnimation.TransformTracks);
        Assert.Single(document.SynchronizedAnimation.ScalarTracks);

        var evaluator = new AnimationEvaluator(
        [
            new ConstantBoneOffsetPreviewStage(
                Dl1AuthoringEndToEndFixture.PreviewStageId,
                0,
                Translation(3),
                AuthoringPreviewFidelity.Bones),
        ]);
        double middleTime =
            document.SynchronizedAnimation.FrameRate.SecondsForFrame(1);
        EvaluationFrame preview = evaluator.Evaluate(
            document.CreateEvaluationRequest(
                middleTime,
                EvaluationPurpose.Preview));
        EvaluationFrame export = evaluator.Evaluate(
            document.CreateEvaluationRequest(
                middleTime,
                EvaluationPurpose.Export));
        Assert.True(preview.Compatibility!.CanEvaluate);
        AssertNear(1.25,
            preview.AuthoredPose.LocalTransforms[0].Translation.X);
        AssertNear(102,
            preview.DisplayPose.LocalTransforms[0].Translation.X);
        AssertNear(1.25,
            export.AuthoredPose.LocalTransforms[0].Translation.X);
        AssertNear(1.25,
            export.DisplayPose.LocalTransforms[0].Translation.X);
        AssertNear(0.7, preview.AuthoredMorphWeights["smile"]);
        Assert.False(preview.DisplayMorphWeights.ContainsKey("smile"));
        AssertNear(0.7, export.AuthoredMorphWeights["smile"]);
        AssertNear(0.7, export.DisplayMorphWeights["smile"]);

        uint bodyDescriptor = targetRig.Bones[0].DescriptorHash!.Value;
        uint mimicDescriptor =
            targetRig.MorphChannels[0].DescriptorHash!.Value;
        var exporter = new Dl1AnimationExporter(
            new Anm2EvaluationAdapter(evaluator));
        Dl1AnimationExportResult exported = exporter.Export(
            new Dl1AnimationExportRequest
            {
                Evaluation = document.CreateEvaluationRequest(
                    0,
                    EvaluationPurpose.Preview),
                Parts = Dl1AnimationExportParts.BodyAndMimic,
                BodyDescriptorOrder = [bodyDescriptor],
                MimicDescriptorOrder = [mimicDescriptor],
            });
        byte[] bodyAnm2 = Assert.IsType<byte[]>(exported.BodyAnm2);
        byte[] mimicAnm2 = Assert.IsType<byte[]>(exported.MimicAnm2);
        Assert.Equal(3, exported.AuthoredSequence.Frames.Length);
        AssertSequenceValues(exported.AuthoredSequence);
        AssertExportedBody(targetRig, bodyAnm2, bodyDescriptor);
        AssertExportedMimic(targetRig, mimicAnm2, mimicDescriptor);

        AnimationScrSections script =
            Dl1AuthoringEndToEndFixture.CreateAnimationScript();
        byte[] libraryBytes = Rp6lAnimationLibraryCodec.Build(
            new Dictionary<string, byte[]>
            {
                [Dl1AuthoringEndToEndFixture.BodyAnimationName] =
                    bodyAnm2,
                [Dl1AuthoringEndToEndFixture.MimicAnimationName] =
                    mimicAnm2,
            },
            new Dictionary<string, Rp6lAnimationScript>
            {
                [Dl1AuthoringEndToEndFixture.AnimationScriptName] =
                    new(
                        script.RecordsAndNames,
                        script.IndexAndNames),
            });
        byte[] rebuilt = Rp6lAnimationLibraryCodec.Build(
            new Dictionary<string, byte[]>
            {
                [Dl1AuthoringEndToEndFixture.MimicAnimationName] =
                    mimicAnm2,
                [Dl1AuthoringEndToEndFixture.BodyAnimationName] =
                    bodyAnm2,
            },
            new Dictionary<string, Rp6lAnimationScript>
            {
                [Dl1AuthoringEndToEndFixture.AnimationScriptName] =
                    new(
                        script.RecordsAndNames,
                        script.IndexAndNames),
            });
        Assert.Equal(libraryBytes, rebuilt);

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
        Assert.Equal(bodyAnm2,
            reopened.Animations[
                Dl1AuthoringEndToEndFixture.BodyAnimationName]);
        Assert.Equal(mimicAnm2,
            reopened.Animations[
                Dl1AuthoringEndToEndFixture.MimicAnimationName]);
        Rp6lAnimationScript reopenedScript =
            reopened.AnimationScripts[
                Dl1AuthoringEndToEndFixture.AnimationScriptName];
        ParsedAnimationScr parsedScript = AnimationScrCodec.Parse(
            new AnimationScrSections(
                reopenedScript.HeaderSection,
                reopenedScript.BodySection));
        ParsedAnimationScrSequence sequence =
            Assert.Single(parsedScript.Sequences);
        Assert.Equal(
            Dl1AuthoringEndToEndFixture.BodyAnimationName,
            sequence.Name);
        Assert.Equal(0, sequence.StartFrame);
        Assert.Equal(2, sequence.EndFrame);
        Assert.Equal(30, sequence.FramesPerSecond);
        Assert.Empty(Directory.EnumerateFiles(directory, "*.tmp"));
    }

    private static Task<string> WriteRetailPackAsync(string directory) =>
        RpackTestData.WriteArchiveAsync(
            directory,
            Dl1AuthoringEndToEndFixture.RetailResourceName,
            Rp6lResourceTypes.Mesh,
            Dl1AuthoringEndToEndFixture.CreateRetailMeshItems(),
            RpackTestCompression.Zlib);

    private static void AssertSequenceValues(
        Dl1Anm2AuthoringSequence sequence)
    {
        double[] expectedBody = [0.25, 1.25, 2.25];
        double[] expectedMimic = [0.2, 0.7, 0.3];
        for (int index = 0; index < sequence.Frames.Length; index++)
        {
            AssertNear(
                expectedBody[index],
                Assert.Single(sequence.Frames[index].Tracks)
                    .LocalTransform.Translation.X);
            AssertNear(
                expectedMimic[index],
                Assert.Single(sequence.Frames[index].Morphs).Value);
        }
    }

    private static void AssertExportedBody(
        RigDefinition targetRig,
        byte[] bodyAnm2,
        uint bodyDescriptor)
    {
        Anm2Clip clip = Anm2Reader.Read(bodyAnm2, "exported-body");
        Assert.Equal([bodyDescriptor], clip.TrackDescriptors.ToArray());
        AnimationClip imported = Anm2DomainAdapter.ImportBody(
                clip,
                targetRig,
                new FrameRate(30, 1))
            .Clip;
        double[] expected = [0.25, 1.25, 2.25];
        for (int frame = 0; frame < expected.Length; frame++)
        {
            SkeletonPose pose = imported.SamplePose(
                targetRig,
                imported.FrameRate.SecondsForFrame(frame));
            AssertNear(
                expected[frame],
                pose.LocalTransforms[0].Translation.X,
                0.0001);
        }
    }

    private static void AssertExportedMimic(
        RigDefinition targetRig,
        byte[] mimicAnm2,
        uint mimicDescriptor)
    {
        Anm2Clip clip = Anm2Reader.Read(
            mimicAnm2,
            "exported-mimic");
        Assert.Equal([mimicDescriptor], clip.TrackDescriptors.ToArray());
        AnimationClip imported = Anm2DomainAdapter.ImportMimic(
            clip,
            targetRig,
            new FrameRate(30, 1));
        double[] expected = [0.2, 0.7, 0.3];
        for (int frame = 0; frame < expected.Length; frame++)
        {
            ImmutableDictionary<string, double> values =
                imported.SampleScalars(
                    imported.FrameRate.SecondsForFrame(frame));
            AssertNear(expected[frame], values["smile"], 0.0001);
        }
    }

    private static TransformTRS Translation(double x) =>
        new(
            new Vector3D(x, 0, 0),
            QuaternionD.Identity,
            Vector3D.One);

    private static void AssertNear(
        double expected,
        double actual,
        double tolerance = 0.0001) =>
        Assert.InRange(Math.Abs(actual - expected), 0, tolerance);
}
