using System.Collections.Immutable;
using ReAnimated.Core.Mathematics;

namespace ReAnimated.Core.ModelAuthoring;

/// <summary>A source-session-bound, transient position edit. Preview never changes authored state.</summary>
public sealed class RigLandmarkMove
{
    internal RigLandmarkMove(RiggingJobToken token, RigLandmark selected, RigLandmark? partner, Vector3D planeOrigin, Vector3D planeNormal)
    { Token = token; Selected = selected; Partner = partner; PlaneOrigin = planeOrigin; PlaneNormal = planeNormal; }
    public RiggingJobToken Token { get; }
    public RigLandmark Selected { get; }
    public RigLandmark? Partner { get; }
    public Vector3D PlaneOrigin { get; }
    public Vector3D PlaneNormal { get; }

    public ImmutableDictionary<Guid, Vector3D> Preview(Vector3D delta)
    {
        if (!delta.IsFinite || !(Selected.Position + delta).IsFinite) throw new ArgumentException("Guide movement must remain finite.", nameof(delta));
        Vector3D position = Selected.Position + delta;
        var result = ImmutableDictionary<Guid, Vector3D>.Empty.Add(Selected.Id, position);
        if (Partner is { } partner)
        {
            if (delta == Vector3D.Zero) return result.Add(partner.Id, partner.Position);
            Vector3D reflected = position - PlaneNormal * (2 * Vector3D.Dot(position - PlaneOrigin, PlaneNormal));
            if (!reflected.IsFinite) throw new ArgumentException("Mirrored guide movement exceeds the finite range.", nameof(delta));
            result = result.Add(partner.Id, reflected);
        }
        return result;
    }
}

public static class RigLandmarkEditing
{
    public static bool TryBeginMove(RiggingSession session, Guid guideId, bool mirror, out RigLandmarkMove? move)
    {
        ArgumentNullException.ThrowIfNull(session); session.Validate(); move = null;
        var selected = session.Landmarks.FirstOrDefault(l => l.Id == guideId);
        if (selected is null || selected.Locked || session.RequiresSourceReview) return false;
        RigLandmark? partner = null;
        if (mirror)
        {
            if (selected.MirrorPartnerId is not { } partnerId) return false;
            partner = session.Landmarks.FirstOrDefault(l => l.Id == partnerId);
            if (partner is null || partner.Locked || partner.MirrorPartnerId != selected.Id) return false;
        }
        var inputNormal = session.SymmetryNormal;
        double magnitude = Math.Max(Math.Abs(inputNormal.X), Math.Max(Math.Abs(inputNormal.Y), Math.Abs(inputNormal.Z)));
        var normal = (inputNormal / magnitude).Normalized();
        move = new(session.CreateJobToken(), selected, partner, session.SymmetryOrigin, normal);
        return true;
    }

    public static bool TryCommitMove(RiggingSession current, RigLandmarkMove move, Vector3D delta, out RiggingSession result)
    {
        ArgumentNullException.ThrowIfNull(current); ArgumentNullException.ThrowIfNull(move); result = current;
        if (!current.Matches(move.Token)) return false;
        var positions = move.Preview(delta);
        if (positions.All(pair => current.Landmarks.Single(l => l.Id == pair.Key).Position == pair.Value)) return true;
        var landmarks = current.Landmarks.Select(l => positions.TryGetValue(l.Id, out var position) ? l with {
            Position = position, UserApproved = false, Confidence = null, Provenance = RigEvidenceKind.UserOverride,
            Evidence = [],
        } : l).ToImmutableArray();
        result = RiggingSessions.Change(current, current with { Landmarks = landmarks }, RiggingEditKind.Anatomy);
        return true;
    }

    /// <summary>Explicit user pin/unpin action; it does not move or approve the guide.</summary>
    public static RiggingSession SetLocked(RiggingSession current, Guid guideId, bool locked)
    {
        ArgumentNullException.ThrowIfNull(current); current.Validate();
        if (!current.Landmarks.Any(l => l.Id == guideId)) throw new ArgumentException("The guide does not exist in this session.", nameof(guideId));
        if (current.Landmarks.Single(l => l.Id == guideId).Locked == locked) return current;
        return RiggingSessions.Change(current, current with { Landmarks = current.Landmarks.Select(l => l.Id == guideId ? l with { Locked = locked } : l).ToImmutableArray() },
            RiggingEditKind.Anatomy, allowLockedChanges: true);
    }
}
