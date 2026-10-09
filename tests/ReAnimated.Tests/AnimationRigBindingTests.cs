using ReAnimated.Codecs.Fbx;
using ReAnimated.Core.Mathematics;
using ReAnimated.Core.ModelAuthoring;

namespace ReAnimated.Tests;

public sealed class AnimationRigBindingTests
{
    [Fact]
    [Trait("ValidationTier", "Hermetic")]
    public void OriginalClipRequiresPreparationAfterRestFrameChanges()
    {
        FbxModelAuthoringImportResult source = FbxHierarchyAuthoringTests.Source();
        CustomModelAnimationClip clip = Assert.Single(source.Package.Document.AnimationClips);
        CustomModelDocument document = source.Package.Document;
        int child = document.Bones.Single(static bone => bone.Name == "Child").Index;
        Guid entity = RiggingSessions.ObserveSourceHierarchy(document)[child].EntityId;
        TransformMatrix desired = FbxRestPoseAuthoringTests.ExactGlobals(document)[child] *
            TransformMatrix.CreateTranslation(new Vector3D(0.2, 0.1, 0));
        FbxRestPosePreview preview = FbxRestPoseAuthoring.Preview(source, entity, desired,
            RigRestDescendantMode.KeepGlobal, RigRestSurfaceMode.PreserveSurface);
        Assert.True(preview.HasChanges);
        Assert.True(FbxRestPoseAuthoring.TryApply(source, preview, out FbxModelAuthoringImportResult changed));

        InvalidOperationException error = Assert.Throws<InvalidOperationException>(() =>
            FbxDerivedMotionAuthoring.ValidateExport(changed, clip));
        Assert.Contains("prepare", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(source.AnimationClips, changed.AnimationClips);
        Assert.Equal(source.Package.SourceFbx, changed.Package.SourceFbx);
    }

    [Fact]
    [Trait("ValidationTier", "Hermetic")]
    public void OriginalClipRemainsExportableWhenOnlyUnanimatedHelpersAreAdded()
    {
        FbxModelAuthoringImportResult model = FbxHierarchyAuthoringTests.Source();
        CustomModelAnimationClip clip = Assert.Single(model.Package.Document.AnimationClips);
        FbxDerivedMotionAuthoring.ValidateExport(model, clip);
    }
}
