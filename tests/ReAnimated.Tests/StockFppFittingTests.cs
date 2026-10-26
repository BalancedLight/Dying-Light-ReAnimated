using System.Collections.Immutable;
using ReAnimated.Codecs.Models;
using ReAnimated.Core.Domain;
using ReAnimated.Core.Mathematics;
using ReAnimated.Core.ModelAuthoring;
using ReAnimated.Retargeting.Conformance;

namespace ReAnimated.Tests;

public sealed class StockFppFittingTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void StockFitHonorsExtraBoneChoiceAndPreservesSkinWeights(bool dropExtras)
    {
        var (model, _) = Dl1RigConformanceApplierTests.BuildModel(dropExtras);
        model = ModelGeometryRevisionCodec.Capture(model);
        Dl1RigTemplate baseline = Dl1RigConformanceApplierTests.CreateTemplate();
        Dl1RigTemplate reference = new("player_fpp", "synthetic_fpp", new string('a', 64),
            baseline.Entities.Add(new Dl1RigTemplateEntity
            {
                Index = baseline.EntityCount, Name = "RefCamera", ParentIndex = 0,
                Kind = BoneKind.Camera, IsDeform = false,
                LocalRestMatrix = TransformMatrix.Identity, GlobalRestMatrix = baseline[0].GlobalRestMatrix,
            }));
        RigCorrespondence correspondence = RigCorrespondenceSolver.Solve(reference, model.Rig!,
            new RigCorrespondenceOptions { DropExtraBones = dropExtras });
        RigLandmarkSolution landmark = RigLandmarkSolver.Solve(reference, model.Rig!, correspondence);
        RigConformanceResult fit = RigConformanceSolver.Solve(reference, model.Rig!, correspondence, landmark);
        var unmatchedWeighted = correspondence.Rows.Where(row => row.TemplateIndex < 0 && row.SourceBoneIndex >= 0 &&
            model.Surfaces.Any(surface => surface.PaletteBoneIndices.Contains(row.SourceBoneIndex))).ToArray();
        Assert.NotEmpty(unmatchedWeighted);

        Dl1RigConformanceApplyResult result = Dl1StockFppModelFitter.ApplyDetailed(model, fit, reference,
            new() { DropExtraBones = dropExtras }, new() { HideHead = false });
        foreach (RigCorrespondenceRow row in unmatchedWeighted)
        {
            Assert.Equal(!dropExtras, result.Model.Package.Document.Bones.Any(bone => bone.Name == row.SourceName));
            Assert.Equal(dropExtras, result.SourceToEffective[row.SourceBoneIndex] < 0);
        }
        foreach (var surface in result.Model.Surfaces)
            foreach (var vertex in surface.Vertices)
            {
                Assert.Equal(1, vertex.BoneWeights.Sum(), 10);
                Assert.All(vertex.BoneIndices, index => Assert.InRange(index, 0, surface.PaletteBoneIndices.Length - 1));
            }
    }

    [Fact]
    public void TorsoRegionsShareOneRigidPlacementInsteadOfStretchingSpineSpacing()
    {
        var source = new Dictionary<string, TransformMatrix>(StringComparer.OrdinalIgnoreCase)
        {
            ["pelvis"] = TransformMatrix.CreateTranslation(new(0, .2, 0)),
            ["spine1"] = TransformMatrix.CreateTranslation(new(0, .5, 0)),
            ["spine2"] = TransformMatrix.CreateTranslation(new(0, .51, 0)),
        };
        var target = new Dictionary<string, TransformMatrix>(StringComparer.OrdinalIgnoreCase)
        {
            ["pelvis"] = TransformMatrix.CreateTranslation(new(0, .9, 0)),
            ["spine1"] = TransformMatrix.CreateTranslation(new(0, 1.1, 0)),
            ["spine2"] = TransformMatrix.CreateTranslation(new(0, 1.3, 0)),
        };
        var result = Dl1StockFppRegionalTransforms.Solve(source, target, source.Keys.ToHashSet(), new(), ImmutableArray.CreateBuilder<string>());
        Assert.Equal(result["pelvis"], result["spine1"]);
        Assert.Equal(result["spine1"], result["spine2"]);
        Assert.Equal(1, result["spine2"].LinearDeterminant, 12);
        Vector3D a = new(.2, .3, -.1), b = new(-.1, .7, .1);
        Assert.Equal((a - b).Length, (result["spine2"].TransformPoint(a) - result["spine1"].TransformPoint(b)).Length, 12);
    }

    [Fact]
    public void NativeFrameSignatureTracksHierarchyAndMatricesWithoutDependingOnSkinIdentity()
    {
        CustomModelBone root = Bone(0, "bip01", -1, new(0, .9, 0));
        CustomModelBone hand = Bone(1, "l_hand", 0, new(.3, .2, 0));
        string signature = Dl1StockFppRigValidation.FrameSignature([root, hand]);
        Assert.Equal(signature, Dl1StockFppRigValidation.FrameSignature([root with { FbxObjectId = 77, IsWeighted = false }, hand with { FbxObjectId = 88 }]));
        Assert.NotEqual(signature, Dl1StockFppRigValidation.FrameSignature([root, hand with { ParentIndex = -1 }]));
        TransformMatrix changed = hand.ExactLocalBindMatrix with { M14 = .31 };
        Assert.NotEqual(signature, Dl1StockFppRigValidation.FrameSignature([root, hand with { ExactLocalBindMatrix = changed, LocalBindTransform = changed.Decompose() }]));
    }

    private static CustomModelBone Bone(int index, string name, int parent, Vector3D offset)
    {
        TransformMatrix local = TransformMatrix.CreateTranslation(offset);
        return new()
        {
            Index = index,
            Name = name,
            ParentIndex = parent,
            Kind = index == 0 ? BoneKind.Root : BoneKind.Deform,
            IsWeighted = true,
            ExactLocalBindMatrix = local,
            LocalBindTransform = local.Decompose()
        };
    }
}
