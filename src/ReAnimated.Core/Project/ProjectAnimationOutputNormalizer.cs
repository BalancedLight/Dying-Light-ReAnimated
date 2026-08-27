using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;

namespace ReAnimated.Core.Project;

/// <summary>
/// Supplies stable, project-owned SCR and ANM2 identities for variants that
/// predate schema 3 or were created by an import workflow. Explicit user
/// assignments are never replaced.
/// </summary>
public static class ProjectAnimationOutputNormalizer
{
    public static DlraProject Normalize(DlraProject project)
    {
        ArgumentNullException.ThrowIfNull(project);
        if (project.Models.IsDefault ||
            project.AnimationSources.IsDefault ||
            project.AnimationVariants.IsDefault ||
            project.AnimationLibraries.IsDefault)
        {
            return project;
        }

        var libraries = project.AnimationLibraries.ToList();
        var libraryIds = libraries
            .Select(static library => library.Id)
            .ToHashSet();
        var resourceNames = libraries
            .Select(static library => library.ResourceName)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var variants = project.AnimationVariants.ToBuilder();
        var models = project.Models.ToBuilder();

        // Resolve every model/library relationship before allocating output
        // names. Multiple models may intentionally share one animation
        // library, so allocation cannot use a model-local collision set.
        for (int modelIndex = 0; modelIndex < models.Count; modelIndex++)
        {
            ProjectModelEntry model = models[modelIndex];
            int[] targetVariantIndices = Enumerable.Range(0, variants.Count)
                .Where(index =>
                    variants[index].TargetModelId == model.Id)
                .OrderBy(index => variants[index].Id)
                .ToArray();
            if (targetVariantIndices.Length == 0 || model.IsStatic)
            {
                continue;
            }

            Guid? rootLibraryId = model.RootAnimationLibraryId;
            if (rootLibraryId is null)
            {
                Guid[] assignedLibraries = targetVariantIndices
                    .Select(index =>
                        variants[index].OwningAnimationLibraryId)
                    .Where(static id => id.HasValue)
                    .Select(static id => id!.Value)
                    .Where(libraryIds.Contains)
                    .Distinct()
                    .ToArray();
                rootLibraryId = assignedLibraries.Length == 1
                    ? assignedLibraries[0]
                    : CreateDefaultLibrary(
                        project.ProjectId,
                        model,
                        libraries,
                        libraryIds,
                        resourceNames);
                models[modelIndex] = model with
                {
                    RootAnimationLibraryId = rootLibraryId,
                };
            }

            foreach (int variantIndex in targetVariantIndices)
            {
                ProjectAnimationVariant variant = variants[variantIndex];
                Guid? owningLibraryId =
                    variant.OwningAnimationLibraryId ?? rootLibraryId;
                variants[variantIndex] = variant with
                {
                    OwningAnimationLibraryId = owningLibraryId,
                };
            }
        }

        // Uniqueness is project-wide, not per library. An animation RPack
        // keys its type-320 resources by name alone, with no per-character or
        // per-script namespace, so two libraries that each allocate
        // "mixamo_com.anm2" produce a pack whose second entry would overwrite
        // the first. Scoping this per library handed out names the exporter
        // then had to reject.
        HashSet<string> usedOutputNames = variants
            .Where(static variant =>
                !string.IsNullOrWhiteSpace(variant.OutputAnm2Name))
            .Select(static variant => variant.OutputAnm2Name!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        Dictionary<Guid, string> modelNames = project.Models.ToDictionary(
            static model => model.Id,
            static model => model.Name);
        foreach (int variantIndex in Enumerable.Range(0, variants.Count)
                     .OrderBy(index => variants[index].Id))
        {
            ProjectAnimationVariant variant = variants[variantIndex];
            if (variant.OwningAnimationLibraryId is null ||
                !string.IsNullOrWhiteSpace(variant.OutputAnm2Name))
            {
                continue;
            }

            variants[variantIndex] = variant with
            {
                OutputAnm2Name = AllocateOutputName(
                    variant,
                    modelNames.GetValueOrDefault(variant.TargetModelId),
                    usedOutputNames),
            };
        }

        return project with
        {
            Models = models.ToImmutable(),
            AnimationVariants = variants.ToImmutable(),
            AnimationLibraries = libraries.ToImmutableArray(),
        };
    }

    private static Guid CreateDefaultLibrary(
        Guid projectId,
        ProjectModelEntry model,
        List<ProjectAnimationLibrary> libraries,
        HashSet<Guid> libraryIds,
        HashSet<string> resourceNames)
    {
        string stem = SanitizeIdentity(model.Name, 52);
        if (string.IsNullOrWhiteSpace(stem))
        {
            stem = "model";
        }

        string resourceName = stem + "_animations";
        if (!resourceNames.Add(resourceName))
        {
            string suffix = model.Id.ToString("N")[..8];
            resourceName =
                SanitizeIdentity(stem, 54) + "_" + suffix;
            int collision = 2;
            while (!resourceNames.Add(resourceName))
            {
                string ordinal = "_" + collision++;
                resourceName =
                    SanitizeIdentity(stem, 63 - suffix.Length -
                        ordinal.Length - 1) + "_" + suffix + ordinal;
            }
        }

        Guid libraryId = CreateDeterministicGuid(
            "dlra-schema3-default-animation-library-v1",
            projectId.ToString("N"),
            model.Id.ToString("N"));
        int salt = 2;
        while (!libraryIds.Add(libraryId))
        {
            libraryId = CreateDeterministicGuid(
                "dlra-schema3-default-animation-library-v1",
                projectId.ToString("N"),
                model.Id.ToString("N"),
                (salt++).ToString(
                    System.Globalization.CultureInfo.InvariantCulture));
        }

        libraries.Add(new ProjectAnimationLibrary
        {
            Id = libraryId,
            ResourceName = resourceName,
            DisplayName = $"{model.Name} animations",
            Mode = ProjectAnimationLibraryMode.CustomAdditive,
            CollisionPolicy =
                ProjectAnimationSequenceCollisionPolicy.Reject,
        });
        return libraryId;
    }

    /// <summary>
    /// Allocates a project-unique <c>.anm2</c> identity for a variant.
    /// </summary>
    /// <remarks>
    /// The target model is folded in before any opaque GUID suffix, because
    /// the usual reason two variants collide is that one source was retargeted
    /// onto several characters. "Thriller_zombie_man_a.anm2" tells the author
    /// which row it belongs to; "Thriller_3f9a1c02.anm2" does not.
    /// </remarks>
    private static string AllocateOutputName(
        ProjectAnimationVariant variant,
        string? targetModelName,
        HashSet<string> usedNames)
    {
        string stem = SanitizeIdentity(variant.Name, 63);
        if (string.IsNullOrWhiteSpace(stem))
        {
            stem = "animation";
        }

        string output = stem + ".anm2";
        if (usedNames.Add(output))
        {
            return output;
        }

        if (!string.IsNullOrWhiteSpace(targetModelName))
        {
            string qualified = SanitizeIdentity(
                $"{stem}_{targetModelName}",
                63);
            if (!string.IsNullOrWhiteSpace(qualified))
            {
                output = qualified + ".anm2";
                if (usedNames.Add(output))
                {
                    return output;
                }
            }
        }

        string suffix = variant.Id.ToString("N")[..8];
        output = SanitizeIdentity(stem, 54) + "_" + suffix + ".anm2";
        int collision = 2;
        while (!usedNames.Add(output))
        {
            string ordinal = "_" + collision++;
            output = SanitizeIdentity(
                    stem,
                    63 - suffix.Length - ordinal.Length - 1) +
                "_" + suffix + ordinal + ".anm2";
        }

        return output;
    }

    private static string SanitizeIdentity(string value, int maximumLength)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumLength);
        var result = new StringBuilder(Math.Min(value.Length, maximumLength));
        bool previousUnderscore = false;
        foreach (char character in value.Trim())
        {
            char normalized = character is >= 'A' and <= 'Z' or
                                  >= 'a' and <= 'z' or
                                  >= '0' and <= '9' or '_'
                ? character
                : '_';
            if (normalized == '_' && previousUnderscore)
            {
                continue;
            }

            result.Append(normalized);
            previousUnderscore = normalized == '_';
            if (result.Length == maximumLength)
            {
                break;
            }
        }

        return result.ToString().Trim('_');
    }

    private static Guid CreateDeterministicGuid(params string[] parts)
    {
        byte[] digest = SHA256.HashData(
            Encoding.UTF8.GetBytes(string.Join("|", parts)));
        Span<byte> guidBytes = digest.AsSpan(0, 16);
        guidBytes[7] = (byte)((guidBytes[7] & 0x0f) | 0x50);
        guidBytes[8] = (byte)((guidBytes[8] & 0x3f) | 0x80);
        return new Guid(guidBytes);
    }
}
