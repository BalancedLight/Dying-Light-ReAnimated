using System.Collections.Immutable;
using ReAnimated.App.Infrastructure;
using ReAnimated.App.ViewModels;
using ReAnimated.Codecs.Fbx;
using ReAnimated.Codecs.Models;
using ReAnimated.Core.Geometry;
using ReAnimated.Core.Domain;
using ReAnimated.Core.Mathematics;
using ReAnimated.Core.ModelAuthoring;
using ReAnimated.Core.Project;
using ReAnimated.Retargeting.Geometry;

namespace ReAnimated.Tests;

public sealed class FbxContactAuthoringTests
{
    // A generic in-memory decoded-surface fixture tests the adapter separately
    // from the binary FBX serializer. Source-linked save/reopen has its own suite.
    internal static (FbxModelAuthoringImportResult Model, Guid Parent) Model(Vector3D? bindShift = null)
    {
        var imported = FbxModelAuthoringImporter.Import(BlenderFbxStrictValidationTests.CreateValidModelFixture(), "shoe.fbx");
        var document = imported.Package.Document;
        document = document with { RiggingSession = RiggingSessions.Create(document, RigStudioEntryPath.RepairExistingRig) };
        var observed = RiggingSessions.ObserveSourceHierarchy(document);
        var parent = observed[document.Bones.First(b => b.Kind == BoneKind.Deform).Index].EntityId;
        int bone = Enumerable.Range(0, observed.Length).Single(i => observed[i].EntityId == parent);
        var points = ContactFootprintSolverTests.Shoe().ToImmutableArray();
        var global = document.CreateRigDefinition().CreateBindPose().GlobalMatrices[bone];
        var surface = imported.Surfaces[0] with
        {
            Vertices = points.Select(p => new FbxModelVertex(p, Vector3D.UnitY, 0, 0, [0], [1.0])).ToImmutableArray(),
            Indices = [0, 4, 5, 0, 5, 1, 2, 3, 7, 2, 7, 6, 0, 2, 6, 0, 6, 4,
                1, 5, 7, 1, 7, 3, 0, 1, 3, 0, 3, 2, 4, 6, 7, 4, 7, 5], PaletteBoneIndices = [bone],
            InverseBindMatrices = [global.InvertedAffine() * TransformMatrix.CreateTranslation(bindShift ?? Vector3D.Zero)],
            IsSkinned = true, MorphTargets = [],
            SourceGeometry = new GeometrySourceComponent("source-shoe", points),
            SourceCorners = Enumerable.Range(0, points.Length).Select(i => new GeometrySourceCorner(i, i)).ToImmutableArray(),
            SourceTriangles = Enumerable.Range(0, 12).Select(i => new GeometrySourceTriangle(i, 0)).ToImmutableArray(),
        };
        return (imported with { Package = imported.Package with { Document = document }, Surfaces = [surface] }, parent);
    }

    [Fact]
    public void UsesExpertInverseBindsAndOriginalPointIds()
    {
        var (model, parent) = Model(new(.4, .2, -.3));
        var snapshot = FbxContactAuthoring.Inspect(model, parent, "source-shoe", .5, null, new() { Origin = ContactOriginMode.FootprintCenter });
        Assert.Equal<int>(Enumerable.Range(0, 8), snapshot.ControlPointIds);
        Assert.True((snapshot.Fit.GlobalFrame.Translation - new Vector3D(.4, .2, -.3)).Length < 1e-10);
        Assert.Equal(.06, snapshot.Fit.BoundsHalfExtents.X, 10);
    }

    [Fact]
    public void UsesExactAffineBoneGlobalsWhenPreviewTrsWouldProjectTheBind()
    {
        var (model, parent) = Model(new(.4, .2, -.3));
        CustomModelDocument original = model.Package.Document;
        int boneIndex = original.Bones.First(bone => bone.Kind == BoneKind.Deform).Index;
        CustomModelBone sourceBone = original.Bones[boneIndex];
        CustomModelBone[] bones = original.Bones.ToArray();
        bones[boneIndex] = sourceBone with
        {
            // Keep the editable TRS projection unchanged while introducing
            // exact affine shear in the source bind.
            ExactLocalBindMatrix = sourceBone.ExactLocalBindMatrix with
            {
                M12 = sourceBone.ExactLocalBindMatrix.M12 + .35,
                M23 = sourceBone.ExactLocalBindMatrix.M23 + .15,
            },
        };
        CustomModelDocument document = original with
        {
            Bones = bones.ToImmutableArray(),
            RigSignature = CustomModelContractSignatures.ComputeRig(bones.ToImmutableArray()),
        };
        var exactGlobals = new TransformMatrix[document.Bones.Length];
        foreach (CustomModelBone bone in document.Bones)
            exactGlobals[bone.Index] = bone.ParentIndex < 0
                ? bone.ExactLocalBindMatrix
                : exactGlobals[bone.ParentIndex] * bone.ExactLocalBindMatrix;
        TransformMatrix previewGlobal = document.CreateRigDefinition().CreateBindPose().GlobalMatrices[boneIndex];
        Assert.False(previewGlobal.NearlyEquals(exactGlobals[boneIndex], 1e-8));
        TransformMatrix expertInverse = exactGlobals[boneIndex].InvertedAffine() *
            TransformMatrix.CreateTranslation(new(.4, .2, -.3));
        FbxModelSurface surface = model.Surfaces[0] with { InverseBindMatrices = [expertInverse] };
        var affine = model with
        {
            Package = model.Package with { Document = document },
            Rig = document.CreateRigDefinition(),
            Surfaces = [surface],
        };

        var snapshot = FbxContactAuthoring.Inspect(affine, parent, "source-shoe", .5, null,
            new() { Origin = ContactOriginMode.FootprintCenter });
        Assert.True((snapshot.Fit.GlobalFrame.Translation - new Vector3D(.4, .2, -.3)).Length < 1e-10);
    }

    [Fact]
    public void RejectsMissingComponentPointsAndConflictingSeamBinds()
    {
        var (model, parent) = Model();
        Assert.Throws<ArgumentException>(() => FbxContactAuthoring.Inspect(model, parent, "other", null, null, new()));
        Assert.Throws<ArgumentException>(() => FbxContactAuthoring.Inspect(model, parent, "source-shoe", null, new HashSet<int> { 999 }, new()));
        var other = model.Surfaces[0] with { Id = "other-draw", InverseBindMatrices = [TransformMatrix.CreateTranslation(Vector3D.UnitX)] };
        var conflicting = model with { Surfaces = model.Surfaces.Add(other) };
        Assert.Throws<InvalidDataException>(() => FbxContactAuthoring.Inspect(conflicting, parent, "source-shoe", null, null, new()));
    }

    [Fact]
    public void ApplyPreservesSurfacesAndPreparationUsesTheFittedFrameAndBounds()
    {
        var (model, parent) = Model();
        var snapshot = FbxContactAuthoring.Inspect(model, parent, "source-shoe", .5, null, new());
        var fit = snapshot.Fit;
        var changed = FbxContactAuthoring.Apply(snapshot, model, null, "shoe_contact", "contact.custom", fit.LocalFrame, fit.BoundsCenter, fit.BoundsHalfExtents, false);
        Assert.Equal(model.Surfaces, changed.Surfaces);
        Assert.Equal(model.Package.Document.Bones, changed.Package.Document.Bones);
        Assert.Equal(model.Package.SourceFbx, changed.Package.SourceFbx);
        var node = Assert.Single(Dl1CustomModelRigPreparer.Prepare(changed).Contract.Nodes, n => n.Name == "shoe_contact");
        Assert.Equal(RigFramePolicy.Contact, node.FramePolicy);
        Assert.True((node.Bounds.HalfExtents - fit.BoundsHalfExtents).Length < 1e-7);
    }

    [Fact]
    public void WorkflowNavigationCanRefreshDraftButSourceEditsCannot()
    {
        var (model, parent) = Model();
        var snapshot = FbxContactAuthoring.Inspect(model, parent, "source-shoe", null, null, new());
        var navigation = model with { Package = model.Package with { Document = model.Package.Document with
        { RiggingSession = RiggingSessions.Navigate(model.Package.Document.RiggingSession!, RigStudioStage.Animate) } } };
        Assert.NotNull(FbxContactAuthoring.RefreshMetadata(snapshot, navigation));
        Assert.Null(FbxContactAuthoring.RefreshMetadata(snapshot, model with { Surfaces = [] }));
        Assert.Throws<InvalidOperationException>(() => FbxContactAuthoring.Apply(snapshot, navigation, null, "shoe_contact", "contact.custom",
            snapshot.Fit.LocalFrame, snapshot.Fit.BoundsCenter, snapshot.Fit.BoundsHalfExtents, false));
    }

    [Fact]
    public async Task ContactWorkflowFitsPreviewsAndPublishesAnExplicitReviewedEdit()
    {
        var (model, parent) = Model();
        var wizard = new RigConformanceWizardViewModel((_, _) => Task.FromResult(Dl1RigTemplateResolution.Failed("player", "Offline test")), _ => { });
        wizard.SetModel(model);
        wizard.ContactParent = wizard.ContactParents.Single(p => p.Id == parent);
        Assert.False(wizard.CanApplyContact);
        await wizard.FitContactCommand.ExecuteAsync(null);
        Assert.True(wizard.HasContactPreview, wizard.ContactStatus);
        Assert.True(wizard.CanApplyContact, wizard.ContactStatus);
        var preview = wizard.GetContactPreview(); Assert.NotNull(preview);
        Assert.InRange(ContactOverlayBuilder.Build(preview).Length, 25, 40);
        BodyModelEventArgs? applied = null;
        wizard.ContactModelApplyRequested += (_, args) => applied = args;
        wizard.ContactOffset.X = .01;
        wizard.ApplyContactCommand.Execute(null);
        Assert.NotNull(applied);
        var recipe = Assert.Single(applied.Result.Package.Document.RiggingSession!.Recipe.Helpers);
        Assert.Equal(RigEvidenceKind.UserOverride, recipe.PlacementProvenance);
        Assert.Equal(RigFramePolicy.Contact, recipe.FramePolicy);
        Assert.True(recipe.UserApproved);
        wizard.ContactHalfExtents.Y = 0;
        Assert.False(wizard.CanApplyContact);
        wizard.ContactSourcePoints = "0,1,2";
        Assert.False(wizard.HasContactPreview);
    }

    [Fact]
    public async Task WorkspaceContactCommitHasUndoAndBacktrackingPreservesItsDraft()
    {
        var (model, parent) = Model();
        using var workspace = new ModelsWorkspaceViewModel(new NoDialogs(), _ => { }, _ => Task.CompletedTask, () => null);
        workspace.CommitProjectRestore(new(model, "contact.dlrmodel", new ProjectModelsWorkspaceState { PackageAssetId = Guid.NewGuid() }));
        workspace.IsConformTabSelected = true;
        var wizard = workspace.Conformance;
        wizard.ContactParent = wizard.ContactParents.Single(p => p.Id == parent);
        await wizard.FitContactCommand.ExecuteAsync(null);
        Assert.True(wizard.CanApplyContact, wizard.ContactStatus);
        Assert.NotEmpty(workspace.Viewport.SceneSource.CaptureFrame().Gizmos);
        wizard.ContactOffset.Z = .02;
        var draft = wizard.GetContactPreview();
        wizard.StudioStage = RigStudioStage.Animate;
        Assert.True(wizard.HasContactPreview);
        Assert.Empty(workspace.Viewport.SceneSource.CaptureFrame().Gizmos);
        wizard.StudioStage = RigStudioStage.HelpersAndHooks;
        Assert.Equal(draft, wizard.GetContactPreview());
        wizard.ApplyContactCommand.Execute(null);
        var applied = workspace.CaptureProjectSession().Model!;
        Assert.Single(applied.Package.Document.AuthoredHelpers);
        workspace.UndoHelperEditCommand.Execute(null);
        Assert.Empty(workspace.CaptureProjectSession().Model!.Package.Document.AuthoredHelpers);
        workspace.RedoHelperEditCommand.Execute(null);
        Assert.Equal(applied.Package.Document.AuthoredHelpers, workspace.CaptureProjectSession().Model!.Package.Document.AuthoredHelpers);
        Assert.Equal(model.Surfaces, workspace.CaptureProjectSession().Model!.Surfaces);
    }

    private sealed class NoDialogs : IProjectFileDialogService
    {
        public string? ShowOpenProjectDialog(string? initialPath) => null;
        public string? ShowSaveProjectDialog(string suggestedName, string? currentPath) => null;
    }
}
