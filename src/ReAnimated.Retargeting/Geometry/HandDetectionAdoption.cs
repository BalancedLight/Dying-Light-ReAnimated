using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using ReAnimated.Core.Mathematics;
using ReAnimated.Core.ModelAuthoring;

namespace ReAnimated.Retargeting.Geometry;

public sealed record HandDigitSelection(string DigitId, RigFingerPresence Presence, string? BranchId);

/// <summary>Adopts reviewed branch-to-digit choices as editable guides; no rig or skin mutation.</summary>
public static class HandDetectionAdoption
{
    public static RiggingSession Adopt(RiggingSession current, RiggingJobToken token, LocalHandDetectionResult detection,
        Vector3D wrist, IReadOnlyList<HandDigitSelection> selections)
    {
        ArgumentNullException.ThrowIfNull(current); ArgumentNullException.ThrowIfNull(token);
        ArgumentNullException.ThrowIfNull(detection); ArgumentNullException.ThrowIfNull(selections);
        current.Validate();
        if (!current.Matches(token) || !current.MatchesSource(detection.SourceSha256))
            throw new InvalidOperationException("The hand source or authoring inputs changed. Detect and review the current model again.");
        if (detection.Side is not ("left" or "right") || detection.PalmFrame is not { } palm || !wrist.IsFinite ||
            selections.Count is < 1 or > 16 || selections.Any(s => s is null || string.IsNullOrWhiteSpace(s.DigitId)) ||
            selections.Select(static s => s.DigitId).Distinct(StringComparer.Ordinal).Count() != selections.Count)
            throw new ArgumentException("A hand draft needs a palm frame, wrist and distinct digit declarations.", nameof(selections));
        var side = detection.Side == "left" ? RigHandSide.Left : RigHandSide.Right;
        var oldHand = current.Hands.FirstOrDefault(h => h.Side == side);
        var guides = current.Landmarks.ToList();
        if (guides.GroupBy(static g => g.RoleId, StringComparer.Ordinal).Any(static g => g.Count() > 1))
            throw new InvalidOperationException("Hand adoption requires unambiguous guide roles.");
        var evidence = ImmutableArray.Create(new RigEvidenceReference { Id = "hand-detection:" + detection.InputFingerprint,
            Kind = RigEvidenceKind.GeometryInference, ArtifactSha256 = detection.GridFingerprint,
            Description = "Local hand volume and branch proposals; geometry scores are uncalibrated and native behavior is unverified." });
        Guid wristId = Upsert("hand." + detection.Side, wrist, AnatomicalPlacementMethod.UserGuide, evidence);
        var fingers = ImmutableArray.CreateBuilder<RigFingerDeclaration>();
        var usedBranches = new HashSet<string>(StringComparer.Ordinal);
        foreach (var selection in selections)
        {
            if (!Enum.IsDefined(selection.Presence)) throw new ArgumentException("Unknown finger presence declaration.", nameof(selections));
            HandFingerProposal? branch = null;
            if (selection.BranchId is { } id)
            {
                branch = detection.Fingers.SingleOrDefault(f => f.BranchId == id) ?? throw new ArgumentException("A selected hand branch is missing.", nameof(selections));
                if (!usedBranches.Add(id)) throw new ArgumentException("One geometric branch cannot be assigned to two digits.", nameof(selections));
            }
            if (selection.Presence is RigFingerPresence.Absent or RigFingerPresence.Fused && branch is not null ||
                selection.Presence == RigFingerPresence.Present && branch is null)
                throw new ArgumentException("Present digits need a branch; absent and fused declarations do not create guides.", nameof(selections));
            var jointIds = ImmutableArray.CreateBuilder<Guid>();
            if (branch is not null)
            {
                for (int j = 0; j < branch.Joints.Length; j++)
                {
                    var proposal = branch.Joints[j];
                    string role = $"finger.{detection.Side}.{selection.DigitId}.{j + 1}";
                    var preserved = detection.PreservedGuides.FirstOrDefault(g => g.Role == role && g.Locked);
                    jointIds.Add(Upsert(role, preserved?.Position ?? proposal.Position, preserved is null ? proposal.Method : AnatomicalPlacementMethod.UserGuide, evidence));
                }
            }
            var oldFinger = oldHand?.Fingers.FirstOrDefault(f => f.Id == selection.DigitId);
            fingers.Add(new() { Id = selection.DigitId, Presence = selection.Presence, JointGuideIds = jointIds.ToImmutable(),
                CurlPlaneNormal = branch?.CurlPlaneNormal ?? oldFinger?.CurlPlaneNormal, RollDegrees = oldFinger?.RollDegrees ?? 0,
                UserApproved = false, Evidence = evidence });
        }
        // Existing guides, including guides for deliberately absent digits,
        // are retained as authored data; the declaration owns only its links.
        var hand = new RigHandSetup { Side = side, WristGuideId = wristId, PalmFrame = palm, Fingers = fingers.ToImmutable(), Evidence = evidence };
        var hands = current.Hands.Where(h => h.Side != side).Append(hand).OrderBy(static h => h.Side).ToImmutableArray();
        var replacement = current with { Landmarks = guides.ToImmutableArray(), Hands = hands };
        replacement.Validate();
        return RiggingSessions.Change(current, replacement, RiggingEditKind.Detection);

        Guid Upsert(string role, Vector3D position, AnatomicalPlacementMethod method, ImmutableArray<RigEvidenceReference> references)
        {
            int index = guides.FindIndex(g => g.RoleId == role);
            if (index >= 0 && guides[index].Locked) return guides[index].Id;
            var pinned = detection.PreservedGuides.FirstOrDefault(g => g.Role == role && g.Locked);
            if (pinned is not null) { position = pinned.Position; method = AnatomicalPlacementMethod.UserGuide; }
            Guid id = index >= 0 ? guides[index].Id : new Guid(SHA256.HashData(Encoding.UTF8.GetBytes($"hand-guide-v1|{current.Id:N}|{role}")).AsSpan(0, 16));
            var guide = new RigLandmark { Id = id, RoleId = role, Position = position, UserApproved = false, Locked = pinned is not null,
                Provenance = method == AnatomicalPlacementMethod.UserGuide ? RigEvidenceKind.UserOverride : RigEvidenceKind.GeometryInference,
                Evidence = references, MirrorPartnerId = index >= 0 ? guides[index].MirrorPartnerId : null };
            if (index >= 0) guides[index] = guide; else guides.Add(guide);
            return id;
        }
    }
}
