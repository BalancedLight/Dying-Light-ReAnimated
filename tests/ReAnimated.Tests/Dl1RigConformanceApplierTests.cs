using System.Collections.Immutable;
using System.Security.Cryptography;
using ReAnimated.Codecs.Fbx;
using ReAnimated.Codecs.Models;
using ReAnimated.Core.Domain;
using ReAnimated.Core.Mathematics;
using ReAnimated.Core.ModelAuthoring;
using ReAnimated.DL1.Assets.Meshes;
using ReAnimated.Retargeting.Conformance;

namespace ReAnimated.Tests;

/// <summary>
/// End-to-end controls proving a conformed rig survives the existing DL1
/// authoring contract unchanged, including the retail Chrome frame convention.
/// </summary>
public sealed class Dl1RigConformanceApplierTests
{
    [Fact]
    public void ConformedModelIsAcceptedByTheExistingAuthoringPreparer()
    {
        Dl1PreparedAuthoredRig prepared = PrepareConformed(dropExtras: false);

        Assert.Empty(prepared.Diagnostics);
        Assert.NotEmpty(prepared.Contract.Nodes);
        Assert.StartsWith("authored:", prepared.Contract.ContractId, StringComparison.Ordinal);
    }

    [Fact]
    public void EmittedRigCarriesTheDl1EntityNamesStockClipsResolve()
    {
        Dl1PreparedAuthoredRig prepared = PrepareConformed(dropExtras: false);
        HashSet<string> names = prepared.Contract.Nodes
            .Select(static node => node.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (string required in new[]
                 {
                     "bip01", "pelvis", "spine", "head",
                     "l_upperarm", "l_forearm", "l_hand",
                     "r_upperarm", "r_forearm", "r_hand",
                     "eyecamera",
                 })
        {
            Assert.Contains(required, names);
        }
    }

    /// <summary>
    /// Locks the convention measured on retail <c>player_1_tpp</c>: every bone's
    /// local +X axis aims exactly at its child.
    /// </summary>
    [Fact]
    public void EmittedChromeFramesAimPositiveXAtTheChild()
    {
        Dl1PreparedAuthoredRig prepared = PrepareConformed(dropExtras: false);
        ImmutableArray<Dl1AuthoredRigNode> nodes = prepared.Contract.Nodes;
        int measured = 0;

        for (int index = 0; index < nodes.Length; index++)
        {
            Dl1AuthoredRigNode[] children = nodes
                .Where(node => node.ParentPhysicalIndex == index)
                .ToArray();
            if (children.Length != 1)
            {
                continue;
            }

            TransformMatrix global = nodes[index].GlobalBindMatrix;
            Vector3D direction =
                children[0].GlobalBindMatrix.Translation - global.Translation;
            if (direction.Length < 1e-6)
            {
                continue;
            }

            var axisX = new Vector3D(global.M11, global.M21, global.M31);
            Assert.Equal(1.0, Vector3D.Dot(axisX, direction.Normalized()), 5);
            measured++;
        }

        Assert.True(measured > 0, "the fixture must contain at least one single-child chain");
    }

    [Fact]
    public void DroppingExtraBonesEmitsExactlyTheTemplateSkeleton()
    {
        Dl1RigTemplate template = CreateTemplate();
        Dl1PreparedAuthoredRig prepared = PrepareConformed(dropExtras: true);

        Assert.Equal(template.EntityCount, prepared.Contract.Nodes.Length);
        Assert.Equal(
            template.Entities.Select(static entity => entity.Name).Order(StringComparer.Ordinal),
            prepared.Contract.Nodes.Select(static node => node.Name).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void KeepingExtraBonesRetainsThemAlongsideTheTemplateSkeleton()
    {
        Dl1RigTemplate template = CreateTemplate();
        Dl1PreparedAuthoredRig prepared = PrepareConformed(dropExtras: false);

        Assert.True(prepared.Contract.Nodes.Length > template.EntityCount);
        Assert.Contains(
            prepared.Contract.Nodes,
            static node => string.Equals(
                node.Name,
                "CC_Base_L_ForearmTwist01",
                StringComparison.Ordinal));
    }

    [Fact]
    public void EveryEmittedSurfacePaletteAddressesTheConformedRig()
    {
        (FbxModelAuthoringImportResult conformed, Dl1PreparedAuthoredRig prepared) =
            Conform(dropExtras: false);

        foreach (FbxModelSurface surface in conformed.Surfaces.Where(static s => s.IsSkinned))
        {
            Assert.All(
                surface.PaletteBoneIndices,
                entry => Assert.InRange(entry, 0, conformed.Package.Document.Bones.Length - 1));
        }

        Assert.Equal(
            conformed.Surfaces.Length,
            prepared.Surfaces.Length);
    }

    [Fact]
    public void ConformanceClearsAHelperAuthoredAgainstTheOldBoneIndexes()
    {
        (FbxModelAuthoringImportResult conformed, _) = Conform(dropExtras: false);

        // Old helper rows referenced source row indexes that no longer exist.
        Assert.Empty(conformed.Package.Document.AuthoredHelpers);
        Assert.Null(conformed.Package.Document.LastBuildReceipt);
    }

    [Fact]
    public void StaticModelWithoutARigIsRefused()
    {
        (FbxModelAuthoringImportResult model, RigConformanceResult fit) = BuildModel(dropExtras: false);

        Assert.Throws<InvalidOperationException>(
            () => Dl1RigConformanceApplier.Apply(model with { Rig = null }, fit));
    }

    private static Dl1PreparedAuthoredRig PrepareConformed(bool dropExtras) =>
        Conform(dropExtras).Prepared;

    private static (FbxModelAuthoringImportResult Conformed, Dl1PreparedAuthoredRig Prepared)
        Conform(bool dropExtras)
    {
        (FbxModelAuthoringImportResult model, RigConformanceResult fit) = BuildModel(dropExtras);
        FbxModelAuthoringImportResult conformed =
            Dl1RigConformanceApplier.Apply(model, fit);
        return (conformed, Dl1CustomModelRigPreparer.Prepare(conformed));
    }

    private static (FbxModelAuthoringImportResult Model, RigConformanceResult Fit)
        BuildModel(bool dropExtras)
    {
        Dl1RigTemplate template = CreateTemplate();
        RigDefinition source = CreateSourceRig();
        RigCorrespondence correspondence = RigCorrespondenceSolver.Solve(
            template,
            source,
            new RigCorrespondenceOptions { DropExtraBones = dropExtras });
        RigLandmarkSolution landmark =
            RigLandmarkSolver.Solve(template, source, correspondence);
        RigConformanceResult fit = RigConformanceSolver.Solve(
            template,
            source,
            correspondence,
            landmark);

        return (CreateImportResult(source), fit);
    }

    private static FbxModelAuthoringImportResult CreateImportResult(RigDefinition source)
    {
        byte[] fbx = "Kaydara FBX Binary  synthetic-conformance-source"u8.ToArray();
        string hash = Convert.ToHexString(SHA256.HashData(fbx)).ToLowerInvariant();

        var bones = ImmutableArray.CreateBuilder<CustomModelBone>(source.BoneCount);
        foreach (BoneDefinition bone in source.Bones)
        {
            bones.Add(new CustomModelBone
            {
                Index = bone.Index,
                FbxObjectId = 100 + bone.Index,
                Name = bone.Name,
                ParentIndex = bone.ParentIndex,
                LocalBindTransform = bone.LocalBindPose,
                ExactLocalBindMatrix = bone.LocalBindPose.ToMatrix(),
                Kind = bone.Kind,
                IsWeighted = true,
            });
        }

        var document = new CustomModelDocument
        {
            ModelId = new Guid("3f5f7a41-2f2a-4f0c-9a56-1c0f3d4b5e6a"),
            Name = "Synthetic conformance model",
            RigMode = CustomModelRigMode.ExactFbxRig,
            Source = new CustomModelSourceIdentity
            {
                OriginalFileName = "synthetic.fbx",
                ContentSha256 = hash,
                FbxVersion = 7400,
            },
            RigSignature = hash,
            Bones = bones.MoveToImmutable(),
            Meshes =
            [
                new CustomModelMeshPart
                {
                    Name = "Body",
                    ControlPointCount = 3,
                    PolygonCount = 1,
                    TriangleCount = 1,
                    ExpandedVertexCount = 3,
                    MaterialSlotCount = 1,
                },
            ],
            Materials =
            [
                new CustomModelMaterial
                {
                    Id = new Guid("22c852ee-2cbd-579a-8a08-73a9335738fd"),
                    Name = "Default",
                },
            ],
        };
        document = document with
        {
            RigSignature = CustomModelContractSignatures.ComputeRig(document.Bones),
        };
        document.Validate();

        int forearm = source.GetBoneIndex("CC_Base_L_Forearm");
        int twist = source.GetBoneIndex("CC_Base_L_ForearmTwist01");
        int hand = source.GetBoneIndex("CC_Base_L_Hand");
        var surface = new FbxModelSurface(
            "body",
            "Body",
            new Guid("22c852ee-2cbd-579a-8a08-73a9335738fd"),
            [
                Vertex(new Vector3D(0.30, 1.35, 0.0), [0, 2], [0.7, 0.3]),
                Vertex(new Vector3D(0.40, 1.35, 0.0), [0, 1], [0.6, 0.4]),
                Vertex(new Vector3D(0.50, 1.35, 0.0), [2], [1.0]),
            ],
            [0, 1, 2],
            [forearm, twist, hand],
            [TransformMatrix.Identity, TransformMatrix.Identity, TransformMatrix.Identity],
            IsSkinned: true);

        return new FbxModelAuthoringImportResult(
            new CustomModelPackage(
                document,
                fbx.ToImmutableArray(),
                ImmutableDictionary<string, ImmutableArray<byte>>.Empty),
            source,
            [surface],
            ImmutableDictionary<Guid, AnimationClip>.Empty,
            new FbxStrictExportInspection(
                [],
                ImmutableDictionary<string, FbxAnimationStackInspection>.Empty,
                ImmutableDictionary<string, long>.Empty,
                ImmutableDictionary<string, long?>.Empty,
                [],
                0,
                0,
                ImmutableHashSet<string>.Empty,
                ImmutableHashSet<string>.Empty,
                ImmutableDictionary<string, FbxMeshGeometryInspection>.Empty,
                0,
                0,
                [],
                [],
                ImmutableHashSet<string>.Empty));
    }

    private static FbxModelVertex Vertex(
        Vector3D position,
        ImmutableArray<int> indices,
        ImmutableArray<double> weights) =>
        new(position, Vector3D.UnitY, 0.0, 0.0, indices, weights);

    private static Dl1RigTemplate CreateTemplate()
    {
        var entities = new List<(string Name, int Parent, Vector3D Offset, BoneKind Kind, bool Deform)>
        {
            ("bip01", -1, new Vector3D(0.0, 0.95, 0.0), BoneKind.Root, true),
            ("pelvis", 0, Vector3D.Zero, BoneKind.Deform, true),
            ("spine", 1, new Vector3D(0.0, 0.10, 0.0), BoneKind.Deform, true),
            ("head", 2, new Vector3D(0.0, 0.50, 0.0), BoneKind.Deform, true),
            ("eyecamera", 3, new Vector3D(0.0, 0.05, 0.10), BoneKind.Camera, false),
            ("l_upperarm", 2, new Vector3D(0.20, 0.40, 0.0), BoneKind.Deform, true),
            ("l_forearm", 5, new Vector3D(0.30, 0.0, 0.0), BoneKind.Deform, true),
            ("l_hand", 6, new Vector3D(0.25, 0.0, 0.0), BoneKind.Deform, true),
            ("r_upperarm", 2, new Vector3D(-0.20, 0.40, 0.0), BoneKind.Deform, true),
            ("r_forearm", 8, new Vector3D(-0.30, 0.0, 0.0), BoneKind.Deform, true),
            ("r_hand", 9, new Vector3D(-0.25, 0.0, 0.0), BoneKind.Deform, true),
        };

        var rows = ImmutableArray.CreateBuilder<Dl1RigTemplateEntity>(entities.Count);
        var globals = new TransformMatrix[entities.Count];
        for (int index = 0; index < entities.Count; index++)
        {
            (string name, int parent, Vector3D offset, BoneKind kind, bool deform) = entities[index];
            TransformMatrix local = TransformMatrix.CreateTranslation(offset);
            globals[index] = parent < 0 ? local : globals[parent] * local;
            rows.Add(new Dl1RigTemplateEntity
            {
                Index = index,
                Name = name,
                ParentIndex = parent,
                Kind = kind,
                IsDeform = deform,
                LocalRestMatrix = local,
                GlobalRestMatrix = globals[index],
                SemanticRole = Dl1RigDefinitionFactory.TryResolveSemanticRole(name),
            });
        }

        return new Dl1RigTemplate("player", "player_1_tpp", "synthetic", rows.MoveToImmutable());
    }

    private static RigDefinition CreateSourceRig()
    {
        var rows = new List<(string Name, int Parent, Vector3D Offset)>
        {
            ("RL_BoneRoot", -1, Vector3D.Zero),
            ("CC_Base_Hip", 0, new Vector3D(0.0, 0.95, 0.0)),
            ("CC_Base_Waist", 1, new Vector3D(0.0, 0.10, 0.0)),
            ("CC_Base_Head", 2, new Vector3D(0.0, 0.50, 0.0)),
            ("CC_Base_L_Upperarm", 2, new Vector3D(0.20, 0.40, 0.0)),
            ("CC_Base_L_Forearm", 4, new Vector3D(0.30, 0.0, 0.0)),
            ("CC_Base_L_Hand", 5, new Vector3D(0.25, 0.0, 0.0)),
            ("CC_Base_L_ForearmTwist01", 5, new Vector3D(0.10, 0.0, 0.0)),
            ("CC_Base_R_Upperarm", 2, new Vector3D(-0.20, 0.40, 0.0)),
            ("CC_Base_R_Forearm", 8, new Vector3D(-0.30, 0.0, 0.0)),
            ("CC_Base_R_Hand", 9, new Vector3D(-0.25, 0.0, 0.0)),
        };

        var bones = ImmutableArray.CreateBuilder<BoneDefinition>(rows.Count);
        for (int index = 0; index < rows.Count; index++)
        {
            (string name, int parent, Vector3D offset) = rows[index];
            bones.Add(new BoneDefinition(
                index,
                name,
                parent,
                new TransformTRS(offset, QuaternionD.Identity, Vector3D.One),
                parent < 0 ? BoneKind.Root : BoneKind.Deform));
        }

        return new RigDefinition("source:test", "synthetic", bones.MoveToImmutable());
    }
}
