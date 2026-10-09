using System.Collections.Immutable;
using ReAnimated.Core.Domain;
using ReAnimated.Core.ModelAuthoring;

namespace ReAnimated.Codecs.Fbx;

/// <summary>Checks the frames that affect an imported animation without binding it to unrelated helpers.</summary>
public static class FbxAnimationRigBinding
{
    public static string Compute(CustomModelDocument document, AnimationClip clip)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(clip);
        ImmutableArray<CustomModelBone> bones = document.CreateEffectiveBones();
        var included = new HashSet<int>();
        foreach (TransformTrack track in clip.TransformTracks)
        {
            if ((uint)track.BoneIndex >= (uint)bones.Length)
                throw new InvalidDataException("An animation track references a missing rig node.");
            int index = track.BoneIndex;
            while (index >= 0 && included.Add(index))
                index = bones[index].ParentIndex;
        }

        CustomModelBone[] ordered = included.Select(index => bones[index])
            .OrderBy(static bone => bone.Name, StringComparer.Ordinal).ToArray();
        Dictionary<int, int> indexes = ordered.Select((bone, index) => (bone.Index, Index: index))
            .ToDictionary(static row => row.Item1, static row => row.Index);
        return CustomModelContractSignatures.ComputeRig(ordered.Select((bone, index) => bone with
        {
            Index = index,
            ParentIndex = bone.ParentIndex < 0 ? -1 : indexes[bone.ParentIndex],
            Kind = BoneKind.Deform,
            IsWeighted = false,
        }));
    }

    internal static CustomModelDocument Capture(CustomModelDocument document,
        IReadOnlyDictionary<Guid, AnimationClip> clips) => document with
    {
        AnimationClips = document.AnimationClips.Select(selection =>
            selection.DerivedMotion is null && selection.AuthoredAnimation is null && clips.TryGetValue(selection.Id, out AnimationClip? clip)
                ? selection with { SourceBindFingerprint = Compute(document, clip) }
                : selection).ToImmutableArray(),
    };

    internal static FbxModelAuthoringImportResult RestoreSourceBindings(
        FbxModelAuthoringImportResult model, FbxModelAuthoringImportResult source)
    {
        Dictionary<string, CustomModelAnimationClip> originals = source.Package.Document.AnimationClips
            .Where(static clip => clip.DerivedMotion is null && clip.AuthoredAnimation is null)
            .ToDictionary(static clip => clip.SourceFingerprint, StringComparer.Ordinal);
        CustomModelDocument document = model.Package.Document;
        ImmutableArray<CustomModelAnimationClip> selections = document.AnimationClips.Select(selection =>
        {
            if (selection.DerivedMotion is not null || selection.AuthoredAnimation is not null ||
                !originals.TryGetValue(selection.SourceFingerprint, out CustomModelAnimationClip? original))
                return selection;
            if (selection.SourceBindFingerprint is { } saved && original.SourceBindFingerprint is { } actual &&
                !string.Equals(saved, actual, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("An animation binding differs from its source file.");
            return selection with { SourceBindFingerprint = original.SourceBindFingerprint };
        }).ToImmutableArray();
        return model with { Package = model.Package with { Document = document with { AnimationClips = selections } } };
    }

    public static bool Matches(FbxModelAuthoringImportResult model, CustomModelAnimationClip selection)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(selection);
        if (selection.SourceBindFingerprint is null)
            return model.Package.Document.RigConformance?.AppliedOutputRigSignature is null;
        return model.AnimationClips.TryGetValue(selection.Id, out AnimationClip? clip) &&
            string.Equals(selection.SourceBindFingerprint, Compute(model.Package.Document, clip),
                StringComparison.OrdinalIgnoreCase);
    }
}
