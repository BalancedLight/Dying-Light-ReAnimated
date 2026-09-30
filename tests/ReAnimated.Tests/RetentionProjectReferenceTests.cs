using System.Collections.Immutable;
using ReAnimated.App.ViewModels;
using ReAnimated.Core.Domain;
using ReAnimated.Core.Mathematics;
using ReAnimated.Core.Project;

namespace ReAnimated.Tests;

public sealed class RetentionProjectReferenceTests
{
    [Fact]
    public void OnlyTheOwnedTargetsBlockForNamesIndicesAndRootMappings()
    {
        var owner = new ProjectModelEntry { Id = Guid.NewGuid(), AssetId = Guid.NewGuid(), Name = "owner" };
        var variant = new ProjectAnimationVariant { Id = Guid.NewGuid(), TargetModelId = owner.Id, Name = "target",
            Attachments = [Binding(1, "keep_marker")], AccumulatorBoneName = "keep_marker",
            BoneMappings = [new() { SourceBoneName = "source", TargetBoneName = "KEEP_MARKER" }] };
        var unrelated = variant with { Id = Guid.NewGuid(), TargetModelId = Guid.NewGuid(), Name = "unrelated" };
        var project = DlraProject.Create("references") with { Models = [owner], AnimationVariants = [variant, unrelated] };
        var blockers = MainWindowViewModel.CollectRetentionProjectBlockers(project, owner, 4, "keep_marker");
        Assert.Equal(3, blockers.Count);
        Assert.DoesNotContain(blockers, b => b.Contains("unrelated", StringComparison.Ordinal));
        project = project with { AnimationVariants = [unrelated], Animations = [new() { Id = Guid.NewGuid(), Name = "legacy",
            TargetAssetId = owner.AssetId, Attachments = [Binding(6, "later_helper")] }] };
        Assert.Contains(MainWindowViewModel.CollectRetentionProjectBlockers(project, owner, 4, "keep_marker"), b => b.Contains("legacy", StringComparison.Ordinal));
    }

    [Fact]
    public void UnaffectedAttachmentsPassButTargetEditAndIkLayersRequireReconciliation()
    {
        var owner = new ProjectModelEntry { Id = Guid.NewGuid(), AssetId = Guid.NewGuid(), Name = "owner" };
        var variant = new ProjectAnimationVariant { Id = Guid.NewGuid(), TargetModelId = owner.Id, Name = "target", Attachments = [Binding(0, "Root")] };
        var project = DlraProject.Create("references") with { Models = [owner], AnimationVariants = [variant] };
        Assert.Empty(MainWindowViewModel.CollectRetentionProjectBlockers(project, owner, 4, "keep_marker"));
        variant = variant with { EditLayers = [new(Guid.NewGuid(), "edit", BoneEditBlendMode.Additive, BoneEditLayerScope.AuthoredExportable, 1, ImmutableArray<BoneEditTrack>.Empty)],
            IkLayers = [new() { Name = "IK", ChainName = "arm" }] };
        var blockers = MainWindowViewModel.CollectRetentionProjectBlockers(project with { AnimationVariants = [variant] }, owner, 4, "keep_marker");
        Assert.Contains(blockers, b => b.Contains("edit layers", StringComparison.Ordinal));
        Assert.Contains(blockers, b => b.Contains("IK layers", StringComparison.Ordinal));
    }

    [Fact]
    public void SecondaryGripAndIkNamesAreCheckedEvenWithOlderCachedIndices()
    {
        var owner = new ProjectModelEntry { Id = Guid.NewGuid(), AssetId = Guid.NewGuid(), Name = "owner" };
        var asset = Guid.NewGuid();
        var frame = AttachmentGripFrame.FromMatrix(0, "grip", TransformMatrix.Identity);
        var binding = new AttachmentBinding(Guid.NewGuid(), asset, "prop", 0, TransformTRS.Identity, AttachmentScope.AuthoredExportable, "Root",
            new() { PropAssetId = asset, PropContentSha256 = new('a',64), PrimaryPropFrame = frame, Secondary = new()
            { CharacterBoneIndex = 1, CharacterBoneName = "keep_marker", PropFrame = frame,
                Ik = new() { RootBoneIndex = 0, JointBoneIndex = 1, EndBoneIndex = 2, RootBoneName = "Root", JointBoneName = "keep_marker", EndBoneName = "hand" } } });
        var project = DlraProject.Create("references") with { Models = [owner], AnimationVariants = [new()
            { Id = Guid.NewGuid(), TargetModelId = owner.Id, Name = "target", Attachments = [binding] }] };
        var blockers = MainWindowViewModel.CollectRetentionProjectBlockers(project, owner, 4, "keep_marker");
        Assert.Contains(blockers, b => b.Contains("secondary grip", StringComparison.Ordinal));
        Assert.Contains(blockers, b => b.Contains("secondary IK", StringComparison.Ordinal));
    }

    private static AttachmentBinding Binding(int index, string name) => new(Guid.NewGuid(), Guid.NewGuid(), "prop", index, TransformTRS.Identity, AttachmentScope.AuthoredExportable, name);
}
