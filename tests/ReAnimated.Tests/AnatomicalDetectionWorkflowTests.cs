using System.Collections.Immutable;
using ReAnimated.App.Infrastructure;
using ReAnimated.App.ViewModels;
using ReAnimated.Codecs.Fbx;
using ReAnimated.Core.Geometry;
using ReAnimated.Core.Mathematics;
using ReAnimated.Core.ModelAuthoring;
using ReAnimated.Renderer.D3D11;
using ReAnimated.Retargeting.Geometry;

namespace ReAnimated.Tests;

public sealed class AnatomicalDetectionWorkflowTests
{
    [Fact]
    public async Task NullRigDetectionPublishesVisualOnlyGuidesAndOffersExplicitAdoption()
    {
        var model = CreateUnriggedModel();
        var wizard = new RigConformanceWizardViewModel((_, _) => Task.FromResult(Dl1RigTemplateResolution.Failed("player", "No template needed for source guides")), static _ => { });
        wizard.SetModel(model);
        wizard.BodyLeftIsPositiveX = false;
        wizard.BodyDetectionResolution = 48;
        Assert.True(wizard.DetectBodyCommand.CanExecute(null));
        BodyDetectionApplyEventArgs? request = null;
        wizard.BodyGuidesApplyRequested += (_, args) => request = args;
        await wizard.DetectBodyCommand.ExecuteAsync(null);
        var detection = Assert.IsType<AnatomicalDetectionResult>(wizard.BodyDetection);
        Assert.True(wizard.BodyProposals.Count >= 15, wizard.BodyDetectionStatus);
        Assert.Null(wizard.Fit);
        Assert.Null(wizard.Correspondence);
        var sourcePreview = CustomModelPreviewAdapter.CreateSession(model, CustomModelPreviewMode.SourceFbx);
        Assert.Null(sourcePreview.CreateSkeleton(null, 0));
        var overlay = AnatomicalGuideOverlayBuilder.Build(detection, "body.pelvis");
        Assert.True(overlay.Length >= detection.Joints.Length * 3);
        Assert.All(overlay, g => { Assert.Equal(GizmoKind.Line, g.Kind); Assert.Null(g.TranslationBinding); Assert.Null(g.TransformBinding); });
        Assert.True(wizard.UseBodyGuidesCommand.CanExecute(null));
        wizard.UseBodyGuidesCommand.Execute(null);
        Assert.NotNull(request);
        Assert.Same(model, request.Model);
        Assert.True(AnatomicalDetectionAdoption.TryApply(request.Session, request.Token, detection.GridFingerprint, detection, out var adopted));
        Assert.True(adopted.Landmarks.Length >= 15);
        foreach ((string leftRole, string rightRole) in MirrorRolePairs)
        {
            RigLandmark left = adopted.Landmarks.Single(landmark => landmark.RoleId == leftRole);
            RigLandmark right = adopted.Landmarks.Single(landmark => landmark.RoleId == rightRole);
            Assert.Equal(right.Id, left.MirrorPartnerId);
            Assert.Equal(left.Id, right.MirrorPartnerId);
        }
        RigLandmark adoptedLeftArm = adopted.Landmarks.Single(landmark => landmark.RoleId == "arm.left.upper");
        Assert.True(RigLandmarkEditing.TryBeginMove(adopted, adoptedLeftArm.Id, true, out _));
        Assert.Empty(model.Package.Document.Bones);
        Assert.Null(model.Package.Document.RiggingSession);
        Assert.All(adopted.Landmarks, l => { Assert.False(l.UserApproved); Assert.Null(l.Confidence); });
        var saved = model with { Package = model.Package with { Document = model.Package.Document with { RiggingSession = adopted } } };
        wizard.SetModel(saved);
        Assert.Null(wizard.BodyDetection);
        Assert.Equal(adopted.Landmarks.Length, wizard.BodyProposals.Count);
        Assert.All(wizard.BodyProposals, p => Assert.Equal(AnatomicalPlacementMethod.StoredGuide, p.Method));
        Assert.False(wizard.UseBodyGuidesCommand.CanExecute(null));
        Assert.Equal(adopted.Landmarks.Length * 3, AnatomicalGuideOverlayBuilder.BuildStored(wizard.BodyProposals).Length);
        wizard.BodyDetectionResolution = 64;
        Assert.Null(wizard.BodyDetection);
        Assert.False(wizard.UseBodyGuidesCommand.CanExecute(null));
    }

    [Fact]
    public void AdoptionRejectsStaleJobsAndPreservesUnrelatedAndLockedGuides()
    {
        var model = CreateUnriggedModel();
        var session = RiggingSessions.Create(model.Package.Document, RigStudioEntryPath.AutoRigBiped);
        var locked = new RigLandmark { RoleId = "body.pelvis", Position = new(.1, .2, .3), Locked = true, UserApproved = true };
        var eye = new RigLandmark { RoleId = "eye.left", Position = new(.2, 2, .1), UserApproved = true };
        var oneSided = new RigLandmark { RoleId = "body.head", Position = new(0, 2.2, 0), MirrorPartnerId = eye.Id };
        session = RiggingSessions.Change(session, session with { Landmarks = [locked, eye, oneSided] }, RiggingEditKind.Anatomy);
        var token = session.CreateJobToken();
        var detection = new AnatomicalDetectionResult(session.SourceSha256, new string('b', 64), new string('c', 64), new string('d', 64),
            AnatomicalDetectionStatus.Proposed, [new("body.pelvis", Vector3D.Zero, AnatomicalPlacementMethod.RegionCenter, .5, false, null),
                new("body.head", Vector3D.UnitY, AnatomicalPlacementMethod.RegionCenter, .5, false, null)], [], [], [], .05);
        Assert.True(AnatomicalDetectionAdoption.TryApply(session, token, detection.GridFingerprint, detection, out var applied));
        Assert.Equal(locked, applied.Landmarks.Single(l => l.RoleId == locked.RoleId));
        Assert.Equal(eye, applied.Landmarks.Single(l => l.RoleId == eye.RoleId));
        RigLandmark appliedOneSided = applied.Landmarks.Single(l => l.RoleId == oneSided.RoleId);
        Assert.Equal(eye.Id, appliedOneSided.MirrorPartnerId);
        Assert.Equal(oneSided.Locked, appliedOneSided.Locked);
        Assert.NotNull(applied.DetectionBackend);
        Assert.False(AnatomicalDetectionAdoption.TryApply(applied, token, detection.GridFingerprint, detection, out var stale));
        Assert.Same(applied, stale);
        Assert.False(AnatomicalDetectionAdoption.TryApply(session, token, new string('e', 64), detection, out _));
        var replay = RiggingSessions.RestoreForUndo(applied, session);
        Assert.False(AnatomicalDetectionAdoption.TryApply(replay, token, detection.GridFingerprint, detection, out _));
    }

    internal static FbxModelAuthoringImportResult CreateUnriggedModel()
    {
        var fixture = AnatomicalVolumeFixtures.Create();
        var component = fixture.Geometry.Components[0];
        var baseModel = RigConformanceWizardTests.CreateModel();
        var geometry = component.Geometry with { Id = "fbx:200:201", Coordinates = new(1, TransformMatrix.Identity),
            Skinning = new(false, component.Geometry.ControlPoints.Select(static _ => new GeometrySourceControlPointWeights([], 0, 0)).ToImmutableArray()) };
        var document = baseModel.Package.Document with { RigMode = CustomModelRigMode.StaticProp, Bones = [],
            RigSignature = CustomModelContractSignatures.ComputeRig([]),
            Meshes = [new() { Name = "Body", ModelObjectId = 200, GeometryObjectId = 201, ControlPointCount = geometry.ControlPoints.Length,
                PolygonCount = component.Triangles.Length, TriangleCount = component.Triangles.Length, ExpandedVertexCount = geometry.ControlPoints.Length, MaterialSlotCount = 1 }] };
        var surface = new FbxModelSurface("body", "Body", document.Materials[0].Id,
            geometry.ControlPoints.Select(p => new FbxModelVertex(p, Vector3D.UnitY, 0, 0, [], [])).ToImmutableArray(),
            component.Triangles.SelectMany(t => new[] { (uint)t.A, (uint)t.B, (uint)t.C }).ToImmutableArray(), [], [], false) {
            SourceGeometry = geometry, SourceCorners = Enumerable.Range(0, geometry.ControlPoints.Length).Select(static i => new GeometrySourceCorner(i, i)).ToImmutableArray(),
            SourceTriangles = component.Triangles.Select(static t => t.Source).ToImmutableArray() };
        return baseModel with { Package = baseModel.Package with { Document = document }, Rig = null, Surfaces = [surface] };
    }

    private static readonly (string Left, string Right)[] MirrorRolePairs =
    [
        ("arm.left.upper", "arm.right.upper"),
        ("arm.left.lower", "arm.right.lower"),
        ("hand.left", "hand.right"),
        ("leg.left.upper", "leg.right.upper"),
        ("leg.left.lower", "leg.right.lower"),
        ("foot.left", "foot.right"),
    ];
}
