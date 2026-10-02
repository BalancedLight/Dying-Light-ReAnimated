using System.Numerics;
using ReAnimated.App.ViewModels;
using ReAnimated.Renderer.D3D11;
using Xunit;

namespace ReAnimated.Tests;

public sealed class MainWindowViewModelPlaybackFramingTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void LinkedComparisonUsesOneDirectionAndRetainsEachModelsCenter(bool linked)
    {
        RenderFrameSnapshot Scene(Vector3 offset, RenderCamera camera) => RenderFrameSnapshot.Empty() with
        {
            Camera = camera,
            Meshes = [new MeshRenderData("generic", new MeshVertex[]
            {
                new(offset, Vector3.UnitY, Vector2.Zero, Vector4.One, Vector4.Zero),
                new(offset + Vector3.One, Vector3.UnitY, Vector2.Zero, Vector4.One, Vector4.Zero),
            }, Array.Empty<uint>(), Matrix4x4.Identity, Array.Empty<Matrix4x4>(), false)],
        };
        var source = Scene(Vector3.Zero, RenderCamera.Default with { Eye = new(0, 0, 5), Target = Vector3.Zero });
        var target = Scene(new(10, 0, 0), RenderCamera.Default with { Eye = new(5, 0, 0), Target = Vector3.Zero });
        Assert.True(MainWindowViewModel.TryFrameComparisonScenes(source, target, linked, out var a, out var b));
        Vector3 directionA = Vector3.Normalize(a.Eye - a.Target);
        Vector3 directionB = Vector3.Normalize(b.Eye - b.Target);
        Assert.True(linked ? Vector3.Distance(directionA, directionB) < 1e-5f
            : Vector3.Distance(directionA, directionB) > 0.5f);
        Assert.True(Vector3.Distance(a.Target, b.Target) > 5);
    }

    [Fact]
    public void PlaybackFramePolicyRefitsWhenMeshArrivesAndThenPreservesUnchangedCamera()
    {
        Guid animation = Guid.NewGuid();
        const string rig = "generic-rig";
        RenderFrameSnapshot skeletonOnly = RenderFrameSnapshot.Empty() with
        {
            Skeleton = new SkeletonRenderData(
                [new BoneRenderData("generic_root", -1, Matrix4x4.Identity, Matrix4x4.Identity, false)],
                Matrix4x4.Identity),
        };
        Assert.True(MainWindowViewModel.TryCreatePlaybackFrame(
            skeletonOnly, animation, rig, null, out RenderCamera skeletonCamera, out string skeletonKey));

        RenderFrameSnapshot mesh = skeletonOnly with
        {
            Meshes =
            [
                new MeshRenderData(
                    "generic-target",
                    new MeshVertex[]
                    {
                        new(new(0, 0, 0), Vector3.UnitY, Vector2.Zero, Vector4.One, Vector4.Zero),
                        new(new(20, 0, 0), Vector3.UnitY, Vector2.UnitX, Vector4.One, Vector4.Zero),
                    },
                    new uint[] { 0, 1 },
                    Matrix4x4.Identity,
                    Array.Empty<Matrix4x4>(),
                    false),
            ],
        };
        Assert.True(MainWindowViewModel.TryCreatePlaybackFrame(
            mesh, animation, rig, skeletonKey, out RenderCamera meshCamera, out string meshKey));
        Assert.NotEqual(skeletonCamera.Target, meshCamera.Target);
        Assert.NotEqual(skeletonKey, meshKey);

        Assert.False(MainWindowViewModel.TryCreatePlaybackFrame(
            mesh, animation, rig, meshKey, out _, out _));
    }

    [Fact]
    public void PlaybackFramePolicyIgnoresGizmoOnlyChanges()
    {
        Guid animation = Guid.NewGuid();
        const string rig = "generic-rig";
        RenderFrameSnapshot frame = RenderFrameSnapshot.Empty() with
        {
            Meshes =
            [new MeshRenderData(
                "generic-target",
                new MeshVertex[] { new(Vector3.Zero, Vector3.UnitY, Vector2.Zero, Vector4.One, Vector4.Zero) },
                Array.Empty<uint>(),
                Matrix4x4.Identity,
                Array.Empty<Matrix4x4>(),
                false)],
        };
        Assert.True(MainWindowViewModel.TryCreatePlaybackFrame(
            frame, animation, rig, null, out _, out string key));
        RenderFrameSnapshot withGizmo = frame with
        {
            Gizmos = [new GizmoRenderData(GizmoKind.Line, Vector3.Zero, Vector3.UnitY, Vector4.One, 1.0f)],
        };
        Assert.False(MainWindowViewModel.TryCreatePlaybackFrame(
            withGizmo, animation, rig, key, out _, out _));
    }
}
