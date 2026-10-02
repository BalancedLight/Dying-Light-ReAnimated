using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using ReAnimated.Codecs.Fbx;
using ReAnimated.Core.Domain;
using ReAnimated.Core.Mathematics;
using ReAnimated.Core.ModelAuthoring;

namespace ReAnimated.Codecs.Models;

/// <summary>A source clip track in effective-rig index space, copied from an embedded decoded clip.</summary>
public sealed record TerminalHelperClipTrackObservation(
    Guid ClipId,
    string ClipName,
    long FrameCount,
    int BoneIndex,
    ImmutableArray<TransformTRS> Keys)
{
    public bool IncludedForExport { get; init; } = true;
}

/// <summary>Caller-supplied fit and skin evidence for one source helper bone.</summary>
public sealed record TerminalHelperFitObservation(
    Guid EntityId,
    int BoneIndex,
    TransformTRS FittedLocalBind,
    bool IsUnmatchedToTarget,
    int RenderWeightCount,
    RigAnimationLod RetainedLodCandidate,
    ImmutableArray<TerminalHelperClipTrackObservation> Tracks)
{
    public int FittedRigBoneIndex { get; init; } = -1;
    public int EffectiveChildNodeCount { get; init; } = -1;
    public string EffectiveRigFingerprint { get; init; } = string.Empty;
}

public enum TerminalHelperPolicyRowStatus { Proposed, ExistingDecision, Ineligible }

public sealed record TerminalHelperChannelPolicyRow(
    Guid EntityId,
    string Name,
    TerminalHelperPolicyRowStatus Status,
    RigAnimationComponents? CurrentMask,
    RigAnimationLod? CurrentLod,
    RigAnimationComponents? ProposedMask,
    RigAnimationLod? ProposedLod,
    string Evidence);

/// <summary>Read-only, source/session/rig-fingerprinted terminal-helper policy review.</summary>
public sealed record TerminalHelperChannelPolicyProposal(
    RiggingJobToken Token,
    string SourceSha256,
    string RigFingerprint,
    ImmutableArray<TerminalHelperChannelPolicyRow> Rows,
    ImmutableArray<RigComponentPolicyEdit> Edits);

/// <summary>
/// Proposes bind-inherited channels for unmatched terminal helpers whose embedded tracks are constant.
/// Constant tracks are evidence only; their values are compared with fitted bind and are never presumed discardable.
/// </summary>
public static class TerminalHelperChannelPolicyAuthoring
{
    private const double ConstantTolerance = 1e-7;
    private const double DifferenceTolerance = 1e-7;

    public static ImmutableArray<TerminalHelperFitObservation> Observe(
        FbxModelAuthoringImportResult model,
        IReadOnlySet<Guid> unmatchedEntityIds,
        RigAnimationLod retainedLodCandidate)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(unmatchedEntityIds);
        if (!Enum.IsDefined(retainedLodCandidate))
            throw new ArgumentOutOfRangeException(nameof(retainedLodCandidate), "Choose a defined animation LOD policy.");

        CustomModelDocument document = model.Package.Document;
        RiggingSession session = document.RiggingSession ?? throw new InvalidOperationException("A rigging session is required.");
        RigDefinition rig = model.Rig ?? throw new InvalidOperationException("A fitted/imported rig is required.");
        for (int i = 0; i < document.Bones.Length; i++)
        {
            int rigIndex = rig.GetBoneIndex(document.Bones[i].Name);
            if (rigIndex < 0)
                throw new InvalidDataException("The current effective rig does not uniquely contain source node '" + document.Bones[i].Name + "'.");
        }
        int[] weights = new int[rig.BoneCount];
        foreach (FbxModelSurface surface in model.Surfaces)
        foreach (FbxModelVertex vertex in surface.Vertices)
        {
            int count = Math.Min(vertex.BoneIndices.Length, vertex.BoneWeights.Length);
            for (int i = 0; i < count; i++)
            {
                int paletteIndex = vertex.BoneIndices[i];
                if ((uint)paletteIndex >= (uint)surface.PaletteBoneIndices.Length)
                    throw new InvalidDataException("A surface contains an invalid bone palette index.");
                int boneIndex = surface.PaletteBoneIndices[paletteIndex];
                if ((uint)boneIndex >= (uint)weights.Length)
                    throw new InvalidDataException("A surface palette refers to a bone outside the source rig.");
                if (vertex.BoneWeights[i] > 0) weights[boneIndex]++;
            }
        }

        RigEntityBinding[] entities = session.Recipe.Entities.Where(e => e.OwnerAssetId == document.ModelId).ToArray();
        var effectiveChildCounts = new int[rig.BoneCount];
        foreach (BoneDefinition bone in rig.Bones)
            if (bone.ParentIndex >= 0) effectiveChildCounts[bone.ParentIndex]++;
        string effectiveRigFingerprint = ComputeRigFingerprint(rig);
        var metadata = document.AnimationClips.Where(static clip => clip.DerivedMotion is null)
            .ToDictionary(static clip => clip.Id);
        if (metadata.Keys.Any(id => !model.AnimationClips.ContainsKey(id)))
            throw new InvalidDataException("Every original embedded clip must be decoded before helper tracks can be reviewed, including takes excluded from export.");

        var result = ImmutableArray.CreateBuilder<TerminalHelperFitObservation>();
        for (int index = 0; index < document.Bones.Length; index++)
        {
            CustomModelBone bone = document.Bones[index];
            RigEntityBinding[] identityMatches = bone.FbxObjectId == 0 ? [] : entities.Where(entity =>
                string.Equals(entity.SourceEntityId, SourceIdentity(bone), StringComparison.Ordinal) &&
                string.Equals(entity.NativeName, bone.Name, StringComparison.Ordinal)).ToArray();
            RigEntityBinding? sourceEntity = identityMatches.Length == 1 ? identityMatches[0] : null;
            if (bone.Kind != BoneKind.Helper || sourceEntity is null)
                continue;
            int rigIndex = rig.GetBoneIndex(bone.Name);
            if (rigIndex < 0)
                throw new InvalidDataException("The current effective rig does not uniquely contain source helper '" + bone.Name + "'.");
            var tracks = ImmutableArray.CreateBuilder<TerminalHelperClipTrackObservation>();
            foreach ((Guid clipId, AnimationClip clip) in model.AnimationClips.OrderBy(static pair => pair.Key))
            {
                if (!metadata.ContainsKey(clipId)) continue;
                TransformTrack? track = clip.TransformTracks.FirstOrDefault(row => row.BoneIndex == rigIndex);
                tracks.Add(new(clipId, clip.Name, clip.FrameCount, rigIndex,
                    track?.Keyframes.Select(static key => key.Value).ToImmutableArray() ?? [])
                { IncludedForExport = metadata[clipId].Included });
            }
            result.Add(new(sourceEntity.EntityId, index, rig.Bones[rigIndex].LocalBindPose,
                unmatchedEntityIds.Contains(sourceEntity.EntityId), weights[rigIndex], retainedLodCandidate, tracks.ToImmutable())
            {
                FittedRigBoneIndex = rigIndex,
                EffectiveChildNodeCount = effectiveChildCounts[rigIndex],
                EffectiveRigFingerprint = effectiveRigFingerprint,
            });
        }
        return result.ToImmutable();
    }

    public static TerminalHelperChannelPolicyProposal Propose(
        CustomModelDocument document,
        IReadOnlyList<TerminalHelperFitObservation> observations)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(observations);
        document.Validate();
        RiggingSession session = document.RiggingSession ?? throw new InvalidOperationException("A rigging session is required.");
        if (!session.MatchesSource(document.Source.ContentSha256))
            throw new InvalidDataException("The rigging session does not match the source model.");

        var byId = observations.ToDictionary(static row => row.EntityId);
        var boneIndexByEntity = session.Recipe.Entities.Where(row => row.OwnerAssetId == document.ModelId)
            .ToDictionary(static row => row.EntityId, row => FindSourceBone(document, row));
        var children = new int[document.CreateEffectiveBones().Length];
        foreach (CustomModelBone bone in document.CreateEffectiveBones())
            if (bone.ParentIndex >= 0) children[bone.ParentIndex]++;
        var existing = session.Recipe.ComponentPolicies.ToDictionary(static row => row.EntityId);
        var rows = ImmutableArray.CreateBuilder<TerminalHelperChannelPolicyRow>();
        var edits = ImmutableArray.CreateBuilder<RigComponentPolicyEdit>();

        foreach (RigEntityBinding entity in session.Recipe.Entities.Where(row => row.OwnerAssetId == document.ModelId))
        {
            if (!boneIndexByEntity.TryGetValue(entity.EntityId, out int boneIndex) || boneIndex < 0 ||
                document.Bones[boneIndex].Kind != BoneKind.Helper)
                continue;
            existing.TryGetValue(entity.EntityId, out AnimationComponentPolicy? current);
            if (!byId.TryGetValue(entity.EntityId, out TerminalHelperFitObservation? observation))
            {
                rows.Add(new(entity.EntityId, entity.NativeName, TerminalHelperPolicyRowStatus.Ineligible,
                    current?.EmittedMask, current?.AnimationLod, null, null, "No current fit, weight, and embedded-track observation was supplied."));
                continue;
            }

            string evidence = Explain(document.Bones[boneIndex], boneIndex, children, observation);
            bool eligible = IsEligible(document.Bones[boneIndex], boneIndex, children, observation, out string reason);
            if (!eligible)
            {
                rows.Add(new(entity.EntityId, entity.NativeName, TerminalHelperPolicyRowStatus.Ineligible,
                    current?.EmittedMask, current?.AnimationLod, null, null, reason + " " + evidence));
                continue;
            }
            if (current is not null)
            {
                rows.Add(new(entity.EntityId, entity.NativeName, TerminalHelperPolicyRowStatus.ExistingDecision,
                    current.EmittedMask, current.AnimationLod, null, null,
                    "A saved component-policy row exists and is preserved whole-node. " + evidence));
                continue;
            }

            edits.Add(new(entity.EntityId, RigAnimationComponents.None, observation.RetainedLodCandidate,
                RigComponentOwner.BindInherited, RigComponentOwner.BindInherited, RigComponentOwner.BindInherited));
            rows.Add(new(entity.EntityId, entity.NativeName, TerminalHelperPolicyRowStatus.Proposed,
                null, null, RigAnimationComponents.None, observation.RetainedLodCandidate,
                "Candidate only: suggest bind-inherited POS/ROT/SCL and animation LOD " + observation.RetainedLodCandidate + ". " + evidence));
        }

        return new(session.CreateJobToken(), document.Source.ContentSha256,
            ComputeFingerprint(document, observations), rows.ToImmutable(), edits.ToImmutable());
    }

    public static bool TryApply(
        CustomModelDocument document,
        IReadOnlyList<TerminalHelperFitObservation> currentObservations,
        TerminalHelperChannelPolicyProposal proposal,
        bool reviewed,
        out RiggingSession result)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(currentObservations);
        ArgumentNullException.ThrowIfNull(proposal);
        result = document.RiggingSession ?? throw new InvalidOperationException("A rigging session is required.");
        if (!reviewed) throw new InvalidOperationException("The proposal requires explicit reviewer acknowledgment.");
        if (!result.Matches(proposal.Token)) return false;
        if (!string.Equals(document.Source.ContentSha256, proposal.SourceSha256, StringComparison.OrdinalIgnoreCase)) return false;
        TerminalHelperChannelPolicyProposal fresh = Propose(document, currentObservations);
        if (!string.Equals(fresh.RigFingerprint, proposal.RigFingerprint, StringComparison.Ordinal) ||
            !fresh.Rows.SequenceEqual(proposal.Rows) || !fresh.Edits.SequenceEqual(proposal.Edits))
            return false;
        if (proposal.Edits.IsEmpty) return false;
        return RigComponentPolicyAuthoring.TryApply(document, proposal.Token, proposal.Edits, out result);
    }

    private static bool IsEligible(CustomModelBone bone, int boneIndex, int[] childCounts,
        TerminalHelperFitObservation row, out string reason)
    {
        reason = string.Empty;
        if (row.BoneIndex != boneIndex || row.FittedRigBoneIndex < 0) { reason = "Observation uses a different source or effective-rig bone index."; return false; }
        if (!row.IsUnmatchedToTarget) { reason = "Target match is not explicitly marked unmatched."; return false; }
        if (row.RenderWeightCount != 0) { reason = "The helper has nonzero render-weight assignments."; return false; }
        if (childCounts[boneIndex] != 0 || row.EffectiveChildNodeCount != 0) { reason = "The helper is not terminal in the effective hierarchy."; return false; }
        if (!IsUsableTransform(row.FittedLocalBind)) { reason = "Fitted bind transform is invalid."; return false; }
        if (!Enum.IsDefined(row.RetainedLodCandidate))
        { reason = "A defined animation LOD policy is required."; return false; }
        if (row.Tracks.IsDefaultOrEmpty) { reason = "No embedded clip track was observed."; return false; }
        if (row.Tracks.Any(track => track.BoneIndex != row.FittedRigBoneIndex || track.ClipId == Guid.Empty ||
            string.IsNullOrWhiteSpace(track.ClipName) || track.FrameCount <= 0 || track.Keys.IsDefaultOrEmpty ||
            track.Keys.Any(key => !IsUsableTransform(key)) || !IsConstant(track.Keys)))
        { reason = "At least one embedded track is absent, malformed, or nonconstant over time."; return false; }
        if (row.Tracks.Select(static track => track.ClipId).Distinct().Count() != row.Tracks.Length)
        { reason = "Clip track evidence contains duplicate clips."; return false; }
        _ = bone;
        return true;
    }

    private static bool IsConstant(ImmutableArray<TransformTRS> keys) =>
        keys.All(value => Near(keys[0], value, ConstantTolerance));

    private static string Explain(CustomModelBone bone, int boneIndex, int[] childCounts,
        TerminalHelperFitObservation row)
    {
        TransformTRS bind = row.FittedLocalBind;
        var deltas = new List<string>();
        foreach (TerminalHelperClipTrackObservation track in row.Tracks.Where(t => t.BoneIndex == row.FittedRigBoneIndex && !t.Keys.IsDefaultOrEmpty))
        {
            TransformTRS value = track.Keys[0];
            if (!IsUsableTransform(bind) || !IsUsableTransform(value)) continue;
            if (!Near(value, bind, DifferenceTolerance))
                deltas.Add(track.ClipName + ": constant value differs from fitted bind (POS=" +
                    Distance(value.Translation, bind.Translation).ToString("G6", System.Globalization.CultureInfo.InvariantCulture) +
                    ", ROT=" + RotationDifference(value.Rotation, bind.Rotation).ToString("G6", System.Globalization.CultureInfo.InvariantCulture) +
                    " rad, SCL=" + Distance(value.Scale, bind.Scale).ToString("G6", System.Globalization.CultureInfo.InvariantCulture) + ")");
        }
        string diff = row.Tracks.IsDefaultOrEmpty
            ? "No embedded tracks were observed; no track-to-bind comparison is available."
            : deltas.Count == 0
            ? "All observed constant track values match fitted bind within tolerance; constancy does not establish safe discard."
            : "Nonzero constant-track versus fitted-bind differences are evidence: " + string.Join("; ", deltas) + ". Constancy does not establish safe discard.";
        return "Unmatched=" + row.IsUnmatchedToTarget + "; renderWeightAssignments=" + row.RenderWeightCount +
            "; childNodes=" + childCounts[boneIndex] + "; constantTracks=" + row.Tracks.Length +
            "; takes excluded from export=" + row.Tracks.Count(static track => !track.IncludedForExport) + ". " + diff;
    }

    private static bool Near(TransformTRS left, TransformTRS right, double tolerance) =>
        Distance(left.Translation, right.Translation) <= tolerance &&
        Distance(left.Scale, right.Scale) <= tolerance && RotationDifference(left.Rotation, right.Rotation) <= tolerance;

    private static bool IsUsableTransform(TransformTRS value) =>
        value.IsFinite && value.Rotation.LengthSquared > 1e-12 &&
        Math.Abs(value.Scale.X) > 1e-12 && Math.Abs(value.Scale.Y) > 1e-12 && Math.Abs(value.Scale.Z) > 1e-12;

    private static double Distance(Vector3D left, Vector3D right)
    {
        double x = left.X - right.X, y = left.Y - right.Y, z = left.Z - right.Z;
        return Math.Sqrt((x * x) + (y * y) + (z * z));
    }

    private static double RotationDifference(QuaternionD left, QuaternionD right)
    {
        double dot = Math.Abs(QuaternionD.Dot(left.Normalized(), right.Normalized()));
        return 2 * Math.Acos(Math.Clamp(dot, -1, 1));
    }

    private static int FindSourceBone(CustomModelDocument document, RigEntityBinding entity)
    {
        for (int index = 0; index < document.Bones.Length; index++)
            if (document.Bones[index].FbxObjectId != 0 && entity.SourceEntityId is not null &&
                string.Equals(SourceIdentity(document.Bones[index]), entity.SourceEntityId, StringComparison.Ordinal)) return index;
        int[] named = document.Bones.Select((bone, index) => (bone, index))
            .Where(row => string.Equals(row.bone.Name, entity.NativeName, StringComparison.Ordinal))
            .Select(static row => row.index).ToArray();
        if (named.Length == 1) return named[0];
        return -1;
    }

    private static string SourceIdentity(CustomModelBone bone) =>
        "fbx:" + bone.FbxObjectId.ToString(System.Globalization.CultureInfo.InvariantCulture);

    private static string ComputeFingerprint(CustomModelDocument document,
        IReadOnlyList<TerminalHelperFitObservation> observations)
    {
        var text = new StringBuilder();
        text.Append(document.Source.ContentSha256).Append('|').Append(document.RigSignature).Append('|');
        RiggingSession session = document.RiggingSession!;
        text.Append(session.Id.ToString("N")).Append('|').Append(session.Generation.ToString("N")).Append('|')
            .Append(session.Revision).Append('|').Append(session.ComputeInputFingerprint());
        foreach (TerminalHelperFitObservation row in observations.OrderBy(static item => item.EntityId))
        {
            text.Append('|').Append(row.EntityId.ToString("N")).Append(':').Append(row.BoneIndex).Append(':')
                .Append(row.IsUnmatchedToTarget).Append(':').Append(row.RenderWeightCount).Append(':')
                .Append((int)row.RetainedLodCandidate).Append(':').Append(row.FittedRigBoneIndex).Append(':')
                .Append(row.EffectiveChildNodeCount).Append(':').Append(row.EffectiveRigFingerprint).Append(':')
                .Append(TransformText(row.FittedLocalBind));
            foreach (TerminalHelperClipTrackObservation track in row.Tracks.OrderBy(static item => item.ClipId))
            {
                text.Append(':').Append(track.ClipId.ToString("N")).Append(':').Append(track.ClipName).Append(':')
                    .Append(track.FrameCount).Append(':').Append(track.BoneIndex).Append(':').Append(track.IncludedForExport);
                foreach (TransformTRS key in track.Keys) text.Append(':').Append(TransformText(key));
            }
        }
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString()))).ToLowerInvariant();
    }

    private static string TransformText(TransformTRS value) =>
        string.Join(",", value.Translation.X.ToString("R", System.Globalization.CultureInfo.InvariantCulture),
            value.Translation.Y.ToString("R", System.Globalization.CultureInfo.InvariantCulture),
            value.Translation.Z.ToString("R", System.Globalization.CultureInfo.InvariantCulture),
            value.Rotation.X.ToString("R", System.Globalization.CultureInfo.InvariantCulture),
            value.Rotation.Y.ToString("R", System.Globalization.CultureInfo.InvariantCulture),
            value.Rotation.Z.ToString("R", System.Globalization.CultureInfo.InvariantCulture),
            value.Rotation.W.ToString("R", System.Globalization.CultureInfo.InvariantCulture),
            value.Scale.X.ToString("R", System.Globalization.CultureInfo.InvariantCulture),
            value.Scale.Y.ToString("R", System.Globalization.CultureInfo.InvariantCulture),
            value.Scale.Z.ToString("R", System.Globalization.CultureInfo.InvariantCulture));

    private static string ComputeRigFingerprint(RigDefinition rig)
    {
        var text = new StringBuilder();
        foreach (BoneDefinition bone in rig.Bones)
            text.Append(bone.Index).Append(':').Append(bone.Name).Append(':').Append(bone.ParentIndex).Append(':')
                .Append((int)bone.Kind).Append(':').Append(TransformText(bone.LocalBindPose)).Append('|');
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString()))).ToLowerInvariant();
    }
}
