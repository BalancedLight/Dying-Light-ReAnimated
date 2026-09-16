using System.Collections.Immutable;
using ReAnimated.Codecs.Fbx;
using ReAnimated.Core.Mathematics;
using ReAnimated.Core.Domain;
using ReAnimated.Core.ModelAuthoring;
using ReAnimated.Retargeting.Geometry;

namespace ReAnimated.Tests;

public sealed class FbxEyeAuthoringTests
{
    [Fact]
    public void InspectAndSaveGlobePreserveSourceIdentityMorphsAndSetupRows()
    {
        FbxModelAuthoringImportResult model = SphereModel(out string componentId);
        ImmutableArray<FbxModelSurface> surfaces = model.Surfaces;
        byte[] sourceBytes = model.Package.SourceFbx.ToArray();
        FbxEyeGeometryWork work = FbxEyeAuthoring.InspectGeometry(model, componentId, 0,
            new() { MaximumNormalizedSurfaceError = .25 }, CancellationToken.None);
        Assert.Equal(model.Package.Document.Source.ContentSha256, work.Detection.SourceSha256);
        Assert.Equal(componentId, work.Detection.ComponentId);
        Assert.NotEmpty(work.Detection.SourceControlPointIds);
        Assert.NotNull(work.Detection.Center);
        Assert.True(work.Detection.Radius > 0);

        uint morph = Assert.Single(model.Surfaces.SelectMany(surface => surface.MorphTargets)).DescriptorHash;
        RigEyeSetup setup = new()
        {
            Side = RigEyeSide.Left,
            Mode = RigEyeSetupMode.GeometryPivot,
            GeometryKind = RigEyeGeometryKind.GlobeCandidate,
            GlobalFrame = TransformMatrix.CreateTranslation(work.Detection.Center!.Value),
            ComponentId = componentId,
            IslandIndex = 0,
            SourceControlPointIds = work.Detection.SourceControlPointIds,
            GlobeRadius = work.Detection.Radius,
            MorphDescriptors = [morph],
            Evidence = [new RigEvidenceReference
            {
                Id = "generic-eye-detection",
                Kind = RigEvidenceKind.GeometryInference,
                ArtifactSha256 = work.Detection.InputFingerprint,
                Description = "Synthetic spherical source geometry evidence.",
            }],
        };
        FbxModelAuthoringImportResult saved = FbxEyeAuthoring.SaveSetup(work.Model, work.Token, setup);
        Assert.Equal(surfaces, saved.Surfaces);
        Assert.True(saved.Package.SourceFbx.AsSpan().SequenceEqual(sourceBytes));
        Assert.Equal(setup, Assert.Single(saved.Package.Document.RiggingSession!.Eyes));
        Assert.Null(saved.Package.Document.LastBuildReceipt);
    }

    [Fact]
    public void SourceNodeObservationUsesOwnedIdsAndExactAffineGlobals()
    {
        FbxModelAuthoringImportResult imported = FbxModelAuthoringImporter.Import(
            BlenderFbxStrictValidationTests.CreateValidModelFixture(), "eye-rig.fbx");
        CustomModelDocument document = imported.Package.Document;
        RiggingSession session = RiggingSessions.Create(document, RigStudioEntryPath.RepairExistingRig);
        CustomModelBone[] bones = document.Bones.ToArray();
        TransformMatrix affine = new(
            1, .25, 0, .1,
            0, 1, .15, .2,
            0, 0, 1, -.3,
            0, 0, 0, 1);
        bones[1] = bones[1] with { ExactLocalBindMatrix = affine };
        CustomModelDocument changed = document with
        {
            Bones = bones.ToImmutableArray(),
            RiggingSession = session,
            RigSignature = CustomModelContractSignatures.ComputeRig(bones.ToImmutableArray()),
        };
        var model = imported with { Package = imported.Package with { Document = changed } };
        ImmutableArray<FbxEyeSourceNodeObservation> nodes = FbxEyeAuthoring.ObserveSourceNodes(model);
        Assert.Equal(bones.Length, nodes.Length);
        Assert.All(nodes, node => Assert.NotEqual(Guid.Empty, node.EntityId));
        Assert.Equal(bones[1].ExactLocalBindMatrix, nodes[1].LocalFrame);
        Assert.Equal(affine.Translation, nodes[1].GlobalFrame.Translation);
        Assert.Equal("Child", nodes[1].Name);
        Assert.False(string.IsNullOrWhiteSpace(nodes[1].SourceEntityId));

        CustomModelAuthoredHelper helper = new()
        {
            Name = "eye_helper",
            ParentNodeIndex = 0,
            LocalTransform = TransformTRS.Identity,
            ExactLocalMatrix = TransformMatrix.Identity,
            Kind = CustomModelAuthoredHelperKind.Camera,
        };
        CustomModelDocument withHelper = changed with
        {
            AuthoredHelpers = [helper],
            RigSignature = CustomModelContractSignatures.ComputeRig(changed.Bones.Concat(new[]
            {
                new CustomModelBone
                {
                    Index = changed.Bones.Length, Name = helper.Name, ParentIndex = helper.ParentNodeIndex,
                    LocalBindTransform = helper.LocalTransform, ExactLocalBindMatrix = helper.ExactLocalMatrix,
                    Kind = BoneKind.Camera,
                },
            }).ToImmutableArray()),
        };
        RigEntityBinding helperEntity = new()
        {
            EntityId = helper.Id,
            OwnerAssetId = changed.ModelId,
            SourceEntityId = null,
            NativeName = helper.Name,
            Kind = RigNativeEntityKind.Helper,
            Imported = false,
        };
        withHelper = withHelper with
        {
            RiggingSession = session with { Recipe = session.Recipe with { Entities = session.Recipe.Entities.Add(helperEntity) } },
        };
        withHelper.Validate();
        ImmutableArray<FbxEyeSourceNodeObservation> helperNodes = FbxEyeAuthoring.ObserveSourceNodes(
            imported with { Package = imported.Package with { Document = withHelper } });
        FbxEyeSourceNodeObservation observedHelper = Assert.Single(helperNodes, node => node.EntityId == helper.Id);
        Assert.Equal("authored:" + helper.Id.ToString("N"), observedHelper.SourceEntityId);
        Assert.Equal(0, observedHelper.ParentIndex);
        Assert.Equal(helper.Name, observedHelper.Name);
    }

    [Fact]
    public void SourceEyeSetupRequiresExactObservedFrameAndSeparateModeRows()
    {
        FbxModelAuthoringImportResult imported = FbxModelAuthoringImporter.Import(
            BlenderFbxStrictValidationTests.CreateValidModelFixture(), "source-eye.fbx");
        CustomModelDocument document = imported.Package.Document;
        RiggingSession session = RiggingSessions.Create(document, RigStudioEntryPath.AdaptExistingRig);
        var model = imported with { Package = imported.Package with { Document = document with { RiggingSession = session } } };
        FbxEyeSourceNodeObservation source = FbxEyeAuthoring.ObserveSourceNodes(model)[1];
        RigEyeSetup setup = new()
        {
            Side = RigEyeSide.Left,
            Mode = RigEyeSetupMode.SourceEye,
            SourceEntityId = source.EntityId,
            GlobalFrame = source.GlobalFrame,
            Evidence = [new RigEvidenceReference { Id = "source-eye-observation", Kind = RigEvidenceKind.UserOverride }],
        };
        FbxModelAuthoringImportResult saved = FbxEyeAuthoring.SaveSetup(model, session.CreateJobToken(), setup);
        Assert.Single(saved.Package.Document.RiggingSession!.Eyes);
        Assert.Throws<InvalidDataException>(() => FbxEyeAuthoring.SaveSetup(model, session.CreateJobToken(),
            setup with { GlobalFrame = TransformMatrix.CreateTranslation(new(1, 2, 3)) }));

        RigEyeSetup painted = new()
        {
            Side = RigEyeSide.Left,
            Mode = RigEyeSetupMode.GazeReference,
            GeometryKind = RigEyeGeometryKind.Painted,
            GlobalFrame = TransformMatrix.Identity,
            Evidence = [new RigEvidenceReference { Id = "painted-eye", Kind = RigEvidenceKind.UserOverride }],
        };
        FbxModelAuthoringImportResult withPainted = FbxEyeAuthoring.SaveSetup(saved,
            saved.Package.Document.RiggingSession!.CreateJobToken(), painted);
        Assert.Equal(2, withPainted.Package.Document.RiggingSession!.Eyes.Length);
    }

    [Fact]
    public void SourceEyeSetupRejectsCameraNodesButAllowsCameraAsObservedParent()
    {
        FbxModelAuthoringImportResult imported = FbxModelAuthoringImporter.Import(
            BlenderFbxStrictValidationTests.CreateValidModelFixture(), "camera-eye.fbx");
        CustomModelDocument original = imported.Package.Document;
        RiggingSession session = RiggingSessions.Create(original, RigStudioEntryPath.RepairExistingRig);
        CustomModelBone[] bones = original.Bones.ToArray();
        bones[1] = bones[1] with { Name = "EyeCamera", Kind = BoneKind.Camera };
        Guid sourceId = session.Recipe.Entities.Single(entity => entity.NativeName == "Child").EntityId;
        RigEntityBinding cameraEntity = session.Recipe.Entities.Single(entity => entity.EntityId == sourceId) with
        {
            NativeName = "EyeCamera",
            Kind = RigNativeEntityKind.Unknown,
        };
        RiggingSession cameraSession = session with
        {
            Recipe = session.Recipe with { Entities = session.Recipe.Entities.Select(e => e.EntityId == sourceId ? cameraEntity : e).ToImmutableArray() },
        };
        CustomModelDocument document = original with
        {
            Bones = bones.ToImmutableArray(),
            RiggingSession = cameraSession,
            RigSignature = CustomModelContractSignatures.ComputeRig(bones.ToImmutableArray()),
        };
        var model = imported with { Package = imported.Package with { Document = document } };
        FbxEyeSourceNodeObservation camera = FbxEyeAuthoring.ObserveSourceNodes(model).Single(node => node.EntityId == sourceId);
        Assert.Equal(BoneKind.Camera, camera.EffectiveBoneKind);
        RigEyeSetup sourceEye = new()
        {
            Side = RigEyeSide.Left,
            Mode = RigEyeSetupMode.SourceEye,
            SourceEntityId = camera.EntityId,
            ParentEntityId = FbxEyeAuthoring.ObserveSourceNodes(model)[0].EntityId,
            GlobalFrame = camera.GlobalFrame,
            Evidence = [new RigEvidenceReference { Id = "camera-selection", Kind = RigEvidenceKind.UserOverride }],
        };
        Assert.Throws<InvalidOperationException>(() => FbxEyeAuthoring.SaveSetup(model, cameraSession.CreateJobToken(), sourceEye));
    }

    [Fact]
    public void PaintedDetectionAndStaleWorkflowAreExplicitlyRejected()
    {
        FbxModelAuthoringImportResult model = SphereModel(out string componentId);
        FbxEyeGeometryWork painted = FbxEyeAuthoring.InspectGeometry(model, componentId, 0,
            new() { PaintedSurface = true });
        Assert.Equal(EyePivotDetectionStatus.UnsupportedGeometry, painted.Detection.Status);
        Assert.Contains(painted.Detection.Diagnostics, diagnostic => diagnostic.Code == "eye_painted_surface");
        Assert.Null(FbxEyeAuthoring.RefreshMetadata(painted, painted.Model with { Surfaces = [] }));
        var navigated = painted.Model with
        {
            Package = painted.Model.Package with
            {
                Document = painted.Model.Package.Document with
                {
                    RiggingSession = RiggingSessions.Navigate(painted.Session, RigStudioStage.Animate),
                },
            },
        };
        Assert.NotNull(FbxEyeAuthoring.RefreshMetadata(painted, navigated));
    }

    private static FbxModelAuthoringImportResult SphereModel(out string componentId)
    {
        const int segments = 12;
        const double radius = .1;
        var points = new List<Vector3D> { new(0, radius, 0) };
        for (int latitude = 1; latitude <= 3; latitude++)
        {
            double phi = Math.PI * latitude / 4;
            for (int segment = 0; segment < segments; segment++)
            {
                double theta = Math.Tau * segment / segments;
                points.Add(new(radius * Math.Sin(phi) * Math.Cos(theta), radius * Math.Cos(phi), radius * Math.Sin(phi) * Math.Sin(theta)));
            }
        }
        int bottom = points.Count;
        points.Add(new(0, -radius, 0));
        var triangles = new List<(int A, int B, int C)>();
        for (int segment = 0; segment < segments; segment++)
            triangles.Add((0, 1 + (segment + 1) % segments, 1 + segment));
        for (int ring = 0; ring < 2; ring++)
        {
            int first = 1 + ring * segments;
            int second = first + segments;
            for (int segment = 0; segment < segments; segment++)
            {
                int next = (segment + 1) % segments;
                triangles.Add((first + segment, second + segment, first + next));
                triangles.Add((first + next, second + segment, second + next));
            }
        }
        int last = 1 + 2 * segments;
        for (int segment = 0; segment < segments; segment++)
            triangles.Add((bottom, last + segment, last + (segment + 1) % segments));
        long[] polygons = triangles.SelectMany(triangle => new[] { (long)triangle.A, (long)triangle.B, -triangle.C - 1L }).ToArray();
        byte[] fbx = FbxCustomModelMorphImportTests.CreateMorphFbx(["eye_blink"], firstShapeDeltaX: .005,
            meshVertices: points.SelectMany(point => new[] { point.X * 100, point.Y * 100, point.Z * 100 }).ToArray(),
            meshPolygons: polygons);
        FbxModelAuthoringImportResult model = FbxModelAuthoringImporter.Import(fbx, "sphere-eye.fbx");
        componentId = model.Surfaces[0].SourceGeometry!.Id;
        return model;
    }
}
