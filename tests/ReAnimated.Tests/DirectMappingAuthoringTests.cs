using ReAnimated.App.ViewModels;
using ReAnimated.Core.Domain;
using ReAnimated.Core.Mathematics;
using ReAnimated.Core.Project;
using ReAnimated.Retargeting.Mapping;

namespace ReAnimated.Tests;

public sealed class DirectMappingAuthoringTests
{
    [Fact]
    public void DirectBindingPublishesScoredIdentityRowsWithoutChangingPlaybackMode()
    {
        RigDefinition source = CreateRig("source");
        RigDefinition target = CreateRig("target");
        RetargetMap authoringMap =
            MainWindowViewModel.CreateDirectAuthoringMap(
                source,
                target);
        var animation = new ProjectAnimation
        {
            Name = "direct-authoring",
            BindingMode =
                ProjectAnimationBindingMode.ExactDirect,
        };

        var visible =
            MainWindowViewModel.ResolveVisibleProjectMappings(
                animation,
                source,
                target,
                authoringMap);

        Assert.Equal(source.BoneCount, authoringMap.Entries.Length);
        Assert.Equal(source.BoneCount, visible.Length);
        Assert.All(authoringMap.Entries, entry =>
        {
            Assert.Equal(BoneMappingMethod.ExactName, entry.Method);
            Assert.Equal(1.0, entry.Confidence);
            Assert.NotEmpty(entry.Evidence);
            Assert.False(string.IsNullOrWhiteSpace(
                entry.ScorerVersion));
            Assert.False(string.IsNullOrWhiteSpace(
                entry.EvidenceFingerprint));
        });
        Assert.All(visible, row =>
        {
            Assert.Equal(
                nameof(BoneMappingMethod.ExactName),
                row.Method);
            Assert.Equal(1.0, row.Confidence);
            Assert.NotEqual("unscored-v1", row.ScorerVersion);
            Assert.NotEqual(new string('0', 64), row.EvidenceFingerprint);
        });
        Assert.Equal(
            ProjectAnimationBindingMode.ExactDirect,
            animation.BindingMode);
        Assert.Contains(
            "PoseRetargeter is bypassed",
            MainWindowViewModel.FormatDirectAuthoringMappingStatus(
                directBinding: null,
                authoringMap),
            StringComparison.Ordinal);
    }

    [Fact]
    public void UnscoredMappingRowIsLabelledAsNotScored()
    {
        var row = new BoneMappingViewModel(
            "source",
            targetBone: null,
            confidence: 0,
            status: "Unmapped");

        Assert.Contains(
            "Not scored",
            row.EvidenceMethod,
            StringComparison.Ordinal);
        Assert.StartsWith(
            "Not scored",
            row.EvidenceSummary,
            StringComparison.Ordinal);
    }

    private static RigDefinition CreateRig(string id) =>
        new(
            id,
            id,
            [
                new BoneDefinition(
                    0,
                    "Bip01",
                    -1,
                    TransformTRS.Identity,
                    BoneKind.Root,
                    semanticRole: "root.skeletal"),
                new BoneDefinition(
                    1,
                    "EyeCamera",
                    0,
                    TransformTRS.Identity,
                    BoneKind.Camera,
                    requiredForExport: false,
                    semanticRole: "camera.eye"),
                new BoneDefinition(
                    2,
                    "PropHolder",
                    0,
                    TransformTRS.Identity,
                    BoneKind.Prop,
                    requiredForExport: false,
                    semanticRole: "prop.right_hand"),
            ]);
}
