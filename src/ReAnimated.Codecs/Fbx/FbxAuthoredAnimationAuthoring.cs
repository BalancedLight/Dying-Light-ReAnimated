using System.Collections.Immutable;
using System.Security.Cryptography;
using ReAnimated.Core.Domain;
using ReAnimated.Core.Mathematics;
using ReAnimated.Core.ModelAuthoring;
using ReAnimated.Codecs.Models;

namespace ReAnimated.Codecs.Fbx;

public static class FbxAuthoredAnimationAuthoring
{
    public const int MaximumCapturedKeyframeCount = 1_000_000;

    public static FbxModelAuthoringImportResult Create(FbxModelAuthoringImportResult model,
        string name, double durationSeconds, FrameRate frameRate)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (model.Rig is null) throw new InvalidOperationException("Choose a rig before creating an animation.");
        ImmutableArray<CustomModelBone> bones = RequireTrsRig(model);
        if (!double.IsFinite(durationSeconds) || durationSeconds <= 0 || frameRate.Numerator <= 0 || frameRate.Denominator <= 0)
            throw new ArgumentException("Animation duration and frame rate must be positive.");
        if (model.Package.Document.AnimationClips.Any(clip => clip.DisplayName.Equals(name.Trim(), StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("Choose a different animation name.");
        long frames = checked((long)Math.Round(durationSeconds * frameRate.FramesPerSecond,
            MidpointRounding.AwayFromZero) + 1);
        if (frames < 2 || frames > ushort.MaxValue)
            throw new ArgumentException("The animation duration exceeds the supported frame range.");
        Guid id = Guid.NewGuid();
        var data = new DerivedAnimationData(id, name.Trim(), frameRate, frames,
            bones.Select(bone => new DerivedTransformTrack(bone.Name,
                [new TransformKeyframe(0, bone.LocalBindTransform), new TransformKeyframe(frames - 1, bone.LocalBindTransform)]))
                .ToImmutableArray());
        var clip = new CustomModelAnimationClip
        {
            Id = id,
            FbxObjectId = 0,
            SourceName = name.Trim(),
            DisplayName = name.Trim(),
            FrameRate = frameRate,
            FrameCount = frames,
            HasSkeletalTracks = true,
            RootBoneName = model.Rig.Bones.FirstOrDefault(static bone => bone.ParentIndex < 0)?.Name,
        };
        if (string.IsNullOrWhiteSpace(model.Package.Document.BuildSettings.AnimationScriptAlias))
            model = model with
            {
                Package = model.Package with
                {
                    Document = model.Package.Document with
                    {
                        BuildSettings = model.Package.Document.BuildSettings with
                        {
                            AnimationScriptAlias = Dl1SourceModelWriter.SanitizeName(
                                model.Package.Document.BuildSettings.ResourceName, 55),
                        },
                    },
                },
            };
        return Save(model, clip, data);
    }

    /// <summary>Captures a sampled runtime-rig clip as a separate editable model animation.</summary>
    public static FbxModelAuthoringImportResult Capture(
        FbxModelAuthoringImportResult model,
        string name,
        AnimationClip sampledClip)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(sampledClip);
        if (model.Rig is null)
            throw new InvalidOperationException("Choose a rig before capturing animation keys.");

        ImmutableArray<CustomModelBone> bones = RequireTrsRig(model);
        if (model.Rig.BoneCount != bones.Length)
            throw new ArgumentException("The sampled clip must target the model's effective runtime rig.", nameof(sampledClip));
        for (int index = 0; index < bones.Length; index++)
        {
            BoneDefinition runtimeBone = model.Rig.Bones[index];
            CustomModelBone authoredBone = bones[index];
            if (!string.Equals(runtimeBone.Name, authoredBone.Name, StringComparison.Ordinal) ||
                runtimeBone.ParentIndex != authoredBone.ParentIndex ||
                !runtimeBone.LocalBindPose.ToMatrix().NearlyEquals(authoredBone.ExactLocalBindMatrix, 1e-7))
                throw new ArgumentException("The sampled clip runtime rig differs from the model's effective authored rig.", nameof(sampledClip));
        }
        if (sampledClip.FrameRate.Numerator <= 0 || sampledClip.FrameRate.Denominator <= 0 ||
            sampledClip.FrameCount <= 0 || sampledClip.FrameCount > ushort.MaxValue)
            throw new ArgumentException("The sampled clip cadence or frame range is invalid.", nameof(sampledClip));
        if (sampledClip.TransformTracks.IsEmpty && sampledClip.ScalarTracks.IsEmpty &&
            sampledClip.AuxiliaryTransformTracks.IsEmpty)
            throw new ArgumentException("Capture at least one animation track.", nameof(sampledClip));

        long totalKeys = 0;
        foreach (TransformTrack track in sampledClip.TransformTracks)
        {
            if ((uint)track.BoneIndex >= (uint)bones.Length)
                throw new ArgumentException("A sampled transform track is outside the model's effective runtime rig.", nameof(sampledClip));
            AddKeyCount(track.Keyframes.Length);
            foreach (TransformKeyframe key in track.Keyframes)
                ValidateTransformKey(key, sampledClip.FrameCount, nameof(sampledClip));
        }
        foreach (ScalarTrack track in sampledClip.ScalarTracks)
        {
            AddKeyCount(track.Keyframes.Length);
            foreach (ScalarKeyframe key in track.Keyframes)
                if (!double.IsFinite(key.Frame) || key.Frame < 0 || key.Frame >= sampledClip.FrameCount ||
                    !double.IsFinite(key.Value))
                    throw new ArgumentException("A sampled scalar key is outside the clip range or non-finite.", nameof(sampledClip));
        }
        foreach (AuxiliaryTransformTrack track in sampledClip.AuxiliaryTransformTracks)
        {
            AddKeyCount(track.Keyframes.Length);
            foreach (TransformKeyframe key in track.Keyframes)
                ValidateTransformKey(key, sampledClip.FrameCount, nameof(sampledClip));
        }
        if (model.Package.Document.AnimationClips.Any(clip =>
                clip.DisplayName.Equals(name.Trim(), StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("Choose a different animation name.");

        Guid id = Guid.NewGuid();
        string clipName = name.Trim();
        var data = new DerivedAnimationData(
            id,
            clipName,
            sampledClip.FrameRate,
            sampledClip.FrameCount,
            sampledClip.TransformTracks.Select(track => new DerivedTransformTrack(
                bones[track.BoneIndex].Name,
                track.Keyframes)).ToImmutableArray(),
            sampledClip.ScalarTracks.Select(track => new DerivedScalarTrack(
                track.ChannelName,
                track.Keyframes)).ToImmutableArray(),
            sampledClip.AuxiliaryTransformTracks.Select(track => new DerivedAuxiliaryTrack(
                track.Descriptor,
                track.Keyframes)).ToImmutableArray());
        var selection = new CustomModelAnimationClip
        {
            Id = id,
            FbxObjectId = 0,
            SourceName = clipName,
            DisplayName = clipName,
            Included = true,
            FrameRate = sampledClip.FrameRate,
            FrameCount = sampledClip.FrameCount,
            HasSkeletalTracks = !sampledClip.TransformTracks.IsEmpty,
            HasMorphTracks = !sampledClip.ScalarTracks.IsEmpty,
            RootBoneName = model.Rig.Bones.FirstOrDefault(static bone => bone.ParentIndex < 0)?.Name,
        };
        if (string.IsNullOrWhiteSpace(model.Package.Document.BuildSettings.AnimationScriptAlias))
            model = model with
            {
                Package = model.Package with
                {
                    Document = model.Package.Document with
                    {
                        BuildSettings = model.Package.Document.BuildSettings with
                        {
                            AnimationScriptAlias = Dl1SourceModelWriter.SanitizeName(
                                model.Package.Document.BuildSettings.ResourceName, 55),
                        },
                    },
                },
            };
        return Save(model, selection, data);

        void AddKeyCount(int count)
        {
            totalKeys = checked(totalKeys + count);
            if (totalKeys > MaximumCapturedKeyframeCount)
                throw new InvalidOperationException(
                    $"The sampled clip exceeds the {MaximumCapturedKeyframeCount:N0}-key capture limit.");
        }
    }

    public static FbxModelAuthoringImportResult SetKey(FbxModelAuthoringImportResult model,
        Guid clipId, int boneIndex, long frame, TransformTRS value)
    {
        ArgumentNullException.ThrowIfNull(model);
        CustomModelAnimationClip clip = model.Package.Document.AnimationClips.Single(selection => selection.Id == clipId);
        ValidateBinding(model, clip);
        ImmutableArray<CustomModelBone> bones = model.Package.Document.CreateEffectiveBones();
        if ((uint)boneIndex >= (uint)bones.Length || frame < 0 || frame >= clip.FrameCount || !value.ToMatrix().IsFinite)
            throw new ArgumentException("The animation key has an invalid node, frame or transform.");
        DerivedAnimationData data = Read(model.Package, clip);
        string name = bones[boneIndex].Name;
        ImmutableArray<DerivedTransformTrack> tracks = data.TransformTracks.Select(track => track.BoneName == name
            ? track with { Keyframes = track.Keyframes.Where(key => key.Frame != frame)
                .Append(new TransformKeyframe(frame, value)).OrderBy(static key => key.Frame).ToImmutableArray() }
            : track).ToImmutableArray();
        return Save(model, clip, data with { TransformTracks = tracks });
    }

    public static void ValidateExport(FbxModelAuthoringImportResult model, CustomModelAnimationClip selection)
    {
        ValidateBinding(model, selection);
        FbxDerivedMotionAuthoring.ValidateComponentPolicies(model.Package.Document, Read(model.Package, selection));
    }

    private static void ValidateBinding(FbxModelAuthoringImportResult model, CustomModelAnimationClip selection)
    {
        AuthoredAnimationReference reference = selection.AuthoredAnimation ??
            throw new InvalidOperationException("Choose an authored animation.");
        if (!reference.RigSignature.Equals(CurrentRigSignature(model.Package.Document), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Prepare this animation for the changed rig before export.");
        _ = Read(model.Package, selection);
    }

    internal static FbxModelAuthoringImportResult RestoreAvailability(FbxModelAuthoringImportResult model)
    {
        CustomModelPackageSerializer.ValidateAuthoredAnimationPayloads(model.Package);
        if (!model.Package.Document.AnimationClips.Any(static clip => clip.AuthoredAnimation is not null))
            return model;
        ImmutableDictionary<Guid, AnimationClip>.Builder clips = model.AnimationClips.ToBuilder();
        foreach (CustomModelAnimationClip selection in model.Package.Document.AnimationClips.Where(static clip => clip.AuthoredAnimation is not null))
        {
            if (!selection.AuthoredAnimation!.RigSignature.Equals(CurrentRigSignature(model.Package.Document), StringComparison.OrdinalIgnoreCase))
            {
                clips.Remove(selection.Id);
                continue;
            }
            clips[selection.Id] = Decode(model.Package.Document, Read(model.Package, selection));
        }
        return model with { AnimationClips = clips.ToImmutable() };
    }

    private static FbxModelAuthoringImportResult Save(FbxModelAuthoringImportResult model,
        CustomModelAnimationClip selection, DerivedAnimationData data)
    {
        ImmutableArray<byte> payload = DerivedAnimationDataCodec.Serialize(data);
        string hash = Convert.ToHexStringLower(SHA256.HashData(payload.AsSpan()));
        selection = selection with
        {
            SourceFingerprint = hash,
            AuthoredAnimation = new()
            {
                RigSignature = CurrentRigSignature(model.Package.Document),
                PayloadSha256 = hash,
                PayloadLength = payload.Length,
            },
        };
        CustomModelDocument document = model.Package.Document with
        {
            AnimationClips = model.Package.Document.AnimationClips.Where(clip => clip.Id != selection.Id)
                .Append(selection).ToImmutableArray(),
            LastBuildReceipt = null,
        };
        document.Validate();
        CustomModelPackage package = model.Package with
        {
            Document = document,
            AuthoredAnimationPayloads = model.Package.AuthoredAnimationPayloads.SetItem(selection.Id, payload),
        };
        return model with { Package = package, AnimationClips = model.AnimationClips.SetItem(selection.Id, Decode(document, data)) };
    }

    private static DerivedAnimationData Read(CustomModelPackage package, CustomModelAnimationClip clip)
    {
        AuthoredAnimationReference reference = clip.AuthoredAnimation!;
        reference.Validate();
        if (!package.AuthoredAnimationPayloads.TryGetValue(clip.Id, out ImmutableArray<byte> payload) ||
            payload.Length != reference.PayloadLength ||
            !Convert.ToHexStringLower(SHA256.HashData(payload.AsSpan())).Equals(reference.PayloadSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The authored animation payload does not match its manifest.");
        DerivedAnimationData data = DerivedAnimationDataCodec.Deserialize(payload.AsSpan());
        if (data.ClipId != clip.Id || data.FrameCount != clip.FrameCount || data.FrameRate != clip.FrameRate)
            throw new InvalidDataException("The authored animation timing or identity differs from its manifest.");
        return data;
    }

    private static string CurrentRigSignature(CustomModelDocument document) =>
        CustomModelContractSignatures.ComputeRig(document.CreateEffectiveBones());

    private static ImmutableArray<CustomModelBone> RequireTrsRig(FbxModelAuthoringImportResult model)
    {
        ImmutableArray<CustomModelBone> bones = model.Package.Document.CreateEffectiveBones();
        CustomModelBone? affineBone = bones.FirstOrDefault(static bone =>
            !bone.LocalBindTransform.ToMatrix().NearlyEquals(bone.ExactLocalBindMatrix, 1e-7));
        if (affineBone is not null)
            throw new InvalidOperationException($"Review the bind frame of '{affineBone.Name}' before creating animation keys.");
        return bones;
    }

    private static void ValidateTransformKey(TransformKeyframe key, long frameCount, string parameterName)
    {
        TransformTRS value = key.Value;
        if (!double.IsFinite(key.Frame) || key.Frame < 0 || key.Frame >= frameCount || !value.IsFinite ||
            !double.IsFinite(value.Scale.X) || !double.IsFinite(value.Scale.Y) || !double.IsFinite(value.Scale.Z) ||
            Math.Abs(value.Scale.X) <= 1e-12 || Math.Abs(value.Scale.Y) <= 1e-12 || Math.Abs(value.Scale.Z) <= 1e-12)
            throw new ArgumentException("A sampled transform key is outside the clip range or contains a non-finite/singular transform.", parameterName);
    }

    private static AnimationClip Decode(CustomModelDocument document, DerivedAnimationData data)
    {
        Dictionary<string, int> indices = document.CreateEffectiveBones().ToDictionary(static bone => bone.Name,
            static bone => bone.Index, StringComparer.Ordinal);
        if (data.TransformTracks.Any(track => !indices.ContainsKey(track.BoneName)))
            throw new InvalidDataException("An authored animation references a missing rig node.");
        return new AnimationClip(data.Name, data.FrameRate, data.FrameCount,
            data.TransformTracks.Select(track => new TransformTrack(indices[track.BoneName], track.Keyframes)),
            data.ScalarTracks.Select(static track => new ScalarTrack(track.ChannelName, track.Keyframes)),
            data.AuxiliaryTracks.Select(static track => new AuxiliaryTransformTrack(track.Descriptor, track.Keyframes)));
    }
}
