using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using ReAnimated.Core.ModelAuthoring;

namespace ReAnimated.Retargeting.Geometry;

/// <summary>Adopts draft body guides through the existing session transaction; never emits another rig.</summary>
public static class AnatomicalDetectionAdoption
{
    private static readonly ImmutableHashSet<string> BodyRoles = new[] {
        "body.pelvis", "body.spine.0", "body.spine.1", "body.spine.2", "body.neck.0", "body.head",
        "arm.left.upper", "arm.left.lower", "hand.left", "arm.right.upper", "arm.right.lower", "hand.right",
        "leg.left.upper", "leg.left.lower", "foot.left", "leg.right.upper", "leg.right.lower", "foot.right",
    }.ToImmutableHashSet(StringComparer.Ordinal);

    private static readonly (string Left, string Right)[] MirrorRolePairs =
    [
        ("arm.left.upper", "arm.right.upper"),
        ("arm.left.lower", "arm.right.lower"),
        ("hand.left", "hand.right"),
        ("leg.left.upper", "leg.right.upper"),
        ("leg.left.lower", "leg.right.lower"),
        ("foot.left", "foot.right"),
    ];

    public static bool OwnsRole(string role) => BodyRoles.Contains(role);

    public static bool TryApply(RiggingSession current, RiggingJobToken token, string expectedGridFingerprint,
        AnatomicalDetectionResult detection, out RiggingSession result)
    {
        ArgumentNullException.ThrowIfNull(current); ArgumentNullException.ThrowIfNull(token); ArgumentNullException.ThrowIfNull(detection);
        result = current;
        if (!current.Matches(token) || !current.MatchesSource(detection.SourceSha256) ||
            !string.Equals(expectedGridFingerprint, detection.GridFingerprint, StringComparison.Ordinal)) return false;
        if (current.Landmarks.Where(l => BodyRoles.Contains(l.RoleId)).GroupBy(static l => l.RoleId).Any(static g => g.Count() > 1)) return false;
        if (detection.Joints.IsDefault || detection.ConfigurationFingerprint is not { Length: 64 } ||
            detection.InputFingerprint is not { Length: 64 } || !Enum.IsDefined(detection.Status))
            throw new ArgumentException("Anatomical detection evidence is incomplete.", nameof(detection));
        var roles = new HashSet<string>(StringComparer.Ordinal);
        var newlyGenerated = new Dictionary<string, Guid>(StringComparer.Ordinal);
        var landmarks = current.Landmarks.Where(l => !BodyRoles.Contains(l.RoleId)).ToList();
        foreach (var proposal in detection.Joints.Where(p => BodyRoles.Contains(p.Role)))
        {
            if (!roles.Add(proposal.Role) || !proposal.Position.IsFinite || !double.IsFinite(proposal.EvidenceStrength) ||
                proposal.EvidenceStrength is < 0 or > 1 || !Enum.IsDefined(proposal.Method))
                throw new ArgumentException("Anatomical joint proposals are invalid or repeated.", nameof(detection));
            var existing = current.Landmarks.FirstOrDefault(l => l.RoleId == proposal.Role);
            Guid id = existing?.Id ?? new Guid(SHA256.HashData(Encoding.UTF8.GetBytes(current.Id.ToString("N") + ":anatomy:" + proposal.Role)).AsSpan(0, 16));
            if (existing is null)
            {
                newlyGenerated.Add(proposal.Role, id);
            }

            landmarks.Add(new RigLandmark {
                Id = id, RoleId = proposal.Role, Position = proposal.Position, Locked = proposal.Locked,
                UserApproved = false, MirrorPartnerId = existing?.MirrorPartnerId,
                Provenance = proposal.Method == AnatomicalPlacementMethod.UserGuide ? RigEvidenceKind.UserOverride : RigEvidenceKind.GeometryInference,
                Confidence = null, // Heuristic evidence strength is not calibrated confidence.
                Evidence = [new() { Id = "anatomical-volume-v1", Kind = RigEvidenceKind.GeometryInference,
                    ArtifactSha256 = detection.InputFingerprint, Description = $"Draft {proposal.Method} proposal from a sampled source volume; review required." }],
            });
        }

        foreach ((string leftRole, string rightRole) in MirrorRolePairs)
        {
            if (!newlyGenerated.TryGetValue(leftRole, out Guid leftId) ||
                !newlyGenerated.TryGetValue(rightRole, out Guid rightId))
            {
                continue;
            }

            landmarks = landmarks.Select(landmark => landmark.Id == leftId
                ? landmark with { MirrorPartnerId = rightId }
                : landmark.Id == rightId
                    ? landmark with { MirrorPartnerId = leftId }
                    : landmark).ToList();
        }

        // An unresolved pass must not erase earlier body guides that it did not propose to replace.
        landmarks.AddRange(current.Landmarks.Where(l => BodyRoles.Contains(l.RoleId) && !roles.Contains(l.RoleId)));
        if (!RiggingSessions.TryAcceptLandmarks(current, token, landmarks.ToImmutableArray(), out var adopted)) return false;
        result = adopted with { DetectionBackend = new RiggingBackendReference { Id = "cpu-volume-biped", Version = "1", SettingsSha256 = detection.ConfigurationFingerprint } };
        result.Validate();
        return true;
    }
}
