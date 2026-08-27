using System.Collections.Immutable;
using ReAnimated.Core.Domain;
using ReAnimated.Core.Mathematics;
using ReAnimated.Core.Project;
using ReAnimated.App.ViewModels;
using ReAnimated.Evaluation;

namespace ReAnimated.Tests;

/// <summary>
/// Guards the motion-accumulator contract.
/// </summary>
/// <remarks>
/// Requesting MotionAccumulator against a rig with no accumulator track used to
/// be tolerated silently: the root correction still stripped the travel and
/// heading, then discarded them because there was nowhere to write them, so the
/// clip went in-place with no error anywhere. These tests pin the refusal and
/// the author-chosen override that replaces it.
/// </remarks>
public sealed class MotionAccumulatorPolicyTests
{
    private const uint AccumulatorDescriptor = 0xCCC3CDDF;

    private static RigDefinition CreateRig(
        bool withDescriptorAccumulator,
        string extraHelperName = "custom_offset")
    {
        var bones = ImmutableArray.CreateBuilder<BoneDefinition>();
        bones.Add(new BoneDefinition(
            0,
            "Bip01",
            -1,
            TransformTRS.Identity,
            kind: BoneKind.Root,
            requiredForExport: true));
        bones.Add(new BoneDefinition(
            1,
            "pelvis",
            0,
            TransformTRS.Identity,
            kind: BoneKind.Deform,
            requiredForExport: true));
        bones.Add(new BoneDefinition(
            2,
            withDescriptorAccumulator ? "offsethelper" : extraHelperName,
            -1,
            TransformTRS.Identity,
            kind: BoneKind.Helper,
            requiredForExport: true,
            descriptorHash: withDescriptorAccumulator
                ? AccumulatorDescriptor
                : 0x1234u));
        return new RigDefinition("rig-under-test", "Rig", bones.ToImmutable());
    }

    [Fact]
    public void AccumulatorPolicyWithNoAccumulatorIsRefused()
    {
        RigDefinition rig = CreateRig(withDescriptorAccumulator: false);

        InvalidOperationException exception =
            Assert.Throws<InvalidOperationException>(() =>
                Dl1AuthoringPolicy.Create(
                    rig,
                    rig,
                    retargetMap: null,
                    AnimationRootMode.MotionAccumulator,
                    "Bip01"));

        Assert.Contains(
            "no motion-accumulator track",
            exception.Message,
            StringComparison.Ordinal);
        Assert.Contains(
            "discard",
            exception.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void OtherRootPoliciesStillTolerateAMissingAccumulator()
    {
        // Compact retail skeletons legitimately omit the row; only the
        // accumulator policy actually needs it.
        RigDefinition rig = CreateRig(withDescriptorAccumulator: false);

        foreach (AnimationRootMode mode in new[]
                 {
                     AnimationRootMode.Recorded,
                     AnimationRootMode.InPlace,
                     AnimationRootMode.Bip01,
                 })
        {
            Dl1AuthoringPolicy policy = Dl1AuthoringPolicy.Create(
                rig,
                rig,
                retargetMap: null,
                mode,
                "Bip01");

            Assert.Null(policy.RootMotion.MotionAccumulatorBoneIndex);
        }
    }

    [Fact]
    public void AParentedBoneCannotBeAnAccumulator()
    {
        // ApplyMotionAccumulator writes world planar displacement and heading
        // as the bone's LOCAL transform, which only equals world for a
        // parentless track. A child bone would be corrupted, not animated.
        RigDefinition rig = CreateRig(withDescriptorAccumulator: false);

        InvalidOperationException exception =
            Assert.Throws<InvalidOperationException>(() =>
                Dl1AuthoringPolicy.Create(
                    rig,
                    rig,
                    retargetMap: null,
                    AnimationRootMode.MotionAccumulator,
                    "Bip01",
                    accumulatorBoneName: "pelvis"));

        Assert.Contains(
            "root-level",
            exception.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void ADescriptorlessBoneCannotBeAnAccumulator()
    {
        // ANM2 serialization addresses tracks by descriptor and skips bones
        // without one, so the accumulated travel would be dropped after the
        // root motion had already been removed.
        var rig = new RigDefinition(
            "no-descriptor",
            "No descriptor",
            [
                new BoneDefinition(
                    0,
                    "Bip01",
                    -1,
                    TransformTRS.Identity,
                    kind: BoneKind.Root,
                    requiredForExport: true),
                new BoneDefinition(
                    1,
                    "loose_helper",
                    -1,
                    TransformTRS.Identity,
                    kind: BoneKind.Helper,
                    requiredForExport: true),
            ]);

        Assert.Throws<InvalidOperationException>(() =>
            Dl1AuthoringPolicy.Create(
                rig,
                rig,
                retargetMap: null,
                AnimationRootMode.MotionAccumulator,
                "Bip01",
                accumulatorBoneName: "loose_helper"));
    }

    [Fact]
    public void AnOverrideIsIgnoredOutsideAccumulatorMode()
    {
        // A saved override must not make Recorded fail, nor let Bip01 or
        // InPlace touch a track the author never nominated for them.
        RigDefinition rig = CreateRig(withDescriptorAccumulator: false);

        Dl1AuthoringPolicy policy = Dl1AuthoringPolicy.Create(
            rig,
            rig,
            retargetMap: null,
            AnimationRootMode.Recorded,
            "Bip01",
            accumulatorBoneName: "does_not_exist");

        Assert.Null(policy.RootMotion.MotionAccumulatorBoneIndex);
    }

    [Fact]
    public void AnAuthorChosenAccumulatorIsAccepted()
    {
        RigDefinition rig = CreateRig(withDescriptorAccumulator: false);

        Dl1AuthoringPolicy policy = Dl1AuthoringPolicy.Create(
            rig,
            rig,
            retargetMap: null,
            AnimationRootMode.MotionAccumulator,
            "Bip01",
            accumulatorBoneName: "custom_offset");

        Assert.Equal(2, policy.RootMotion.MotionAccumulatorBoneIndex);
    }

    [Fact]
    public void AnUnknownAccumulatorBoneIsNamedInTheError()
    {
        RigDefinition rig = CreateRig(withDescriptorAccumulator: false);

        InvalidOperationException exception =
            Assert.Throws<InvalidOperationException>(() =>
                Dl1AuthoringPolicy.Create(
                    rig,
                    rig,
                    retargetMap: null,
                    AnimationRootMode.MotionAccumulator,
                    "Bip01",
                    accumulatorBoneName: "not_a_bone"));

        Assert.Contains(
            "not_a_bone",
            exception.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void TheAccumulatorMayNotBeTheSkeletalRoot()
    {
        RigDefinition rig = CreateRig(withDescriptorAccumulator: false);

        InvalidOperationException exception =
            Assert.Throws<InvalidOperationException>(() =>
                Dl1AuthoringPolicy.Create(
                    rig,
                    rig,
                    retargetMap: null,
                    AnimationRootMode.MotionAccumulator,
                    "Bip01",
                    accumulatorBoneName: "Bip01"));

        Assert.Contains(
            "distinct",
            exception.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void ARigThatOwnsTheDescriptorNeedsNoOverride()
    {
        RigDefinition rig = CreateRig(withDescriptorAccumulator: true);

        Dl1AuthoringPolicy policy = Dl1AuthoringPolicy.Create(
            rig,
            rig,
            retargetMap: null,
            AnimationRootMode.MotionAccumulator,
            "Bip01");

        Assert.Equal(2, policy.RootMotion.MotionAccumulatorBoneIndex);
    }

    [Fact]
    public void AccumulatorBoneNameRoundTripsThroughTheProjectFile()
    {
        string directory = Path.Combine(
            Path.GetTempPath(),
            $"ReAnimated-Accumulator-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            Guid sourceAssetId = Guid.NewGuid();
            Guid targetAssetId = Guid.NewGuid();
            Guid sourceId = Guid.NewGuid();
            Guid modelId = Guid.NewGuid();
            Guid variantId = Guid.NewGuid();
            string signature = new('a', 64);
            DlraProject project = DlraProject.Create("Accumulator") with
            {
                Assets =
                [
                    new ProjectAssetReference
                    {
                        Id = sourceAssetId,
                        Kind = ProjectAssetKind.SourceAnimation,
                        RelativePath = "inputs/a.fbx",
                        ContentSha256 = new string('1', 64),
                    },
                    new ProjectAssetReference
                    {
                        Id = targetAssetId,
                        Kind = ProjectAssetKind.CustomModelSource,
                        RelativePath = "models/t.dlrmodel",
                        ContentSha256 = new string('2', 64),
                    },
                ],
                Models =
                [
                    new ProjectModelEntry
                    {
                        Id = modelId,
                        AssetId = targetAssetId,
                        Name = "Target",
                        RigSignature = signature,
                    },
                ],
                AnimationSources =
                [
                    new ProjectAnimationSource
                    {
                        Id = sourceId,
                        Name = "Take",
                        SourceAssetId = sourceAssetId,
                        RequiresSourceRebind = true,
                        MigrationNote = "Unavailable.",
                        FrameCount = 2,
                    },
                ],
                AnimationVariants =
                [
                    new ProjectAnimationVariant
                    {
                        Id = variantId,
                        SourceId = sourceId,
                        Name = "Take",
                        TargetModelId = modelId,
                        TargetRigId = "target",
                        TargetRigSignature = signature,
                        BindingMode = ProjectAnimationBindingMode.Retarget,
                        RootMotionMode = Dl1RootMotionMode.MotionAccumulator,
                        AccumulatorBoneName = "custom_offset",
                    },
                ],
            };
            string path = Path.Combine(directory, "accumulator.dlraproj");
            ProjectSerializer.SaveAtomic(project, path);

            DlraProject reloaded = ProjectSerializer.Load(path);

            Assert.Equal(
                "custom_offset",
                Assert.Single(
                    reloaded.AnimationVariants,
                    variant => variant.Id == variantId).AccumulatorBoneName);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void ABlankAccumulatorNameIsRejected()
    {
        DlraProject project = DlraProject.Create("Blank") with
        {
            Animations =
            [
                new ProjectAnimation
                {
                    Id = Guid.NewGuid(),
                    Name = "Take",
                    SourceAssetId = Guid.NewGuid(),
                    AccumulatorBoneName = "   ",
                },
            ],
        };

        Assert.ThrowsAny<Exception>(project.Validate);
    }
}
