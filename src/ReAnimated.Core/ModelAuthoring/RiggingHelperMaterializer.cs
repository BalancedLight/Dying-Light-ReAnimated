using System.Collections.Immutable;
using ReAnimated.Core.Mathematics;

namespace ReAnimated.Core.ModelAuthoring;

/// <summary>
/// Materializes prepared helper decisions in the existing authored-helper layer.
/// The immutable transition leaves imported bones, skin weights and source payloads untouched.
/// </summary>
public static class RiggingHelperMaterializer
{
    public static CustomModelDocument Apply(CustomModelDocument document)
    {
        ArgumentNullException.ThrowIfNull(document); document.Validate();
        RiggingSession session = document.RiggingSession ?? throw new InvalidOperationException("The model has no studio session.");
        ImmutableArray<RigParentObservation> observed = RiggingSessions.ObserveSourceHierarchy(document);
        var sourceIndices = observed.Take(document.Bones.Length).Select(static (row, index) => (row.EntityId, Index: index))
            .ToDictionary(static row => row.EntityId, static row => row.Index);
        var entities = session.Recipe.Entities.ToDictionary(static e => e.EntityId);
        var helpers = document.AuthoredHelpers.ToDictionary(static h => h.Id);
        var parentIds = document.AuthoredHelpers.ToDictionary(static h => h.Id, h => observed[h.ParentNodeIndex].EntityId);
        var existingOrder = document.AuthoredHelpers.Select(static (h, i) => (h.Id, Index: i)).ToDictionary(static h => h.Id, static h => h.Index);
        foreach (HelperRecipe recipe in session.Recipe.Helpers.Where(h => h.OwnerAssetId == document.ModelId))
        {
            RigEntityBinding entity = entities[recipe.EntityId];
            if (sourceIndices.TryGetValue(recipe.EntityId, out int sourceIndex))
            {
                if (observed[sourceIndex].ParentEntityId != recipe.ParentEntityId || document.Bones[sourceIndex].Name != entity.NativeName)
                    throw new InvalidOperationException("Imported-node renames and reparenting require a complete rig transaction; helper application cannot rewrite them.");
                // Existing source nodes consume frame/bounds overrides through the preparer.
                continue;
            }
            bool existing = helpers.TryGetValue(recipe.EntityId, out CustomModelAuthoredHelper? previous);
            if (!existing && (entity.Imported || entity.Kind != RigNativeEntityKind.Helper))
                throw new InvalidOperationException("Only explicitly authored helper entities can be created in the helper layer.");
            if (recipe.FramePolicy == RigFramePolicy.PreserveSource && (!existing || previous!.ExactLocalMatrix != recipe.LocalFrame))
                throw new InvalidOperationException("Preserve-source policy requires an existing unchanged helper frame.");
            TransformTRS local;
            if (previous is not null && previous.ExactLocalMatrix == recipe.LocalFrame) local = previous.LocalTransform;
            else
            {
                try { local = recipe.LocalFrame.Decompose(1e-7); }
                catch (InvalidOperationException) { local = Dl1AuthoredRigContract.ProjectAffineToTrs(recipe.LocalFrame); }
            }
            helpers[recipe.EntityId] = new()
            {
                Id = recipe.EntityId, Name = entity.NativeName, ExactLocalMatrix = recipe.LocalFrame, LocalTransform = local,
                Kind = previous?.Kind ?? recipe.FramePolicy switch
                {
                    RigFramePolicy.Camera => CustomModelAuthoredHelperKind.Camera,
                    RigFramePolicy.Socket => CustomModelAuthoredHelperKind.Prop,
                    _ => CustomModelAuthoredHelperKind.Helper,
                },
            };
            parentIds[recipe.EntityId] = recipe.ParentEntityId;
        }

        var children = helpers.Keys.ToDictionary(static id => id, static _ => new List<Guid>());
        var ready = new SortedSet<(int Order, Guid Id)>();
        foreach (Guid id in helpers.Keys)
        {
            Guid parent = parentIds[id];
            if (sourceIndices.ContainsKey(parent)) ready.Add((existingOrder.GetValueOrDefault(id, int.MaxValue), id));
            else if (children.TryGetValue(parent, out List<Guid>? siblings)) siblings.Add(id);
            else throw new InvalidOperationException("A helper's parent is absent from the authored model.");
        }
        var indices = new Dictionary<Guid, int>(sourceIndices);
        var ordered = ImmutableArray.CreateBuilder<CustomModelAuthoredHelper>(helpers.Count);
        while (ready.Count > 0)
        {
            var next = ready.Min;
            ready.Remove(next);
            CustomModelAuthoredHelper helper = helpers[next.Id] with { ParentNodeIndex = indices[parentIds[next.Id]] };
            indices.Add(next.Id, document.Bones.Length + ordered.Count);
            ordered.Add(helper);
            foreach (Guid child in children[next.Id]) ready.Add((existingOrder.GetValueOrDefault(child, int.MaxValue), child));
        }
        if (ordered.Count != helpers.Count) throw new InvalidOperationException("The prepared helper hierarchy contains a cycle.");
        ImmutableArray<CustomModelAuthoredHelper> materialized = ordered.MoveToImmutable();
        if (materialized.SequenceEqual(document.AuthoredHelpers)) return document;
        var updatedEntities = session.Recipe.Entities.Select(e => helpers.ContainsKey(e.EntityId)
            ? e with { SourceEntityId = "authored:" + e.EntityId.ToString("N"), Imported = false } : e).ToImmutableArray();
        var updated = document with
        {
            AuthoredHelpers = materialized, LastBuildReceipt = null,
            RiggingSession = RiggingSessions.Change(session, session with { Recipe = session.Recipe with { Entities = updatedEntities } }, RiggingEditKind.Helpers),
        };
        updated = updated with { RigSignature = CustomModelContractSignatures.ComputeRig(updated.CreateEffectiveBones()) };
        updated.Validate();
        return updated;
    }
}
