using System.Security.Cryptography;
using System.Text.Json;
using ReAnimated.Cli;
using ReAnimated.Codecs.Anm2;
using ReAnimated.Codecs.Rp6l;
using ReAnimated.Core.Domain;
using ReAnimated.Core.Mathematics;
using ReAnimated.Core.Project;
using ReAnimated.DL1.Assets.Catalog;
using ReAnimated.DL1.Assets.Meshes;
using ReAnimated.Retargeting.Mapping;
using ReAnimated.Tests.Fixtures;

namespace ReAnimated.Tests;

public sealed class ProjectExportMimicCliTests
{
    [Fact]
    public void BuildMapPreservesHelperPoliciesAndSourceFanout()
    {
        RigDefinition source = new(
            "source",
            "Source",
            [
                new BoneDefinition(
                    0,
                    "root",
                    -1,
                    TransformTRS.Identity,
                    BoneKind.Root),
                new BoneDefinition(
                    1,
                    "head",
                    0,
                    TransformTRS.Identity),
            ]);
        RigDefinition target = new(
            "target",
            "Target",
            [
                new BoneDefinition(
                    0,
                    "root",
                    -1,
                    TransformTRS.Identity,
                    BoneKind.Root),
                new BoneDefinition(
                    1,
                    "head",
                    0,
                    TransformTRS.Identity),
                new BoneDefinition(
                    2,
                    "EyeCamera",
                    1,
                    TransformTRS.Identity,
                    BoneKind.Camera),
            ]);
        ProjectBoneMapping body = new()
        {
            SourceBoneName = "head",
            TargetBoneName = "head",
            Method = BoneMappingMethod.Manual.ToString(),
            IsReviewed = true,
            MappingKind = RetargetMappingKind.Bone,
            TransferPolicy =
                RetargetTransferPolicy.RotationDelta,
            ComponentPolicy =
                RetargetComponentPolicy.Rotation,
        };
        ProjectBoneMapping helper = new()
        {
            SourceBoneName = "head",
            TargetBoneName = "EyeCamera",
            Method = BoneMappingMethod.Manual.ToString(),
            IsReviewed = true,
            MappingKind =
                RetargetMappingKind.HelperOverride,
            TransferPolicy =
                RetargetTransferPolicy.RestRelative,
            ComponentPolicy =
                RetargetComponentPolicy.RotationTranslation,
        };

        RetargetMap map = ProjectExportCommand.BuildMap(
            source,
            target,
            [body, helper],
            []);

        Assert.Equal(2, map.Entries.Length);
        Assert.All(
            map.Entries,
            entry => Assert.Equal(
                1,
                entry.SourceBoneIndex));
        BoneMapEntry helperEntry = Assert.Single(
            map.Entries,
            entry => entry.TargetBoneIndex == 2);
        Assert.Equal(
            RetargetMappingKind.HelperOverride,
            helperEntry.MappingKind);
        Assert.Equal(
            RetargetTransferPolicy.RestRelative,
            helperEntry.TransferPolicy);
        Assert.Equal(
            RetargetComponentPolicy.RotationTranslation,
            helperEntry.ComponentPolicy);

        Assert.Throws<ArgumentException>(() =>
            ProjectExportCommand.BuildMap(
                source,
                target,
                [body, body],
                []));
    }

    [Fact]
    public async Task ExportProjectSupportsRetailAnm2DirectSameRigPlayback()
    {
        string directory = RpackTestData.CreateTemporaryDirectory();
        try
        {
            string installPath = Path.Combine(directory, "install");
            string meshPackPath =
                await RpackTestData.WriteArchiveAsync(
                    Path.Combine(installPath, "meshes"),
                    Dl1AuthoringEndToEndFixture.RetailResourceName,
                    Rp6lResourceTypes.Mesh,
                    Dl1AuthoringEndToEndFixture.CreateRetailMeshItems(),
                    RpackTestCompression.Zlib);
            await using var cache = new Rp6lChunkCache(
                new Rp6lChunkCacheOptions
                {
                    CacheDirectory = Path.Combine(directory, "cache"),
                });
            Rp6lArchive meshArchive = await Rp6lArchive.OpenAsync(
                meshPackPath);
            Rp6lResourceDescriptor meshResource =
                Assert.Single(meshArchive.Resources);
            Dl1MeshData mesh = await Dl1MeshResourceDecoder.DecodeAsync(
                meshArchive,
                meshResource,
                cache);
            RigDefinition rig = mesh.Rig ??
                throw new InvalidDataException(
                    "Generated retail source fixture has no rig.");
            string meshHash = await HashResourceAsync(
                meshArchive,
                meshResource,
                cache);
            FrameRate rate = new(30, 1);
            TransformTRS bind = rig.Bones[0].LocalBindPose;
            var body = new AnimationClip(
                "retail_direct",
                rate,
                3,
                [
                    new TransformTrack(
                        0,
                        [
                            new TransformKeyframe(0, bind),
                            new TransformKeyframe(
                                1,
                                new TransformTRS(
                                    bind.Translation +
                                        new Vector3D(0.125, 0, 0),
                                    bind.Rotation,
                                    bind.Scale)),
                            new TransformKeyframe(
                                2,
                                new TransformTRS(
                                    bind.Translation +
                                        new Vector3D(0.25, 0, 0),
                                    bind.Rotation,
                                    bind.Scale)),
                        ]),
                ]);
            byte[] bodyBytes = Anm2DomainAdapter.ExportBody(
                body,
                rig,
                [rig.Bones[0].DescriptorHash!.Value]);
            string animationPackPath =
                await RpackTestData.WriteArchiveAsync(
                    Path.Combine(installPath, "animations"),
                    "retail_direct",
                    Rp6lResourceTypes.Animation,
                    [new RpackTestItem(0, bodyBytes)],
                    RpackTestCompression.Zlib);
            Rp6lArchive animationArchive =
                await Rp6lArchive.OpenAsync(animationPackPath);
            Rp6lResourceDescriptor animationResource =
                Assert.Single(animationArchive.Resources);
            string animationHash = await HashResourceAsync(
                animationArchive,
                animationResource,
                cache);
            Anm2TrackPartition partition =
                Anm2TrackPartitioner.Partition(
                    Anm2Reader.Read(bodyBytes),
                    rig,
                    rate).Partition;
            string signature = RigSignature.Compute(rig);
            string installId = RetailAssetIdentity.CreateInstallId(
                installPath);
            Guid animationAssetId = Guid.NewGuid();
            Guid modelAssetId = Guid.NewGuid();
            Guid animationId = Guid.NewGuid();
            ProjectAssetReference modelAsset = new()
            {
                Id = modelAssetId,
                Kind = ProjectAssetKind.RetailGameResource,
                RelativePath = "retail/272/0",
                ResourceId = "fixture-model",
                ContentSha256 = meshHash,
                RetailIdentity = new ProjectRetailAssetIdentity
                {
                    InstallFingerprint = installId,
                    ProviderId = "fixture",
                    ProviderPack = "meshes/fixture.rpack",
                    ResourceType = Rp6lResourceTypes.Mesh,
                    ResourceIndex = 0,
                    ResourceName = meshResource.Name,
                    Precedence = 1,
                    ContentSha256 = meshHash,
                },
            };
            ProjectAssetReference animationAsset = new()
            {
                Id = animationAssetId,
                Kind = ProjectAssetKind.RetailGameResource,
                RelativePath = "retail/320/0",
                ResourceId = "fixture-animation",
                ContentSha256 = animationHash,
                RetailIdentity = new ProjectRetailAssetIdentity
                {
                    InstallFingerprint = installId,
                    ProviderId = "fixture",
                    ProviderPack = "animations/fixture.rpack",
                    ResourceType = Rp6lResourceTypes.Animation,
                    ResourceIndex = 0,
                    ResourceName = animationResource.Name,
                    Precedence = 1,
                    ContentSha256 = animationHash,
                },
            };
            var animation = new ProjectAnimation
            {
                Id = animationId,
                Name = "Retail direct",
                SourceAssetId = animationAssetId,
                SourceBinding = new ProjectAnimationSourceBinding
                {
                    Kind = AnimationSourceKind.RetailAnm2,
                    AssetId = animationAssetId,
                    Roles = partition.Roles,
                    SourceRigSignature = signature,
                    RetailSourceModelAssetId = modelAssetId,
                    TimingProvenance =
                        AnimationTimingProvenance.UserSpecified,
                    Partition = partition,
                },
                TargetAssetId = modelAssetId,
                TargetRigId = rig.Id,
                SourceRigSignature = signature,
                TargetRigSignature = signature,
                FrameRate = rate,
                FrameCount = body.FrameCount,
                RootMotionMode = Dl1RootMotionMode.Recorded,
            };
            string projectDirectory = Path.Combine(directory, "project");
            Directory.CreateDirectory(projectDirectory);
            string projectPath = Path.Combine(
                projectDirectory,
                "retail-direct.dlraproj");
            ProjectSerializer.SaveAtomic(
                DlraProject.Create("Retail direct") with
                {
                    Assets = [animationAsset, modelAsset],
                    Animations = [animation],
                    ActiveAnimationId = animationId,
                },
                projectPath);
            string outputDirectory = Path.Combine(directory, "output");

            int result = await ProjectExportCommand.RunAsync(
                [
                    projectPath,
                    installPath,
                    outputDirectory,
                    animationId.ToString(),
                    "body",
                ],
                new JsonSerializerOptions(),
                CancellationToken.None);

            Assert.Equal(0, result);
            AnimationClip exported = Anm2DomainAdapter.ImportBody(
                await Anm2Reader.ReadFileAsync(
                    Path.Combine(outputDirectory, "Retail direct.anm2")),
                rig,
                rate).Clip;
            SkeletonPose expected = body.SamplePose(
                rig,
                rate.SecondsForFrame(1));
            SkeletonPose actual = exported.SamplePose(
                rig,
                rate.SecondsForFrame(1));
            Assert.InRange(
                Math.Abs(
                    expected.LocalTransforms[0].Translation.X -
                    actual.LocalTransforms[0].Translation.X),
                0,
                0.0001);
        }
        finally
        {
            RpackTestData.DeleteTemporaryDirectory(directory);
        }
    }

    [Fact]
    public async Task ExportProjectResolvesSavedMimicAndWritesItsValues()
    {
        string directory =
            RpackTestData.CreateTemporaryDirectory();
        try
        {
            string installPath = Path.Combine(
                directory,
                "install");
            string packDirectory = Path.Combine(
                installPath,
                "packs");
            string packPath =
                await RpackTestData.WriteArchiveAsync(
                    packDirectory,
                    Dl1AuthoringEndToEndFixture
                        .RetailResourceName,
                    Rp6lResourceTypes.Mesh,
                    Dl1AuthoringEndToEndFixture
                        .CreateRetailMeshItems(),
                    RpackTestCompression.Zlib);
            string cacheDirectory = Path.Combine(
                directory,
                "cache");
            await using var cache = new Rp6lChunkCache(
                new Rp6lChunkCacheOptions
                {
                    CacheDirectory = cacheDirectory,
                });
            Rp6lArchive archive =
                await Rp6lArchive.OpenAsync(packPath);
            Rp6lResourceDescriptor resource =
                Assert.Single(archive.Resources);
            Dl1MeshData mesh =
                await Dl1MeshResourceDecoder.DecodeAsync(
                    archive,
                    resource,
                    cache);
            RigDefinition rig = mesh.Rig ??
                throw new InvalidDataException(
                    "Generated target fixture has no rig.");
            string contentHash;
            await using (Stream resourceStream =
                         await archive.OpenResourceStreamAsync(
                             resource,
                             cache))
            {
                contentHash = Convert.ToHexString(
                        await SHA256.HashDataAsync(
                            resourceStream))
                    .ToLowerInvariant();
            }

            FrameRate rate = new(30, 1);
            var body = new AnimationClip(
                "cli_body",
                rate,
                3,
                [
                    new TransformTrack(
                        0,
                        [
                            new TransformKeyframe(
                                0,
                                rig.Bones[0].LocalBindPose),
                            new TransformKeyframe(
                                1,
                                rig.Bones[0].LocalBindPose),
                            new TransformKeyframe(
                                2,
                                rig.Bones[0].LocalBindPose),
                        ]),
                ]);
            MorphChannelDefinition smile =
                Assert.Single(rig.MorphChannels);
            double[] expected = [0.12, 0.72, 0.32];
            var mimic = new AnimationClip(
                "cli_mimic",
                rate,
                expected.Length,
                scalarTracks:
                [
                    new ScalarTrack(
                        smile.Name,
                        expected.Select(
                            (value, frame) =>
                                new ScalarKeyframe(
                                    frame,
                                    value))),
                ]);
            byte[] bodyBytes =
                Anm2DomainAdapter.ExportBody(
                    body,
                    rig,
                    [rig.Bones[0].DescriptorHash!.Value]);
            byte[] mimicBytes =
                Anm2DomainAdapter.ExportMimic(
                    mimic,
                    rig,
                    [smile.DescriptorHash!.Value]);
            Anm2TrackPartition bodyPartition =
                Anm2TrackPartitioner.Partition(
                    Anm2Reader.Read(bodyBytes),
                    rig,
                    rate).Partition;
            Anm2TrackPartition mimicPartition =
                Anm2TrackPartitioner.Partition(
                    Anm2Reader.Read(mimicBytes),
                    rig,
                    rate).Partition;
            string projectDirectory = Path.Combine(
                directory,
                "project");
            string sourceDirectory = Path.Combine(
                projectDirectory,
                "Sources");
            Directory.CreateDirectory(sourceDirectory);
            await File.WriteAllBytesAsync(
                Path.Combine(
                    sourceDirectory,
                    "body.anm2"),
                bodyBytes);
            await File.WriteAllBytesAsync(
                Path.Combine(
                    sourceDirectory,
                    "face.anm2"),
                mimicBytes);

            string signature =
                RigSignature.Compute(rig);
            Guid bodyAssetId = Guid.NewGuid();
            Guid mimicAssetId = Guid.NewGuid();
            Guid targetAssetId = Guid.NewGuid();
            Guid animationId = Guid.NewGuid();
            var animation = new ProjectAnimation
            {
                Id = animationId,
                Name = "CLI mimic persistence",
                SourceAssetId = bodyAssetId,
                MimicAssetId = mimicAssetId,
                TargetAssetId = targetAssetId,
                TargetRigId = rig.Id,
                SourceRigSignature = signature,
                TargetRigSignature = signature,
                SourceBinding =
                    new ProjectAnimationSourceBinding
                    {
                        Kind = AnimationSourceKind.LocalAnm2,
                        AssetId = bodyAssetId,
                        Roles = bodyPartition.Roles,
                        SourceRigSignature = signature,
                        RetailSourceModelAssetId = targetAssetId,
                        TimingProvenance =
                            AnimationTimingProvenance.UserSpecified,
                        Partition = bodyPartition,
                    },
                FacialAnimationSourceBinding =
                    new ProjectAnimationSourceBinding
                    {
                        Kind = AnimationSourceKind.LocalAnm2,
                        AssetId = mimicAssetId,
                        Roles = AnimationSourceRoles.Facial,
                        SourceRigSignature = signature,
                        RetailSourceModelAssetId = targetAssetId,
                        TimingProvenance =
                            AnimationTimingProvenance.UserSpecified,
                        Partition = mimicPartition,
                    },
                FacialTiming = FacialClipTiming.ForClip(mimic),
                FrameRate = rate,
                FrameCount = body.FrameCount,
                RootMotionMode = Dl1RootMotionMode.Recorded,
            };
            string installId =
                RetailAssetIdentity.CreateInstallId(
                    installPath);
            DlraProject project =
                DlraProject.Create("CLI mimic") with
                {
                    Assets =
                    [
                        new ProjectAssetReference
                        {
                            Id = bodyAssetId,
                            Kind =
                                ProjectAssetKind
                                    .SourceAnimation,
                            RelativePath =
                                "Sources/body.anm2",
                            ContentSha256 =
                                Sha256(bodyBytes),
                        },
                        new ProjectAssetReference
                        {
                            Id = mimicAssetId,
                            Kind =
                                ProjectAssetKind
                                    .SourceAnimation,
                            RelativePath =
                                "Sources/face.anm2",
                            ContentSha256 =
                                Sha256(mimicBytes),
                        },
                        new ProjectAssetReference
                        {
                            Id = targetAssetId,
                            Kind =
                                ProjectAssetKind
                                    .RetailGameResource,
                            RelativePath =
                                "retail/272/0",
                            ResourceId =
                                "fixture-target",
                            ContentSha256 =
                                contentHash,
                            RetailIdentity =
                                new ProjectRetailAssetIdentity
                                {
                                    InstallFingerprint =
                                        installId,
                                    ProviderId =
                                        "fixture",
                                    ProviderPack =
                                        "packs/fixture.rpack",
                                    ResourceType =
                                        Rp6lResourceTypes
                                            .Mesh,
                                    ResourceIndex = 0,
                                    ResourceName =
                                        resource.Name,
                                    Precedence = 1,
                                    ContentSha256 =
                                        contentHash,
                                },
                        },
                    ],
                    Animations = [animation],
                };
            string projectPath = Path.Combine(
                projectDirectory,
                "cli.dlraproj");
            ProjectSerializer.SaveAtomic(
                project,
                projectPath);
            string outputDirectory = Path.Combine(
                directory,
                "output");

            int result =
                await ProjectExportCommand.RunAsync(
                    [
                        projectPath,
                        installPath,
                        outputDirectory,
                        animationId.ToString(),
                        "mimic",
                    ],
                    new JsonSerializerOptions(),
                    CancellationToken.None);

            Assert.Equal(0, result);
            string outputPath = Path.Combine(
                outputDirectory,
                "CLI mimic persistence_mimic.anm2");
            Assert.True(File.Exists(outputPath));
            AnimationClip exported =
                Anm2DomainAdapter.ImportMimicExact(
                    await Anm2Reader.ReadFileAsync(
                        outputPath),
                    rig,
                    rate);
            for (var frame = 0;
                 frame < expected.Length;
                 frame++)
            {
                double actual =
                    exported.SampleScalars(
                        rate.SecondsForFrame(frame))[
                        smile.Name];
                Assert.InRange(
                    Math.Abs(
                        expected[frame] - actual),
                    0,
                    0.0001);
            }
        }
        finally
        {
            RpackTestData.DeleteTemporaryDirectory(
                directory);
        }
    }

    private static string Sha256(byte[] value) =>
        Convert.ToHexString(
                SHA256.HashData(value))
            .ToLowerInvariant();

    private static async Task<string> HashResourceAsync(
        Rp6lArchive archive,
        Rp6lResourceDescriptor resource,
        Rp6lChunkCache cache)
    {
        await using Stream stream =
            await archive.OpenResourceStreamAsync(
                resource,
                cache);
        return Convert.ToHexString(
                await SHA256.HashDataAsync(stream))
            .ToLowerInvariant();
    }
}
