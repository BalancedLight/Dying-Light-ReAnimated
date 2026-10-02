using ReAnimated.App.ViewModels;
using ReAnimated.Core.Domain;

namespace ReAnimated.Tests;

public sealed class GuidedExtraBonePreservationTests
{
    [Fact]
    [Trait("ValidationTier", "Hermetic")]
    [Trait("Gate", "EditorUsability")]
    public void BulkDefaultsReviewHundredsOfEligibleWeightedAndUnweightedRows()
    {
        TargetBindReviewViewModel[] rows = Enumerable.Range(0, 240)
            .Select(index => new TargetBindReviewViewModel(
                targetBoneIndex: index,
                targetBone: $"extra_{index:D3}",
                boneKind: index % 2 == 0 ? BoneKind.Deform : BoneKind.Prop,
                isReviewed: false,
                contextSummary: index % 2 == 0
                    ? "Weighted extra bone."
                    : "Unweighted extra bone.",
                canReview: true,
                canPreserveWithParent: true))
            .ToArray();

        MainWindowViewModel.ApplyGuidedExtraBonePreservationDefaults(rows);

        Assert.All(rows, row => Assert.True(row.IsReviewed));
        Assert.Equal(240, rows.Count(static row => row.IsReviewed));
    }

    [Fact]
    [Trait("ValidationTier", "Hermetic")]
    [Trait("Gate", "EditorUsability")]
    public void BulkDefaultsLeaveBlockedAndDeclarationRowsUnreviewed()
    {
        TargetBindReviewViewModel[] rows =
        [
            Review(1, "ordinary_weighted", BoneKind.Deform),
            Review(2, "ordinary_unweighted", BoneKind.Prop),
            new TargetBindReviewViewModel(
                targetBoneIndex: 3,
                targetBone: "blocked_extra",
                boneKind: BoneKind.Deform,
                isReviewed: false,
                contextSummary: "Blocked by an unresolved parent.",
                canReview: false,
                canPreserveWithParent: true),
            new TargetBindReviewViewModel(
                targetBoneIndex: 4,
                targetBone: "declared_extra",
                boneKind: BoneKind.Prop,
                isReviewed: false,
                contextSummary: "Declaration requires an explicit choice.",
                canReview: true,
                canPreserveWithParent: false),
        ];

        MainWindowViewModel.ApplyGuidedExtraBonePreservationDefaults(rows);

        Assert.True(rows[0].IsReviewed);
        Assert.True(rows[1].IsReviewed);
        Assert.False(rows[2].IsReviewed);
        Assert.False(rows[3].IsReviewed);
    }

    [Fact]
    [Trait("ValidationTier", "Hermetic")]
    [Trait("Gate", "EditorUsability")]
    public void BulkDefaultsDoNotApproveRowsThatCannotBeMappedOrPreserved()
    {
        TargetBindReviewViewModel[] rows =
        [
            new TargetBindReviewViewModel(
                targetBoneIndex: 10,
                targetBone: "unmapped_extra",
                boneKind: BoneKind.Deform,
                isReviewed: false,
                contextSummary: "No mapped parent is available.",
                canReview: false,
                canPreserveWithParent: false),
            new TargetBindReviewViewModel(
                targetBoneIndex: 11,
                targetBone: "explicit_only_extra",
                boneKind: BoneKind.Prop,
                isReviewed: false,
                contextSummary: "Requires an explicit review.",
                canReview: true,
                canPreserveWithParent: false),
        ];

        MainWindowViewModel.ApplyGuidedExtraBonePreservationDefaults(rows);

        Assert.All(rows, row => Assert.False(row.IsReviewed));
    }

    private static TargetBindReviewViewModel Review(
        int index,
        string name,
        BoneKind kind) =>
        new(
            targetBoneIndex: index,
            targetBone: name,
            boneKind: kind,
            isReviewed: false,
            canReview: true,
            canPreserveWithParent: true);
}
