using System.Numerics;
using ReAnimated.App.Infrastructure;
using ReAnimated.App.ViewModels;
using ReAnimated.Codecs.CompactMesh;
using ReAnimated.Core.Domain;
using ReAnimated.Core.Mathematics;
using ReAnimated.DL1.Assets.Meshes;
using ReAnimated.Renderer.D3D11;

namespace ReAnimated.Tests;

public sealed class BoneRenderRoleTests
{
    [Fact]
    public void LegacyBoneRowsDefaultToVisibleDeformRole()
    {
        BoneRenderData bone = CreateBone("legacy", -1);
        var skeleton = new SkeletonRenderData(
            [bone],
            Matrix4x4.Identity);

        Assert.Equal(BoneRenderRole.Deform, bone.Role);
        Assert.True(skeleton.ShowDeformBones);
        Assert.False(skeleton.ShowHelpers);
        Assert.True(skeleton.ShowCameraHelpers);
        Assert.False(skeleton.ShowProps);
        Assert.True(skeleton.IsVisible(bone));
    }

    [Fact]
    public void CoreAdapterMapsEveryBoneKindToStableRenderRole()
    {
        var rig = new RigDefinition(
            "role-rig",
            "Role rig",
            [
                Bone(0, "root", -1, BoneKind.Root),
                Bone(1, "deform", 0, BoneKind.Deform),
                Bone(2, "helper", 1, BoneKind.Helper),
                Bone(3, "camera", 2, BoneKind.Camera),
                Bone(4, "prop", 3, BoneKind.Prop),
            ]);

        SkeletonRenderData rendered =
            CorePreviewAdapter.ToRenderSkeleton(
                rig.CreateBindPose());

        Assert.Equal(
            [
                BoneRenderRole.Deform,
                BoneRenderRole.Deform,
                BoneRenderRole.Helper,
                BoneRenderRole.Camera,
                BoneRenderRole.Prop,
            ],
            rendered.Bones.Select(static bone => bone.Role));
    }

    [Fact]
    public void CompactHierarchyUsesFlagsAndOnlyKnownCameraNames()
    {
        CompactMeshDocument hierarchy = CreateHierarchy(
            ("bip01", -1, CompactMeshEntityType.Bone),
            ("cloth_helper", 0, CompactMeshEntityType.Helper),
            ("EyeCamera", 0, CompactMeshEntityType.Helper),
            ("RefCamera", 0, CompactMeshEntityType.Helper),
            ("EyeRef", 0, CompactMeshEntityType.Helper),
            ("weapon_socket", 0, CompactMeshEntityType.Unknown));
        var mesh = new Dl1MeshData(
            "role-test",
            Dl1MeshContainerLayout.ThreeItemMetadataOnly,
            hierarchy,
            null,
            [],
            [],
            [],
            [],
            [],
            []);

        SkeletonRenderData skeleton =
            Assert.IsType<SkeletonRenderData>(
                Dl1MeshPreviewAdapter.Convert(mesh).Skeleton);

        Assert.Equal(BoneRenderRole.Deform, skeleton.Bones[0].Role);
        Assert.Equal(BoneRenderRole.Helper, skeleton.Bones[1].Role);
        Assert.Equal(BoneRenderRole.Camera, skeleton.Bones[2].Role);
        Assert.Equal(BoneRenderRole.Camera, skeleton.Bones[3].Role);
        Assert.Equal(BoneRenderRole.Helper, skeleton.Bones[4].Role);
        Assert.Equal(BoneRenderRole.Prop, skeleton.Bones[5].Role);
    }

    [Fact]
    public void EmbeddedSkinnedMeshRigUsesCompactPropPivotOverlay()
    {
        CompactMeshDocument hierarchy = CreateHierarchy(
            ("prop_root", -1, CompactMeshEntityType.Bone),
            ("prop_bolt", 0, CompactMeshEntityType.Bone),
            ("prop_mesh", 0, CompactMeshEntityType.SkinnedMesh),
            ("prop_helper", 2, CompactMeshEntityType.Helper));
        var mesh = new Dl1MeshData(
            "embedded-prop-rig",
            Dl1MeshContainerLayout.FiveItemSplitGpu,
            hierarchy,
            null,
            [],
            [CreateEmbeddedPropSurface(entityIndex: 2)],
            [],
            [],
            [],
            []);

        Dl1MeshPreviewPayload preview =
            Dl1MeshPreviewAdapter.Convert(mesh);
        SkeletonRenderData skeleton =
            Assert.IsType<SkeletonRenderData>(preview.Skeleton);

        Assert.Equal(
            [
                BoneRenderRole.Prop,
                BoneRenderRole.Prop,
                BoneRenderRole.Prop,
                BoneRenderRole.Helper,
            ],
            skeleton.Bones.Select(static bone => bone.Role));
        Assert.False(skeleton.Bones[0].IsHierarchyOverlayVisible);
        Assert.False(skeleton.Bones[1].IsHierarchyOverlayVisible);
        Assert.False(skeleton.Bones[2].IsHierarchyOverlayVisible);
        Assert.True(skeleton.Bones[3].IsHierarchyOverlayVisible);
        Assert.DoesNotContain(
            skeleton.Bones,
            static bone => bone.Role == BoneRenderRole.Deform);
        Assert.Contains(
            preview.Diagnostics,
            static diagnostic => diagnostic.Contains(
                "embedded skinned-mesh animated-prop layout",
                StringComparison.Ordinal));

        SkeletonRenderData propsVisible = skeleton with
        {
            ShowHelpers = true,
            ShowProps = true,
        };
        Assert.False(propsVisible.IsVisible(skeleton.Bones[0]));
        Assert.False(propsVisible.IsVisible(skeleton.Bones[1]));
        Assert.False(propsVisible.IsVisible(skeleton.Bones[2]));
        Assert.True(propsVisible.IsVisible(skeleton.Bones[3]));
        Assert.True(propsVisible.IsVisible(
            skeleton.Bones[1] with
            {
                IsSelected = true,
            }));
    }

    [Fact]
    public void EmbeddedSkinnedMeshWithoutEffectivePaletteFailsClosed()
    {
        CompactMeshDocument hierarchy = CreateHierarchy(
            ("character_root", -1, CompactMeshEntityType.Bone),
            ("character_helper", 0, CompactMeshEntityType.Helper),
            ("character_mesh", 0, CompactMeshEntityType.SkinnedMesh));
        var mesh = new Dl1MeshData(
            "unproven-embedded-layout",
            Dl1MeshContainerLayout.ThreeItemMetadataOnly,
            hierarchy,
            null,
            [],
            [],
            [],
            [],
            [],
            []);

        SkeletonRenderData skeleton =
            Assert.IsType<SkeletonRenderData>(
                Dl1MeshPreviewAdapter.Convert(mesh).Skeleton);

        Assert.Equal(BoneRenderRole.Deform, skeleton.Bones[0].Role);
        Assert.True(skeleton.Bones[0].IsHierarchyOverlayVisible);
        Assert.Equal(BoneRenderRole.Helper, skeleton.Bones[1].Role);
        Assert.Equal(BoneRenderRole.Prop, skeleton.Bones[2].Role);
    }

    [Fact]
    public void VisibilityFlagsHideHelpersAndPropsButSelectionWins()
    {
        BoneRenderData deform = CreateBone("deform", -1) with
        {
            Role = BoneRenderRole.Deform,
        };
        BoneRenderData helper = CreateBone("helper", 0) with
        {
            Role = BoneRenderRole.Helper,
        };
        BoneRenderData camera = CreateBone("camera", 0) with
        {
            Role = BoneRenderRole.Camera,
        };
        BoneRenderData prop = CreateBone("prop", 0) with
        {
            Role = BoneRenderRole.Prop,
        };
        var defaults = new SkeletonRenderData(
            [deform, helper, camera, prop],
            Matrix4x4.Identity);

        Assert.True(defaults.IsVisible(deform));
        Assert.False(defaults.IsVisible(helper));
        Assert.True(defaults.IsVisible(camera));
        Assert.False(defaults.IsVisible(prop));
        Assert.True(defaults.IsVisible(helper with
        {
            IsSelected = true,
        }));
        Assert.True(defaults.IsVisible(prop with
        {
            IsSelected = true,
        }));
    }

    [Fact]
    public void SceneBufferAndSelectionPreserveVisibilityFlags()
    {
        var coordinator = new LinkedViewportCoordinator();
        var source = new ViewportSceneSource(
            coordinator,
            ViewportSide.Target,
            Vector4.Zero);
        var skeleton = new SkeletonRenderData(
            [
                CreateBone("root", -1),
                CreateBone("helper", 0) with
                {
                    Role = BoneRenderRole.Helper,
                },
            ],
            Matrix4x4.Identity)
        {
            ShowDeformBones = false,
            ShowHelpers = true,
            ShowCameraHelpers = false,
            ShowProps = true,
        };

        source.SetSkeleton(skeleton);
        source.SelectBone(1);
        SkeletonRenderData captured =
            Assert.IsType<SkeletonRenderData>(
                source.CaptureFrame().Skeleton);

        Assert.False(captured.ShowDeformBones);
        Assert.True(captured.ShowHelpers);
        Assert.False(captured.ShowCameraHelpers);
        Assert.True(captured.ShowProps);
        Assert.True(captured.Bones[1].IsSelected);
        Assert.Equal(BoneRenderRole.Helper, captured.Bones[1].Role);
    }

    private static BoneDefinition Bone(
        int index,
        string name,
        int parent,
        BoneKind kind) =>
        new(
            index,
            name,
            parent,
            TransformTRS.Identity,
            kind);

    private static BoneRenderData CreateBone(
        string name,
        int parent) =>
        new(
            name,
            parent,
            Matrix4x4.Identity,
            Matrix4x4.Identity,
            false);

    private static Dl1MeshSurface CreateEmbeddedPropSurface(
        int entityIndex) =>
        new(
            "prop_mesh",
            entityIndex,
            0,
            0,
            new Dl1VertexLayout(12, []),
            new Dl1MeshBufferSlice(0, 0, 36, 12),
            new Dl1MeshBufferSlice(1, 0, 6, 2),
            3,
            3,
            [
                CreatePropVertex(new Vector3(0.0f, 0.0f, 0.0f)),
                CreatePropVertex(new Vector3(1.0f, 0.0f, 0.0f)),
                CreatePropVertex(new Vector3(0.0f, 1.0f, 0.0f)),
            ],
            [0, 1, 2],
            [
                new Dl1MeshSubmesh(
                    0,
                    0,
                    3,
                    0,
                    [0, 1])
                {
                    SkinBindingMode =
                        Dl1SkinBindingMode.ExplicitVertexWeights,
                },
            ]);

    private static Dl1MeshVertex CreatePropVertex(
        Vector3 position) =>
        new(
            position,
            Vector3.UnitZ,
            new Vector4(1.0f, 0.0f, 0.0f, 1.0f),
            Vector2.Zero,
            Vector2.Zero,
            Vector4.One,
            Vector4.UnitX,
            new Dl1BoneIndex4(0, 0, 0, 0));

    private static CompactMeshDocument CreateHierarchy(
        params (
            string Name,
            short Parent,
            CompactMeshEntityType Type)[] rows)
    {
        CompactMeshEntity[] entities = rows
            .Select(static (row, index) =>
                new CompactMeshEntity(
                    index,
                    row.Name,
                    0,
                    new CompactBounds(),
                    row.Parent,
                    row.Type,
                    0,
                    0,
                    CompactMatrix3x4.Identity,
                    CompactMatrix3x4.Identity,
                    0,
                    0))
            .ToArray();
        return new CompactMeshDocument(
            entities.Length,
            entities.Count(static entity =>
                entity.ParentIndex < 0),
            0,
            entities,
            []);
    }
}
