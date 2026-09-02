using System.Collections.Immutable;
using ReAnimated.Core.Domain;
using ReAnimated.Core.ModelAuthoring;
using ReAnimated.Retargeting.Mapping;

namespace ReAnimated.Retargeting.Conformance;

/// <summary>
/// Options controlling how an arbitrary source rig is joined to a DL1 target
/// template.
/// </summary>
public sealed record RigCorrespondenceOptions
{
    /// <summary>
    /// When false (the default) source bones with no DL1 counterpart are kept
    /// as extra rows under their mapped ancestor, preserving facial, twist and
    /// share-bone deformation. When true they are dropped and their weights
    /// fold into the nearest surviving ancestor.
    /// </summary>
    public bool DropExtraBones { get; init; }

    /// <summary>
    /// Explicit user decisions keyed by shared humanoid role, taking priority
    /// over the automatic tie-break. Values are source bone names.
    /// </summary>
    public ImmutableDictionary<string, string> RoleOverrides { get; init; } =
        ImmutableDictionary<string, string>.Empty;

    /// <summary>
    /// Source bone names the user explicitly excluded regardless of
    /// <see cref="DropExtraBones"/>.
    /// </summary>
    public ImmutableHashSet<string> ExcludedSourceBones { get; init; } =
        ImmutableHashSet<string>.Empty;
}

/// <summary>
/// Joins an imported source rig to a DL1 target template by shared humanoid
/// role.
/// </summary>
/// <remarks>
/// <para>
/// The join is name-driven and deliberately conservative. Role spellings come
/// from <see cref="HumanoidBoneSemanticClassifier"/>, which both rigs already
/// share, so no parallel naming table is introduced here.
/// </para>
/// <para>
/// Twist, share and other structural bones are intentionally not auto-mapped.
/// The classifier excludes them from humanoid roles, and their positions
/// genuinely disagree across rigs - DL1's <c>l_foretwist</c> sits at the wrist
/// while a Character Creator <c>ForearmTwist01</c> sits near the elbow - so a
/// name-shaped guess would move skin weights to the wrong place. They stay
/// extra rows unless the user maps them explicitly.
/// </para>
/// </remarks>
public static class RigCorrespondenceSolver
{
    public static RigCorrespondence Solve(
        Dl1RigTemplate template,
        RigDefinition sourceRig,
        RigCorrespondenceOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(template);
        ArgumentNullException.ThrowIfNull(sourceRig);
        options ??= new RigCorrespondenceOptions();

        ImmutableArray<string?> sourceRoles = ClassifySource(sourceRig);
        Dictionary<string, List<int>> candidatesByRole = GroupByRole(sourceRoles);
        var ambiguities = ImmutableArray.CreateBuilder<RigCorrespondenceAmbiguity>();
        var claimedSource = new HashSet<int>();
        var rows = ImmutableArray.CreateBuilder<RigCorrespondenceRow>();

        foreach (Dl1RigTemplateEntity entity in template.Entities)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string? role = ResolveTemplateRole(entity);
            int chosen = -1;
            bool ambiguous = false;
            string evidence;
            double confidence;

            if (role is not null &&
                candidatesByRole.TryGetValue(role, out List<int>? candidates) &&
                candidates.Count > 0)
            {
                List<int> available = candidates
                    .Where(index =>
                        !claimedSource.Contains(index) &&
                        !options.ExcludedSourceBones.Contains(sourceRig.Bones[index].Name))
                    .ToList();
                if (available.Count > 0)
                {
                    chosen = ChooseCandidate(
                        role,
                        available,
                        sourceRig,
                        sourceRoles,
                        template,
                        entity,
                        options,
                        out string reason);
                    ambiguous = available.Count > 1;
                    if (ambiguous)
                    {
                        ambiguities.Add(new RigCorrespondenceAmbiguity(
                            role,
                            entity.Name,
                            available
                                .Select(index => sourceRig.Bones[index].Name)
                                .ToImmutableArray(),
                            sourceRig.Bones[chosen].Name,
                            reason));
                    }
                }
            }

            if (chosen >= 0)
            {
                claimedSource.Add(chosen);
                confidence = ambiguous ? 0.6 : 0.95;
                evidence =
                    $"role '{role}' joined '{sourceRig.Bones[chosen].Name}' to '{entity.Name}'";
                rows.Add(new RigCorrespondenceRow
                {
                    Disposition = RigBoneDisposition.Mapped,
                    TemplateIndex = entity.Index,
                    SourceBoneIndex = chosen,
                    Name = entity.Name,
                    TemplateName = entity.Name,
                    SourceName = sourceRig.Bones[chosen].Name,
                    Role = role,
                    Confidence = confidence,
                    Evidence = evidence,
                    WasAmbiguous = ambiguous,
                });
                continue;
            }

            rows.Add(new RigCorrespondenceRow
            {
                Disposition = RigBoneDisposition.Synthesized,
                TemplateIndex = entity.Index,
                SourceBoneIndex = -1,
                Name = entity.Name,
                TemplateName = entity.Name,
                SourceName = null,
                Role = role,
                Confidence = 1.0,
                Evidence = role is null
                    ? $"'{entity.Name}' is a DL1 structural entity with no humanoid role; generated from its template rest offset"
                    : $"no source bone carries role '{role}'; generated from its template rest offset",
                WasAmbiguous = false,
            });
        }

        foreach (BoneDefinition bone in sourceRig.Bones)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (claimedSource.Contains(bone.Index))
            {
                continue;
            }

            bool excluded =
                options.DropExtraBones ||
                options.ExcludedSourceBones.Contains(bone.Name);
            rows.Add(new RigCorrespondenceRow
            {
                Disposition = excluded
                    ? RigBoneDisposition.Dropped
                    : RigBoneDisposition.Extra,
                TemplateIndex = -1,
                SourceBoneIndex = bone.Index,
                Name = bone.Name,
                TemplateName = null,
                SourceName = bone.Name,
                Role = sourceRoles[bone.Index],
                Confidence = 1.0,
                Evidence = excluded
                    ? $"'{bone.Name}' has no DL1 counterpart and was excluded; its weights fold into the nearest surviving ancestor"
                    : $"'{bone.Name}' has no DL1 counterpart and is retained with its weights",
                WasAmbiguous = false,
            });
        }

        return new RigCorrespondence(
            template.TemplateId,
            rows.ToImmutable(),
            ambiguities.ToImmutable());
    }

    /// <summary>
    /// Prefers the template's own factory role, falling back to the shared
    /// name classifier. The factory role is authoritative because it is the
    /// spelling hashed into persisted rig signatures.
    /// </summary>
    private static string? ResolveTemplateRole(Dl1RigTemplateEntity entity) =>
        entity.SemanticRole ??
        HumanoidBoneSemanticClassifier.Classify(entity.Name)?.Role;

    private static ImmutableArray<string?> ClassifySource(RigDefinition sourceRig)
    {
        var roles = ImmutableArray.CreateBuilder<string?>(sourceRig.BoneCount);
        foreach (BoneDefinition bone in sourceRig.Bones)
        {
            roles.Add(
                bone.SemanticRole ??
                HumanoidBoneSemanticClassifier.Classify(bone.Name)?.Role);
        }

        return roles.MoveToImmutable();
    }

    private static Dictionary<string, List<int>> GroupByRole(
        ImmutableArray<string?> roles)
    {
        var grouped = new Dictionary<string, List<int>>(StringComparer.Ordinal);
        for (int index = 0; index < roles.Length; index++)
        {
            if (roles[index] is not { } role)
            {
                continue;
            }

            if (!grouped.TryGetValue(role, out List<int>? bucket))
            {
                grouped[role] = bucket = [];
            }

            bucket.Add(index);
        }

        return grouped;
    }

    /// <summary>
    /// Resolves several source bones claiming one role.
    /// </summary>
    /// <remarks>
    /// An explicit user choice always wins. Otherwise the candidate that is an
    /// ancestor of every other candidate is preferred, because the outer joint
    /// of a duplicated pair is the one that carries the limb - a Character
    /// Creator rig exposes both <c>CC_Base_Hip</c> and <c>CC_Base_Pelvis</c>
    /// as <c>body.pelvis</c>, and <c>CC_Base_Hip</c> is the bone the thighs
    /// actually hang from, matching DL1's <c>pelvis</c>. Remaining ties fall
    /// back to child-role agreement and finally to source order, and every
    /// multi-candidate role is reported for review either way.
    /// </remarks>
    private static int ChooseCandidate(
        string role,
        List<int> candidates,
        RigDefinition sourceRig,
        ImmutableArray<string?> sourceRoles,
        Dl1RigTemplate template,
        Dl1RigTemplateEntity entity,
        RigCorrespondenceOptions options,
        out string reason)
    {
        if (options.RoleOverrides.TryGetValue(role, out string? overrideName))
        {
            foreach (int candidate in candidates)
            {
                if (string.Equals(
                        sourceRig.Bones[candidate].Name,
                        overrideName,
                        StringComparison.OrdinalIgnoreCase))
                {
                    reason = "explicit user choice";
                    return candidate;
                }
            }
        }

        if (candidates.Count == 1)
        {
            reason = "single candidate";
            return candidates[0];
        }

        foreach (int candidate in candidates)
        {
            if (candidates.All(other =>
                    other == candidate ||
                    IsAncestor(sourceRig, candidate, other)))
            {
                reason =
                    $"'{sourceRig.Bones[candidate].Name}' is the common ancestor of the other candidates";
                return candidate;
            }
        }

        ImmutableHashSet<string> templateChildRoles = template
            .BuildChildIndexes(entity.Index)
            .Select(index => ResolveTemplateRole(template[index]))
            .Where(static childRole => childRole is not null)
            .Select(static childRole => childRole!)
            .ToImmutableHashSet(StringComparer.Ordinal);

        int best = candidates[0];
        int bestScore = -1;
        foreach (int candidate in candidates)
        {
            int score = 0;
            foreach (BoneDefinition bone in sourceRig.Bones)
            {
                if (bone.ParentIndex == candidate &&
                    sourceRoles[bone.Index] is { } childRole &&
                    templateChildRoles.Contains(childRole))
                {
                    score++;
                }
            }

            if (score > bestScore)
            {
                bestScore = score;
                best = candidate;
            }
        }

        reason = bestScore > 0
            ? $"'{sourceRig.Bones[best].Name}' matched {bestScore} of the template entity's child roles"
            : "no distinguishing evidence; first source bone in order was used";
        return best;
    }

    private static bool IsAncestor(
        RigDefinition rig,
        int ancestorIndex,
        int descendantIndex)
    {
        int current = rig.Bones[descendantIndex].ParentIndex;
        while (current >= 0)
        {
            if (current == ancestorIndex)
            {
                return true;
            }

            current = rig.Bones[current].ParentIndex;
        }

        return false;
    }
}
