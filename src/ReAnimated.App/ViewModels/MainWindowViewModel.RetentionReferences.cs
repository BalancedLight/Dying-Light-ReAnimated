using ReAnimated.Core.Domain;
using ReAnimated.Core.ModelAuthoring;
using ReAnimated.Core.Project;

namespace ReAnimated.App.ViewModels;

public sealed partial class MainWindowViewModel
{
    private List<string> GetRetentionRemovalBlockers(CustomModelDocument document, int removedIndex, string removedName)
    {
        ArgumentNullException.ThrowIfNull(document);
        var owner = FindCustomModelEntry(_project, document.ModelId);
        if (owner is null) return ["Synchronize this model into the project first; its project references have not been resolved."];
        var blockers = CollectRetentionProjectBlockers(_project, owner, removedIndex, removedName);
        if (GetActiveAnimation()?.TargetAssetId == owner.AssetId)
        {
            if (AttachmentEditor.SelectedAttachment?.Binding is { } binding)
                AddRetentionAttachmentBlockers(blockers, "active attachment editor", binding, removedIndex, removedName);
            if (AttachmentEditor.SelectedParentBone is { } parent && (parent.Index >= removedIndex || SameRetentionBone(parent.Name, removedName)))
                blockers.Add($"Active attachment parent '{parent.Name}' must be reassigned before removal.");
            if (_selectedBoneEditLayer is not null)
                blockers.Add($"Active bone edit layer '{_selectedBoneEditLayer.Name}' requires target-index reconciliation.");
        }
        return blockers;
    }

    internal static List<string> CollectRetentionProjectBlockers(DlraProject project, ProjectModelEntry owner,
        int removedIndex, string removedName)
    {
        ArgumentNullException.ThrowIfNull(project); ArgumentNullException.ThrowIfNull(owner);
        ArgumentOutOfRangeException.ThrowIfNegative(removedIndex); ArgumentException.ThrowIfNullOrWhiteSpace(removedName);
        List<string> blockers = [];
        foreach (var variant in project.AnimationVariants.Where(v => v.TargetModelId == owner.Id))
            Add($"animation variant '{variant.Name}'", variant.Attachments, variant.EditLayers.Length, variant.IkLayers.Length,
                variant.RootBoneName, variant.AccumulatorBoneName, variant.BoneMappings);
        foreach (var animation in project.Animations.Where(a => a.TargetAssetId == owner.AssetId))
            Add($"animation '{animation.Name}'", animation.Attachments, animation.EditLayers.Length, animation.IkLayers.Length,
                animation.RootBoneName, animation.AccumulatorBoneName, animation.BoneMappings);
        return blockers;

        void Add(string label, IEnumerable<AttachmentBinding> attachments, int edits, int ik,
            string? root, string? accumulator, IEnumerable<ProjectBoneMapping> mappings)
        {
            foreach (var attachment in attachments)
                AddRetentionAttachmentBlockers(blockers, $"{label} attachment '{attachment.Name}'", attachment, removedIndex, removedName);
            if (edits > 0) blockers.Add($"{label} has target edit layers requiring bone-index reconciliation.");
            if (ik > 0) blockers.Add($"{label} has IK layers requiring bone-index reconciliation.");
            if (SameRetentionBone(root, removedName) || SameRetentionBone(accumulator, removedName)) blockers.Add($"{label} uses '{removedName}' for root motion.");
            if (mappings.Any(m => SameRetentionBone(m.TargetBoneName, removedName))) blockers.Add($"{label} maps animation to '{removedName}'.");
        }
    }

    private static void AddRetentionAttachmentBlockers(List<string> blockers, string label, AttachmentBinding attachment,
        int removedIndex, string removedName)
    {
        if (attachment.ParentBoneIndex >= removedIndex || SameRetentionBone(attachment.ParentBoneName, removedName))
            blockers.Add($"{label} parent '{attachment.ParentBoneName ?? $"index {attachment.ParentBoneIndex}"}' must be reassigned or reindexed.");
        if (attachment.GripCalibration?.Secondary is not { } secondary) return;
        if (secondary.CharacterBoneIndex >= removedIndex || SameRetentionBone(secondary.CharacterBoneName, removedName))
            blockers.Add($"{label} secondary grip '{secondary.CharacterBoneName}' must be reassigned or reindexed.");
        if (secondary.Ik is { } ik && (ik.RootBoneIndex >= removedIndex || ik.JointBoneIndex >= removedIndex || ik.EndBoneIndex >= removedIndex ||
            SameRetentionBone(ik.RootBoneName, removedName) || SameRetentionBone(ik.JointBoneName, removedName) || SameRetentionBone(ik.EndBoneName, removedName)))
            blockers.Add($"{label} secondary IK chain requires bone-index reconciliation.");
    }

    private static bool SameRetentionBone(string? first, string second) => string.Equals(first, second, StringComparison.OrdinalIgnoreCase);
}
