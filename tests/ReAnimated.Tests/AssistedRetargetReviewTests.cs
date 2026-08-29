using ReAnimated.Core.Domain;
using ReAnimated.Core.Mathematics;
using ReAnimated.Retargeting.Mapping;

namespace ReAnimated.Tests;

public sealed class AssistedRetargetReviewTests
{
    [Fact]
    public void NormalizedNameWithMappedParentCarriesExplainableAssistedApproval()
    {
        RigDefinition source = Rig(
            "source",
            ("Root", -1, null),
            ("mixamorig:Spine", 0, null));
        RigDefinition target = Rig(
            "target",
            ("Root", -1, null),
            ("Spine", 0, null));

        RetargetMap reviewed = RetargetSuggestionScorer.ApplyAssistedReview(
            source,
            target,
            RetargetMapBuilder.CreateSuggested(source, target));

        BoneMapEntry spine = Assert.Single(
            reviewed.Entries,
            static row => row.TargetBoneIndex == 1);
        Assert.Equal(BoneMappingMethod.NormalizedName, spine.Method);
        Assert.Equal(0.95, spine.Confidence, 10);
        Assert.Equal(MappingReviewOrigin.Assisted, spine.ReviewOrigin);
        Assert.True(spine.IsReviewed);
        Assert.True(spine.IsLocked);
        Assert.Equal(
            RetargetSuggestionScorer.PolicyVersion,
            spine.ScorerVersion);
        Assert.Equal(64, spine.EvidenceFingerprint.Length);
        Assert.Contains(
            spine.Evidence,
            static row => row.Kind == MappingEvidenceKind.ParentChainAgreement);
    }

    [Fact]
    public void InferredHumanoidRoleReachesPointNineOnlyWithCorroboration()
    {
        RigDefinition source = Rig(
            "source",
            ("Root", -1, null),
            ("LeftArm", 0, null));
        RigDefinition target = Rig(
            "target",
            ("Root", -1, null),
            ("l_upperarm", 0, null));

        RetargetMap reviewed = RetargetSuggestionScorer.ApplyAssistedReview(
            source,
            target,
            RetargetMapBuilder.CreateSuggested(source, target));

        BoneMapEntry arm = Assert.Single(
            reviewed.Entries,
            static row => row.TargetBoneIndex == 1);
        Assert.Equal(BoneMappingMethod.Semantic, arm.Method);
        Assert.Equal(0.90, arm.Confidence, 10);
        Assert.Equal(MappingReviewOrigin.Assisted, arm.ReviewOrigin);
        Assert.Contains(
            arm.Evidence,
            static row => row.Kind == MappingEvidenceKind.SideAgreement);
        Assert.Contains(
            arm.Evidence,
            static row => row.Kind == MappingEvidenceKind.TransferPolicyAgreement);
    }

    [Fact]
    public void StructuralSuggestionsNeverReceiveAssistedApproval()
    {
        RigDefinition source = Rig(
            "source",
            ("source_root", -1, null),
            ("source_unknown", 0, null));
        RigDefinition target = Rig(
            "target",
            ("target_root", -1, null),
            ("target_unknown", 0, null));

        RetargetMap reviewed = RetargetSuggestionScorer.ApplyAssistedReview(
            source,
            target,
            RetargetMapBuilder.CreateSuggested(source, target));

        Assert.All(
            reviewed.Entries.Where(static row =>
                row.Method == BoneMappingMethod.Structural),
            static row =>
            {
                Assert.Equal(MappingReviewOrigin.None, row.ReviewOrigin);
                Assert.False(row.IsReviewed);
                Assert.False(row.IsLocked);
                Assert.True(row.Confidence < 0.90);
            });
    }

    [Fact]
    public void ChangedRigEvidenceInvalidatesAssistedApproval()
    {
        RigDefinition source = Rig(
            "source",
            ("Root", -1, null),
            ("mixamorig:Spine", 0, null));
        RigDefinition originalTarget = Rig(
            "target",
            ("Root", -1, null),
            ("Spine", 0, null));
        RetargetMap reviewed = RetargetSuggestionScorer.ApplyAssistedReview(
            source,
            originalTarget,
            RetargetMapBuilder.CreateSuggested(source, originalTarget));
        RigDefinition changedTarget = new(
            originalTarget.Id,
            originalTarget.DisplayName,
            [
                new BoneDefinition(
                    0,
                    "Root",
                    -1,
                    TransformTRS.Identity,
                    BoneKind.Root),
                new BoneDefinition(
                    1,
                    "Spine",
                    0,
                    new TransformTRS(
                        new Vector3D(0, 0.25, 0),
                        QuaternionD.Identity,
                        Vector3D.One)),
            ]);

        RetargetMap revalidated =
            RetargetSuggestionScorer.RevalidateAssistedApprovals(
                source,
                changedTarget,
                reviewed);

        BoneMapEntry spine = Assert.Single(
            revalidated.Entries,
            static row => row.TargetBoneIndex == 1);
        Assert.Equal(MappingReviewOrigin.None, spine.ReviewOrigin);
        Assert.False(spine.IsReviewed);
        Assert.False(spine.IsLocked);
    }

    [Fact]
    public void ClaimedExactIdentityIsRejectedWhenNamesAreAmbiguous()
    {
        RigDefinition source = Rig(
            "source",
            ("source_root", -1, null),
            ("duplicate", 0, null),
            ("duplicate", 0, null));
        RigDefinition target = Rig(
            "target",
            ("target_root", -1, null),
            ("duplicate", 0, null),
            ("duplicate", 0, null));
        var claimedExact = new RetargetMap(
            source.Id,
            target.Id,
            [
                new BoneMapEntry(
                    1,
                    1,
                    BoneMappingMethod.ExactName,
                    1.0),
            ]);

        BoneMapEntry result = Assert.Single(
            RetargetSuggestionScorer.ApplyAssistedReview(
                source,
                target,
                claimedExact).Entries);

        Assert.Equal(0.89, result.Confidence, 10);
        Assert.Equal(MappingReviewOrigin.None, result.ReviewOrigin);
        Assert.False(result.IsReviewed);
        Assert.False(result.IsLocked);
        Assert.DoesNotContain(
            result.Evidence,
            static row => row.Kind == MappingEvidenceKind.ExactName);
        RetargetMappingReviewReport exportReview =
            RetargetMappingReview.Analyze(
                source,
                target,
                claimedExact);
        Assert.False(exportReview.IsReady);
        Assert.Contains(
            exportReview.Diagnostics,
            static diagnostic =>
                diagnostic.Code ==
                    "deterministic_mapping_identity_mismatch");
    }

    [Fact]
    public void CcBodyHelperArmClearsAssistedThresholdButParentlessThighDoesNot()
    {
        RigDefinition source = Rig(
            "source",
            ("Armature", -1, null),
            ("mixamorig:Hips", 0, null),
            ("mixamorig:LeftUpLeg", 1, null),
            ("mixamorig:Spine", 1, null),
            ("mixamorig:LeftShoulder", 3, null),
            ("mixamorig:LeftArm", 4, null));
        RigDefinition target = new(
            "target",
            "target",
            [
                new BoneDefinition(0, "CC_Base_BoneRoot", -1, TransformTRS.Identity, BoneKind.Root),
                new BoneDefinition(1, "CC_Base_Hip", 0, TransformTRS.Identity, BoneKind.Helper),
                new BoneDefinition(2, "CC_Base_Pelvis", 1, TransformTRS.Identity, BoneKind.Helper),
                new BoneDefinition(3, "CC_Base_L_Thigh", 2, TransformTRS.Identity, BoneKind.Helper),
                new BoneDefinition(4, "CC_Base_L_ThighTwist01", 3, TransformTRS.Identity, BoneKind.Deform),
                new BoneDefinition(5, "CC_Base_Waist", 2, TransformTRS.Identity, BoneKind.Deform),
                new BoneDefinition(6, "CC_Base_L_Clavicle", 5, TransformTRS.Identity, BoneKind.Deform),
                new BoneDefinition(7, "CC_Base_L_Upperarm", 6, TransformTRS.Identity, BoneKind.Helper),
                new BoneDefinition(8, "CC_Base_L_UpperarmTwist01", 7, TransformTRS.Identity, BoneKind.Deform),
            ]);

        RetargetMap reviewed = RetargetSuggestionScorer.ApplyAssistedReview(
            source,
            target,
            RetargetMapBuilder.CreateSuggested(source, target));
        BoneMapEntry arm = Assert.Single(
            reviewed.Entries,
            static row => row.TargetBoneIndex == 7);
        BoneMapEntry thigh = Assert.Single(
            reviewed.Entries,
            static row => row.TargetBoneIndex == 3);

        Assert.Equal(0.90, arm.Confidence, 10);
        Assert.Equal(MappingReviewOrigin.Assisted, arm.ReviewOrigin);
        Assert.Equal(0.82, thigh.Confidence, 10);
        Assert.Equal(MappingReviewOrigin.None, thigh.ReviewOrigin);
    }

    private static RigDefinition Rig(
        string id,
        params (string Name, int Parent, string? Role)[] rows) =>
        new(
            id,
            id,
            rows.Select((row, index) => new BoneDefinition(
                index,
                row.Name,
                row.Parent,
                TransformTRS.Identity,
                row.Parent < 0 ? BoneKind.Root : BoneKind.Deform,
                semanticRole: row.Role)));
}
