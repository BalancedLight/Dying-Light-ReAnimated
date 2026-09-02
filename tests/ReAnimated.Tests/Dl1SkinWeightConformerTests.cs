using System.Collections.Immutable;
using ReAnimated.Codecs.Fbx;
using ReAnimated.Codecs.Models;
using ReAnimated.Core.Domain;
using ReAnimated.Core.Mathematics;
using ReAnimated.Core.ModelAuthoring;
using ReAnimated.Retargeting.Conformance;

namespace ReAnimated.Tests;

/// <summary>
/// Controls for rewriting imported skin bindings onto a conformed DL1 rig.
/// </summary>
public sealed class Dl1SkinWeightConformerTests
{
    [Fact]
    public void PaletteEntriesAreRemappedToEmittedBonesAndWeightsSurvive()
    {
        Fixture fixture = Build(dropExtras: false);

        Dl1SkinConformanceResult result = Dl1SkinWeightConformer.Conform(
            [fixture.Surface],
            fixture.Source,
            fixture.Fit);

        FbxModelSurface surface = Assert.Single(result.Surfaces);
        Assert.All(
            surface.PaletteBoneIndices,
            entry => Assert.InRange(entry, 0, fixture.Fit.Bones.Length - 1));
        Assert.Empty(result.Report.FoldedBones);
        AssertWeightsNormalized(surface);
    }

    [Fact]
    public void DroppedBoneWeightsFoldIntoTheNearestSurvivingAncestor()
    {
        Fixture fixture = Build(dropExtras: true);

        Dl1SkinConformanceResult result = Dl1SkinWeightConformer.Conform(
            [fixture.Surface],
            fixture.Source,
            fixture.Fit);

        FbxModelSurface surface = Assert.Single(result.Surfaces);
        Assert.Contains("CC_Base_L_ForearmTwist01", result.Report.FoldedBones);

        // The twist bone folded into l_forearm, so the vertex that was split
        // between them now rides entirely on the forearm.
        int forearm = fixture.Fit.Bones
            .Single(static bone => bone.Name == "l_forearm").Index;
        int slot = surface.PaletteBoneIndices.IndexOf(forearm);
        Assert.True(slot >= 0, "the conformed palette must retain l_forearm");

        FbxModelVertex vertex = surface.Vertices[1];
        int influence = vertex.BoneIndices.IndexOf(slot);
        Assert.True(influence >= 0);
        Assert.Equal(1.0, vertex.BoneWeights[influence], 9);
        AssertWeightsNormalized(surface);
    }

    [Fact]
    public void EveryWeightedVertexStillSumsToOne()
    {
        foreach (bool dropExtras in new[] { false, true })
        {
            Fixture fixture = Build(dropExtras);

            Dl1SkinConformanceResult result = Dl1SkinWeightConformer.Conform(
                [fixture.Surface],
                fixture.Source,
                fixture.Fit);

            AssertWeightsNormalized(Assert.Single(result.Surfaces));
        }
    }

    [Fact]
    public void StaleSourceInverseBindMatricesAreCleared()
    {
        Fixture fixture = Build(dropExtras: false);

        Dl1SkinConformanceResult result = Dl1SkinWeightConformer.Conform(
            [fixture.Surface],
            fixture.Source,
            fixture.Fit);

        // The emitted hierarchy owns these; carrying source matrices forward
        // would be silently wrong.
        Assert.Empty(Assert.Single(result.Surfaces).InverseBindMatrices);
    }

    [Fact]
    public void UnskinnedSurfacesPassThroughUntouched()
    {
        Fixture fixture = Build(dropExtras: false);
        FbxModelSurface staticSurface = fixture.Surface with
        {
            IsSkinned = false,
            PaletteBoneIndices = [],
        };

        Dl1SkinConformanceResult result = Dl1SkinWeightConformer.Conform(
            [staticSurface],
            fixture.Source,
            fixture.Fit);

        Assert.Same(staticSurface, Assert.Single(result.Surfaces));
    }

    [Fact]
    public void UnknownSourceBoneInAPaletteFailsClosed()
    {
        Fixture fixture = Build(dropExtras: false);
        FbxModelSurface broken = fixture.Surface with
        {
            PaletteBoneIndices = [fixture.Source.BoneCount + 5],
        };

        InvalidDataException exception = Assert.Throws<InvalidDataException>(
            () => Dl1SkinWeightConformer.Conform(
                [broken],
                fixture.Source,
                fixture.Fit));
        Assert.Contains("unknown source bone", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void InfluencesBeyondTheDl1CapAreDroppedAndReported()
    {
        Fixture fixture = Build(dropExtras: false);
        // Five roughly equal influences: one must be discarded to reach the
        // four-influence contract, and the rest renormalized.
        var crowded = new FbxModelVertex(
            new Vector3D(0.1, 1.0, 0.0),
            Vector3D.UnitY,
            0.0,
            0.0,
            [0, 1, 2, 3, 4],
            [0.3, 0.25, 0.2, 0.15, 0.1]);
        FbxModelSurface surface = fixture.Surface with
        {
            Vertices = [crowded],
            PaletteBoneIndices =
            [
                Index(fixture.Source, "CC_Base_Hip"),
                Index(fixture.Source, "CC_Base_Waist"),
                Index(fixture.Source, "CC_Base_L_Upperarm"),
                Index(fixture.Source, "CC_Base_L_Forearm"),
                Index(fixture.Source, "CC_Base_L_Hand"),
            ],
        };

        Dl1SkinConformanceResult result = Dl1SkinWeightConformer.Conform(
            [surface],
            fixture.Source,
            fixture.Fit);

        FbxModelVertex vertex = Assert.Single(Assert.Single(result.Surfaces).Vertices);
        Assert.Equal(4, vertex.BoneIndices.Length);
        Assert.Equal(1, result.Report.VerticesTruncatedByInfluenceCap);
        Assert.Equal(0.1, result.Report.LargestDiscardedWeight, 9);
        Assert.Equal(1.0, vertex.BoneWeights.Sum(), 9);
    }

    private static void AssertWeightsNormalized(FbxModelSurface surface)
    {
        foreach (FbxModelVertex vertex in surface.Vertices)
        {
            if (vertex.BoneWeights.IsEmpty)
            {
                continue;
            }

            Assert.Equal(vertex.BoneIndices.Length, vertex.BoneWeights.Length);
            Assert.True(vertex.BoneIndices.Length <= 4);
            Assert.Equal(1.0, vertex.BoneWeights.Sum(), 9);
            Assert.All(
                vertex.BoneIndices,
                slot => Assert.InRange(slot, 0, surface.PaletteBoneIndices.Length - 1));
        }
    }

    private static int Index(RigDefinition rig, string name) => rig.GetBoneIndex(name);

    private sealed record Fixture(
        RigDefinition Source,
        RigConformanceResult Fit,
        FbxModelSurface Surface);

    private static Fixture Build(bool dropExtras)
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

        int forearm = source.GetBoneIndex("CC_Base_L_Forearm");
        int twist = source.GetBoneIndex("CC_Base_L_ForearmTwist01");
        int hand = source.GetBoneIndex("CC_Base_L_Hand");
        var surface = new FbxModelSurface(
            "body",
            "Body",
            Guid.NewGuid(),
            [
                new FbxModelVertex(
                    new Vector3D(0.1, 1.2, 0.0),
                    Vector3D.UnitY,
                    0.0,
                    0.0,
                    [0, 2],
                    [0.75, 0.25]),
                // Split between the forearm and its twist: folding must merge
                // these two influences into one.
                new FbxModelVertex(
                    new Vector3D(0.2, 1.2, 0.0),
                    Vector3D.UnitY,
                    0.0,
                    0.0,
                    [0, 1],
                    [0.6, 0.4]),
            ],
            [0, 1, 2],
            [forearm, twist, hand],
            [TransformMatrix.Identity, TransformMatrix.Identity, TransformMatrix.Identity],
            IsSkinned: true);

        return new Fixture(source, fit, surface);
    }

    private static Dl1RigTemplate CreateTemplate()
    {
        var entities = new List<(string Name, int Parent, Vector3D Offset, BoneKind Kind, bool Deform)>
        {
            ("bip01", -1, new Vector3D(0.0, 0.95, 0.0), BoneKind.Root, true),
            ("pelvis", 0, Vector3D.Zero, BoneKind.Deform, true),
            ("spine", 1, new Vector3D(0.0, 0.10, 0.0), BoneKind.Deform, true),
            ("head", 2, new Vector3D(0.0, 0.50, 0.0), BoneKind.Deform, true),
            ("l_upperarm", 2, new Vector3D(0.20, 0.40, 0.0), BoneKind.Deform, true),
            ("l_forearm", 4, new Vector3D(0.30, 0.0, 0.0), BoneKind.Deform, true),
            ("l_hand", 5, new Vector3D(0.25, 0.0, 0.0), BoneKind.Deform, true),
            ("r_upperarm", 2, new Vector3D(-0.20, 0.40, 0.0), BoneKind.Deform, true),
            ("r_forearm", 7, new Vector3D(-0.30, 0.0, 0.0), BoneKind.Deform, true),
            ("r_hand", 8, new Vector3D(-0.25, 0.0, 0.0), BoneKind.Deform, true),
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
                SemanticRole = Dl1RigDefinitionFactoryRole(name),
            });
        }

        return new Dl1RigTemplate("player", "player_1_tpp", "synthetic", rows.MoveToImmutable());
    }

    private static string? Dl1RigDefinitionFactoryRole(string name) =>
        ReAnimated.DL1.Assets.Meshes.Dl1RigDefinitionFactory.TryResolveSemanticRole(name);

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
