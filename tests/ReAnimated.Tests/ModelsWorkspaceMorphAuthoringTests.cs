using System.Collections.Immutable;
using System.Numerics;
using System.Security.Cryptography;
using ReAnimated.App.Infrastructure;
using ReAnimated.App.ViewModels;
using ReAnimated.Codecs.Fbx;
using ReAnimated.Codecs.Models;
using ReAnimated.Core.Geometry;
using ReAnimated.Core.Mathematics;
using ReAnimated.Core.ModelAuthoring;
using ReAnimated.Renderer.D3D11;

namespace ReAnimated.Tests;

public sealed class ModelsWorkspaceMorphAuthoringTests
{
    private const string Expression = "face_expression";
    private const string ZeroExpression = "face_zero";
    private const uint Descriptor = 0x1234;
    private const uint ZeroDescriptor = 0x5678;
    private static readonly int[] ReviewedRegion = [0, 1, 2];
    private static readonly int[] LockedRegion = [2];

    [Fact]
    [Trait("ValidationTier", "Hermetic")]
    [Trait("Gate", "ViewModelWpf")]
    public async Task AttachmentImportRejectsBoneSelectionChangedDuringFileChoice()
    {
        string directory = RpackTestData.CreateTemporaryDirectory();
        try
        {
            string target = Path.Combine(directory, "target.dlrmodel"), accessory = Path.Combine(directory, "accessory.fbx");
            CustomModelPackageSerializer.SaveAtomic(CreateGenericReferencePackage(), target);
            await File.WriteAllBytesAsync(accessory, CreateGenericManualSculptFbx(0));
            ModelsWorkspaceViewModel? active = null;
            var dialogs = new MorphDialogs(target, target, accessory)
            {
                BeforeFbxDialogReturn = () => active!.SelectedBone = null,
            };
            using var workspace = CreateWorkspace(dialogs);
            active = workspace;
            await workspace.OpenPackagePathAsync(target);
            workspace.SelectedBone = workspace.Bones[0];
            var before = CurrentPackage(workspace);
            var originalSurface = CurrentSurface(workspace);
            await workspace.AddCharacterAttachmentCommand.ExecuteAsync(null);
            Assert.Contains("selection changed", workspace.BuildStatus, StringComparison.Ordinal);
            Assert.Same(originalSurface, CurrentSurface(workspace));
            Assert.Equal(before.Document.GeometryRevision, CurrentPackage(workspace).Document.GeometryRevision);
            Assert.False(workspace.UndoHelperEditCommand.CanExecute(null));
        }
        finally { RpackTestData.DeleteTemporaryDirectory(directory); }
    }

    [Fact]
    [Trait("ValidationTier", "Hermetic")]
    [Trait("Gate", "ViewModelWpf")]
    public async Task FaceRayPickingAddsOnlyCurrentReviewedIdsWithoutMutatingModelHistory()
    {
        string directory = RpackTestData.CreateTemporaryDirectory();
        try
        {
            (string targetPath, string referencePath) = SaveGenericPackages(directory);
            using var workspace = CreateWorkspace(new MorphDialogs(targetPath, referencePath));
            await workspace.OpenPackagePathAsync(targetPath);
            await workspace.LoadMorphReferenceCommand.ExecuteAsync(null);
            workspace.IsCharacterTabSelected = true;
            workspace.ExpressionCorrespondenceReviewed = true;
            workspace.ExpressionReviewed = true;
            CustomModelPackage before = CurrentPackage(workspace);
            FbxModelSurface targetBefore = CurrentSurface(workspace);
            Assert.True(targetBefore.IsSkinned);
            Assert.NotEmpty(targetBefore.InverseBindMatrices);
            Assert.NotEmpty(targetBefore.PaletteBoneIndices);
            Assert.False(workspace.UndoHelperEditCommand.CanExecute(null));

            workspace.PickReferenceFaceCommand.Execute(null);
            IRenderBrushTarget referenceInput = Assert.IsAssignableFrom<IRenderBrushTarget>(workspace.Viewport.SceneSource.BrushTarget);
            Assert.True(referenceInput.IsBrushEnabled);
            Assert.Equal("Original neutral", workspace.ExpressionPreviewSubject);
            AssertValidStaticFaceMesh(workspace);
            Assert.InRange(VisibleFirstX(workspace), -0.001f, 0.001f);
            var sourceRay = new RenderBrushPointerRay(new Vector3(.1f, .1f, 1), -Vector3.UnitZ);
            Assert.True(referenceInput.TryBeginBrush(sourceRay));
            referenceInput.CompleteBrush(false);
            Assert.DoesNotContain("selected vertex", workspace.FacePickStatus, StringComparison.Ordinal);
            Assert.True(referenceInput.TryBeginBrush(sourceRay));
            referenceInput.CompleteBrush(true);
            Assert.Contains("selected vertex 0, triangle 0", workspace.FacePickStatus, StringComparison.Ordinal);
            Assert.NotEmpty(workspace.Viewport.SceneSource.CaptureFrame().Gizmos);

            workspace.PickTargetFaceCommand.Execute(null);
            Assert.False(referenceInput.IsBrushEnabled);
            IRenderBrushTarget targetInput = Assert.IsAssignableFrom<IRenderBrushTarget>(workspace.Viewport.SceneSource.BrushTarget);
            Assert.Equal("Neutral", workspace.ExpressionPreviewSubject);
            AssertValidStaticFaceMesh(workspace);
            Assert.InRange(VisibleFirstX(workspace), 9.999f, 10.001f);
            var targetRay = new RenderBrushPointerRay(new Vector3(10.1f, .1f, 1), -Vector3.UnitZ);
            Assert.True(targetInput.TryBeginBrush(targetRay));
            targetInput.CompleteBrush(true);
            Assert.Contains("selected vertex 0, triangle 0", workspace.FacePickStatus, StringComparison.Ordinal);
            Assert.NotEmpty(workspace.Viewport.SceneSource.CaptureFrame().Gizmos);

            workspace.AddPickedLandmarkCommand.Execute(null);
            workspace.AddPickedTriangleCommand.Execute(null);
            workspace.AddPickedRegionCommand.Execute(null);
            workspace.AddPickedLockCommand.Execute(null);
            Assert.Equal("0 0 1", workspace.ExpressionLandmarks.Trim());
            Assert.Equal("0 0", workspace.ExpressionCorrespondences.Trim());
            Assert.Equal("0", workspace.ExpressionTargetRegion);
            Assert.Equal("0", workspace.ExpressionLockedVertices);
            Assert.False(workspace.ExpressionCorrespondenceReviewed);
            Assert.False(workspace.ExpressionReviewed);
            Assert.Same(before, CurrentPackage(workspace));
            Assert.Same(targetBefore, CurrentSurface(workspace));
            Assert.True(CurrentSurface(workspace).IsSkinned);
            Assert.NotEmpty(CurrentSurface(workspace).InverseBindMatrices);
            Assert.NotEmpty(CurrentSurface(workspace).PaletteBoneIndices);
            Assert.False(workspace.UndoHelperEditCommand.CanExecute(null));

            workspace.StopFacePickingCommand.Execute(null);
            Assert.Null(workspace.Viewport.SceneSource.BrushTarget);
            Assert.True(Assert.Single(workspace.Viewport.SceneSource.CaptureFrame().Meshes).IsSkinned);

            workspace.PickTargetFaceCommand.Execute(null);
            IRenderBrushTarget staleByExpression = Assert.IsAssignableFrom<IRenderBrushTarget>(workspace.Viewport.SceneSource.BrushTarget);
            Assert.True(staleByExpression.TryBeginBrush(targetRay));
            workspace.ExpressionName = ZeroExpression;
            Assert.False(staleByExpression.IsBrushEnabled);
            staleByExpression.CompleteBrush(true);
            workspace.AddPickedRegionCommand.Execute(null);
            Assert.Equal("0", workspace.ExpressionTargetRegion);

            workspace.PickTargetFaceCommand.Execute(null);
            IRenderBrushTarget staleBySurface = Assert.IsAssignableFrom<IRenderBrushTarget>(workspace.Viewport.SceneSource.BrushTarget);
            Assert.True(staleBySurface.TryBeginBrush(targetRay));
            workspace.SelectedCharacterSurface = null;
            Assert.False(staleBySurface.IsBrushEnabled);
            staleBySurface.CompleteBrush(true);
            workspace.SelectedCharacterSurface = "surface/0";
            workspace.AddPickedLockCommand.Execute(null);
            Assert.Equal("0", workspace.ExpressionLockedVertices);

            workspace.PickTargetFaceCommand.Execute(null);
            IRenderBrushTarget staleByModel = Assert.IsAssignableFrom<IRenderBrushTarget>(workspace.Viewport.SceneSource.BrushTarget);
            Assert.True(staleByModel.TryBeginBrush(targetRay));
            await workspace.OpenPackagePathAsync(targetPath);
            Assert.False(staleByModel.IsBrushEnabled);
            staleByModel.CompleteBrush(true);
            Assert.Null(workspace.Viewport.SceneSource.BrushTarget);
        }
        finally { RpackTestData.DeleteTemporaryDirectory(directory); }
    }

    [Fact]
    [Trait("ValidationTier", "Hermetic")]
    [Trait("Gate", "ViewModelWpf")]
    public async Task MissingOriginalReferenceReportsPrerequisitesWithoutOpeningSculptOrEditing()
    {
        string directory = RpackTestData.CreateTemporaryDirectory();
        try
        {
            (string targetPath, string referencePath) = SaveGenericPackages(directory);
            using var workspace = CreateWorkspace(new MorphDialogs(targetPath, referencePath));
            await workspace.OpenPackagePathAsync(targetPath);
            CustomModelPackage before = CurrentPackage(workspace);
            await workspace.ImportManualShapeCommand.ExecuteAsync(null);
            Assert.Contains("reference models", workspace.BuildStatus, StringComparison.Ordinal);
            workspace.ProposeExpressionCommand.Execute(null);
            Assert.Contains("reference models", workspace.BuildStatus, StringComparison.Ordinal);
            workspace.AcceptExpressionCommand.Execute(null);
            Assert.Contains("Propose an expression", workspace.BuildStatus, StringComparison.Ordinal);
            workspace.PreviewReferenceExpressionCommand.Execute(null);
            Assert.Contains("Load a character reference", workspace.BuildStatus, StringComparison.Ordinal);
            Assert.Same(before, CurrentPackage(workspace));
            Assert.Empty(CurrentSurface(workspace).MorphTargets);
            Assert.False(workspace.UndoHelperEditCommand.CanExecute(null));
        }
        finally { RpackTestData.DeleteTemporaryDirectory(directory); }
    }
    [Fact]
    [Trait("ValidationTier", "Hermetic")]
    [Trait("Gate", "ViewModelWpf")]
    public async Task ReopeningTargetWithSameSurfaceIdentityInvalidatesPendingExpressionReview()
    {
        string directory = RpackTestData.CreateTemporaryDirectory();
        try
        {
            (string targetPath, string referencePath) = SaveGenericPackages(directory);
            using var workspace = CreateWorkspace(new MorphDialogs(targetPath, referencePath));
            await workspace.OpenPackagePathAsync(targetPath);
            await workspace.LoadMorphReferenceCommand.ExecuteAsync(null);
            SetReviewedCorrespondence(workspace);
            workspace.ExpressionCorrespondenceReviewed = true;
            workspace.ProposeExpressionCommand.Execute(null);
            workspace.ExpressionReviewed = true;
            string? surface = workspace.SelectedCharacterSurface;

            await workspace.OpenPackagePathAsync(targetPath);

            Assert.Equal(surface, workspace.SelectedCharacterSurface);
            Assert.False(workspace.ExpressionReviewed);
            Assert.False(workspace.ExpressionCorrespondenceReviewed);
            workspace.AcceptExpressionCommand.Execute(null);
            Assert.Contains("Propose an expression", workspace.BuildStatus, StringComparison.Ordinal);
            Assert.Empty(CurrentSurface(workspace).MorphTargets);
            Assert.Empty(CurrentPackage(workspace).Document.MorphAuthoringRecords);
            Assert.False(workspace.UndoHelperEditCommand.CanExecute(null));
        }
        finally { RpackTestData.DeleteTemporaryDirectory(directory); }
    }
    [Fact]
    [Trait("ValidationTier", "Hermetic")]
    [Trait("Gate", "ViewModelWpf")]
    public async Task NeutralObjExportAndReviewedSculptImportRetainAnatomyProvenanceAndUndo()
    {
        string directory = RpackTestData.CreateTemporaryDirectory();
        try
        {
            (string targetPath, string referencePath) = SaveGenericPackages(directory);
            string neutralPath = Path.Combine(directory, "neutral.obj");
            string sculptPath = Path.Combine(directory, "sculpt.obj");
            var dialogs = new MorphDialogs(targetPath, referencePath, sculptPath) { NeutralExportPath = neutralPath };
            using var workspace = CreateWorkspace(dialogs);
            await workspace.OpenPackagePathAsync(targetPath);
            await workspace.LoadMorphReferenceCommand.ExecuteAsync(null);
            CustomModelPackage before = CurrentPackage(workspace);
            Vector3D[] neutral = CurrentSurface(workspace).Vertices.Select(v => v.Position).ToArray();

            await workspace.ExportManualNeutralCommand.ExecuteAsync(null);

            Assert.True(File.Exists(neutralPath));
            Assert.Contains("exported in meters", workspace.BuildStatus, StringComparison.Ordinal);
            Assert.Same(before, CurrentPackage(workspace));
            Assert.False(workspace.UndoHelperEditCommand.CanExecute(null));
            byte[] neutralBytes = await File.ReadAllBytesAsync(neutralPath);
            Assert.All(ManualMorphObjCodec.ComputePositionDeltas(CurrentSurface(workspace), neutralBytes), delta => Assert.Equal(Vector3D.Zero, delta));
            string sculptText = System.Text.Encoding.UTF8.GetString(neutralBytes);
            string[] lines = sculptText.Split('\n');
            int firstVertex = Array.FindIndex(lines, line => line.StartsWith("v ", StringComparison.Ordinal));
            Assert.True(firstVertex >= 0);
            lines[firstVertex] = "v 10.25 0 0";
            await File.WriteAllTextAsync(sculptPath, string.Join('\n', lines));
            workspace.ExpressionReviewed = true;

            await workspace.ImportManualShapeCommand.ExecuteAsync(null);

            Assert.Contains("Manual expression imported", workspace.BuildStatus, StringComparison.Ordinal);
            FbxModelMorphTarget expression = Assert.Single(CurrentSurface(workspace).MorphTargets);
            Assert.Equal(Expression, expression.Name);
            Assert.Equal(new Vector3D(0.25, 0, 0), expression.PositionDeltas[0]);
            Assert.All(expression.PositionDeltas.Skip(1), delta => Assert.Equal(Vector3D.Zero, delta));
            Assert.Equal(neutral, CurrentSurface(workspace).Vertices.Select(v => v.Position).ToArray());
            MorphAuthoringRecord record = Assert.Single(CurrentPackage(workspace).Document.MorphAuthoringRecords);
            Assert.Equal(MorphAuthoringMethod.ManualSculpt, record.Method);
            Assert.Equal(5, record.OriginalSourceChannelIndex);
            workspace.UndoHelperEditCommand.Execute(null);
            Assert.Empty(CurrentSurface(workspace).MorphTargets);
            Assert.Empty(CurrentPackage(workspace).Document.MorphAuthoringRecords);
            Assert.Equal(neutral, CurrentSurface(workspace).Vertices.Select(v => v.Position).ToArray());
        }
        finally { RpackTestData.DeleteTemporaryDirectory(directory); }
    }
    [Theory]
    [InlineData("triangles")]
    [InlineData("landmarks")]
    [InlineData("region")]
    [InlineData("locks")]
    [Trait("ValidationTier", "Hermetic")]
    [Trait("Gate", "ViewModelWpf")]
    public async Task OversizedReviewedCorrespondenceIndexIsReportedWithoutAuthoringMutation(string field)
    {
        string directory = RpackTestData.CreateTemporaryDirectory();
        try
        {
            (string targetPath, string referencePath) = SaveGenericPackages(directory);
            using var workspace = CreateWorkspace(new MorphDialogs(targetPath, referencePath));
            await workspace.OpenPackagePathAsync(targetPath);
            await workspace.LoadMorphReferenceCommand.ExecuteAsync(null);
            SetReviewedCorrespondence(workspace);
            const string hugeIndex = "999999999999999999999";
            switch (field)
            {
                case "triangles": workspace.ExpressionCorrespondences = hugeIndex + " 0"; break;
                case "landmarks": workspace.ExpressionLandmarks = hugeIndex + " 0"; break;
                case "region": workspace.ExpressionTargetRegion = hugeIndex; break;
                case "locks": workspace.ExpressionLockedVertices = hugeIndex; break;
                default: throw new ArgumentOutOfRangeException(nameof(field));
            }
            workspace.ExpressionCorrespondenceReviewed = true;
            CustomModelPackage before = CurrentPackage(workspace);
            Assert.False(workspace.UndoHelperEditCommand.CanExecute(null));

            workspace.ProposeExpressionCommand.Execute(null);

            Assert.StartsWith("Expression transfer rejected:", workspace.BuildStatus, StringComparison.Ordinal);
            Assert.Same(before, CurrentPackage(workspace));
            Assert.Empty(CurrentSurface(workspace).MorphTargets);
            Assert.Empty(CurrentPackage(workspace).Document.MorphAuthoringRecords);
            Assert.False(workspace.UndoHelperEditCommand.CanExecute(null));
            Assert.NotEqual("Expression preview", workspace.ExpressionPreviewSubject);
        }
        finally { RpackTestData.DeleteTemporaryDirectory(directory); }
    }

    [Fact]
    [Trait("ValidationTier", "Hermetic")]
    [Trait("Gate", "ViewModelWpf")]
    public async Task ManualSculptCommandRequiresReviewAndExactTopologyThenHonorsConflictChoices()
    {
        string directory = RpackTestData.CreateTemporaryDirectory();
        try
        {
            byte[] neutralFbx = CreateGenericManualSculptFbx(0);
            string neutralFbxPath = Path.Combine(directory, "neutral.fbx");
            string firstSculptPath = Path.Combine(directory, "sculpt-a.fbx");
            string replacementPath = Path.Combine(directory, "sculpt-b.fbx");
            await File.WriteAllBytesAsync(neutralFbxPath, neutralFbx);
            await File.WriteAllBytesAsync(firstSculptPath, CreateGenericManualSculptFbx(0.25));
            await File.WriteAllBytesAsync(replacementPath, CreateGenericManualSculptFbx(0.5));
            string targetPath = Path.Combine(directory, "target.dlrmodel");
            string referencePath = Path.Combine(directory, "reference.dlrmodel");
            CustomModelPackageSerializer.SaveAtomic(CreateGenericManualTargetPackage(neutralFbx), targetPath);
            CustomModelPackageSerializer.SaveAtomic(CreateGenericReferencePackage(), referencePath);
            var dialogs = new MorphDialogs(targetPath, referencePath, firstSculptPath, firstSculptPath,
                firstSculptPath, firstSculptPath, replacementPath, neutralFbxPath);
            using var workspace = CreateWorkspace(dialogs);
            await workspace.OpenPackagePathAsync(targetPath);
            await workspace.LoadMorphReferenceCommand.ExecuteAsync(null);
            Vector3D[] neutral = CurrentSurface(workspace).Vertices.Select(v => v.Position).ToArray();
            byte[] source = CurrentPackage(workspace).SourceFbx.ToArray();

            await workspace.ImportManualShapeCommand.ExecuteAsync(null);
            Assert.Contains("Review the sculpt", workspace.BuildStatus, StringComparison.Ordinal);
            Assert.Empty(CurrentSurface(workspace).MorphTargets);

            workspace.ExpressionReviewed = true;
            await workspace.ImportManualShapeCommand.ExecuteAsync(null);
            FbxModelMorphTarget first = Assert.Single(CurrentSurface(workspace).MorphTargets);
            Assert.Equal(Expression, first.Name);
            Assert.Equal(Descriptor, first.DescriptorHash);
            Assert.InRange(first.PositionDeltas[0].X, 0.002499, 0.002501);
            Assert.All(first.PositionDeltas.Skip(1), delta => Assert.Equal(Vector3D.Zero, delta));
            Assert.Equal(neutral, CurrentSurface(workspace).Vertices.Select(v => v.Position).ToArray());
            Assert.Equal(source, CurrentPackage(workspace).SourceFbx.ToArray());
            MorphAuthoringRecord originalReceipt = Assert.Single(CurrentPackage(workspace).Document.MorphAuthoringRecords);
            Assert.Equal(MorphAuthoringMethod.ManualSculpt, originalReceipt.Method);
            Assert.Equal(5, originalReceipt.OriginalSourceChannelIndex);
            Assert.Equal(0, originalReceipt.TargetChannelSlot);

            workspace.ExpressionReviewed = true;
            workspace.ExpressionConflict = MorphTransferConflict.Reject;
            await workspace.ImportManualShapeCommand.ExecuteAsync(null);
            Assert.Contains("Choose keep or replace", workspace.BuildStatus, StringComparison.Ordinal);
            Assert.Equal(first.PositionDeltas.ToArray(), Assert.Single(CurrentSurface(workspace).MorphTargets).PositionDeltas.ToArray());

            workspace.ExpressionReviewed = true;
            workspace.ExpressionConflict = MorphTransferConflict.KeepExisting;
            CustomModelPackage beforeKeep = CurrentPackage(workspace);
            await workspace.ImportManualShapeCommand.ExecuteAsync(null);
            Assert.Equal(first.PositionDeltas.ToArray(), Assert.Single(CurrentSurface(workspace).MorphTargets).PositionDeltas.ToArray());
            Assert.Same(beforeKeep, CurrentPackage(workspace));
            Assert.Same(originalReceipt, Assert.Single(CurrentPackage(workspace).Document.MorphAuthoringRecords));
            Assert.Equal(MorphTransferConflict.Reject,
                Assert.Single(CurrentPackage(workspace).Document.MorphAuthoringRecords).ConflictChoice);

            workspace.ExpressionReviewed = true;
            workspace.ExpressionConflict = MorphTransferConflict.ReplaceExisting;
            await workspace.ImportManualShapeCommand.ExecuteAsync(null);
            Vector3D[] replaced = Assert.Single(CurrentSurface(workspace).MorphTargets).PositionDeltas.ToArray();
            Assert.False(first.PositionDeltas.SequenceEqual(replaced));
            Assert.InRange(replaced[0].X, 0.004999, 0.005001);
            Assert.All(replaced.Skip(1), delta => Assert.Equal(Vector3D.Zero, delta));
            Assert.Equal(MorphTransferConflict.ReplaceExisting,
                Assert.Single(CurrentPackage(workspace).Document.MorphAuthoringRecords).ConflictChoice);
            Assert.Equal(neutral, CurrentSurface(workspace).Vertices.Select(v => v.Position).ToArray());

            workspace.ExpressionName = ZeroExpression;
            workspace.ExpressionReviewed = true;
            workspace.ExpressionConflict = MorphTransferConflict.Reject;
            await workspace.ImportManualShapeCommand.ExecuteAsync(null);
            Assert.Contains("zero-delta placeholder", workspace.BuildStatus, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(CurrentSurface(workspace).MorphTargets, morph => morph.Name == ZeroExpression);
            Assert.Equal(source, CurrentPackage(workspace).SourceFbx.ToArray());

            workspace.UndoHelperEditCommand.Execute(null); // replace -> first accepted shape
            Assert.Equal(first.PositionDeltas.ToArray(), Assert.Single(CurrentSurface(workspace).MorphTargets).PositionDeltas.ToArray());
            workspace.UndoHelperEditCommand.Execute(null); // first acceptance -> neutral
            Assert.Empty(CurrentSurface(workspace).MorphTargets);
        }
        finally { RpackTestData.DeleteTemporaryDirectory(directory); }
    }

    [Fact]
    [Trait("ValidationTier", "Hermetic")]
    public void ManualSculptRejectsReorderedTrianglesConflictingCornersAndMissingControlPoints()
    {
        byte[] neutralFbx = CreateGenericManualSculptFbx(0);
        FbxModelSurface target = Assert.Single(FbxModelAuthoringImporter.ImportPackage(
            CreateGenericManualTargetPackage(neutralFbx)).Surfaces);
        FbxModelSurface sculpt = Assert.Single(FbxModelAuthoringImporter.Import(
            CreateGenericManualSculptFbx(0.25), "sculpt.fbx",
            new() { RigMode = CustomModelRigMode.Auto, DecodeAnimationClips = false }).Surfaces);
        ImmutableArray<Vector3D> valid = ManualMorphShapeAuthoring.ComputePositionDeltas(target, sculpt);
        Assert.Equal(4, valid.Length);
        Assert.InRange(valid[0].X, 0.002499, 0.002501);

        FbxModelSurface reordered = sculpt with { Indices = sculpt.Indices.SetItem(0, sculpt.Indices[1]) };
        Assert.Throws<InvalidDataException>(() => ManualMorphShapeAuthoring.ComputePositionDeltas(target, reordered));

        int duplicateCorner = sculpt.SourceCorners.Select((corner, index) => (corner.ControlPointIndex, Index: index))
            .GroupBy(item => item.ControlPointIndex).First(group => group.Count() > 1).Skip(1).First().Index;
        FbxModelSurface conflicting = sculpt with { Vertices = sculpt.Vertices.SetItem(duplicateCorner,
            sculpt.Vertices[duplicateCorner] with
            { Position = sculpt.Vertices[duplicateCorner].Position + new Vector3D(0.1, 0, 0) }) };
        Assert.Throws<InvalidDataException>(() => ManualMorphShapeAuthoring.ComputePositionDeltas(target, conflicting));

        FbxModelSurface missingPoint = sculpt with { SourceGeometry = sculpt.SourceGeometry! with
            { ControlPoints = sculpt.SourceGeometry!.ControlPoints.RemoveAt(3) } };
        Assert.Throws<InvalidDataException>(() => ManualMorphShapeAuthoring.ComputePositionDeltas(target, missingPoint));
    }

    [Fact]
    [Trait("ValidationTier", "Hermetic")]
    [Trait("Gate", "ViewModelWpf")]
    public async Task ReviewedDifferentTopologyExpressionRetainsNeutralGeometryAndRoundTripsHistory()
    {
        string directory = RpackTestData.CreateTemporaryDirectory();
        try
        {
            (string targetPath, string referencePath) = SaveGenericPackages(directory);
            var dialogs = new MorphDialogs(targetPath, referencePath);
            using var workspace = CreateWorkspace(dialogs);
            await workspace.OpenPackagePathAsync(targetPath);
            CustomModelPackage neutral = CurrentPackage(workspace);
            Vector3D[] neutralPositions = CurrentSurface(workspace).Vertices.Select(v => v.Position).ToArray();
            await workspace.LoadMorphReferenceCommand.ExecuteAsync(null);
            Assert.Equal(Expression, workspace.ExpressionName);
            Assert.Equal("surface/0", workspace.SelectedReferenceSurface);
            Assert.Equal("surface/0", workspace.SelectedCharacterSurface);
            workspace.PreviewReferenceExpressionCommand.Execute(null);
            Assert.Equal("Original expression", workspace.ExpressionPreviewSubject);
            var referenceFrame = workspace.Viewport.SceneSource.CaptureFrame();
            Assert.Null(referenceFrame.Skeleton);
            Assert.InRange(referenceFrame.Camera.Target.X, -1, 2);
            MeshRenderData referenceMesh = Assert.Single(referenceFrame.Meshes);
            Assert.True(RenderMeshValidation.TryValidate(referenceMesh, null, out string? referenceError), referenceError);
            workspace.ExpressionPreviewWeight = 0.4;
            Assert.Equal(0.4f, Assert.Single(workspace.Viewport.SceneSource.CaptureFrame().MorphWeights).Weight);
            Assert.Equal(referenceFrame.Camera, workspace.Viewport.SceneSource.CaptureFrame().Camera);
            workspace.ExpressionPreviewWeight = 1;
            Assert.InRange(VisibleFirstX(workspace), -0.001f, 0.001f);
            Assert.Equal(Expression, Assert.Single(workspace.Viewport.SceneSource.CaptureFrame().MorphWeights).Name);

            SetReviewedCorrespondence(workspace);
            workspace.ProposeExpressionCommand.Execute(null);
            Assert.Contains("Expression preview ready", workspace.BuildStatus, StringComparison.Ordinal);
            Assert.Equal("Expression preview", workspace.ExpressionPreviewSubject);
            Assert.InRange(workspace.Viewport.SceneSource.CaptureFrame().Camera.Target.X, 9, 12);
            Assert.InRange(VisibleFirstX(workspace), 9.999f, 10.001f);
            Assert.False(workspace.ExpressionReviewed);
            workspace.AcceptExpressionCommand.Execute(null);
            Assert.Contains("Mark this expression reviewed", workspace.BuildStatus, StringComparison.Ordinal);
            Assert.Empty(CurrentSurface(workspace).MorphTargets);

            workspace.ExpressionReviewed = true;
            workspace.AcceptExpressionCommand.Execute(null);
            Assert.Contains("expression accepted", workspace.BuildStatus, StringComparison.OrdinalIgnoreCase);
            Assert.Equal("Neutral", workspace.ExpressionPreviewSubject);
            FbxModelMorphTarget accepted = Assert.Single(CurrentSurface(workspace).MorphTargets);
            Assert.Equal(Expression, accepted.Name);
            Assert.Equal(Descriptor, accepted.DescriptorHash);
            Assert.Equal(Vector3D.Zero, accepted.PositionDeltas[2]); // reviewed lock
            Assert.Equal(Vector3D.Zero, accepted.PositionDeltas[3]); // outside face region
            Assert.True(accepted.PositionDeltas[1].Length > 0.01);
            Assert.Equal(neutralPositions, CurrentSurface(workspace).Vertices.Select(v => v.Position).ToArray());
            Assert.InRange(VisibleFirstX(workspace), 9.999f, 10.001f);
            workspace.PreviewExpressionCommand.Execute(null);
            Assert.Equal("Target expression", workspace.ExpressionPreviewSubject);
            Assert.Equal(Expression, Assert.Single(workspace.Viewport.SceneSource.CaptureFrame().MorphWeights).Name);
            workspace.PreviewTargetNeutralCommand.Execute(null);
            Assert.Equal("Neutral", workspace.ExpressionPreviewSubject);
            Assert.Empty(workspace.Viewport.SceneSource.CaptureFrame().MorphWeights);
            Assert.InRange(VisibleFirstX(workspace), 9.999f, 10.001f);
            workspace.PreviewReferenceExpressionCommand.Execute(null);
            Assert.InRange(VisibleFirstX(workspace), -0.001f, 0.001f);
            workspace.PreviewExpressionCommand.Execute(null);
            Assert.Equal("Target expression", workspace.ExpressionPreviewSubject);
            Assert.InRange(VisibleFirstX(workspace), 9.999f, 10.001f);
            Assert.Equal(neutral.SourceFbx.ToArray(), CurrentPackage(workspace).SourceFbx.ToArray());
            Assert.Equal(neutral.Document.Bones.Select(b => (b.Name, b.ExactLocalBindMatrix)),
                CurrentPackage(workspace).Document.Bones.Select(b => (b.Name, b.ExactLocalBindMatrix)));
            MorphAuthoringRecord origin = Assert.Single(CurrentPackage(workspace).Document.MorphAuthoringRecords);
            Assert.Equal(MorphAuthoringMethod.AssistedTransfer, origin.Method);
            Assert.Equal(5, origin.OriginalSourceChannelIndex);
            Assert.Equal(0, origin.TargetChannelSlot);
            Assert.Equal("reviewed face", origin.ReviewedRegionName);
            Assert.Equal(ReviewedRegion, origin.ReviewedTargetRegion.ToArray());
            Assert.Equal(LockedRegion, origin.LockedTargetVertices.ToArray());
            Assert.Equal(3, origin.ReviewedLandmarks.Length);
            Assert.Equal(2, origin.ReviewedTriangles.Length);
            Assert.True(origin.Accepted);

            Assert.True(workspace.UndoHelperEditCommand.CanExecute(null));
            workspace.UndoHelperEditCommand.Execute(null);
            Assert.Empty(CurrentSurface(workspace).MorphTargets);
            Assert.Empty(CurrentPackage(workspace).Document.MorphAuthoringRecords);
            Assert.True(workspace.RedoHelperEditCommand.CanExecute(null));
            workspace.RedoHelperEditCommand.Execute(null);
            Assert.Equal(accepted.PositionDeltas.ToArray(), Assert.Single(CurrentSurface(workspace).MorphTargets).PositionDeltas.ToArray());
            Assert.Single(CurrentPackage(workspace).Document.MorphAuthoringRecords);

            workspace.SavePackageCommand.Execute(null);
            Assert.DoesNotContain("Save failed", workspace.BuildStatus, StringComparison.Ordinal);
            using var reopened = CreateWorkspace(dialogs);
            await reopened.OpenPackagePathAsync(targetPath);
            Assert.Equal(accepted.PositionDeltas.ToArray(), Assert.Single(CurrentSurface(reopened).MorphTargets).PositionDeltas.ToArray());
            Assert.Single(CurrentPackage(reopened).Document.MorphAuthoringRecords);
            Assert.Equal(neutralPositions, CurrentSurface(reopened).Vertices.Select(v => v.Position).ToArray());
        }
        finally { RpackTestData.DeleteTemporaryDirectory(directory); }
    }

    [Fact]
    [Trait("ValidationTier", "Hermetic")]
    [Trait("Gate", "ViewModelWpf")]
    public async Task SelectionChangeInvalidatesProposalAndRejectOrKeepCannotRecreateZeroShape()
    {
        string directory = RpackTestData.CreateTemporaryDirectory();
        try
        {
            (string targetPath, string referencePath) = SaveGenericPackages(directory);
            using var workspace = CreateWorkspace(new MorphDialogs(targetPath, referencePath));
            await workspace.OpenPackagePathAsync(targetPath);
            await workspace.LoadMorphReferenceCommand.ExecuteAsync(null);
            SetReviewedCorrespondence(workspace);
            workspace.ProposeExpressionCommand.Execute(null);
            workspace.ExpressionReviewed = true;
            string? selectedSource = workspace.SelectedReferenceSurface;
            workspace.SelectedReferenceSurface = null; // selection invalidates the previewed proposal
            Assert.False(workspace.ExpressionReviewed);
            workspace.SelectedReferenceSurface = selectedSource;
            workspace.ExpressionReviewed = true;
            workspace.AcceptExpressionCommand.Execute(null);
            Assert.Empty(CurrentSurface(workspace).MorphTargets);
            SetReviewedCorrespondence(workspace);
            workspace.ProposeExpressionCommand.Execute(null);
            workspace.ExpressionReviewed = true;
            workspace.ExpressionLockedVertices = "1 2"; // changes a reviewed proposal
            Assert.False(workspace.ExpressionReviewed);
            workspace.ExpressionReviewed = true;
            workspace.AcceptExpressionCommand.Execute(null);
            Assert.Empty(CurrentSurface(workspace).MorphTargets);

            workspace.ExpressionLockedVertices = "2";
            workspace.ExpressionCorrespondenceReviewed = true;
            workspace.ProposeExpressionCommand.Execute(null);
            workspace.ExpressionReviewed = true;
            workspace.AcceptExpressionCommand.Execute(null);
            Vector3D[] first = Assert.Single(CurrentSurface(workspace).MorphTargets).PositionDeltas.ToArray();
            Assert.Contains(first, v => v.Length > 0.01);
            SetReviewedCorrespondence(workspace);
            workspace.ProposeExpressionCommand.Execute(null);
            workspace.ExpressionReviewed = true;
            workspace.ExpressionConflict = MorphTransferConflict.Reject;
            workspace.AcceptExpressionCommand.Execute(null);
            Assert.Contains("Choose keep or replace", workspace.BuildStatus, StringComparison.Ordinal);
            Assert.Equal(first, Assert.Single(CurrentSurface(workspace).MorphTargets).PositionDeltas.ToArray());

            workspace.ExpressionConflict = MorphTransferConflict.KeepExisting;
            workspace.AcceptExpressionCommand.Execute(null);
            Assert.Equal(first, Assert.Single(CurrentSurface(workspace).MorphTargets).PositionDeltas.ToArray());

            workspace.ExpressionTargetRegion = "0 1 2 3";
            workspace.ExpressionCorrespondenceReviewed = true;
            workspace.ProposeExpressionCommand.Execute(null);
            workspace.ExpressionReviewed = true;
            workspace.ExpressionConflict = MorphTransferConflict.ReplaceExisting;
            workspace.AcceptExpressionCommand.Execute(null);
            Vector3D[] replaced = Assert.Single(CurrentSurface(workspace).MorphTargets).PositionDeltas.ToArray();
            Assert.False(first.SequenceEqual(replaced));
            Assert.True(replaced[3].Length > 0.01);
            Assert.Equal(MorphTransferConflict.ReplaceExisting,
                Assert.Single(CurrentPackage(workspace).Document.MorphAuthoringRecords).ConflictChoice);

            workspace.ExpressionName = ZeroExpression;
            SetReviewedCorrespondence(workspace);
            workspace.ProposeExpressionCommand.Execute(null);
            Assert.Contains("zero-delta placeholder", workspace.BuildStatus, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(CurrentSurface(workspace).MorphTargets, target => target.Name == ZeroExpression);
        }
        finally { RpackTestData.DeleteTemporaryDirectory(directory); }
    }

    private static void SetReviewedCorrespondence(ModelsWorkspaceViewModel workspace)
    {
        workspace.ExpressionRegionName = "reviewed face";
        workspace.ExpressionTargetRegion = "0 1 2";
        workspace.ExpressionLandmarks = "0 0 1\n1 1 1\n2 2 1";
        workspace.ExpressionCorrespondences = "0 0\n0 1";
        workspace.ExpressionLockedVertices = "2";
        workspace.ExpressionCorrespondenceReviewed = true;
    }

    private static (string Target, string Reference) SaveGenericPackages(string directory)
    {
        string target = Path.Combine(directory, "target.dlrmodel");
        string reference = Path.Combine(directory, "reference.dlrmodel");
        CustomModelPackageSerializer.SaveAtomic(CreateGenericDifferentTopologyTargetPackage(), target);
        CustomModelPackageSerializer.SaveAtomic(CreateGenericReferencePackage(), reference);
        return (target, reference);
    }

    private static ModelsWorkspaceViewModel CreateWorkspace(MorphDialogs dialogs) =>
        new(dialogs, static _ => { }, static _ => Task.CompletedTask, static () => null);

    private static CustomModelPackage CurrentPackage(ModelsWorkspaceViewModel workspace) =>
        workspace.CaptureProjectSession().Model!.Package;

    private static FbxModelSurface CurrentSurface(ModelsWorkspaceViewModel workspace) =>
        Assert.Single(workspace.CaptureProjectSession().Model!.Surfaces);

    private static float VisibleFirstX(ModelsWorkspaceViewModel workspace) =>
        Assert.Single(workspace.Viewport.SceneSource.CaptureFrame().Meshes).Vertices.Span[0].Position.X;

    private static void AssertValidStaticFaceMesh(ModelsWorkspaceViewModel workspace)
    {
        MeshRenderData mesh = Assert.Single(workspace.Viewport.SceneSource.CaptureFrame().Meshes);
        Assert.Equal("surface/0", mesh.Id);
        Assert.False(mesh.IsSkinned);
        Assert.True(mesh.InverseBindMatrices.IsEmpty);
        Assert.True(mesh.SkinBoneIndices.IsEmpty);
        Assert.True(RenderMeshValidation.TryValidate(mesh, null, out string? error), error);
    }

    internal static CustomModelPackage CreateGenericReferencePackage() => CreatePackage(reference: true);

    internal static CustomModelPackage CreateGenericDifferentTopologyTargetPackage() => CreatePackage(reference: false);

    /// <summary>Generated binary FBX with one quad; displacement changes only its first control point.</summary>
    internal static byte[] CreateGenericManualSculptFbx(double firstControlPointShift)
    {
        byte[] neutral = BlenderFbxStrictValidationTests.CreateSourceProvenanceFixture(quad: true, splitMaterials: false);
        if (firstControlPointShift == 0) return neutral;
        FbxBinaryDocument document = FbxBinaryReader.Read(neutral);
        FbxNode objects = document.Nodes.Single(node => node.Name == "Objects");
        FbxNode geometry = objects.Children.Single(node => node.Name == "Geometry" &&
            node.FindChild("PolygonVertexIndex")?.Properties[0].Value is ImmutableArray<long> polygonIndexes &&
            !polygonIndexes.IsEmpty);
        FbxNode positions = geometry.FindChild("Vertices")!;
        ImmutableArray<double> coordinates = positions.Properties[0].Get<ImmutableArray<double>>();
        FbxNode changedPositions = positions with { Properties = positions.Properties.SetItem(0,
            new('d', coordinates.SetItem(0, coordinates[0] + firstControlPointShift))) };
        FbxNode changedGeometry = geometry with { Children = geometry.Children.Replace(positions, changedPositions) };
        FbxNode changedObjects = objects with { Children = objects.Children.Replace(geometry, changedGeometry) };
        return BlenderFbxStrictValidationTests.Serialize(document with
        {
            Nodes = document.Nodes.Replace(objects, changedObjects),
        });
    }

    internal static CustomModelPackage CreateGenericManualTargetPackage(byte[] neutralSculptFbx)
    {
        FbxModelAuthoringImportResult sculpt = FbxModelAuthoringImporter.Import(neutralSculptFbx, "neutral.fbx",
            new() { RigMode = CustomModelRigMode.Auto, DecodeAnimationClips = false });
        FbxModelSurface sculptSurface = Assert.Single(sculpt.Surfaces);
        if (sculptSurface.SourceGeometry?.ControlPoints.Length != 4 || sculptSurface.Indices.Length != 6 ||
            sculptSurface.SourceCorners.Length != sculptSurface.Vertices.Length)
            throw new InvalidDataException("The generated quad control did not retain four source points and two triangles.");
        var points = new Vector3D[4];
        var seen = new bool[4];
        for (int i = 0; i < sculptSurface.Vertices.Length; i++)
        {
            int point = sculptSurface.SourceCorners[i].ControlPointIndex;
            if ((uint)point >= 4u) throw new InvalidDataException("A sculpt corner references an invalid source point.");
            Vector3D position = sculptSurface.Vertices[i].Position;
            if (seen[point] && (points[point] - position).Length > 1e-9)
                throw new InvalidDataException("Expanded corners of one sculpt point disagree on its position.");
            points[point] = position;
            seen[point] = true;
        }
        if (seen.Any(value => !value)) throw new InvalidDataException("A sculpt source point has no rendered corner.");
        ImmutableArray<uint> pointTriangles = sculptSurface.Indices.Select(index =>
        {
            if (index >= (uint)sculptSurface.SourceCorners.Length)
                throw new InvalidDataException("A sculpt triangle references an invalid corner.");
            return checked((uint)sculptSurface.SourceCorners[(int)index].ControlPointIndex);
        }).ToImmutableArray();
        for (int i = 0; i < pointTriangles.Length; i += 3)
            if (pointTriangles[i] == pointTriangles[i + 1] || pointTriangles[i] == pointTriangles[i + 2] ||
                pointTriangles[i + 1] == pointTriangles[i + 2])
                throw new InvalidDataException("A sculpt triangle collapses after source-point mapping.");
        CustomModelPackage original = CreateGenericDifferentTopologyTargetPackage();
        FbxModelAuthoringImportResult loaded = FbxModelAuthoringImporter.ImportPackage(original);
        FbxModelSurface target = Assert.Single(loaded.Surfaces);
        ImmutableArray<Vector3D> positions = points.ToImmutableArray();
        FbxModelSurface matched = target with
        {
            Vertices = target.Vertices.Select((v, index) => v with { Position = positions[index] }).ToImmutableArray(),
            Indices = pointTriangles,
            SourceGeometry = new GeometrySourceComponent("surface", positions),
            SourceCorners = Enumerable.Range(0, positions.Length).Select(i => new GeometrySourceCorner(i, i)).ToImmutableArray(),
            SourceTriangles = Enumerable.Range(0, pointTriangles.Length / 3)
                .Select(i => new GeometrySourceTriangle(i, 0)).ToImmutableArray(),
        };
        CustomModelDocument document = original.Document with
        {
            Meshes = original.Document.Meshes.Select(m => m with
            {
                ControlPointCount = positions.Length, ExpandedVertexCount = positions.Length,
                PolygonCount = pointTriangles.Length / 3, TriangleCount = pointTriangles.Length / 3,
            }).ToImmutableArray(),
            MorphSignature = FbxModelAuthoringImporter.ComputeMorphSignature(original.Document.MorphChannels, [matched]),
        };
        ImmutableArray<byte> decoded = DecodedCharacterSnapshotCodec.Encode(new(document, [matched], []));
        CharacterResourceInventory inventory = document.CharacterResources! with
        {
            DecodedSha256 = Sha(decoded.AsSpan()), DecodedByteLength = decoded.Length,
        };
        document = document with { CharacterResources = inventory };
        CustomModelPackage package = original with { Document = document, DecodedCharacterPayload = decoded };
        document.Validate();
        return package;
    }

    private static CustomModelPackage CreatePackage(bool reference)
    {
        FbxModelAuthoringImportResult baseline = CustomModelPreviewSessionTests.CreateModel(flipTextureCoordinateV: false);
        FbxModelSurface baseSurface = baseline.Surfaces.Single();
        ImmutableArray<Vector3D> positions = reference
            ? [new(0, 0, 0), new(1, 0, 0), new(0, 1, 0)]
            : [new(10, 0, 0), new(12, 0, 0), new(10, 2, 0), new(12, 2, 0)];
        ImmutableArray<FbxModelVertex> vertices = positions.Select((p, i) =>
            baseSurface.Vertices[Math.Min(i, baseSurface.Vertices.Length - 1)] with { Position = p }).ToImmutableArray();
        ImmutableArray<uint> indices = reference ? [0, 1, 2] : [0, 1, 2, 1, 3, 2];
        ImmutableArray<FbxModelMorphTarget> targets = reference
            ? [new(Expression, Descriptor, 100, 101, [Vector3D.Zero, new(0.2, 0, 0), new(0, 0.2, 0)]),
               new(ZeroExpression, ZeroDescriptor, 102, 103, [Vector3D.Zero, Vector3D.Zero, Vector3D.Zero])]
            : [];
        FbxModelSurface surface = baseSurface with
        {
            Vertices = vertices, Indices = indices, MorphTargets = targets,
            SourceGeometry = new GeometrySourceComponent("surface", positions),
            SourceCorners = Enumerable.Range(0, vertices.Length).Select(i => new GeometrySourceCorner(i, i)).ToImmutableArray(),
            SourceTriangles = Enumerable.Range(0, indices.Length / 3).Select(i => new GeometrySourceTriangle(i, 0)).ToImmutableArray(),
        };
        byte[] source = reference ? [0x46, 0x42, 0x58, 0x01] : [0x46, 0x42, 0x58, 0x02];
        ImmutableArray<CustomModelMorphChannel> channels = reference
            ? [new() { Index = 0, Name = Expression, DescriptorHash = Descriptor, BlendShapeChannelObjectId = 100,
                ShapeObjectId = 101, GeometryObjectIds = [100] },
               new() { Index = 1, Name = ZeroExpression, DescriptorHash = ZeroDescriptor, BlendShapeChannelObjectId = 102,
                ShapeObjectId = 103, GeometryObjectIds = [102] }]
            : [];
        CharacterResourceRecord root = new()
        {
            Id = "root", LogicalName = reference ? "characters/reference.msh" : "characters/target.msh",
            ProviderIdentity = "synthetic-provider", SourceFingerprint = new string('a', 64),
            ContentSha256 = Sha(source), EntryPath = "character/resources/root.bin", ByteLength = source.Length,
            Subsystem = CharacterSubsystem.Geometry, Status = CharacterDependencyStatus.Preserved,
        };
        CharacterResourceInventory inventory = new()
        {
            RootResourceId = "root", DecodedSha256 = new string('0', 64), DecodedByteLength = 1,
            Resources = [root],
            Subsystems = Enum.GetValues<CharacterSubsystem>().Select(s =>
                new CharacterSubsystemReview(s, CharacterDependencyStatus.Missing, "Source review pending.")).ToImmutableArray(),
            MorphBindings = reference
                ? [new(5, 0, Expression, Descriptor, surface.Id, 0, 0, 3, "VertexDeltasDecoded"),
                   new(6, 1, ZeroExpression, ZeroDescriptor, surface.Id, 0, 0, 3, "VertexDeltasDecoded")]
                : [],
        };
        CustomModelDocument document = baseline.Package.Document with
        {
            Source = baseline.Package.Document.Source with
            {
                Kind = CustomModelSourceKind.StockCharacter,
                OriginalFileName = reference ? "characters/reference.skn" : "characters/target.skn",
                ContentSha256 = Sha(source), EmbeddedEntryPath = "source/character.bin",
            },
            Meshes = baseline.Package.Document.Meshes.Select(m => m with
            {
                ControlPointCount = vertices.Length, ExpandedVertexCount = vertices.Length,
                PolygonCount = indices.Length / 3, TriangleCount = indices.Length / 3,
            }).ToImmutableArray(),
            MorphChannels = channels, CharacterResources = inventory,
            RigSignature = CustomModelContractSignatures.ComputeRig(baseline.Package.Document.Bones),
            MorphSignature = FbxModelAuthoringImporter.ComputeMorphSignature(channels, [surface]),
        };
        ImmutableArray<byte> decoded = DecodedCharacterSnapshotCodec.Encode(new(document, [surface], []));
        inventory = inventory with { DecodedSha256 = Sha(decoded.AsSpan()), DecodedByteLength = decoded.Length };
        document = document with { CharacterResources = inventory };
        var payloads = ImmutableDictionary<string, ImmutableArray<byte>>.Empty.Add(root.EntryPath!, ImmutableArray.Create(source));
        CustomModelPackage package = baseline.Package with
        {
            Document = document, SourceFbx = ImmutableArray.Create(source),
            DecodedCharacterPayload = decoded, CompanionPayloads = payloads,
        };
        document.Validate();
        return package;
    }

    private static string Sha(byte[] payload) => Convert.ToHexStringLower(SHA256.HashData(payload));
    private static string Sha(ReadOnlySpan<byte> payload) => Convert.ToHexStringLower(SHA256.HashData(payload));

    private sealed class MorphDialogs(string targetPackage, string referencePackage, params string[] sculptFiles) : IProjectFileDialogService
    {
        private readonly Queue<string> _sculptFiles = new(sculptFiles);
        public string? NeutralExportPath { get; init; }
        public Action? BeforeFbxDialogReturn { get; init; }
        public string? ShowSaveManualMorphNeutralDialog(string suggestedName, string? initialPath) => NeutralExportPath;
        public string? ShowOpenProjectDialog(string? initialPath) => null;
        public string? ShowSaveProjectDialog(string suggestedName, string? currentPath) => null;
        public string? ShowOpenCustomModelPackageDialog(string? initialPath) => referencePackage;
        public string? ShowSaveCustomModelPackageDialog(string suggestedName, string? initialPath) => targetPackage;
        public string? ShowOpenCustomModelFbxDialog(string? initialPath)
        {
            BeforeFbxDialogReturn?.Invoke();
            return _sculptFiles.Count > 0 ? _sculptFiles.Dequeue() : null;
        }
    }
}
