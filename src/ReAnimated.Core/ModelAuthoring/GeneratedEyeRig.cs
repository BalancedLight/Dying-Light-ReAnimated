using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using ReAnimated.Core.Domain;
using ReAnimated.Core.Mathematics;

namespace ReAnimated.Core.ModelAuthoring;

/// <summary>
/// Appends one reviewed geometry-pivot eye deform node. The node is an
/// unweighted authoring candidate; source eyes, weights, morphs and camera
/// metadata remain separate and unchanged.
/// </summary>
public static class GeneratedEyeRig
{
    private const string SourcePrefix = "generated-eye:";

    public static CustomModelDocument Append(
        CustomModelDocument document,
        RigEyeSide side,
        string boneName)
    {
        ArgumentNullException.ThrowIfNull(document);
        document.Validate();
        if (!Enum.IsDefined(side))
            throw new ArgumentOutOfRangeException(nameof(side), "Generated eye nodes require a defined eye side.");
        ArgumentException.ThrowIfNullOrWhiteSpace(boneName);
        boneName = boneName.Trim();
        if (boneName.Equals("EyeCamera", StringComparison.OrdinalIgnoreCase) ||
            boneName.Equals("RefCamera", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Generated eye nodes cannot claim the reserved EyeCamera or RefCamera names.");

        RiggingSession session = document.RiggingSession ??
            throw new InvalidOperationException("Generated eye authoring requires a current rigging session.");
        if (!session.MatchesSource(document.Source.ContentSha256))
            throw new InvalidOperationException("Generated eye authoring requires a reviewed current source revision.");
        RigEyeSetup setup = session.Eyes.FirstOrDefault(eye =>
            eye.Side == side && eye.Mode == RigEyeSetupMode.GeometryPivot) ??
            throw new InvalidOperationException("The selected side has no persisted geometry-pivot eye setup.");
        if (!setup.UserApproved || setup.GeometryKind is not (RigEyeGeometryKind.GlobeCandidate or RigEyeGeometryKind.ManualPivot) ||
            setup.ParentEntityId is null || setup.ComponentId is null || setup.IslandIndex is null || setup.SourceControlPointIds.IsEmpty)
            throw new InvalidOperationException("Generated eye authoring requires a reviewed globe or manual pivot with explicit component, island and source control-point support.");

        var entityIds = session.Recipe.Entities.Where(entity => entity.OwnerAssetId == document.ModelId)
            .Select(static entity => entity.EntityId).ToHashSet();
        var componentIds = session.Components.Select(static component => component.Id)
            .ToHashSet(StringComparer.Ordinal);
        setup.Validate(entityIds, componentIds);
        Guid parentEntityId = setup.ParentEntityId.Value;
        ImmutableArray<RigParentObservation> observed = RiggingSessions.ObserveSourceHierarchy(document);
        int parentIndex = IndexOfEntity(observed, parentEntityId);
        if (parentIndex < 0 || parentIndex >= document.Bones.Length)
            throw new ArgumentException("Generated eye parents must be explicitly observed source bones.");
        if (document.Bones[parentIndex].Kind is not (BoneKind.Root or BoneKind.Deform))
            throw new ArgumentException("Generated eye parents must be Root or Deform source bones.");

        string role = Role(side);
        Guid expectedId = StableId(session.Id, role);
        string expectedSource = SourcePrefix + role;
        ImmutableArray<CustomModelBone> currentBones = document.Bones;
        int existingBoneIndex = -1;
        for (int index = 0; index < currentBones.Length; index++)
        {
            CustomModelBone bone = currentBones[index];
            if (bone.Name == boneName) existingBoneIndex = index;
        }
        RigEntityBinding? existingEntity = session.Recipe.Entities.FirstOrDefault(entity => entity.EntityId == expectedId);
        if (setup.DeformEntityId is { } savedId)
        {
            if (savedId != expectedId)
                throw new InvalidOperationException("The persisted generated-eye identity does not match the selected session and side.");
            existingBoneIndex = FindGeneratedBone(document, session, savedId, expectedSource);
            RigEntityFramePolicy? existingPolicy = session.Recipe.FramePolicies.FirstOrDefault(policy => policy.EntityId == savedId);
            if (existingBoneIndex < 0 || existingEntity is null ||
                existingEntity.OwnerAssetId != document.ModelId || existingEntity.Imported ||
                existingEntity.Kind != RigNativeEntityKind.Bone || existingEntity.NativeName != boneName ||
                document.Bones[existingBoneIndex].Name != boneName ||
                document.Bones[existingBoneIndex].Kind != BoneKind.Deform ||
                document.Bones[existingBoneIndex].ParentIndex != parentIndex ||
                !Globals(document.Bones)[existingBoneIndex].NearlyEquals(setup.GlobalFrame, 1e-10) ||
                !session.Recipe.Assignments.Any(assignment => assignment.RoleId == role && assignment.EntityId == savedId) ||
                existingPolicy is null || existingPolicy.FramePolicy != RigFramePolicy.GeneratedDeform)
                throw new InvalidOperationException("An existing generated eye node must retain its owned identity, name, parent and reviewed global frame; differing rest frames require a separate transaction.");
            return document;
        }

        if (existingEntity is not null || existingBoneIndex >= 0 ||
            session.Recipe.Assignments.Any(assignment => assignment.RoleId == role || assignment.EntityId == expectedId) ||
            session.Recipe.FramePolicies.Any(policy => policy.EntityId == expectedId))
            throw new InvalidOperationException("A generated eye identity or hierarchy name already exists; it cannot be renamed or overwritten.");

        TransformMatrix parentGlobal = Globals(document.Bones)[parentIndex];
        TransformMatrix local;
        try
        {
            local = parentGlobal.InvertedAffine() * setup.GlobalFrame;
        }
        catch (InvalidOperationException exception)
        {
            throw new InvalidDataException("The selected eye parent has no usable affine bind frame.", exception);
        }
        if (!local.IsFinite || !double.IsFinite(local.LinearDeterminant) || local.LinearDeterminant == 0)
            throw new InvalidDataException("The generated eye local bind frame is nonfinite or singular.");
        TransformTRS localTrs = Dl1AuthoredRigContract.ProjectAffineToTrs(local);
        var bones = currentBones.ToBuilder();
        bones.Add(new CustomModelBone
        {
            Index = bones.Count,
            FbxObjectId = 0,
            Name = boneName,
            ParentIndex = parentIndex,
            LocalBindTransform = localTrs,
            ExactLocalBindMatrix = local,
            Kind = BoneKind.Deform,
            IsWeighted = false,
        });
        int oldBoneCount = currentBones.Length;
        ImmutableArray<CustomModelAuthoredHelper> shiftedHelpers = document.AuthoredHelpers.Select(helper =>
            helper.ParentNodeIndex >= oldBoneCount
                ? helper with { ParentNodeIndex = checked(helper.ParentNodeIndex + 1) }
                : helper).ToImmutableArray();
        var entities = session.Recipe.Entities.ToBuilder();
        entities.Add(new RigEntityBinding
        {
            EntityId = expectedId,
            OwnerAssetId = document.ModelId,
            SourceEntityId = expectedSource,
            NativeName = boneName,
            Kind = RigNativeEntityKind.Bone,
            Imported = false,
        });
        var assignments = session.Recipe.Assignments.ToBuilder();
        assignments.Add(new RigRoleAssignment(role, expectedId));
        var framePolicies = session.Recipe.FramePolicies.ToBuilder();
        framePolicies.Add(new RigEntityFramePolicy
        {
            EntityId = expectedId,
            FramePolicy = RigFramePolicy.GeneratedDeform,
            BoundsPolicy = RigBoundsPolicy.GenerateSegmentProxy,
            SolvedGlobalFrame = setup.GlobalFrame,
            Evidence = MergeEvidence(setup.Evidence, [new RigEvidenceReference
            {
                Id = SourcePrefix + role,
                Kind = RigEvidenceKind.UserOverride,
                ArtifactSha256 = session.SourceSha256,
                Description = "Reviewed geometry-pivot eye frame candidate; native validation has not been performed.",
            }]),
        });
        RigEvidenceReference sourceEvidence = new()
        {
            Id = SourcePrefix + role,
            Kind = RigEvidenceKind.UserOverride,
            ArtifactSha256 = session.SourceSha256,
            Description = "Reviewed geometry-pivot eye frame candidate; native validation has not been performed.",
        };
        RiggingSession replacement = session with
        {
            Eyes = session.Eyes.Select(eye => eye.Side == side && eye.Mode == RigEyeSetupMode.GeometryPivot
                ? eye with { DeformEntityId = expectedId, Evidence = MergeEvidence(eye.Evidence, [sourceEvidence]) }
                : eye).ToImmutableArray(),
            Recipe = session.Recipe with
            {
                Entities = entities.ToImmutable(),
                Assignments = assignments.ToImmutable(),
                FramePolicies = framePolicies.ToImmutable(),
            },
        };
        RiggingSession changed = RiggingSessions.Change(session, replacement, RiggingEditKind.Anatomy);
        CustomModelDocument result = document with
        {
            Bones = bones.ToImmutable(),
            AuthoredHelpers = shiftedHelpers,
            RiggingSession = changed,
            RigSignature = CustomModelContractSignatures.ComputeRig(
                (document with { Bones = bones.ToImmutable(), AuthoredHelpers = shiftedHelpers }).CreateEffectiveBones()),
            LastBuildReceipt = null,
        };
        result.Validate();
        return result;
    }

    /// <summary>Returns the owned generated eye bone index for the selected side.</summary>
    public static int GetBoneIndex(CustomModelDocument document, RigEyeSide side)
    {
        ArgumentNullException.ThrowIfNull(document);
        document.Validate();
        if (!Enum.IsDefined(side))
            throw new ArgumentOutOfRangeException(nameof(side), "Generated eye nodes require a defined eye side.");
        if (!ExtensionsValid(document))
            throw new InvalidOperationException("The document contains no current owned generated-eye extension.");
        RigEyeSetup setup = document.RiggingSession!.Eyes.FirstOrDefault(eye =>
            eye.Side == side && eye.Mode == RigEyeSetupMode.GeometryPivot) ??
            throw new InvalidOperationException("The selected side has no geometry-pivot eye setup.");
        if (setup.DeformEntityId is not { } id)
            throw new InvalidOperationException("The selected eye has no generated deform entity.");
        int index = FindGeneratedBone(document, document.RiggingSession, id, SourcePrefix + Role(side));
        if (index < 0) throw new InvalidOperationException("The selected generated-eye entity is not a current document bone.");
        return index;
    }

    /// <summary>
    /// Validates only owned generated-eye rows. It intentionally does not call
    /// source hierarchy observation, because observation uses this predicate.
    /// </summary>
    internal static bool ExtensionsValid(CustomModelDocument document)
    {
        RiggingSession? session = document.RiggingSession;
        if (session is null || session.RequiresSourceReview ||
            !RigContractRules.SameHash(session.SourceSha256, document.Source.ContentSha256)) return false;
        var entities = session.Recipe.Entities.ToDictionary(static entity => entity.EntityId);
        var eyeIds = new HashSet<Guid>();
        foreach (RigEyeSetup setup in session.Eyes.Where(static eye => eye.DeformEntityId is not null))
        {
            if (!Enum.IsDefined(setup.Side) || setup.Mode != RigEyeSetupMode.GeometryPivot ||
                setup.DeformEntityId is not { } id || !eyeIds.Add(id)) return false;
            string role = Role(setup.Side);
            if (id != StableId(session.Id, role) || !entities.TryGetValue(id, out RigEntityBinding? entity) ||
                entity.OwnerAssetId != document.ModelId || entity.Imported || entity.Kind != RigNativeEntityKind.Bone ||
                entity.SourceEntityId != SourcePrefix + role) return false;
            int boneIndex = FindGeneratedBone(document, session, id, SourcePrefix + role);
            if (boneIndex < 0 || document.Bones[boneIndex].Kind != BoneKind.Deform || document.Bones[boneIndex].FbxObjectId != 0 ||
                document.Bones[boneIndex].Name != entity.NativeName || setup.ParentEntityId is not { } parentId) return false;
            int parentIndex = FindEntityBoneIndex(document, session, parentId);
            if (parentIndex < 0 || document.Bones[boneIndex].ParentIndex != parentIndex) return false;
            TransformMatrix[] globals = Globals(document.Bones);
            if (!globals[boneIndex].NearlyEquals(setup.GlobalFrame, 1e-10)) return false;
            if (!session.Recipe.Assignments.Any(assignment => assignment.RoleId == role && assignment.EntityId == id)) return false;
            RigEntityFramePolicy? policy = session.Recipe.FramePolicies.FirstOrDefault(candidate => candidate.EntityId == id);
            if (policy is null || policy.FramePolicy != RigFramePolicy.GeneratedDeform ||
                policy.SolvedGlobalFrame is not { } solved || !solved.NearlyEquals(globals[boneIndex], 1e-10)) return false;
        }

        foreach (RigEntityBinding entity in session.Recipe.Entities.Where(entity =>
                     entity.SourceEntityId?.StartsWith(SourcePrefix, StringComparison.Ordinal) == true))
        {
            if (!eyeIds.Contains(entity.EntityId) ||
                (entity.SourceEntityId != SourcePrefix + "eye.left.deform" &&
                 entity.SourceEntityId != SourcePrefix + "eye.right.deform" &&
                 entity.SourceEntityId != SourcePrefix + "eye.shared.deform"))
                return false;
        }
        return true;
    }

    internal static bool IsOwnedEyeBone(CustomModelDocument document, int boneIndex)
    {
        if ((uint)boneIndex >= (uint)document.Bones.Length || document.RiggingSession is not { } session) return false;
        CustomModelBone bone = document.Bones[boneIndex];
        return session.Recipe.Entities.Any(entity => entity.NativeName == bone.Name &&
            entity.SourceEntityId?.StartsWith(SourcePrefix, StringComparison.Ordinal) == true &&
            entity.Kind == RigNativeEntityKind.Bone && !entity.Imported);
    }

    private static int FindGeneratedBone(CustomModelDocument document, RiggingSession session, Guid id, string sourceIdentity)
    {
        RigEntityBinding? entity = session.Recipe.Entities.FirstOrDefault(candidate => candidate.EntityId == id &&
            candidate.SourceEntityId == sourceIdentity);
        if (entity is null) return -1;
        for (int index = 0; index < document.Bones.Length; index++)
            if (document.Bones[index].Name == entity.NativeName) return index;
        return -1;
    }

    private static int FindEntityBoneIndex(CustomModelDocument document, RiggingSession session, Guid entityId)
    {
        RigEntityBinding? entity = session.Recipe.Entities.FirstOrDefault(candidate => candidate.EntityId == entityId);
        if (entity is null) return -1;
        for (int index = 0; index < document.Bones.Length; index++)
        {
            CustomModelBone bone = document.Bones[index];
            if (bone.Name != entity.NativeName) continue;
            if (bone.FbxObjectId != 0 && entity.SourceEntityId == "fbx:" + bone.FbxObjectId.ToString(System.Globalization.CultureInfo.InvariantCulture)) return index;
            if (bone.FbxObjectId == 0 && entity.SourceEntityId?.StartsWith("source-name:", StringComparison.Ordinal) == true &&
                entity.SourceEntityId["source-name:".Length..] == bone.Name) return index;
            if (entity.SourceEntityId?.StartsWith("generated-body:", StringComparison.Ordinal) == true && !entity.Imported) return index;
        }
        return -1;
    }

    private static string Role(RigEyeSide side) => $"eye.{side switch
    {
        RigEyeSide.Left => "left",
        RigEyeSide.Right => "right",
        RigEyeSide.Shared => "shared",
        _ => throw new ArgumentOutOfRangeException(nameof(side)),
    }}.deform";
    private static Guid StableId(Guid sessionId, string role) => new(SHA256.HashData(Encoding.UTF8.GetBytes(sessionId.ToString("N") + ":" + role)).AsSpan(0, 16));
    private static int IndexOfEntity(ImmutableArray<RigParentObservation> observed, Guid id)
    {
        for (int index = 0; index < observed.Length; index++) if (observed[index].EntityId == id) return index;
        return -1;
    }
    private static TransformMatrix[] Globals(IReadOnlyList<CustomModelBone> bones)
    {
        var globals = new TransformMatrix[bones.Count];
        foreach (CustomModelBone bone in bones) globals[bone.Index] = bone.ParentIndex < 0 ? bone.ExactLocalBindMatrix : globals[bone.ParentIndex] * bone.ExactLocalBindMatrix;
        return globals;
    }
    private static ImmutableArray<RigEvidenceReference> MergeEvidence(params ImmutableArray<RigEvidenceReference>[] groups)
    {
        var result = ImmutableArray.CreateBuilder<RigEvidenceReference>();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (ImmutableArray<RigEvidenceReference> group in groups)
            foreach (RigEvidenceReference evidence in group) if (ids.Add(evidence.Id)) result.Add(evidence);
        return result.ToImmutable();
    }
}
