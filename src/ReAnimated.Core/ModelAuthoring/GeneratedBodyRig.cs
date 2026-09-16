using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using ReAnimated.Core.Domain;
using ReAnimated.Core.Mathematics;

namespace ReAnimated.Core.ModelAuthoring;

/// <summary>One guide-derived deform segment in authoring coordinates.</summary>
public sealed record GeneratedBodySegment(
    Guid EntityId,
    string RoleId,
    Vector3D Start,
    Vector3D End);

/// <summary>Generated body rig output and the transaction that describes it.</summary>
public sealed record GeneratedBodyRigResult(
    ImmutableArray<CustomModelBone> Bones,
    RiggingSession Session,
    ImmutableArray<GeneratedBodySegment> Segments);

/// <summary>
/// Builds a guide-authored biped hierarchy. The result is an authoring
/// candidate and carries no skin weights, source geometry, or native proof.
/// </summary>
public static class GeneratedBodyRig
{
    private const string RootRole = "root_motion";

    private static readonly string[] Roles =
    [
        "body.pelvis", "body.spine.0", "body.spine.1", "body.spine.2", "body.neck.0", "body.head",
        "arm.left.upper", "arm.left.lower", "hand.left", "arm.right.upper", "arm.right.lower", "hand.right",
        "leg.left.upper", "leg.left.lower", "foot.left", "leg.right.upper", "leg.right.lower", "foot.right",
    ];

    private static readonly (string Role, string? Parent, string? Continuation)[] Topology =
    [
        ("root_motion", null, "body.spine.0"),
        ("body.pelvis", "root_motion", "body.spine.0"),
        ("body.spine.0", "body.pelvis", "body.spine.1"),
        ("body.spine.1", "body.spine.0", "body.spine.2"),
        ("body.spine.2", "body.spine.1", "body.neck.0"),
        ("body.neck.0", "body.spine.2", "body.head"),
        ("body.head", "body.neck.0", null),
        ("arm.left.upper", "body.spine.2", "arm.left.lower"),
        ("arm.left.lower", "arm.left.upper", "hand.left"),
        ("hand.left", "arm.left.lower", null),
        ("arm.right.upper", "body.spine.2", "arm.right.lower"),
        ("arm.right.lower", "arm.right.upper", "hand.right"),
        ("hand.right", "arm.right.lower", null),
        ("leg.left.upper", "body.pelvis", "leg.left.lower"),
        ("leg.left.lower", "leg.left.upper", "foot.left"),
        ("foot.left", "leg.left.lower", null),
        ("leg.right.upper", "body.pelvis", "leg.right.lower"),
        ("leg.right.lower", "leg.right.upper", "foot.right"),
        ("foot.right", "leg.right.lower", null),
    ];

    public static GeneratedBodyRigResult Build(CustomModelDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        document.Validate();
        if (!document.Bones.IsEmpty)
        {
            throw new InvalidOperationException("Generated body rig requires a document without existing bones.");
        }

        RiggingSession session = document.RiggingSession
            ?? throw new InvalidOperationException("Generated body rig requires a rigging session.");
        session.Validate();
        if (session.EntryPath != RigStudioEntryPath.AutoRigBiped ||
            session.RequiresSourceReview ||
            !RigContractRules.SameHash(session.SourceSha256, document.Source.ContentSha256))
        {
            throw new InvalidOperationException("Generated body rig requires a current AutoRigBiped source session.");
        }

        var guides = new Dictionary<string, RigLandmark>(StringComparer.Ordinal);
        foreach (RigLandmark landmark in session.Landmarks)
        {
            if (!guides.TryAdd(landmark.RoleId, landmark) && Roles.Contains(landmark.RoleId, StringComparer.Ordinal))
            {
                throw new InvalidDataException($"Guide role '{landmark.RoleId}' is duplicated.");
            }
        }

        foreach (string role in Roles)
        {
            if (!guides.ContainsKey(role))
            {
                throw new InvalidDataException($"Guide role '{role}' is required for the generated body rig.");
            }
        }

        var existingNames = document.CreateEffectiveBones()
            .Select(static bone => bone.Name)
            .Concat(session.Recipe.Entities.Select(static entity => entity.NativeName))
            .ToHashSet(StringComparer.Ordinal);
        var existingRoles = session.Recipe.Assignments
            .Select(static assignment => assignment.RoleId)
            .ToHashSet(StringComparer.Ordinal);
        var existingIds = session.Recipe.Entities
            .Select(static entity => entity.EntityId)
            .Concat(document.AuthoredHelpers.Select(static helper => helper.Id))
            .ToHashSet();
        var generatedNames = new HashSet<string>(StringComparer.Ordinal);
        var generatedRoles = new HashSet<string>(StringComparer.Ordinal);
        var generatedIds = new HashSet<Guid>();
        foreach ((string role, _, _) in Topology)
        {
            string name = Name(role);
            Guid id = role == RootRole ? StableId(session.Id, Guid.Empty, role) : StableId(session.Id, guides[role].Id, role);
            if (!generatedNames.Add(name) || !generatedRoles.Add(role) || !generatedIds.Add(id) ||
                existingNames.Contains(name) || existingRoles.Contains(role) || existingIds.Contains(id))
            {
                throw new InvalidDataException($"Generated body entity '{role}' collides with existing session identity.");
            }
        }

        var globals = new Dictionary<string, TransformMatrix>(StringComparer.Ordinal);
        var segments = ImmutableArray.CreateBuilder<GeneratedBodySegment>(Roles.Length);
        var bones = ImmutableArray.CreateBuilder<CustomModelBone>(Topology.Length);
        var entities = session.Recipe.Entities.ToBuilder();
        var assignments = session.Recipe.Assignments.ToBuilder();
        var policies = session.Recipe.FramePolicies.ToBuilder();
        var entityIds = new Dictionary<string, Guid>(StringComparer.Ordinal)
        {
            [RootRole] = StableId(session.Id, Guid.Empty, RootRole),
        };
        foreach (string role in Roles)
        {
            entityIds[role] = StableId(session.Id, guides[role].Id, role);
        }

        var boneIndexByRole = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach ((string role, string? parentRole, string? continuationRole) in Topology)
        {
            RigLandmark guide = role == RootRole ? guides["body.pelvis"] : guides[role];
            Vector3D start = guide.Position;
            Vector3D frameEnd = continuationRole is not null
                ? guides[continuationRole].Position
                : LeafEnd(role, parentRole, guides);
            Vector3D segmentEnd = continuationRole is null ? start : frameEnd;
            TransformMatrix global = Frame(start, frameEnd, role);
            globals.Add(role, global);
            Guid entityId = entityIds[role];
            if (role != RootRole)
            {
                segments.Add(new GeneratedBodySegment(entityId, role, start, segmentEnd));
            }

            TransformMatrix local = parentRole is null
                ? global
                : globals[parentRole].InvertedAffine() * global;
            TransformTRS localTrs;
            try
            {
                localTrs = local.Decompose();
            }
            catch (InvalidOperationException exception)
            {
                throw new InvalidDataException($"Generated guide chain '{role}' cannot produce a TRS bind.", exception);
            }

            bones.Add(new CustomModelBone
            {
                Index = bones.Count,
                FbxObjectId = 0,
                Name = Name(role),
                ParentIndex = parentRole is null ? -1 : boneIndexByRole[parentRole],
                LocalBindTransform = localTrs,
                ExactLocalBindMatrix = local,
                Kind = role == RootRole ? BoneKind.Root : BoneKind.Deform,
                IsWeighted = role != RootRole,
            });
            boneIndexByRole.Add(role, bones.Count - 1);

            entities.Add(new RigEntityBinding
            {
                EntityId = entityId,
                OwnerAssetId = document.ModelId,
                SourceEntityId = "generated-body:" + role,
                NativeName = Name(role),
                Kind = RigNativeEntityKind.Bone,
                Imported = false,
            });
            assignments.Add(new RigRoleAssignment(role, entityId));
            RigLandmark evidenceGuide = role == RootRole ? guides["body.pelvis"] : guide;
            policies.Add(new RigEntityFramePolicy
            {
                EntityId = entityId,
                FramePolicy = RigFramePolicy.GeneratedDeform,
                BoundsPolicy = RigBoundsPolicy.GenerateSegmentProxy,
                SolvedGlobalFrame = global,
                Evidence = [new RigEvidenceReference
                {
                    Id = "generated-guide:" + role,
                    Kind = evidenceGuide.Provenance is RigEvidenceKind.UserOverride ? RigEvidenceKind.UserOverride : RigEvidenceKind.GeometryInference,
                    Description = "Guide-authored frame candidate; native validation has not been performed.",
                }],
            });
        }

        RiggingSession replacement = session with
        {
            Recipe = session.Recipe with
            {
                Entities = entities.ToImmutable(),
                Assignments = assignments.ToImmutable(),
                FramePolicies = policies.ToImmutable(),
            },
        };
        RiggingSession changed = RiggingSessions.Change(session, replacement, RiggingEditKind.Anatomy);
        changed.Validate();
        return new GeneratedBodyRigResult(bones.MoveToImmutable(), changed, segments.MoveToImmutable());
    }

    /// <summary>Returns true only when the document contains this owned generated hierarchy.</summary>
    public static bool IsGenerated(CustomModelDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        try
        {
            document.Validate();
        }
        catch (ArgumentException)
        {
            return false;
        }

        RiggingSession? session = document.RiggingSession;
        if (session is null || session.EntryPath != RigStudioEntryPath.AutoRigBiped || session.RequiresSourceReview ||
            !RigContractRules.SameHash(session.SourceSha256, document.Source.ContentSha256) ||
            document.Bones.Length < Topology.Length)
        {
            return false;
        }

        if (session.Landmarks.GroupBy(static landmark => landmark.RoleId, StringComparer.Ordinal).Any(static group => group.Count() > 1) ||
            session.Recipe.Assignments.GroupBy(static assignment => assignment.RoleId, StringComparer.Ordinal).Any(static group => group.Count() > 1))
        {
            return false;
        }

        var entities = session.Recipe.Entities.ToDictionary(static entity => entity.EntityId);
        var assignments = session.Recipe.Assignments.ToDictionary(static assignment => assignment.RoleId);
        var guides = session.Landmarks.ToDictionary(static landmark => landmark.RoleId, StringComparer.Ordinal);
        if (Roles.Any(role => !guides.ContainsKey(role)))
        {
            return false;
        }

        var indices = new Dictionary<string, int>(StringComparer.Ordinal);
        for (int index = 0; index < Topology.Length; index++)
        {
            (string role, _, _) = Topology[index];
            if (!TryFindGeneratedRoleBone(document, session, role, out int boneIndex))
            {
                return false;
            }
            CustomModelBone bone = document.Bones[boneIndex];
            if (bone.FbxObjectId != 0 || bone.Kind != (role == RootRole ? BoneKind.Root : BoneKind.Deform) ||
                bone.Name != Name(role)) return false;
            indices.Add(role, boneIndex);

            string entityRole = role;
            Guid expectedId = role == RootRole
                ? StableId(session.Id, Guid.Empty, role)
                : StableId(session.Id, guides[role].Id, role);
            if (!entities.TryGetValue(expectedId, out RigEntityBinding? entity) ||
                entity.OwnerAssetId != document.ModelId ||
                entity.NativeName != Name(role) ||
                entity.SourceEntityId != "generated-body:" + role ||
                entity.Imported ||
                entity.Kind != RigNativeEntityKind.Bone ||
                !assignments.TryGetValue(entityRole, out RigRoleAssignment? assignment) ||
                assignment.EntityId != expectedId)
            {
                return false;
            }
        }

        foreach ((string role, string? parentRole, _) in Topology)
        {
            int index = indices[role];
            if (session.ParentDecisions.IsEmpty)
            {
                if (document.Bones[index].ParentIndex != (parentRole is null ? -1 : indices[parentRole])) return false;
                continue;
            }
            Guid? actualParent = document.Bones[index].ParentIndex < 0
                ? null
                : EntityForBone(document, session, document.Bones[index].ParentIndex);
            Guid? expectedParent = parentRole is null ? null : EntityForRole(session, parentRole);
            RigParentDecision? decision = session.ParentDecisions.FirstOrDefault(candidate => candidate.EntityId == EntityForRole(session, role));
            if (decision is { UserApproved: true } && RigContractRules.SameHash(decision.SourceSha256, session.SourceSha256))
                expectedParent = decision.ParentEntityId;
            if (actualParent != expectedParent) return false;
        }

        return GeneratedEyeRig.ExtensionsValid(document) && GeneratedHandRig.ExtensionsValid(document);

    }

    internal static bool TryFindGeneratedRoleBone(CustomModelDocument document, RiggingSession session, string role, out int index)
    {
        index = -1;
        RigRoleAssignment? assignment = session.Recipe.Assignments.FirstOrDefault(candidate => candidate.RoleId == role);
        if (assignment is null) return false;
        Guid id = assignment.EntityId;
        RigEntityBinding? entity = session.Recipe.Entities.FirstOrDefault(candidate => candidate.EntityId == id &&
            candidate.SourceEntityId == "generated-body:" + role && candidate.OwnerAssetId == document.ModelId);
        if (entity is null) return false;
        for (int candidate = 0; candidate < document.Bones.Length; candidate++)
            if (document.Bones[candidate].Name == entity.NativeName) { index = candidate; return true; }
        return false;
    }

    internal static Guid EntityForRole(RiggingSession session, string role) =>
        session.Recipe.Assignments.First(assignment => assignment.RoleId == role).EntityId;

    internal static bool IsGeneratedBodyRole(string role) =>
        role == RootRole || Roles.Contains(role, StringComparer.Ordinal);

    internal static Guid? EntityForBone(CustomModelDocument document, RiggingSession session, int boneIndex)
    {
        if ((uint)boneIndex >= (uint)document.Bones.Length) return null;
        CustomModelBone bone = document.Bones[boneIndex];
        string source = bone.FbxObjectId == 0 ? "source-name:" + bone.Name : "fbx:" + bone.FbxObjectId.ToString(System.Globalization.CultureInfo.InvariantCulture);
        RigEntityBinding? entity = session.Recipe.Entities.FirstOrDefault(candidate => candidate.OwnerAssetId == document.ModelId &&
            candidate.SourceEntityId == source);
        entity ??= session.Recipe.Entities.FirstOrDefault(candidate => candidate.OwnerAssetId == document.ModelId &&
            !candidate.Imported && candidate.NativeName == bone.Name &&
            (candidate.SourceEntityId?.StartsWith("generated-body:", StringComparison.Ordinal) == true ||
             candidate.SourceEntityId?.StartsWith("generated-eye:", StringComparison.Ordinal) == true));
        return entity?.EntityId;
    }

    /// <summary>
    /// Reconstructs owned segments from the document's current exact global
    /// bind frames. The document must still contain the same source session.
    /// </summary>
    public static ImmutableArray<GeneratedBodySegment> GetSegments(CustomModelDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (!IsGenerated(document))
        {
            throw new InvalidOperationException("The document does not contain a current generated body rig.");
        }

        RiggingSession session = document.RiggingSession!;
        var globals = new TransformMatrix[document.Bones.Length];
        foreach (CustomModelBone bone in document.Bones)
        {
            globals[bone.Index] = bone.ParentIndex < 0
                ? bone.ExactLocalBindMatrix
                : globals[bone.ParentIndex] * bone.ExactLocalBindMatrix;
        }

        var indexByRole = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach ((string role, _, _) in Topology)
        {
            if (!TryFindGeneratedRoleBone(document, session, role, out int index))
                throw new InvalidDataException($"Generated body role '{role}' has no owned base bone.");
            indexByRole.Add(role, index);
        }
        var segments = ImmutableArray.CreateBuilder<GeneratedBodySegment>(Roles.Length);
        foreach ((string role, string? parentRole, string? continuationRole) in Topology)
        {
            if (role == RootRole)
            {
                continue;
            }

            int index = indexByRole[role];
            Vector3D start = globals[index].Translation;
            Vector3D end;
            if (continuationRole is not null)
            {
                end = globals[indexByRole[continuationRole]].Translation;
            }
            else
            {
                Vector3D incoming = start - globals[indexByRole[parentRole!]].Translation;
                if (!incoming.TryNormalize(out Vector3D direction, epsilon: 0.0) || incoming.Length == 0.0)
                {
                    throw new InvalidDataException($"Generated guide chain '{parentRole}' to '{role}' has coincident endpoints.");
                }

                end = start + (direction * incoming.Length);
            }

            segments.Add(new GeneratedBodySegment(
                StableId(session.Id, session.Landmarks.Single(landmark => landmark.RoleId == role).Id, role),
                role,
                start,
                continuationRole is null ? start : end));
        }

        segments.AddRange(GeneratedHandRig.GetSegments(document));
        return segments.ToImmutable();
    }

    private static string Name(string role) => role.Replace('.', '_');

    private static Guid StableId(Guid sessionId, Guid guideId, string role)
    {
        byte[] bytes = SHA256.HashData(Encoding.UTF8.GetBytes(
            sessionId.ToString("N") + ":" + guideId.ToString("N") + ":" + role));
        return new Guid(bytes.AsSpan(0, 16));
    }

    private static Vector3D LeafEnd(
        string role,
        string? parentRole,
        Dictionary<string, RigLandmark> guides)
    {
        if (parentRole is null)
        {
            throw new InvalidDataException($"Generated root '{role}' has no frame continuation.");
        }

        Vector3D start = guides[role].Position;
        Vector3D incoming = start - guides[parentRole].Position;
        if (!incoming.TryNormalize(out Vector3D direction, epsilon: 0.0) || incoming.Length == 0.0)
        {
            throw new InvalidDataException($"Guide chain '{parentRole}' to '{role}' has coincident endpoints.");
        }

        return start + (direction * incoming.Length);
    }

    private static TransformMatrix Frame(Vector3D start, Vector3D end, string role)
    {
        Vector3D direction = end - start;
        if (!direction.TryNormalize(out Vector3D x, epsilon: 0.0) || direction.Length == 0.0)
        {
            throw new InvalidDataException($"Generated guide segment '{role}' has coincident endpoints.");
        }

        Vector3D up = Vector3D.UnitY;
        if (Math.Abs(Vector3D.Dot(up, x)) > 0.999999)
        {
            up = Vector3D.UnitZ;
        }

        Vector3D yCandidate = up - (x * Vector3D.Dot(up, x));
        if (!yCandidate.TryNormalize(out Vector3D y, epsilon: 0.0))
        {
            up = Vector3D.UnitZ;
            y = (up - (x * Vector3D.Dot(up, x))).Normalized(epsilon: 0.0);
        }

        Vector3D z = Vector3D.Cross(x, y).Normalized(epsilon: 0.0);
        y = Vector3D.Cross(z, x).Normalized(epsilon: 0.0);
        return new TransformMatrix(
            x.X, y.X, z.X, start.X,
            x.Y, y.Y, z.Y, start.Y,
            x.Z, y.Z, z.Z, start.Z,
            0.0, 0.0, 0.0, 1.0);
    }
}
