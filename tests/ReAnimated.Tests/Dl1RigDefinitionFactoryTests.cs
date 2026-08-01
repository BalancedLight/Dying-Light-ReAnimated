using ReAnimated.Codecs.CompactMesh;
using ReAnimated.Codecs.Anm2;
using ReAnimated.Core.Domain;
using ReAnimated.DL1.Assets.Meshes;

namespace ReAnimated.Tests;

public sealed class Dl1RigDefinitionFactoryTests
{
    [Fact]
    public void RetailHierarchyProducesDynamicRigMorphInventoryAndValidatedChains()
    {
        CompactMeshDocument hierarchy = CreateHierarchy(
            ("bip01", -1, CompactMeshEntityType.Bone),
            ("pelvis", 0, CompactMeshEntityType.Bone),
            ("l_upperarm", 1, CompactMeshEntityType.Bone),
            ("l_forearm", 2, CompactMeshEntityType.Bone),
            ("l_hand", 3, CompactMeshEntityType.Bone),
            ("eyecamera", 1, CompactMeshEntityType.Helper));
        Dl1MorphTarget morph = new(
            7,
            "blink_l",
            [],
            [],
            [],
            Dl1MorphPayloadStatus.ChannelOnly);

        RigDefinition? rig = Dl1RigDefinitionFactory.TryCreate(
            "player_1_tpp",
            hierarchy,
            [morph]);

        Assert.NotNull(rig);
        Assert.Equal("dl1-retail:player_1_tpp", rig.Id);
        Assert.Equal(6, rig.BoneCount);
        Assert.Equal("body.root", rig.Bones[0].SemanticRole);
        Assert.Equal(0x10F2DC54u, rig.Bones[0].DescriptorHash);
        Assert.Equal(BoneKind.Camera, rig.Bones[5].Kind);
        Assert.Equal("camera.eye", rig.Bones[5].SemanticRole);
        Assert.Single(rig.MorphChannels);
        Assert.Equal("blink_l", rig.MorphChannels[0].Name);
        TwoBoneIkChainDefinition chain = Assert.Single(rig.IkChains);
        Assert.Equal("left-hand", chain.Name);
        Assert.Equal((2, 3, 4), (
            chain.RootBoneIndex,
            chain.JointBoneIndex,
            chain.EndBoneIndex));
    }

    [Fact]
    public void StaticRetailHierarchyDoesNotPretendToHaveAnAnimationRig()
    {
        CompactMeshDocument hierarchy = CreateHierarchy(
            ("static_mesh", -1, CompactMeshEntityType.Mesh));

        RigDefinition? rig = Dl1RigDefinitionFactory.TryCreate(
            "street_prop",
            hierarchy);

        Assert.Null(rig);
    }

    [Fact]
    public void DuplicateRetailHelperNamesRetainIndexedIdentityWithoutAmbiguousLookup()
    {
        CompactMeshDocument hierarchy = CreateHierarchy(
            ("hook", -1, CompactMeshEntityType.Helper),
            ("hook", -1, CompactMeshEntityType.Helper),
            ("static_mesh", -1, CompactMeshEntityType.Mesh));

        RigDefinition? rig = Dl1RigDefinitionFactory.TryCreate(
            "barricade_razorwire",
            hierarchy);

        Assert.NotNull(rig);
        Assert.Equal(3, rig.BoneCount);
        Assert.Equal("hook", rig.Bones[0].Name);
        Assert.Equal("hook", rig.Bones[1].Name);
        Assert.Equal(
            Dl1NameHash.Compute("hook"),
            rig.Bones[0].DescriptorHash);
        Assert.Equal(
            rig.Bones[0].DescriptorHash,
            rig.Bones[1].DescriptorHash);
        Assert.Equal(-1, rig.GetBoneIndex("hook"));
        Assert.Equal([0, 1], rig.GetBoneIndices("hook").ToArray());
    }

    [Fact]
    public void RawMatrixHelperPreviewPolicyRejectsEverySkinPalette()
    {
        CompactMeshDocument hierarchy = CreateHierarchy(
            ("sheared_helper", -1, CompactMeshEntityType.Helper),
            ("static_mesh", -1, CompactMeshEntityType.Mesh));
        hierarchy = hierarchy with
        {
            Entities =
            [
                hierarchy.Entities[0] with
                {
                    LocalMatrix = new CompactMatrix3x4(
                        1, 0.5f, 0, 0,
                        0, 1, 0, 0,
                        0, 0, 1, 0),
                },
                hierarchy.Entities[1],
            ],
        };
        Dl1MeshSurface staticSurface =
            CreateSurface([]);
        const string failure =
            "entity 0 contains a singular or sheared local transform";

        Assert.True(
            Dl1RigPromotionPolicy
                .CanPublishRawMatrixHelperPreview(
                    hierarchy,
                    [staticSurface],
                    failure));
        Assert.False(
            Dl1RigPromotionPolicy
                .CanPublishRawMatrixHelperPreview(
                    hierarchy,
                    [
                        staticSurface with
                        {
                            Submeshes =
                            [
                                new Dl1MeshSubmesh(
                                    0,
                                    0,
                                    3,
                                    0,
                                    [0]),
                            ],
                        },
                    ],
                    failure));
    }

    [Fact]
    public void PromotionAnalysisSeparatesWeightedBonesFromUnweightedHelpers()
    {
        CompactMeshDocument hierarchy = CreateHierarchy(
            ("weighted_bone", -1, CompactMeshEntityType.Bone),
            ("unweighted_helper", 0, CompactMeshEntityType.Helper),
            ("skinned_mesh", -1, CompactMeshEntityType.SkinnedMesh));
        hierarchy = hierarchy with
        {
            Entities =
            [
                hierarchy.Entities[0],
                hierarchy.Entities[1] with
                {
                    LocalMatrix = new CompactMatrix3x4(
                        1, 0.5f, 0, 0,
                        0, 1, 0, 0,
                        0, 0, 1, 0),
                },
                hierarchy.Entities[2],
            ],
        };
        Dl1MeshVertex vertex = new(
            System.Numerics.Vector3.Zero,
            System.Numerics.Vector3.UnitZ,
            System.Numerics.Vector4.Zero,
            System.Numerics.Vector2.Zero,
            System.Numerics.Vector2.Zero,
            System.Numerics.Vector4.One,
            System.Numerics.Vector4.UnitX,
            new Dl1BoneIndex4(0, 0, 0, 0));
        Dl1MeshSurface surface = CreateSurface(
            [
                new Dl1MeshSubmesh(
                    0,
                    0,
                    3,
                    0,
                    [0])
                {
                    SkinBindingMode =
                        Dl1SkinBindingMode
                            .ExplicitVertexWeights,
                },
            ]) with
        {
            EntityIndex = 2,
            VertexCount = 1,
            Vertices = [vertex],
            Indices = [0, 0, 0],
        };

        Dl1RigPromotionAnalysis analysis =
            Dl1RigPromotionPolicy.Analyze(
                hierarchy,
                [surface]);

        Assert.Equal(
            [1],
            analysis.NonTrsEntityIndexes.ToArray());
        Assert.Equal(
            [0],
            analysis.DeclaredPaletteEntityIndexes.ToArray());
        Assert.Equal(
            [0],
            analysis.EffectiveSkinEntityIndexes.ToArray());
        Assert.False(
            analysis.HasEffectiveNonTrsSkinInfluence);
        Assert.False(analysis.HasUnresolvedSkinBindings);
        Assert.True(
            Dl1RigPromotionPolicy
                .CanPublishRawBindPosePreview(
                    hierarchy,
                    [surface]));

        CompactMeshDocument weightedHierarchy =
            hierarchy with
            {
                Entities =
                [
                    hierarchy.Entities[0] with
                    {
                        LocalMatrix =
                            hierarchy.Entities[1]
                                .LocalMatrix,
                    },
                    hierarchy.Entities[1] with
                    {
                        LocalMatrix =
                            CompactMatrix3x4.Identity,
                    },
                    hierarchy.Entities[2],
                ],
            };
        Dl1RigPromotionAnalysis weightedNonTrs =
            Dl1RigPromotionPolicy.Analyze(
                weightedHierarchy,
                [surface]);
        Assert.True(
            weightedNonTrs.HasEffectiveNonTrsSkinInfluence);
        Assert.False(
            Dl1RigPromotionPolicy
                .CanPublishRawBindPosePreview(
                    weightedHierarchy,
                    [surface]));

        Dl1MeshSurface declaredOnlySurface = surface with
        {
            Vertices =
            [
                vertex with
                {
                    LocalBlendIndices =
                        new Dl1BoneIndex4(
                            0,
                            1,
                            0,
                            0),
                },
            ],
            Submeshes =
            [
                surface.Submeshes[0] with
                {
                    BonePaletteEntityIndexes =
                        [0, 1],
                },
            ],
        };
        Dl1RigPromotionAnalysis declaredOnly =
            Dl1RigPromotionPolicy.Analyze(
                hierarchy,
                [declaredOnlySurface]);
        Assert.Contains(
            1,
            declaredOnly.DeclaredPaletteEntityIndexes);
        Assert.DoesNotContain(
            1,
            declaredOnly.EffectiveSkinEntityIndexes);
        Assert.False(
            Dl1RigPromotionPolicy
                .CanPublishRawBindPosePreview(
                    hierarchy,
                    [declaredOnlySurface]));

        Dl1MeshSurface unresolvedSurface = surface with
        {
            Submeshes =
            [
                surface.Submeshes[0] with
                {
                    SkinBindingMode =
                        Dl1SkinBindingMode
                            .UnresolvedMissingBlendStreams,
                },
            ],
        };
        Assert.False(
            Dl1RigPromotionPolicy
                .CanPublishRawBindPosePreview(
                    hierarchy,
                    [unresolvedSurface]));

        CompactMeshDocument nonFiniteHierarchy =
            hierarchy with
            {
                Entities =
                [
                    hierarchy.Entities[0],
                    hierarchy.Entities[1] with
                    {
                        LocalMatrix = new CompactMatrix3x4(
                            float.NaN, 0, 0, 0,
                            0, 1, 0, 0,
                            0, 0, 1, 0),
                    },
                    hierarchy.Entities[2],
                ],
            };
        Assert.False(
            Dl1RigPromotionPolicy
                .CanPublishRawBindPosePreview(
                    nonFiniteHierarchy,
                    [surface]));
    }

    private static CompactMeshDocument CreateHierarchy(
        params (string Name, short Parent, CompactMeshEntityType Type)[] rows)
    {
        CompactMeshEntity[] entities = rows
            .Select((row, index) =>
                new CompactMeshEntity(
                    index,
                    row.Name,
                    0,
                    new CompactBounds(0, 0, 0, 1, 1, 1),
                    row.Parent,
                    row.Type,
                    0,
                    1,
                    index == 0
                        ? CompactMatrix3x4.Identity
                        : new CompactMatrix3x4(
                            1, 0, 0, 0,
                            0, 1, 0, 1,
                            0, 0, 1, 0),
                    CompactMatrix3x4.Identity,
                    0,
                    0))
            .ToArray();
        return new CompactMeshDocument(
            entities.Length,
            entities.Count(static entity => entity.ParentIndex < 0),
            0,
            entities,
            []);
    }

    private static Dl1MeshSurface CreateSurface(
        IReadOnlyList<Dl1MeshSubmesh> submeshes) =>
        new(
            "static_mesh",
            1,
            0,
            0,
            new Dl1VertexLayout(12, []),
            new Dl1MeshBufferSlice(0, 0, 36, 12),
            new Dl1MeshBufferSlice(1, 0, 6, 2),
            3,
            3,
            [],
            [0, 1, 2],
            submeshes);
}
