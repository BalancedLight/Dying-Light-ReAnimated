using System.Collections.Immutable;
using ReAnimated.App.ViewModels;
using ReAnimated.Codecs.Anm2;
using ReAnimated.Core.Domain;
using ReAnimated.Core.Mathematics;
using ReAnimated.Core.Project;
using ReAnimated.DL1.Assets.Meshes;
using ReAnimated.Evaluation;
using ReAnimated.Retargeting;
using ReAnimated.Retargeting.Mapping;

namespace ReAnimated.Tests;

public sealed class RetargetCompatibilityTests
{
    [Fact]
    [Trait("ValidationTier", "Focused")]
    [Trait("Gate", "EditorUsability")]
    public void MappingViewModelEditsEveryComponentCombinationWithoutAllowingEmpty()
    {
        BoneMappingViewModel row = new(
            "source",
            "target",
            0.95,
            BoneMappingMethod.ExactName.ToString(),
            componentPolicy: RetargetComponentPolicy.FullTransform,
            evidence: "Unique normalized identity.",
            transformComponents:
                RetargetTransformComponents.Rotation |
                RetargetTransformComponents.Scale);
        List<string?> changed = [];
        row.PropertyChanged += (_, args) =>
            changed.Add(args.PropertyName);

        Assert.False(row.IsTranslationEnabled);
        Assert.True(row.IsRotationEnabled);
        Assert.True(row.IsScaleEnabled);
        Assert.Equal(
            RetargetTransformComponents.Rotation |
                RetargetTransformComponents.Scale,
            row.TransformComponents);
        Assert.Equal("Exact · Not scored", row.EvidenceMethod);

        row.IsTranslationEnabled = true;
        Assert.Equal(
            RetargetTransformComponents.All,
            row.TransformComponents);
        Assert.Equal(
            RetargetComponentPolicy.FullTransform,
            row.ComponentPolicy);
        Assert.Contains(
            nameof(BoneMappingViewModel.TransformComponents),
            changed);

        row.IsRotationEnabled = false;
        Assert.Equal(
            RetargetTransformComponents.Translation |
                RetargetTransformComponents.Scale,
            row.TransformComponents);
        row.IsTranslationEnabled = false;
        Assert.Equal(
            RetargetTransformComponents.Scale,
            row.TransformComponents);
        Assert.Equal(
            RetargetComponentPolicy.Scale,
            row.ComponentPolicy);

        row.IsScaleEnabled = false;
        Assert.Equal(
            RetargetTransformComponents.Scale,
            row.TransformComponents);
        Assert.True(row.IsScaleEnabled);

        RetargetTransferPolicyOption restRelative = Assert.Single(
            row.TransferPolicyOptions,
            option => option.Value ==
                RetargetTransferPolicy.RestRelative);
        row.SelectedTransferPolicyOption = restRelative;
        Assert.Equal(
            RetargetTransferPolicy.RestRelative,
            row.TransferPolicy);
        Assert.False(string.IsNullOrWhiteSpace(restRelative.Description));
    }

    [Fact]
    public void MappingTargetDropdownCommitsManualSelectionAndRestoresRejectedEdit()
    {
        BoneMappingViewModel row = new(
            "mixamorig:Neck",
            "spine_001",
            0.9,
            BoneMappingMethod.Semantic.ToString(),
            targetBoneOptions: ["spine", "spine_001", "Neck"]);

        Assert.Equal(
            ["spine", "spine_001", "Neck"],
            row.TargetBoneOptions);

        row.TargetBone = "Neck";
        row.AcceptManualEdit();

        Assert.Equal("Neck", row.TargetBone);
        Assert.Equal(BoneMappingMethod.Manual.ToString(), row.Status);
        Assert.Equal(1.0, row.Confidence);
        Assert.False(row.IsReviewed);
        Assert.False(row.IsLocked);

        row.TargetBone = "spine";
        row.RestorePersistedTargetBone();

        Assert.Equal("Neck", row.TargetBone);
    }

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
        BoneMapEntry explicitlyReviewed = Assert.Single(
            reviewed.Entries,
            entry => entry.TargetBoneIndex == 1);
        Assert.True(explicitlyReviewed.IsReviewed);
        Assert.Equal(
            MappingReviewOrigin.Explicit,
            explicitlyReviewed.ReviewOrigin);
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
    [Trait("ValidationTier", "Focused")]
    [Trait("Gate", "EditorUsability")]
    public void ExplicitReviewSelectionsCanExcludeOptionalSceneCameraCoverage()
    {
        RigDefinition source = CreateRig(
            "source",
            ("root", -1, true));
        RigDefinition target = CreateRig(
            "target",
            ("root", -1, true, BoneKind.Root),
            ("scene_camera", 0, true, BoneKind.Camera));
        RetargetMap mapping = new(
            source.Id,
            target.Id,
            [new BoneMapEntry(0, 0, BoneMappingMethod.ExactName, 1.0)]);

        RetargetMap reviewed =
            MainWindowViewModel.ApplyExplicitReviewSelections(
                source,
                target,
                mapping,
                [new BoneMappingViewModel(
                    "root",
                    "root",
                    1.0,
                    BoneMappingMethod.ExactName.ToString())],
                [],
                requiredTargetBoneIndices: [0]);

        Assert.Empty(reviewed.ReviewedTargetBindBoneIndices);
        Assert.True(RetargetMappingReview.Analyze(
            source,
            target,
            reviewed,
            requiredTargetBoneIndices: [0]).IsReady);
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
    [Trait("ValidationTier", "Focused")]
    [Trait("Gate", "EditorUsability")]
    public void GuidedPreviewRequiredCoverageMatchesStrictExportReadiness()
    {
        RigDefinition source = CreateRig(
            "source",
            ("root", -1, true));
        RigDefinition target = CreateRig(
            "target",
            ("root", -1, true, BoneKind.Root),
            ("scene_camera", 0, true, BoneKind.Camera));
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
        var clip = new AnimationClip(
            "selected",
            new FrameRate(30, 1),
            1,
            [new TransformTrack(0, [new TransformKeyframe(0, source.Bones[0].LocalBindPose)])]);
        ImmutableArray<int> defaultRequired =
            RigCompatibilityAnalyzer.GetRequiredTargetBoneIndices(target);
        ImmutableArray<int> guidedRequired =
            MainWindowViewModel.GetGuidedPreviewRequiredTargetBoneIndices(
                source,
                target,
                clip,
                mapping);

        RetargetMappingReviewReport strict =
            RetargetMappingReview.Analyze(source, target, mapping);
        RetargetMappingReviewReport guided =
            RetargetMappingReview.Analyze(
                source,
                target,
                mapping,
                requiredTargetBoneIndices: guidedRequired);

        Assert.Equal([0, 1], defaultRequired.ToArray());
        Assert.Equal(defaultRequired.ToArray(), guidedRequired.ToArray());
        Assert.False(strict.IsReady);
        Assert.False(guided.IsReady);
        Assert.Equal(strict.IsReady, guided.IsReady);
        Assert.Contains(
            guided.Diagnostics,
            diagnostic => diagnostic.Code ==
                "required_target_unmapped" &&
                diagnostic.TargetBoneName == "scene_camera");
    }

    [Fact]
    [Trait("ValidationTier", "Focused")]
    [Trait("Gate", "EditorUsability")]
    public void RequiredCoverageIncludesTrackedAuxiliaryAndBodyButExcludesOptionalUntrackedNodes()
    {
        RigDefinition source = CreateRig(
            "source",
            ("root", -1, true, BoneKind.Root),
            ("scene_prop", 0, true, BoneKind.Prop));
        RigDefinition target = CreateRig(
            "target",
            ("root", -1, true, BoneKind.Root),
            ("scene_prop", 0, true, BoneKind.Prop),
            ("spine", 0, true, BoneKind.Deform),
            ("optional_prop", 0, false, BoneKind.Prop),
            ("optional_camera", 0, false, BoneKind.Camera));
        BoneMapEntry root = new(0, 0, BoneMappingMethod.ExactName, 1.0);
        BoneMapEntry trackedProp = new(
            1,
            1,
            BoneMappingMethod.ExactName,
            1.0,
            mappingKind: RetargetMappingKind.HelperOverride,
            transferPolicy: RetargetTransferPolicy.RestRelative,
            componentPolicy: RetargetMapBuilder.GetDefaultHelperComponentPolicy("scene_prop"));
        RetargetMap mapping = new(source.Id, target.Id, [root, trackedProp]);
        var clip = new AnimationClip(
            "selected",
            new FrameRate(30, 1),
            1,
            [
                new TransformTrack(0, [new TransformKeyframe(0, source.Bones[0].LocalBindPose)]),
                new TransformTrack(1, [new TransformKeyframe(0, source.Bones[1].LocalBindPose)]),
            ]);

        ImmutableArray<int> required =
            MainWindowViewModel.GetGuidedPreviewRequiredTargetBoneIndices(
                source,
                target,
                clip,
                mapping);
        RetargetMappingReviewReport strict =
            RetargetMappingReview.Analyze(source, target, mapping);
        RetargetMappingReviewReport guided =
            RetargetMappingReview.Analyze(
                source,
                target,
                mapping,
                requiredTargetBoneIndices: required);

        Assert.True(RetargetMappingReview.IsVerifiedDeterministicIdentity(
            source, target, trackedProp));
        Assert.Contains(1, clip.TransformTracks.Select(static track => track.BoneIndex));
        Assert.Equal([0, 1, 2], required.ToArray());
        Assert.DoesNotContain(3, required);
        Assert.DoesNotContain(4, required);
        Assert.False(strict.IsReady);
        Assert.False(guided.IsReady);
        Assert.Equal(strict.IsReady, guided.IsReady);
        Assert.Contains(
            guided.Diagnostics,
            diagnostic => diagnostic.Code == "required_target_unmapped" &&
                diagnostic.TargetBoneName == "spine");
        Assert.DoesNotContain(
            guided.Diagnostics,
            diagnostic => diagnostic.Code == "required_target_unmapped" &&
                diagnostic.TargetBoneName == "scene_prop");
        Assert.DoesNotContain(
            guided.Diagnostics,
            diagnostic => diagnostic.Code == "required_target_unmapped" &&
                diagnostic.TargetBoneName is "optional_prop" or "optional_camera");
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
        "mixamorig:LeftHandRing1",
        "finger.left.ring.1")]
    [InlineData(
        "l_finger01",
        "finger.left.thumb.1")]
    [InlineData(
        "CC_Base_L_Mid2",
        "finger.left.middle.2")]
    [InlineData(
        "mixamorig:LeftHandMiddle1",
        "finger.left.middle.1")]
    [InlineData(
        "lowerarm_l",
        "arm.left.lower")]
    [InlineData(
        "CC_Base_BoneRoot",
        "body.root")]
    [InlineData(
        "CC_Base_Hip",
        "body.pelvis")]
    [InlineData(
        "spine_01",
        "body.spine.0")]
    [InlineData(
        "CC_Base_Spine01",
        "body.spine.1")]
    [InlineData("Bip001", "body.root")]
    [InlineData("Bip001 Pelvis", "body.pelvis")]
    [InlineData("Bip001 Spine1", "body.spine.1")]
    [InlineData("Bip001 L Thigh", "leg.left.upper")]
    [InlineData("Bip001 R Calf", "leg.right.lower")]
    [InlineData("Bip001 L Toe0", "toe.left")]
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
                "CC_Base_L_ThighTwist01"));
        Assert.Null(
            HumanoidBoneSemanticClassifier.Classify(
                "CC_Base_L_CalfTwist02"));
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

    [Theory]
    [InlineData("mixamorig:LDrawstring1")]
    [InlineData("mixamorig:RHoodieString2")]
    public void HumanoidAliasesKeepGarmentStringsOutOfFingerRoles(string boneName)
    {
        Assert.Null(HumanoidBoneSemanticClassifier.Classify(boneName));
        Assert.True(HumanoidBoneSemanticClassifier.IsNonAnatomicalBranchName(boneName));
    }

    [Fact]
    public void CcAccuRigUnweightedAnatomicalDriversMapButTwistsAndPelvisStayAtBind()
    {
        RigDefinition source = CreateRig(
            "mixamo",
            ("Armature", -1, true),
            ("mixamorig:Hips", 0, true),
            ("mixamorig:Spine", 1, true),
            ("mixamorig:LeftShoulder", 2, true),
            ("mixamorig:LeftArm", 3, true),
            ("mixamorig:LeftForeArm", 4, true),
            ("mixamorig:LeftHand", 5, true),
            ("mixamorig:LeftHandMiddle1", 6, true),
            ("mixamorig:LeftHandMiddle2", 7, true),
            ("mixamorig:LeftUpLeg", 1, true),
            ("mixamorig:LeftLeg", 9, true),
            ("mixamorig:LeftFoot", 10, true));
        RigDefinition target = CreateRig(
            "cc",
            ("CC_Base_BoneRoot", -1, true, BoneKind.Root),
            ("CC_Base_Hip", 0, true, BoneKind.Helper),
            ("CC_Base_Pelvis", 1, true, BoneKind.Helper),
            ("CC_Base_Waist", 2, true, BoneKind.Deform),
            ("CC_Base_L_Clavicle", 3, true, BoneKind.Deform),
            ("CC_Base_L_Upperarm", 4, true, BoneKind.Helper),
            ("CC_Base_L_UpperarmTwist01", 5, true, BoneKind.Deform),
            ("CC_Base_L_Forearm", 5, true, BoneKind.Helper),
            ("CC_Base_L_ForearmTwist01", 7, true, BoneKind.Deform),
            ("CC_Base_L_Hand", 7, true, BoneKind.Deform),
            ("CC_Base_L_Mid1", 9, true, BoneKind.Deform),
            ("CC_Base_L_Mid2", 10, true, BoneKind.Deform),
            ("CC_Base_L_Thigh", 2, true, BoneKind.Helper),
            ("CC_Base_L_ThighTwist01", 12, true, BoneKind.Deform),
            ("CC_Base_L_Calf", 12, true, BoneKind.Helper),
            ("CC_Base_L_CalfTwist01", 14, true, BoneKind.Deform),
            ("CC_Base_L_Foot", 14, true, BoneKind.Deform));

        RetargetMap map = RetargetMapBuilder.CreateSuggested(source, target);
        Dictionary<string, BoneMapEntry> byTarget = map.Entries.ToDictionary(
            entry => target.Bones[entry.TargetBoneIndex].Name,
            StringComparer.Ordinal);

        Assert.Equal("mixamorig:Hips", source.Bones[byTarget["CC_Base_Hip"].SourceBoneIndex].Name);
        Assert.Equal("mixamorig:LeftUpLeg", source.Bones[byTarget["CC_Base_L_Thigh"].SourceBoneIndex].Name);
        Assert.Equal("mixamorig:LeftLeg", source.Bones[byTarget["CC_Base_L_Calf"].SourceBoneIndex].Name);
        Assert.Equal("mixamorig:LeftArm", source.Bones[byTarget["CC_Base_L_Upperarm"].SourceBoneIndex].Name);
        Assert.Equal("mixamorig:LeftForeArm", source.Bones[byTarget["CC_Base_L_Forearm"].SourceBoneIndex].Name);
        Assert.Equal(BoneMappingMethod.Semantic, byTarget["CC_Base_L_Mid1"].Method);
        Assert.Equal(RetargetMappingKind.Bone, byTarget["CC_Base_L_Upperarm"].MappingKind);
        Assert.DoesNotContain(map.Entries, entry => target.Bones[entry.TargetBoneIndex].Name.Contains("Twist", StringComparison.Ordinal));
        Assert.DoesNotContain(map.Entries, entry => target.Bones[entry.TargetBoneIndex].Name == "CC_Base_Pelvis");
        Assert.Equal(0.90, byTarget["CC_Base_Hip"].Confidence, 10);
        // Pelvis has no Mixamo counterpart, so the thigh honestly lacks parent-chain agreement.
        Assert.Equal(0.82, byTarget["CC_Base_L_Thigh"].Confidence, 10);
    }

    [Fact]
    public void RetargetRigRoleIndexCollapsesAncestorChainsButRejectsSiblings()
    {
        RigDefinition chain = CreateRig(
            "chain",
            ("CC_Base_BoneRoot", -1, true, BoneKind.Root),
            ("CC_Base_Hip", 0, true, BoneKind.Helper),
            ("CC_Base_Pelvis", 1, true, BoneKind.Helper),
            ("CC_Base_L_ThighTwist01", 2, true, BoneKind.Deform));
        var chainIndex = new RetargetRigRoleIndex(chain);
        Assert.True(chainIndex.TryGetUniqueBodyRoleTarget("body.pelvis", out int owner));
        Assert.Equal(1, owner);

        RigDefinition siblings = CreateRig(
            "siblings",
            ("root", -1, true, BoneKind.Root),
            ("hip", 0, true, BoneKind.Helper),
            ("pelvis", 0, true, BoneKind.Helper),
            ("left_thigh", 1, true, BoneKind.Deform),
            ("right_thigh", 2, true, BoneKind.Deform));
        var siblingIndex = new RetargetRigRoleIndex(siblings);
        Assert.False(siblingIndex.TryGetUniqueBodyRoleTarget("body.pelvis", out _));
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
    public void AnatomicalRetargetUsesSplitHipRootsWhenTargetHasNoCentralPelvis()
    {
        static TransformTRS At(double x, double y, double z) =>
            new(
                new Vector3D(x, y, z),
                QuaternionD.Identity,
                Vector3D.One);

        RigDefinition source = new(
            "mixamo-source",
            "Mixamo source",
            [
                new BoneDefinition(0, "Armature", -1, At(0, 0, 0), BoneKind.Root),
                new BoneDefinition(1, "mixamorig:Hips", 0, At(0, 0, 0)),
                new BoneDefinition(2, "mixamorig:Spine", 1, At(0, 1, 0)),
                new BoneDefinition(3, "mixamorig:LeftShoulder", 2, At(-1, 1, 0)),
                new BoneDefinition(4, "mixamorig:LeftArm", 3, At(-0.5, 0, 0)),
                new BoneDefinition(5, "mixamorig:LeftForeArm", 4, At(-1, 0, 0)),
                new BoneDefinition(6, "mixamorig:LeftHand", 5, At(-1, 0, 0)),
                new BoneDefinition(7, "mixamorig:RightShoulder", 2, At(1, 1, 0)),
                new BoneDefinition(8, "mixamorig:RightArm", 7, At(0.5, 0, 0)),
                new BoneDefinition(9, "mixamorig:RightForeArm", 8, At(1, 0, 0)),
                new BoneDefinition(10, "mixamorig:RightHand", 9, At(1, 0, 0)),
                new BoneDefinition(11, "mixamorig:LeftUpLeg", 1, At(-0.5, -1, 0)),
                new BoneDefinition(12, "mixamorig:LeftLeg", 11, At(0, -1, 0)),
                new BoneDefinition(13, "mixamorig:LeftFoot", 12, At(0, -1, 0)),
                new BoneDefinition(14, "mixamorig:RightUpLeg", 1, At(0.5, -1, 0)),
                new BoneDefinition(15, "mixamorig:RightLeg", 14, At(0, -1, 0)),
                new BoneDefinition(16, "mixamorig:RightFoot", 15, At(0, -1, 0)),
            ]);
        RigDefinition target = new(
            "split-hip-target",
            "Split hip target",
            [
                new BoneDefinition(0, "spine", -1, At(0, 0, 0), BoneKind.Root),
                new BoneDefinition(1, "spine_001", 0, At(0, 0, 1)),
                new BoneDefinition(2, "l_clavicle", 1, At(-1, 0, 1)),
                new BoneDefinition(3, "l_upperarm", 2, At(-0.5, 0, 0)),
                new BoneDefinition(4, "l_forearm", 3, At(-1, 0, 0)),
                new BoneDefinition(5, "l_hand", 4, At(-1, 0, 0)),
                new BoneDefinition(6, "r_clavicle", 1, At(1, 0, 1)),
                new BoneDefinition(7, "r_upperarm", 6, At(0.5, 0, 0)),
                new BoneDefinition(8, "r_forearm", 7, At(1, 0, 0)),
                new BoneDefinition(9, "r_hand", 8, At(1, 0, 0)),
                new BoneDefinition(10, "l_thigh", 0, At(-0.5, 0, -1)),
                new BoneDefinition(11, "l_calf", 10, At(0, 0, -1)),
                new BoneDefinition(12, "l_foot", 11, At(0, 0, -1)),
                new BoneDefinition(13, "r_thigh", 0, At(0.5, 0, -1)),
                new BoneDefinition(14, "r_calf", 13, At(0, 0, -1)),
                new BoneDefinition(15, "r_foot", 14, At(0, 0, -1)),
            ]);
        var entries = new List<BoneMapEntry>
        {
            new(1, 0, BoneMappingMethod.Semantic, 0.9),
            new(2, 1, BoneMappingMethod.Semantic, 0.9),
        };
        int[] sourceAnatomical = [3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16];
        int[] targetAnatomical = [2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15];
        entries.AddRange(sourceAnatomical.Zip(
            targetAnatomical,
            static (sourceIndex, targetIndex) => new BoneMapEntry(
                sourceIndex,
                targetIndex,
                BoneMappingMethod.Semantic,
                0.9,
                transferPolicy: RetargetTransferPolicy.AnatomicalDirection,
                componentPolicy: RetargetComponentPolicy.Rotation)));
        RetargetMap map = new(source.Id, target.Id, entries);
        TransformTRS[] locals = source.Bones
            .Select(static bone => bone.LocalBindPose)
            .ToArray();
        locals[4] = locals[4] with
        {
            Rotation = QuaternionD.FromAxisAngle(
                Vector3D.UnitZ,
                -Math.PI / 2.0),
        };
        SkeletonPose sourcePose = new(source, locals);

        SkeletonPose result = PoseRetargeter.Retarget(
            sourcePose,
            target,
            map);
        Vector3D targetArmDirection = (
                result.GlobalMatrices[4].Translation -
                result.GlobalMatrices[3].Translation)
            .Normalized();

        Assert.True(
            Vector3D.Dot(targetArmDirection, Vector3D.UnitZ) > 0.95,
            $"Expected the raised source arm to follow target body-up; actual {targetArmDirection}.");
        Assert.True(
            Math.Abs(Vector3D.Dot(targetArmDirection, Vector3D.UnitY)) < 0.2,
            "A source-local fallback incorrectly raised the target arm along its unrelated local Y axis.");
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

    [Fact]
    [Trait("ValidationTier", "Focused")]
    [Trait("Gate", "CodecEvaluation")]
    public void ExactNativeDescriptorsUseRotationOnlyBindBasisAndRemainExportReady()
    {
        uint rootDescriptor = Dl1NameHash.Compute("root_native");
        uint armDescriptor = Dl1NameHash.Compute("arm_native");
        var source = new RigDefinition(
            "native-source",
            "native-source",
            [
                new BoneDefinition(
                    0,
                    "root_native",
                    -1,
                    new TransformTRS(
                        Vector3D.Zero,
                        QuaternionD.Identity,
                        new Vector3D(1.8, 0.9, 0.6)),
                    BoneKind.Root,
                    descriptorHash: rootDescriptor),
                new BoneDefinition(
                    1,
                    "arm_native",
                    0,
                    new TransformTRS(
                        new Vector3D(0.4, 0.8, 0.0),
                        QuaternionD.Identity,
                        Vector3D.One),
                    BoneKind.Deform,
                    descriptorHash: armDescriptor,
                    semanticRole: "arm.left.upper"),
            ]);
        var target = new RigDefinition(
            "prepared-target",
            "prepared-target",
            [
                new BoneDefinition(
                    0,
                    "root_native",
                    -1,
                    new TransformTRS(
                        Vector3D.Zero,
                        QuaternionD.Identity,
                        new Vector3D(1.2, 1.0, 0.8)),
                    BoneKind.Root,
                    descriptorHash: rootDescriptor),
                new BoneDefinition(
                    1,
                    "arm_native",
                    0,
                    new TransformTRS(
                        new Vector3D(0.0, 1.35, 0.0),
                        QuaternionD.Identity,
                        new Vector3D(0.75, 1.1, 1.0)),
                    BoneKind.Deform,
                    descriptorHash: armDescriptor,
                    semanticRole: "arm.left.upper"),
            ]);

        RetargetMap map =
            RetargetMapBuilder.CreateSuggested(source, target);
        RetargetMappingReviewReport review =
            RetargetMappingReview.Analyze(source, target, map);

        Assert.True(
            review.IsReady,
            string.Join(
                Environment.NewLine,
                review.Diagnostics.Select(static diagnostic =>
                    diagnostic.Message)));
        Assert.All(
            map.Entries,
            entry => Assert.Equal(
                RetargetTransferPolicy.GlobalRotationDelta,
                entry.TransferPolicy));
        Assert.All(
            map.Entries,
            entry => Assert.Equal(
                RetargetTransformComponents.Rotation,
                entry.TransformComponents));

        SkeletonPose targetBind = target.CreateBindPose();
        SkeletonPose sourceBind = source.CreateBindPose();
        SkeletonPose atBind = PoseRetargeter.Retarget(
            sourceBind,
            target,
            map);
        Assert.Equal(
            targetBind.LocalTransforms[1].Translation,
            atBind.LocalTransforms[1].Translation);
        Assert.Equal(
            targetBind.LocalTransforms[1].Scale,
            atBind.LocalTransforms[1].Scale);

        TransformTRS[] animatedSourceLocals =
            sourceBind.LocalTransforms.ToArray();
        animatedSourceLocals[1] = animatedSourceLocals[1] with
        {
            Rotation = QuaternionD.FromAxisAngle(
                Vector3D.UnitZ,
                Math.PI / 4.0),
        };
        SkeletonPose animatedTarget = PoseRetargeter.Retarget(
            new SkeletonPose(source, animatedSourceLocals),
            target,
            map);
        Assert.Equal(
            targetBind.LocalTransforms[1].Translation,
            animatedTarget.LocalTransforms[1].Translation);
        Assert.Equal(
            targetBind.LocalTransforms[1].Scale,
            animatedTarget.LocalTransforms[1].Scale);
        Assert.False(
            targetBind.GlobalMatrices[1].NearlyEquals(
                animatedTarget.GlobalMatrices[1]));
    }

    [Fact]
    [Trait("ValidationTier", "Focused")]
    [Trait("Gate", "CodecEvaluation")]
    public void RequiredTargetLeafFollowsCanonicalParentAndKeepsItsOwnBindLocal()
    {
        uint rootDescriptor = Dl1NameHash.Compute("root_native");
        uint parentDescriptor = Dl1NameHash.Compute("foot_native");
        uint sourceExtraDescriptor = Dl1NameHash.Compute("source_extra_leaf");
        uint targetLeafDescriptor = Dl1NameHash.Compute("target_required_leaf");
        var source = new RigDefinition(
            "native-source",
            "native-source",
            [
                new BoneDefinition(
                    0,
                    "root_native",
                    -1,
                    TransformTRS.Identity,
                    BoneKind.Root,
                    descriptorHash: rootDescriptor),
                new BoneDefinition(
                    1,
                    "foot_native",
                    0,
                    new TransformTRS(
                        Vector3D.UnitY,
                        QuaternionD.Identity,
                        Vector3D.One),
                    BoneKind.Deform,
                    descriptorHash: parentDescriptor,
                    semanticRole: "foot.left"),
                new BoneDefinition(
                    2,
                    "source_extra_leaf",
                    1,
                    new TransformTRS(
                        new Vector3D(0.0, 0.3, 0.1),
                        QuaternionD.Identity,
                        Vector3D.One),
                    BoneKind.Deform,
                    descriptorHash: sourceExtraDescriptor),
            ]);
        var target = new RigDefinition(
            "prepared-target",
            "prepared-target",
            [
                new BoneDefinition(
                    0,
                    "root_native",
                    -1,
                    TransformTRS.Identity,
                    BoneKind.Root,
                    descriptorHash: rootDescriptor),
                new BoneDefinition(
                    1,
                    "foot_native",
                    0,
                    new TransformTRS(
                        Vector3D.UnitY,
                        QuaternionD.Identity,
                        Vector3D.One),
                    BoneKind.Deform,
                    descriptorHash: parentDescriptor,
                    semanticRole: "foot.left"),
                new BoneDefinition(
                    2,
                    "target_required_leaf",
                    1,
                    new TransformTRS(
                        new Vector3D(0.15, 0.25, 0.05),
                        QuaternionD.Identity,
                        new Vector3D(0.9, 1.1, 1.0)),
                    BoneKind.Deform,
                    requiredForExport: true,
                    descriptorHash: targetLeafDescriptor),
            ]);

        RetargetMap map =
            RetargetMapBuilder.CreateSuggested(source, target);
        BoneMapEntry leafMapping = Assert.Single(
            map.Entries.Where(entry =>
                entry.TargetBoneIndex == 2));
        Assert.Equal(BoneMappingMethod.ParentFollow, leafMapping.Method);
        Assert.Equal(1, leafMapping.SourceBoneIndex);
        Assert.Equal(RetargetTransferPolicy.Bind, leafMapping.TransferPolicy);
        Assert.Equal(
            RetargetTransformComponents.All,
            leafMapping.TransformComponents);
        RetargetMappingReviewReport review =
            RetargetMappingReview.Analyze(source, target, map);
        Assert.True(
            review.IsReady,
            string.Join(
                Environment.NewLine,
                review.Diagnostics.Select(static diagnostic =>
                    diagnostic.Message)));
        Assert.Contains(
            review.Diagnostics,
            diagnostic =>
                diagnostic.Code == "source_has_extra_bones" &&
                diagnostic.Severity ==
                    CompatibilityDiagnosticSeverity.Information);

        TransformTRS[] animatedSourceLocals =
            source.CreateBindPose().LocalTransforms.ToArray();
        animatedSourceLocals[1] = animatedSourceLocals[1] with
        {
            Rotation = QuaternionD.FromAxisAngle(
                Vector3D.UnitZ,
                Math.PI / 3.0),
        };
        SkeletonPose targetBind = target.CreateBindPose();
        SkeletonPose result = PoseRetargeter.Retarget(
            new SkeletonPose(source, animatedSourceLocals),
            target,
            map);

        Assert.Equal(
            targetBind.LocalTransforms[2],
            result.LocalTransforms[2]);
        Assert.False(
            targetBind.GlobalMatrices[2].NearlyEquals(
                result.GlobalMatrices[2]));
    }

    [Fact]
    [Trait("ValidationTier", "Focused")]
    [Trait("Gate", "CodecEvaluation")]
    public void EyeAndSpineHelpersDoNotAcquireTargetBindScale()
    {
        Assert.Equal(
            RetargetComponentPolicy.Rotation,
            RetargetMapBuilder.GetDefaultHelperComponentPolicy("hspine1"));
        Assert.Equal(
            RetargetComponentPolicy.Rotation,
            RetargetMapBuilder.GetDefaultHelperComponentPolicy("l_eye"));
        Assert.Equal(
            RetargetComponentPolicy.RotationTranslation,
            RetargetMapBuilder.GetDefaultHelperComponentPolicy("l_eye_pos"));
        Assert.Equal(
            RetargetComponentPolicy.Rotation,
            RetargetMapBuilder.GetDefaultHelperComponentPolicy("r_eye"));
        Assert.Equal(
            RetargetComponentPolicy.RotationTranslation,
            RetargetMapBuilder.GetDefaultHelperComponentPolicy("r_eye_pos"));
    }

    [Fact]
    [Trait("ValidationTier", "Focused")]
    [Trait("Gate", "CodecEvaluation")]
    public void ExactDescriptorsUseRotationOnlyBindBasisAndKeepTargetProportions()
    {
        uint rootDescriptor = Dl1NameHash.Compute("root_native");
        uint limbDescriptor = Dl1NameHash.Compute("limb_native");
        var source = new RigDefinition(
            "native-source",
            "native-source",
            [
                new BoneDefinition(
                    0,
                    "root_native",
                    -1,
                    new TransformTRS(
                        Vector3D.Zero,
                        QuaternionD.Identity,
                        new Vector3D(1.8, 0.9, 0.6)),
                    BoneKind.Root,
                    descriptorHash: rootDescriptor),
                new BoneDefinition(
                    1,
                    "limb_native",
                    0,
                    new TransformTRS(
                        new Vector3D(0.4, 0.8, 0.0),
                        QuaternionD.Identity,
                        Vector3D.One),
                    BoneKind.Deform,
                    descriptorHash: limbDescriptor),
            ]);
        var target = new RigDefinition(
            "prepared-target",
            "prepared-target",
            [
                new BoneDefinition(
                    0,
                    "root_native",
                    -1,
                    new TransformTRS(
                        Vector3D.Zero,
                        QuaternionD.Identity,
                        new Vector3D(1.2, 1.0, 0.8)),
                    BoneKind.Root,
                    descriptorHash: rootDescriptor),
                new BoneDefinition(
                    1,
                    "limb_native",
                    0,
                    new TransformTRS(
                        new Vector3D(0.0, 1.35, 0.0),
                        QuaternionD.Identity,
                        new Vector3D(0.75, 1.1, 1.0)),
                    BoneKind.Deform,
                    descriptorHash: limbDescriptor),
            ]);

        RetargetMap mapping =
            RetargetMapBuilder.CreateSuggested(source, target);
        RetargetMappingReviewReport review =
            RetargetMappingReview.Analyze(source, target, mapping);
        Assert.True(
            review.IsReady,
            string.Join(
                Environment.NewLine,
                review.Diagnostics.Select(static diagnostic =>
                    diagnostic.Message)));
        Assert.All(
            mapping.Entries,
            entry => Assert.Equal(
                RetargetTransferPolicy.GlobalRotationDelta,
                entry.TransferPolicy));
        Assert.All(
            mapping.Entries,
            entry => Assert.Equal(
                RetargetTransformComponents.Rotation,
                entry.TransformComponents));

        var clip = new AnimationClip(
            "native-turn",
            new FrameRate(30, 1),
            2,
            transformTracks:
            [
                new TransformTrack(
                    1,
                    [
                        new TransformKeyframe(
                            0,
                            source.Bones[1].LocalBindPose),
                        new TransformKeyframe(
                            1,
                            source.Bones[1].LocalBindPose with
                            {
                                Rotation = QuaternionD.FromAxisAngle(
                                    Vector3D.UnitZ,
                                    Math.PI / 4.0),
                            }),
                    ]),
            ]);
        SkeletonPose targetBind = target.CreateBindPose();
        SkeletonPose sourceFrameZero = clip.SamplePose(source, 0.0);
        SkeletonPose sourceFrameOne = clip.SamplePose(
            source,
            clip.FrameRate.SecondsForFrame(1));
        SkeletonPose targetFrameZero = PoseRetargeter.Retarget(
            sourceFrameZero,
            target,
            mapping);
        SkeletonPose targetFrameOne = PoseRetargeter.Retarget(
            sourceFrameOne,
            target,
            mapping);

        for (int index = 0; index < target.BoneCount; index++)
        {
            Assert.Equal(
                targetBind.LocalTransforms[index].Translation,
                targetFrameZero.LocalTransforms[index].Translation);
            Assert.Equal(
                targetBind.LocalTransforms[index].Scale,
                targetFrameZero.LocalTransforms[index].Scale);
            Assert.Equal(
                targetBind.LocalTransforms[index].Translation,
                targetFrameOne.LocalTransforms[index].Translation);
            Assert.Equal(
                targetBind.LocalTransforms[index].Scale,
                targetFrameOne.LocalTransforms[index].Scale);
        }

        Assert.False(
            targetFrameZero.GlobalMatrices[1].NearlyEquals(
                targetFrameOne.GlobalMatrices[1]));
    }

    [Fact]
    [Trait("ValidationTier", "Focused")]
    [Trait("Gate", "CodecEvaluation")]
    public void RequiredTargetLeafFollowsMappedParentAndRetainsItsBindLocal()
    {
        uint rootDescriptor = Dl1NameHash.Compute("root_native");
        uint parentDescriptor = Dl1NameHash.Compute("parent_native");
        uint sourceExtraDescriptor = Dl1NameHash.Compute("source_extra_leaf");
        uint targetLeafDescriptor = Dl1NameHash.Compute("required_target_leaf");
        var source = new RigDefinition(
            "native-source",
            "native-source",
            [
                new BoneDefinition(
                    0,
                    "root_native",
                    -1,
                    TransformTRS.Identity,
                    BoneKind.Root,
                    descriptorHash: rootDescriptor),
                new BoneDefinition(
                    1,
                    "parent_native",
                    0,
                    new TransformTRS(
                        Vector3D.UnitY,
                        QuaternionD.Identity,
                        Vector3D.One),
                    BoneKind.Deform,
                    descriptorHash: parentDescriptor),
                new BoneDefinition(
                    2,
                    "source_extra_leaf",
                    0,
                    new TransformTRS(
                        new Vector3D(0.3, 0.2, 0.1),
                        QuaternionD.Identity,
                        Vector3D.One),
                    BoneKind.Deform,
                    descriptorHash: sourceExtraDescriptor),
            ]);
        var target = new RigDefinition(
            "prepared-target",
            "prepared-target",
            [
                new BoneDefinition(
                    0,
                    "root_native",
                    -1,
                    TransformTRS.Identity,
                    BoneKind.Root,
                    descriptorHash: rootDescriptor),
                new BoneDefinition(
                    1,
                    "parent_native",
                    0,
                    new TransformTRS(
                        Vector3D.UnitY,
                        QuaternionD.Identity,
                        Vector3D.One),
                    BoneKind.Deform,
                    descriptorHash: parentDescriptor),
                new BoneDefinition(
                    2,
                    "required_target_leaf",
                    1,
                    new TransformTRS(
                        new Vector3D(0.15, 0.25, 0.05),
                        QuaternionD.Identity,
                        new Vector3D(0.9, 1.1, 1.0)),
                    BoneKind.Deform,
                    requiredForExport: true,
                    descriptorHash: targetLeafDescriptor),
            ]);

        RetargetMap mapping =
            RetargetMapBuilder.CreateSuggested(source, target);
        BoneMapEntry leaf = Assert.Single(mapping.Entries.Where(entry =>
            entry.TargetBoneIndex == 2));
        Assert.Equal(BoneMappingMethod.ParentFollow, leaf.Method);
        Assert.Equal(1, leaf.SourceBoneIndex);
        Assert.Equal(RetargetTransferPolicy.Bind, leaf.TransferPolicy);
        Assert.Equal(
            RetargetTransformComponents.All,
            leaf.TransformComponents);
        RetargetMappingReviewReport review =
            RetargetMappingReview.Analyze(source, target, mapping);
        Assert.True(
            review.IsReady,
            string.Join(
                Environment.NewLine,
                review.Diagnostics.Select(static diagnostic =>
                    diagnostic.Message)));
        Assert.Contains(
            review.Diagnostics,
            diagnostic =>
                diagnostic.Code == "source_has_extra_bones" &&
                diagnostic.Severity ==
                    CompatibilityDiagnosticSeverity.Information);

        var clip = new AnimationClip(
            "parent-turn",
            new FrameRate(30, 1),
            2,
            transformTracks:
            [
                new TransformTrack(
                    1,
                    [
                        new TransformKeyframe(
                            0,
                            source.Bones[1].LocalBindPose),
                        new TransformKeyframe(
                            1,
                            source.Bones[1].LocalBindPose with
                            {
                                Rotation = QuaternionD.FromAxisAngle(
                                    Vector3D.UnitZ,
                                    Math.PI / 3.0),
                            }),
                    ]),
            ]);
        SkeletonPose targetBind = target.CreateBindPose();
        SkeletonPose first = PoseRetargeter.Retarget(
            clip.SamplePose(source, 0.0),
            target,
            mapping);
        SkeletonPose moving = PoseRetargeter.Retarget(
            clip.SamplePose(
                source,
                clip.FrameRate.SecondsForFrame(1)),
            target,
            mapping);
        Assert.Equal(
            targetBind.LocalTransforms[2],
            first.LocalTransforms[2]);
        Assert.Equal(
            targetBind.LocalTransforms[2],
            moving.LocalTransforms[2]);
        Assert.False(
            first.GlobalMatrices[2].NearlyEquals(
                moving.GlobalMatrices[2]));

        Dl1AuthoringPolicy policy = Dl1AuthoringPolicy.Create(
            source,
            target,
            mapping,
            AnimationRootMode.Recorded);
        Dl1Anm2AuthoringSequence authored =
            new Anm2EvaluationAdapter(new AnimationEvaluator())
                .SampleAuthoredFrames(
                    new EvaluationRequest(
                        source,
                        target,
                        clip,
                        0.0,
                        PreviewProfile.RawAuthoring,
                        mapping,
                        purpose: EvaluationPurpose.Preview,
                        dl1AuthoringPolicy: policy));
        Assert.Equal(2, authored.Frames.Length);
        Dl1Anm2TrackSample leafTrack = Assert.Single(
            authored.Frames[1].Tracks.Where(track =>
                track.BoneIndex == 2));
        Assert.Equal(
            targetBind.LocalTransforms[2],
            leafTrack.LocalTransform);
        Assert.Contains(
            authored.Frames[1].Tracks,
            track => track.DescriptorHash == targetLeafDescriptor);
    }

    [Fact]
    [Trait("ValidationTier", "Focused")]
    [Trait("Gate", "CodecEvaluation")]
    public void ParentFollowCannotHideNonLeafOrSourceIdentityMappings()
    {
        uint rootDescriptor = Dl1NameHash.Compute("root_native");
        uint parentDescriptor = Dl1NameHash.Compute("parent_native");
        uint leafDescriptor = Dl1NameHash.Compute("existing_source_leaf");
        var source = new RigDefinition(
            "source",
            "source",
            [
                new BoneDefinition(
                    0,
                    "root_native",
                    -1,
                    TransformTRS.Identity,
                    BoneKind.Root,
                    descriptorHash: rootDescriptor),
                new BoneDefinition(
                    1,
                    "parent_native",
                    0,
                    TransformTRS.Identity,
                    BoneKind.Deform,
                    descriptorHash: parentDescriptor),
                new BoneDefinition(
                    2,
                    "existing_source_leaf",
                    1,
                    TransformTRS.Identity,
                    BoneKind.Deform,
                    descriptorHash: leafDescriptor),
            ]);
        var target = new RigDefinition(
            "target",
            "target",
            [
                new BoneDefinition(
                    0,
                    "root_native",
                    -1,
                    TransformTRS.Identity,
                    BoneKind.Root,
                    descriptorHash: rootDescriptor),
                new BoneDefinition(
                    1,
                    "parent_native",
                    0,
                    TransformTRS.Identity,
                    BoneKind.Deform,
                    descriptorHash: parentDescriptor),
                new BoneDefinition(
                    2,
                    "nonleaf_target",
                    1,
                    TransformTRS.Identity,
                    BoneKind.Deform,
                    requiredForExport: true,
                    descriptorHash: Dl1NameHash.Compute("nonleaf_target")),
                new BoneDefinition(
                    3,
                    "child_target",
                    2,
                    TransformTRS.Identity,
                    BoneKind.Deform,
                    requiredForExport: true,
                    descriptorHash: Dl1NameHash.Compute("child_target")),
                new BoneDefinition(
                    4,
                    "existing_source_leaf",
                    1,
                    TransformTRS.Identity,
                    BoneKind.Deform,
                    requiredForExport: true,
                    descriptorHash: leafDescriptor),
            ]);
        var invalid = new RetargetMap(
            source.Id,
            target.Id,
            [
                new BoneMapEntry(
                    0,
                    0,
                    BoneMappingMethod.DescriptorHash,
                    1.0),
                new BoneMapEntry(
                    1,
                    1,
                    BoneMappingMethod.DescriptorHash,
                    1.0),
                new BoneMapEntry(
                    1,
                    2,
                    BoneMappingMethod.ParentFollow,
                    1.0,
                    transferPolicy: RetargetTransferPolicy.Bind,
                    componentPolicy: RetargetComponentPolicy.FullTransform),
                new BoneMapEntry(
                    1,
                    4,
                    BoneMappingMethod.ParentFollow,
                    1.0,
                    transferPolicy: RetargetTransferPolicy.Bind,
                    componentPolicy: RetargetComponentPolicy.FullTransform),
            ]);

        RetargetMappingReviewReport review =
            RetargetMappingReview.Analyze(source, target, invalid);

        Assert.False(review.IsReady);
        Assert.True(
            review.Diagnostics.Count(diagnostic =>
                diagnostic.Code ==
                    "deterministic_mapping_identity_mismatch") >= 2);
        Assert.Contains(
            review.Diagnostics,
            diagnostic =>
                diagnostic.Code == "required_target_unmapped" &&
                diagnostic.TargetBoneName == "child_target");
    }

    [Fact]
    [Trait("ValidationTier", "Focused")]
    [Trait("Gate", "CodecEvaluation")]
    public void ToeAndHeadTerminalLeavesMayFollowTheirVerifiedCanonicalParents()
    {
        uint rootDescriptor = Dl1NameHash.Compute("root_native");
        uint footDescriptor = Dl1NameHash.Compute("foot_native");
        uint headDescriptor = Dl1NameHash.Compute("head_native");
        var source = new RigDefinition(
            "native-source",
            "native-source",
            [
                new BoneDefinition(
                    0,
                    "root_native",
                    -1,
                    TransformTRS.Identity,
                    BoneKind.Root,
                    descriptorHash: rootDescriptor),
                new BoneDefinition(
                    1,
                    "foot_native",
                    0,
                    TransformTRS.Identity,
                    BoneKind.Deform,
                    descriptorHash: footDescriptor,
                    semanticRole: "foot.right"),
                new BoneDefinition(
                    2,
                    "head_native",
                    0,
                    TransformTRS.Identity,
                    BoneKind.Deform,
                    descriptorHash: headDescriptor,
                    semanticRole: "body.head"),
            ]);
        var target = new RigDefinition(
            "prepared-target",
            "prepared-target",
            [
                new BoneDefinition(
                    0,
                    "root_native",
                    -1,
                    TransformTRS.Identity,
                    BoneKind.Root,
                    descriptorHash: rootDescriptor),
                new BoneDefinition(
                    1,
                    "foot_native",
                    0,
                    TransformTRS.Identity,
                    BoneKind.Deform,
                    descriptorHash: footDescriptor,
                    semanticRole: "foot.right"),
                new BoneDefinition(
                    2,
                    "head_native",
                    0,
                    TransformTRS.Identity,
                    BoneKind.Deform,
                    descriptorHash: headDescriptor,
                    semanticRole: "body.head"),
                new BoneDefinition(
                    3,
                    "right_toe_end",
                    1,
                    TransformTRS.Identity,
                    BoneKind.Helper,
                    requiredForExport: true,
                    descriptorHash: Dl1NameHash.Compute("target_toe_end"),
                    semanticRole: "foot.right"),
                new BoneDefinition(
                    4,
                    "head_top_end",
                    2,
                    TransformTRS.Identity,
                    BoneKind.Helper,
                    requiredForExport: true,
                    descriptorHash: Dl1NameHash.Compute("target_head_end"),
                    semanticRole: "body.head"),
            ]);

        RetargetMap mapping =
            RetargetMapBuilder.CreateSuggested(source, target);
        Assert.Equal(
            2,
            mapping.Entries.Count(entry =>
                entry.Method == BoneMappingMethod.ParentFollow));
        RetargetMappingReviewReport review =
            RetargetMappingReview.Analyze(source, target, mapping);

        Assert.True(
            review.IsReady,
            string.Join(
                Environment.NewLine,
                review.Diagnostics.Select(static diagnostic =>
                    diagnostic.Message)));

        var clip = new AnimationClip(
            "terminal-parent-turn",
            new FrameRate(30, 1),
            2,
            transformTracks:
            [
                new TransformTrack(
                    1,
                    [
                        new TransformKeyframe(0, source.Bones[1].LocalBindPose),
                        new TransformKeyframe(
                            1,
                            source.Bones[1].LocalBindPose with
                            {
                                Rotation = QuaternionD.FromAxisAngle(
                                    Vector3D.UnitZ,
                                    Math.PI / 4.0),
                            }),
                    ]),
                new TransformTrack(
                    2,
                    [
                        new TransformKeyframe(0, source.Bones[2].LocalBindPose),
                        new TransformKeyframe(
                            1,
                            source.Bones[2].LocalBindPose with
                            {
                                Rotation = QuaternionD.FromAxisAngle(
                                    Vector3D.UnitX,
                                    Math.PI / 6.0),
                            }),
                    ]),
            ]);
        SkeletonPose targetBind = target.CreateBindPose();
        SkeletonPose sourceFirst = clip.SamplePose(source, 0.0);
        SkeletonPose sourceMoving = clip.SamplePose(
            source,
            clip.FrameRate.SecondsForFrame(1));
        SkeletonPose targetFirst = PoseRetargeter.Retarget(
            sourceFirst,
            target,
            mapping);
        SkeletonPose targetMoving = PoseRetargeter.Retarget(
            sourceMoving,
            target,
            mapping);
        foreach (int leafIndex in new[] { 3, 4 })
        {
            Assert.Equal(
                targetBind.LocalTransforms[leafIndex],
                targetMoving.LocalTransforms[leafIndex]);
            Assert.False(
                targetFirst.GlobalMatrices[leafIndex].NearlyEquals(
                    targetMoving.GlobalMatrices[leafIndex]));
        }

        Dl1AuthoringPolicy policy = Dl1AuthoringPolicy.Create(
            source,
            target,
            mapping,
            AnimationRootMode.Recorded);
        Dl1Anm2AuthoringSequence authored =
            new Anm2EvaluationAdapter(new AnimationEvaluator())
                .SampleAuthoredFrames(
                    new EvaluationRequest(
                        source,
                        target,
                        clip,
                        0.0,
                        PreviewProfile.RawAuthoring,
                        mapping,
                        purpose: EvaluationPurpose.Preview,
                        dl1AuthoringPolicy: policy));
        Assert.Equal(2, authored.Frames.Length);
        foreach (int leafIndex in new[] { 3, 4 })
        {
            Assert.Contains(
                authored.Frames[1].Tracks,
                track =>
                    track.BoneIndex == leafIndex &&
                    track.DescriptorHash ==
                        target.Bones[leafIndex].DescriptorHash);
            Dl1Anm2TrackSample leafTrack = authored.Frames[1].Tracks
                .Single(track => track.BoneIndex == leafIndex);
            Assert.Equal(
                targetBind.LocalTransforms[leafIndex],
                leafTrack.LocalTransform);
        }
    }

    [Fact]
    [Trait("ValidationTier", "Focused")]
    [Trait("Gate", "CodecEvaluation")]
    public void FourthFingerHelperFollowsItsMappedThirdSegmentParent()
    {
        uint rootDescriptor = Dl1NameHash.Compute("generic_root");
        uint parentDescriptor = Dl1NameHash.Compute("generic_finger_parent");
        uint leafDescriptor = Dl1NameHash.Compute("target_finger_leaf");
        var source = new RigDefinition(
            "source",
            "source",
            [
                new BoneDefinition(
                    0,
                    "generic_root",
                    -1,
                    TransformTRS.Identity,
                    BoneKind.Root,
                    descriptorHash: rootDescriptor),
                new BoneDefinition(
                    1,
                    "left_hand_index3",
                    0,
                    new TransformTRS(
                        Vector3D.UnitX,
                        QuaternionD.Identity,
                        Vector3D.One),
                    BoneKind.Deform,
                    descriptorHash: parentDescriptor),
            ]);
        var target = new RigDefinition(
            "target",
            "target",
            [
                new BoneDefinition(
                    0,
                    "generic_root",
                    -1,
                    TransformTRS.Identity,
                    BoneKind.Root,
                    descriptorHash: rootDescriptor),
                new BoneDefinition(
                    1,
                    "left_hand_index3",
                    0,
                    new TransformTRS(
                        Vector3D.UnitX,
                        QuaternionD.Identity,
                        Vector3D.One),
                    BoneKind.Deform,
                    descriptorHash: parentDescriptor),
                new BoneDefinition(
                    2,
                    "left_hand_index4",
                    1,
                    new TransformTRS(
                        new Vector3D(0.2, 0.0, 0.0),
                        QuaternionD.Identity,
                        new Vector3D(1.0, 0.9, 1.1)),
                    BoneKind.Helper,
                    requiredForExport: true,
                    descriptorHash: leafDescriptor),
            ]);

        RetargetMap mapping =
            RetargetMapBuilder.CreateSuggested(source, target);
        BoneMapEntry leaf = Assert.Single(mapping.Entries.Where(entry =>
            entry.TargetBoneIndex == 2));
        Assert.Equal(BoneMappingMethod.ParentFollow, leaf.Method);
        Assert.Equal(1, leaf.SourceBoneIndex);
        Assert.True(
            RetargetMappingReview.Analyze(
                source,
                target,
                mapping).IsReady);

        var clip = new AnimationClip(
            "finger-parent-turn",
            new FrameRate(30, 1),
            2,
            transformTracks:
            [
                new TransformTrack(
                    1,
                    [
                        new TransformKeyframe(
                            0,
                            source.Bones[1].LocalBindPose),
                        new TransformKeyframe(
                            1,
                            source.Bones[1].LocalBindPose with
                            {
                                Rotation = QuaternionD.FromAxisAngle(
                                    Vector3D.UnitZ,
                                    Math.PI / 4.0),
                            }),
                    ]),
            ]);
        SkeletonPose bind = target.CreateBindPose();
        SkeletonPose first = PoseRetargeter.Retarget(
            clip.SamplePose(source, 0.0),
            target,
            mapping);
        SkeletonPose moving = PoseRetargeter.Retarget(
            clip.SamplePose(
                source,
                clip.FrameRate.SecondsForFrame(1)),
            target,
            mapping);
        Assert.Equal(bind.LocalTransforms[2], moving.LocalTransforms[2]);
        Assert.False(
            first.GlobalMatrices[2].NearlyEquals(
                moving.GlobalMatrices[2]));
    }

    [Fact]
    [Trait("ValidationTier", "Focused")]
    [Trait("Gate", "CodecEvaluation")]
    public void FourthFingerHelperAuthoredFramesFollowTheMappedThirdSegment()
    {
        uint rootDescriptor = Dl1NameHash.Compute("generic_root");
        uint parentDescriptor = Dl1NameHash.Compute("finger_parent");
        uint leafDescriptor = Dl1NameHash.Compute("target_finger_leaf");
        var source = new RigDefinition(
            "source",
            "source",
            [
                new BoneDefinition(
                    0,
                    "generic_root",
                    -1,
                    TransformTRS.Identity,
                    BoneKind.Root,
                    descriptorHash: rootDescriptor),
                new BoneDefinition(
                    1,
                    "left_hand_index3",
                    0,
                    new TransformTRS(
                        Vector3D.UnitX,
                        QuaternionD.Identity,
                        Vector3D.One),
                    BoneKind.Deform,
                    descriptorHash: parentDescriptor),
            ]);
        var target = new RigDefinition(
            "target",
            "target",
            [
                new BoneDefinition(
                    0,
                    "generic_root",
                    -1,
                    TransformTRS.Identity,
                    BoneKind.Root,
                    descriptorHash: rootDescriptor),
                new BoneDefinition(
                    1,
                    "left_hand_index3",
                    0,
                    new TransformTRS(
                        Vector3D.UnitX,
                        QuaternionD.Identity,
                        Vector3D.One),
                    BoneKind.Deform,
                    descriptorHash: parentDescriptor),
                new BoneDefinition(
                    2,
                    "left_hand_index4",
                    1,
                    new TransformTRS(
                        new Vector3D(0.2, 0.0, 0.0),
                        QuaternionD.Identity,
                        new Vector3D(1.0, 0.9, 1.1)),
                    BoneKind.Helper,
                    requiredForExport: true,
                    descriptorHash: leafDescriptor),
            ]);

        RetargetMap mapping =
            RetargetMapBuilder.CreateSuggested(source, target);
        BoneMapEntry leaf = Assert.Single(mapping.Entries.Where(entry =>
            entry.TargetBoneIndex == 2));
        Assert.Equal(BoneMappingMethod.ParentFollow, leaf.Method);
        RetargetMappingReviewReport review =
            RetargetMappingReview.Analyze(source, target, mapping);
        Assert.True(review.IsReady);

        var clip = new AnimationClip(
            "finger-parent-turn",
            new FrameRate(30, 1),
            2,
            transformTracks:
            [
                new TransformTrack(
                    1,
                    [
                        new TransformKeyframe(0, source.Bones[1].LocalBindPose),
                        new TransformKeyframe(
                            1,
                            source.Bones[1].LocalBindPose with
                            {
                                Rotation = QuaternionD.FromAxisAngle(
                                    Vector3D.UnitZ,
                                    Math.PI / 4.0),
                            }),
                    ]),
            ]);
        SkeletonPose targetBind = target.CreateBindPose();
        var policy = Dl1AuthoringPolicy.Create(
            source,
            target,
            mapping,
            AnimationRootMode.Recorded);
        Dl1Anm2AuthoringSequence authored =
            new Anm2EvaluationAdapter(new AnimationEvaluator())
                .SampleAuthoredFrames(
                    new EvaluationRequest(
                        source,
                        target,
                        clip,
                        0.0,
                        PreviewProfile.RawAuthoring,
                        mapping,
                        purpose: EvaluationPurpose.Preview,
                        dl1AuthoringPolicy: policy));
        Assert.Equal(2, authored.Frames.Length);
        Dl1Anm2TrackSample leafTrack = authored.Frames[1].Tracks
            .Single(track => track.BoneIndex == 2);
        Assert.Equal(leafDescriptor, leafTrack.DescriptorHash);
        Assert.Equal(targetBind.LocalTransforms[2], leafTrack.LocalTransform);
    }

    [Fact]
    [Trait("ValidationTier", "Focused")]
    [Trait("Gate", "CodecEvaluation")]
    public void MismatchedFingerParentRoleDoesNotQualifyForParentFollow()
    {
        uint rootDescriptor = Dl1NameHash.Compute("generic_root");
        uint leftParentDescriptor = Dl1NameHash.Compute("left_finger_parent");
        uint rightParentDescriptor = Dl1NameHash.Compute("right_finger_parent");
        var source = new RigDefinition(
            "source",
            "source",
            [
                new BoneDefinition(
                    0,
                    "generic_root",
                    -1,
                    TransformTRS.Identity,
                    BoneKind.Root,
                    descriptorHash: rootDescriptor),
                new BoneDefinition(
                    1,
                    "left_hand_index3",
                    0,
                    TransformTRS.Identity,
                    BoneKind.Deform,
                    descriptorHash: leftParentDescriptor),
                new BoneDefinition(
                    2,
                    "right_hand_index3",
                    0,
                    TransformTRS.Identity,
                    BoneKind.Deform,
                    descriptorHash: rightParentDescriptor),
            ]);
        var target = new RigDefinition(
            "target",
            "target",
            [
                new BoneDefinition(
                    0,
                    "generic_root",
                    -1,
                    TransformTRS.Identity,
                    BoneKind.Root,
                    descriptorHash: rootDescriptor),
                new BoneDefinition(
                    1,
                    "left_hand_index3",
                    0,
                    TransformTRS.Identity,
                    BoneKind.Deform,
                    descriptorHash: leftParentDescriptor),
                new BoneDefinition(
                    2,
                    "right_hand_index4",
                    1,
                    TransformTRS.Identity,
                    BoneKind.Helper,
                    requiredForExport: true,
                    descriptorHash: Dl1NameHash.Compute("target_right_finger_leaf")),
            ]);

        RetargetMap mapping =
            RetargetMapBuilder.CreateSuggested(source, target);
        Assert.DoesNotContain(
            mapping.Entries,
            entry =>
                entry.TargetBoneIndex == 2 &&
                entry.Method == BoneMappingMethod.ParentFollow);
        Assert.False(
            RetargetMappingReview.Analyze(
                source,
                target,
                mapping).IsReady);
    }

    [Fact]
    [Trait("ValidationTier", "Focused")]
    [Trait("Gate", "CodecEvaluation")]
    public void SemanticGuessDoesNotMakeHeadBlendATerminalMarker()
    {
        uint rootDescriptor = Dl1NameHash.Compute("generic_root");
        uint headDescriptor = Dl1NameHash.Compute("generic_head");
        var source = new RigDefinition(
            "source",
            "source",
            [
                new BoneDefinition(
                    0,
                    "generic_root",
                    -1,
                    TransformTRS.Identity,
                    BoneKind.Root,
                    descriptorHash: rootDescriptor),
                new BoneDefinition(
                    1,
                    "generic_head",
                    0,
                    TransformTRS.Identity,
                    BoneKind.Deform,
                    descriptorHash: headDescriptor,
                    semanticRole: "body.head"),
            ]);
        var target = new RigDefinition(
            "target",
            "target",
            [
                new BoneDefinition(
                    0,
                    "generic_root",
                    -1,
                    TransformTRS.Identity,
                    BoneKind.Root,
                    descriptorHash: rootDescriptor),
                new BoneDefinition(
                    1,
                    "generic_head",
                    0,
                    TransformTRS.Identity,
                    BoneKind.Deform,
                    descriptorHash: headDescriptor,
                    semanticRole: "body.head"),
                new BoneDefinition(
                    2,
                    "head_blend",
                    1,
                    TransformTRS.Identity,
                    BoneKind.Deform,
                    requiredForExport: true,
                    descriptorHash: Dl1NameHash.Compute("target_head_blend"),
                    semanticRole: "body.head"),
            ]);

        RetargetMap mapping =
            RetargetMapBuilder.CreateSuggested(source, target);

        Assert.DoesNotContain(
            mapping.Entries,
            entry =>
                entry.TargetBoneIndex == 2 &&
                entry.Method == BoneMappingMethod.ParentFollow);
        Assert.False(
            RetargetMappingReview.Analyze(
                source,
                target,
                mapping).IsReady);
    }

    [Fact]
    [Trait("ValidationTier", "Focused")]
    [Trait("Gate", "CodecEvaluation")]
    public void ParentFollowRejectsNonLeafAndExistingSourceIdentity()
    {
        uint rootDescriptor = Dl1NameHash.Compute("root_native");
        uint parentDescriptor = Dl1NameHash.Compute("parent_native");
        uint existingLeafDescriptor = Dl1NameHash.Compute("existing_leaf");
        var source = new RigDefinition(
            "source",
            "source",
            [
                new BoneDefinition(
                    0,
                    "root_native",
                    -1,
                    TransformTRS.Identity,
                    BoneKind.Root,
                    descriptorHash: rootDescriptor),
                new BoneDefinition(
                    1,
                    "parent_native",
                    0,
                    TransformTRS.Identity,
                    BoneKind.Deform,
                    descriptorHash: parentDescriptor),
                new BoneDefinition(
                    2,
                    "existing_leaf",
                    1,
                    TransformTRS.Identity,
                    BoneKind.Deform,
                    descriptorHash: existingLeafDescriptor),
            ]);
        var target = new RigDefinition(
            "target",
            "target",
            [
                new BoneDefinition(
                    0,
                    "root_native",
                    -1,
                    TransformTRS.Identity,
                    BoneKind.Root,
                    descriptorHash: rootDescriptor),
                new BoneDefinition(
                    1,
                    "parent_native",
                    0,
                    TransformTRS.Identity,
                    BoneKind.Deform,
                    descriptorHash: parentDescriptor),
                new BoneDefinition(
                    2,
                    "unmapped_nonleaf",
                    1,
                    TransformTRS.Identity,
                    BoneKind.Deform,
                    requiredForExport: true,
                    descriptorHash: Dl1NameHash.Compute("unmapped_nonleaf")),
                new BoneDefinition(
                    3,
                    "nonleaf_child",
                    2,
                    TransformTRS.Identity,
                    BoneKind.Deform,
                    requiredForExport: true,
                    descriptorHash: Dl1NameHash.Compute("nonleaf_child")),
                new BoneDefinition(
                    4,
                    "existing_leaf",
                    1,
                    TransformTRS.Identity,
                    BoneKind.Deform,
                    requiredForExport: true,
                    descriptorHash: existingLeafDescriptor),
            ]);
        var mapping = new RetargetMap(
            source.Id,
            target.Id,
            [
                new BoneMapEntry(
                    0,
                    0,
                    BoneMappingMethod.DescriptorHash,
                    1.0),
                new BoneMapEntry(
                    1,
                    1,
                    BoneMappingMethod.DescriptorHash,
                    1.0),
                new BoneMapEntry(
                    1,
                    2,
                    BoneMappingMethod.ParentFollow,
                    1.0,
                    transferPolicy: RetargetTransferPolicy.Bind,
                    componentPolicy: RetargetComponentPolicy.FullTransform),
                new BoneMapEntry(
                    1,
                    4,
                    BoneMappingMethod.ParentFollow,
                    1.0,
                    transferPolicy: RetargetTransferPolicy.Bind,
                    componentPolicy: RetargetComponentPolicy.FullTransform),
            ]);

        RetargetMappingReviewReport review =
            RetargetMappingReview.Analyze(source, target, mapping);

        Assert.False(review.IsReady);
        Assert.True(
            review.Diagnostics.Count(diagnostic =>
                diagnostic.Code ==
                    "deterministic_mapping_identity_mismatch") >= 2);
        Assert.Contains(
            review.Diagnostics,
            diagnostic =>
                diagnostic.Code == "required_target_unmapped" &&
                diagnostic.TargetBoneName == "nonleaf_child");
    }

    [Fact]
    [Trait("ValidationTier", "Focused")]
    [Trait("Gate", "CodecEvaluation")]
    public void CanonicalHelperOverridesPreserveEyeAndSpineBindScale()
    {
        Assert.Equal(
            RetargetComponentPolicy.Rotation,
            RetargetMapBuilder.GetDefaultHelperComponentPolicy("hspine1"));
        Assert.Equal(
            RetargetComponentPolicy.Rotation,
            RetargetMapBuilder.GetDefaultHelperComponentPolicy("l_eye"));
        Assert.Equal(
            RetargetComponentPolicy.RotationTranslation,
            RetargetMapBuilder.GetDefaultHelperComponentPolicy("l_eye_pos"));
        Assert.Equal(
            RetargetComponentPolicy.Rotation,
            RetargetMapBuilder.GetDefaultHelperComponentPolicy("r_eye"));
        Assert.Equal(
            RetargetComponentPolicy.RotationTranslation,
            RetargetMapBuilder.GetDefaultHelperComponentPolicy("r_eye_pos"));
    }

    private static RigDefinition CreateRig(
        string id,
        params (string Name, int Parent, bool Required)[] rows) =>
        CreateRig(
            id,
            rows.Select((row, index) => (
                row.Name,
                row.Parent,
                row.Required,
                index == 0 ? BoneKind.Root : BoneKind.Deform)).ToArray());

    private static RigDefinition CreateRig(
        string id,
        params (string Name, int Parent, bool Required, BoneKind Kind)[] rows) =>
        new(
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
                        row.Kind,
                        row.Required)));

    private static TransformTRS OffsetBind() =>
        new(
            Vector3D.UnitY,
            QuaternionD.Identity,
            Vector3D.One);
}
