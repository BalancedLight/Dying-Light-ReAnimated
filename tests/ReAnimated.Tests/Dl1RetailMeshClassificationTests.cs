using System.Numerics;
using ReAnimated.Codecs.CompactMesh;
using ReAnimated.Core.Domain;
using ReAnimated.Core.Mathematics;
using ReAnimated.DL1.Assets.Catalog;
using ReAnimated.DL1.Assets.Meshes;

namespace ReAnimated.Tests;

public sealed class Dl1RetailMeshClassificationTests
{
    private readonly Dl1RetailMeshClassificationService _classifier =
        new();

    [Theory]
    [InlineData("player_1_tpp", Dl1RigFamily.Player)]
    [InlineData("jade", Dl1RigFamily.GenericNpc)]
    [InlineData("rais", Dl1RigFamily.GenericNpc)]
    [InlineData("survivor_a", Dl1RigFamily.GenericNpc)]
    [InlineData("survivor_woman_a", Dl1RigFamily.GenericNpc)]
    [InlineData("zombie_man_a", Dl1RigFamily.GenericInfected)]
    [InlineData("zombie_woman", Dl1RigFamily.GenericInfected)]
    [InlineData("zombie_voleteile", Dl1RigFamily.Volatile)]
    [InlineData("zombie_screamer", Dl1RigFamily.Screamer)]
    [InlineData("armored", Dl1RigFamily.Demolisher)]
    [InlineData("zombie_goon", Dl1RigFamily.Goon)]
    public void PromotesBoundedNameHintOnlyWithDecodedSkinAndRigAnchors(
        string resourceName,
        Dl1RigFamily expectedFamily)
    {
        Dl1RetailMeshProfile profile = _classifier.Classify(
            CreateAsset(resourceName),
            CreateMesh(resourceName));

        Assert.Equal(Dl1MeshGeometryKind.Skinned, profile.GeometryKind);
        Assert.Equal(expectedFamily, profile.RigFamily);
        Assert.Equal(
            Dl1ClassificationConfidence.High,
            profile.RigFamilyConfidence);
        Assert.NotNull(profile.RigSignature);
        Assert.Contains(
            profile.Evidence,
            static row =>
                row.Code == "family.humanoid-anchors-corroborated");
    }

    [Fact]
    public void ExplicitPerspectiveTokenDoesNotInferRuntimeCorrections()
    {
        Dl1RetailMeshProfile fpp = _classifier.Classify(
            CreateAsset("player_1_fpp"),
            CreateMesh("player_1_fpp"));
        Dl1RetailMeshProfile tpp = _classifier.Classify(
            CreateAsset("player_1_tpp"),
            CreateMesh("player_1_tpp"));
        Dl1RetailMeshProfile ambiguous = _classifier.Classify(
            CreateAsset("player_fpp_tpp"),
            CreateMesh("player_fpp_tpp"));

        Assert.Equal(Dl1MeshPerspective.FirstPerson, fpp.Perspective);
        Assert.Equal(Dl1MeshPerspective.ThirdPerson, tpp.Perspective);
        Assert.Equal(
            Dl1ClassificationConfidence.High,
            fpp.PerspectiveConfidence);
        Assert.Equal(
            Dl1MeshPerspective.Unknown,
            ambiguous.Perspective);
        Assert.Contains(
            ambiguous.Evidence,
            static row =>
                row.Code == "perspective.conflicting-tokens");
    }

    [Fact]
    public void FamilyHintFailsClosedWithoutSkinOrHumanoidAnchors()
    {
        Dl1MeshData staticMesh = CreateMesh(
            "armored",
            skinned: false);
        Dl1MeshData partialRig = CreateMesh(
            "zombie_screamer",
            includeLimbAnchors: false);

        Dl1RetailMeshProfile staticProfile = _classifier.Classify(
            CreateAsset("armored"),
            staticMesh);
        Dl1RetailMeshProfile partialProfile = _classifier.Classify(
            CreateAsset("zombie_screamer"),
            partialRig);

        Assert.Equal(Dl1MeshGeometryKind.Static, staticProfile.GeometryKind);
        Assert.Equal(Dl1RigFamily.Unknown, staticProfile.RigFamily);
        Assert.Equal(Dl1RigFamily.Unknown, partialProfile.RigFamily);
        Assert.Contains(
            staticProfile.Evidence,
            static row => row.Code == "family.hint-not-corroborated");
        Assert.Contains(
            partialProfile.Evidence,
            static row =>
                row.Code == "family.humanoid-anchors-insufficient");
    }

    [Fact]
    public void CatalogAndDecodedIdentityMismatchDisablesNameClassification()
    {
        Dl1RetailMeshProfile profile = _classifier.Classify(
            CreateAsset("player_1_fpp"),
            CreateMesh("armored"));

        Assert.Equal(Dl1RigFamily.Unknown, profile.RigFamily);
        Assert.Equal(Dl1MeshPerspective.Unknown, profile.Perspective);
        Assert.Contains(
            profile.Evidence,
            static row =>
                row.Code == "identity.resource-name-mismatch");
    }

    [Fact]
    public void PositiveSkinEvidenceSurvivesAnIndependentDecodeError()
    {
        Dl1MeshData mesh = CreateMesh("armored") with
        {
            Diagnostics =
            [
                new Dl1MeshDiagnostic(
                    "TEST001",
                    Dl1MeshDiagnosticSeverity.Error,
                    "Synthetic unrelated surface diagnostic."),
            ],
        };

        Dl1RetailMeshProfile profile = _classifier.Classify(
            CreateAsset("armored"),
            mesh);

        Assert.Equal(Dl1MeshGeometryKind.Skinned, profile.GeometryKind);
        Assert.Equal(Dl1RigFamily.Demolisher, profile.RigFamily);
        Assert.NotNull(profile.RigSignature);
        Assert.Contains(
            profile.Evidence,
            static row =>
                row.Code == "geometry.partial-decode-errors");
    }

    [Fact]
    public void ExcessiveResourceNameDisablesTokenClassification()
    {
        string resourceName =
            "player_fpp_" + new string('a', 4_096);
        Dl1RetailMeshProfile profile = _classifier.Classify(
            CreateAsset(resourceName),
            CreateMesh(resourceName));

        Assert.Equal(Dl1MeshGeometryKind.Skinned, profile.GeometryKind);
        Assert.Equal(Dl1RigFamily.Unknown, profile.RigFamily);
        Assert.Equal(Dl1MeshPerspective.Unknown, profile.Perspective);
        Assert.Contains(
            profile.Evidence,
            static row =>
                row.Code == "identity.resource-name-too-long");
    }

    [Fact]
    public void EmitsFacialVariantProviderAndDlcFilterMetadata()
    {
        Dl1MeshData mesh = CreateMesh(
            "player_1_tpp",
            morphTargets:
            [
                new Dl1MorphTarget(
                    0,
                    "morph_jaw_open",
                    [7],
                    [],
                    [],
                    Dl1MorphPayloadStatus.VertexDeltasDecoded),
            ],
            variants: ["Wet", "Default", "wet"]);
        Dl1RetailMeshProfile profile = _classifier.Classify(
            CreateAsset(
                "player_1_tpp",
                containerPath:
                    @"E:\SteamLibrary\steamapps\common\Dying Light\DW_DLC49\Data\characters_PC.rpack"),
            mesh);

        Assert.Equal(
            Dl1FacialSupport.DecodedMorphDeltas,
            profile.FacialSupport);
        Assert.Equal(Dl1RetailSourceScope.Dlc, profile.SourceScope);
        Assert.Equal("DW_DLC49", profile.DlcIdentifier);
        Assert.Equal(["Default", "Wet"], profile.VariantNames);
        var filter = new Dl1RetailMeshFilter
        {
            GeometryKind = Dl1MeshGeometryKind.Skinned,
            RigSignature = profile.RigSignature,
            RigFamily = Dl1RigFamily.Player,
            MinimumRigFamilyConfidence =
                Dl1ClassificationConfidence.High,
            Perspective = Dl1MeshPerspective.ThirdPerson,
            MinimumPerspectiveConfidence =
                Dl1ClassificationConfidence.High,
            FacialSupport = true,
            ProviderId = "dl1-rpacks",
            PackName = "characters_PC.rpack",
            SourceScope = Dl1RetailSourceScope.Dlc,
            DlcIdentifier = "dw_dlc49",
            VariantName = "wet",
        };
        Assert.True(filter.Matches(profile));
        Assert.False(filter.Matches(profile with
        {
            RigFamilyConfidence =
                Dl1ClassificationConfidence.Medium,
        }));
        Assert.False(new Dl1RetailMeshFilter
        {
            FacialSupport = false,
        }.Matches(profile));
    }

    [Fact]
    public void UnknownFacialEvidenceDoesNotMatchNegativeCapabilityFilter()
    {
        Dl1MeshData container = CreateMesh(
            "zombie_goon",
            skinned: false,
            includeSurface: false,
            containerLayout:
                Dl1MeshContainerLayout.ThreeItemMetadataOnly);
        Dl1RetailMeshProfile profile = _classifier.Classify(
            CreateAsset("zombie_goon"),
            container);

        Assert.Equal(
            Dl1MeshGeometryKind.MetadataContainer,
            profile.GeometryKind);
        Assert.Equal(Dl1FacialSupport.Unknown, profile.FacialSupport);
        Assert.False(new Dl1RetailMeshFilter
        {
            FacialSupport = false,
        }.Matches(profile));
    }

    [Fact]
    public void AdditionalRootPrecedenceOverridesBaseLookingPathScope()
    {
        Dl1RetailMeshProfile profile = _classifier.Classify(
            CreateAsset(
                "survivor_a",
                priority: 100_000_000,
                containerPath:
                    @"E:\SteamLibrary\steamapps\common\Dying Light\DW\Data\override.rpack"),
            CreateMesh("survivor_a"));

        Assert.Equal(
            Dl1RetailSourceScope.UserAdded,
            profile.SourceScope);
        Assert.Contains(
            profile.Evidence,
            static row => row.Code == "source.user-added");
    }

    [Fact]
    public void RejectsNonMeshCatalogAssets()
    {
        RetailAssetRecord asset = CreateAsset(
            "not_a_mesh",
            resourceType: 320);

        Assert.Throws<ArgumentException>(() =>
            _classifier.Classify(
                asset,
                CreateMesh("not_a_mesh")));
    }

    private static RetailAssetRecord CreateAsset(
        string resourceName,
        int priority = 10_000,
        string containerPath =
            @"E:\SteamLibrary\steamapps\common\Dying Light\DW\Data\common_meshes_PC.rpack",
        short resourceType = 272)
    {
        RetailAssetLogicalId logical =
            RetailAssetLogicalId.Rpack(
                resourceType,
                resourceName);
        return new RetailAssetRecord(
            RetailAssetId.Create(
                logical,
                "test-install",
                "dl1-rpacks",
                12,
                priority,
                "test-snapshot"),
            resourceName,
            new RetailAssetSource(
                "dl1-rpacks",
                RetailAssetSourceKind.Rpack,
                priority,
                containerPath,
                resourceName,
                12,
                1024,
                2048,
                DateTime.UnixEpoch));
    }

    private static Dl1MeshData CreateMesh(
        string resourceName,
        bool skinned = true,
        bool includeLimbAnchors = true,
        bool includeSurface = true,
        Dl1MeshContainerLayout containerLayout =
            Dl1MeshContainerLayout.FiveItemSplitGpu,
        IReadOnlyList<Dl1MorphTarget>? morphTargets = null,
        IReadOnlyList<string>? variants = null)
    {
        List<BoneDefinition> bones =
        [
            Bone(0, "bip01", -1, BoneKind.Root),
            Bone(1, "pelvis", 0),
            Bone(2, "head", 1),
        ];
        if (includeLimbAnchors)
        {
            bones.AddRange(
            [
                Bone(3, "l_upperarm", 1),
                Bone(4, "r_upperarm", 1),
                Bone(5, "l_thigh", 1),
                Bone(6, "r_thigh", 1),
            ]);
        }

        RigDefinition? rig = skinned
            ? new RigDefinition(
                $"dl1-retail:{resourceName}",
                resourceName,
                bones)
            : null;
        CompactMeshEntity[] entities = bones
            .Select(static bone =>
                new CompactMeshEntity(
                    bone.Index,
                    bone.Name,
                    0,
                    new CompactBounds(0, 0, 0, 1, 1, 1),
                    checked((short)bone.ParentIndex),
                    CompactMeshEntityType.Bone,
                    0,
                    1,
                    CompactMatrix3x4.Identity,
                    CompactMatrix3x4.Identity,
                    0,
                    0))
            .ToArray();
        CompactMeshDocument hierarchy = new(
            entities.Length,
            1,
            0,
            entities,
            []);
        Dl1MeshSurface[] surfaces = includeSurface
            ?
            [
                new Dl1MeshSurface(
                    "body",
                    0,
                    0,
                    0,
                    new Dl1VertexLayout(12, []),
                    new Dl1MeshBufferSlice(3, 0, 12, 12),
                    new Dl1MeshBufferSlice(4, 0, 2, 2),
                    1,
                    1,
                    [new Dl1MeshVertex(
                        Vector3.Zero,
                        Vector3.UnitY,
                        Vector4.UnitX,
                        Vector2.Zero,
                        Vector2.Zero,
                        Vector4.One,
                        skinned ? Vector4.UnitX : Vector4.Zero,
                        new Dl1BoneIndex4(0, 0, 0, 0))],
                    [0],
                    [new Dl1MeshSubmesh(
                        0,
                        0,
                        1,
                        0,
                        skinned ? [0] : [])]),
            ]
            : [];
        return new Dl1MeshData(
            resourceName,
            containerLayout,
            hierarchy,
            rig,
            [],
            surfaces,
            [],
            morphTargets ?? [],
            variants ?? [],
            []);
    }

    private static BoneDefinition Bone(
        int index,
        string name,
        int parent,
        BoneKind kind = BoneKind.Deform) =>
        new(
            index,
            name,
            parent,
            TransformTRS.Identity,
            kind,
            requiredForExport: true);
}
