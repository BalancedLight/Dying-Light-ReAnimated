using ReAnimated.Core.Domain;
using ReAnimated.Core.Mathematics;
using ReAnimated.Retargeting;
using ReAnimated.Retargeting.Mapping;

namespace ReAnimated.Tests;

public sealed class HelperRetargetPolicyTests
{
    private const double Tolerance = 1e-9;

    [Theory]
    [InlineData(RetargetComponentPolicy.Rotation)]
    [InlineData(RetargetComponentPolicy.Translation)]
    [InlineData(RetargetComponentPolicy.RotationTranslation)]
    [InlineData(RetargetComponentPolicy.Scale)]
    [InlineData(RetargetComponentPolicy.FullTransform)]
    public void ComponentPoliciesReplaceOnlyOwnedBindComponents(
        RetargetComponentPolicy componentPolicy)
    {
        TransformTRS sourceAnimated = new(
            new Vector3D(4.0, -2.0, 7.0),
            Rotation(Vector3D.UnitZ, 42.0),
            new Vector3D(1.4, 0.8, 1.2));
        RigDefinition source = Rig(
            "component-source",
            new BoneDefinition(
                0,
                "Driver",
                -1,
                TransformTRS.Identity,
                BoneKind.Root));
        SkeletonPose sourcePose = new(source, [sourceAnimated]);

        TransformTRS targetBind = new(
            new Vector3D(-3.0, 5.0, 2.0),
            Rotation(Vector3D.UnitX, -17.0),
            new Vector3D(0.7, 1.1, 1.3));
        RigDefinition target = Rig(
            "component-target",
            new BoneDefinition(
                0,
                "Helper",
                -1,
                targetBind,
                BoneKind.Helper,
                requiredForExport: false));
        RetargetMap map = Map(
            source,
            target,
            Entry(
                0,
                0,
                RetargetTransferPolicy.CopyLocal,
                componentPolicy));

        TransformTRS actual =
            PoseRetargeter.Retarget(sourcePose, target, map)
                .LocalTransforms[0];
        TransformTRS expected = componentPolicy switch
        {
            RetargetComponentPolicy.Rotation =>
                new(targetBind.Translation, sourceAnimated.Rotation, targetBind.Scale),
            RetargetComponentPolicy.Translation =>
                new(sourceAnimated.Translation, targetBind.Rotation, targetBind.Scale),
            RetargetComponentPolicy.RotationTranslation =>
                new(sourceAnimated.Translation, sourceAnimated.Rotation, targetBind.Scale),
            RetargetComponentPolicy.Scale =>
                new(targetBind.Translation, targetBind.Rotation, sourceAnimated.Scale),
            RetargetComponentPolicy.FullTransform => sourceAnimated,
            _ => throw new ArgumentOutOfRangeException(
                nameof(componentPolicy),
                componentPolicy,
                null),
        };

        AssertTransformNear(expected, actual);
    }

    [Theory]
    [InlineData(RetargetTransferPolicy.GlobalBindBasis)]
    [InlineData(RetargetTransferPolicy.RestRelative)]
    [InlineData(RetargetTransferPolicy.RotationDelta)]
    [InlineData(RetargetTransferPolicy.GlobalRotationDelta)]
    [InlineData(RetargetTransferPolicy.CopyLocal)]
    [InlineData(RetargetTransferPolicy.Bind)]
    public void TransferPoliciesMatchTheirTargetLocalContracts(
        RetargetTransferPolicy transferPolicy)
    {
        TransformTRS sourceRootBind = new(
            new Vector3D(0.5, -0.2, 0.1),
            Rotation(Vector3D.UnitY, 12.0),
            Vector3D.One);
        TransformTRS sourceDriverBind = new(
            new Vector3D(0.0, 1.0, 0.2),
            Rotation(Vector3D.UnitX, -8.0),
            Vector3D.One);
        TransformTRS sourceRootAnimated = new(
            new Vector3D(1.5, 0.4, -0.8),
            Rotation(Vector3D.UnitY, 37.0),
            Vector3D.One);
        TransformTRS sourceDriverAnimated = new(
            new Vector3D(0.3, 1.2, -0.1),
            Rotation(Vector3D.UnitZ, 28.0),
            Vector3D.One);
        RigDefinition source = Rig(
            "transfer-source",
            new BoneDefinition(
                0,
                "Root",
                -1,
                sourceRootBind,
                BoneKind.Root),
            new BoneDefinition(
                1,
                "Driver",
                0,
                sourceDriverBind));
        SkeletonPose sourcePose = new(
            source,
            [sourceRootAnimated, sourceDriverAnimated]);

        TransformTRS targetRootBind = new(
            new Vector3D(-0.5, 0.1, 0.7),
            Rotation(Vector3D.UnitY, -19.0),
            Vector3D.One);
        TransformTRS targetHelperBind = new(
            new Vector3D(0.2, 1.7, -0.35),
            Rotation(Vector3D.UnitX, 11.0),
            new Vector3D(1.2, 1.2, 1.2));
        RigDefinition target = Rig(
            "transfer-target",
            new BoneDefinition(
                0,
                "Root",
                -1,
                targetRootBind,
                BoneKind.Root),
            new BoneDefinition(
                1,
                "CameraHelper",
                0,
                targetHelperBind,
                BoneKind.Camera,
                requiredForExport: false));
        RetargetMap map = Map(
            source,
            target,
            new BoneMapEntry(
                0,
                0,
                BoneMappingMethod.ExactName,
                1.0),
            Entry(
                1,
                1,
                transferPolicy,
                RetargetComponentPolicy.FullTransform));

        SkeletonPose actual =
            PoseRetargeter.Retarget(sourcePose, target, map);

        TransformMatrix targetRootGlobal =
            sourceRootAnimated.ToMatrix() *
            sourceRootBind.ToMatrix().InvertedAffine() *
            targetRootBind.ToMatrix();
        TransformMatrix expectedHelperLocal = transferPolicy switch
        {
            RetargetTransferPolicy.GlobalBindBasis =>
                targetRootGlobal.InvertedAffine() *
                (
                    sourcePose.GlobalMatrices[1] *
                    source.CreateBindPose().GlobalMatrices[1].InvertedAffine() *
                    target.CreateBindPose().GlobalMatrices[1]
                ),
            RetargetTransferPolicy.RestRelative =>
                targetHelperBind.ToMatrix() *
                sourceDriverBind.ToMatrix().InvertedAffine() *
                sourceDriverAnimated.ToMatrix(),
            RetargetTransferPolicy.RotationDelta =>
                new TransformTRS(
                    targetHelperBind.Translation,
                    (
                        targetHelperBind.Rotation *
                        sourceDriverBind.Rotation.Inverse() *
                        sourceDriverAnimated.Rotation
                    ).Normalized(),
                    targetHelperBind.Scale)
                .ToMatrix(),
            RetargetTransferPolicy.GlobalRotationDelta =>
                new TransformTRS(
                    targetHelperBind.Translation,
                    (
                        targetRootGlobal.Decompose()
                            .Rotation.Inverse() *
                        (
                            (
                                sourceRootAnimated.Rotation *
                                sourceDriverAnimated.Rotation
                            ) *
                            (
                                sourceRootBind.Rotation *
                                sourceDriverBind.Rotation
                            ).Inverse() *
                            (
                                targetRootBind.Rotation *
                                targetHelperBind.Rotation
                            )
                        )
                    ).Normalized(),
                    targetHelperBind.Scale)
                .ToMatrix(),
            RetargetTransferPolicy.CopyLocal =>
                sourceDriverAnimated.ToMatrix(),
            RetargetTransferPolicy.Bind =>
                targetHelperBind.ToMatrix(),
            _ => throw new ArgumentOutOfRangeException(
                nameof(transferPolicy),
                transferPolicy,
                null),
        };

        AssertMatrixNear(
            expectedHelperLocal,
            actual.LocalTransforms[1].ToMatrix());
    }

    [Fact]
    public void HeadCanDriveBodyAndDistinctCameraHelperBinds()
    {
        TransformTRS sourceHeadBind = new(
            new Vector3D(0.0, 1.0, 0.0),
            QuaternionD.Identity,
            Vector3D.One);
        TransformTRS sourceHeadAnimated = new(
            new Vector3D(0.25, 1.15, -0.2),
            Rotation(Vector3D.UnitZ, 30.0),
            Vector3D.One);
        RigDefinition source = Rig(
            "fanout-source",
            new BoneDefinition(
                0,
                "Root",
                -1,
                TransformTRS.Identity,
                BoneKind.Root),
            new BoneDefinition(
                1,
                "Head",
                0,
                sourceHeadBind));
        SkeletonPose sourcePose = new(
            source,
            [TransformTRS.Identity, sourceHeadAnimated]);

        TransformTRS targetHeadBind = new(
            new Vector3D(0.0, 1.4, 0.0),
            Rotation(Vector3D.UnitY, 7.0),
            Vector3D.One);
        TransformTRS refCameraBind = new(
            new Vector3D(0.12, 0.20, -0.08),
            Rotation(Vector3D.UnitX, 9.0),
            new Vector3D(1.25, 1.25, 1.25));
        TransformTRS eyeCameraBind = new(
            new Vector3D(-0.04, 0.32, 0.10),
            Rotation(Vector3D.UnitX, -6.0),
            new Vector3D(0.85, 0.85, 0.85));
        TransformTRS unmappedBind = new(
            new Vector3D(0.3, -0.1, 0.2),
            Rotation(Vector3D.UnitY, 13.0),
            new Vector3D(1.1, 1.1, 1.1));
        RigDefinition target = Rig(
            "fanout-target",
            new BoneDefinition(
                0,
                "Root",
                -1,
                TransformTRS.Identity,
                BoneKind.Root),
            new BoneDefinition(
                1,
                "Head",
                0,
                targetHeadBind),
            new BoneDefinition(
                2,
                "RefCamera",
                1,
                refCameraBind,
                BoneKind.Camera,
                requiredForExport: false),
            new BoneDefinition(
                3,
                "EyeCamera",
                1,
                eyeCameraBind,
                BoneKind.Camera,
                requiredForExport: false),
            new BoneDefinition(
                4,
                "UnmappedSocket",
                1,
                unmappedBind,
                BoneKind.Helper,
                requiredForExport: false));
        RetargetMap map = Map(
            source,
            target,
            new BoneMapEntry(
                0,
                0,
                BoneMappingMethod.ExactName,
                1.0),
            new BoneMapEntry(
                1,
                1,
                BoneMappingMethod.ExactName,
                1.0),
            Entry(
                1,
                2,
                RetargetTransferPolicy.RestRelative,
                RetargetComponentPolicy.Translation),
            Entry(
                1,
                3,
                RetargetTransferPolicy.RestRelative,
                RetargetComponentPolicy.RotationTranslation));

        SkeletonPose actual =
            PoseRetargeter.Retarget(sourcePose, target, map);
        SkeletonPose repeated =
            PoseRetargeter.Retarget(sourcePose, target, map);
        TransformTRS refCandidate = (
            refCameraBind.ToMatrix() *
            sourceHeadBind.ToMatrix().InvertedAffine() *
            sourceHeadAnimated.ToMatrix()
        ).Decompose();
        TransformTRS eyeCandidate = (
            eyeCameraBind.ToMatrix() *
            sourceHeadBind.ToMatrix().InvertedAffine() *
            sourceHeadAnimated.ToMatrix()
        ).Decompose();

        Assert.Equal(
            3,
            map.Entries.Count(entry => entry.SourceBoneIndex == 1));
        AssertTransformNear(
            new(
                refCandidate.Translation,
                refCameraBind.Rotation,
                refCameraBind.Scale),
            actual.LocalTransforms[2]);
        AssertTransformNear(
            new(
                eyeCandidate.Translation,
                eyeCandidate.Rotation,
                eyeCameraBind.Scale),
            actual.LocalTransforms[3]);
        AssertTransformNear(
            unmappedBind,
            actual.LocalTransforms[4]);
        Assert.True(
            Vector3D.Distance(
                actual.LocalTransforms[2].Translation,
                actual.LocalTransforms[3].Translation) >
            1e-3);
        for (var index = 0; index < target.BoneCount; index++)
        {
            AssertMatrixNear(
                actual.LocalTransforms[index].ToMatrix(),
                repeated.LocalTransforms[index].ToMatrix());
            AssertMatrixNear(
                actual.GlobalMatrices[index],
                repeated.GlobalMatrices[index]);
        }

        CompatibilityDiagnostic fanout = Assert.Single(
            RigCompatibilityAnalyzer
                .Analyze(source, target, map)
                .Diagnostics,
            diagnostic =>
                diagnostic.Code == "helper_source_fanout");
        Assert.Contains("Head", fanout.Message, StringComparison.Ordinal);
        Assert.Contains(
            "RefCamera",
            fanout.Message,
            StringComparison.Ordinal);
        Assert.Contains(
            "EyeCamera",
            fanout.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void RotationDeltaPreservesTargetBindTranslationAndScale()
    {
        TransformTRS sourceBind = new(
            new Vector3D(5.0, 6.0, 7.0),
            Rotation(Vector3D.UnitZ, 10.0),
            new Vector3D(0.8, 0.8, 0.8));
        TransformTRS sourceAnimated = new(
            new Vector3D(-9.0, 12.0, 4.0),
            Rotation(Vector3D.UnitZ, 55.0),
            new Vector3D(2.0, 2.0, 2.0));
        RigDefinition source = Rig(
            "rotation-source",
            new BoneDefinition(
                0,
                "Driver",
                -1,
                sourceBind,
                BoneKind.Root));
        TransformTRS targetBind = new(
            new Vector3D(1.0, 2.0, 3.0),
            Rotation(Vector3D.UnitX, 15.0),
            new Vector3D(1.25, 1.25, 1.25));
        RigDefinition target = Rig(
            "rotation-target",
            new BoneDefinition(
                0,
                "RefCamera",
                -1,
                targetBind,
                BoneKind.Camera,
                requiredForExport: false));
        RetargetMap map = Map(
            source,
            target,
            Entry(
                0,
                0,
                RetargetTransferPolicy.RotationDelta,
                RetargetComponentPolicy.FullTransform));

        TransformTRS actual =
            PoseRetargeter.Retarget(
                    new SkeletonPose(source, [sourceAnimated]),
                    target,
                    map)
                .LocalTransforms[0];
        QuaternionD expectedRotation = (
            targetBind.Rotation *
            sourceBind.Rotation.Inverse() *
            sourceAnimated.Rotation
        ).Normalized();

        AssertVectorNear(targetBind.Translation, actual.Translation);
        AssertQuaternionNear(expectedRotation, actual.Rotation);
        AssertVectorNear(targetBind.Scale, actual.Scale);
    }

    [Fact]
    public void UnmappedHelperRetainsItsBindLocal()
    {
        RigDefinition source = Rig(
            "unmapped-source",
            new BoneDefinition(
                0,
                "Root",
                -1,
                TransformTRS.Identity,
                BoneKind.Root));
        TransformTRS helperBind = new(
            new Vector3D(0.4, 0.5, -0.6),
            Rotation(Vector3D.UnitY, 21.0),
            new Vector3D(1.3, 1.3, 1.3));
        RigDefinition target = Rig(
            "unmapped-target",
            new BoneDefinition(
                0,
                "Root",
                -1,
                TransformTRS.Identity,
                BoneKind.Root),
            new BoneDefinition(
                1,
                "EyeCamera",
                0,
                helperBind,
                BoneKind.Camera,
                requiredForExport: false));
        RetargetMap map = Map(
            source,
            target,
            new BoneMapEntry(
                0,
                0,
                BoneMappingMethod.ExactName,
                1.0));

        SkeletonPose actual =
            PoseRetargeter.Retarget(
                source.CreateBindPose(),
                target,
                map);

        AssertTransformNear(helperBind, actual.LocalTransforms[1]);
    }

    [Fact]
    public void NonFiniteHelperCandidateIsRejected()
    {
        RigDefinition source = Rig(
            "nonfinite-source",
            new BoneDefinition(
                0,
                "Driver",
                -1,
                TransformTRS.Identity,
                BoneKind.Root));
        SkeletonPose sourcePose = new(
            source,
            [
                new TransformTRS(
                    Vector3D.Zero,
                    QuaternionD.Identity,
                    new Vector3D(1e200, 1e200, 1e200)),
            ]);
        RigDefinition target = Rig(
            "nonfinite-target",
            new BoneDefinition(
                0,
                "Helper",
                -1,
                new TransformTRS(
                    Vector3D.Zero,
                    QuaternionD.Identity,
                    new Vector3D(1e200, 1e200, 1e200)),
                BoneKind.Helper,
                requiredForExport: false));
        RetargetMap map = Map(
            source,
            target,
            Entry(
                0,
                0,
                RetargetTransferPolicy.RestRelative,
                RetargetComponentPolicy.FullTransform));

        Exception exception = Assert.ThrowsAny<Exception>(
            () => PoseRetargeter.Retarget(sourcePose, target, map));

        Assert.Contains(
            "finite",
            exception.Message,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void MappingFingerprintChangesWithHelperPolicies()
    {
        RetargetMap baseline = new(
            "source",
            "target",
            [
                Entry(
                    0,
                    0,
                    RetargetTransferPolicy.RestRelative,
                    RetargetComponentPolicy.Translation),
            ]);
        RetargetMap changedTransfer = new(
            "source",
            "target",
            [
                Entry(
                    0,
                    0,
                    RetargetTransferPolicy.RotationDelta,
                    RetargetComponentPolicy.Translation),
            ]);
        RetargetMap changedComponents = new(
            "source",
            "target",
            [
                Entry(
                    0,
                    0,
                    RetargetTransferPolicy.RestRelative,
                    RetargetComponentPolicy.RotationTranslation),
            ]);

        string fingerprint = Fingerprint(baseline);

        Assert.NotEqual(fingerprint, Fingerprint(changedTransfer));
        Assert.NotEqual(fingerprint, Fingerprint(changedComponents));
    }

    [Fact]
    public void HelperOverridesDoNotChangeBaseIdentityOrHierarchyCompatibility()
    {
        RigDefinition source = Rig(
            "compatibility-source",
            new BoneDefinition(
                0,
                "Root",
                -1,
                TransformTRS.Identity,
                BoneKind.Root),
            new BoneDefinition(
                1,
                "Head",
                0,
                new TransformTRS(
                    Vector3D.UnitY,
                    QuaternionD.Identity,
                    Vector3D.One)));
        RigDefinition target = Rig(
            "compatibility-target",
            new BoneDefinition(
                0,
                "Root",
                -1,
                TransformTRS.Identity,
                BoneKind.Root),
            new BoneDefinition(
                1,
                "Head",
                0,
                new TransformTRS(
                    Vector3D.UnitY,
                    QuaternionD.Identity,
                    Vector3D.One)),
            new BoneDefinition(
                2,
                "RefCamera",
                1,
                new TransformTRS(
                    new Vector3D(0.1, 0.2, -0.1),
                    QuaternionD.Identity,
                    Vector3D.One),
                BoneKind.Camera,
                requiredForExport: false),
            new BoneDefinition(
                3,
                "EyeCamera",
                1,
                new TransformTRS(
                    new Vector3D(-0.1, 0.3, 0.1),
                    QuaternionD.Identity,
                    Vector3D.One),
                BoneKind.Camera,
                requiredForExport: false));
        RetargetMap map = Map(
            source,
            target,
            new BoneMapEntry(
                0,
                0,
                BoneMappingMethod.ExactName,
                1.0),
            new BoneMapEntry(
                1,
                1,
                BoneMappingMethod.ExactName,
                1.0),
            Entry(
                1,
                2,
                RetargetTransferPolicy.RestRelative,
                RetargetComponentPolicy.Translation),
            Entry(
                1,
                3,
                RetargetTransferPolicy.RestRelative,
                RetargetComponentPolicy.RotationTranslation));

        CompatibilityReport report =
            RigCompatibilityAnalyzer.Analyze(source, target, map);

        Assert.True(report.CanEvaluate);
        Assert.Equal(
            CompatibilityClassification.ExactIdentity,
            report.Classification);
        Assert.DoesNotContain(
            report.Diagnostics,
            diagnostic =>
                diagnostic.Code is
                    "mapped_hierarchy_differs" or
                    "mapped_bind_pose_differs");
    }

    [Fact]
    public void SuggestedMapClassifiesExactCameraHelpersWithProfilePolicies()
    {
        RigDefinition source = Rig(
            "exact-helper-source",
            new BoneDefinition(
                0,
                "Root",
                -1,
                TransformTRS.Identity,
                BoneKind.Root),
            new BoneDefinition(
                1,
                "Head",
                0,
                new TransformTRS(
                    Vector3D.UnitY,
                    QuaternionD.Identity,
                    Vector3D.One)),
            new BoneDefinition(
                2,
                "RefCamera",
                1,
                new TransformTRS(
                    new Vector3D(0.1, 0.2, -0.1),
                    QuaternionD.Identity,
                    Vector3D.One),
                BoneKind.Camera,
                requiredForExport: false),
            new BoneDefinition(
                3,
                "EyeCamera",
                1,
                new TransformTRS(
                    new Vector3D(-0.1, 0.3, 0.1),
                    QuaternionD.Identity,
                    Vector3D.One),
                BoneKind.Camera,
                requiredForExport: false));
        RigDefinition target = Rig(
            "exact-helper-target",
            source.Bones
                .Select(bone =>
                    new BoneDefinition(
                        bone.Index,
                        bone.Name,
                        bone.ParentIndex,
                        bone.LocalBindPose,
                        bone.Kind,
                        bone.RequiredForExport))
                .ToArray());

        RetargetMap map =
            RetargetMapBuilder.CreateSuggested(source, target);
        BoneMapEntry refCamera =
            RequiredTargetEntry(map, target, "RefCamera");
        BoneMapEntry eyeCamera =
            RequiredTargetEntry(map, target, "EyeCamera");

        AssertHelperSuggestion(
            refCamera,
            RetargetComponentPolicy.Translation);
        AssertHelperSuggestion(
            eyeCamera,
            RetargetComponentPolicy.RotationTranslation);
        Assert.Equal(BoneMappingMethod.ExactName, refCamera.Method);
        Assert.Equal(BoneMappingMethod.ExactName, eyeCamera.Method);
        Assert.True(
            RetargetMappingReview.IsVerifiedDeterministicMatch(
                source,
                target,
                refCamera));
        Assert.True(
            RetargetMappingReview.IsVerifiedDeterministicMatch(
                source,
                target,
                eyeCamera));
    }

    [Fact]
    public void DuplicateSameNameHelperSourcesDoNotFallBackToHead()
    {
        RigDefinition source = Rig(
            "ambiguous-helper-source",
            new BoneDefinition(
                0,
                "Root",
                -1,
                TransformTRS.Identity,
                BoneKind.Root),
            new BoneDefinition(
                1,
                "Head",
                0,
                new TransformTRS(
                    Vector3D.UnitY,
                    QuaternionD.Identity,
                    Vector3D.One)),
            new BoneDefinition(
                2,
                "EyeCamera",
                1,
                TransformTRS.Identity,
                BoneKind.Camera,
                requiredForExport: false),
            new BoneDefinition(
                3,
                "eye_camera",
                1,
                TransformTRS.Identity,
                BoneKind.Camera,
                requiredForExport: false));
        RigDefinition target = Rig(
            "ambiguous-helper-target",
            new BoneDefinition(
                0,
                "Root",
                -1,
                TransformTRS.Identity,
                BoneKind.Root),
            new BoneDefinition(
                1,
                "Head",
                0,
                new TransformTRS(
                    Vector3D.UnitY,
                    QuaternionD.Identity,
                    Vector3D.One)),
            new BoneDefinition(
                2,
                "EyeCamera",
                1,
                TransformTRS.Identity,
                BoneKind.Camera,
                requiredForExport: false));

        RetargetMap map =
            RetargetMapBuilder.CreateSuggested(source, target);

        Assert.False(
            map.TryGetTargetEntry(
                target.GetBoneIndex("EyeCamera"),
                out _));
        BoneMapEntry head =
            RequiredTargetEntry(map, target, "Head");
        Assert.Equal(source.GetBoneIndex("Head"), head.SourceBoneIndex);
        Assert.Equal(RetargetMappingKind.Bone, head.MappingKind);
    }

    [Fact]
    public void HeadFallbackFansOutWithoutStealingBodyMappingOrReview()
    {
        RigDefinition source = Rig(
            "fallback-helper-source",
            new BoneDefinition(
                0,
                "Root",
                -1,
                TransformTRS.Identity,
                BoneKind.Root),
            new BoneDefinition(
                1,
                "Head",
                0,
                new TransformTRS(
                    Vector3D.UnitY,
                    QuaternionD.Identity,
                    Vector3D.One)));
        RigDefinition target = Rig(
            "fallback-helper-target",
            new BoneDefinition(
                0,
                "Root",
                -1,
                TransformTRS.Identity,
                BoneKind.Root),
            new BoneDefinition(
                1,
                "Head",
                0,
                new TransformTRS(
                    Vector3D.UnitY,
                    QuaternionD.Identity,
                    Vector3D.One)),
            new BoneDefinition(
                2,
                "RefCamera",
                1,
                new TransformTRS(
                    new Vector3D(0.1, 0.2, -0.1),
                    QuaternionD.Identity,
                    Vector3D.One),
                BoneKind.Camera,
                requiredForExport: false),
            new BoneDefinition(
                3,
                "EyeCamera",
                1,
                new TransformTRS(
                    new Vector3D(-0.1, 0.3, 0.1),
                    QuaternionD.Identity,
                    Vector3D.One),
                BoneKind.Camera,
                requiredForExport: false));

        RetargetMap map =
            RetargetMapBuilder.CreateSuggested(source, target);
        BoneMapEntry head =
            RequiredTargetEntry(map, target, "Head");
        BoneMapEntry refCamera =
            RequiredTargetEntry(map, target, "RefCamera");
        BoneMapEntry eyeCamera =
            RequiredTargetEntry(map, target, "EyeCamera");

        Assert.Equal(source.GetBoneIndex("Head"), head.SourceBoneIndex);
        Assert.Equal(RetargetMappingKind.Bone, head.MappingKind);
        Assert.Equal(BoneMappingMethod.ExactName, head.Method);
        Assert.True(
            RetargetMappingReview.IsVerifiedDeterministicMatch(
                source,
                target,
                head));

        Assert.Equal(head.SourceBoneIndex, refCamera.SourceBoneIndex);
        Assert.Equal(head.SourceBoneIndex, eyeCamera.SourceBoneIndex);
        AssertHelperSuggestion(
            refCamera,
            RetargetComponentPolicy.Translation);
        AssertHelperSuggestion(
            eyeCamera,
            RetargetComponentPolicy.RotationTranslation);
        Assert.Equal(BoneMappingMethod.Semantic, refCamera.Method);
        Assert.Equal(BoneMappingMethod.Semantic, eyeCamera.Method);
        Assert.False(refCamera.IsReviewed);
        Assert.False(eyeCamera.IsReviewed);
        Assert.False(
            RetargetMappingReview.IsVerifiedDeterministicMatch(
                source,
                target,
                refCamera));
        Assert.False(
            RetargetMappingReview.IsVerifiedDeterministicMatch(
                source,
                target,
                eyeCamera));
        Assert.Equal(
            3,
            map.Entries.Count(entry =>
                entry.SourceBoneIndex == head.SourceBoneIndex));

        RetargetMappingReviewReport review =
            RetargetMappingReview.Analyze(source, target, map);
        Assert.False(review.IsReady);
        Assert.Equal(2, review.ExplicitReviewRequiredCount);
    }

    [Fact]
    public void ExactEyeCameraIdentityWithUnsafePolicyRequiresReview()
    {
        RigDefinition source = Rig(
            "eye-policy-source",
            new BoneDefinition(
                0,
                "Root",
                -1,
                TransformTRS.Identity,
                BoneKind.Root),
            new BoneDefinition(
                1,
                "EyeCamera",
                0,
                TransformTRS.Identity,
                BoneKind.Camera,
                requiredForExport: false));
        RigDefinition target = Rig(
            "eye-policy-target",
            new BoneDefinition(
                0,
                "Root",
                -1,
                TransformTRS.Identity,
                BoneKind.Root),
            new BoneDefinition(
                1,
                "EyeCamera",
                0,
                TransformTRS.Identity,
                BoneKind.Camera,
                requiredForExport: false));
        BoneMapEntry root = new(
            0,
            0,
            BoneMappingMethod.ExactName,
            1.0);
        BoneMapEntry unreviewedEyeCamera = new(
            1,
            1,
            BoneMappingMethod.ExactName,
            1.0,
            isReviewed: false,
            mappingKind: RetargetMappingKind.HelperOverride,
            transferPolicy: RetargetTransferPolicy.GlobalBindBasis,
            componentPolicy: RetargetComponentPolicy.FullTransform);

        Assert.True(
            RetargetMappingReview.IsVerifiedDeterministicIdentity(
                source,
                target,
                unreviewedEyeCamera));
        Assert.False(
            RetargetMappingReview.IsVerifiedDeterministicMatch(
                source,
                target,
                unreviewedEyeCamera));

        RetargetMappingReviewReport blocked =
            RetargetMappingReview.Analyze(
                source,
                target,
                Map(source, target, root, unreviewedEyeCamera));
        Assert.False(blocked.IsReady);
        Assert.Equal(1, blocked.ExplicitReviewRequiredCount);
        Assert.DoesNotContain(
            blocked.Diagnostics,
            diagnostic =>
                diagnostic.Code ==
                    "deterministic_mapping_identity_mismatch");

        BoneMapEntry reviewedEyeCamera = new(
            1,
            1,
            BoneMappingMethod.ExactName,
            1.0,
            isReviewed: true,
            mappingKind: RetargetMappingKind.HelperOverride,
            transferPolicy: RetargetTransferPolicy.GlobalBindBasis,
            componentPolicy: RetargetComponentPolicy.FullTransform);
        RetargetMappingReviewReport ready =
            RetargetMappingReview.Analyze(
                source,
                target,
                Map(source, target, root, reviewedEyeCamera));

        Assert.True(ready.IsReady);
        Assert.DoesNotContain(
            ready.Diagnostics,
            diagnostic =>
                diagnostic.Code is
                    "deterministic_mapping_identity_mismatch" or
                    "mapping_row_requires_review");
    }

    [Fact]
    public void ReviewedClaimWithFalseExactHelperIdentityStillBlocks()
    {
        RigDefinition source = Rig(
            "false-identity-source",
            new BoneDefinition(
                0,
                "Root",
                -1,
                TransformTRS.Identity,
                BoneKind.Root),
            new BoneDefinition(
                1,
                "SourceCamera",
                0,
                TransformTRS.Identity,
                BoneKind.Camera,
                requiredForExport: false));
        RigDefinition target = Rig(
            "false-identity-target",
            new BoneDefinition(
                0,
                "Root",
                -1,
                TransformTRS.Identity,
                BoneKind.Root),
            new BoneDefinition(
                1,
                "EyeCamera",
                0,
                TransformTRS.Identity,
                BoneKind.Camera,
                requiredForExport: false));
        BoneMapEntry falseExactIdentity = new(
            1,
            1,
            BoneMappingMethod.ExactName,
            1.0,
            isReviewed: true,
            mappingKind: RetargetMappingKind.HelperOverride,
            transferPolicy: RetargetTransferPolicy.RestRelative,
            componentPolicy:
                RetargetComponentPolicy.RotationTranslation);

        RetargetMappingReviewReport report =
            RetargetMappingReview.Analyze(
                source,
                target,
                Map(
                    source,
                    target,
                    new BoneMapEntry(
                        0,
                        0,
                        BoneMappingMethod.ExactName,
                        1.0),
                    falseExactIdentity));

        Assert.False(
            RetargetMappingReview.IsVerifiedDeterministicIdentity(
                source,
                target,
                falseExactIdentity));
        Assert.False(report.IsReady);
        Assert.Contains(
            report.Diagnostics,
            diagnostic =>
                diagnostic.Code ==
                    "deterministic_mapping_identity_mismatch");
    }

    [Fact]
    public void FullTransformHelperPoliciesProduceScaleAndCameraWarnings()
    {
        RigDefinition source = Rig(
            "full-helper-source",
            new BoneDefinition(
                0,
                "Root",
                -1,
                TransformTRS.Identity,
                BoneKind.Root),
            new BoneDefinition(
                1,
                "AimHelper",
                0,
                TransformTRS.Identity,
                BoneKind.Helper,
                requiredForExport: false),
            new BoneDefinition(
                2,
                "EyeCamera",
                0,
                TransformTRS.Identity,
                BoneKind.Camera,
                requiredForExport: false));
        RigDefinition target = Rig(
            "full-helper-target",
            source.Bones
                .Select(bone =>
                    new BoneDefinition(
                        bone.Index,
                        bone.Name,
                        bone.ParentIndex,
                        bone.LocalBindPose,
                        bone.Kind,
                        bone.RequiredForExport))
                .ToArray());
        RetargetMap map = Map(
            source,
            target,
            new BoneMapEntry(
                0,
                0,
                BoneMappingMethod.ExactName,
                1.0),
            Entry(
                1,
                1,
                RetargetTransferPolicy.CopyLocal,
                RetargetComponentPolicy.FullTransform),
            Entry(
                2,
                2,
                RetargetTransferPolicy.CopyLocal,
                RetargetComponentPolicy.FullTransform));

        CompatibilityReport report =
            RigCompatibilityAnalyzer.Analyze(source, target, map);

        Assert.True(report.CanEvaluate);
        Assert.Equal(
            2,
            report.Diagnostics.Count(diagnostic =>
                diagnostic.Code ==
                    "helper_full_transform_changes_scale"));
        CompatibilityDiagnostic cameraWarning = Assert.Single(
            report.Diagnostics,
            diagnostic =>
                diagnostic.Code ==
                    "camera_helper_full_transform_unsafe");
        Assert.Equal(
            CompatibilityDiagnosticSeverity.Warning,
            cameraWarning.Severity);
        Assert.Equal("EyeCamera", cameraWarning.TargetBoneName);
    }

    private static BoneMapEntry Entry(
        int sourceBoneIndex,
        int targetBoneIndex,
        RetargetTransferPolicy transferPolicy,
        RetargetComponentPolicy componentPolicy) =>
        new(
            sourceBoneIndex,
            targetBoneIndex,
            BoneMappingMethod.Manual,
            1.0,
            isLocked: false,
            isReviewed: true,
            mappingKind: RetargetMappingKind.HelperOverride,
            transferPolicy: transferPolicy,
            componentPolicy: componentPolicy);

    private static RetargetMap Map(
        RigDefinition source,
        RigDefinition target,
        params BoneMapEntry[] entries) =>
        new(source.Id, target.Id, entries);

    private static BoneMapEntry RequiredTargetEntry(
        RetargetMap map,
        RigDefinition target,
        string targetBoneName)
    {
        int targetIndex = target.GetBoneIndex(targetBoneName);
        Assert.True(targetIndex >= 0);
        Assert.True(
            map.TryGetTargetEntry(
                targetIndex,
                out BoneMapEntry? entry));
        return Assert.IsType<BoneMapEntry>(entry);
    }

    private static void AssertHelperSuggestion(
        BoneMapEntry entry,
        RetargetComponentPolicy expectedComponents)
    {
        Assert.Equal(
            RetargetMappingKind.HelperOverride,
            entry.MappingKind);
        Assert.Equal(
            RetargetTransferPolicy.RestRelative,
            entry.TransferPolicy);
        Assert.Equal(expectedComponents, entry.ComponentPolicy);
    }

    private static RigDefinition Rig(
        string id,
        params BoneDefinition[] bones) =>
        new(id, id, bones);

    private static QuaternionD Rotation(
        Vector3D axis,
        double degrees) =>
        QuaternionD.FromAxisAngle(
            axis,
            degrees * Math.PI / 180.0);

    private static string Fingerprint(RetargetMap map) =>
        RetargetMapFingerprint.Compute(
            "source-rig-signature",
            "target-rig-signature",
            "target-asset-fingerprint",
            map);

    private static void AssertTransformNear(
        TransformTRS expected,
        TransformTRS actual)
    {
        AssertVectorNear(expected.Translation, actual.Translation);
        AssertQuaternionNear(expected.Rotation, actual.Rotation);
        AssertVectorNear(expected.Scale, actual.Scale);
    }

    private static void AssertVectorNear(
        Vector3D expected,
        Vector3D actual)
    {
        Assert.InRange(
            Math.Abs(expected.X - actual.X),
            0.0,
            Tolerance);
        Assert.InRange(
            Math.Abs(expected.Y - actual.Y),
            0.0,
            Tolerance);
        Assert.InRange(
            Math.Abs(expected.Z - actual.Z),
            0.0,
            Tolerance);
    }

    private static void AssertQuaternionNear(
        QuaternionD expected,
        QuaternionD actual)
    {
        QuaternionD expectedUnit = expected.Normalized();
        QuaternionD actualUnit = actual.Normalized();
        double dot = Math.Abs(
            QuaternionD.Dot(expectedUnit, actualUnit));
        Assert.InRange(dot, 1.0 - Tolerance, 1.0 + Tolerance);
    }

    private static void AssertMatrixNear(
        TransformMatrix expected,
        TransformMatrix actual) =>
        Assert.True(
            expected.NearlyEquals(actual, Tolerance),
            $"Expected matrix {expected}, actual {actual}.");
}
