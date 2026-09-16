using System.Collections.Immutable;
using System.Text.Json;
using ReAnimated.Core.Domain;
using ReAnimated.Core.Mathematics;

namespace ReAnimated.Core.ModelAuthoring;

/// <summary>
/// Materializes one reviewed eye pivot or gaze reference as an unweighted
/// authored helper. Imported bones and source-linked geometry remain intact;
/// this operation does not create or select a camera contract.
/// </summary>
public static class RigEyeHelperAuthoring
{
    public static CustomModelDocument Apply(
        CustomModelDocument document,
        RiggingJobToken token,
        RigEyeSetup setup,
        string helperName)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(token);
        ArgumentNullException.ThrowIfNull(setup);
        document.Validate();

        RiggingSession session = document.RiggingSession ??
            throw new InvalidOperationException("Eye-helper authoring requires a current rigging session.");
        if (!session.Matches(token))
            throw new InvalidOperationException("Eye-helper authoring inputs changed; refresh the model and select the eye parent again.");
        if (!session.MatchesSource(document.Source.ContentSha256))
            throw new InvalidOperationException("Eye-helper authoring requires a reviewed current source revision.");
        if (setup.Mode is not (RigEyeSetupMode.GeometryPivot or RigEyeSetupMode.GazeReference))
            throw new ArgumentException("Only reviewed geometry-pivot and gaze-reference setups can create an eye helper.", nameof(setup));
        if (!setup.UserApproved)
            throw new InvalidOperationException("Eye-helper authoring requires an explicitly reviewed eye setup.");

        ArgumentException.ThrowIfNullOrWhiteSpace(helperName);
        helperName = helperName.Trim();
        if (string.Equals(helperName, "EyeCamera", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(helperName, "RefCamera", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Eye-helper authoring cannot create or rename the reserved EyeCamera or RefCamera nodes.");

        var entityIds = session.Recipe.Entities.Where(entity => entity.OwnerAssetId == document.ModelId)
            .Select(static entity => entity.EntityId).ToHashSet();
        var componentIds = session.Components.Select(static component => component.Id)
            .ToHashSet(StringComparer.Ordinal);
        setup.Validate(entityIds, componentIds);
        if (setup.ParentEntityId is not { } parentEntityId || parentEntityId == Guid.Empty)
            throw new ArgumentException("An eye helper requires an explicit observed parent entity.", nameof(setup));

        ImmutableArray<RigParentObservation> observed = RiggingSessions.ObserveSourceHierarchy(document);
        int parentNodeIndex = IndexOfEntity(observed, parentEntityId);
        if (parentNodeIndex < 0 || parentNodeIndex >= document.CreateEffectiveBones().Length)
            throw new ArgumentException("The eye helper parent must be an observed entity in the effective hierarchy.", nameof(setup));
        RigEntityBinding parentEntity = session.Recipe.Entities.FirstOrDefault(entity => entity.EntityId == parentEntityId) ??
            throw new ArgumentException("The eye helper parent is not owned by this model.", nameof(setup));
        if (parentEntity.OwnerAssetId != document.ModelId)
            throw new ArgumentException("The eye helper parent is not owned by this model.", nameof(setup));

        TransformMatrix parentGlobal = ComputeGlobals(document.CreateEffectiveBones())[parentNodeIndex];
        TransformMatrix localFrame;
        try
        {
            localFrame = parentGlobal.InvertedAffine() * setup.GlobalFrame;
        }
        catch (InvalidOperationException exception)
        {
            throw new InvalidDataException("The observed eye-helper parent has no usable affine global frame.", exception);
        }
        if (!localFrame.IsFinite || !double.IsFinite(localFrame.LinearDeterminant) || localFrame.LinearDeterminant == 0)
            throw new InvalidDataException("The composed eye-helper local frame is nonfinite or singular.");
        TransformTRS previewLocal = Dl1AuthoredRigContract.ProjectAffineToTrs(localFrame);

        Guid helperId = setup.HelperEntityId ?? Guid.Empty;
        CustomModelAuthoredHelper? existingHelper = null;
        RigEntityBinding? existingEntity = null;
        HelperRecipe? existingRecipe = null;
        if (setup.HelperEntityId is { } requestedId)
        {
            if (requestedId == Guid.Empty)
                throw new ArgumentException("An existing eye-helper identity cannot be empty.", nameof(setup));
            helperId = requestedId;
            existingHelper = document.AuthoredHelpers.FirstOrDefault(helper => helper.Id == helperId);
            existingEntity = session.Recipe.Entities.FirstOrDefault(entity => entity.EntityId == helperId);
            existingRecipe = session.Recipe.Helpers.FirstOrDefault(recipe => recipe.EntityId == helperId);
            int observedHelperIndex = IndexOfEntity(observed, helperId);
            if (existingHelper is null || existingEntity is null ||
                existingEntity.OwnerAssetId != document.ModelId || existingEntity.Imported ||
                existingEntity.Kind != RigNativeEntityKind.Helper || observedHelperIndex < document.Bones.Length ||
                observedHelperIndex < 0)
                throw new InvalidOperationException("An existing eye helper must be a separately authored helper owned and observed by this model; imported source references remain SourceEye setups.");
            if (existingHelper.Name != helperName || existingEntity.NativeName != helperName ||
                observed[observedHelperIndex].ParentEntityId != parentEntityId ||
                existingRecipe is not null && (existingRecipe.ParentEntityId != parentEntityId || existingRecipe.OwnerAssetId != document.ModelId))
                throw new InvalidOperationException("An existing eye helper retains its authored name and parent; use a new identity for a different placement.");
            if (existingRecipe is not null)
                RequireLockedFieldsPreserved(existingEntity, existingRecipe, helperName, parentEntityId, localFrame,
                    BoundsFor(setup));
        }
        else
        {
            EnsureUniqueName(document, helperName);
        }

        CustomModelDocument working = document;
        if (setup.HelperEntityId is null)
        {
            working = CustomModelHelperAuthoring.DuplicateAsHelper(
                working, parentNodeIndex, CustomModelAuthoredHelperKind.Helper, helperName);
            helperId = working.AuthoredHelpers[^1].Id;
        }

        // SetLocalTransform records both the preview TRS and the exact affine
        // residual, so parent scale/shear is not silently projected away.
        if (working.AuthoredHelpers.FirstOrDefault(helper => helper.Id == helperId) is not null)
            working = CustomModelHelperAuthoring.SetLocalTransform(working, helperId, previewLocal, localFrame);
        else
            throw new InvalidOperationException("The requested eye helper was not materialized in the authored hierarchy.");

        RiggingSession materializedSession = working.RiggingSession ??
            throw new InvalidOperationException("Eye-helper materialization lost the rigging session.");
        HelperRecipe? materializedRecipe = materializedSession.Recipe.Helpers.FirstOrDefault(recipe => recipe.EntityId == helperId);
        RigEntityFramePolicy? standalonePolicy = materializedSession.Recipe.FramePolicies.FirstOrDefault(policy => policy.EntityId == helperId);
        if (materializedSession.Recipe.Entities.FirstOrDefault(entity => entity.EntityId == helperId) is null)
            throw new InvalidOperationException("Eye-helper materialization lost its stable entity.");
        var nextEntities = materializedSession.Recipe.Entities.ToBuilder();
        if (parentNodeIndex < working.Bones.Length)
        {
            int parentEntityIndex = IndexOfEntity(nextEntities, parentEntityId);
            if (parentEntityIndex < 0)
                throw new InvalidOperationException("The observed eye-helper parent has no stable recipe entity.");
            RigEntityBinding observedParent = nextEntities[parentEntityIndex];
            if (observedParent.Kind == RigNativeEntityKind.Unknown)
            {
                RigNativeEntityKind classifiedKind = working.Bones[parentNodeIndex].Kind switch
                {
                    BoneKind.Root or BoneKind.Deform => RigNativeEntityKind.Bone,
                    BoneKind.Helper => RigNativeEntityKind.Helper,
                    _ => throw new ArgumentException("An eye-helper parent must be an observed source bone or helper.", nameof(setup)),
                };
                nextEntities[parentEntityIndex] = observedParent with { Kind = classifiedKind };
            }
        }
        Vector3D boundsCenter = Vector3D.Zero;
        Vector3D boundsHalfExtents = BoundsFor(setup);
        RigEvidenceReference sourceEvidence = new()
        {
            Id = $"eye-helper-source:{helperId:N}",
            Kind = RigEvidenceKind.ImportedSource,
            ArtifactSha256 = session.SourceSha256,
            Description = "Observed eye-helper parent and source identity; native camera behavior has not been established.",
        };
        ImmutableArray<RigEvidenceReference> evidence = MergeEvidence(
            materializedRecipe?.Evidence ?? [], standalonePolicy?.Evidence ?? [], setup.Evidence, [sourceEvidence]);
        HelperRecipe desiredRecipe = new()
        {
            EntityId = helperId,
            OwnerAssetId = document.ModelId,
            RoleId = setup.Mode == RigEyeSetupMode.GeometryPivot ? "eye.geometry_pivot" : "eye.gaze_reference",
            ParentEntityId = parentEntityId,
            LocalFrame = localFrame,
            BoundsCenter = boundsCenter,
            BoundsHalfExtents = boundsHalfExtents,
            FramePolicy = RigFramePolicy.Manual,
            PlacementProvenance = setup.GeometryKind == RigEyeGeometryKind.GlobeCandidate
                ? RigEvidenceKind.GeometryInference : RigEvidenceKind.UserOverride,
            DetectionConfidence = null,
            LockedFields = materializedRecipe?.LockedFields ?? RigHelperEditFields.None,
            UserApproved = setup.UserApproved,
            Evidence = evidence,
        };

        RigEyeSetup storedSetup = setup with
        {
            HelperEntityId = helperId,
            Evidence = MergeEvidence(setup.Evidence, [sourceEvidence]),
        };
        ImmutableArray<RigEyeSetup> nextEyes = materializedSession.Eyes
            .Where(eye => eye.Side != storedSetup.Side || eye.Mode != storedSetup.Mode)
            .Append(storedSetup)
            .ToImmutableArray();
        ImmutableArray<HelperRecipe> nextHelpers = materializedSession.Recipe.Helpers
            .Where(recipe => recipe.EntityId != helperId)
            .Append(desiredRecipe)
            .ToImmutableArray();
        ImmutableArray<RigEntityFramePolicy> nextFramePolicies = materializedSession.Recipe.FramePolicies
            .Where(policy => policy.EntityId != helperId)
            .ToImmutableArray();
        RiggingSession candidateSession = materializedSession with
        {
            Eyes = nextEyes,
            Recipe = materializedSession.Recipe with
            {
                Entities = nextEntities.ToImmutable(),
                Helpers = nextHelpers,
                FramePolicies = nextFramePolicies,
            },
        };

        if (Equal(materializedSession.Eyes, nextEyes) &&
            Equal(materializedSession.Recipe.Helpers, nextHelpers) &&
            Equal(materializedSession.Recipe.FramePolicies, nextFramePolicies) &&
            Equal(working.AuthoredHelpers, document.AuthoredHelpers))
            return document;

        candidateSession = RiggingSessions.Change(
            materializedSession, candidateSession, RiggingEditKind.Helpers);
        CustomModelDocument candidate = working with
        {
            RiggingSession = candidateSession,
            LastBuildReceipt = null,
            RigSignature = CustomModelContractSignatures.ComputeRig(working.CreateEffectiveBones()),
        };
        CustomModelDocument result = RiggingHelperMaterializer.Apply(candidate);
        result.Validate();
        return result;
    }

    private static Vector3D BoundsFor(RigEyeSetup setup)
    {
        if (setup.GeometryKind != RigEyeGeometryKind.GlobeCandidate) return Vector3D.Zero;
        double radius = setup.GlobeRadius ?? throw new InvalidDataException("A globe candidate has no fitted radius.");
        return new Vector3D(radius, radius, radius);
    }

    private static ImmutableArray<RigEvidenceReference> MergeEvidence(
        params ImmutableArray<RigEvidenceReference>[] groups)
    {
        var result = ImmutableArray.CreateBuilder<RigEvidenceReference>();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (ImmutableArray<RigEvidenceReference> group in groups)
            foreach (RigEvidenceReference evidence in group)
                if (ids.Add(evidence.Id)) result.Add(evidence);
        return result.ToImmutable();
    }

    private static void RequireLockedFieldsPreserved(
        RigEntityBinding entity,
        HelperRecipe recipe,
        string name,
        Guid parentEntityId,
        TransformMatrix localFrame,
        Vector3D halfExtents)
    {
        RigHelperEditFields locked = recipe.LockedFields;
        if (locked.HasFlag(RigHelperEditFields.Name) && !string.Equals(entity.NativeName, name, StringComparison.Ordinal))
            throw new InvalidOperationException("The eye-helper name is locked; unlock it before changing the helper identity.");
        if (locked.HasFlag(RigHelperEditFields.Parent) && recipe.ParentEntityId != parentEntityId)
            throw new InvalidOperationException("The eye-helper parent is locked; unlock it before reparenting the helper.");
        if (locked.HasFlag(RigHelperEditFields.Position) && recipe.LocalFrame.Translation != localFrame.Translation)
            throw new InvalidOperationException("The eye-helper position is locked; unlock it before moving the helper.");
        if (locked.HasFlag(RigHelperEditFields.Orientation) && WithoutTranslation(recipe.LocalFrame) != WithoutTranslation(localFrame))
            throw new InvalidOperationException("The eye-helper orientation is locked; unlock it before aiming the helper.");
        if (locked.HasFlag(RigHelperEditFields.Extents) &&
            (recipe.BoundsCenter != Vector3D.Zero || recipe.BoundsHalfExtents != halfExtents))
            throw new InvalidOperationException("The eye-helper extents are locked; unlock them before changing the marker bounds.");
    }

    private static void EnsureUniqueName(
        CustomModelDocument document,
        string name)
    {
        if (document.CreateEffectiveBones().Any(bone => string.Equals(bone.Name, name, StringComparison.OrdinalIgnoreCase)))
            throw new ArgumentException($"Hierarchy name '{name}' is already in use.", nameof(name));
    }

    private static int IndexOfEntity(ImmutableArray<RigParentObservation> observed, Guid entityId)
    {
        for (int index = 0; index < observed.Length; index++)
            if (observed[index].EntityId == entityId) return index;
        return -1;
    }

    private static int IndexOfEntity(ImmutableArray<RigEntityBinding>.Builder entities, Guid entityId)
    {
        for (int index = 0; index < entities.Count; index++)
            if (entities[index].EntityId == entityId) return index;
        return -1;
    }

    private static TransformMatrix[] ComputeGlobals(IReadOnlyList<CustomModelBone> bones)
    {
        var globals = new TransformMatrix[bones.Count];
        foreach (CustomModelBone bone in bones)
            globals[bone.Index] = bone.ParentIndex < 0
                ? bone.ExactLocalBindMatrix
                : globals[bone.ParentIndex] * bone.ExactLocalBindMatrix;
        return globals;
    }

    private static TransformMatrix WithoutTranslation(TransformMatrix matrix) =>
        matrix with { M14 = 0, M24 = 0, M34 = 0 };

    private static bool Equal<T>(T left, T right) =>
        JsonSerializer.Serialize(left, CustomModelPackageSerializer.CreateSerializerOptions()) ==
        JsonSerializer.Serialize(right, CustomModelPackageSerializer.CreateSerializerOptions());
}
