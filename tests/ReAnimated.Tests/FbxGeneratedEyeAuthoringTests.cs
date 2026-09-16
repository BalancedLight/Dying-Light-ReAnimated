using System.Collections.Immutable;
using ReAnimated.Codecs.Fbx;
using ReAnimated.Core.Geometry;
using ReAnimated.Core.Domain;
using ReAnimated.Core.Mathematics;
using ReAnimated.Core.ModelAuthoring;

namespace ReAnimated.Tests;

public sealed class FbxGeneratedEyeAuthoringTests : IDisposable
{
    private readonly string _directory = RpackTestData.CreateTemporaryDirectory();

    [Fact]
    public void AppendingReviewedEyePreservesSourcePayloadMorphsWeightsHandsAndHelperParent()
    {
        FbxModelAuthoringImportResult source = SphereSource(out string componentId);
        ImmutableArray<FbxModelSurface> sourceSurfaces = source.Surfaces;
        ImmutableArray<CustomModelAnimationClip> sourceAnimations = source.Package.Document.AnimationClips;
        var guided = GeneratedBodyWorkflowTests.WithFixtureGuides(source);
        RiggingSession guidedSession = guided.Package.Document.RiggingSession!;
        RigLandmark wrist = guidedSession.Landmarks.Single(landmark => landmark.RoleId == "hand.left");
        RiggingSession withHand = guidedSession with
        {
            Hands = [new RigHandSetup { Side = RigHandSide.Left, WristGuideId = wrist.Id, UserApproved = true }],
        };
        withHand.Validate();
        FbxModelAuthoringImportResult generated = FbxGeneratedBodyBinding.Generate(guided with
        {
            Package = guided.Package with { Document = guided.Package.Document with { RiggingSession = withHand } },
        });
        CustomModelDocument helperDocument = CustomModelHelperAuthoring.DuplicateAsHelper(
            generated.Package.Document, 0, CustomModelAuthoredHelperKind.Helper, "eye_anchor");
        FbxModelAuthoringImportResult withHelper = generated with
        {
            Package = generated.Package with { Document = helperDocument },
            Rig = helperDocument.CreateRigDefinition(),
        };
        FbxEyeGeometryWork geometryWork = FbxEyeAuthoring.InspectGeometry(withHelper, componentId, 0,
            new() { MaximumNormalizedSurfaceError = .5 });
        Guid parent = withHelper.Package.Document.RiggingSession!.Recipe.Assignments
            .Single(assignment => assignment.RoleId == "body.head").EntityId;
        RigEyeSetup setup = new()
        {
            Side = RigEyeSide.Left,
            Mode = RigEyeSetupMode.GeometryPivot,
            GeometryKind = RigEyeGeometryKind.ManualPivot,
            ParentEntityId = parent,
            GlobalFrame = TransformMatrix.CreateTranslation(geometryWork.Detection.Center ?? Vector3D.Zero),
            ComponentId = componentId,
            IslandIndex = 0,
            SourceControlPointIds = geometryWork.Detection.SourceControlPointIds,
            UserApproved = true,
            Evidence = [new RigEvidenceReference { Id = "generated-eye-review", Kind = RigEvidenceKind.UserOverride, Description = "Generic reviewed eye pivot." }],
        };
        RiggingSession eyeSession = withHelper.Package.Document.RiggingSession! with { Eyes = [setup] };
        eyeSession.Validate();
        FbxModelAuthoringImportResult ready = withHelper with
        {
            Package = withHelper.Package with { Document = withHelper.Package.Document with { RiggingSession = eyeSession } },
        };
        FbxModelAuthoringImportResult appended = FbxGeneratedEyeAuthoring.Append(ready, RigEyeSide.Left, "eye_left");
        RigEyeSetup savedSetup = Assert.Single(appended.Package.Document.RiggingSession!.Eyes);
        Assert.NotNull(savedSetup.DeformEntityId);
        Assert.Equal(ready.Package.Document.Bones.Length + 1, appended.Package.Document.Bones.Length);
        Assert.Equal(sourceSurfaces, appended.Surfaces);
        Assert.Equal(sourceAnimations, appended.Package.Document.AnimationClips);
        Assert.Equal(ready.Package.Document.AuthoredHelpers[0].ParentNodeIndex, appended.Package.Document.AuthoredHelpers[0].ParentNodeIndex);
        Assert.Contains(FbxEyeAuthoring.ObserveSourceNodes(appended), node => node.EntityId == savedSetup.DeformEntityId &&
            node.Name == "eye_left" && node.EffectiveBoneKind == BoneKind.Deform);
        Assert.NotNull(appended.Package.Document.AuthoredLayer);
        CustomModelPackageSerializer.ValidateAuthoredLayer(appended.Package);
        Assert.Same(appended, FbxGeneratedEyeAuthoring.Append(appended, RigEyeSide.Left, "eye_left"));

        string path = Path.Combine(_directory, "generated-eye.dlrmodel");
        CustomModelPackageSerializer.SaveAtomic(appended.Package, path);
        FbxModelAuthoringImportResult reopened = FbxModelAuthoringImporter.ImportPackage(CustomModelPackageSerializer.Load(path));
        Assert.Equal(appended.Package.Document.Bones.ToArray(), reopened.Package.Document.Bones.ToArray());
        Assert.Equal(System.Text.Json.JsonSerializer.Serialize(appended.Package.Document.RiggingSession!.Eyes), System.Text.Json.JsonSerializer.Serialize(reopened.Package.Document.RiggingSession!.Eyes));
        Assert.Equal<CustomModelAuthoredHelper>(appended.Package.Document.AuthoredHelpers, reopened.Package.Document.AuthoredHelpers);
        Assert.True(appended.Package.AuthoredLayerPayload.AsSpan().SequenceEqual(reopened.Package.AuthoredLayerPayload.AsSpan()));
    }

    [Fact]
    public void AppendRequiresAReviewedGeometryPivot()
    {
        FbxModelAuthoringImportResult source = GeneratedBodyWorkflowTests.WithFixtureGuides(GeneratedBodyWorkflowTests.Source());
        Assert.Throws<InvalidOperationException>(() => FbxGeneratedEyeAuthoring.Append(source, RigEyeSide.Left, "eye_left"));
    }

    [Fact]
    public void AppendingEyeToAnImportedAffineBaseKeepsTheObservedParentFrame()
    {
        FbxModelAuthoringImportResult imported = FbxModelAuthoringImporter.Import(
            BlenderFbxStrictValidationTests.CreateValidModelFixture(), "imported-eye.fbx");
        CustomModelDocument document = imported.Package.Document;
        RiggingSession session = RiggingSessions.Create(document, RigStudioEntryPath.RepairExistingRig);
        CustomModelBone[] bones = document.Bones.ToArray();
        bones[0] = bones[0] with
        {
            ExactLocalBindMatrix = new(
                1, .15, 0, .1,
                0, 1, .1, .2,
                0, 0, 1, -.1,
                0, 0, 0, 1),
        };
        document = document with
        {
            Bones = bones.ToImmutableArray(),
            RiggingSession = session,
            RigSignature = CustomModelContractSignatures.ComputeRig(bones.ToImmutableArray()),
        };
        var withSession = imported with { Package = imported.Package with { Document = document } };
        FbxEyeSourceNodeObservation parent = FbxEyeAuthoring.ObserveSourceNodes(withSession)[0];
        string componentId = withSession.Surfaces[0].SourceGeometry!.Id;
        FbxEyeGeometryWork geometryWork = FbxEyeAuthoring.InspectGeometry(withSession, componentId, 0, new());
        RigEyeSetup setup = new()
        {
            Side = RigEyeSide.Right,
            Mode = RigEyeSetupMode.GeometryPivot,
            GeometryKind = RigEyeGeometryKind.ManualPivot,
            ParentEntityId = parent.EntityId,
            GlobalFrame = parent.GlobalFrame * TransformMatrix.CreateTranslation(new Vector3D(.1, 0, 0)),
            ComponentId = componentId,
            IslandIndex = 0,
            SourceControlPointIds = geometryWork.Detection.SourceControlPointIds,
            UserApproved = true,
            Evidence = [new RigEvidenceReference { Id = "imported-eye-review", Kind = RigEvidenceKind.UserOverride }],
        };
        session = session with { Eyes = [setup] };
        session.Validate();
        FbxModelAuthoringImportResult ready = withSession with
        {
            Package = withSession.Package with { Document = withSession.Package.Document with { RiggingSession = session } },
        };
        FbxModelAuthoringImportResult appended = FbxGeneratedEyeAuthoring.Append(ready, RigEyeSide.Right, "eye_right");
        Assert.Equal(ready.Package.Document.Bones.Length + 1, appended.Package.Document.Bones.Length);
        Assert.Equal(ready.Surfaces, appended.Surfaces);
        Assert.Equal(parent.EntityId, Assert.Single(appended.Package.Document.RiggingSession!.Eyes).ParentEntityId);
    }

    private static FbxModelAuthoringImportResult SphereSource(out string componentId)
    {
        Vector3D[] points = [new(0, .1, 0), new(0, -.1, 0), new(.1, 0, 0), new(-.1, 0, 0), new(0, 0, .1), new(0, 0, -.1)];
        (int A, int B, int C)[] triangles = [(0, 2, 4), (0, 4, 3), (0, 3, 5), (0, 5, 2), (1, 4, 2), (1, 3, 4), (1, 5, 3), (1, 2, 5)];
        long[] polygons = triangles.SelectMany(triangle => new[] { (long)triangle.A, (long)triangle.B, -triangle.C - 1L }).ToArray();
        byte[] fbx = FbxCustomModelMorphImportTests.CreateMorphFbx(["eye_blink"], firstShapeDeltaX: .005,
            meshVertices: points.SelectMany(point => new[] { point.X * 100, point.Y * 100, point.Z * 100 }).ToArray(), meshPolygons: polygons);
        FbxModelAuthoringImportResult model = FbxModelAuthoringImporter.Import(fbx, "sphere-eye.fbx");
        componentId = model.Surfaces[0].SourceGeometry!.Id;
        return model;
    }

    public void Dispose() => RpackTestData.DeleteTemporaryDirectory(_directory);
}
