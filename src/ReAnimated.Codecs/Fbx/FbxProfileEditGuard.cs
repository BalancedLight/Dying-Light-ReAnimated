using System.Collections.Immutable;
using ReAnimated.Codecs.Models;
using ReAnimated.Core.ModelAuthoring;

namespace ReAnimated.Codecs.Fbx;

/// <summary>Checks the effect of an edit on the same prepared frames and bounds used by output.</summary>
public static class FbxProfileEditGuard
{
    public static void RequireAllowed(FbxModelAuthoringImportResult before, FbxModelAuthoringImportResult after, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(before); ArgumentNullException.ThrowIfNull(after); token.ThrowIfCancellationRequested();
        if (before.Package.Document.ModelId != after.Package.Document.ModelId) return;
        var constraints = RigProfileEditGuard.Constraints(before.Package.Document.RiggingSession);
        if (constraints.IsEmpty) return;
        RigProfileEditGuard.RequireDocumentAllowed(before.Package.Document, after.Package.Document);
        var oldNodes = before.Rig is null ? [] : Dl1CustomModelRigPreparer.Prepare(before, token).Contract.Nodes;
        var newNodes = after.Rig is null ? [] : Dl1CustomModelRigPreparer.Prepare(after, token).Contract.Nodes;
        var next = newNodes.Where(n => n.SemanticEntityId is not null).ToDictionary(n => n.SemanticEntityId!.Value);
        var failures = ImmutableArray.CreateBuilder<RigProfileDiagnostic>();
        foreach (var rule in constraints)
        {
            token.ThrowIfCancellationRequested();
            var previous = oldNodes.FirstOrDefault(n => n.SemanticEntityId == rule.EntityId); if (previous is null) continue;
            if (!next.TryGetValue(rule.EntityId, out var node)) { RigProfileEditGuard.AddRemoval(rule, failures); continue; }
            var changed = RigProfileEditGuard.FrameChanges(previous.LocalBindMatrix, node.LocalBindMatrix);
            if (previous.Name != node.Name) changed |= RigHelperEditFields.Name;
            Guid? oldParent = previous.ParentPhysicalIndex < 0 ? null : oldNodes[previous.ParentPhysicalIndex].SemanticEntityId;
            Guid? newParent = node.ParentPhysicalIndex < 0 ? null : newNodes[node.ParentPhysicalIndex].SemanticEntityId;
            if (oldParent != newParent) changed |= RigHelperEditFields.Parent;
            if (previous.Bounds != node.Bounds || previous.BoundsPolicy != node.BoundsPolicy) changed |= RigHelperEditFields.Extents;
            if (previous.FramePolicy != node.FramePolicy) changed |= RigHelperEditFields.Position | RigHelperEditFields.Orientation;
            RigProfileEditGuard.AddChanges(rule, changed, "prepared output", failures);
            if (previous.IsDeform != node.IsDeform)
                failures.Add(new("profile-edit-representation-denied", RigValidationStatus.Failed,
                    $"{rule.RoleId} / {rule.EntityName}: the native bone/helper representation changes.",
                    "Review this representation change separately from frame editing.", RoleId: rule.RoleId, EntityId: rule.EntityId, ConsumerIds: rule.ConsumerIds));
        }
        RigProfileEditGuard.ThrowIfAny(failures);
    }
}
