using System.Collections.Immutable;
using ReAnimated.Core.Domain;
using ReAnimated.Core.Mathematics;
using ReAnimated.Core.Project;

namespace ReAnimated.Tests;

public sealed class ProjectMorphSuggestionScorerTests
{
    [Fact]
    public void ExactAndSemanticRowsCanBeAssistedButCompanionRowsCannot()
    {
        RigDefinition rig = CreateRig("target", 0x1000u);
        ProjectMorphBinding exact = CreateBinding("exact_alias", 0.2);
        ProjectMorphBinding semantic = CreateBinding("semantic_alias", 0.92) with
        {
            SourceChannel = "SmileLeft",
        };
        ProjectMorphBinding companion = CreateBinding(
            "blink_lower_lid_companion",
            0.99) with
        {
            SourceChannel = "BlinkLeft",
            Weight = 0.65,
        };

        ImmutableArray<ProjectMorphBinding> result =
            ProjectMorphSuggestionScorer.ApplyAssistedReview(
                [exact, semantic, companion],
                rig);

        Assert.Equal(1.0, result[0].Confidence);
        Assert.Equal(ProjectMappingReviewOrigin.Assisted, result[0].ReviewOrigin);
        Assert.True(result[0].IsLocked);
        Assert.Equal(0.92, result[1].Confidence);
        Assert.Equal(ProjectMappingReviewOrigin.Assisted, result[1].ReviewOrigin);
        Assert.False(result[2].IsReviewed);
        Assert.Equal(ProjectMappingReviewOrigin.None, result[2].ReviewOrigin);
        Assert.Contains("explicit review", result[2].Evidence, StringComparison.Ordinal);
    }

    [Fact]
    public void ScoreOnlyRecordsEvidenceAndDoesNotReviewRows()
    {
        ProjectMorphBinding result = Assert.Single(
            ProjectMorphSuggestionScorer.Score(
                [CreateBinding("exact_alias", 1.0)],
                CreateRig("target", 0x1000u)));

        Assert.False(result.IsReviewed);
        Assert.False(result.IsLocked);
        Assert.Equal(ProjectMappingReviewOrigin.None, result.ReviewOrigin);
        Assert.Equal(ProjectMorphSuggestionScorer.PolicyVersion, result.ScorerVersion);
        Assert.Equal(64, result.EvidenceFingerprint.Length);
        Assert.Contains("unique exact facial alias", result.Evidence, StringComparison.Ordinal);
    }

    [Fact]
    public void ChangedRigInvalidatesAssistedApprovalWhileExplicitReviewSurvives()
    {
        RigDefinition initialRig = CreateRig("target", 0x1000u);
        ProjectMorphBinding assisted = Assert.Single(
            ProjectMorphSuggestionScorer.ApplyAssistedReview(
                [CreateBinding("exact_alias", 1.0)],
                initialRig));
        ProjectMorphBinding explicitReview = assisted with
        {
            ReviewOrigin = ProjectMappingReviewOrigin.Explicit,
        };
        RigDefinition changedRig = CreateRig("target", 0x2000u);

        ProjectMorphBinding invalidated = Assert.Single(
            ProjectMorphSuggestionScorer.RevalidateAssistedApprovals(
                [assisted],
                changedRig));
        ProjectMorphBinding preserved = Assert.Single(
            ProjectMorphSuggestionScorer.RevalidateAssistedApprovals(
                [explicitReview],
                changedRig));

        Assert.False(invalidated.IsReviewed);
        Assert.False(invalidated.IsLocked);
        Assert.Equal(ProjectMappingReviewOrigin.None, invalidated.ReviewOrigin);
        Assert.True(preserved.IsReviewed);
        Assert.True(preserved.IsLocked);
        Assert.Equal(ProjectMappingReviewOrigin.Explicit, preserved.ReviewOrigin);
    }

    private static ProjectMorphBinding CreateBinding(
        string method,
        double confidence) => new()
    {
        SourceChannel = "Smile",
        TargetMorph = "morph_smile",
        TargetDescriptorHash = 0x1000u,
        Method = method,
        Confidence = confidence,
        Evidence = "Awaiting versioned scoring.",
        EvidenceFingerprint = new string('0', 64),
    };

    private static RigDefinition CreateRig(string id, uint descriptor) => new(
        id,
        id,
        [
            new BoneDefinition(
                0,
                "root",
                -1,
                TransformTRS.Identity,
                BoneKind.Root),
        ],
        morphChannels:
        [
            new MorphChannelDefinition(0, "morph_smile", descriptor),
        ]);
}
