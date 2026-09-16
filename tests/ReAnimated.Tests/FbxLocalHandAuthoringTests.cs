using System.Collections.Immutable;
using ReAnimated.Codecs.Fbx;
using ReAnimated.Core.Geometry;
using ReAnimated.Core.Mathematics;
using ReAnimated.Core.ModelAuthoring;
using ReAnimated.Retargeting.Geometry;

namespace ReAnimated.Tests;

public sealed class FbxLocalHandAuthoringTests
{
    [Fact]
    public void DetectUsesSourceIdentityAndGridEvidenceWithoutChangingTheModel()
    {
        var model = ClosedSourceModel(out string componentId);
        ImmutableArray<FbxModelSurface> surfaces = model.Surfaces;
        CustomModelDocument document = model.Package.Document;
        var work = FbxLocalHandAuthoring.Detect(model, componentId,
            new(-.2, -.1, -.3), new(.2, .2, .3),
            new() { Wrist = new(0, 0, -.1), ExpectedDigits = 1 },
            new() { LongestAxisCells = 12 });

        Assert.Equal(document.Source.ContentSha256, work.Grid.SourceSha256);
        Assert.Equal(work.Grid.InputFingerprint, work.Detection.GridFingerprint);
        Assert.Equal(componentId, work.ComponentId);
        Assert.Equal(RigStudioEntryPath.AdaptExistingRig, work.Session.EntryPath);
        Assert.Equal(surfaces, model.Surfaces);
        Assert.Equal(document.Bones, model.Package.Document.Bones);
        Assert.Equal(document.Source, model.Package.Document.Source);
        Assert.Equal(new(0, 0, -.1), work.Wrist);
    }

    [Fact]
    public void LockedWristGuideOverridesTheDetectionSeed()
    {
        var model = ClosedSourceModel(out string componentId);
        CustomModelDocument document = model.Package.Document;
        RiggingSession session = RiggingSessions.Create(document, RigStudioEntryPath.AdaptExistingRig) with
        {
            Landmarks = [new RigLandmark { RoleId = "hand.left", Position = new(.01, .02, .03), Locked = true }],
        };
        session.Validate();
        model = model with { Package = model.Package with { Document = document with { RiggingSession = session } } };

        var work = FbxLocalHandAuthoring.Detect(model, componentId,
            new(-.2, -.1, -.3), new(.2, .2, .3),
            new() { Wrist = new(3, 3, 3), ExpectedDigits = 1 },
            new() { LongestAxisCells = 12 });

        Assert.Equal(new(.01, .02, .03), work.Wrist);
    }

    [Fact]
    public void RefreshAllowsNavigationButRejectsSourceSurfaceOrBoneChanges()
    {
        var model = ClosedSourceModel(out string componentId);
        var work = FbxLocalHandAuthoring.Detect(model, componentId,
            new(-.2, -.1, -.3), new(.2, .2, .3), new() { ExpectedDigits = 1 },
            new() { LongestAxisCells = 12 });
        FbxModelAuthoringImportResult navigated = work.Model with
        {
            Package = work.Model.Package with
            {
                Document = work.Model.Package.Document with
                {
                    RiggingSession = RiggingSessions.Navigate(work.Session, RigStudioStage.Animate),
                },
            },
        };
        Assert.NotNull(FbxLocalHandAuthoring.RefreshMetadata(work, navigated));
        Assert.Null(FbxLocalHandAuthoring.RefreshMetadata(work, work.Model with { Surfaces = [] }));
        CustomModelBone[] bones = work.Model.Package.Document.Bones.ToArray();
        bones[0] = bones[0] with { Name = bones[0].Name + "_changed" };
        Assert.Null(FbxLocalHandAuthoring.RefreshMetadata(work, work.Model with
        {
            Package = work.Model.Package with
            {
                Document = work.Model.Package.Document with { Bones = bones.ToImmutableArray() },
            },
        }));
    }

    [Fact]
    public void OpenSourceVolumeIsRejectedBeforeHandProposals()
    {
        var model = FbxModelAuthoringImporter.Import(
            BlenderFbxStrictValidationTests.CreateSourceProvenanceFixture(quad: true, splitMaterials: false), "plane.fbx");
        string componentId = model.Surfaces[0].SourceGeometry!.Id;
        Assert.Throws<InvalidDataException>(() => FbxLocalHandAuthoring.Detect(model, componentId,
            new(-1, -1, -1), new(2, 2, 2), new() { ExpectedDigits = 1 },
            new() { LongestAxisCells = 8 }));
    }

    [Fact]
    public void MissingSourceSkinProvenanceIsRejectedWithoutFillingTheSurface()
    {
        var model = ClosedSourceModel(out string componentId);
        FbxModelSurface surface = model.Surfaces[0];
        GeometrySourceComponent geometry = surface.SourceGeometry! with { Skinning = null };
        FbxModelAuthoringImportResult incomplete = model with { Surfaces = [surface with { SourceGeometry = geometry }] };
        Assert.Throws<InvalidDataException>(() => FbxLocalHandAuthoring.Detect(incomplete, componentId,
            new(-.2, -.1, -.3), new(.2, .2, .3), new() { ExpectedDigits = 1 },
            new() { LongestAxisCells = 8 }));
    }

    private static FbxModelAuthoringImportResult ClosedSourceModel(out string componentId)
    {
        var imported = FbxContactAuthoringTests.Model();
        FbxModelSurface sourceSurface = imported.Model.Surfaces[0];
        componentId = sourceSurface.SourceGeometry!.Id;
        GeometrySourceControlPointWeights[] points = Enumerable.Range(0, sourceSurface.Vertices.Length)
            .Select(index => new GeometrySourceControlPointWeights(
                [new GeometrySourceInfluence("skin", "deformer", "bone", index, 0, 1, true)], 1, 0))
            .ToArray();
        GeometrySourceComponent geometry = sourceSurface.SourceGeometry with
        {
            Coordinates = new GeometrySourceCoordinates(1, TransformMatrix.Identity),
            Skinning = new GeometrySourceSkinning(true, points.ToImmutableArray()),
        };
        FbxModelSurface surface = sourceSurface with { SourceGeometry = geometry };
        CustomModelDocument document = imported.Model.Package.Document with { RiggingSession = null };
        return imported.Model with
        {
            Package = imported.Model.Package with { Document = document },
            Surfaces = [surface],
        };
    }
}
