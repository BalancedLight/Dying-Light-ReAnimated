using ReAnimated.App.Infrastructure;
using ReAnimated.App.ViewModels;
using ReAnimated.Codecs.Fbx;
using ReAnimated.Core.Domain;
using ReAnimated.Core.Mathematics;
using ReAnimated.Core.ModelAuthoring;
using ReAnimated.Core.Project;

namespace ReAnimated.Tests;

public sealed class TimelineEditingWorkflowTests
{
    [Fact]
    [Trait("ValidationTier", "Hermetic")]
    [Trait("Gate", "ViewModelWpf")]
    public async Task CurveEditPersistsFractionalFrameAndComponentThroughUndoRedoAndSave()
    {
        string directory = RpackTestData.CreateTemporaryDirectory();
        try
        {
            FbxModelAuthoringImportResult model = FbxModelAuthoringImporter.Import(
                RigidPropWorkflowTests.CreateDoorFixture(), "hinged-door.fbx");
            model = FbxAuthoredAnimationAuthoring.Create(model, "open", 1, new FrameRate(30, 1));
            CustomModelAnimationClip original = model.Package.Document.AnimationClips.Single(
                static clip => clip.AuthoredAnimation is not null);
            var pivot = model.Package.Document.Bones.Single(bone =>
                bone.FbxObjectId == model.Surfaces[0].RigidGeometryOwnerFbxObjectId);

            await using var assets = new Dl1AssetWorkspace(
                Path.Combine(directory, "catalog.sqlite3"), Path.Combine(directory, "cache"));
            await using var owner = new MainWindowViewModel(
                new JsonWorkspaceStateStore(Path.Combine(directory, "recovery.json")),
                new WindowsProjectFileDialogService(), assets);
            owner.Models.CommitProjectRestore(new(model, Path.Combine(directory, "door.dlrmodel"),
                new ProjectModelsWorkspaceState { PackageAssetId = Guid.NewGuid() }));
            owner.Models.SelectedAnimation = owner.Models.Animations.Single(clip => clip.Id == original.Id);
            await owner.Models.OpenSelectedAnimationInAnimateCommand.ExecuteAsync(null);
            owner.Timeline.IsPlaying = false;

            owner.SelectedBone = Flatten(owner.SkeletonRoots).Single(bone => bone.Name == pivot.Name);
            owner.Timeline.CurrentFrame = 0;
            owner.SelectedBone.RotationY = 0;
            owner.BoneEditor.ApplyCommand.Execute(null);
            owner.Timeline.CurrentFrame = 30;
            owner.SelectedBone.RotationY = 90;
            owner.BoneEditor.ApplyCommand.Execute(null);

            Guid sourceId = owner.CurrentProject.AnimationSources.Single(source =>
                source.EmbeddedCustomModelStack?.ClipId == original.Id).Id;
            Guid variantId = owner.CurrentProject.AnimationVariants.Single(variant => variant.SourceId == sourceId).Id;
            ProjectAnimationVariant variant = owner.CurrentProject.AnimationVariants.Single(item => item.Id == variantId);
            BoneEditLayer layer = Assert.Single(variant.EditLayers);
            BoneEditTrack track = Assert.Single(layer.Tracks, item => item.BoneIndex == pivot.Index);
            TransformTRS before = track.Keyframes[0].Value;
            string trackId = $"edit:{layer.Id:N}:{pivot.Index}";

            owner.Timeline.SelectKey(trackId, track.Keyframes[0].Frame, "Translation X");
            owner.Timeline.KeyFrameEdit = 7.5;
            owner.Timeline.KeyValueEdit = 0.375;
            Assert.True(owner.Timeline.ApplyKeyEditCommand.CanExecute(null));
            owner.Timeline.ApplyKeyEditCommand.Execute(null);

            ProjectAnimationVariant editedVariant = owner.CurrentProject.AnimationVariants.Single(item => item.Id == variantId);
            BoneEditTrack editedTrack = Assert.Single(Assert.Single(editedVariant.EditLayers).Tracks);
            TransformKeyframe editedKey = Assert.Single(editedTrack.Keyframes, key => Math.Abs(key.Frame - 7.5) < 1e-8);
            Assert.Equal(0.375, editedKey.Value.Translation.X, 8);
            Assert.Equal(before.Translation.Y, editedKey.Value.Translation.Y, 8);
            Assert.Equal(before.Translation.Z, editedKey.Value.Translation.Z, 8);
            Assert.Equal(before.Rotation, editedKey.Value.Rotation);
            Assert.Equal(before.Scale, editedKey.Value.Scale);

            owner.UndoCommand.Execute(null);
            Assert.False(owner.Timeline.HasSelectedKey);
            Assert.False(owner.Timeline.ApplyKeyEditCommand.CanExecute(null));
            Assert.False(owner.Timeline.DeleteKeyCommand.CanExecute(null));
            ProjectAnimationVariant undoneVariant = owner.CurrentProject.AnimationVariants.Single(item => item.Id == variantId);
            Assert.Contains(Assert.Single(undoneVariant.EditLayers).Tracks.Single().Keyframes,
                key => Math.Abs(key.Frame - 0) < 1e-8);
            owner.RedoCommand.Execute(null);
            ProjectAnimationVariant redoneVariant = owner.CurrentProject.AnimationVariants.Single(item => item.Id == variantId);
            Assert.Contains(Assert.Single(redoneVariant.EditLayers).Tracks.Single().Keyframes,
                key => Math.Abs(key.Frame - 7.5) < 1e-8);
            owner.Timeline.SelectKey(trackId, 7.5, "Translation X");
            Assert.True(owner.Timeline.CanEditSelectedKey);
            Assert.Equal(0.375, owner.Timeline.KeyValueEdit);

            string projectPath = Path.Combine(directory, "timeline-edit.dlraproj");
            ProjectSerializer.SaveAtomic(owner.CurrentProject, projectPath);
            DlraProject reopened = ProjectSerializer.Load(projectPath);
            ProjectAnimationVariant reopenedVariant = reopened.AnimationVariants.Single(item => item.Id == variantId);
            TransformKeyframe reopenedKey = Assert.Single(Assert.Single(reopenedVariant.EditLayers).Tracks.Single().Keyframes,
                key => Math.Abs(key.Frame - 7.5) < 1e-8);
            Assert.Equal(0.375, reopenedKey.Value.Translation.X, 8);
        }
        finally
        {
            RpackTestData.DeleteTemporaryDirectory(directory);
        }
    }

    private static IEnumerable<SkeletonNodeViewModel> Flatten(IEnumerable<SkeletonNodeViewModel> nodes)
    {
        foreach (SkeletonNodeViewModel node in nodes)
        {
            yield return node;
            foreach (SkeletonNodeViewModel child in Flatten(node.Children))
                yield return child;
        }
    }
}
