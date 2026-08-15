using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Security.Cryptography;
using ReAnimated.App.Infrastructure;
using ReAnimated.App.ViewModels;
using ReAnimated.Codecs.Fbx;
using ReAnimated.Codecs.Models;
using ReAnimated.Core.Domain;
using ReAnimated.Core.Mathematics;
using ReAnimated.Core.ModelAuthoring;
using ReAnimated.Renderer.D3D11;

namespace ReAnimated.Tests;

public sealed class CustomModelPreviewSessionTests
{
    [Fact]
    [Trait("ValidationTier", "Hermetic")]
    [Trait("Gate", "CustomModelPreview")]
    public void SourcePreviewKeepsFbxUvsWhileDl1PreviewAppliesConfiguredVFlip()
    {
        FbxModelAuthoringImportResult model = CreateModel(flipTextureCoordinateV: true);

        CustomModelPreviewSession source = CustomModelPreviewAdapter.CreateSession(
            model,
            CustomModelPreviewMode.SourceFbx);
        CustomModelPreviewSession dl1 = CustomModelPreviewAdapter.CreateSession(
            model,
            CustomModelPreviewMode.Dl1Output);

        Assert.Equal(CustomModelPreviewMode.SourceFbx, source.EffectiveMode);
        Assert.False(source.AppliesTextureCoordinateVFlip);
        Assert.Equal(0.25f, source.Meshes[0].Vertices.Span[0].TextureCoordinate.Y, precision: 6);
        Assert.Equal(CustomModelPreviewMode.Dl1Output, dl1.EffectiveMode);
        Assert.True(dl1.AppliesTextureCoordinateVFlip);
        Assert.Equal(0.75f, dl1.Meshes[0].Vertices.Span[0].TextureCoordinate.Y, precision: 6);
    }

    [Fact]
    [Trait("ValidationTier", "Hermetic")]
    [Trait("Gate", "CustomModelPreview")]
    public void AnimationTargetUsesIndependentDl1OutputPresentation()
    {
        FbxModelAuthoringImportResult model = CreateModel(
            flipTextureCoordinateV: true);
        byte[] sourceFbxBefore = model.Package.SourceFbx.ToArray();
        CustomModelPreviewSession source =
            CustomModelPreviewAdapter.CreateSession(
                model,
                CustomModelPreviewMode.SourceFbx);
        CustomModelPreviewSession authoring =
            CustomModelPreviewAdapter.CreateSession(
                model,
                CustomModelPreviewMode.Dl1Output);
        CustomModelPreviewSession target =
            MainWindowViewModel.CreateCustomModelAnimationTargetPreview(
                model);

        Assert.Equal(CustomModelPreviewMode.SourceFbx, source.EffectiveMode);
        Assert.Equal(CustomModelPreviewMode.Dl1Output, target.RequestedMode);
        Assert.Equal(CustomModelPreviewMode.Dl1Output, target.EffectiveMode);
        Assert.NotSame(source.Meshes[0], target.Meshes[0]);
        Assert.Equal(
            0.25f,
            source.Meshes[0].Vertices.Span[0].TextureCoordinate.Y,
            precision: 6);
        Assert.Equal(
            0.75f,
            target.Meshes[0].Vertices.Span[0].TextureCoordinate.Y,
            precision: 6);
        Assert.Equal(authoring.Meshes[0].Tint, target.Meshes[0].Tint);
        TextureRenderData authoringTexture = Assert.IsType<TextureRenderData>(
            authoring.Meshes[0].BaseColorTexture);
        TextureRenderData targetTexture = Assert.IsType<TextureRenderData>(
            target.Meshes[0].BaseColorTexture);
        Assert.NotSame(
            source.Meshes[0].BaseColorTexture,
            targetTexture);
        Assert.Equal(
            authoringTexture.Width,
            targetTexture.Width);
        Assert.Equal(
            authoringTexture.Format,
            targetTexture.Format);
        Assert.True(
            authoringTexture.BaseMipBytes.Span
                .SequenceEqual(
                    targetTexture.BaseMipBytes.Span));
        Assert.True(target.AppliesTextureCoordinateVFlip);
        Assert.True(model.Package.SourceFbx.SequenceEqual(sourceFbxBefore));
    }

    [Fact]
    [Trait("ValidationTier", "Hermetic")]
    [Trait("Gate", "CustomModelPreview")]
    public void TimelineSamplingReusesPreparedMeshesAndUpdatesOnlySkeletonData()
    {
        FbxModelAuthoringImportResult model = CreateModel(flipTextureCoordinateV: true);
        CustomModelPreviewSession session = CustomModelPreviewAdapter.CreateSession(
            model,
            CustomModelPreviewMode.Dl1Output);
        var clip = new AnimationClip(
            "synthetic_motion",
            new FrameRate(30, 1),
            frameCount: 2,
            transformTracks:
            [
                new TransformTrack(
                    boneIndex: 0,
                    [
                        new TransformKeyframe(0, TransformTRS.Identity),
                        new TransformKeyframe(
                            1,
                            new TransformTRS(
                                new Vector3D(0, 0.5, 0),
                                QuaternionD.Identity,
                                Vector3D.One)),
                    ]),
            ]);

        CustomModelPreviewPayload first = session.CreatePayload(clip, frame: 0);
        CustomModelPreviewPayload second = session.CreatePayload(clip, frame: 1);

        Assert.Same(first.Meshes[0], second.Meshes[0]);
        Assert.NotSame(first.Skeleton, second.Skeleton);
        Assert.NotEqual(
            first.Skeleton!.Bones[0].WorldTransform,
            second.Skeleton!.Bones[0].WorldTransform);
        Assert.True(session.Matches(model, CustomModelPreviewMode.Dl1Output));
        Assert.False(session.Matches(model, CustomModelPreviewMode.SourceFbx));

        FbxModelAuthoringImportResult changedFlip = WithFlip(model, value: false);
        Assert.False(session.Matches(changedFlip, CustomModelPreviewMode.Dl1Output));
    }

    [Fact]
    [Trait("ValidationTier", "Hermetic")]
    [Trait("Gate", "CustomModelPreview")]
    public void EvaluatedRuntimePoseIsRebasedIntoDl1OutputPresentationRig()
    {
        FbxModelAuthoringImportResult model = CreateModel(
            flipTextureCoordinateV: true);
        RigDefinition runtimeRig = Assert.IsType<RigDefinition>(model.Rig);
        CustomModelPreviewSession session =
            CustomModelPreviewAdapter.CreateSession(
                model,
                CustomModelPreviewMode.Dl1Output);
        var runtimePose = new SkeletonPose(
            runtimeRig,
            runtimeRig.Bones.Select((bone, index) =>
                index == 1
                    ? bone.LocalBindPose with
                    {
                        Translation =
                            bone.LocalBindPose.Translation +
                            new Vector3D(0, 0.25, 0),
                    }
                    : bone.LocalBindPose));

        SkeletonPose presentation = session.CreatePresentationPose(
            runtimePose);
        SkeletonRenderData rendered = session.CreateSkeleton(runtimePose);

        Assert.NotSame(runtimeRig, presentation.Rig);
        Assert.Equal(runtimeRig.BoneCount, presentation.Rig.BoneCount);
        Assert.Equal(
            presentation.GlobalMatrices[1].Translation,
            new Vector3D(
                rendered.Bones[1].WorldTransform.M41,
                rendered.Bones[1].WorldTransform.M42,
                rendered.Bones[1].WorldTransform.M43));
        Assert.Equal(
            session.CreatePayload(clip: null, frame: 0)
                .Skeleton!.Bones.Count,
            rendered.Bones.Count);
    }

    [Fact]
    [Trait("ValidationTier", "Hermetic")]
    [Trait("Gate", "CustomModelPreview")]
    public void Dl1PreparationFailureFallsBackToVisibleSourcePreviewWithoutWeakeningPreparation()
    {
        FbxModelAuthoringImportResult model = CreateModel(
            flipTextureCoordinateV: true,
            duplicateNormalizedBoneName: true);
        RigDefinition sourcePreviewRig = Assert.IsType<RigDefinition>(model.Rig);
        Assert.NotNull(sourcePreviewRig.Bones[0].DescriptorHash);
        Assert.Equal(
            sourcePreviewRig.Bones[0].DescriptorHash,
            sourcePreviewRig.Bones[1].DescriptorHash);
        Assert.Throws<InvalidDataException>(
            model.Package.Document.CreateDl1AnimationRigDefinition);

        CustomModelPreviewSession session = CustomModelPreviewAdapter.CreateSession(
            model,
            CustomModelPreviewMode.Dl1Output);

        Assert.True(session.IsSourceFallback);
        Assert.Equal(CustomModelPreviewMode.Dl1Output, session.RequestedMode);
        Assert.Equal(CustomModelPreviewMode.SourceFbx, session.EffectiveMode);
        Assert.False(session.AppliesTextureCoordinateVFlip);
        Assert.Equal(0.25f, session.Meshes[0].Vertices.Span[0].TextureCoordinate.Y, precision: 6);
        Assert.StartsWith(
            "DL1 Output preview preparation failed",
            session.Diagnostics[0],
            StringComparison.Ordinal);
        Assert.Contains(
            "build and export remain blocked",
            session.Diagnostics[0],
            StringComparison.OrdinalIgnoreCase);

        Assert.Throws<InvalidDataException>(() =>
            Dl1CustomModelRigPreparer.Prepare(model));
    }

    internal static FbxModelAuthoringImportResult CreateModel(
        bool flipTextureCoordinateV,
        bool duplicateNormalizedBoneName = false)
    {
        const string fingerprint =
            "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
        Guid materialId = new("5348d012-9a03-574d-8300-b07080a1900b");
        const string texturePath = "textures/base-color.dds";
        ImmutableArray<byte> texturePayload = CreateDxt1Texture();
        string textureFingerprint = Convert.ToHexString(
                SHA256.HashData(texturePayload.AsSpan()))
            .ToLowerInvariant();
        ImmutableArray<CustomModelBone> bones =
        [
            new CustomModelBone
            {
                Index = 0,
                FbxObjectId = 1,
                Name = duplicateNormalizedBoneName ? "joint" : "root",
                ParentIndex = -1,
                LocalBindTransform = TransformTRS.Identity,
                ExactLocalBindMatrix = TransformMatrix.Identity,
                Kind = BoneKind.Root,
                IsWeighted = true,
            },
            new CustomModelBone
            {
                Index = 1,
                FbxObjectId = 2,
                Name = duplicateNormalizedBoneName ? "JOINT" : "tip",
                ParentIndex = 0,
                LocalBindTransform = new TransformTRS(
                    new Vector3D(1, 0, 0),
                    QuaternionD.Identity,
                    Vector3D.One),
                ExactLocalBindMatrix = TransformMatrix.CreateTranslation(
                    new Vector3D(1, 0, 0)),
                Kind = BoneKind.Deform,
                IsWeighted = true,
            },
        ];
        var document = new CustomModelDocument
        {
            ModelId = new Guid("47ed227b-b874-50fa-9229-d58b35cdca5c"),
            Name = "Synthetic preview model",
            RigMode = CustomModelRigMode.ExactFbxRig,
            Source = new CustomModelSourceIdentity
            {
                OriginalFileName = "synthetic.fbx",
                ContentSha256 = fingerprint,
                FbxVersion = 7400,
            },
            RigSignature = fingerprint,
            Bones = bones,
            Meshes =
            [
                new CustomModelMeshPart
                {
                    Name = "surface",
                    ControlPointCount = 3,
                    PolygonCount = 1,
                    TriangleCount = 1,
                    ExpandedVertexCount = 3,
                    MaterialSlotCount = 1,
                    MaximumSourceInfluences = 1,
                    MaximumRetainedInfluences = 1,
                    RequiredPaletteSize = 2,
                },
            ],
            Materials =
            [
                new CustomModelMaterial
                {
                    Id = materialId,
                    Name = "surface",
                    Textures =
                    [
                        new CustomModelTextureBinding
                        {
                            Semantic = CustomModelTextureSemantic.BaseColor,
                            SourceKind = CustomModelTextureSourceKind.EmbeddedFbx,
                            ColorSpace = CustomModelTextureColorSpace.Srgb,
                            DisplayName = "Generic base color",
                            PackageEntryPath = texturePath,
                            ContentSha256 = textureFingerprint,
                            MediaType = "image/vnd-ms.dds",
                        },
                    ],
                },
            ],
            BuildSettings = new CustomModelBuildSettings
            {
                ResourceName = "synthetic_preview",
                SurfaceName = "default",
                FlipTextureCoordinateV = flipTextureCoordinateV,
            },
        };
        document.Validate();
        RigDefinition rig = document.CreateRigDefinition();
        ImmutableArray<TransformMatrix> globals = rig.CreateBindPose().GlobalMatrices;
        ImmutableArray<FbxModelVertex> vertices =
        [
            Vertex(new Vector3D(0, 0, 0), 0.2, 0.25),
            Vertex(new Vector3D(1, 0, 0), 0.8, 0.25),
            Vertex(new Vector3D(0, 1, 0), 0.2, 0.75),
        ];
        var surface = new FbxModelSurface(
            "surface/0",
            "surface",
            materialId,
            vertices,
            [0, 1, 2],
            [0, 1],
            globals.Select(static matrix => matrix.InvertedAffine()).ToImmutableArray(),
            IsSkinned: true);
        var package = new CustomModelPackage(
            document,
            [0x46, 0x42, 0x58],
            ImmutableDictionary<string, ImmutableArray<byte>>.Empty.Add(
                texturePath,
                texturePayload));
        return new FbxModelAuthoringImportResult(
            package,
            rig,
            [surface],
            ImmutableDictionary<Guid, AnimationClip>.Empty,
            CreateEmptyInspection());
    }

    private static FbxModelVertex Vertex(
        Vector3D position,
        double u,
        double v) =>
        new(
            position,
            Vector3D.UnitZ,
            u,
            v,
            [0],
            [1.0]);

    private static ImmutableArray<byte> CreateDxt1Texture()
    {
        byte[] payload = new byte[136];
        "DDS "u8.CopyTo(payload);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(4), 124);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(12), 4);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(16), 4);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(76), 32);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(84), 0x3154_5844);
        payload.AsSpan(128).Fill(0x5a);
        return payload.ToImmutableArray();
    }

    private static FbxModelAuthoringImportResult WithFlip(
        FbxModelAuthoringImportResult model,
        bool value)
    {
        CustomModelPackage package = model.Package;
        CustomModelDocument document = package.Document with
        {
            BuildSettings = package.Document.BuildSettings with
            {
                FlipTextureCoordinateV = value,
            },
        };
        return model with
        {
            Package = new CustomModelPackage(
                document,
                package.SourceFbx,
                package.TexturePayloads),
        };
    }

    private static FbxStrictExportInspection CreateEmptyInspection() =>
        new(
            [],
            ImmutableDictionary<string, FbxAnimationStackInspection>.Empty,
            ImmutableDictionary<string, long>.Empty,
            ImmutableDictionary<string, long?>.Empty,
            [],
            0,
            0,
            ImmutableHashSet<string>.Empty,
            ImmutableHashSet<string>.Empty,
            ImmutableDictionary<string, FbxMeshGeometryInspection>.Empty,
            0,
            0,
            [],
            [],
            ImmutableHashSet<string>.Empty);
}
