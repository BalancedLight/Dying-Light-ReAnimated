using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using ReAnimated.Core.Domain;
using ReAnimated.Core.Mathematics;

namespace ReAnimated.Core.ModelAuthoring;

/// <summary>Appends reviewed local finger chains to an owned generated body.</summary>
public static class GeneratedHandRig
{
    private const string GeneratedPrefix = "generated-body:";

    public static CustomModelDocument Append(CustomModelDocument document, RigHandSide side)
    {
        ArgumentNullException.ThrowIfNull(document);
        document.Validate();
        if (!Enum.IsDefined(side)) throw new ArgumentOutOfRangeException(nameof(side));
        if (!GeneratedBodyRig.IsGenerated(document))
            throw new InvalidOperationException("Finger generation requires a current owned generated body rig.");
        RiggingSession session = document.RiggingSession!;
        RigHandSetup setup = session.Hands.FirstOrDefault(hand => hand.Side == side) ??
            throw new InvalidOperationException("The selected hand has no persisted setup.");
        setup.Validate(session.Landmarks.ToDictionary(static landmark => landmark.Id));
        if (!setup.UserApproved || setup.Fingers.IsEmpty)
            throw new InvalidOperationException("Finger generation requires an explicitly reviewed hand setup.");
        if (setup.Fingers.Any(static finger => finger.Presence is RigFingerPresence.Unresolved or RigFingerPresence.Fused))
            throw new InvalidOperationException("Resolve every declared digit as present or absent before generating fingers.");
        if (setup.Fingers.Any(finger => finger.Presence == RigFingerPresence.Present && !finger.UserApproved))
            throw new InvalidOperationException("Every present digit requires explicit review before generation.");

        var present = setup.Fingers.Where(static finger => finger.Presence == RigFingerPresence.Present).ToArray();
        var expected = present.SelectMany(finger => Enumerable.Range(1, finger.JointGuideIds.Length - 1)
            .Select(segment => (Role: Role(side, finger.Id, segment), Finger: finger, Segment: segment))).ToArray();
        var actual = FingerBones(document, side);
        if (actual.Length > 0)
        {
            if (actual.Length != expected.Length || !actual.Select(static row => row.Role).Order(StringComparer.Ordinal)
                    .SequenceEqual(expected.Select(static row => row.Role).Order(StringComparer.Ordinal)))
                throw new InvalidOperationException("This hand has a partial or differing generated extension; a general rest transaction is required.");
            VerifyExistingFrames(document, expected);
            return document;
        }
        if (expected.Length == 0) return document;

        int parentIndex = FindHandBone(document, side);
        var bones = document.Bones.ToBuilder();
        var globals = ComputeGlobals(document.Bones);
        var entities = session.Recipe.Entities.ToBuilder();
        var assignments = session.Recipe.Assignments.ToBuilder();
        var frames = session.Recipe.FramePolicies.ToBuilder();
        var existingNames = document.CreateEffectiveBones().Select(static bone => bone.Name)
            .Concat(session.Recipe.Entities.Select(static entity => entity.NativeName)).ToHashSet(StringComparer.Ordinal);
        var existingIds = session.Recipe.Entities.Select(static entity => entity.EntityId).ToHashSet();
        var existingRoles = session.Recipe.Assignments.Select(static assignment => assignment.RoleId).ToHashSet(StringComparer.Ordinal);
        var guideById = session.Landmarks.ToDictionary(static landmark => landmark.Id);
        var roleToIndex = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach ((string role, RigFingerDeclaration finger, int segment) in expected)
        {
            string name = Name(role);
            Guid id = StableId(session.Id, guideById[finger.JointGuideIds[segment - 1]].Id, role);
            if (!existingNames.Add(name) || !existingIds.Add(id) || !existingRoles.Add(role))
                throw new InvalidDataException($"Generated finger entity '{role}' collides with an existing identity.");
            Vector3D start = guideById[finger.JointGuideIds[segment - 1]].Position;
            Vector3D end = guideById[finger.JointGuideIds[segment]].Position;
            TransformMatrix global = Frame(start, end, finger.CurlPlaneNormal, finger.RollDegrees, role);
            int parent = segment == 1 ? parentIndex : roleToIndex[Role(side, finger.Id, segment - 1)];
            TransformMatrix local = globals[parent].InvertedAffine() * global;
            TransformTRS localTrs;
            try { localTrs = local.Decompose(); }
            catch (InvalidOperationException exception)
            { throw new InvalidDataException($"Generated finger chain '{role}' cannot produce a TRS bind.", exception); }
            int index = bones.Count;
            bones.Add(new CustomModelBone
            {
                Index = index, FbxObjectId = 0, Name = name, ParentIndex = parent,
                LocalBindTransform = localTrs, ExactLocalBindMatrix = local,
                Kind = BoneKind.Deform, IsWeighted = true,
            });
            globals = AppendGlobal(globals, global);
            roleToIndex.Add(role, index);
            entities.Add(new RigEntityBinding
            {
                EntityId = id, OwnerAssetId = document.ModelId,
                SourceEntityId = GeneratedPrefix + role, NativeName = name,
                Kind = RigNativeEntityKind.Bone, Imported = false,
            });
            assignments.Add(new RigRoleAssignment(role, id));
            frames.Add(new RigEntityFramePolicy
            {
                EntityId = id, FramePolicy = RigFramePolicy.GeneratedDeform,
                BoundsPolicy = RigBoundsPolicy.GenerateSegmentProxy, SolvedGlobalFrame = global,
                Evidence = [new RigEvidenceReference
                {
                    Id = "generated-hand-guide:" + role,
                    Kind = finger.UserApproved ? RigEvidenceKind.UserOverride : RigEvidenceKind.GeometryInference,
                    ArtifactSha256 = session.SourceSha256,
                    Description = "Reviewed hand guide frame candidate; native validation has not been performed.",
                }],
            });
        }

        int added = bones.Count - document.Bones.Length;
        ImmutableArray<CustomModelAuthoredHelper> shiftedHelpers = document.AuthoredHelpers.Select(helper =>
            helper.ParentNodeIndex >= document.Bones.Length
                ? helper with { ParentNodeIndex = checked(helper.ParentNodeIndex + added) }
                : helper).ToImmutableArray();
        RiggingSession replacement = session with
        {
            BindingBackend = null,
            Recipe = session.Recipe with
            {
                Entities = entities.ToImmutable(), Assignments = assignments.ToImmutable(), FramePolicies = frames.ToImmutable(),
            },
        };
        RiggingSession changed = RiggingSessions.Change(session, replacement, RiggingEditKind.Anatomy);
        CustomModelDocument result = document with
        {
            Bones = bones.ToImmutable(), AuthoredHelpers = shiftedHelpers, RiggingSession = changed,
            RigSignature = CustomModelContractSignatures.ComputeRig(
                (document with { Bones = bones.ToImmutable(), AuthoredHelpers = shiftedHelpers }).CreateEffectiveBones()),
            LastBuildReceipt = null,
        };
        result.Validate();
        return result;
    }

    internal static bool ExtensionsValid(CustomModelDocument document)
    {
        if (!GeneratedEyeRig.ExtensionsValid(document)) return false;
        RiggingSession? session = document.RiggingSession;
        if (session is null) return false;
        var expected = session.Hands.SelectMany(hand => hand.Fingers.Where(static finger => finger.Presence == RigFingerPresence.Present)
            .SelectMany(finger => Enumerable.Range(1, finger.JointGuideIds.Length - 1)
                .Select(segment => (Role: Role(hand.Side, finger.Id, segment), Finger: finger, Segment: segment))))
            .ToDictionary(static row => row.Role, StringComparer.Ordinal);
        var actual = new Dictionary<string, (int Index, RigEntityBinding Entity)>(StringComparer.Ordinal);
        var bodyEntityIds = session.Recipe.Entities.Where(entity => IsBodyEntity(entity))
            .Select(static entity => entity.EntityId).ToHashSet();
        foreach (CustomModelBone bone in document.Bones)
        {
            if (GeneratedEyeRig.IsOwnedEyeBone(document, bone.Index)) continue;
            RigEntityBinding? entity = session.Recipe.Entities.FirstOrDefault(candidate => candidate.NativeName == bone.Name &&
                candidate.SourceEntityId?.StartsWith(GeneratedPrefix, StringComparison.Ordinal) == true);
            if (entity is not null && bodyEntityIds.Contains(entity.EntityId)) continue;
            if (entity is null || entity.SourceEntityId is not { } sourceId) return false;
            string role = sourceId[GeneratedPrefix.Length..];
            if (!expected.ContainsKey(role) || !actual.TryAdd(role, (bone.Index, entity)) ||
                entity.OwnerAssetId != document.ModelId || entity.SourceEntityId != GeneratedPrefix + role ||
                entity.Kind != RigNativeEntityKind.Bone || entity.Imported || bone.Name != Name(role) ||
                bone.FbxObjectId != 0 || bone.Kind != BoneKind.Deform || !bone.IsWeighted)
                return false;
        }
        foreach (var row in expected)
        {
            if (!actual.TryGetValue(row.Key, out var bone)) continue; // A pending declaration need not be built yet.
            if (bone.Entity.EntityId != StableId(session.Id, row.Value.Finger.JointGuideIds[row.Value.Segment - 1], row.Key)) return false;
            int expectedParent = row.Value.Segment == 1
                ? FindBodyHandIndex(document, session, row.Key.StartsWith("finger.left.", StringComparison.Ordinal) ? RigHandSide.Left : RigHandSide.Right)
                : actual.TryGetValue(RoleFromSegment(row.Key, row.Value.Segment - 1), out var previous)
                    ? previous.Index
                    : -1;
            RigParentDecision? decision = session.ParentDecisions.FirstOrDefault(candidate => candidate.EntityId == bone.Entity.EntityId);
            if (decision is { UserApproved: true } && RigContractRules.SameHash(decision.SourceSha256, session.SourceSha256))
                expectedParent = decision.ParentEntityId is { } parent ? FindEntityBoneIndex(document, session, parent) : -1;
            if (expectedParent < 0) return false;
            if (document.Bones[bone.Index].ParentIndex != expectedParent) return false;
            if (!session.Recipe.Assignments.Any(assignment => assignment.RoleId == row.Key && assignment.EntityId == bone.Entity.EntityId)) return false;
        }
        return actual.Count == 0 || actual.Values.All(row => session.Recipe.FramePolicies.Any(policy =>
            policy.EntityId == row.Entity.EntityId && policy.FramePolicy == RigFramePolicy.GeneratedDeform && policy.SolvedGlobalFrame is not null));
    }

    internal static ImmutableArray<GeneratedBodySegment> GetSegments(CustomModelDocument document)
    {
        if (!ExtensionsValid(document)) throw new InvalidOperationException("The generated finger extension is not owned by the current hand declarations.");
        RiggingSession session = document.RiggingSession!;
        var globals = ComputeGlobals(document.Bones);
        var guides = session.Landmarks.ToDictionary(static landmark => landmark.Id);
        var fingers = session.Hands.SelectMany(hand => hand.Fingers.Where(static finger => finger.Presence == RigFingerPresence.Present)
            .Select(finger => (Side: hand.Side, Finger: finger))).ToDictionary(row => (row.Side, row.Finger.Id));
        var bodyEntityIds = session.Recipe.Entities.Where(entity => IsBodyEntity(entity))
            .Select(static entity => entity.EntityId).ToHashSet();
        var result = ImmutableArray.CreateBuilder<GeneratedBodySegment>();
        for (int index = 0; index < document.Bones.Length; index++)
        {
            if (GeneratedEyeRig.IsOwnedEyeBone(document, index)) continue;
            RigEntityBinding entity = session.Recipe.Entities.SingleOrDefault(candidate => candidate.NativeName == document.Bones[index].Name &&
                candidate.SourceEntityId?.StartsWith(GeneratedPrefix, StringComparison.Ordinal) == true) ?? throw new InvalidDataException("Generated extension has no owned entity.");
            if (bodyEntityIds.Contains(entity.EntityId)) continue;
            string role = entity.SourceEntityId![GeneratedPrefix.Length..];
            if (!TryParseRole(role, out RigHandSide side, out string digit, out int segment) || !fingers.TryGetValue((side, digit), out var finger))
                throw new InvalidDataException("Generated finger entity has no matching declaration.");
            int next = index + 1;
            RigEntityBinding? nextEntity = null;
            while (next < document.Bones.Length)
            {
                RigEntityBinding? candidateEntity = session.Recipe.Entities.FirstOrDefault(candidate => candidate.NativeName == document.Bones[next].Name);
                if (!GeneratedEyeRig.IsOwnedEyeBone(document, next) && !bodyEntityIds.Contains(candidateEntity?.EntityId ?? Guid.Empty))
                {
                    nextEntity = session.Recipe.Entities.SingleOrDefault(candidate => candidate.NativeName == document.Bones[next].Name &&
                        candidate.SourceEntityId?.StartsWith(GeneratedPrefix, StringComparison.Ordinal) == true);
                    break;
                }
                next++;
            }
            Vector3D end = nextEntity is not null && document.Bones[next].ParentIndex == index &&
                TryParseRole(nextEntity.SourceEntityId![GeneratedPrefix.Length..], out RigHandSide nextSide, out string nextDigit, out int nextSegment) &&
                nextSide == side && nextDigit == digit && nextSegment == segment + 1
                ? globals[next].Translation
                : segment < finger.Finger.JointGuideIds.Length - 1
                    ? guides[finger.Finger.JointGuideIds[segment]].Position
                    : guides[finger.Finger.JointGuideIds[^1]].Position;
            result.Add(new(entity.EntityId, role, globals[index].Translation, end));
        }
        return result.ToImmutable();
    }

    private static (string Role, RigFingerDeclaration Finger, int Segment)[] FingerBones(CustomModelDocument document, RigHandSide side) =>
        FingerBonesCore(document, document.RiggingSession!, side);

    private static (string Role, RigFingerDeclaration Finger, int Segment)[] FingerBonesCore(CustomModelDocument document, RiggingSession session, RigHandSide side) =>
        session.Recipe.Entities.Where(entity => entity.SourceEntityId?.StartsWith(GeneratedPrefix + "finger." + Side(side) + ".", StringComparison.Ordinal) == true)
            .Select(entity => (Role: entity.SourceEntityId![GeneratedPrefix.Length..], Entity: entity))
            .Select(row => (row.Role, Finger: session.Hands.Single(hand => hand.Side == side).Fingers.Single(finger => row.Role.StartsWith($"finger.{Side(side)}.{finger.Id}.", StringComparison.Ordinal)), Segment: ParseSegment(row.Role)))
            .OrderBy(static row => row.Role, StringComparer.Ordinal).ToArray();

    private static void VerifyExistingFrames(CustomModelDocument document, IEnumerable<(string Role, RigFingerDeclaration Finger, int Segment)> expected)
    {
        RiggingSession session = document.RiggingSession!;
        var globals = ComputeGlobals(document.Bones);
        foreach ((string role, RigFingerDeclaration finger, int segment) in expected)
        {
            int index = IndexOfBone(document, Name(role));
            RigEntityBinding? entity = session.Recipe.Entities.FirstOrDefault(candidate =>
                candidate.NativeName == document.Bones[index].Name &&
                candidate.SourceEntityId == GeneratedPrefix + role);
            RigEntityFramePolicy? policy = entity is null
                ? null
                : session.Recipe.FramePolicies.FirstOrDefault(candidate => candidate.EntityId == entity.EntityId);
            RigEvidenceReference? manualEvidence = policy is { } policyValue
                ? policyValue.Evidence.FirstOrDefault(evidence =>
                    evidence.Id == "rest-pose-manual:" + policyValue.EntityId.ToString("N"))
                : null;
            if (manualEvidence is not null)
            {
                string marker = "fingerInputsSha256=";
                int markerIndex = manualEvidence.Description?.IndexOf(marker, StringComparison.Ordinal) ?? -1;
                string expectedFingerprint = RigRestPoseAuthoring.ComputeFingerInputsFingerprint(session, role);
                bool currentInputs = markerIndex >= 0 && manualEvidence.Description!.Length >= markerIndex + marker.Length + 64 &&
                    string.Equals(manualEvidence.Description.Substring(markerIndex + marker.Length, 64), expectedFingerprint, StringComparison.OrdinalIgnoreCase);
                bool currentFrame = policy?.SolvedGlobalFrame is { } solved && solved.NearlyEquals(globals[index], 1e-10);
                if (currentInputs && currentFrame) continue;
                throw new InvalidOperationException("A reviewed finger guide, curl plane, roll or generated frame changed after the rest-pose transaction; apply a new rest transaction before regenerating the hand.");
            }
            Vector3D start = finger.JointGuideIds[segment - 1] == Guid.Empty ? Vector3D.Zero : session.Landmarks.Single(guide => guide.Id == finger.JointGuideIds[segment - 1]).Position;
            Vector3D end = session.Landmarks.Single(guide => guide.Id == finger.JointGuideIds[segment]).Position;
            TransformMatrix frame = Frame(start, end, finger.CurlPlaneNormal, finger.RollDegrees, role);
            if (!globals[index].NearlyEquals(frame, 1e-10)) throw new InvalidOperationException("An existing generated finger frame differs from the reviewed hand setup; a general rest transaction is required.");
        }
    }

    private static int FindHandBone(CustomModelDocument document, RigHandSide side) => FindBodyHandIndex(document, document.RiggingSession!, side);
    private static int FindBodyHandIndex(CustomModelDocument document, RiggingSession session, RigHandSide side)
    {
        int index = GeneratedBodyRig.TryFindGeneratedRoleBone(document, session, "hand." + Side(side), out int found) ? found : -1;
        if (index < 0) throw new InvalidDataException("The generated body has no owned hand parent bone.");
        return index;
    }
    private static int FindEntityBoneIndex(CustomModelDocument document, RiggingSession session, Guid entityId)
    {
        for (int index = 0; index < document.Bones.Length; index++)
            if (GeneratedBodyRig.EntityForBone(document, session, index) == entityId) return index;
        return -1;
    }
    private static bool IsBodyEntity(RigEntityBinding entity) =>
        entity.SourceEntityId is { } sourceId &&
        sourceId.StartsWith(GeneratedPrefix, StringComparison.Ordinal) &&
        GeneratedBodyRig.IsGeneratedBodyRole(sourceId[GeneratedPrefix.Length..]);
    private static int IndexOfBone(CustomModelDocument document, string name)
    {
        for (int index = 0; index < document.Bones.Length; index++)
            if (document.Bones[index].Name == name) return index;
        throw new InvalidDataException($"Generated finger bone '{name}' is missing.");
    }

    private static TransformMatrix[] ComputeGlobals(IReadOnlyList<CustomModelBone> bones)
    {
        var globals = new TransformMatrix[bones.Count];
        foreach (CustomModelBone bone in bones) globals[bone.Index] = bone.ParentIndex < 0 ? bone.ExactLocalBindMatrix : globals[bone.ParentIndex] * bone.ExactLocalBindMatrix;
        return globals;
    }
    private static TransformMatrix[] AppendGlobal(TransformMatrix[] globals, TransformMatrix global) { var next = new TransformMatrix[globals.Length + 1]; globals.CopyTo(next, 0); next[^1] = global; return next; }
    private static string Side(RigHandSide side) => side == RigHandSide.Left ? "left" : "right";
    private static string Role(RigHandSide side, string digit, int segment) => $"finger.{Side(side)}.{digit}.{segment}";
    private static string Name(string role) => role.Replace('.', '_');
    private static Guid StableId(Guid sessionId, Guid guideId, string role) => new(SHA256.HashData(Encoding.UTF8.GetBytes(sessionId.ToString("N") + ":" + guideId.ToString("N") + ":" + role)).AsSpan(0, 16));
    private static int ParseSegment(string role) => int.Parse(role[(role.LastIndexOf('.') + 1)..], System.Globalization.CultureInfo.InvariantCulture);
    private static string RoleFromSegment(string role, int segment) => role[..(role.LastIndexOf('.') + 1)] + segment.ToString(System.Globalization.CultureInfo.InvariantCulture);
    private static bool TryParseRole(string role, out RigHandSide side, out string digit, out int segment)
    {
        side = default; digit = string.Empty; segment = 0;
        string[] parts = role.Split('.');
        if (parts.Length != 4 || parts[0] != "finger" || !int.TryParse(parts[3], out segment) || segment < 1 || segment > 4 ||
            parts[1] is not ("left" or "right")) return false;
        side = parts[1] == "left" ? RigHandSide.Left : RigHandSide.Right; digit = parts[2]; return true;
    }
    private static TransformMatrix Frame(Vector3D start, Vector3D end, Vector3D? curlNormal, double rollDegrees, string role)
    {
        Vector3D direction = end - start;
        if (!direction.TryNormalize(out Vector3D x, epsilon: 0.0) || direction.Length == 0 || curlNormal is not { } rawNormal || !rawNormal.TryNormalize(out Vector3D normal, epsilon: 0.0))
            throw new InvalidDataException($"Generated finger segment '{role}' requires finite noncoincident points and a nonzero curl-plane normal.");
        Vector3D zCandidate = normal - x * Vector3D.Dot(normal, x);
        if (!zCandidate.TryNormalize(out Vector3D z, epsilon: 0.0)) throw new InvalidDataException($"Generated finger segment '{role}' has a curl normal parallel to its direction.");
        Vector3D y = Vector3D.Cross(z, x).Normalized(epsilon: 0.0);
        double roll = rollDegrees * Math.PI / 180.0;
        Vector3D rolledY = y * Math.Cos(roll) + z * Math.Sin(roll);
        Vector3D rolledZ = z * Math.Cos(roll) - y * Math.Sin(roll);
        return new( x.X, rolledY.X, rolledZ.X, start.X, x.Y, rolledY.Y, rolledZ.Y, start.Y, x.Z, rolledY.Z, rolledZ.Z, start.Z, 0, 0, 0, 1);
    }
}
