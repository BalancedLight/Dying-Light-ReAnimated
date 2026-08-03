using ReAnimated.App.ViewModels;
using ReAnimated.Core.Domain;
using ReAnimated.Core.Mathematics;
using ReAnimated.Core.Project;
using ReAnimated.Retargeting;
using ReAnimated.Retargeting.Mapping;

namespace ReAnimated.Tests;

public sealed class RetargetCompatibilityTests
{
    [Theory]
    [InlineData(
        "RefCamera",
        RetargetComponentPolicy.Translation)]
    [InlineData(
        "EyeCamera",
        RetargetComponentPolicy.RotationTranslation)]
    [InlineData(
        "LForeTwist",
        RetargetComponentPolicy.Rotation)]
    [InlineData(
        "custom_socket",
        RetargetComponentPolicy.FullTransform)]
    public void HelperOverrideDefaultsMatchDl1Profiles(
        string targetName,
        RetargetComponentPolicy expected)
    {
        Assert.Equal(
            expected,
            MainWindowViewModel
                .DefaultHelperComponentPolicy(
                    targetName));
    }

    [Fact]
    public void MappingEditsRetainOnlyUnmappedTargetBindReviews()
    {
        ProjectTargetBindReview first = new()
        {
            TargetBoneIndex = 3,
            TargetBoneName = "unrelated_helper",
        };
        ProjectTargetBindReview nowMapped = new()
        {
            TargetBoneIndex = 7,
            TargetBoneName = "EyeCamera",
        };

        var retained =
            MainWindowViewModel
                .RetainUnmappedTargetBindReviews(
                    [first, nowMapped],
                    [1, 7]);

        Assert.Equal(
            first,
            Assert.Single(retained));
    }

    [Fact]
    public void ExplicitReviewSelectionsPreserveUncheckedRowsAndOnlyAcceptCheckedBindFallbacks()
    {
        RigDefinition source = CreateRig(
            "source",
            ("root", -1, true),
            ("source_arm", 0, true));
        RigDefinition target = CreateRig(
            "target",
            ("root", -1, true),
            ("target_arm", 0, true),
            ("required_socket_a", 0, true),
            ("required_socket_b", 0, true),
            ("optional_socket", 0, false));
        RetargetMap mapping = new(
            source.Id,
            target.Id,
            [
                new BoneMapEntry(
                    0,
                    0,
                    BoneMappingMethod.ExactName,
                    1.0),
                new BoneMapEntry(
                    1,
                    1,
                    BoneMappingMethod.Semantic,
                    0.9),
            ],
            reviewedTargetBindBoneIndices: [3, 4]);
        BoneMappingViewModel[] mappingRows =
        [
            new(
                "root",
                "root",
                1.0,
                BoneMappingMethod.ExactName.ToString(),
                isReviewed: false),
            new(
                "source_arm",
                "target_arm",
                0.9,
                BoneMappingMethod.Semantic.ToString(),
                isReviewed: true),
        ];
        TargetBindReviewViewModel[] bindRows =
        [
            new(
                2,
                "required_socket_a",
                BoneKind.Deform,
                isReviewed: true),
            new(
                3,
                "required_socket_b",
                BoneKind.Deform,
                isReviewed: false),
        ];

        RetargetMap reviewed =
            MainWindowViewModel.ApplyExplicitReviewSelections(
                source,
                target,
                mapping,
                mappingRows,
                bindRows);

        Assert.False(
            Assert.Single(
                reviewed.Entries,
                entry => entry.TargetBoneIndex == 0)
                .IsReviewed);
        Assert.True(
            Assert.Single(
                reviewed.Entries,
                entry => entry.TargetBoneIndex == 1)
                .IsReviewed);
        Assert.Equal(
            [2, 4],
            reviewed.ReviewedTargetBindBoneIndices
                .Order()
                .ToArray());
        RetargetMappingReviewReport report =
            RetargetMappingReview.Analyze(
                source,
                target,
                reviewed);
        Assert.False(report.IsReady);
        Assert.Equal(0, report.ExplicitReviewRequiredCount);
        Assert.Equal(1, report.RequiredTargetBindReviewCount);
        Assert.Contains(
            report.Diagnostics,
            diagnostic =>
                diagnostic.Code == "required_target_unmapped" &&
                diagnostic.TargetBoneName ==
                    "required_socket_b");
    }

    [Fact]
    public void ExplicitReviewSelectionsFailClosedWhenARequiredBindRowIsHidden()
    {
        RigDefinition source =
            CreateRig("source", ("root", -1, true));
        RigDefinition target = CreateRig(
            "target",
            ("root", -1, true),
            ("required_socket_a", 0, true),
            ("required_socket_b", 0, true));
        RetargetMap mapping = new(
            source.Id,
            target.Id,
            [
                new BoneMapEntry(
                    0,
                    0,
                    BoneMappingMethod.ExactName,
                    1.0),
            ]);

        InvalidOperationException exception = Assert.Throws<
            InvalidOperationException>(() =>
            MainWindowViewModel.ApplyExplicitReviewSelections(
                source,
                target,
                mapping,
                [
                    new BoneMappingViewModel(
                        "root",
                        "root",
                        1.0,
                        BoneMappingMethod.ExactName.ToString()),
                ],
                [
                    new TargetBindReviewViewModel(
                        1,
                        "required_socket_a",
                        BoneKind.Deform,
                        isReviewed: true),
                ]));

        Assert.Contains(
            "Every required unmapped target bone",
            exception.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void NameMapNormalizesFbxNamespaceAndReportsExactContract()
    {
        RigDefinition source = CreateRig(
            "source",
            ("mixamorig:Hips", -1, true),
            ("Spine", 0, true));
        RigDefinition target = CreateRig(
            "target",
            ("Hips", -1, true),
            ("Spine", 0, true));

        RetargetMap map = RetargetMapBuilder.CreateNameBased(source, target);
        CompatibilityReport report = RigCompatibilityAnalyzer.Analyze(source, target, map);

        Assert.Equal(2, map.Entries.Length);
        Assert.Equal(BoneMappingMethod.NormalizedName, map.Entries[0].Method);
        Assert.Equal(BoneMappingMethod.ExactName, map.Entries[1].Method);
        Assert.Equal(CompatibilityClassification.Retargetable, report.Classification);
        Assert.True(report.CanEvaluate);
    }

    [Fact]
    public void NameMapLeavesDuplicateRowsForManualIndexedReview()
    {
        RigDefinition source = CreateRig(
            "source",
            ("root", -1, true),
            ("hook", 0, false),
            ("hook", 0, false));
        RigDefinition target = CreateRig(
            "target",
            ("root", -1, true),
            ("hook", 0, false),
            ("hook", 0, false));

        RetargetMap map =
            RetargetMapBuilder.CreateNameBased(source, target);

        BoneMapEntry root = Assert.Single(map.Entries);
        Assert.Equal((0, 0), (
            root.SourceBoneIndex,
            root.TargetBoneIndex));
        Assert.Equal(-1, source.GetBoneIndex("hook"));
        Assert.Equal(
            [1, 2],
            source.GetBoneIndices("hook").ToArray());
    }

    [Fact]
    public void SuggestedMapUsesHashThenNameThenSemanticThenReviewableStructure()
    {
        RigDefinition source = new(
            "source",
            "Source",
            [
                new BoneDefinition(
                    0,
                    "src_root",
                    -1,
                    TransformTRS.Identity,
                    BoneKind.Root,
                    descriptorHash: 0x1000),
                new BoneDefinition(
                    1,
                    "Spine",
                    0,
                    OffsetBind(),
                    semanticRole: "body.spine"),
                new BoneDefinition(
                    2,
                    "src_hand",
                    1,
                    OffsetBind(),
                    semanticRole: "hand.right"),
                new BoneDefinition(
                    3,
                    "src_tip",
                    2,
                    OffsetBind()),
            ]);
        RigDefinition target = new(
            "target",
            "Target",
            [
                new BoneDefinition(
                    0,
                    "dst_root",
                    -1,
                    TransformTRS.Identity,
                    BoneKind.Root,
                    descriptorHash: 0x1000),
                new BoneDefinition(
                    1,
                    "Spine",
                    0,
                    OffsetBind(),
                    semanticRole: "unrelated.role"),
                new BoneDefinition(
                    2,
                    "dst_hand",
                    1,
                    OffsetBind(),
                    semanticRole: "hand.right"),
                new BoneDefinition(
                    3,
                    "dst_tip",
                    2,
                    OffsetBind()),
            ]);

        RetargetMap map = RetargetMapBuilder.CreateSuggested(source, target);

        Assert.Collection(
            map.Entries,
            entry => Assert.Equal(BoneMappingMethod.DescriptorHash, entry.Method),
            entry => Assert.Equal(BoneMappingMethod.ExactName, entry.Method),
            entry => Assert.Equal(BoneMappingMethod.Semantic, entry.Method),
            entry =>
            {
                Assert.Equal(BoneMappingMethod.Structural, entry.Method);
                Assert.Equal(0.7, entry.Confidence, 10);
            });
        CompatibilityReport report =
            RigCompatibilityAnalyzer.Analyze(source, target, map);
        Assert.True(report.CanEvaluate);
        Assert.Contains(
            report.Diagnostics,
            diagnostic =>
                diagnostic.Code == "mapping_requires_review" &&
                diagnostic.TargetBoneName == "dst_tip");
    }

    [Fact]
    public void SuggestedMapDoesNotGuessAmbiguousStructuralRows()
    {
        RigDefinition source = CreateRig(
            "source",
            ("root", -1, true),
            ("left", 0, true),
            ("right", 0, true));
        RigDefinition target = CreateRig(
            "target",
            ("different_root", -1, true),
            ("branch_a", 0, true),
            ("branch_b", 0, true));

        RetargetMap map = RetargetMapBuilder.CreateSuggested(source, target);

        Assert.Single(map.Entries);
        Assert.Equal(0, map.Entries[0].SourceBoneIndex);
        Assert.Equal(0, map.Entries[0].TargetBoneIndex);
        Assert.Equal(BoneMappingMethod.Structural, map.Entries[0].Method);
    }

    [Fact]
    public void SuggestedMapBridgesReviewedMixamoRolesToDl1HumanoidNames()
    {
        RigDefinition source = CreateRig(
            "mixamo",
            ("Armature", -1, true),
            ("mixamorig:Hips", 0, true),
            ("mixamorig:Spine", 1, true),
            ("mixamorig:Spine1", 2, true),
            ("mixamorig:Spine2", 3, true),
            ("mixamorig:Neck", 4, true),
            ("mixamorig:Head", 5, true),
            ("mixamorig:LeftShoulder", 4, true),
            ("mixamorig:LeftArm", 7, true),
            ("mixamorig:LeftForeArm", 8, true),
            ("mixamorig:LeftHand", 9, true),
            ("mixamorig:RightShoulder", 4, true),
            ("mixamorig:RightArm", 11, true),
            ("mixamorig:RightForeArm", 12, true),
            ("mixamorig:RightHand", 13, true),
            ("mixamorig:LeftUpLeg", 1, true),
            ("mixamorig:LeftLeg", 15, true),
            ("mixamorig:LeftFoot", 16, true),
            ("mixamorig:LeftToeBase", 17, true),
            ("mixamorig:RightUpLeg", 1, true),
            ("mixamorig:RightLeg", 19, true),
            ("mixamorig:RightFoot", 20, true),
            ("mixamorig:RightToeBase", 21, true));
        RigDefinition target = CreateRig(
            "dl1",
            ("bip01", -1, true),
            ("pelvis", 0, true),
            ("spine", 1, true),
            ("spine1", 2, true),
            ("spine2", 3, true),
            ("neck", 4, true),
            ("head", 5, true),
            ("l_clavicle", 4, true),
            ("l_upperarm", 7, true),
            ("l_forearm", 8, true),
            ("l_hand", 9, true),
            ("r_clavicle", 4, true),
            ("r_upperarm", 11, true),
            ("r_forearm", 12, true),
            ("r_hand", 13, true),
            ("l_thigh", 1, true),
            ("l_calf", 15, true),
            ("l_foot", 16, true),
            ("l_toebase", 17, true),
            ("r_thigh", 1, true),
            ("r_calf", 19, true),
            ("r_foot", 20, true),
            ("r_toebase", 21, true));

        RetargetMap map =
            RetargetMapBuilder.CreateSuggested(source, target);
        Dictionary<string, string> sourceByTarget =
            map.Entries.ToDictionary(
                entry => target.Bones[entry.TargetBoneIndex].Name,
                entry => source.Bones[entry.SourceBoneIndex].Name,
                StringComparer.OrdinalIgnoreCase);

        Assert.Equal(target.BoneCount, map.Entries.Length);
        Assert.Equal("Armature", sourceByTarget["bip01"]);
        Assert.Equal("mixamorig:Hips", sourceByTarget["pelvis"]);
        Assert.Equal(
            "mixamorig:LeftShoulder",
            sourceByTarget["l_clavicle"]);
        Assert.Equal(
            "mixamorig:LeftArm",
            sourceByTarget["l_upperarm"]);
        Assert.Equal(
            "mixamorig:LeftForeArm",
            sourceByTarget["l_forearm"]);
        Assert.Equal(
            "mixamorig:RightUpLeg",
            sourceByTarget["r_thigh"]);
        Assert.Equal(
            "mixamorig:RightLeg",
            sourceByTarget["r_calf"]);
        Assert.Equal(
            "mixamorig:RightToeBase",
            sourceByTarget["r_toebase"]);
        Assert.Equal(
            BoneMappingMethod.Semantic,
            Assert.Single(
                map.Entries,
                entry =>
                    target.Bones[entry.TargetBoneIndex].Name ==
                    "l_upperarm")
                .Method);
        BoneMapEntry root = Assert.Single(
            map.Entries,
            entry =>
                target.Bones[entry.TargetBoneIndex].Name ==
                "bip01");
        Assert.Equal(
            RetargetTransferPolicy.GlobalBindBasis,
            root.TransferPolicy);
        Assert.Equal(
            RetargetComponentPolicy.FullTransform,
            root.ComponentPolicy);
        BoneMapEntry upperArm = Assert.Single(
            map.Entries,
            entry =>
                target.Bones[entry.TargetBoneIndex].Name ==
                "l_upperarm");
        Assert.Equal(
            RetargetTransferPolicy.AnatomicalDirection,
            upperArm.TransferPolicy);
        Assert.Equal(
            RetargetComponentPolicy.Rotation,
            upperArm.ComponentPolicy);
        foreach (string anatomicalTarget in
                 new[]
                 {
                     "head",
                     "l_hand",
                     "r_hand",
                     "l_foot",
                     "r_foot",
                 })
        {
            BoneMapEntry anatomical = Assert.Single(
                map.Entries,
                entry =>
                    target.Bones[entry.TargetBoneIndex].Name ==
                    anatomicalTarget);
            Assert.Equal(
                RetargetTransferPolicy.AnatomicalDirection,
                anatomical.TransferPolicy);
            Assert.Equal(
                RetargetComponentPolicy.Rotation,
                anatomical.ComponentPolicy);
        }
        Assert.False(
            RetargetMappingReview.Analyze(
                    source,
                    target,
                    map)
                .IsReady);
    }

    [Fact]
    public void SuggestedMapDistributesMissingSourceMiddleFingerBySegment()
    {
        RigDefinition source = CreateRig(
            "mixamo_without_middle",
            ("Armature", -1, true),
            ("mixamorig:Hips", 0, true),
            ("mixamorig:LeftHand", 1, true),
            ("mixamorig:LeftHandIndex1", 2, true),
            ("mixamorig:LeftHandIndex2", 3, true),
            ("mixamorig:LeftHandIndex3", 4, true),
            ("mixamorig:LeftHandIndex4", 5, false),
            ("mixamorig:LeftHandRing1", 2, true),
            ("mixamorig:LeftHandRing2", 7, true),
            ("mixamorig:LeftHandRing3", 8, true),
            ("mixamorig:LeftHandRing4", 9, false));
        RigDefinition target = CreateRig(
            "dl1_with_middle",
            ("bip01", -1, true),
            ("pelvis", 0, true),
            ("l_hand", 1, true),
            ("l_finger11", 2, true),
            ("l_finger12", 3, true),
            ("l_finger13", 4, true),
            ("l_finger21", 2, true),
            ("l_finger22", 6, true),
            ("l_finger23", 7, true),
            ("l_finger31", 2, true),
            ("l_finger32", 9, true),
            ("l_finger33", 10, true));

        RetargetMap map =
            RetargetMapBuilder.CreateSuggested(source, target);

        for (int segment = 1; segment <= 3; segment++)
        {
            int targetIndex =
                target.GetBoneIndex($"l_finger2{segment}");
            BoneMapEntry row = Assert.Single(
                map.Entries,
                entry =>
                    entry.TargetBoneIndex == targetIndex);

            Assert.Equal(
                $"mixamorig:LeftHandIndex{segment}",
                source.Bones[row.SourceBoneIndex].Name);
            Assert.Equal(
                BoneMappingMethod.Distributed,
                row.Method);
            Assert.Equal(
                RetargetTransferPolicy.AnatomicalDirection,
                row.TransferPolicy);
            Assert.Equal(
                RetargetComponentPolicy.Rotation,
                row.ComponentPolicy);
        }
    }

    [Fact]
    public void SuggestedMapKeepsShiftedIdenticalFingerChainsInBindBasisPolicy()
    {
        // DL1 TPP and FPP player rigs share the hand and finger bind chains,
        // but an omitted/reordered helper shifts their numeric bone indexes.
        // Index equality is not part of skeleton compatibility.
        RigDefinition source = CreateRig(
            "player_tpp",
            ("bip01", -1, true),
            ("pelvis", 0, true),
            ("tpp_only_helper", 0, false),
            ("l_hand", 1, true),
            ("l_finger01", 3, true),
            ("l_finger02", 4, true),
            ("l_finger03", 5, true));
        RigDefinition target = CreateRig(
            "player_fpp",
            ("bip01", -1, true),
            ("pelvis", 0, true),
            ("l_hand", 1, true),
            ("l_finger01", 2, true),
            ("l_finger02", 3, true),
            ("l_finger03", 4, true));

        RetargetMap map =
            RetargetMapBuilder.CreateSuggested(source, target);

        foreach (string name in
                 new[] { "l_hand", "l_finger01", "l_finger02", "l_finger03" })
        {
            int targetIndex = target.GetBoneIndex(name);
            BoneMapEntry row = Assert.Single(
                map.Entries,
                entry => entry.TargetBoneIndex == targetIndex);

            Assert.Equal(
                name,
                source.Bones[row.SourceBoneIndex].Name,
                ignoreCase: true);
            Assert.Equal(
                RetargetTransferPolicy.GlobalBindBasis,
                row.TransferPolicy);
            Assert.Equal(
                RetargetComponentPolicy.FullTransform,
                row.ComponentPolicy);
        }
    }

    [Theory]
    [InlineData(
        "mixamorig:LeftHandThumb1",
        "finger.left.thumb.1")]
    [InlineData(
        "l_finger01",
        "finger.left.thumb.1")]
    [InlineData(
        "CC_Base_L_Mid2",
        "finger.left.middle.2")]
    [InlineData(
        "spine_01",
        "body.spine.0")]
    [InlineData(
        "CC_Base_Spine01",
        "body.spine.1")]
    [InlineData(
        "CC_Base_NeckTwist02",
        "body.neck.1")]
    public void HumanoidAliasesProduceCanonicalReviewRoles(
        string boneName,
        string expectedRole)
    {
        HumanoidBoneSemanticMatch match =
            Assert.IsType<HumanoidBoneSemanticMatch>(
                HumanoidBoneSemanticClassifier.Classify(
                    boneName));

        Assert.Equal(expectedRole, match.Role);
        Assert.InRange(match.Confidence, 0.8, 0.9);
    }

    [Fact]
    public void HumanoidAliasesRejectTwistsAndAmbiguousDuplicateRoles()
    {
        Assert.Null(
            HumanoidBoneSemanticClassifier.Classify(
                "CC_Base_L_UpperarmTwist01"));
        Assert.Null(
            HumanoidBoneSemanticClassifier.Classify(
                "mixamorig:HeadTop_End"));
        RigDefinition source = CreateRig(
            "source",
            ("root", -1, true),
            ("mixamorig:LeftArm", 0, true),
            ("upperarm_l", 0, true));
        RigDefinition target = CreateRig(
            "target",
            ("bip01", -1, true),
            ("l_upperarm", 0, true));

        RetargetMap map =
            RetargetMapBuilder.CreateSuggested(source, target);

        Assert.DoesNotContain(
            map.Entries,
            static entry => entry.TargetBoneIndex == 1);
    }

    [Fact]
    public void MappingFingerprintBindsBothRigsAssetAndReviewedRows()
    {
        RetargetMap first = new(
            "source",
            "target",
            [
                new BoneMapEntry(
                    2,
                    1,
                    BoneMappingMethod.Semantic,
                    0.9),
                new BoneMapEntry(
                    0,
                    0,
                    BoneMappingMethod.ExactName,
                    1.0),
            ]);
        RetargetMap reordered = new(
            "source",
            "target",
            first.Entries.Reverse());

        string fingerprint =
            RetargetMapFingerprint.Compute(
                "source-signature",
                "target-signature",
                "asset-sha256",
                first);

        Assert.Equal(
            fingerprint,
            RetargetMapFingerprint.Compute(
                "source-signature",
                "target-signature",
                "asset-sha256",
                reordered));
        Assert.NotEqual(
            fingerprint,
            RetargetMapFingerprint.Compute(
                "source-signature",
                "target-signature",
                "changed-asset",
                first));
        Assert.NotEqual(
            fingerprint,
            RetargetMapFingerprint.Compute(
                "source-signature",
                "target-signature",
                "asset-sha256",
                new RetargetMap(
                    "source",
                    "target",
                    first.Entries.Select(entry =>
                        entry.TargetBoneIndex == 1
                            ? new BoneMapEntry(
                                entry.SourceBoneIndex,
                                entry.TargetBoneIndex,
                                entry.Method,
                                entry.Confidence,
                                isLocked: true,
                                isReviewed: true)
                            : entry))));
        Assert.Equal(64, fingerprint.Length);
    }

    [Fact]
    public void MissingOptionalHelperUsesBindFallbackButMissingDeformIsIncompatible()
    {
        RigDefinition source = CreateRig("source", ("root", -1, true));
        RigDefinition optionalTarget = CreateRig(
            "optional",
            ("root", -1, true),
            ("refcamera", 0, false));
        RigDefinition requiredTarget = CreateRig(
            "required",
            ("root", -1, true),
            ("spine", 0, true));

        CompatibilityReport optionalReport = RigCompatibilityAnalyzer.Analyze(
            source,
            optionalTarget,
            new RetargetMap(
                source.Id,
                optionalTarget.Id,
                [new BoneMapEntry(0, 0, BoneMappingMethod.ExactName, 1.0)]));
        CompatibilityReport requiredReport = RigCompatibilityAnalyzer.Analyze(
            source,
            requiredTarget,
            new RetargetMap(
                source.Id,
                requiredTarget.Id,
                [new BoneMapEntry(0, 0, BoneMappingMethod.ExactName, 1.0)]));

        Assert.Equal(
            CompatibilityClassification.TargetWithBindFallback,
            optionalReport.Classification);
        Assert.True(optionalReport.CanEvaluate);
        Assert.Contains(
            optionalReport.Diagnostics,
            diagnostic => diagnostic.Code == "optional_target_bind_fallback");
        Assert.Equal(CompatibilityClassification.Incompatible, requiredReport.Classification);
        Assert.False(requiredReport.CanEvaluate);
        Assert.Contains(
            requiredReport.Diagnostics,
            diagnostic => diagnostic.Code == "required_target_unmapped");
    }

    [Fact]
    public void MappingReviewRequiresExplicitDecisionForNonDeterministicRows()
    {
        RigDefinition source = CreateRig(
            "source",
            ("root", -1, true),
            ("source_arm", 0, true));
        RigDefinition target = CreateRig(
            "target",
            ("root", -1, true),
            ("target_arm", 0, true));
        RetargetMap unreviewed = new(
            source.Id,
            target.Id,
            [
                new BoneMapEntry(
                    0,
                    0,
                    BoneMappingMethod.ExactName,
                    1),
                new BoneMapEntry(
                    1,
                    1,
                    BoneMappingMethod.Semantic,
                    0.9),
            ]);

        RetargetMappingReviewReport blocked =
            RetargetMappingReview.Analyze(
                source,
                target,
                unreviewed);
        RetargetMap reviewed = new(
            source.Id,
            target.Id,
            unreviewed.Entries.Select(entry =>
                entry.TargetBoneIndex == 1
                    ? new BoneMapEntry(
                        entry.SourceBoneIndex,
                        entry.TargetBoneIndex,
                        entry.Method,
                        entry.Confidence,
                        isReviewed: true)
                    : entry));
        RetargetMappingReviewReport ready =
            RetargetMappingReview.Analyze(
                source,
                target,
                reviewed);

        Assert.False(blocked.IsReady);
        Assert.Equal(1, blocked.ExplicitReviewRequiredCount);
        Assert.Contains(
            blocked.Diagnostics,
            diagnostic =>
                diagnostic.Code ==
                "mapping_row_requires_review");
        Assert.True(ready.IsReady);
    }

    [Fact]
    public void MappingReviewRequiresExplicitRequiredTargetBindOwnership()
    {
        RigDefinition source =
            CreateRig("source", ("root", -1, true));
        RigDefinition target = CreateRig(
            "target",
            ("root", -1, true),
            ("required_helper", 0, true));
        BoneMapEntry root = new(
            0,
            0,
            BoneMappingMethod.ExactName,
            1);

        RetargetMappingReviewReport blocked =
            RetargetMappingReview.Analyze(
                source,
                target,
                new RetargetMap(
                    source.Id,
                    target.Id,
                    [root]));
        RetargetMappingReviewReport ready =
            RetargetMappingReview.Analyze(
                source,
                target,
                new RetargetMap(
                    source.Id,
                    target.Id,
                    [root],
                    reviewedTargetBindBoneIndices: [1]));

        Assert.False(blocked.IsReady);
        Assert.Equal(1, blocked.RequiredTargetBindReviewCount);
        Assert.True(ready.IsReady);
    }

    [Fact]
    public void AutoMapPreservesOnlyLockedReviewedRows()
    {
        RetargetMap proposal = new(
            "source",
            "target",
            [
                new BoneMapEntry(
                    0,
                    0,
                    BoneMappingMethod.ExactName,
                    1),
                new BoneMapEntry(
                    1,
                    1,
                    BoneMappingMethod.ExactName,
                    1),
                new BoneMapEntry(
                    2,
                    2,
                    BoneMappingMethod.ExactName,
                    1),
            ]);
        RetargetMap current = new(
            "source",
            "target",
            [
                new BoneMapEntry(
                    3,
                    0,
                    BoneMappingMethod.Manual,
                    1,
                    isLocked: true,
                    isReviewed: true),
                new BoneMapEntry(
                    4,
                    2,
                    BoneMappingMethod.Manual,
                    1,
                    isReviewed: true),
            ],
            reviewedTargetBindBoneIndices: [5]);

        RetargetMap merged =
            MainWindowViewModel.MergeAutoMapWithLockedRows(
                proposal,
                current);

        BoneMapEntry locked = Assert.Single(
            merged.Entries,
            entry => entry.TargetBoneIndex == 0);
        Assert.Equal(3, locked.SourceBoneIndex);
        Assert.True(locked.IsLocked);
        Assert.True(locked.IsReviewed);
        BoneMapEntry replaced = Assert.Single(
            merged.Entries,
            entry => entry.TargetBoneIndex == 2);
        Assert.Equal(2, replaced.SourceBoneIndex);
        Assert.False(replaced.IsReviewed);
        Assert.Empty(merged.ReviewedTargetBindBoneIndices);
    }

    [Fact]
    public void AutoMapCanFanOutACompleteDigitWithoutReplacingLockedRows()
    {
        RetargetMap proposal = new(
            "source",
            "target",
            [
                new BoneMapEntry(
                    0,
                    0,
                    BoneMappingMethod.Semantic,
                    0.9),
                new BoneMapEntry(
                    0,
                    1,
                    BoneMappingMethod.Distributed,
                    0.65,
                    transferPolicy:
                        RetargetTransferPolicy.AnatomicalDirection,
                    componentPolicy:
                        RetargetComponentPolicy.Rotation),
            ]);
        RetargetMap current = new(
            "source",
            "target",
            [
                new BoneMapEntry(
                    0,
                    0,
                    BoneMappingMethod.Manual,
                    1,
                    isLocked: true,
                    isReviewed: true),
            ]);

        RetargetMap merged =
            MainWindowViewModel.MergeAutoMapWithLockedRows(
                proposal,
                current);

        Assert.Equal(2, merged.Entries.Length);
        Assert.True(
            Assert.Single(
                merged.Entries,
                entry => entry.TargetBoneIndex == 0)
                .IsLocked);
        BoneMapEntry distributed = Assert.Single(
            merged.Entries,
            entry => entry.TargetBoneIndex == 1);
        Assert.Equal(0, distributed.SourceBoneIndex);
        Assert.Equal(
            BoneMappingMethod.Distributed,
            distributed.Method);
    }

    [Theory]
    [InlineData(RetargetTransferPolicy.RotationDelta)]
    [InlineData(RetargetTransferPolicy.GlobalRotationDelta)]
    public void AutoMapUpgradesUnreviewedLegacyRotationRows(
        RetargetTransferPolicy legacyPolicy)
    {
        RetargetMap proposal = new(
            "source",
            "target",
            [
                new BoneMapEntry(
                    0,
                    0,
                    BoneMappingMethod.Semantic,
                    0.9,
                    transferPolicy:
                        RetargetTransferPolicy
                            .AnatomicalDirection,
                    componentPolicy:
                        RetargetComponentPolicy.Rotation),
            ]);
        RetargetMap legacy = new(
            "source",
            "target",
            [
                new BoneMapEntry(
                    0,
                    0,
                    BoneMappingMethod.Semantic,
                    0.9,
                    transferPolicy: legacyPolicy,
                    componentPolicy:
                        RetargetComponentPolicy.Rotation),
            ]);

        RetargetMap merged =
            MainWindowViewModel.MergeAutoMapWithLockedRows(
                proposal,
                legacy);

        BoneMapEntry upgraded =
            Assert.Single(merged.Entries);
        Assert.Equal(
            RetargetTransferPolicy.AnatomicalDirection,
            upgraded.TransferPolicy);
        Assert.Equal(
            RetargetComponentPolicy.Rotation,
            upgraded.ComponentPolicy);
    }

    [Fact]
    public void AutoMapPreservesHelperFanoutAndPerRowPolicies()
    {
        RetargetMap proposal = new(
            "source",
            "target",
            [
                new BoneMapEntry(
                    0,
                    0,
                    BoneMappingMethod.ExactName,
                    1),
                new BoneMapEntry(
                    1,
                    1,
                    BoneMappingMethod.ExactName,
                    1),
                new BoneMapEntry(
                    0,
                    2,
                    BoneMappingMethod.ExactName,
                    1,
                    mappingKind:
                        RetargetMappingKind.HelperOverride,
                    transferPolicy:
                        RetargetTransferPolicy.RestRelative,
                    componentPolicy:
                        RetargetComponentPolicy.Translation),
                new BoneMapEntry(
                    0,
                    3,
                    BoneMappingMethod.ExactName,
                    1,
                    mappingKind:
                        RetargetMappingKind.HelperOverride,
                    transferPolicy:
                        RetargetTransferPolicy.RestRelative,
                    componentPolicy:
                        RetargetComponentPolicy.RotationTranslation),
            ]);
        RetargetMap current = new(
            "source",
            "target",
            [
                new BoneMapEntry(
                    0,
                    0,
                    BoneMappingMethod.Manual,
                    1,
                    isLocked: true,
                    isReviewed: true,
                    transferPolicy:
                        RetargetTransferPolicy.RotationDelta,
                    componentPolicy:
                        RetargetComponentPolicy.Rotation),
                new BoneMapEntry(
                    0,
                    2,
                    BoneMappingMethod.Manual,
                    1,
                    isReviewed: true,
                    mappingKind:
                        RetargetMappingKind.HelperOverride,
                    transferPolicy:
                        RetargetTransferPolicy.CopyLocal,
                    componentPolicy:
                        RetargetComponentPolicy.Translation),
                new BoneMapEntry(
                    1,
                    1,
                    BoneMappingMethod.Manual,
                    1,
                    transferPolicy:
                        RetargetTransferPolicy.RotationDelta,
                    componentPolicy:
                        RetargetComponentPolicy.Scale),
            ]);

        RetargetMap merged =
            MainWindowViewModel.MergeAutoMapWithLockedRows(
                proposal,
                current);

        Assert.Equal(4, merged.Entries.Length);
        Assert.Equal(
            merged.Entries.Length,
            merged.Entries
                .Select(static entry =>
                    entry.TargetBoneIndex)
                .Distinct()
                .Count());
        Assert.Equal(
            3,
            merged.Entries.Count(entry =>
                entry.SourceBoneIndex == 0));
        BoneMapEntry body = Assert.Single(
            merged.Entries,
            entry => entry.TargetBoneIndex == 0);
        Assert.Equal(
            RetargetMappingKind.Bone,
            body.MappingKind);
        Assert.Equal(
            RetargetTransferPolicy.RotationDelta,
            body.TransferPolicy);
        Assert.Equal(
            RetargetComponentPolicy.Rotation,
            body.ComponentPolicy);
        BoneMapEntry explicitHelper = Assert.Single(
            merged.Entries,
            entry => entry.TargetBoneIndex == 2);
        Assert.Equal(
            RetargetMappingKind.HelperOverride,
            explicitHelper.MappingKind);
        Assert.Equal(
            RetargetTransferPolicy.CopyLocal,
            explicitHelper.TransferPolicy);
        Assert.Equal(
            RetargetComponentPolicy.Translation,
            explicitHelper.ComponentPolicy);
        BoneMapEntry refreshedBody = Assert.Single(
            merged.Entries,
            entry => entry.TargetBoneIndex == 1);
        Assert.Equal(
            RetargetTransferPolicy.RotationDelta,
            refreshedBody.TransferPolicy);
        Assert.Equal(
            RetargetComponentPolicy.Scale,
            refreshedBody.ComponentPolicy);
        Assert.Contains(
            merged.Entries,
            entry =>
                entry.TargetBoneIndex == 3 &&
                entry.MappingKind ==
                    RetargetMappingKind.HelperOverride);
    }

    [Fact]
    public void BindBasisRetargetPreservesTargetProportionsAndRootMotion()
    {
        RigDefinition source = new(
            "source",
            "Source",
            [
                new BoneDefinition(0, "root", -1, TransformTRS.Identity, BoneKind.Root),
                new BoneDefinition(
                    1,
                    "child",
                    0,
                    new TransformTRS(Vector3D.UnitX, QuaternionD.Identity, Vector3D.One)),
            ]);
        RigDefinition target = new(
            "target",
            "Target",
            [
                new BoneDefinition(0, "root", -1, TransformTRS.Identity, BoneKind.Root),
                new BoneDefinition(
                    1,
                    "child",
                    0,
                    new TransformTRS(
                        new Vector3D(2.0, 0.0, 0.0),
                        QuaternionD.Identity,
                        Vector3D.One)),
            ]);
        SkeletonPose sourcePose = new(
            source,
            [
                new TransformTRS(Vector3D.UnitX, QuaternionD.Identity, Vector3D.One),
                source.Bones[1].LocalBindPose,
            ]);
        RetargetMap map = new(
            source.Id,
            target.Id,
            [
                new BoneMapEntry(0, 0, BoneMappingMethod.ExactName, 1.0),
                new BoneMapEntry(1, 1, BoneMappingMethod.ExactName, 1.0),
            ]);

        SkeletonPose retargeted = PoseRetargeter.Retarget(sourcePose, target, map);

        Assert.Equal(1.0, retargeted.LocalTransforms[0].Translation.X, 10);
        Assert.Equal(2.0, retargeted.LocalTransforms[1].Translation.X, 10);
        Assert.Equal(3.0, retargeted.GlobalMatrices[1].Translation.X, 10);
    }

    [Fact]
    public void LegacyGlobalBindRowFallsBackToSkinSafeRotationWhenScaleCreatesShear()
    {
        TransformTRS sourceRootBind = new(
            Vector3D.Zero,
            QuaternionD.Identity,
            new Vector3D(2.0, 1.0, 1.0));
        RigDefinition source = new(
            "scaled-source",
            "Scaled source",
            [
                new BoneDefinition(
                    0,
                    "root",
                    -1,
                    sourceRootBind,
                    BoneKind.Root),
                new BoneDefinition(
                    1,
                    "child",
                    0,
                    TransformTRS.Identity),
            ]);
        TransformTRS targetChildBind = new(
            Vector3D.UnitY,
            QuaternionD.Identity,
            new Vector3D(1.0, 1.25, 0.8));
        RigDefinition target = new(
            "target",
            "Target",
            [
                new BoneDefinition(
                    0,
                    "root",
                    -1,
                    TransformTRS.Identity,
                    BoneKind.Root),
                new BoneDefinition(
                    1,
                    "child",
                    0,
                    targetChildBind),
            ]);
        QuaternionD animatedRotation =
            QuaternionD.FromAxisAngle(
                Vector3D.UnitZ,
                Math.PI / 4.0);
        SkeletonPose sourcePose = new(
            source,
            [
                sourceRootBind,
                TransformTRS.Identity with
                {
                    Rotation = animatedRotation,
                },
            ]);
        RetargetMap map = new(
            source.Id,
            target.Id,
            [
                new BoneMapEntry(
                    0,
                    0,
                    BoneMappingMethod.ExactName,
                    1.0,
                    transferPolicy:
                        RetargetTransferPolicy.Bind),
                new BoneMapEntry(
                    1,
                    1,
                    BoneMappingMethod.ExactName,
                    1.0,
                    transferPolicy:
                        RetargetTransferPolicy.GlobalBindBasis,
                    componentPolicy:
                        RetargetComponentPolicy.FullTransform),
            ]);

        SkeletonPose retargeted =
            PoseRetargeter.Retarget(
                sourcePose,
                target,
                map);

        Assert.Equal(
            targetChildBind.Translation,
            retargeted.LocalTransforms[1].Translation);
        Assert.Equal(
            targetChildBind.Scale,
            retargeted.LocalTransforms[1].Scale);
        Assert.True(
            TransformMatrix.CreateRotation(
                    animatedRotation)
                .NearlyEquals(
                    TransformMatrix.CreateRotation(
                        retargeted.LocalTransforms[1]
                            .Rotation),
                    1e-9));
    }

    private static RigDefinition CreateRig(
        string id,
        params (string Name, int Parent, bool Required)[] rows)
    {
        return new RigDefinition(
            id,
            id,
            rows.Select(
                (row, index) =>
                    new BoneDefinition(
                        index,
                        row.Name,
                        row.Parent,
                        index == 0
                            ? TransformTRS.Identity
                            : new TransformTRS(
                                Vector3D.UnitY,
                                QuaternionD.Identity,
                                Vector3D.One),
                        index == 0 ? BoneKind.Root : BoneKind.Deform,
                        row.Required)));
    }

    private static TransformTRS OffsetBind() =>
        new(
            Vector3D.UnitY,
            QuaternionD.Identity,
            Vector3D.One);
}
