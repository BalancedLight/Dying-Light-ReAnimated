using ReAnimated.App.Infrastructure;
using ReAnimated.App.ViewModels;
using ReAnimated.Codecs.Fbx;
using ReAnimated.Core.ModelAuthoring;
using ReAnimated.Core.Project;
using ReAnimated.Renderer.D3D11;

namespace ReAnimated.Tests;

public sealed class GuidedModelFeatureTests
{
    [Fact]
    [Trait("ValidationTier", "Focused")]
    [Trait("Gate", "CustomModelMorph")]
    public void MorphPreviewChangesSurfaceAndResetKeepsOriginalData()
    {
        var model = FbxModelAuthoringImporter.Import(FbxCustomModelMorphImportTests.CreateMorphFbx(["generic_smile"]),
            "generic-morph.fbx", new FbxModelAuthoringImportOptions { RigMode = CustomModelRigMode.StaticProp });
        using var workspace = CreateWorkspace();
        Restore(workspace, model);
        Assert.True(workspace.HasGuidedMorphs);
        Assert.False(workspace.CanPlayGuidedEmbeddedAnimation);
        var choice = Assert.Single(workspace.GuidedMorphs);
        Assert.Equal("generic_smile", choice.Name);
        var original = workspace.Viewport.SceneSource.CaptureFrame();
        var originalSurface = CpuMeshDeformationEvaluator.Evaluate(original.Meshes[0], original.Skeleton, original.MorphWeights);
        long revision = workspace.PersistenceRevision;
        string sourceHash = model.Package.Document.Source.ContentSha256;
        choice.Weight = .5;
        var preview = workspace.Viewport.SceneSource.CaptureFrame();
        var morphedSurface = CpuMeshDeformationEvaluator.Evaluate(preview.Meshes[0], preview.Skeleton, preview.MorphWeights);
        Assert.NotEqual(originalSurface[0].Position, morphedSurface[0].Position);
        Assert.Equal(.5f, Assert.Single(preview.MorphWeights).Weight);
        Assert.Equal(revision, workspace.PersistenceRevision);
        Assert.Equal(sourceHash, model.Package.Document.Source.ContentSha256);
        workspace.ResetGuidedMorphsCommand.Execute(null);
        var reset = workspace.Viewport.SceneSource.CaptureFrame();
        var resetSurface = CpuMeshDeformationEvaluator.Evaluate(reset.Meshes[0], reset.Skeleton, reset.MorphWeights);
        Assert.Equal(originalSurface[0].Position, resetSurface[0].Position);
    }

    [Fact]
    [Trait("ValidationTier", "Focused")]
    [Trait("Gate", "CustomModelMorph")]
    public void SwitchingModelClearsMorphChoicesAndPreviewOverrides()
    {
        var model = FbxModelAuthoringImporter.Import(FbxCustomModelMorphImportTests.CreateMorphFbx(["generic_smile"]),
            "generic-morph.fbx", new FbxModelAuthoringImportOptions { RigMode = CustomModelRigMode.StaticProp });
        using var workspace = CreateWorkspace();
        Restore(workspace, model);
        Assert.Single(workspace.GuidedMorphs).Weight = .7;
        Restore(workspace, RigConformanceWizardTests.CreateModel());
        Assert.False(workspace.HasGuidedMorphs);
        Assert.Null(workspace.GuidedSelectedMorph);
        Assert.Empty(workspace.Viewport.SceneSource.CaptureFrame().MorphWeights);
    }

    private static ModelsWorkspaceViewModel CreateWorkspace() => new(new NoDialogs(), static _ => { },
        static _ => Task.CompletedTask, static () => null);

    private static void Restore(ModelsWorkspaceViewModel workspace, FbxModelAuthoringImportResult model) =>
        workspace.CommitProjectRestore(new PreparedModelsWorkspaceRestore(model, "generic-model.dlrmodel",
            new ProjectModelsWorkspaceState { PackageAssetId = Guid.NewGuid() }));

    private sealed class NoDialogs : IProjectFileDialogService
    {
        public string? ShowOpenProjectDialog(string? initialPath) => null;
        public string? ShowSaveProjectDialog(string suggestedName, string? currentPath) => null;
    }
}
