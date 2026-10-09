using System.Collections.Immutable;
using System.Security.Cryptography;
using ReAnimated.Core.Domain;
using ReAnimated.Core.ModelAuthoring;
using ReAnimated.Codecs.Anm2;

namespace ReAnimated.Codecs.Fbx;

public static class FbxAnimationTimingAuthoring
{
    public static AnimationClip WithFrameRate(AnimationClip clip, FrameRate frameRate)
    {
        ArgumentNullException.ThrowIfNull(clip);
        if (clip.FrameRate == frameRate) return clip;
        long frames = clip.FrameCount == 1 ? 1 : Math.Max(2,
            checked((long)Math.Round(clip.DurationSeconds * frameRate.FramesPerSecond, MidpointRounding.AwayFromZero) + 1));
        if (frames > ushort.MaxValue) throw new InvalidOperationException("The chosen frame rate exceeds the animation frame limit.");
        long tracks = checked((long)clip.TransformTracks.Length + clip.ScalarTracks.Length + clip.AuxiliaryTransformTracks.Length);
        if (checked(frames * tracks) > Anm2TemporalResampler.MaximumOutputTransformCount)
            throw new InvalidOperationException("The chosen frame rate exceeds the animation key limit.");
        double SourceFrame(int frame) => clip.ResolveFrame(frame == frames - 1 ? clip.DurationSeconds :
            Math.Min(clip.DurationSeconds, frameRate.SecondsForFrame(frame)), PlaybackMode.Clamp);
        return new AnimationClip(clip.Name, frameRate, frames,
            clip.TransformTracks.Select(track => new TransformTrack(track.BoneIndex,
                Enumerable.Range(0, checked((int)frames)).Select(frame => new TransformKeyframe(frame, track.Sample(SourceFrame(frame)))))),
            clip.ScalarTracks.Select(track => new ScalarTrack(track.ChannelName,
                Enumerable.Range(0, checked((int)frames)).Select(frame => new ScalarKeyframe(frame, track.Sample(SourceFrame(frame)))))),
            clip.AuxiliaryTransformTracks.Select(track => new AuxiliaryTransformTrack(track.Descriptor,
                Enumerable.Range(0, checked((int)frames)).Select(frame => new TransformKeyframe(frame, track.Sample(SourceFrame(frame)))))));
    }

    public static FbxModelAuthoringImportResult ApplySelections(FbxModelAuthoringImportResult model,
        ImmutableArray<CustomModelAnimationClip> selections)
    {
        ArgumentNullException.ThrowIfNull(model);
        var authored = model.Package.AuthoredAnimationPayloads;
        var derived = model.Package.DerivedAnimationPayloads;
        var rows = ImmutableArray.CreateBuilder<CustomModelAnimationClip>(selections.Length);
        var clips = model.AnimationClips.ToBuilder();
        foreach (CustomModelAnimationClip selection in selections)
        {
            CustomModelAnimationClip current = model.Package.Document.AnimationClips.Single(clip => clip.Id == selection.Id);
            CustomModelAnimationClip updated = selection with
            {
                AuthoredAnimation = current.AuthoredAnimation,
                DerivedMotion = current.DerivedMotion,
                SourceFingerprint = current.SourceFingerprint,
            };
            if (current.FrameRate != selection.FrameRate &&
                (current.AuthoredAnimation is not null || current.DerivedMotion is not null))
            {
                ImmutableArray<byte> payload = current.AuthoredAnimation is not null ? authored[current.Id] : derived[current.Id];
                string expectedHash = current.AuthoredAnimation?.PayloadSha256 ?? current.DerivedMotion!.PayloadSha256;
                if (!Convert.ToHexStringLower(SHA256.HashData(payload.AsSpan())).Equals(expectedHash, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("The animation payload does not match its manifest.");
                DerivedAnimationData data = DerivedAnimationDataCodec.Deserialize(payload.AsSpan());
                var source = new AnimationClip(data.Name, data.FrameRate, data.FrameCount,
                    data.TransformTracks.Select((track, index) => new TransformTrack(index, track.Keyframes)),
                    data.ScalarTracks.Select(static track => new ScalarTrack(track.ChannelName, track.Keyframes)),
                    data.AuxiliaryTracks.Select(static track => new AuxiliaryTransformTrack(track.Descriptor, track.Keyframes)));
                AnimationClip resampled = WithFrameRate(source, selection.FrameRate);
                data = data with
                {
                    FrameRate = resampled.FrameRate, FrameCount = resampled.FrameCount,
                    TransformTracks = resampled.TransformTracks.Select(track =>
                        new DerivedTransformTrack(data.TransformTracks[track.BoneIndex].BoneName, track.Keyframes)).ToImmutableArray(),
                    ScalarTracks = resampled.ScalarTracks.Select(static track => new DerivedScalarTrack(track.ChannelName, track.Keyframes)).ToImmutableArray(),
                    AuxiliaryTracks = resampled.AuxiliaryTransformTracks.Select(static track => new DerivedAuxiliaryTrack(track.Descriptor, track.Keyframes)).ToImmutableArray(),
                };
                payload = DerivedAnimationDataCodec.Serialize(data);
                string hash = Convert.ToHexStringLower(SHA256.HashData(payload.AsSpan()));
                updated = updated with { SourceFingerprint = hash, FrameCount = data.FrameCount };
                if (current.AuthoredAnimation is { } authoredReference)
                {
                    authored = authored.SetItem(current.Id, payload);
                    updated = updated with { AuthoredAnimation = authoredReference with { PayloadSha256 = hash, PayloadLength = payload.Length } };
                }
                else
                {
                    derived = derived.SetItem(current.Id, payload);
                    updated = updated with { DerivedMotion = current.DerivedMotion! with { PayloadSha256 = hash, PayloadLength = payload.Length } };
                }
            }
            if (clips.TryGetValue(selection.Id, out AnimationClip? clip))
            {
                clips[selection.Id] = WithFrameRate(clip, selection.FrameRate);
                updated = updated with { FrameCount = clips[selection.Id].FrameCount };
            }
            rows.Add(updated);
        }
        return model with { Package = model.Package with
        {
            Document = model.Package.Document with { AnimationClips = rows.ToImmutable() },
            AuthoredAnimationPayloads = authored,
            DerivedAnimationPayloads = derived,
        }, AnimationClips = clips.ToImmutable() };
    }

    internal static FbxModelAuthoringImportResult RestoreTiming(FbxModelAuthoringImportResult model)
    {
        var clips = model.AnimationClips;
        var selections = model.Package.Document.AnimationClips;
        foreach (CustomModelAnimationClip selection in model.Package.Document.AnimationClips)
            if (clips.TryGetValue(selection.Id, out AnimationClip? clip) && clip.FrameRate != selection.FrameRate)
            {
                clips = clips.SetItem(selection.Id, WithFrameRate(clip, selection.FrameRate));
                selections = selections.Replace(selection, selection with { FrameCount = clips[selection.Id].FrameCount });
            }
        return ReferenceEquals(clips, model.AnimationClips) ? model : model with
        {
            AnimationClips = clips,
            Package = model.Package with { Document = model.Package.Document with { AnimationClips = selections } },
        };
    }
}
