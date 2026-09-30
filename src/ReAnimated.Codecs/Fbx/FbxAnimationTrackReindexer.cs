using System.Collections.Immutable;
using ReAnimated.Core.Domain;
using ReAnimated.Core.ModelAuthoring;

namespace ReAnimated.Codecs.Fbx;

/// <summary>Moves decoded track indices with their bone identities; keyframes are never resampled or reinterpreted here.</summary>
internal static class FbxAnimationTrackReindexer
{
    public static ImmutableDictionary<Guid,AnimationClip> Reindex(ImmutableDictionary<Guid,AnimationClip> clips,
        IReadOnlyList<int> oldToNew, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(clips);ArgumentNullException.ThrowIfNull(oldToNew);
        if(oldToNew.Any(i=>i<0)||oldToNew.Distinct().Count()!=oldToNew.Count)
            throw new InvalidDataException("Animation track remapping requires a one-to-one bone identity map.");
        if(clips.IsEmpty)return clips;
        var result=clips.ToBuilder();bool changed=false;
        foreach(var (id,clip) in clips)
        {
            cancellationToken.ThrowIfCancellationRequested();bool clipChanged=false;
            var tracks=ImmutableArray.CreateBuilder<TransformTrack>(clip.TransformTracks.Length);
            foreach(var track in clip.TransformTracks)
            {
                if((uint)track.BoneIndex>=(uint)oldToNew.Count)
                    throw new InvalidDataException("An animation track does not identify a bone in its source hierarchy.");
                int target=oldToNew[track.BoneIndex];clipChanged|=target!=track.BoneIndex;
                tracks.Add(target==track.BoneIndex?track:new TransformTrack(target,track.Keyframes));
            }
            if(!clipChanged)continue;
            result[id]=new AnimationClip(clip.Name,clip.FrameRate,clip.FrameCount,tracks.MoveToImmutable(),clip.ScalarTracks,clip.AuxiliaryTransformTracks);
            changed=true;
        }
        return changed?result.ToImmutable():clips;
    }

    public static ImmutableDictionary<Guid, AnimationClip> ReindexAvailable(ImmutableDictionary<Guid, AnimationClip> clips,
        IReadOnlyList<int> oldToNew, CancellationToken token = default)
    {
        if (oldToNew.Any(i => i < -1) || oldToNew.Where(i => i >= 0).Distinct().Count() != oldToNew.Count(i => i >= 0))
            throw new InvalidDataException("Conformance animation ownership needs an unambiguous direct map.");
        var result = clips.ToBuilder(); bool changed = false;
        foreach (var (id, clip) in clips)
        {
            token.ThrowIfCancellationRequested();
            if (clip.TransformTracks.Any(t => (uint)t.BoneIndex >= (uint)oldToNew.Count)) throw new InvalidDataException("An animation track is outside its source rig.");
            if (clip.TransformTracks.Any(t => oldToNew[t.BoneIndex] < 0)) { result.Remove(id); changed = true; continue; }
            if (clip.TransformTracks.All(t => oldToNew[t.BoneIndex] == t.BoneIndex)) continue;
            changed = true;
            result[id] = new AnimationClip(clip.Name, clip.FrameRate, clip.FrameCount,
                clip.TransformTracks.Select(t => new TransformTrack(oldToNew[t.BoneIndex], t.Keyframes)), clip.ScalarTracks, clip.AuxiliaryTransformTracks);
        }
        return changed ? result.ToImmutable() : clips;
    }

    public static ImmutableDictionary<Guid, AnimationClip> ReindexByIdentity(ImmutableDictionary<Guid, AnimationClip> clips,
        IReadOnlyList<CustomModelBone> source, IReadOnlyList<CustomModelBone> target, CancellationToken token = default)
    {
        var byId = target.Where(b => b.FbxObjectId != 0).ToDictionary(b => b.FbxObjectId, b => b.Index);
        var byName = target.ToDictionary(b => b.Name, b => b.Index, StringComparer.Ordinal);
        return ReindexAvailable(clips, source.Select(b => b.FbxObjectId != 0 && byId.TryGetValue(b.FbxObjectId, out int index)
            ? index : b.FbxObjectId != 0 && byId.Count > 0 ? -1 : byName.GetValueOrDefault(b.Name, -1)).ToArray(), token);
    }

    public static ImmutableDictionary<Guid,AnimationClip> ReindexByName(ImmutableDictionary<Guid,AnimationClip> clips,
        IReadOnlyList<CustomModelBone> source,IReadOnlyList<CustomModelBone> target,CancellationToken cancellationToken=default)
    {
        if(clips.IsEmpty)return clips;
        var targets=target.ToDictionary(b=>b.Name,b=>b.Index,StringComparer.Ordinal);
        var mapping=source.Select(b=>targets.TryGetValue(b.Name,out int index)?index:
            throw new InvalidDataException($"The animated source bone '{b.Name}' is missing from the edited hierarchy.")).ToArray();
        return Reindex(clips,mapping,cancellationToken);
    }
}
