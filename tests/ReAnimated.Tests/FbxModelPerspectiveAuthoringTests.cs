using System.Collections.Immutable;
using System.Security.Cryptography;
using ReAnimated.Codecs.Fbx;
using ReAnimated.Codecs.Models;
using ReAnimated.Core.Domain;
using ReAnimated.Core.Geometry;
using ReAnimated.Core.ModelAuthoring;
using ReAnimated.Core.Mathematics;

namespace ReAnimated.Tests;

public sealed class FbxModelPerspectiveAuthoringTests
{
    [Fact]
    public void FilteringMergedSurfacePreservesSourceWeightsMorphsAndFullModel()
    {
        FbxModelAuthoringImportResult original = CreateMergedModel();
        byte[] source = original.Package.SourceFbx.ToArray();
        FbxModelSurface surface = original.Surfaces[0];
        CustomModelPerspectiveSelection selection = Selection(
            original,
            new CustomModelPerspectiveTriangleKey(
                surface.SourceGeometry!.Id,
                surface.SourceTriangles[1]));

        FbxModelAuthoringImportResult filtered =
            FbxModelPerspectiveAuthoring.Apply(original, selection);

        Assert.Equal(source, original.Package.SourceFbx.ToArray());
        Assert.Equal(original.Package.Document, filtered.Package.Document);
        Assert.Equal(3, filtered.Surfaces[0].Vertices.Length);
        Assert.Single(filtered.Surfaces[0].SourceTriangles);
        Assert.Single(filtered.Surfaces[0].MorphTargets);
        Assert.Equal(3, filtered.Surfaces[0].MorphTargets[0].PositionDeltas.Length);
        Assert.Equal(surface.PaletteBoneIndices, filtered.Surfaces[0].PaletteBoneIndices);
        Assert.Equal(surface.InverseBindMatrices, filtered.Surfaces[0].InverseBindMatrices);
    }

    [Fact]
    public void ThirdPersonIsCompleteAndStaleSelectionIsRejected()
    {
        FbxModelAuthoringImportResult original = CreateMergedModel();
        CustomModelPerspectiveSelection full = Selection(
            original,
            perspective: CustomModelPerspective.ThirdPerson);
        Assert.Same(original, FbxModelPerspectiveAuthoring.Apply(original, full));

        FbxModelSurface changed = original.Surfaces[0] with
        {
            SourceTriangles = [new GeometrySourceTriangle(99, 0), new GeometrySourceTriangle(100, 0)],
        };
        FbxModelAuthoringImportResult changedModel =
            original with { Surfaces = [changed] };
        Assert.Throws<InvalidOperationException>(() =>
            FbxModelPerspectiveAuthoring.Apply(changedModel, Selection(
                original,
                new CustomModelPerspectiveTriangleKey(
                    original.Surfaces[0].SourceGeometry!.Id,
                    original.Surfaces[0].SourceTriangles[0]))));

        FbxModelSurface changedWeights = original.Surfaces[0] with
        {
            Vertices = original.Surfaces[0].Vertices.SetItem(
                0,
                original.Surfaces[0].Vertices[0] with
                {
                    BoneWeights = [0.9, 0.1],
                }),
        };
        Assert.Throws<InvalidOperationException>(() =>
            FbxModelPerspectiveAuthoring.Apply(
                original with { Surfaces = [changedWeights] },
                Selection(original, new CustomModelPerspectiveTriangleKey(
                    original.Surfaces[0].SourceGeometry!.Id,
                    original.Surfaces[0].SourceTriangles[0]))));
    }

    [Fact]
    public void SurfaceDecisionWorksWithoutTriangleMetadataAndUnknownIdsFail()
    {
        FbxModelAuthoringImportResult original = CreateMergedModel();
        FbxModelSurface opaque = original.Surfaces[0] with
        {
            Indices = [], SourceCorners = [], SourceTriangles = [],
        };
        FbxModelAuthoringImportResult opaqueModel = original with { Surfaces = [opaque] };
        CustomModelPerspectiveSelection surfaceSelection = Selection(
            opaqueModel,
            hiddenSurface: new CustomModelPerspectiveSurfaceKey(
                opaque.SourceGeometry!.Id,
                opaque.Id));
        Assert.Empty(FbxModelPerspectiveAuthoring.Apply(opaqueModel, surfaceSelection).Surfaces);

        CustomModelPerspectiveSelection unknown = Selection(
            original,
            hidden: new CustomModelPerspectiveTriangleKey("unknown", new(99, 0)));
        Assert.Throws<InvalidOperationException>(() =>
            FbxModelPerspectiveAuthoring.Apply(original, unknown));

        Assert.Empty(FbxModelPerspectiveAuthoring.ProposeFirstPerson(opaqueModel).Reviews);
        Assert.Single(FbxModelPerspectiveAuthoring.Apply(
            opaqueModel,
            Selection(opaqueModel)).Surfaces);
    }

    [Fact]
    public void SemanticHeadRoleProposesHideButIncidentalNameDoesNot()
    {
        FbxModelAuthoringImportResult original = CreateMergedModel();
        BoneDefinition[] bones = original.Rig!.Bones
            .Select((bone, index) => index == 0
                ? new BoneDefinition(index, "arbitrary_node", bone.ParentIndex,
                    bone.LocalBindPose, bone.Kind, bone.RequiredForExport,
                    bone.DescriptorHash, "body.head")
                : bone)
            .ToArray();
        FbxModelSurface weighted = original.Surfaces[0] with
        {
            Vertices = original.Surfaces[0].Vertices.Select(vertex => vertex with
            {
                BoneIndices = [0], BoneWeights = [1.0],
            }).ToImmutableArray(),
        };
        FbxModelAuthoringImportResult semantic = original with
        {
            Rig = new RigDefinition(original.Rig.Id, original.Rig.DisplayName, bones),
            Surfaces = [weighted],
        };
        Assert.All(
            FbxModelPerspectiveAuthoring.ProposeFirstPerson(semantic).Reviews,
            review => Assert.True(review.ProposedHidden));

        BoneDefinition[] incidentalBones = bones.Select((bone, index) => index == 0
            ? new BoneDefinition(index, "headgear", bone.ParentIndex, bone.LocalBindPose,
                bone.Kind, bone.RequiredForExport, bone.DescriptorHash)
            : bone).ToArray();
        FbxModelAuthoringImportResult incidental = semantic with
        {
            Rig = new RigDefinition(original.Rig.Id, original.Rig.DisplayName, incidentalBones),
        };
        Assert.DoesNotContain(
            FbxModelPerspectiveAuthoring.ProposeFirstPerson(incidental).Reviews,
            static review => review.ProposedHidden);
    }

    [Fact]
    public void PersistedSelectionRetainsKeptSurfaceIntentAndOrderedMorphs()
    {
        FbxModelAuthoringImportResult original = CreateMergedModel();
        FbxModelSurface surface = original.Surfaces[0];
        CustomModelPerspectiveSelection selection = Selection(
            original,
            new CustomModelPerspectiveTriangleKey(
                surface.SourceGeometry!.Id,
                surface.SourceTriangles[1])) with
        {
            KeptSurfaces =
            [new CustomModelPerspectiveSurfaceKey(surface.SourceGeometry.Id, surface.Id)],
        };
        CustomModelPackage package = original.Package with
        {
            Document = original.Package.Document with { FirstPersonVisibility = selection },
        };
        string path = Path.Combine(Path.GetTempPath(), $"perspective-{Guid.NewGuid():N}.dlrmodel");
        try
        {
            CustomModelPackageSerializer.SaveAtomic(package, path);
            CustomModelPackage loaded = CustomModelPackageSerializer.Load(path);
            CustomModelPerspectiveSelection persisted =
                Assert.IsType<CustomModelPerspectiveSelection>(
                    loaded.Document.FirstPersonVisibility);
            Assert.Equal(selection.KeptSurfaces.ToArray(), persisted.KeptSurfaces.ToArray());
            Assert.Equal(selection.HiddenTriangles.ToArray(), persisted.HiddenTriangles.ToArray());
            FbxModelAuthoringImportResult filtered =
                FbxModelPerspectiveAuthoring.Apply(original, persisted);
            Assert.Equal(
                new Vector3D(0.1, 0, 0),
                filtered.Surfaces[0].MorphTargets[0].PositionDeltas[0]);
            Assert.Equal(
                new Vector3D(0.1, 0, 0),
                filtered.Surfaces[0].MorphTargets[0].PositionDeltas[1]);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void ExportViewFiltersMorphInventoryAndPreservesEditableSource()
    {
        FbxModelAuthoringImportResult original = CreateMergedModel();
        CustomModelMorphChannel bodyMorph = new()
        {
            Index = 0,
            Name = "shape",
            DescriptorHash = 1,
            BlendShapeChannelObjectId = 2,
            ShapeObjectId = 3,
            GeometryObjectIds = [4],
        };
        CustomModelDocument document = original.Package.Document with
        {
            MorphChannels = [bodyMorph],
            MorphSignature = new string('a', 64),
        };
        original = original with { Package = original.Package with { Document = document } };
        byte[] sourceBefore = original.Package.SourceFbx.ToArray();
        FbxModelPerspectiveExportView export =
            FbxModelPerspectiveAuthoring.CreateFirstPersonExportView(
                original,
                Selection(original, hidden: new CustomModelPerspectiveTriangleKey(
                    original.Surfaces[0].SourceGeometry!.Id,
                    original.Surfaces[0].SourceTriangles[1])));

        Assert.Equal(["shape"], export.RetainedMorphNames.ToArray());
        Assert.Empty(export.DroppedMorphNames);
        Assert.Equal(["shape"], export.Model.Package.Document.MorphChannels.Select(static channel => channel.Name).ToArray());
        Assert.Equal(sourceBefore, original.Package.SourceFbx.ToArray());
        Assert.Equal(document, original.Package.Document);
    }

    [Fact]
    public void ExportViewDropsHeadOnlyMorphsWithEmptyDerivedInventory()
    {
        FbxModelAuthoringImportResult original = CreateMergedModel();
        CustomModelMorphChannel headMorph = new()
        {
            Index = 0,
            Name = "shape",
            DescriptorHash = 1,
            BlendShapeChannelObjectId = 2,
            ShapeObjectId = 3,
            GeometryObjectIds = [4],
        };
        original = original with
        {
            Package = original.Package with
            {
                Document = original.Package.Document with
                {
                    MorphChannels = [headMorph],
                    MorphSignature = new string('b', 64),
                },
            },
        };
        FbxModelPerspectiveExportView export =
            FbxModelPerspectiveAuthoring.CreateFirstPersonExportView(
                original,
                Selection(original, hiddenSurface: new CustomModelPerspectiveSurfaceKey(
                    original.Surfaces[0].SourceGeometry!.Id,
                    original.Surfaces[0].Id)));

        Assert.Empty(export.RetainedMorphNames);
        Assert.Equal(["shape"], export.DroppedMorphNames.ToArray());
        Assert.Empty(export.Model.Package.Document.MorphChannels);
        Assert.Equal(CustomModelDocument.EmptyMorphSignature,
            export.Model.Package.Document.MorphSignature);
    }

    [Fact]
    public async Task SeparateHeadSurfaceExportRetainsBodyMorphAndUnrelatedScalarTrack()
    {
        FbxModelAuthoringImportResult original = CreateSeparateMorphModel();
        FbxModelSurface head = original.Surfaces[0];
        CustomModelPerspectiveSelection selection = Selection(
            original,
            hiddenSurface: new CustomModelPerspectiveSurfaceKey(
                head.SourceGeometry!.Id,
                head.Id));
        FbxModelPerspectiveExportView export =
            FbxModelPerspectiveAuthoring.CreateFirstPersonExportView(original, selection);

        Assert.Equal(["body_shape"], export.RetainedMorphNames.ToArray());
        Assert.Equal(["head_shape"], export.DroppedMorphNames.ToArray());
        Assert.Equal(["body_shape"], export.Model.Package.Document.MorphChannels.Select(static channel => channel.Name).ToArray());
        AnimationClip clip = Assert.Single(export.Model.AnimationClips.Values);
        Assert.Equal(["body_shape", "unrelated_scalar"], clip.ScalarTracks.Select(static track => track.ChannelName).ToArray());
        Assert.Equal(["head_shape", "body_shape"], original.Package.Document.MorphChannels.Select(static channel => channel.Name).ToArray());

        string output = Path.Combine(Path.GetTempPath(), $"perspective-writer-{Guid.NewGuid():N}");
        try
        {
            await Dl1SourceModelWriter.WriteAsync(new()
            {
                Model = export.Model,
                OutputDirectory = output,
                ResourceName = "perspective_export",
                SurfaceName = "default",
            });
        }
        finally
        {
            if (Directory.Exists(output)) Directory.Delete(output, recursive: true);
        }
    }

    private static CustomModelPerspectiveSelection Selection(
        FbxModelAuthoringImportResult model,
        CustomModelPerspectiveTriangleKey? hidden = null,
        CustomModelPerspective perspective = CustomModelPerspective.FirstPerson,
        CustomModelPerspectiveSurfaceKey? hiddenSurface = null) =>
        new()
        {
            Perspective = perspective,
            SourceSha256 = model.Package.Document.Source.ContentSha256,
            SourceGeometryFingerprint = FbxModelPerspectiveAuthoring.ComputeGeometryFingerprint(model),
            HiddenTriangles = hidden is { } key ? [key] : [],
            HiddenSurfaces = hiddenSurface is { } surface ? [surface] : [],
        };

    private static FbxModelAuthoringImportResult CreateMergedModel()
    {
        FbxModelAuthoringImportResult baseModel =
            CustomModelPreviewSessionTests.CreateModel(flipTextureCoordinateV: false);
        baseModel = baseModel with { Package = baseModel.Package with { Document = baseModel.Package.Document with
        {
            Source = baseModel.Package.Document.Source with
            {
                ContentSha256 = Convert.ToHexStringLower(SHA256.HashData(baseModel.Package.SourceFbx.AsSpan())),
            },
        } } };
        FbxModelSurface source = baseModel.Surfaces[0];
        ImmutableArray<FbxModelVertex> vertices =
        [source.Vertices[0], source.Vertices[1], source.Vertices[2], source.Vertices[0] with
        {
            Position = source.Vertices[0].Position with { X = 2.0 },
        }];
        GeometrySourceComponent component = new(
            "merged-body-head",
            [new(0, 0, 0), new(1, 0, 0), new(0, 1, 0), new(2, 0, 0)]);
        FbxModelMorphTarget morph = new(
            "shape",
            1,
            2,
            3,
            vertices.Select(vertex => vertex.Position with { X = 0.1 }).ToImmutableArray());
        FbxModelSurface merged = source with
        {
            Vertices = vertices,
            Indices = [0, 1, 2, 0, 2, 3],
            SourceGeometry = component,
            SourceCorners = [new(0, 0), new(1, 1), new(2, 2), new(0, 3)],
            SourceTriangles = [new(0, 0), new(1, 0)],
            MorphTargets = [morph],
        };
        return baseModel with { Surfaces = [merged] };
    }

    private static FbxModelAuthoringImportResult CreateSeparateMorphModel()
    {
        FbxModelAuthoringImportResult baseModel = CreateMergedModel();
        FbxModelSurface source = baseModel.Surfaces[0];
        FbxModelSurface head = source with
        {
            Id = "head-surface",
            SourceGeometry = source.SourceGeometry! with { Id = "head-component" },
            Indices = [0, 1, 2],
            SourceCorners = source.SourceCorners,
            SourceTriangles = [new GeometrySourceTriangle(0, 0)],
            MorphTargets = [source.MorphTargets[0] with { Name = "head_shape", DescriptorHash = 10 }],
        };
        FbxModelSurface body = source with
        {
            Id = "body-surface",
            SourceGeometry = source.SourceGeometry! with { Id = "body-component" },
            Indices = [0, 2, 3],
            SourceCorners = source.SourceCorners,
            SourceTriangles = [new GeometrySourceTriangle(1, 0)],
            MorphTargets = [source.MorphTargets[0] with { Name = "body_shape", DescriptorHash = 11 }],
        };
        ImmutableArray<CustomModelMorphChannel> channels =
        [
            new() { Index = 0, Name = "head_shape", DescriptorHash = 10, BlendShapeChannelObjectId = 20, ShapeObjectId = 21, GeometryObjectIds = [22] },
            new() { Index = 1, Name = "body_shape", DescriptorHash = 11, BlendShapeChannelObjectId = 23, ShapeObjectId = 24, GeometryObjectIds = [25] },
        ];
        CustomModelDocument document = baseModel.Package.Document with
        {
            MorphChannels = channels,
            MorphSignature = new string('c', 64),
        };
        AnimationClip clip = new(
            "morphs",
            new FrameRate(30, 1),
            1,
            scalarTracks:
            [
                new ScalarTrack("head_shape", [new ScalarKeyframe(0, 1)]),
                new ScalarTrack("body_shape", [new ScalarKeyframe(0, 1)]),
                new ScalarTrack("unrelated_scalar", [new ScalarKeyframe(0, 1)]),
            ]);
        CustomModelAnimationClip metadata = baseModel.Package.Document.AnimationClips.IsEmpty
            ? new() { Id = Guid.NewGuid(), FbxObjectId = 30, SourceName = "morphs", DisplayName = "morphs", FrameCount = 1, SourceFingerprint = new string('d', 64), HasMorphTracks = true }
            : baseModel.Package.Document.AnimationClips[0];
        document = document with { AnimationClips = [metadata] };
        return baseModel with
        {
            Package = baseModel.Package with { Document = document },
            Surfaces = [head, body],
            AnimationClips = ImmutableDictionary<Guid, AnimationClip>.Empty.Add(metadata.Id, clip),
        };
    }
}
