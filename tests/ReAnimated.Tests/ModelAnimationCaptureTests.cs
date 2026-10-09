using ReAnimated.App.Infrastructure;
using ReAnimated.App.ViewModels;
using ReAnimated.Codecs.Fbx;
using ReAnimated.Codecs.Models;
using ReAnimated.Codecs.Anm2;
using ReAnimated.Core.Domain;
using ReAnimated.Core.ModelAuthoring;
using ReAnimated.Core.Project;

namespace ReAnimated.Tests;

public sealed class ModelAnimationCaptureTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("ValidationTier", "Hermetic")]
    [Trait("Gate", "CustomModelPreview")]
    public async Task CanceledOrSupersededCaptureDoesNotChangeModelClips(bool superseded)
    {
        string directory = RpackTestData.CreateTemporaryDirectory();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            var model = FbxAuthoredAnimationAuthoring.Create(FbxModelAuthoringImporter.Import(
                RigidPropWorkflowTests.CreateDoorFixture(), "hinged-door.fbx"), "open", 1, new FrameRate(30, 1));
            var original = model.Package.Document.AnimationClips.Single(static clip => clip.AuthoredAnimation is not null);
            await using var assets = new Dl1AssetWorkspace(Path.Combine(directory, "catalog.sqlite3"), Path.Combine(directory, "cache"));
            await using var owner = new MainWindowViewModel(new JsonWorkspaceStateStore(Path.Combine(directory, "recovery.json")),
                new WindowsProjectFileDialogService(), assets);
            owner.Models.CommitProjectRestore(new(model, Path.Combine(directory, "door.dlrmodel"),
                new ProjectModelsWorkspaceState { PackageAssetId = Guid.NewGuid() }));
            owner.Models.SelectedAnimation = owner.Models.Animations.Single(clip => clip.Id == original.Id);
            await owner.Models.OpenSelectedAnimationInAnimateCommand.ExecuteAsync(null);
            owner.ModelAnimationCaptureScheduler = async (work, token) =>
            {
                entered.TrySetResult();
                await release.Task.WaitAsync(token);
                return work();
            };
            Task saving = owner.SaveAnimationToModelCommand.ExecuteAsync(null);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            if (superseded) owner.Models.ModelName = "Changed model";
            else owner.Jobs.Last(job => job.Name == "Save animation to model").CancelCommand.Execute(null);
            release.TrySetResult();
            await saving.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(model.Package.Document.AnimationClips.Length, owner.Models.Animations.Count);
            Assert.True(owner.Models.Animations.Single(clip => clip.Id == original.Id).Included);
            Assert.Contains(superseded ? "changed" : "canceled", owner.StatusText, StringComparison.OrdinalIgnoreCase);
            if (superseded) Assert.Equal("Changed model", owner.Models.ModelName);
        }
        finally { release.TrySetResult(); RpackTestData.DeleteTemporaryDirectory(directory); }
    }

    [Fact]
    [Trait("ValidationTier", "Hermetic")]
    [Trait("Gate", "CustomModelPreview")]
    public async Task SharedEditorKeysCanBeSavedAsASeparateUndoableModelClip()
    {
        string directory = RpackTestData.CreateTemporaryDirectory();
        try
        {
            var model = FbxModelAuthoringImporter.Import(RigidPropWorkflowTests.CreateDoorFixture(), "hinged-door.fbx");
            model = FbxAuthoredAnimationAuthoring.Create(model, "open", 1, new FrameRate(30, 1));
            var original = model.Package.Document.AnimationClips.Single(static clip => clip.AuthoredAnimation is not null);
            var pivot = model.Package.Document.Bones.Single(bone =>
                bone.FbxObjectId == model.Surfaces[0].RigidGeometryOwnerFbxObjectId);
            await using var assets = new Dl1AssetWorkspace(Path.Combine(directory, "catalog.sqlite3"), Path.Combine(directory, "cache"));
            await using var owner = new MainWindowViewModel(new JsonWorkspaceStateStore(Path.Combine(directory, "recovery.json")),
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
            Guid originalSourceId = owner.CurrentProject.AnimationSources.Single(source =>
                source.EmbeddedCustomModelStack?.ClipId == original.Id).Id;
            var originalLayers = owner.CurrentProject.AnimationVariants.Single(variant =>
                variant.SourceId == originalSourceId).EditLayers;
            Assert.NotEmpty(originalLayers);
            Assert.True(owner.SaveAnimationToModelCommand.CanExecute(null));
            await owner.SaveAnimationToModelCommand.ExecuteAsync(null);
            var saved = owner.Models.CaptureProjectSession().Model!;
            Assert.Equal(model.Package.Document.AnimationClips.Length + 1, saved.Package.Document.AnimationClips.Length);
            var copy = owner.Models.SelectedAnimation!.Contract;
            Assert.NotEqual(original.Id, copy.Id);
            Assert.NotNull(copy.AuthoredAnimation);
            Assert.True(copy.Included);
            Assert.False(saved.Package.Document.AnimationClips.Single(clip => clip.Id == original.Id).Included);
            Assert.Equal(model.Package.AuthoredAnimationPayloads[original.Id], saved.Package.AuthoredAnimationPayloads[original.Id]);
            var sampled = saved.AnimationClips[copy.Id].SamplePose(saved.Rig!, 30, PlaybackMode.Clamp);
            Assert.True(Math.Abs(sampled.LocalTransforms[pivot.Index].Rotation.Y) > 0.6);
            string packagePath = Path.Combine(directory, "edited-door.dlrmodel");
            CustomModelPackageSerializer.SaveAtomic(saved.Package, packagePath);
            var reopened = FbxModelAuthoringImporter.ImportPackage(CustomModelPackageSerializer.Load(packagePath));
            var library = await CustomModelAnimationLibraryExporter.PrepareAsync(new()
            {
                Model = reopened, Selections = [copy], OutputPath = Path.Combine(directory, "edited-animations.rpack"),
            });
            var encoded = Anm2Reader.Read(Assert.Single(library.Animations).Payload, "edited");
            var firstFrame = Anm2SemanticDecoder.Sample(encoded, 0).Frame;
            var lastFrame = Anm2SemanticDecoder.Sample(encoded, 30).Frame;
            Assert.Contains(Enumerable.Range(0, firstFrame.Tracks.Length), index =>
                Math.Abs(firstFrame.Tracks[index].RotationX - lastFrame.Tracks[index].RotationX) > 0.2 ||
                Math.Abs(firstFrame.Tracks[index].RotationY - lastFrame.Tracks[index].RotationY) > 0.2 ||
                Math.Abs(firstFrame.Tracks[index].RotationZ - lastFrame.Tracks[index].RotationZ) > 0.2);
            var expectedKeys = saved.AnimationClips[copy.Id].TransformTracks[pivot.Index].Keyframes;
            var actualKeys = reopened.AnimationClips[copy.Id].TransformTracks[pivot.Index].Keyframes;
            Assert.Equal(expectedKeys.Length, actualKeys.Length);
            for (int index = 0; index < expectedKeys.Length; index++)
            {
                Assert.Equal(expectedKeys[index].Frame, actualKeys[index].Frame);
                Assert.True(expectedKeys[index].Value.ToMatrix().NearlyEquals(actualKeys[index].Value.ToMatrix(), 1e-9));
            }
            await owner.Models.OpenSelectedAnimationInAnimateCommand.ExecuteAsync(null);
            Guid copiedSourceId = owner.CurrentProject.AnimationSources.Single(source =>
                source.EmbeddedCustomModelStack?.ClipId == copy.Id).Id;
            Assert.Empty(owner.CurrentProject.AnimationVariants.Single(variant => variant.SourceId == copiedSourceId).EditLayers);
            Assert.Equal(originalLayers, owner.CurrentProject.AnimationVariants.Single(variant => variant.SourceId == originalSourceId).EditLayers);
            owner.Models.UndoHelperEditCommand.Execute(null);
            Assert.Equal(model.Package.Document.AnimationClips.Length, owner.Models.Animations.Count);
            Assert.True(owner.Models.Animations.Single(clip => clip.Id == original.Id).Included);
        }
        finally { RpackTestData.DeleteTemporaryDirectory(directory); }
    }

    private static IEnumerable<SkeletonNodeViewModel> Flatten(IEnumerable<SkeletonNodeViewModel> nodes)
    {
        foreach (var node in nodes)
        {
            yield return node;
            foreach (var child in Flatten(node.Children)) yield return child;
        }
    }
}
