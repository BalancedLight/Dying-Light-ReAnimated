using System.Numerics;
using ReAnimated.Renderer.D3D11;

namespace ReAnimated.Tests;

public sealed class RendererCameraFramingTests
{
    [Fact]
    public void DefaultCameraFacesDl1CharacterFrontWithoutHorizontalSkew()
    {
        RenderCamera camera = RenderCamera.Default;
        Vector3 viewOffset = camera.Eye - camera.Target;

        Assert.Equal(0.0f, viewOffset.X);
        Assert.True(
            viewOffset.Z > 0.0f,
            "Decoded retail DL1 characters face +Z; the default orbit camera must begin on their front side.");
        Assert.Equal(Vector3.UnitY, camera.Up);
    }

    [Fact]
    public void FramesTransformedStaticMeshAndPreservesLens()
    {
        RenderCamera original = RenderCamera.Default with
        {
            VerticalFieldOfViewDegrees = 52.0f,
            NearPlane = 0.04f,
        };
        MeshRenderData mesh = CreateMesh(
            Matrix4x4.CreateTranslation(10.0f, 2.0f, -3.0f),
            isSkinned: false);
        RenderFrameSnapshot frame = RenderFrameSnapshot.Empty() with
        {
            Camera = original,
            Meshes = [mesh],
        };

        Assert.True(RenderCameraFraming.TryFrame(
            frame,
            out RenderCamera framed));

        Assert.Equal(10.0f, framed.Target.X, 4);
        Assert.Equal(2.5f, framed.Target.Y, 4);
        Assert.Equal(-3.0f, framed.Target.Z, 4);
        Assert.Equal(52.0f, framed.VerticalFieldOfViewDegrees);
        Assert.Equal(0.04f, framed.NearPlane);
        Assert.True(
            Vector3.Distance(framed.Eye, framed.Target) >
            0.5f);
    }

    [Fact]
    public void FramesCurrentSkinnedPoseAndActiveMorph()
    {
        MeshRenderData mesh = CreateMesh(
            Matrix4x4.Identity,
            isSkinned: true) with
        {
            InverseBindMatrices =
                new Matrix4x4[] { Matrix4x4.Identity },
            MorphTargets =
            [
                new MorphTargetRenderData(
                    "smile",
                    new Vector3[]
                    {
                        new(0.0f, 1.0f, 0.0f),
                        new(0.0f, 1.0f, 0.0f),
                    },
                    ReadOnlyMemory<Vector3>.Empty),
            ],
        };
        SkeletonRenderData skeleton = new(
            [
                new BoneRenderData(
                    "root",
                    -1,
                    Matrix4x4.Identity,
                    Matrix4x4.CreateTranslation(
                        4.0f,
                        0.0f,
                        0.0f),
                    false),
            ],
            Matrix4x4.Identity);
        RenderFrameSnapshot frame = RenderFrameSnapshot.Empty() with
        {
            Meshes = [mesh],
            Skeleton = skeleton,
            MorphWeights = [new MorphWeight("smile", 0.5f)],
        };

        Assert.True(RenderCameraFraming.TryFrame(
            frame,
            out RenderCamera framed));

        Assert.Equal(4.0f, framed.Target.X, 4);
        Assert.Equal(0.75f, framed.Target.Y, 4);
    }

    [Fact]
    public void TallCharacterUsesViewExtentsInsteadOfDiagonalSphere()
    {
        MeshRenderData mesh = CreateMesh(
            Matrix4x4.Identity,
            isSkinned: false) with
        {
            Vertices = new MeshVertex[]
            {
                CreateVertex(new Vector3(-0.5f, 0.0f, 0.0f)),
                CreateVertex(new Vector3(0.5f, 2.0f, 0.0f)),
            },
        };
        RenderFrameSnapshot frame = RenderFrameSnapshot.Empty() with
        {
            Camera = RenderCamera.Default with
            {
                Eye = new Vector3(0.0f, 0.0f, 4.5f),
                Target = Vector3.Zero,
            },
            Meshes = [mesh],
        };

        Assert.True(RenderCameraFraming.TryFrame(
            frame,
            out RenderCamera framed));

        float distance =
            Vector3.Distance(framed.Eye, framed.Target);
        float verticalTangent = MathF.Tan(
            framed.VerticalFieldOfViewDegrees *
            (MathF.PI / 360.0f));
        float viewportHeightFraction =
            1.0f / (distance * verticalTangent);

        Assert.InRange(distance, 1.89f, 1.92f);
        Assert.InRange(viewportHeightFraction, 0.90f, 0.92f);
    }

    [Fact]
    public void ExplicitWideLensAspectFitsHorizontalExtent()
    {
        MeshRenderData mesh = CreateMesh(
            Matrix4x4.Identity,
            isSkinned: false) with
        {
            Vertices = new MeshVertex[]
            {
                CreateVertex(new Vector3(-1.0f, -0.5f, 0.0f)),
                CreateVertex(new Vector3(1.0f, 0.5f, 0.0f)),
            },
        };
        RenderFrameSnapshot squareFrame =
            RenderFrameSnapshot.Empty() with
            {
                Camera = RenderCamera.Default with
                {
                    Eye = new Vector3(0.0f, 0.0f, 4.5f),
                    Target = Vector3.Zero,
                },
                Meshes = [mesh],
            };
        RenderFrameSnapshot wideFrame = squareFrame with
        {
            Camera = squareFrame.Camera with
            {
                ProjectionAspectRatio = 16.0f / 9.0f,
            },
        };

        Assert.True(RenderCameraFraming.TryFrame(
            squareFrame,
            out RenderCamera square));
        Assert.True(RenderCameraFraming.TryFrame(
            wideFrame,
            out RenderCamera wide));

        Assert.InRange(
            Vector3.Distance(square.Eye, square.Target),
            1.89f,
            1.92f);
        Assert.InRange(
            Vector3.Distance(wide.Eye, wide.Target),
            1.06f,
            1.09f);
    }

    [Theory]
    [InlineData(BoneRenderRole.Helper)]
    [InlineData(BoneRenderRole.Prop)]
    public void HiddenSkeletonRolesOnlyAffectFramingWhenSelected(
        BoneRenderRole hiddenRole)
    {
        BoneRenderData[] visibleBones =
        [
            new BoneRenderData(
                "root",
                -1,
                Matrix4x4.Identity,
                Matrix4x4.Identity,
                false),
            new BoneRenderData(
                "deform",
                0,
                Matrix4x4.CreateTranslation(Vector3.UnitY),
                Matrix4x4.CreateTranslation(Vector3.UnitY),
                false),
        ];
        RenderFrameSnapshot baselineFrame =
            RenderFrameSnapshot.Empty() with
            {
                Skeleton = new SkeletonRenderData(
                    visibleBones,
                    Matrix4x4.Identity),
            };

        Assert.True(RenderCameraFraming.TryFrame(
            baselineFrame,
            out RenderCamera baseline));

        BoneRenderData hiddenBone = new(
            hiddenRole.ToString(),
            0,
            Matrix4x4.CreateTranslation(100.0f, 0.0f, 0.0f),
            Matrix4x4.CreateTranslation(100.0f, 0.0f, 0.0f),
            false)
        {
            Role = hiddenRole,
        };
        RenderFrameSnapshot hiddenFrame = baselineFrame with
        {
            Skeleton = new SkeletonRenderData(
                [.. visibleBones, hiddenBone],
                Matrix4x4.Identity),
        };

        Assert.True(RenderCameraFraming.TryFrame(
            hiddenFrame,
            out RenderCamera hidden));

        Assert.Equal(baseline.Target, hidden.Target);
        Assert.Equal(
            Vector3.Distance(baseline.Eye, baseline.Target),
            Vector3.Distance(hidden.Eye, hidden.Target));

        RenderFrameSnapshot selectedFrame = hiddenFrame with
        {
            Skeleton = hiddenFrame.Skeleton! with
            {
                Bones =
                [
                    .. visibleBones,
                    hiddenBone with
                    {
                        IsSelected = true,
                    },
                ],
            },
        };

        Assert.True(RenderCameraFraming.TryFrame(
            selectedFrame,
            out RenderCamera selected));

        Assert.Equal(50.0f, selected.Target.X, 4);
        Assert.True(
            Vector3.Distance(selected.Eye, selected.Target) >
            Vector3.Distance(baseline.Eye, baseline.Target));
    }

    [Fact]
    public void EmptySceneDoesNotChangeCamera()
    {
        RenderFrameSnapshot frame = RenderFrameSnapshot.Empty();

        Assert.False(RenderCameraFraming.TryFrame(
            frame,
            out RenderCamera camera));
        Assert.Equal(frame.Camera, camera);
    }

    private static MeshRenderData CreateMesh(
        Matrix4x4 localToWorld,
        bool isSkinned) =>
        new(
            "framing",
            new MeshVertex[]
            {
                new(
                    Vector3.Zero,
                    Vector3.UnitY,
                    Vector2.Zero,
                    Vector4.UnitX,
                    Vector4.Zero),
                new(
                    Vector3.UnitY,
                    Vector3.UnitY,
                    Vector2.One,
                    Vector4.UnitX,
                    Vector4.Zero),
            },
            new uint[] { 0, 1, 1 },
            localToWorld,
            ReadOnlyMemory<Matrix4x4>.Empty,
            isSkinned);

    private static MeshVertex CreateVertex(Vector3 position) =>
        new(
            position,
            Vector3.UnitY,
            Vector2.Zero,
            Vector4.UnitX,
            Vector4.Zero);
}
