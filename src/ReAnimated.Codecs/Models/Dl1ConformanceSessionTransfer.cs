using System.Collections.Immutable;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using ReAnimated.Core.ModelAuthoring;

namespace ReAnimated.Codecs.Models;

internal static class Dl1ConformanceSessionTransfer
{
    internal static CustomModelDocument Apply(CustomModelDocument original, CustomModelDocument result,
        Dl1ConformanceHierarchy hierarchy, string templateId)
    {
        if (original.RiggingSession is not { } session) return result;
        var observed = RiggingSessions.ObserveSourceHierarchy(original);
        var entities = session.Recipe.Entities.ToDictionary(e => e.EntityId);
        var effective = result.CreateEffectiveBones();
        var ids = new Guid[effective.Length];
        for (int source = 0; source < hierarchy.SourceToEffective.Length; source++)
        {
            int target = hierarchy.SourceToEffective[source]; if (target < 0) continue;
            Guid id = observed[source].EntityId;
            if (ids[target] != Guid.Empty && ids[target] != id) throw new InvalidDataException("Conformance merged two stable entity identities.");
            ids[target] = id;
        }
        for (int index = 0; index < result.AuthoredHelpers.Length; index++) ids[result.Bones.Length + index] = result.AuthoredHelpers[index].Id;
        var occupiedEntityIds = entities.Keys.ToHashSet();
        var assignedOutputIds = new HashSet<Guid>();
        foreach (Guid id in ids)
        {
            if (id == Guid.Empty)
            {
                continue;
            }

            if (!assignedOutputIds.Add(id))
            {
                throw new InvalidDataException(
                    "Two retained conformance rows resolve to the same stable identity; conformance cannot replace either source identity safely.");
            }
            occupiedEntityIds.Add(id);
        }

        for (int index = 0; index < ids.Length; index++)
        {
            var bone = effective[index];
            string key = index >= result.Bones.Length ? "authored:" + ids[index].ToString("N")
                : bone.FbxObjectId == 0 ? "source-name:" + bone.Name : "fbx:" + bone.FbxObjectId.ToString(CultureInfo.InvariantCulture);
            if (ids[index] == Guid.Empty)
            {
                RigEntityBinding[] matchingSourceEntities = entities.Values
                    .Where(entity =>
                        entity.OwnerAssetId == original.ModelId &&
                        string.Equals(
                            entity.SourceEntityId,
                            key,
                            StringComparison.Ordinal))
                    .Take(2)
                    .ToArray();
                if (matchingSourceEntities.Length > 1)
                {
                    throw new InvalidDataException(
                        "A source identity maps to multiple retained entities and cannot be resolved safely.");
                }

                if (matchingSourceEntities.Length == 1)
                {
                    Guid retainedId = matchingSourceEntities[0].EntityId;
                    int retainedIndex = Array.FindIndex(ids, id => id == retainedId);
                    if (retainedIndex >= 0)
                    {
                        throw new InvalidDataException(
                            $"A source identity is already retained at output row {retainedIndex} and cannot also identify output row {index}.");
                    }

                    ids[index] = retainedId;
                    assignedOutputIds.Add(retainedId);
                    occupiedEntityIds.Add(retainedId);
                }
                else
                {
                    // Retained source and helper identities stay authoritative.
                    // Only a truly source-less output row receives a new ID.
                    // Preserve the legacy hash if free; deterministically salt
                    // it only when that hash is already owned or allocated.
                    ids[index] = CreateUniqueConformanceEntityId(
                        original.ModelId,
                        templateId,
                        bone.Name,
                        index,
                        occupiedEntityIds);
                    assignedOutputIds.Add(ids[index]);
                }
            }
            var existing = entities.GetValueOrDefault(ids[index]);
            if (entities.Values.Any(e => e.OwnerAssetId == original.ModelId && e.SourceEntityId == key && e.EntityId != ids[index]))
                throw new InvalidDataException("Conformance cannot reconcile a source identity without ambiguity.");
            entities[ids[index]] = (existing ?? new() { EntityId = ids[index], OwnerAssetId = original.ModelId, Imported = false }) with
            {
                NativeName = bone.Name, SourceEntityId = key,
                Kind = bone.IsWeighted && bone.Kind is ReAnimated.Core.Domain.BoneKind.Root or ReAnimated.Core.Domain.BoneKind.Deform
                    ? RigNativeEntityKind.Bone : RigNativeEntityKind.Helper,
            };
        }
        var byId = new Dictionary<Guid, int>();
        for (int index = 0; index < ids.Length; index++)
        {
            if (!byId.TryAdd(ids[index], index))
            {
                throw new InvalidDataException(
                    $"Conformance output rows {byId[ids[index]]} and {index} share one stable entity identity.");
            }
        }
        var removed = observed.Select(o => o.EntityId).Where(id => !byId.ContainsKey(id)).ToHashSet();
        if (session.WeightLocks.Any(l => removed.Contains(l.EntityId)))
            throw new InvalidOperationException("Conformance would drop a source influence with locked weights. Retain that bone or explicitly unlock it first.");
        var helpers = session.Recipe.Helpers.Where(h => h.OwnerAssetId != original.ModelId || !removed.Contains(h.EntityId)).Select(h =>
        {
            if (!byId.TryGetValue(h.EntityId, out int index)) return h;
            int parent = effective[index].ParentIndex;
            if (parent < 0) throw new InvalidOperationException("A helper frame decision cannot become a world root implicitly. Review the conformance mapping first.");
            return h with { ParentEntityId = ids[parent], LocalFrame = effective[index].ExactLocalBindMatrix, UserApproved = false };
        }).ToImmutableArray();
        var policies = session.Recipe.FramePolicies.Where(p => !removed.Contains(p.EntityId)).Select(p =>
            byId.TryGetValue(p.EntityId, out int index) && p.SolvedGlobalFrame is not null
                ? p with { SolvedGlobalFrame = hierarchy.EffectiveGlobals[index] } : p).ToImmutableArray();
        var parents = session.ParentDecisions.Select(p => byId.TryGetValue(p.EntityId, out int index)
            ? p with { ParentEntityId = effective[index].ParentIndex < 0 ? null : ids[effective[index].ParentIndex], UserApproved = false }
            : p with { UserApproved = false }).ToImmutableArray();
        var replacement = session with
        {
            Recipe = session.Recipe with { Entities = entities.Values.ToImmutableArray(), Helpers = helpers, FramePolicies = policies,
                ComponentPolicies = session.Recipe.ComponentPolicies.Where(p => !removed.Contains(p.EntityId)).ToImmutableArray() },
            ParentDecisions = parents,
            Hands = session.Hands.Select(h => h with { UserApproved = false, Fingers = h.Fingers.Select(f => f with { UserApproved = false }).ToImmutableArray() }).ToImmutableArray(),
            Eyes = session.Eyes.Select(e => e with { UserApproved = false }).ToImmutableArray(),
        };
        return result with { RiggingSession = replacement };
    }

    private static Guid CreateUniqueConformanceEntityId(
        Guid modelId,
        string templateId,
        string boneName,
        int effectiveIndex,
        HashSet<Guid> occupiedEntityIds)
    {
        string seed = modelId.ToString("N") + ":conformance:" + templateId + ":" + boneName;
        Guid candidate = HashToGuid(seed);
        if (candidate != Guid.Empty && occupiedEntityIds.Add(candidate))
        {
            return candidate;
        }

        for (int collisionIndex = 0; ; collisionIndex++)
        {
            candidate = HashToGuid(
                seed + ":output:" + effectiveIndex.ToString(CultureInfo.InvariantCulture) + ":" +
                collisionIndex.ToString(CultureInfo.InvariantCulture));
            if (candidate != Guid.Empty && occupiedEntityIds.Add(candidate))
            {
                return candidate;
            }
        }
    }

    private static Guid HashToGuid(string value) =>
        new(SHA256.HashData(Encoding.UTF8.GetBytes(value)).AsSpan(0, 16));
}
