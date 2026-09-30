using System.Collections.Immutable;
using ReAnimated.Codecs.Models;
using ReAnimated.Core.Mathematics;
using ReAnimated.Core.ModelAuthoring;

namespace ReAnimated.Codecs.Fbx;

public static partial class FbxCompilerRetentionAuthoring
{
    /// <summary>Removes an unused authored retention leaf. Callers must recheck external project blockers at commit.</summary>
    public static StructuralHelperPreview PreviewRemoval(FbxModelAuthoringImportResult model, Guid helperId,
        IReadOnlyList<string>? externalBlockers = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(model); cancellationToken.ThrowIfCancellationRequested();
        if (externalBlockers is { Count: > 0 })
            throw new InvalidOperationException("Resolve project references before removing this helper: " + string.Join("; ", externalBlockers.Take(6)));
        var doc = model.Package.Document;
        var session = doc.RiggingSession ?? throw new InvalidOperationException("Start a studio session before removing a retention helper.");
        var recipe = session.Recipe.Helpers.SingleOrDefault(h => h.EntityId == helperId && h.RoleId == RoleId)
            ?? throw new InvalidOperationException("Select an authored compiler-retention helper.");
        if (recipe.LockedFields != RigHelperEditFields.None)
            throw new InvalidOperationException("Unlock the retention helper's protected decisions before removing it.");
        int helperIndex = Enumerable.Range(0, doc.AuthoredHelpers.Length).FirstOrDefault(i => doc.AuthoredHelpers[i].Id == helperId, -1);
        if (helperIndex < 0) throw new InvalidOperationException("The retention helper is not in the current authored hierarchy.");
        int removed = doc.Bones.Length + helperIndex;
        string name = doc.AuthoredHelpers[helperIndex].Name;
        var beforeBones = doc.CreateEffectiveBones();
        var beforePrepared = Dl1CustomModelRigPreparer.Prepare(model, cancellationToken); // Enforces unweighted leaf role.
        if (doc.Camera.ActivePreviewNodeName?.Equals(name, StringComparison.OrdinalIgnoreCase) == true ||
            doc.AnimationClips.Any(c => c.RootBoneName?.Equals(name, StringComparison.OrdinalIgnoreCase) == true))
            throw new InvalidOperationException("The helper is selected by a camera or animation-root reference. Reassign it before removal.");
        if (model.AnimationClips.Values.Any(c => c.TransformTracks.Any(t => t.BoneIndex == removed)))
            throw new InvalidOperationException("The helper owns authored animation tracks. Preserve or reassign those tracks before removal.");
        if (!model.Package.DerivedAnimationPayloads.IsEmpty || model.AnimationClips.Values.Any(c => !c.AuxiliaryTransformTracks.IsEmpty))
            throw new InvalidOperationException("Derived or auxiliary animation data requires dependency reconciliation before removing rig nodes.");
        foreach (var native in doc.SecondaryMotion.NativeSources)
        {
            var syntax = Dl1ClothCodec.Parse(native.Text);
            if (syntax.Commands.Any(c => c.Name == "!include" &&
                !(native.Kind == NativeClothSourceKind.Phx && c.Arguments.Length == 1 && c.Arguments[0] == "\"MeshPartCloth.def\"")))
                throw new InvalidDataException($"Native cloth '{native.ResourceName}' has an external include whose bone dependencies must be resolved before removal.");
            var diagnostics = native.Kind == NativeClothSourceKind.Phx
                ? Dl1ClothCodec.ReadPhx(native.Text, beforeBones.Select(b => b.Name)).Diagnostics
                : Dl1ClothCodec.ReadMpCloth(native.Text, doc.SecondaryMotion.NativeSources.Where(s => s.Kind == NativeClothSourceKind.Phx).Select(s => s.ResourceName)).Diagnostics;
            if (diagnostics.Any(d => d.IsError || d.Code == "native_statement_unsupported"))
                throw new InvalidDataException($"Resolve unsupported or invalid native cloth statements in '{native.ResourceName}' before removing a rig node.");
        }
        var map = Enumerable.Range(0, beforeBones.Length).Select(i => i == removed ? -1 : i > removed ? i - 1 : i).ToImmutableArray();
        var helpers = doc.AuthoredHelpers.RemoveAt(helperIndex).Select(h => h.ParentNodeIndex > removed
            ? h with { ParentNodeIndex = h.ParentNodeIndex - 1 } : h).ToImmutableArray();
        var changedSession = session with { Recipe = session.Recipe with
        {
            Entities = session.Recipe.Entities.Where(e => e.EntityId != helperId).ToImmutableArray(),
            Helpers = session.Recipe.Helpers.Where(h => h.EntityId != helperId).ToImmutableArray(),
            ComponentPolicies = session.Recipe.ComponentPolicies.Where(p => p.EntityId != helperId).ToImmutableArray(),
            FramePolicies = session.Recipe.FramePolicies.Where(p => p.EntityId != helperId).ToImmutableArray(),
        } };
        try { changedSession.Validate(); }
        catch (ArgumentException error)
        { throw new InvalidOperationException("The helper is referenced by saved rig decisions. Reassign those references before removal. " + error.Message, error); }
        var updated = doc with { AuthoredHelpers = helpers, RiggingSession = RiggingSessions.Change(session, changedSession, RiggingEditKind.Helpers), LastBuildReceipt = null };
        updated = updated with { RigSignature = CustomModelContractSignatures.ComputeRig(updated.CreateEffectiveBones()) };
        var companions = Dl1CompanionReferenceTransfer.Apply(doc.SecondaryMotion, beforeBones, updated.CreateEffectiveBones(), map,
            beforeBones.Select(_ => TransformMatrix.Identity).ToImmutableArray(), cancellationToken);
        updated = updated with { SecondaryMotion = companions.Definition, Diagnostics = updated.Diagnostics.Add(new()
        {
            Code = ReviewDiagnosticCode, Severity = CustomModelImportSeverity.Warning,
            Message = $"Retention helper '{name}' was removed. Its parent may be omitted by the compiler; verify the new output before deployment.",
        }) };
        updated.Validate();
        var surfaces = model.Surfaces.Select(surface =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (surface.PaletteBoneIndices.Length != surface.InverseBindMatrices.Length ||
                surface.PaletteBoneIndices.Any(i => (uint)i >= (uint)map.Length))
                throw new InvalidDataException("A surface has invalid palette references.");
            var slots = Enumerable.Range(0, surface.PaletteBoneIndices.Length).Where(i => surface.PaletteBoneIndices[i] != removed).ToArray();
            var slotMap = Enumerable.Repeat(-1, surface.PaletteBoneIndices.Length).ToArray();
            for (int i = 0; i < slots.Length; i++) slotMap[slots[i]] = i;
            var vertices = surface.Vertices.Select(v =>
            {
                if (v.BoneIndices.Length != v.BoneWeights.Length || v.BoneIndices.Any(i => (uint)i >= (uint)slotMap.Length))
                    throw new InvalidDataException("A vertex has invalid palette slots.");
                if (v.BoneIndices.Select((slot, i) => (slot, weight: v.BoneWeights[i])).Any(w => slotMap[w.slot] < 0 && w.weight != 0))
                    throw new InvalidDataException("A removed helper still owns a skin influence.");
                var retained = Enumerable.Range(0, v.BoneIndices.Length).Where(i => slotMap[v.BoneIndices[i]] >= 0).ToArray();
                var indices = retained.Select(i => slotMap[v.BoneIndices[i]]).ToImmutableArray();
                return indices.SequenceEqual(v.BoneIndices) ? v : v with
                    { BoneIndices = indices, BoneWeights = retained.Select(i => v.BoneWeights[i]).ToImmutableArray() };
            }).ToImmutableArray();
            var palette = slots.Select(i => map[surface.PaletteBoneIndices[i]]).ToImmutableArray();
            return palette.SequenceEqual(surface.PaletteBoneIndices) && vertices.SequenceEqual(surface.Vertices) ? surface : surface with
                { PaletteBoneIndices = palette, InverseBindMatrices = slots.Select(i => surface.InverseBindMatrices[i]).ToImmutableArray(), Vertices = vertices };
        }).ToImmutableArray();
        var clips = FbxAnimationTrackReindexer.ReindexAvailable(model.AnimationClips, map, cancellationToken);
        if (clips.Count != model.AnimationClips.Count) throw new InvalidDataException("Removal would discard an animation clip.");
        var candidate = model with { Package = model.Package with { Document = updated }, Rig = updated.CreateRigDefinition(), Surfaces = surfaces, AnimationClips = clips };
        FbxProfileEditGuard.RequireAllowed(model, candidate, cancellationToken);
        var prepared = Dl1CustomModelRigPreparer.Prepare(candidate, cancellationToken);
        foreach (var before in beforePrepared.Contract.Nodes.Where(n => n.SourceBoneIndex != removed))
        {
            var after = prepared.Contract.Nodes.Single(n => n.SourceBoneIndex == map[before.SourceBoneIndex]);
            if (before.Name != after.Name || before.IsDeform != after.IsDeform || !before.GlobalBindMatrix.NearlyEquals(after.GlobalBindMatrix, 1e-9) ||
                !before.InverseGlobalReferenceMatrix.NearlyEquals(after.InverseGlobalReferenceMatrix, 1e-9) || before.Bounds != after.Bounds)
                throw new InvalidDataException($"Removal changed prepared node '{before.Name}'. Review its frame/bounds dependencies first.");
        }
        return new(model, candidate, helperId, $"Preview only: remove retention helper '{name}'. Remaining nodes and skinning are preserved. Its parent may be removed by the compiler; compile and revalidate the changed rig before deployment.")
            { RemovedHelperId = helperId };
    }
}
