using System.Numerics;
using ReAnimated.App.Infrastructure;
using ReAnimated.App.ViewModels;
using ReAnimated.Core.Domain;
using ReAnimated.Core.Mathematics;
using ReAnimated.Renderer.D3D11;

namespace ReAnimated.Tests;

public sealed class SecondaryMotionRenderAdapterTests
{
    [Fact]
    public void EquivalentRecreatedFppProfilesDoNotResetTheSimulation()
    {
        var first = PreviewProfile.FirstPersonAuthoring;
        var second = PreviewProfile.FirstPersonAuthoring;
        Assert.NotSame(first, second);
        Assert.True(MainWindowViewModel.SecondaryPreviewProfilesEquivalent(first, second));
        Assert.False(MainWindowViewModel.SecondaryPreviewProfilesEquivalent(first, PreviewProfile.ThirdPersonAuthoring));
    }
    [Fact]
    public void ExternalOrbitRetainsAuthoredBodyAndHelpersWhileReceivingOnlyLocalClothDelta()
    {
        var authored = new SkeletonRenderData([
            new("root", -1, Matrix4x4.Identity, Matrix4x4.Identity, false),
            new("fabric", 0, Matrix4x4.CreateTranslation(0, 1, 0), Matrix4x4.CreateTranslation(0, 1, 0), false),
            new("weapon", 0, Matrix4x4.CreateTranslation(1, 0, 0), Matrix4x4.CreateTranslation(1, 0, 0), false),
            new("camera", 0, Matrix4x4.CreateTranslation(0, 2, 0), Matrix4x4.CreateTranslation(0, 2, 0), false),
        ], Matrix4x4.CreateTranslation(12, 0, 0));
        // FPP projection might scale/translate the camera view. Its global body pose must not be
        // copied into the external orbit when propagating a cloth-only local correction.
        TransformMatrix fppFabric = TransformMatrix.CreateTranslation(new(0, 3, -2)) * TransformMatrix.CreateScale(new(2, 2, 2));
        TransformMatrix correction = TransformMatrix.CreateRotation(QuaternionD.FromAxisAngle(Vector3D.UnitZ, 0.2));
        TransformMatrix simulated = fppFabric * correction;
        Dictionary<string, TransformMatrix> localDelta = new(StringComparer.Ordinal) { ["fabric"] = fppFabric.InvertedAffine() * simulated };
        SkeletonRenderData orbit = SecondaryMotionRenderAdapter.ApplyBoneLocalDeltas(authored, localDelta);
        Assert.Equal(authored.RootTransform, orbit.RootTransform);
        foreach (int i in new[] { 0, 2, 3 }) Assert.Equal(authored.Bones[i], orbit.Bones[i]);
        Assert.Equal(CorePreviewAdapter.ToSystemMatrix(CorePreviewAdapter.ToCoreMatrix(authored.Bones[1].WorldTransform) * correction), orbit.Bones[1].WorldTransform);
        Assert.NotEqual(CorePreviewAdapter.ToSystemMatrix(simulated), orbit.Bones[1].WorldTransform);
        Assert.Equal(authored.Bones[1].WorldTransform.Translation, orbit.Bones[1].WorldTransform.Translation);
    }

    [Fact]
    public void EmptyDeltaIsIdentityAndNoSkeletonInputIsMutated()
    {
        var source = new SkeletonRenderData([new("root", -1, Matrix4x4.Identity, Matrix4x4.Identity, false)], Matrix4x4.Identity);
        Assert.Same(source, SecondaryMotionRenderAdapter.ApplyBoneLocalDeltas(source, new Dictionary<string, TransformMatrix>()));
        _ = SecondaryMotionRenderAdapter.ApplyBoneLocalDeltas(source, new Dictionary<string, TransformMatrix> { ["root"] = TransformMatrix.CreateTranslation(Vector3D.UnitY) });
        Assert.Equal(Matrix4x4.Identity, source.Bones[0].WorldTransform);
    }
}
