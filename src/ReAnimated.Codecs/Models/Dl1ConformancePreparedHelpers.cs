using System.Collections.Immutable;
using ReAnimated.Codecs.Fbx;
using ReAnimated.Core.Mathematics;
using ReAnimated.Core.ModelAuthoring;
using ReAnimated.Retargeting.Conformance;

namespace ReAnimated.Codecs.Models;

internal static class Dl1ConformancePreparedHelpers
{
    internal static FbxModelAuthoringImportResult Preserve(FbxModelAuthoringImportResult original, FbxModelAuthoringImportResult candidate,
        RigConformanceResult fit, CancellationToken token)
    {
        var before = original.Package.Document;
        if (before.AuthoredHelpers.IsEmpty || before.RiggingSession is null) return candidate;
        var oldPrepared = Dl1CustomModelRigPreparer.Prepare(original, token).Contract.Nodes.ToDictionary(n => n.SourceBoneIndex);
        var newPrepared = Dl1CustomModelRigPreparer.Prepare(candidate, token).Contract.Nodes.ToDictionary(n => n.SourceBoneIndex);
        var doc = candidate.Package.Document; var session = doc.RiggingSession!;
        var globals = new List<TransformMatrix>(); var actual = new List<TransformMatrix>();
        foreach (var bone in doc.Bones)
        {
            globals.Add(bone.ParentIndex < 0 ? bone.ExactLocalBindMatrix : globals[bone.ParentIndex] * bone.ExactLocalBindMatrix);
            actual.Add(newPrepared[bone.Index].GlobalBindMatrix);
        }
        var helpers = doc.AuthoredHelpers.ToBuilder(); var recipes = session.Recipe.Helpers.ToDictionary(h => h.EntityId);
        var policies = session.Recipe.FramePolicies.ToDictionary(p => p.EntityId);
        for (int i = 0; i < helpers.Count; i++)
        {
            token.ThrowIfCancellationRequested(); var helper = helpers[i];
            int oldIndex = before.Bones.Length + Enumerable.Range(0, before.AuthoredHelpers.Length).Single(j => before.AuthoredHelpers[j].Id == helper.Id);
            TransformMatrix desired = fit.RestPoseTransfer.SkinningTransforms[oldIndex] * oldPrepared[oldIndex].GlobalBindMatrix;
            var recipe = recipes.GetValueOrDefault(helper.Id);
            TransformMatrix parent = recipe?.FollowPreparedParent == true ? actual[helper.ParentNodeIndex] : globals[helper.ParentNodeIndex];
            TransformMatrix local = parent.InvertedAffine() * desired;
            helpers[i] = helper with { ExactLocalMatrix = local, LocalTransform = FbxCoreAnimationAdapter.ProjectAffineToTrs(local, helper.Name, "conformed prepared helper") };
            if (recipe is not null) recipes[helper.Id] = recipe with { LocalFrame = local, UserApproved = false };
            if (policies.TryGetValue(helper.Id, out var policy) && policy.SolvedGlobalFrame is not null)
                policies[helper.Id] = policy with { SolvedGlobalFrame = desired };
            globals.Add(globals[helper.ParentNodeIndex] * local); actual.Add(desired);
        }
        doc = doc with { AuthoredHelpers = helpers.ToImmutable(), RiggingSession = session with
            { Recipe = session.Recipe with { Helpers = session.Recipe.Helpers.Select(h => recipes[h.EntityId]).ToImmutableArray(),
                FramePolicies = session.Recipe.FramePolicies.Select(p => policies[p.EntityId]).ToImmutableArray() } } };
        doc = doc with { RigSignature = CustomModelContractSignatures.ComputeRig(doc.CreateEffectiveBones()) };
        var inverse = globals.Select(g => g.InvertedAffine()).ToArray();
        return candidate with { Package = candidate.Package with { Document = doc }, Rig = doc.CreateRigDefinition(),
            Surfaces = candidate.Surfaces.Select(s => s with { InverseBindMatrices = s.PaletteBoneIndices.Select(i => inverse[i]).ToImmutableArray() }).ToImmutableArray() };
    }
}
