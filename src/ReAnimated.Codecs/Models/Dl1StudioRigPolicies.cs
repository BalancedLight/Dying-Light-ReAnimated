using System.Collections.Immutable;
using ReAnimated.Core.Mathematics;
using ReAnimated.Core.ModelAuthoring;

namespace ReAnimated.Codecs.Models;

internal static class Dl1StudioRigPolicies
{
    public static ImmutableArray<RigEntityFramePolicy> Resolve(CustomModelDocument document,
        ImmutableArray<TransformMatrix> sourceGlobals)
    {
        RiggingSession? session = document.RiggingSession;
        if (session is null) return default;
        ImmutableArray<RigParentObservation> observed = RiggingSessions.ObserveSourceHierarchy(document);
        ImmutableArray<CustomModelBone> bones = document.CreateEffectiveBones();
        var entities = session.Recipe.Entities.ToDictionary(static e => e.EntityId);
        var indices = observed.Select(static (row, index) => (row.EntityId, Index: index)).ToDictionary(static row => row.EntityId, static row => row.Index);
        var policies = session.Recipe.FramePolicies.ToDictionary(static p => p.EntityId);
        foreach (RigEntityFramePolicy policy in session.Recipe.FramePolicies)
            if (entities[policy.EntityId].OwnerAssetId == document.ModelId && !indices.ContainsKey(policy.EntityId))
                throw new InvalidDataException("A frame policy targets an entity not yet materialized in this model's hierarchy.");
        foreach (HelperRecipe helper in session.Recipe.Helpers.Where(h => h.OwnerAssetId == document.ModelId))
        {
            if (!indices.TryGetValue(helper.EntityId, out int index) || observed[index].ParentEntityId != helper.ParentEntityId ||
                !indices.TryGetValue(helper.ParentEntityId, out int parent))
                throw new InvalidDataException($"Apply the helper creation/parent edit for role '{helper.RoleId}' before preparing the model.");
            bool preserve = helper.FramePolicy == RigFramePolicy.PreserveSource;
            if (preserve && !helper.LocalFrame.NearlyEquals(bones[index].ExactLocalBindMatrix, 1e-9))
                throw new InvalidDataException($"Helper '{entities[helper.EntityId].NativeName}' has a replacement frame but still selects preserve-source policy.");
            policies.Add(helper.EntityId, new()
            {
                EntityId = helper.EntityId, FramePolicy = helper.FramePolicy,
                SolvedGlobalFrame = preserve ? null : sourceGlobals[parent] * helper.LocalFrame,
                BoundsPolicy = helper.BoundsCenter is null ? RigBoundsPolicy.GenerateSegmentProxy : preserve ? RigBoundsPolicy.PreserveSource : RigBoundsPolicy.Solved,
                BoundsCenter = helper.BoundsCenter, BoundsHalfExtents = helper.BoundsHalfExtents, Evidence = helper.Evidence,
            });
        }
        var result = ImmutableArray.CreateBuilder<RigEntityFramePolicy>(observed.Length);
        for (int i = 0; i < observed.Length; i++)
        {
            Guid id = observed[i].EntityId;
            if (entities[id].NativeName != bones[i].Name)
                throw new InvalidDataException("Apply entity renames to the source hierarchy before preparing the model.");
            RigEntityFramePolicy policy = policies.TryGetValue(id, out RigEntityFramePolicy? selected)
                ? selected : new() { EntityId = id };
            policy.Validate();
            result.Add(policy);
        }
        return result.MoveToImmutable();
    }

    public static TransformMatrix RoundMatrix(TransformMatrix value)
    {
        var result = new TransformMatrix(
            Single(value.M11), Single(value.M12), Single(value.M13), Single(value.M14),
            Single(value.M21), Single(value.M22), Single(value.M23), Single(value.M24),
            Single(value.M31), Single(value.M32), Single(value.M33), Single(value.M34), 0, 0, 0, 1);
        if (!result.IsFinite || !double.IsFinite(result.LinearDeterminant) || result.LinearDeterminant == 0)
            throw new InvalidDataException("A prepared frame becomes singular or non-finite at native float precision.");
        return result;
    }

    public static Dl1AuthoredBoneBounds RoundBounds(Dl1AuthoredBoneBounds bounds) => new(
        new(Single(bounds.Center.X), Single(bounds.Center.Y), Single(bounds.Center.Z)),
        new(Single(bounds.HalfExtents.X), Single(bounds.HalfExtents.Y), Single(bounds.HalfExtents.Z)));

    private static float Single(double value)
    {
        float result = (float)value;
        if (!float.IsFinite(result)) throw new InvalidDataException("A prepared value exceeds native float precision.");
        return result;
    }
}
