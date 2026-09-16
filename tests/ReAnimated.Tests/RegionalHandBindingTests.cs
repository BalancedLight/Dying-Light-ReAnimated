using System.Collections.Immutable;
using System.Globalization;
using ReAnimated.Codecs.Fbx;
using ReAnimated.Core.Geometry;
using ReAnimated.Core.Mathematics;
using ReAnimated.Core.ModelAuthoring;
using ReAnimated.Retargeting.Geometry;
using ReAnimated.App.Infrastructure;
using ReAnimated.App.ViewModels;
using ReAnimated.Core.Project;

namespace ReAnimated.Tests;

public sealed class RegionalHandBindingTests : IDisposable
{
    private static readonly Vector3D HandOrigin = new(-1.82, 1.6, 0);
    private static readonly Vector3D RegionMinimum = HandOrigin + new Vector3D(-.12, -.06, -.02);
    private static readonly Vector3D RegionMaximum = HandOrigin + new Vector3D(.12, .06, .19);
    private readonly string _directory = RpackTestData.CreateTemporaryDirectory();

    [Fact]
    public async Task WorkspacePreviewSurvivesNavigationAndApplyHasOneUndo()
    {
        var source = HandSource(out string componentId);
        var model = SeedCanonicalWristWeights(GeneratedHandModel(source), componentId);
        using var workspace = new ModelsWorkspaceViewModel(new NoDialogs(), _ => { }, _ => Task.CompletedTask, () => null);
        workspace.CommitProjectRestore(new(model, "regional-hand.dlrmodel", new ProjectModelsWorkspaceState { PackageAssetId = Guid.NewGuid() }));
        workspace.IsConformTabSelected = true;
        var wizard = workspace.Conformance;
        wizard.StudioStage = RigStudioStage.Fit; wizard.HandReviewEnabled = true;
        wizard.HandMinimum.Set(RegionMinimum); wizard.HandMaximum.Set(RegionMaximum); wizard.HandResolution = 48;
        Assert.True(wizard.CanPreviewHandBinding, wizard.HandStatus);
        await wizard.PreviewHandBindingCommand.ExecuteAsync(null);
        var preview = wizard.RegionalHandBinding;
        Assert.NotNull(preview);
        Assert.True(wizard.CanApplyHandBinding, wizard.HandBindingStatus);
        Assert.NotNull(wizard.HandBindingHeatmapBoneIndex);
        wizard.StudioStage = RigStudioStage.Animate;
        Assert.Same(preview.Binding, wizard.RegionalHandBinding!.Binding);
        wizard.StudioStage = RigStudioStage.Fit;
        await wizard.ApplyHandBindingCommand.ExecuteAsync(null);
        var applied = workspace.CaptureProjectSession().Model!;
        Assert.NotEqual(WeightSignature(model), WeightSignature(applied));
        Assert.Equal<SurfaceState>(OutsideStates(model, componentId), OutsideStates(applied, componentId));
        workspace.UndoHelperEditCommand.Execute(null);
        Assert.Equal(WeightSignature(model), WeightSignature(workspace.CaptureProjectSession().Model!));
        workspace.RedoHelperEditCommand.Execute(null);
        Assert.Equal(WeightSignature(applied), WeightSignature(workspace.CaptureProjectSession().Model!));
    }

    private sealed class NoDialogs : IProjectFileDialogService
    {
        public string? ShowOpenProjectDialog(string? initialPath) => null;
        public string? ShowSaveProjectDialog(string suggestedName, string? currentPath) => null;
    }

    [Fact]
    public void RegionalApplyPreservesOutsidePointsMorphsLocksHelperAndReopens()
    {
        FbxModelAuthoringImportResult source = HandSource(out string componentId);
        byte[] sourceBytes = source.Package.SourceFbx.ToArray();
        ImmutableArray<FbxModelSurface> sourceSurfaces = source.Surfaces;
        FbxModelAuthoringImportResult model = GeneratedHandModel(source);
        model = SeedCanonicalWristWeights(model, componentId);
        ImmutableArray<SurfaceState> beforeOutside = OutsideStates(model, componentId);
        string beforeMorphs = MorphSignature(model);
        int beforeBoneCount = model.Package.Document.Bones.Length;

        RegionalHandBindingPreview preview = FbxRegionalHandBinding.Preview(model, RigHandSide.Left, componentId,
            RegionMinimum, RegionMaximum, new() { LongestAxisCells = 48 });
        Assert.True(preview.CanApply, string.Join("; ", preview.Binding.Diagnostics.Select(static d => d.Code + ":" + d.Message)));
        Assert.True(preview.ChangedPointCount > 0);
        Assert.True(FbxRegionalHandBinding.TryApply(model, preview, out FbxModelAuthoringImportResult applied));
        Assert.Equal(beforeBoneCount, applied.Package.Document.Bones.Length);
        Assert.Equal<SurfaceState>(beforeOutside, OutsideStates(applied, componentId));
        Assert.Equal(beforeMorphs, MorphSignature(applied));
        Assert.True(source.Package.SourceFbx.AsSpan().SequenceEqual(sourceBytes));
        Assert.Equal(sourceSurfaces, source.Surfaces);
        Assert.Single(applied.Package.Document.AuthoredHelpers);
        Assert.Contains(applied.Surfaces, surface => surface.Vertices.Any(vertex =>
            vertex.BoneIndices.Select(slot => applied.Package.Document.Bones[surface.PaletteBoneIndices[slot]].Name)
                .Any(name => name.StartsWith("finger_left_", StringComparison.Ordinal))));
        Assert.Contains(applied.Package.Document.Diagnostics, static diagnostic => diagnostic.Code == "regional_hand_binding_review");

        string path = Path.Combine(_directory, "regional-hand.dlrmodel");
        CustomModelPackageSerializer.SaveAtomic(applied.Package, path);
        FbxModelAuthoringImportResult reopened = FbxModelAuthoringImporter.ImportPackage(CustomModelPackageSerializer.Load(path));
        Assert.Equal(MorphSignature(applied), MorphSignature(reopened));
        Assert.Equal(WeightSignature(applied), WeightSignature(reopened));
        Assert.Single(reopened.Package.Document.AuthoredHelpers);
        Assert.Contains(reopened.Package.Document.Diagnostics, d => d.Code == FbxRegionalHandBinding.ReviewDiagnosticCode);
    }

    [Fact]
    public void RegionalPreviewRetainsLockedAndZeroInfluenceFractions()
    {
        var source = HandSource(out string componentId);
        var model = SeedCanonicalWristWeights(GeneratedHandModel(source), componentId);
        RegionalHandBindingPreview preview = FbxRegionalHandBinding.Preview(model, RigHandSide.Left, componentId,
            RegionMinimum, RegionMaximum, new() { LongestAxisCells = 48 });
        Assert.True(preview.CanApply);
        Guid wristEntity = model.Package.Document.RiggingSession!.Recipe.Assignments
            .Single(assignment => assignment.RoleId == "hand.left").EntityId;
        SkinWeightCorrectionPoint lockedPositive = preview.WeightPreview!.SourcePoints.Single(point => point.ControlPointIndex == 0);
        SkinWeightCorrectionPoint lockedZero = preview.WeightPreview.SourcePoints.Single(point => point.ControlPointIndex == 1);
        Assert.Contains(wristEntity, lockedPositive.LockedInfluences);
        Assert.Contains(wristEntity, lockedZero.LockedInfluences);
        Assert.Equal(.6, lockedPositive.Weights.Where(weight => weight.HandleId == wristEntity).Sum(static weight => weight.Weight), 12);
        Assert.DoesNotContain(lockedZero.Weights, weight => weight.HandleId == wristEntity);
        Assert.Equal(.6, preview.Binding.Points.Single(point => point.ControlPointIndex == 0).Influences
            .Where(influence => influence.HandleId == wristEntity).Sum(static influence => influence.Weight), 12);
        Assert.Equal(0, preview.Binding.Points.Single(point => point.ControlPointIndex == 1).Influences
            .Where(influence => influence.HandleId == wristEntity).Sum(static influence => influence.Weight), 12);
    }

    [Fact]
    public void RegionalApplyRefusesStaleWorkExpertBindAndCancellation()
    {
        var source = HandSource(out string componentId);
        var model = SeedCanonicalWristWeights(GeneratedHandModel(source), componentId);
        RegionalHandBindingPreview preview = FbxRegionalHandBinding.Preview(model, RigHandSide.Left, componentId,
            RegionMinimum, RegionMaximum, new() { LongestAxisCells = 48 });

        var changedSession = RiggingSessions.Change(model.Package.Document.RiggingSession!,
            model.Package.Document.RiggingSession! with { WeightMirrorTolerance = .006 }, RiggingEditKind.Skinning);
        var stale = model with { Package = model.Package with { Document = model.Package.Document with { RiggingSession = changedSession } } };
        Assert.False(FbxRegionalHandBinding.TryApply(stale, preview, out _));
        Assert.ThrowsAny<OperationCanceledException>(() => FbxRegionalHandBinding.Preview(model, RigHandSide.Left, componentId,
            RegionMinimum, RegionMaximum, new() { LongestAxisCells = 48 }, cancellationToken: new(true)));

        FbxModelAuthoringImportResult expert = model with
        {
            Surfaces = model.Surfaces.Select(surface => surface with
            {
                InverseBindMatrices = surface.InverseBindMatrices.SetItem(0,
                    surface.InverseBindMatrices[0] * TransformMatrix.CreateTranslation(new(.01, 0, 0))),
            }).ToImmutableArray(),
        };
        Assert.Throws<InvalidOperationException>(() => FbxRegionalHandBinding.Preview(expert, RigHandSide.Left, componentId,
            RegionMinimum, RegionMaximum, new() { LongestAxisCells = 48 }));
    }

    [Fact]
    public void RegionalRefreshAcceptsNavigationAndRejectsSurfaceChanges()
    {
        var source = HandSource(out string componentId);
        var model = SeedCanonicalWristWeights(GeneratedHandModel(source), componentId);
        RegionalHandBindingPreview preview = FbxRegionalHandBinding.Preview(model, RigHandSide.Left, componentId,
            RegionMinimum, RegionMaximum, new() { LongestAxisCells = 32 });
        var navigation = model with
        {
            Package = model.Package with
            {
                Document = model.Package.Document with
                {
                    RiggingSession = RiggingSessions.Navigate(model.Package.Document.RiggingSession!, RigStudioStage.Animate),
                },
            },
        };
        Assert.NotNull(FbxRegionalHandBinding.RefreshMetadata(preview, navigation));
        Assert.Null(FbxRegionalHandBinding.RefreshMetadata(preview, model with { Surfaces = [] }));
    }

    private static FbxModelAuthoringImportResult GeneratedHandModel(FbxModelAuthoringImportResult source)
    {
        FbxModelAuthoringImportResult guided = GeneratedBodyWorkflowTests.WithFixtureGuides(source);
        SourceGeometryAnalysis analysis = FbxSourceGeometryAnalysis.Build(guided);
        SourceGeometryVolume volume = SourceGeometryVolume.Build(analysis);
        SourceVolumeGrid grid = SourceVolumeGrid.BuildRegion(volume, RegionMinimum, RegionMaximum,
            new() { LongestAxisCells = 48 });
        LocalHandDetectionResult detection = LocalHandDetector.Detect(grid,
            new() { Side = "left", Wrist = HandOrigin, ExpectedDigits = 5 });
        Assert.Equal(5, detection.Fingers.Length);
        var selections = detection.Fingers.Select((finger, index) => new HandDigitSelection(
            "digit" + index.ToString(CultureInfo.InvariantCulture), RigFingerPresence.Present, finger.BranchId)).ToArray();
        RiggingSession adopted = HandDetectionAdoption.Adopt(guided.Package.Document.RiggingSession!,
            guided.Package.Document.RiggingSession!.CreateJobToken(), detection, HandOrigin, selections);
        adopted = RiggingSessions.Change(adopted, adopted with
        {
            Hands = adopted.Hands.Select(hand => hand with
            {
                UserApproved = true,
                Fingers = hand.Fingers.Select(finger => finger with { UserApproved = true }).ToImmutableArray(),
            }).ToImmutableArray(),
        }, RiggingEditKind.Anatomy);
        FbxModelAuthoringImportResult withHand = guided with
        {
            Package = guided.Package with { Document = guided.Package.Document with { RiggingSession = adopted } },
        };
        FbxModelAuthoringImportResult generated = FbxGeneratedBodyBinding.Generate(withHand);
        FbxModelAuthoringImportResult appended = FbxGeneratedHandAuthoring.Append(generated, RigHandSide.Left);
        CustomModelDocument document = appended.Package.Document;
        Guid parent = document.RiggingSession!.Recipe.Assignments.Single(assignment => assignment.RoleId == "hand.left").EntityId;
        CustomModelDocument withHelper = RigContactHelperAuthoring.Apply(document, document.RiggingSession.CreateJobToken(), parent,
            null, "regional_helper", "hand.contact", TransformMatrix.Identity, HandOrigin, new(.01, .01, .01),
            RigEvidenceKind.UserOverride, "generic regional hand test helper");
        return appended with
        {
            Package = appended.Package with { Document = withHelper },
            Rig = withHelper.CreateRigDefinition(),
        };
    }

    private static FbxModelAuthoringImportResult SeedCanonicalWristWeights(FbxModelAuthoringImportResult model, string componentId)
    {
        CustomModelDocument document = model.Package.Document;
        int wrist = document.Bones.Single(bone => bone.Name == "hand_left").Index;
        int root = document.Bones.Single(bone => bone.Name == "root_motion").Index;
        TransformMatrix[] globals = new TransformMatrix[document.Bones.Length];
        foreach (CustomModelBone bone in document.Bones)
            globals[bone.Index] = bone.ParentIndex < 0 ? bone.ExactLocalBindMatrix : globals[bone.ParentIndex] * bone.ExactLocalBindMatrix;
        var surfaces = model.Surfaces.Select(surface => surface with
        {
            Vertices = surface.Vertices.Select((vertex, index) =>
            {
                int point = surface.SourceCorners[index].ControlPointIndex;
                return point switch
                {
                    0 => vertex with { BoneIndices = [0, 1], BoneWeights = [.6, .4] },
                    1 => vertex with { BoneIndices = [0, 1], BoneWeights = [0, 1] },
                    _ => vertex with { BoneIndices = [0], BoneWeights = [1] },
                };
            }).ToImmutableArray(),
            PaletteBoneIndices = [wrist, root],
            InverseBindMatrices = [globals[wrist].InvertedAffine(), globals[root].InvertedAffine()],
            IsSkinned = true,
        }).ToImmutableArray();
        RiggingSession session = document.RiggingSession!;
        Guid wristEntity = session.Recipe.Assignments.Single(assignment => assignment.RoleId == "hand.left").EntityId;
        RiggingSession locked = RiggingSessions.Change(session, session with
        {
            WeightLocks = [new RigSkinWeightLock(componentId, 0, wristEntity), new RigSkinWeightLock(componentId, 1, wristEntity)],
        }, RiggingEditKind.Skinning);
        CustomModelDocument updated = document with { RiggingSession = locked };
        return FbxAuthoredModelLayer.Capture(model with { Package = model.Package with { Document = updated }, Surfaces = surfaces });
    }

    private static ImmutableArray<SurfaceState> OutsideStates(FbxModelAuthoringImportResult model, string componentId) =>
        model.Surfaces.Where(surface => surface.SourceGeometry?.Id == componentId)
            .SelectMany(surface => surface.Vertices.Select((vertex, index) => (surface, vertex, index)))
            .Where(row => !Inside(row.surface.SourceGeometry!.ControlPoints[row.surface.SourceCorners[row.index].ControlPointIndex], RegionMinimum, RegionMaximum))
            .Select(row => new SurfaceState(row.surface.SourceCorners[row.index].ControlPointIndex, row.vertex.Position, row.vertex.Normal,
                SemanticWeights(model, row.surface, row.vertex)))
            .OrderBy(state => state.ControlPointIndex).ThenBy(state => state.Position.X).ToImmutableArray();

    private static string SemanticWeights(FbxModelAuthoringImportResult model, FbxModelSurface surface, FbxModelVertex vertex) =>
        string.Join(";", vertex.BoneIndices.Select((slot, index) =>
            model.Package.Document.Bones[surface.PaletteBoneIndices[slot]].Name + ":" + vertex.BoneWeights[index].ToString("R", CultureInfo.InvariantCulture)).Order(StringComparer.Ordinal));

    private static string WeightSignature(FbxModelAuthoringImportResult model) =>
        string.Join("|", model.Surfaces.SelectMany(surface => surface.Vertices.Select((vertex, i) =>
            surface.SourceGeometry!.Id + ":" + surface.SourceCorners[i].ControlPointIndex + ":" + surface.SourceCorners[i].PolygonVertexIndex + "=" + SemanticWeights(model, surface, vertex))).Order(StringComparer.Ordinal));

    private static string MorphSignature(FbxModelAuthoringImportResult model) =>
        string.Join("|", model.Surfaces.SelectMany(surface => surface.MorphTargets.SelectMany(target =>
            target.PositionDeltas.Select((delta, index) =>
            {
                GeometrySourceCorner corner = surface.SourceCorners[index];
                Vector3D normal = target.NormalDeltas.IsDefaultOrEmpty ? Vector3D.Zero : target.NormalDeltas[index];
                return target.DescriptorHash.ToString(CultureInfo.InvariantCulture) + ":" + corner.ControlPointIndex + ":" + corner.PolygonVertexIndex + ":" +
                    $"{delta.X:R},{delta.Y:R},{delta.Z:R}:{normal.X:R},{normal.Y:R},{normal.Z:R}";
            }))).Order(StringComparer.Ordinal));

    private static bool Inside(Vector3D point, Vector3D minimum, Vector3D maximum) =>
        point.X >= minimum.X && point.X <= maximum.X && point.Y >= minimum.Y && point.Y <= maximum.Y && point.Z >= minimum.Z && point.Z <= maximum.Z;

    private static FbxModelAuthoringImportResult HandSource(out string componentId)
    {
        SourceGeometryAnalysis hand = LocalHandDetectorTests.Geometry();
        var points = new List<Vector3D>();
        var triangles = new List<(int A, int B, int C)>();
        foreach (SourceGeometryComponentAnalysis component in hand.Components)
        {
            int baseIndex = points.Count;
            points.AddRange(component.Geometry.ControlPoints.Select(point => point + HandOrigin));
            triangles.AddRange(component.Triangles.Select(triangle => (triangle.A + baseIndex, triangle.B + baseIndex, triangle.C + baseIndex)));
        }
        int distantBase = points.Count;
        points.AddRange(BoxPoints(new(5, 5, 5), new(5.2, 5.2, 5.2)));
        triangles.AddRange(BoxTriangles(distantBase));
        long[] polygons = triangles.SelectMany(triangle => new[] { (long)triangle.A, (long)triangle.B, -triangle.C - 1L }).ToArray();
        byte[] fbx = FbxCustomModelMorphImportTests.CreateMorphFbx(["generic_smile"], firstShapeDeltaX: .01,
            meshVertices: points.SelectMany(point => new[] { point.X * 100, point.Y * 100, point.Z * 100 }).ToArray(), meshPolygons: polygons);
        FbxModelAuthoringImportResult imported = FbxModelAuthoringImporter.Import(fbx, "regional-hand.fbx");
        componentId = imported.Surfaces[0].SourceGeometry!.Id;
        return imported;
    }

    private static ImmutableArray<Vector3D> BoxPoints(Vector3D minimum, Vector3D maximum) =>
        [new(minimum.X, minimum.Y, minimum.Z), new(maximum.X, minimum.Y, minimum.Z), new(maximum.X, maximum.Y, minimum.Z), new(minimum.X, maximum.Y, minimum.Z),
         new(minimum.X, minimum.Y, maximum.Z), new(maximum.X, minimum.Y, maximum.Z), new(maximum.X, maximum.Y, maximum.Z), new(minimum.X, maximum.Y, maximum.Z)];

    private static ImmutableArray<(int A, int B, int C)> BoxTriangles(int offset) =>
        [(offset + 0, offset + 2, offset + 1), (offset + 0, offset + 3, offset + 2), (offset + 4, offset + 5, offset + 6), (offset + 4, offset + 6, offset + 7),
         (offset + 0, offset + 1, offset + 5), (offset + 0, offset + 5, offset + 4), (offset + 3, offset + 7, offset + 6), (offset + 3, offset + 6, offset + 2),
         (offset + 0, offset + 4, offset + 7), (offset + 0, offset + 7, offset + 3), (offset + 1, offset + 2, offset + 6), (offset + 1, offset + 6, offset + 5)];

    public void Dispose() => RpackTestData.DeleteTemporaryDirectory(_directory);

    private sealed record SurfaceState(int ControlPointIndex, Vector3D Position, Vector3D Normal, string Weights);
}
