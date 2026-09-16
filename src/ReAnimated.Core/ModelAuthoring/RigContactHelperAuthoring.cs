using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using ReAnimated.Core.Domain;
using ReAnimated.Core.Mathematics;

namespace ReAnimated.Core.ModelAuthoring;

/// <summary>
/// Commits one reviewed contact-helper placement through the existing authored
/// helper layer. Imported bones and source geometry remain immutable; the
/// materializer owns the final helper-row ordering.
/// </summary>
public static class RigContactHelperAuthoring
{
    public static CustomModelDocument Apply(
        CustomModelDocument document,
        RiggingJobToken token,
        Guid parentEntityId,
        Guid? helperEntityId,
        string name,
        string roleId,
        TransformMatrix localFrame,
        Vector3D center,
        Vector3D halfExtents,
        RigEvidenceKind provenance,
        string evidenceDetail)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(token);
        document.Validate();

        RiggingSession session = document.RiggingSession ??
            throw new InvalidOperationException(
                "Contact-helper authoring requires a current rigging session.");
        if (!session.Matches(token))
        {
            throw new InvalidOperationException(
                "Contact-helper authoring inputs changed; refresh the model and select the parent again.");
        }

        if (!session.MatchesSource(document.Source.ContentSha256))
        {
            throw new InvalidOperationException(
                "Contact-helper authoring requires a reviewed current source revision.");
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(roleId);
        ArgumentException.ThrowIfNullOrWhiteSpace(evidenceDetail);
        if (!Enum.IsDefined(provenance))
        {
            throw new ArgumentOutOfRangeException(nameof(provenance));
        }

        if (parentEntityId == Guid.Empty)
        {
            throw new ArgumentException(
                "A contact helper requires a non-empty parent entity.",
                nameof(parentEntityId));
        }

        RigRecipeRules.Affine(localFrame, nameof(localFrame));
        ValidateFinite(center, nameof(center));
        ValidatePositiveExtents(halfExtents);

        ImmutableArray<RigParentObservation> observed =
            RiggingSessions.ObserveSourceHierarchy(document);
        ImmutableArray<RigEntityBinding> entities = session.Recipe.Entities;
        RigEntityBinding parentEntity = entities.FirstOrDefault(entity =>
            entity.EntityId == parentEntityId) ??
            throw new ArgumentException(
                "The contact-helper parent is not an entity in this model.",
                nameof(parentEntityId));
        int parentNodeIndex = IndexOfEntity(observed, parentEntityId);
        if (parentNodeIndex < 0 || parentNodeIndex >= document.Bones.Length)
        {
            throw new ArgumentException(
                "A contact helper parent must be an observed source bone.",
                nameof(parentEntityId));
        }

        bool ownedBoneParent = parentEntity.Kind == RigNativeEntityKind.Bone ||
            (parentEntity.Kind == RigNativeEntityKind.Unknown &&
            document.Bones[parentNodeIndex].Kind == BoneKind.Deform);
        if (parentEntity.OwnerAssetId != document.ModelId || !ownedBoneParent)
        {
            throw new ArgumentException(
                "A contact helper must be parented to an owned observed source bone or deformation node.",
                nameof(parentEntityId));
        }

        Guid id = ResolveHelperId(
            document,
            session,
            helperEntityId,
            parentEntityId,
            name,
            roleId);
        ImmutableArray<CustomModelAuthoredHelper> authoredHelpers =
            document.AuthoredHelpers;
        CustomModelAuthoredHelper? existingAuthoring = authoredHelpers.FirstOrDefault(
            helper => helper.Id == id);
        RigEntityBinding? existingEntity = entities.FirstOrDefault(
            entity => entity.EntityId == id);
        HelperRecipe? existingRecipe = session.Recipe.Helpers.FirstOrDefault(
            helper => helper.EntityId == id);

        bool existing = existingAuthoring is not null ||
            existingEntity is not null ||
            existingRecipe is not null;
        bool preserveImportedSource = false;
        if (existing)
        {
            preserveImportedSource = RequireObservedHelper(
                document,
                observed,
                id,
                existingAuthoring,
                existingEntity,
                existingRecipe,
                name,
                parentEntityId);
            if (existingRecipe is not null)
            {
                RequireLockedFieldsPreserved(
                    existingEntity!,
                    existingRecipe,
                    name,
                    parentEntityId,
                    localFrame,
                    center,
                    halfExtents);
            }
        }

        EnsureUniqueName(document, observed, name, id);

        bool classifyUnknownDeformParent =
            parentEntity.Kind == RigNativeEntityKind.Unknown &&
            document.Bones[parentNodeIndex].Kind == BoneKind.Deform;
        bool classifyUnknownImportedHelper =
            preserveImportedSource &&
            existingEntity is not null &&
            existingEntity.Kind == RigNativeEntityKind.Unknown;
        string sourceEvidence =
            $"Observed parent source kind '{document.Bones[parentNodeIndex].Kind}' " +
            $"with reviewed authoring parent kind 'Bone'. Contact placement is " +
            $"bound to source SHA-256 {session.SourceSha256}.";
        if (preserveImportedSource)
        {
            int helperNodeIndex = IndexOfEntity(observed, id);
            sourceEvidence +=
                $" The selected imported contact has observed source kind " +
                $"'{document.Bones[helperNodeIndex].Kind}'.";
        }

        ImmutableArray<RigEvidenceReference> evidence =
        [
            new RigEvidenceReference
            {
                Id = $"contact-source:{id:N}",
                Kind = RigEvidenceKind.ImportedSource,
                ArtifactSha256 = session.SourceSha256,
                Description = sourceEvidence,
            },
            new RigEvidenceReference
            {
                Id = $"contact-placement:{id:N}",
                Kind = provenance,
                ArtifactSha256 = session.SourceSha256,
                Description = evidenceDetail,
            },
        ];
        HelperRecipe desired = new()
        {
            EntityId = id,
            OwnerAssetId = document.ModelId,
            RoleId = roleId,
            ParentEntityId = parentEntityId,
            LocalFrame = localFrame,
            BoundsCenter = center,
            BoundsHalfExtents = halfExtents,
            FramePolicy = RigFramePolicy.Contact,
            PlacementProvenance = provenance,
            UserApproved = true,
            LockedFields = existingRecipe?.LockedFields ?? RigHelperEditFields.None,
            Evidence = evidence,
        };

        if (existing && existingRecipe is not null &&
            existingEntity!.NativeName == name &&
            existingRecipe.RoleId == desired.RoleId &&
            existingRecipe.ParentEntityId == desired.ParentEntityId &&
            existingRecipe.LocalFrame == desired.LocalFrame &&
            existingRecipe.BoundsCenter == desired.BoundsCenter &&
            existingRecipe.BoundsHalfExtents == desired.BoundsHalfExtents &&
            existingRecipe.FramePolicy == desired.FramePolicy &&
            existingRecipe.PlacementProvenance == desired.PlacementProvenance &&
            existingRecipe.UserApproved == desired.UserApproved &&
            existingRecipe.LockedFields == desired.LockedFields &&
            existingRecipe.Evidence.SequenceEqual(desired.Evidence))
        {
            return document;
        }

        var nextEntities = entities.ToBuilder();
        if (classifyUnknownDeformParent)
        {
            int parentEntityIndex = IndexOfEntity(nextEntities, parentEntityId);
            nextEntities[parentEntityIndex] = parentEntity with
            {
                Kind = RigNativeEntityKind.Bone,
            };
        }
        if (existingEntity is null)
        {
            nextEntities.Add(new RigEntityBinding
            {
                EntityId = id,
                OwnerAssetId = document.ModelId,
                SourceEntityId = $"authored:{id:N}",
                NativeName = name,
                Kind = RigNativeEntityKind.Helper,
                Imported = false,
            });
        }
        else if (preserveImportedSource)
        {
            if (classifyUnknownImportedHelper)
            {
                int entityIndex = IndexOfEntity(nextEntities, id);
                nextEntities[entityIndex] = existingEntity with
                {
                    Kind = RigNativeEntityKind.Helper,
                };
            }
        }
        else
        {
            int entityIndex = IndexOfEntity(nextEntities, id);
            nextEntities[entityIndex] = existingEntity with
            {
                OwnerAssetId = document.ModelId,
                SourceEntityId = $"authored:{id:N}",
                NativeName = name,
                Kind = RigNativeEntityKind.Helper,
                Imported = false,
            };
        }

        var nextHelpers = session.Recipe.Helpers.ToBuilder();
        int recipeIndex = IndexOfHelper(nextHelpers, id);
        if (recipeIndex < 0)
        {
            nextHelpers.Add(desired);
        }
        else
        {
            nextHelpers[recipeIndex] = desired;
        }

        // A legacy standalone frame policy cannot compete with a helper recipe.
        // Preserve every other entity policy exactly.
        ImmutableArray<RigEntityFramePolicy> nextFramePolicies = session.Recipe
            .FramePolicies
            .Where(policy => policy.EntityId != id)
            .ToImmutableArray();
        RiggingSession candidateSession = session with
        {
            Recipe = session.Recipe with
            {
                Entities = nextEntities.ToImmutable(),
                Helpers = nextHelpers.ToImmutable(),
                FramePolicies = nextFramePolicies,
            },
        };
        candidateSession = preserveImportedSource
            ? RiggingSessions.Change(
                session,
                candidateSession,
                RiggingEditKind.Helpers)
            : candidateSession;
        candidateSession.Validate();

        CustomModelDocument candidate = document with
        {
            RiggingSession = candidateSession,
            LastBuildReceipt = null,
        };
        // RiggingHelperMaterializer performs the single Helpers edit transition,
        // resets dependent stage reviews, and owns effective helper ordering.
        CustomModelDocument materialized = RiggingHelperMaterializer.Apply(candidate);
        materialized.Validate();
        return materialized;
    }

    private static Guid ResolveHelperId(
        CustomModelDocument document,
        RiggingSession session,
        Guid? requested,
        Guid parentEntityId,
        string name,
        string roleId)
    {
        if (requested is { } explicitId)
        {
            if (explicitId == Guid.Empty)
            {
                throw new ArgumentException(
                    "The helper identifier cannot be empty.",
                    nameof(requested));
            }

            return explicitId;
        }

        Guid[] matches = session.Recipe.Helpers
            .Where(helper =>
                helper.OwnerAssetId == document.ModelId &&
                string.Equals(helper.RoleId, roleId, StringComparison.Ordinal) &&
                helper.ParentEntityId == parentEntityId &&
                session.Recipe.Entities.Any(entity =>
                    entity.EntityId == helper.EntityId &&
                    string.Equals(entity.NativeName, name, StringComparison.Ordinal)))
            .Select(static helper => helper.EntityId)
            .Distinct()
            .ToArray();
        if (matches.Length > 1)
        {
            throw new InvalidOperationException(
                "The contact-helper identity is ambiguous; select an existing helper explicitly.");
        }

        if (matches.Length == 1)
        {
            return matches[0];
        }

        byte[] digest = SHA256.HashData(Encoding.UTF8.GetBytes(
            string.Create(
                System.Globalization.CultureInfo.InvariantCulture,
                $"{session.Id:N}|{document.ModelId:N}|{parentEntityId:N}|{roleId}|{name}")));
        return new Guid(digest.AsSpan(0, 16));
    }

    private static bool RequireObservedHelper(
        CustomModelDocument document,
        ImmutableArray<RigParentObservation> observed,
        Guid id,
        CustomModelAuthoredHelper? authored,
        RigEntityBinding? entity,
        HelperRecipe? recipe,
        string name,
        Guid parentEntityId)
    {
        int observedIndex = IndexOfEntity(observed, id);
        if (entity is null || entity.OwnerAssetId != document.ModelId || observedIndex < 0)
        {
            throw new InvalidOperationException(
                "An existing contact helper must be an observed authored helper owned by this model.");
        }

        bool importedSource = authored is null &&
            observedIndex < document.Bones.Length &&
            entity.Imported &&
            (entity.Kind is RigNativeEntityKind.Helper or RigNativeEntityKind.Unknown) &&
            document.Bones[observedIndex].Kind == BoneKind.Helper;
        if (importedSource)
        {
            if (document.Bones[observedIndex].Name != name ||
                entity.NativeName != name ||
                observed[observedIndex].ParentEntityId != parentEntityId ||
                recipe is not null &&
                (recipe.OwnerAssetId != document.ModelId ||
                 recipe.ParentEntityId != parentEntityId))
            {
                throw new InvalidOperationException(
                    "Imported contact nodes retain their source name and parent; use a new authored helper for a different identity.");
            }

            return true;
        }

        if (authored is null ||
            entity.Imported ||
            entity.Kind != RigNativeEntityKind.Helper ||
            observedIndex < document.Bones.Length ||
            recipe is not null &&
            (recipe.OwnerAssetId != document.ModelId ||
             observed[observedIndex].ParentEntityId != recipe.ParentEntityId))
        {
            throw new InvalidOperationException(
                "An existing contact helper must be an observed authored helper owned by this model.");
        }

        return false;
    }

    private static void RequireLockedFieldsPreserved(
        RigEntityBinding entity,
        HelperRecipe recipe,
        string name,
        Guid parentEntityId,
        TransformMatrix localFrame,
        Vector3D center,
        Vector3D halfExtents)
    {
        RigHelperEditFields locked = recipe.LockedFields;
        if (locked.HasFlag(RigHelperEditFields.Name) &&
            !string.Equals(entity.NativeName, name, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "The helper name is locked; unlock it before changing the contact identity.");
        }

        if (locked.HasFlag(RigHelperEditFields.Parent) &&
            recipe.ParentEntityId != parentEntityId)
        {
            throw new InvalidOperationException(
                "The helper parent is locked; unlock it before reparenting the contact.");
        }

        if (locked.HasFlag(RigHelperEditFields.Position) &&
            recipe.LocalFrame.Translation != localFrame.Translation)
        {
            throw new InvalidOperationException(
                "The helper position is locked; unlock it before moving the contact.");
        }

        if (locked.HasFlag(RigHelperEditFields.Orientation) &&
            WithoutTranslation(recipe.LocalFrame) != WithoutTranslation(localFrame))
        {
            throw new InvalidOperationException(
                "The helper orientation is locked; unlock it before aiming the contact.");
        }

        if (locked.HasFlag(RigHelperEditFields.Extents) &&
            (recipe.BoundsCenter != center || recipe.BoundsHalfExtents != halfExtents))
        {
            throw new InvalidOperationException(
                "The helper extents are locked; unlock them before changing the contact bounds.");
        }
    }

    private static void EnsureUniqueName(
        CustomModelDocument document,
        ImmutableArray<RigParentObservation> observed,
        string name,
        Guid currentId)
    {
        if (document.CreateEffectiveBones().Any(bone =>
        {
            if (!bone.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            Guid identity = bone.Index < document.Bones.Length
                ? observed[bone.Index].EntityId
                : document.AuthoredHelpers[bone.Index - document.Bones.Length].Id;
            return identity != currentId;
        }))
        {
            throw new ArgumentException(
                $"Hierarchy name '{name}' is already in use.",
                nameof(name));
        }
    }

    private static int IndexOfEntity(
        ImmutableArray<RigParentObservation> observed,
        Guid entityId)
    {
        for (int index = 0; index < observed.Length; index++)
        {
            if (observed[index].EntityId == entityId)
            {
                return index;
            }
        }

        return -1;
    }

    private static int IndexOfEntity(
        ImmutableArray<RigEntityBinding>.Builder entities,
        Guid entityId)
    {
        for (int index = 0; index < entities.Count; index++)
        {
            if (entities[index].EntityId == entityId)
            {
                return index;
            }
        }

        return -1;
    }

    private static int IndexOfHelper(
        ImmutableArray<HelperRecipe>.Builder helpers,
        Guid entityId)
    {
        for (int index = 0; index < helpers.Count; index++)
        {
            if (helpers[index].EntityId == entityId)
            {
                return index;
            }
        }

        return -1;
    }

    private static void ValidateFinite(Vector3D value, string parameterName)
    {
        if (!value.IsFinite)
        {
            throw new ArgumentException(
                "Contact-helper bounds must be finite authoring coordinates.",
                parameterName);
        }
    }

    private static void ValidatePositiveExtents(Vector3D value)
    {
        if (!value.IsFinite || value.X <= 0.0 || value.Y <= 0.0 || value.Z <= 0.0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(value),
                "Contact-helper half extents must be strictly positive and finite.");
        }
    }

    private static TransformMatrix WithoutTranslation(TransformMatrix matrix) =>
        matrix with { M14 = 0.0, M24 = 0.0, M34 = 0.0 };
}
