using System.Collections.Immutable;
using ReAnimated.Codecs.Fbx;
using ReAnimated.Core.Mathematics;
using ReAnimated.Core.ModelAuthoring;

namespace ReAnimated.Tests;

public sealed class MorphDeformationTransferTests
{
    [Fact]
    [Trait("ValidationTier", "Hermetic")]
    public void ExactTopologyPreservesNamesDescriptorsAndDeltas()
    {
        MorphReferenceProfile source = Profile("a", [new(0, 0, 0), new(1, 0, 0), new(0, 1, 0)]);
        MorphReferenceProfile target = Profile("b", [new(0, 0, 0), new(1, 0, 0), new(0, 1, 0)]);
        MorphExpressionProposal proposal = MorphDeformationTransfer.Propose(
            source, target, "face_smile",
            new Dictionary<string, ImmutableArray<ImmutableArray<Vector3D>>>
            {
                ["face_smile"] = [[new(0.1, 0, 0), new(0, 0.2, 0), new(0, 0, 0.3)]],
            });

        Assert.Equal(MorphTransferMethod.ExactTopology, proposal.Method);
        Assert.Equal("face_smile", proposal.TargetExpressionName);
        Assert.Equal(new Vector3D(0, 0.2, 0), proposal.PositionDeltas[0][1]);
        Assert.Equal(0x1234u, proposal.SourceDescriptorHash);
    }

    [Fact]
    [Trait("ValidationTier", "Hermetic")]
    public void ExactTopologyHonorsReviewedRegionAndLockedCorrespondences()
    {
        ImmutableArray<Vector3D> neutral = [new(0, 0, 0), new(1, 0, 0), new(0, 1, 0), new(1, 1, 0)];
        MorphReferenceProfile source = Profile("source", neutral, 0, 1, 2, 1, 3, 2);
        MorphReferenceProfile target = Profile("target", neutral, 0, 1, 2, 1, 3, 2);
        MorphExpressionProposal proposal = MorphDeformationTransfer.Propose(source, target, "face_smile",
            new Dictionary<string, ImmutableArray<ImmutableArray<Vector3D>>>
            { ["face_smile"] = [[new(1, 0, 0), new(1, 0, 0), new(1, 0, 0), new(1, 0, 0)]] },
            new MorphTransferOptions
            {
                TargetVertexMask = [0, 1, 2],
                LockedTargetVertices = [1],
                Correspondences = [new("source", 2, "target", 2, Locked: true)],
                TriangleCorrespondences = [new("source", 1, "target", 1, Locked: true)],
            });

        Assert.Equal(MorphTransferMethod.ExactTopology, proposal.Method);
        Assert.Equal(new Vector3D(1, 0, 0), proposal.PositionDeltas[0][0]);
        Assert.All(proposal.PositionDeltas[0].Skip(1), delta => Assert.Equal(Vector3D.Zero, delta));
        Assert.True(proposal.RequiresExplicitReview);
    }

    [Fact]
    [Trait("ValidationTier", "Hermetic")]
    public void DifferentTopologyUsesReviewedCorrespondencesAndMasksLockedVertices()
    {
        MorphReferenceProfile source = Profile("source", [new(0, 0, 0), new(1, 0, 0), new(0, 1, 0)]);
        MorphReferenceProfile target = Profile("target", [new(0, 0, 0), new(1, 0, 0), new(0, 1, 0), new(1, 1, 0)], [0, 1, 2, 1, 3, 2]);
        var correspondences = Enumerable.Range(0, 3).Select(i => new MorphVertexCorrespondence("source", i, "target", i)).ToImmutableArray();
        MorphExpressionProposal proposal = MorphDeformationTransfer.Propose(source, target, "face_smile",
            new Dictionary<string, ImmutableArray<ImmutableArray<Vector3D>>> { ["face_smile"] = [[new(1, 0, 0), new(0, 2, 0), new(0, 0, 3)]] },
            new MorphTransferOptions { Method = MorphTransferMethod.CorrespondenceDeformationGradient, Correspondences = correspondences, LockedTargetVertices = [1] });

        Assert.Equal(MorphTransferMethod.CorrespondenceDeformationGradient, proposal.Method);
        Assert.Equal(Vector3D.Zero, proposal.PositionDeltas[0][1]);
        Assert.Equal(new Vector3D(1, 0, 0), proposal.PositionDeltas[0][0]);
        Assert.True(proposal.RequiresExplicitReview);
    }

    [Fact]
    [Trait("ValidationTier", "Hermetic")]
    public void ZeroExpressionDoesNotReshapeDifferentTargetNeutral()
    {
        MorphReferenceProfile source = Profile("source", [new(0, 0, 0), new(1, 0, 0), new(0, 1, 0)]);
        MorphReferenceProfile target = Profile("target", [new(10, 2, 0), new(12, 2, 0), new(10, 5, 0), new(12, 5, 0)], [0, 1, 2, 1, 3, 2]);
        MorphExpressionProposal proposal = MorphDeformationTransfer.Propose(source, target, "face_smile",
            new Dictionary<string, ImmutableArray<ImmutableArray<Vector3D>>> { ["face_smile"] = [[Vector3D.Zero, Vector3D.Zero, Vector3D.Zero]] },
            new MorphTransferOptions
            {
                Method = MorphTransferMethod.CorrespondenceDeformationGradient,
                TriangleCorrespondences = [new("source", 0, "target", 0), new("source", 0, "target", 1)],
            });
        Assert.All(proposal.PositionDeltas.SelectMany(static x => x), static delta => Assert.Equal(Vector3D.Zero, delta));
    }

    [Fact]
    [Trait("ValidationTier", "Hermetic")]
    public void TriangleSolveKeepsOutsideRegionFixedAcrossOverlappingConstraints()
    {
        MorphReferenceProfile source = Profile("source", [new(0, 0, 0), new(1, 0, 0), new(0, 1, 0)]);
        MorphReferenceProfile target = Profile("target", [new(0, 0, 0), new(1, 0, 0), new(0, 1, 0), new(1, 1, 0)], 0, 1, 2, 1, 3, 2);
        MorphExpressionProposal proposal = MorphDeformationTransfer.Propose(source, target, "face_smile",
            new Dictionary<string, ImmutableArray<ImmutableArray<Vector3D>>>
            { ["face_smile"] = [[new(1, 0, 0), new(1, 0, 0), new(1, 0, 0)]] },
            new MorphTransferOptions
            {
                Method = MorphTransferMethod.CorrespondenceDeformationGradient,
                TargetVertexMask = [0, 1, 2],
                TriangleCorrespondences = [new("source", 0, "target", 0), new("source", 0, "target", 1)],
            });

        Assert.Equal(Vector3D.Zero, proposal.PositionDeltas[0][3]);
        Assert.InRange(proposal.PositionDeltas[0][0].X, 0.1, 1.1);
    }

    [Fact]
    [Trait("ValidationTier", "Hermetic")]
    public void LockedMatchedVertexAndTriangleRemainFixedDuringTriangleSolve()
    {
        MorphReferenceProfile source = Profile("source", [new(0, 0, 0), new(1, 0, 0), new(0, 1, 0)]);
        MorphReferenceProfile target = Profile("target", [new(0, 0, 0), new(1, 0, 0), new(0, 1, 0), new(1, 1, 0)], 0, 1, 2, 1, 3, 2);
        MorphExpressionProposal proposal = MorphDeformationTransfer.Propose(source, target, "face_smile",
            new Dictionary<string, ImmutableArray<ImmutableArray<Vector3D>>>
            { ["face_smile"] = [[new(1, 0, 0), new(1, 0, 0), new(1, 0, 0)]] },
            new MorphTransferOptions
            {
                Method = MorphTransferMethod.CorrespondenceDeformationGradient,
                Correspondences = [new("source", 1, "target", 1, Locked: true)],
                TriangleCorrespondences = [new("source", 0, "target", 0), new("source", 0, "target", 1, Locked: true)],
            });

        Assert.Equal(Vector3D.Zero, proposal.PositionDeltas[0][1]);
        Assert.Equal(Vector3D.Zero, proposal.PositionDeltas[0][2]);
        Assert.Equal(Vector3D.Zero, proposal.PositionDeltas[0][3]);
        Assert.InRange(proposal.PositionDeltas[0][0].X, 0.1, 1.1);
    }

    [Fact]
    [Trait("ValidationTier", "Hermetic")]
    public void SameTopologyCanRequestGradientForDifferentNeutralAnatomy()
    {
        MorphReferenceProfile source = Profile("source", [new(0, 0, 0), new(1, 0, 0), new(0, 1, 0)]);
        MorphReferenceProfile target = Profile("target", [new(10, 5, 0), new(12, 5, 0), new(10, 8, 0)]);
        MorphExpressionProposal proposal = MorphDeformationTransfer.Propose(source, target, "face_smile",
            new Dictionary<string, ImmutableArray<ImmutableArray<Vector3D>>>
            { ["face_smile"] = [[Vector3D.Zero, new(1, 0, 0), Vector3D.Zero]] },
            new MorphTransferOptions
            {
                Method = MorphTransferMethod.CorrespondenceDeformationGradient,
                TriangleCorrespondences = [new("source", 0, "target", 0)],
            });

        Assert.Equal(MorphTransferMethod.CorrespondenceDeformationGradient, proposal.Method);
        Assert.InRange(proposal.PositionDeltas[0][1].X, 1.999, 2.001);
        Assert.True(proposal.RequiresExplicitReview);
    }

    [Fact]
    [Trait("ValidationTier", "Hermetic")]
    public void GradientTransfersRigidRotationAcrossDifferentNeutralScale()
    {
        MorphReferenceProfile source = Profile("source", [new(0, 0, 0), new(1, 0, 0), new(0, 1, 0)]);
        MorphReferenceProfile target = Profile("target", [new(10, 5, 0), new(12, 5, 0), new(10, 7, 0)]);
        MorphExpressionProposal proposal = MorphDeformationTransfer.Propose(source, target, "face_smile",
            new Dictionary<string, ImmutableArray<ImmutableArray<Vector3D>>>
            { ["face_smile"] = [[Vector3D.Zero, new(-1, 1, 0), new(-1, -1, 0)]] },
            new MorphTransferOptions
            {
                Method = MorphTransferMethod.CorrespondenceDeformationGradient,
                TriangleCorrespondences = [new("source", 0, "target", 0)],
            });

        Assert.InRange(proposal.PositionDeltas[0][1].X, -2.001, -1.999);
        Assert.InRange(proposal.PositionDeltas[0][1].Y, 1.999, 2.001);
        Assert.InRange(proposal.PositionDeltas[0][2].X, -2.001, -1.999);
        Assert.InRange(proposal.PositionDeltas[0][2].Y, -2.001, -1.999);
    }

    [Fact]
    [Trait("ValidationTier", "Hermetic")]
    public void InvalidCorrespondenceIndicesAndWeightsFailBeforeExactCopy()
    {
        MorphReferenceProfile source = Profile("source", [new(0, 0, 0), new(1, 0, 0), new(0, 1, 0)]);
        MorphReferenceProfile target = Profile("target", [new(0, 0, 0), new(1, 0, 0), new(0, 1, 0)]);
        var deltas = new Dictionary<string, ImmutableArray<ImmutableArray<Vector3D>>>
        { ["face_smile"] = [[new(1, 0, 0), Vector3D.Zero, Vector3D.Zero]] };

        Assert.Throws<InvalidDataException>(() => MorphDeformationTransfer.Propose(source, target, "face_smile", deltas,
            new MorphTransferOptions { Correspondences = [new("source", 0, "target", 3)] }));
        Assert.Throws<InvalidDataException>(() => MorphDeformationTransfer.Propose(source, target, "face_smile", deltas,
            new MorphTransferOptions { Correspondences = [new("source", 0, "target", 0, double.NaN)] }));
        Assert.Throws<InvalidDataException>(() => MorphDeformationTransfer.Propose(source, target, "face_smile", deltas,
            new MorphTransferOptions { TriangleCorrespondences = [new("source", 0, "target", 1)] }));
        Assert.Throws<InvalidDataException>(() => MorphDeformationTransfer.Propose(source, target, "face_smile", deltas,
            new MorphTransferOptions { TriangleCorrespondences = [new("source", 0, "target", 0, 0)] }));
    }

    [Theory]
    [InlineData(2.0, 0.0, 0.0)]
    [InlineData(1.0, 1.0, 0.0)]
    [Trait("ValidationTier", "Hermetic")]
    public void TriangleGradientCarriesTranslationAndScale(double scale, double tx, double ty)
    {
        MorphReferenceProfile source = Profile("source", [new(0, 0, 0), new(1, 0, 0), new(0, 1, 0)]);
        MorphReferenceProfile target = Profile("target", [new(0, 0, 0), new(1, 0, 0), new(0, 1, 0), new(1, 1, 0)], [0, 1, 2, 1, 3, 2]);
        ImmutableArray<Vector3D> neutral = [new(0, 0, 0), new(1, 0, 0), new(0, 1, 0)];
        ImmutableArray<Vector3D> deformed = neutral.Select(p => new Vector3D(scale * p.X + tx, scale * p.Y + ty, p.Z)).ToImmutableArray();
        ImmutableArray<Vector3D> deltas = deformed.Zip(neutral).Select(p => p.First - p.Second).ToImmutableArray();
        MorphExpressionProposal proposal = MorphDeformationTransfer.Propose(source, target, "face_smile",
            new Dictionary<string, ImmutableArray<ImmutableArray<Vector3D>>> { ["face_smile"] = [deltas] },
            new MorphTransferOptions { Method = MorphTransferMethod.CorrespondenceDeformationGradient,
                TriangleCorrespondences = [new("source", 0, "target", 0)] });
        Assert.InRange(proposal.PositionDeltas[0][0].X, tx - 1e-6, tx + 1e-6);
        Assert.InRange(proposal.PositionDeltas[0][1].X, tx + scale - 1 - 1e-6, tx + scale - 1 + 1e-6);
    }

    [Fact]
    [Trait("ValidationTier", "Hermetic")]
    public void AugmentedNormalCarriesUniformScaleIntoOutOfPlaneTarget()
    {
        MorphReferenceProfile source = Profile("source", [new(0, 0, 0), new(1, 0, 0), new(0, 1, 0)]);
        MorphReferenceProfile target = Profile("target", [new(10, 2, 3), new(11, 2, 3), new(10, 3, 3)]);
        ImmutableArray<Vector3D> neutral = [new(0, 0, 0), new(1, 0, 0), new(0, 1, 0)];
        ImmutableArray<Vector3D> deltas = neutral.Select(p => p).ToImmutableArray();
        MorphExpressionProposal proposal = MorphDeformationTransfer.Propose(source, target, "face_smile",
            new Dictionary<string, ImmutableArray<ImmutableArray<Vector3D>>> { ["face_smile"] = [deltas] },
            new MorphTransferOptions { Method = MorphTransferMethod.CorrespondenceDeformationGradient,
                TriangleCorrespondences = [new("source", 0, "target", 0)] });
        Assert.InRange(proposal.PositionDeltas[0][2].Y, 0.999, 1.001);
        Assert.InRange(proposal.PositionDeltas[0][2].Z, -0.001, 0.001);
    }

    [Fact]
    [Trait("ValidationTier", "Hermetic")]
    public void StaleTopologyAndUnreviewedReplacementFailClosed()
    {
        MorphReferenceProfile source = Profile("source", [new(0, 0, 0), new(1, 0, 0), new(0, 1, 0)]);
        MorphReferenceProfile target = Profile("target", [new(0, 0, 0), new(1, 0, 0), new(0, 1, 0)]);
        Assert.Throws<InvalidDataException>(() => MorphDeformationTransfer.Propose(
            source, target, "missing", new Dictionary<string, ImmutableArray<ImmutableArray<Vector3D>>>()));

        MorphExpressionProposal proposal = MorphDeformationTransfer.Propose(source, target, "face_smile",
            new Dictionary<string, ImmutableArray<ImmutableArray<Vector3D>>> { ["face_smile"] = [[new(1, 0, 0), new(0, 1, 0), new(0, 0, 1)]] });
        Assert.Throws<InvalidOperationException>(() => MorphDeformationTransfer.Apply(proposal, source, target, proposal, MorphTransferConflict.Reject));
        MorphExpressionProposal reviewed = proposal with { IsReviewed = true };
        Assert.Same(proposal, MorphDeformationTransfer.Apply(reviewed, source, target, proposal, MorphTransferConflict.KeepExisting));
    }

    private static MorphReferenceProfile Profile(string id, ImmutableArray<Vector3D> positions, params int[] indices)
    {
        var surface = new MorphReferenceSurface(id, id, 0, positions, indices.Length == 0 ? [0, 1, 2] : indices.ToImmutableArray());
        return MorphDeformationTransfer.CreateProfile(new string('a', 64), null,
            [new MorphReferenceChannel(0, "face_smile", 0x1234)], [surface]);
    }
}

