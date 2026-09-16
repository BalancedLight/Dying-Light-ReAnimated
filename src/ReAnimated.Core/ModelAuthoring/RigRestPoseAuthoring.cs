using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using ReAnimated.Core.Domain;
using ReAnimated.Core.Mathematics;

namespace ReAnimated.Core.ModelAuthoring;

public enum RigRestDescendantMode { KeepGlobal, FollowLocal }

/// <summary>The immutable result of one rest-pose frame transaction.</summary>
public sealed record RigRestPoseEditResult(
    CustomModelDocument Document,
    ImmutableArray<TransformMatrix> BeforeGlobals,
    ImmutableArray<TransformMatrix> AfterGlobals,
    ImmutableArray<int> ChangedBoneIndices,
    ImmutableArray<Guid> ChangedEntityIds);

/// <summary>
/// Changes one owned source/generated bone frame while retaining hierarchy
/// order and parentage. This is an authoring transaction; it does not claim a
/// native runtime rest-pose or skinning validation result.
/// </summary>
public static class RigRestPoseAuthoring
{
    public static RigRestPoseEditResult Apply(
        CustomModelDocument document,
        RiggingJobToken token,
        Guid entityId,
        TransformMatrix desiredGlobal,
        RigRestDescendantMode mode)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(token);
        document.Validate();
        if (!Enum.IsDefined(mode)) throw new ArgumentOutOfRangeException(nameof(mode));
        RigRecipeRules.Affine(desiredGlobal, nameof(desiredGlobal));
        RiggingSession session = document.RiggingSession ??
            throw new InvalidOperationException("Rest-pose authoring requires a current rigging session.");
        if (!session.Matches(token))
            throw new InvalidOperationException("Rest-pose authoring inputs changed; refresh the model before applying the pose.");

        ImmutableArray<RigParentObservation> observed = RiggingSessions.ObserveSourceHierarchy(document);
        int selectedIndex = IndexOfEntity(observed, entityId);
        if (selectedIndex < 0 || selectedIndex >= document.Bones.Length)
            throw new ArgumentException("Rest-pose selection must identify an observed source or generated bone.", nameof(entityId));
        if (document.Bones[selectedIndex].Kind is not (BoneKind.Root or BoneKind.Deform))
            throw new ArgumentException("Rest-pose selection must identify a Root or Deform bone.", nameof(entityId));

        ImmutableArray<CustomModelBone> beforeBones = document.Bones;
        ImmutableArray<CustomModelBone> beforeEffective = document.CreateEffectiveBones();
        TransformMatrix[] beforeGlobals = ComputeGlobals(beforeEffective);
        foreach (TransformMatrix global in beforeGlobals)
            EnsureUsableFrame(global, "The current rest-pose hierarchy contains a nonfinite or singular global frame.");
        if (beforeGlobals[selectedIndex].NearlyEquals(desiredGlobal, 1e-12))
        {
            return new RigRestPoseEditResult(
                document,
                beforeGlobals.ToImmutableArray(),
                beforeGlobals.ToImmutableArray(),
                [],
                []);
        }

        TransformMatrix[] afterGlobals = (TransformMatrix[])beforeGlobals.Clone();
        afterGlobals[selectedIndex] = desiredGlobal;
        TransformMatrix parentGlobal = beforeBones[selectedIndex].ParentIndex < 0
            ? TransformMatrix.Identity
            : beforeGlobals[beforeBones[selectedIndex].ParentIndex];
        TransformMatrix selectedLocal = parentGlobal.InvertedAffine() * desiredGlobal;
        EnsureUsableFrame(selectedLocal, "The selected rest-pose local frame is nonfinite or singular.");

        var localByEffectiveIndex = new Dictionary<int, TransformMatrix>();
        foreach (CustomModelBone bone in beforeEffective)
            localByEffectiveIndex[bone.Index] = bone.ExactLocalBindMatrix;
        localByEffectiveIndex[selectedIndex] = selectedLocal;

        if (mode == RigRestDescendantMode.KeepGlobal)
        {
            for (int index = 0; index < beforeEffective.Length; index++)
            {
                if (index == selectedIndex || beforeEffective[index].ParentIndex != selectedIndex) continue;
                TransformMatrix compensated = desiredGlobal.InvertedAffine() * beforeGlobals[index];
                EnsureUsableFrame(compensated, "A direct child cannot retain its global rest frame after the selected change.");
                localByEffectiveIndex[index] = compensated;
                afterGlobals[index] = beforeGlobals[index];
            }
        }

        for (int index = 0; index < beforeEffective.Length; index++)
        {
            int parent = beforeEffective[index].ParentIndex;
            afterGlobals[index] = parent < 0
                ? localByEffectiveIndex[index]
                : afterGlobals[parent] * localByEffectiveIndex[index];
            EnsureUsableFrame(afterGlobals[index], "The rest-pose hierarchy produced a nonfinite or singular global frame.");
        }

        var changedGlobalIndices = ImmutableArray.CreateBuilder<int>();
        for (int index = 0; index < beforeEffective.Length; index++)
            if (!beforeGlobals[index].NearlyEquals(afterGlobals[index], 1e-12)) changedGlobalIndices.Add(index);
        var changedLocalIndices = ImmutableArray.CreateBuilder<int>();
        for (int index = 0; index < beforeEffective.Length; index++)
            if (localByEffectiveIndex[index] != beforeEffective[index].ExactLocalBindMatrix) changedLocalIndices.Add(index);
        ImmutableArray<int> changedEffective = changedGlobalIndices.Concat(changedLocalIndices).Distinct().ToImmutableArray();

        ImmutableArray<CustomModelBone> nextBones = beforeBones.ToBuilder().MoveToImmutable();
        var boneBuilder = nextBones.ToBuilder();
        for (int index = 0; index < beforeBones.Length; index++)
        {
            TransformMatrix local = localByEffectiveIndex[index];
            if (local == beforeBones[index].ExactLocalBindMatrix) continue;
            boneBuilder[index] = beforeBones[index] with
            {
                LocalBindTransform = Dl1AuthoredRigContract.ProjectAffineToTrs(local),
                ExactLocalBindMatrix = local,
            };
        }
        nextBones = boneBuilder.ToImmutable();

        var helperBuilder = document.AuthoredHelpers.ToBuilder();
        for (int helperIndex = 0; helperIndex < document.AuthoredHelpers.Length; helperIndex++)
        {
            int effectiveIndex = document.Bones.Length + helperIndex;
            TransformMatrix local = localByEffectiveIndex[effectiveIndex];
            CustomModelAuthoredHelper helper = document.AuthoredHelpers[helperIndex];
            if (local == helper.ExactLocalMatrix) continue;
            helperBuilder[helperIndex] = helper with
            {
                LocalTransform = Dl1AuthoredRigContract.ProjectAffineToTrs(local),
                ExactLocalMatrix = local,
            };
        }

        CustomModelDocument frameDocument = document with
        {
            Bones = nextBones,
            AuthoredHelpers = helperBuilder.ToImmutable(),
        };
        ImmutableArray<RigParentObservation> frameObserved = observed;
        RiggingSession candidateSession = UpdateSession(
            session,
            frameDocument,
            frameObserved,
            beforeGlobals,
            afterGlobals,
            changedGlobalIndices.ToImmutable(),
            changedLocalIndices.ToImmutable());
        RiggingSession changedSession = RiggingSessions.Change(session, candidateSession, RiggingEditKind.Anatomy);

        CustomModelDocument result = frameDocument with
        {
            RiggingSession = changedSession,
            RigSignature = CustomModelContractSignatures.ComputeRig(frameDocument.CreateEffectiveBones()),
            LastBuildReceipt = null,
        };
        result.Validate();
        return new RigRestPoseEditResult(
            result,
            beforeGlobals.ToImmutableArray(),
            afterGlobals.ToImmutableArray(),
            changedEffective,
            changedEffective.Select(index => frameObserved[index].EntityId).ToImmutableArray());
    }

    private static RiggingSession UpdateSession(
        RiggingSession session,
        CustomModelDocument frameDocument,
        ImmutableArray<RigParentObservation> observed,
        TransformMatrix[] beforeGlobals,
        TransformMatrix[] afterGlobals,
        ImmutableArray<int> changedGlobalIndices,
        ImmutableArray<int> changedLocalIndices)
    {
        var changed = changedGlobalIndices.ToHashSet();
        var localChanged = changedLocalIndices.ToHashSet();
        ImmutableArray<RigLandmark>.Builder landmarkBuilder = session.Landmarks.ToBuilder();
        foreach (int index in changedGlobalIndices)
        {
            Guid id = observed[index].EntityId;
            string? role = session.Recipe.Assignments.FirstOrDefault(assignment => assignment.EntityId == id)?.RoleId;
            if (role is null) continue;
            if (TryFingerRole(role, out _, out _, out _, out _))
            {
                MoveFingerGuides(landmarkBuilder, session, role, beforeGlobals[index], afterGlobals[index]);
            }
            else
            {
                int guideIndex = IndexOfGuideRole(landmarkBuilder, role);
                if (guideIndex >= 0)
                {
                    landmarkBuilder[guideIndex] = landmarkBuilder[guideIndex] with
                    {
                        Position = afterGlobals[index].Translation,
                        UserApproved = false,
                    };
                }
            }
        }
        ImmutableArray<RigLandmark> landmarks = landmarkBuilder.ToImmutable();

        ImmutableArray<RigHandSetup> hands = session.Hands.Select(hand =>
        {
            string wristRole = "hand." + (hand.Side == RigHandSide.Left ? "left" : "right");
            Guid? wristEntity = session.Recipe.Assignments.FirstOrDefault(assignment => assignment.RoleId == wristRole)?.EntityId;
            int wristIndex = wristEntity is { } id ? IndexOfEntity(observed, id) : -1;
            if (wristIndex < 0 || !changed.Contains(wristIndex)) return hand;
            TransformMatrix wristDelta = afterGlobals[wristIndex] * beforeGlobals[wristIndex].InvertedAffine();
            TransformMatrix palmFrame = wristDelta * hand.PalmFrame;
            if (!palmFrame.IsFinite || !double.IsFinite(palmFrame.LinearDeterminant) || palmFrame.LinearDeterminant <= 0)
                palmFrame = hand.PalmFrame;
            ImmutableArray<RigFingerDeclaration> fingers = hand.Fingers.Select(finger =>
            {
                if (finger.CurlPlaneNormal is not { } normal) return finger with { UserApproved = false };
                Vector3D transformed = TransformNormal(wristDelta, normal);
                return transformed.TryNormalize(out Vector3D unitNormal, epsilon: 0.0)
                    ? finger with { CurlPlaneNormal = unitNormal, UserApproved = false }
                    : finger with { UserApproved = false };
            }).ToImmutableArray();
            return hand with { PalmFrame = palmFrame, UserApproved = false, Fingers = fingers };
        }).ToImmutableArray();

        ImmutableArray<RigEyeSetup> eyes = session.Eyes.Select(eye =>
        {
            int index = EyeEffectiveIndex(eye, observed);
            if (index < 0 || !changed.Contains(index)) return eye;
            RigEyeSetup updated = eye with { GlobalFrame = afterGlobals[index], UserApproved = false };
            if (updated.Mode == RigEyeSetupMode.GeometryPivot && updated.GeometryKind == RigEyeGeometryKind.GlobeCandidate)
                updated = updated with { GeometryKind = RigEyeGeometryKind.ManualPivot, GlobeRadius = null };
            return updated;
        }).ToImmutableArray();

        ImmutableArray<HelperRecipe> helpers = session.Recipe.Helpers.Select(recipe =>
        {
            HelperRecipe updated = recipe;
            int index = IndexOfEntity(observed, recipe.EntityId);
            if (index >= 0 && localChanged.Contains(index))
            {
                TransformMatrix local = index < frameDocument.Bones.Length
                    ? frameDocument.Bones[index].ExactLocalBindMatrix
                    : frameDocument.AuthoredHelpers[index - frameDocument.Bones.Length].ExactLocalMatrix;
                updated = updated with { LocalFrame = local, UserApproved = false };
            }
            return updated;
        }).ToImmutableArray();

        ImmutableArray<RigEntityFramePolicy> policies = session.Recipe.FramePolicies.Select(policy =>
        {
            int index = IndexOfEntity(observed, policy.EntityId);
            if (index < 0 || !changed.Contains(index) || policy.SolvedGlobalFrame is null) return policy;
            ImmutableArray<RigEvidenceReference> evidence = policy.Evidence;
            string? role = session.Recipe.Assignments.FirstOrDefault(assignment => assignment.EntityId == policy.EntityId)?.RoleId;
            if (role is not null && TryFingerRole(role, out _, out _, out _, out _))
            {
                string markerId = "rest-pose-manual:" + policy.EntityId.ToString("N");
                RigEvidenceReference marker = new()
                {
                    Id = markerId,
                    Kind = RigEvidenceKind.UserOverride,
                    ArtifactSha256 = session.SourceSha256,
                    Description = "Reviewed rest-pose transaction preserved this generated finger frame; guide re-aiming is not implied. fingerInputsSha256=" +
                        ComputeFingerInputsFingerprint(session with { Landmarks = landmarks, Hands = hands }, role),
                };
                int markerIndex = -1;
                for (int evidenceIndex = 0; evidenceIndex < evidence.Length; evidenceIndex++)
                    if (evidence[evidenceIndex].Id == markerId) { markerIndex = evidenceIndex; break; }
                evidence = markerIndex < 0 ? evidence.Add(marker) : evidence.SetItem(markerIndex, marker);
            }
            return policy with { SolvedGlobalFrame = afterGlobals[index], Evidence = evidence };
        }).ToImmutableArray();

        return session with
        {
            Landmarks = landmarks,
            Hands = hands,
            Eyes = eyes,
            BindingBackend = null,
            Recipe = session.Recipe with { Helpers = helpers, FramePolicies = policies },
        };
    }

    private static void MoveFingerGuides(
        ImmutableArray<RigLandmark>.Builder landmarks,
        RiggingSession session,
        string role,
        TransformMatrix beforeGlobal,
        TransformMatrix afterGlobal)
    {
        if (!TryFingerRole(role, out RigHandSide side, out string digit, out int segment, out _)) return;
        RigHandSetup? hand = session.Hands.FirstOrDefault(candidate => candidate.Side == side);
        RigFingerDeclaration? finger = hand?.Fingers.FirstOrDefault(candidate => candidate.Id == digit);
        if (finger is null || segment < 1 || segment > finger.JointGuideIds.Length) return;
        Guid guideId = finger.JointGuideIds[segment - 1];
        int guideIndex = IndexOfGuide(landmarks, guideId);
        if (guideIndex >= 0)
            landmarks[guideIndex] = landmarks[guideIndex] with { Position = afterGlobal.Translation, UserApproved = false };
        if (segment == finger.JointGuideIds.Length - 1)
        {
            TransformMatrix delta = afterGlobal * beforeGlobal.InvertedAffine();
            Guid terminalId = finger.JointGuideIds[^1];
            int terminalIndex = IndexOfGuide(landmarks, terminalId);
            if (terminalIndex >= 0)
            {
                Vector3D transformed = delta.TransformPoint(landmarks[terminalIndex].Position);
                landmarks[terminalIndex] = landmarks[terminalIndex] with { Position = transformed, UserApproved = false };
            }
        }
    }

    private static bool TryFingerRole(string role, out RigHandSide side, out string digit, out int segment, out string ignored)
    {
        side = default; digit = string.Empty; segment = 0; ignored = string.Empty;
        string[] parts = role.Split('.');
        if (parts.Length != 4 || parts[0] != "finger" || parts[1] is not ("left" or "right") ||
            !int.TryParse(parts[3], out segment) || segment < 1) return false;
        side = parts[1] == "left" ? RigHandSide.Left : RigHandSide.Right;
        digit = parts[2]; return true;
    }

    /// <summary>
    /// Hashes only the semantic inputs that determine a generated finger frame.
    /// Review flags and unrelated session fields are intentionally excluded so
    /// a later re-review does not invalidate an otherwise unchanged frame.
    /// </summary>
    internal static string ComputeFingerInputsFingerprint(RiggingSession session, string role)
    {
        if (!TryFingerRole(role, out RigHandSide side, out string digit, out _, out _)) return string.Empty;
        RigHandSetup? hand = session.Hands.FirstOrDefault(candidate => candidate.Side == side);
        RigFingerDeclaration? finger = hand?.Fingers.FirstOrDefault(candidate => candidate.Id == digit);
        if (finger is null) return string.Empty;
        var guides = session.Landmarks.ToDictionary(static guide => guide.Id);
        var text = new StringBuilder()
            .Append((int)side).Append('|').Append(digit).Append('|').Append((int)finger.Presence).Append('|');
        foreach (Guid guideId in finger.JointGuideIds)
        {
            text.Append(guideId.ToString("N")).Append('|');
            if (guides.TryGetValue(guideId, out RigLandmark? guide))
                AppendVector(text, guide.Position);
            else
                text.Append("missing");
            text.Append('|');
        }
        if (finger.CurlPlaneNormal is { } normal) AppendVector(text, normal); else text.Append("null");
        text.Append('|').Append(finger.RollDegrees.ToString("R", System.Globalization.CultureInfo.InvariantCulture));
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString())));

        static void AppendVector(StringBuilder builder, Vector3D value) => builder
            .Append(value.X.ToString("R", System.Globalization.CultureInfo.InvariantCulture)).Append(',')
            .Append(value.Y.ToString("R", System.Globalization.CultureInfo.InvariantCulture)).Append(',')
            .Append(value.Z.ToString("R", System.Globalization.CultureInfo.InvariantCulture));
    }

    private static int EyeEffectiveIndex(RigEyeSetup eye, ImmutableArray<RigParentObservation> observed)
    {
        Guid? id = eye.DeformEntityId ?? eye.SourceEntityId ?? eye.HelperEntityId;
        return id is null ? -1 : IndexOfEntity(observed, id.Value);
    }

    private static Vector3D TransformNormal(TransformMatrix transform, Vector3D normal)
    {
        TransformMatrix inverse = transform.InvertedAffine();
        return new Vector3D(
            (inverse.M11 * normal.X) + (inverse.M21 * normal.Y) + (inverse.M31 * normal.Z),
            (inverse.M12 * normal.X) + (inverse.M22 * normal.Y) + (inverse.M32 * normal.Z),
            (inverse.M13 * normal.X) + (inverse.M23 * normal.Y) + (inverse.M33 * normal.Z));
    }

    private static int IndexOfGuide(ImmutableArray<RigLandmark>.Builder guides, Guid id)
    {
        for (int index = 0; index < guides.Count; index++) if (guides[index].Id == id) return index;
        return -1;
    }

    private static int IndexOfGuideRole(ImmutableArray<RigLandmark>.Builder guides, string role)
    {
        for (int index = 0; index < guides.Count; index++) if (guides[index].RoleId == role) return index;
        return -1;
    }

    private static int IndexOfEntity(ImmutableArray<RigParentObservation> observed, Guid entityId)
    {
        for (int index = 0; index < observed.Length; index++) if (observed[index].EntityId == entityId) return index;
        return -1;
    }

    private static TransformMatrix[] ComputeGlobals(IReadOnlyList<CustomModelBone> bones)
    {
        var globals = new TransformMatrix[bones.Count];
        foreach (CustomModelBone bone in bones)
            globals[bone.Index] = bone.ParentIndex < 0 ? bone.ExactLocalBindMatrix : globals[bone.ParentIndex] * bone.ExactLocalBindMatrix;
        return globals;
    }

    private static void EnsureUsableFrame(TransformMatrix matrix, string message)
    {
        if (!matrix.IsFinite || !double.IsFinite(matrix.LinearDeterminant) || matrix.LinearDeterminant == 0)
            throw new InvalidDataException(message);
    }
}
