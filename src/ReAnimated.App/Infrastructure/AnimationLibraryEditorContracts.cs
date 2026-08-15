using System.Collections.Immutable;
using System.Globalization;
using System.IO;
using System.Text;
using ReAnimated.Codecs.Models;
using ReAnimated.Core.Project;

namespace ReAnimated.App.Infrastructure;

public sealed record AnimationLibraryRetailScriptOption(
    ProjectRetailAssetIdentity Identity,
    string DisplayName)
{
    public string Label =>
        $"{DisplayName} ({Identity.ResourceName}.scr, {ShortFingerprint(Identity.ContentSha256)})";

    internal string IdentityKey => string.Join(
        "|",
        Identity.InstallFingerprint,
        Identity.ProviderId,
        Identity.ProviderPack,
        Identity.ResourceType.ToString(CultureInfo.InvariantCulture),
        Identity.ResourceIndex?.ToString(CultureInfo.InvariantCulture) ??
            string.Empty,
        Identity.ResourceName,
        Identity.ContentSha256);

    private static string ShortFingerprint(string fingerprint) =>
        string.IsNullOrWhiteSpace(fingerprint)
            ? "unverified"
            : fingerprint[..Math.Min(12, fingerprint.Length)];
}

public sealed record AnimationLibrarySequenceAssignment(
    Guid VariantId,
    string SequenceName,
    string OutputAnm2Name,
    long FrameCount,
    int FramesPerSecond);

/// <summary>
/// Complete, process-local input for the structured animation-library editor.
/// Retail options always carry their exact project fingerprint; the editor
/// never accepts a path or a name-only retail reference.
/// </summary>
public sealed record AnimationLibraryEditorRequest
{
    public required AnimationLibrarySequenceAssignment Assignment { get; init; }

    public ImmutableArray<ProjectAnimationLibrary> Libraries { get; init; } = [];

    public ImmutableArray<AnimationLibraryRetailScriptOption> RetailScripts
    { get; init; } = [];

    public Guid? SelectedLibraryId { get; init; }

    public static AnimationLibraryEditorRequest FromProject(
        DlraProject project,
        Guid variantId)
    {
        ArgumentNullException.ThrowIfNull(project);
        ProjectAnimationVariant variant = project.AnimationVariants
            .SingleOrDefault(candidate => candidate.Id == variantId)
            ?? throw new InvalidOperationException(
                "The selected animation variant no longer exists.");
        ProjectAnimationSource source = project.AnimationSources
            .SingleOrDefault(candidate => candidate.Id == variant.SourceId)
            ?? throw new InvalidOperationException(
                "The selected animation variant has no immutable source.");
        ProjectModelEntry target = project.Models
            .SingleOrDefault(candidate => candidate.Id == variant.TargetModelId)
            ?? throw new InvalidOperationException(
                "The selected animation variant has no target model.");

        string outputName = string.IsNullOrWhiteSpace(variant.OutputAnm2Name)
            ? CreateTargetSpecificOutputName(source.Name, target.Name)
            : variant.OutputAnm2Name;
        int framesPerSecond = checked((int)Math.Clamp(
            Math.Round(
                source.FrameRate.FramesPerSecond,
                MidpointRounding.AwayFromZero),
            1,
            240));

        ImmutableArray<AnimationLibraryRetailScriptOption> retailScripts =
            CollectRetailScriptOptions(project);
        var request = new AnimationLibraryEditorRequest
        {
            Assignment = new AnimationLibrarySequenceAssignment(
                variant.Id,
                SanitizeSequenceName(source.Name),
                outputName,
                source.FrameCount,
                framesPerSecond),
            Libraries = project.AnimationLibraries,
            RetailScripts = retailScripts,
            SelectedLibraryId = variant.OwningAnimationLibraryId,
        };
        AnimationLibraryAssignmentValidator.ValidateRequest(request);
        return request;
    }

    private static ImmutableArray<AnimationLibraryRetailScriptOption>
        CollectRetailScriptOptions(DlraProject project)
    {
        IEnumerable<ProjectRetailAssetIdentity> assetIdentities = project.Assets
            .Select(static asset => asset.RetailIdentity)
            .OfType<ProjectRetailAssetIdentity>()
            .Where(static identity => identity.ResourceType == 322);
        IEnumerable<ProjectRetailAssetIdentity> libraryIdentities =
            project.AnimationLibraries.SelectMany(static library =>
                library.Imports
                    .Select(static import => import.RetailScriptIdentity)
                    .OfType<ProjectRetailAssetIdentity>()
                    .AppendIfNotNull(library.ExistingScriptIdentity));

        return assetIdentities
            .Concat(libraryIdentities)
            .Select(static identity =>
                new AnimationLibraryRetailScriptOption(
                    identity,
                    identity.ResourceName))
            .GroupBy(static option => option.IdentityKey,
                StringComparer.OrdinalIgnoreCase)
            .Select(static group => group.First())
            .OrderBy(static option => option.Identity.ResourceName,
                StringComparer.OrdinalIgnoreCase)
            .ThenBy(static option => option.Identity.ProviderId,
                StringComparer.OrdinalIgnoreCase)
            .ToImmutableArray();
    }

    private static string CreateTargetSpecificOutputName(
        string sourceName,
        string targetName)
    {
        string stem = Dl1SourceModelWriter.SanitizeName(
            $"{sourceName}_{targetName}",
            96);
        return $"{stem}.anm2";
    }

    private static string SanitizeSequenceName(string sourceName) =>
        Dl1SourceModelWriter.SanitizeName(sourceName, 63);
}

public sealed record AnimationLibraryAssignmentResult(
    ImmutableArray<ProjectAnimationLibrary> Libraries,
    Guid OwningLibraryId,
    string OutputAnm2Name)
{
    public DlraProject ApplyTo(DlraProject project, Guid variantId)
    {
        ArgumentNullException.ThrowIfNull(project);
        if (variantId == Guid.Empty)
        {
            throw new ArgumentException(
                "An animation-library assignment requires a variant ID.",
                nameof(variantId));
        }

        AnimationLibraryAssignmentValidator.ValidateAssignment(
            Libraries,
            OwningLibraryId,
            OutputAnm2Name);
        if (project.AnimationVariants.All(candidate => candidate.Id != variantId))
        {
            throw new InvalidOperationException(
                "The animation variant changed while its SCR assignment was open.");
        }

        ImmutableArray<ProjectAnimationVariant> variants = project.AnimationVariants
            .Select(variant => variant.Id == variantId
                ? variant with
                {
                    OwningAnimationLibraryId = OwningLibraryId,
                    OutputAnm2Name = OutputAnm2Name,
                }
                : variant)
            .ToImmutableArray();
        ValidateOutputAndSequenceIdentities(variants, Libraries);

        DlraProject updated = project with
        {
            SchemaVersion = DlraProject.CurrentSchemaVersion,
            AnimationLibraries = Libraries,
            AnimationVariants = variants,
        };
        updated.Validate();
        return updated;
    }

    private static void ValidateOutputAndSequenceIdentities(
        ImmutableArray<ProjectAnimationVariant> variants,
        ImmutableArray<ProjectAnimationLibrary> libraries)
    {
        ProjectAnimationVariant[] assigned = variants
            .Where(static variant =>
                variant.OwningAnimationLibraryId is not null &&
                !string.IsNullOrWhiteSpace(variant.OutputAnm2Name))
            .ToArray();
        string? duplicateOutput = assigned
            .GroupBy(static variant =>
                Dl1SourceModelWriter.SanitizeName(
                    Path.GetFileNameWithoutExtension(
                        variant.OutputAnm2Name!),
                    63),
                StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(static group => group.Count() > 1)
            ?.Key;
        if (duplicateOutput is not null)
        {
            throw new InvalidOperationException(
                $"More than one animation variant owns the output resource '{duplicateOutput}'. Choose a unique target-specific ANM2 name.");
        }

        Dictionary<Guid, ProjectAnimationLibrary> byId = libraries
            .ToDictionary(static library => library.Id);
        IGrouping<string, ProjectAnimationVariant>? duplicateSequence = assigned
            .GroupBy(
                variant => string.Join(
                    "|",
                    variant.OwningAnimationLibraryId,
                    Dl1SourceModelWriter.SanitizeName(
                        variant.Name,
                        63)),
                StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(static group => group.Count() > 1);
        if (duplicateSequence is not null)
        {
            ProjectAnimationVariant first = duplicateSequence.First();
            string library = byId.TryGetValue(
                    first.OwningAnimationLibraryId!.Value,
                    out ProjectAnimationLibrary? owner)
                ? owner.DisplayName
                : first.OwningAnimationLibraryId.Value.ToString("N");
            throw new InvalidOperationException(
                $"Animation library '{library}' has more than one project clip named '{first.Name}'. Rename a clip before assigning it; Replace existing applies only to an imported or preserved base sequence.");
        }
    }
}

public static class AnimationLibraryAssignmentValidator
{
    public const int MaximumLibraries = 256;
    public const int MaximumImportsPerLibrary = 128;

    public static void ValidateRequest(AnimationLibraryEditorRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Libraries.IsDefault ||
            request.RetailScripts.IsDefault ||
            request.Assignment.VariantId == Guid.Empty ||
            string.IsNullOrWhiteSpace(request.Assignment.SequenceName) ||
            request.Assignment.FrameCount <= 0 ||
            request.Assignment.FramesPerSecond is < 1 or > 240)
        {
            throw new ArgumentException(
                "The animation-library editor request is incomplete.",
                nameof(request));
        }

        ValidateOutputName(request.Assignment.OutputAnm2Name);
        ValidateExactDl1Name(
            request.Assignment.SequenceName,
            63,
            "animation sequence");
        ValidateLibraries(request.Libraries);
        if (request.SelectedLibraryId is { } selectedId &&
            request.Libraries.All(library => library.Id != selectedId))
        {
            throw new InvalidOperationException(
                "The selected animation library no longer exists.");
        }

        foreach (AnimationLibraryRetailScriptOption option in
                 request.RetailScripts)
        {
            ValidateRetailIdentity(option.Identity);
        }

        string? duplicateRetail = request.RetailScripts
            .GroupBy(static option => option.IdentityKey,
                StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(static group => group.Count() > 1)
            ?.Key;
        if (duplicateRetail is not null)
        {
            throw new InvalidOperationException(
                "The animation-library editor received duplicate fingerprinted retail script identities.");
        }
    }

    public static void ValidateAssignment(
        ImmutableArray<ProjectAnimationLibrary> libraries,
        Guid owningLibraryId,
        string outputAnm2Name)
    {
        ValidateLibraries(libraries);
        if (owningLibraryId == Guid.Empty ||
            libraries.All(library => library.Id != owningLibraryId))
        {
            throw new InvalidOperationException(
                "Choose an existing project animation library for this variant.");
        }

        ValidateOutputName(outputAnm2Name);
    }

    public static void ValidateLibraries(
        ImmutableArray<ProjectAnimationLibrary> libraries)
    {
        if (libraries.IsDefault || libraries.Length > MaximumLibraries)
        {
            throw new InvalidOperationException(
                $"A project may contain at most {MaximumLibraries:N0} animation libraries.");
        }

        Dictionary<Guid, ProjectAnimationLibrary> byId;
        try
        {
            byId = libraries.ToDictionary(static library => library.Id);
        }
        catch (ArgumentException exception)
        {
            throw new InvalidOperationException(
                "Project animation-library IDs must be unique.",
                exception);
        }

        string? duplicateResource = libraries
            .GroupBy(static library => library.ResourceName,
                StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(static group => group.Count() > 1)
            ?.Key;
        if (duplicateResource is not null)
        {
            throw new InvalidOperationException(
                $"More than one project animation library uses the type-322 resource name '{duplicateResource}'.");
        }

        foreach (ProjectAnimationLibrary library in libraries)
        {
            ValidateLibrary(library, byId);
        }

        var states = new Dictionary<Guid, byte>();
        foreach (Guid id in byId.Keys.Order())
        {
            Visit(id);
        }

        void Visit(Guid id)
        {
            if (states.GetValueOrDefault(id) == 2)
            {
                return;
            }

            if (states.GetValueOrDefault(id) == 1)
            {
                throw new InvalidOperationException(
                    "Project animation-library imports contain a cycle. Remove one of the highlighted project-library imports.");
            }

            states[id] = 1;
            foreach (Guid dependency in byId[id].Imports
                         .Where(static import =>
                             import.Kind ==
                                 ProjectAnimationLibraryImportKind.ProjectLibrary)
                         .Select(static import =>
                             import.ProjectLibraryId!.Value))
            {
                Visit(dependency);
            }

            states[id] = 2;
        }
    }

    public static string BuildGeneratedSourcePreview(
        ProjectAnimationLibrary library,
        IReadOnlyDictionary<Guid, ProjectAnimationLibrary> libraries,
        AnimationLibrarySequenceAssignment assignment)
    {
        ArgumentNullException.ThrowIfNull(library);
        ArgumentNullException.ThrowIfNull(libraries);
        ArgumentNullException.ThrowIfNull(assignment);
        ValidateOutputName(assignment.OutputAnm2Name);

        var builder = new StringBuilder();
        builder.Append("// Type-322 resource: ")
            .Append(library.ResourceName)
            .Append(".scr\n");
        if (library.Mode ==
            ProjectAnimationLibraryMode.ExistingScriptExtension)
        {
            builder.Append("// Preserved base: ")
                .Append(library.ExistingScriptIdentity?.ResourceName ??
                    "select a fingerprinted retail script")
                .Append(".scr\n");
        }

        builder.Append("// Collision policy: ")
            .Append(library.CollisionPolicy ==
                ProjectAnimationSequenceCollisionPolicy.ReplaceExisting
                ? "Replace existing sequence explicitly"
                : "Add new sequence; reject collisions")
            .Append('\n');
        foreach (ProjectAnimationLibraryImport import in library.Imports)
        {
            string resourceName = import.Kind ==
                ProjectAnimationLibraryImportKind.ProjectLibrary
                ? libraries.TryGetValue(
                    import.ProjectLibraryId!.Value,
                    out ProjectAnimationLibrary? dependency)
                    ? dependency.ResourceName
                    : $"missing-project-library-{import.ProjectLibraryId:N}"
                : import.RetailScriptIdentity!.ResourceName;
            builder.Append("!include(\"")
                .Append(EscapeScrString(resourceName))
                .Append(".scr\")\n");
        }

        builder.Append("SeqTrack( \"")
            .Append(EscapeScrString(assignment.SequenceName))
            .Append("\", \"")
            .Append(EscapeScrString(assignment.OutputAnm2Name))
            .Append("\", 0, ")
            .Append((assignment.FrameCount - 1).ToString(
                CultureInfo.InvariantCulture))
            .Append(", ")
            .Append(assignment.FramesPerSecond.ToString(
                CultureInfo.InvariantCulture))
            .Append(", 1, 0.5 )\n");
        return builder.ToString();
    }

    private static string EscapeScrString(string value) =>
        value.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal);

    private static void ValidateLibrary(
        ProjectAnimationLibrary library,
        Dictionary<Guid, ProjectAnimationLibrary> libraries)
    {
        if (library.Id == Guid.Empty ||
            string.IsNullOrWhiteSpace(library.DisplayName) ||
            library.DisplayName.Length > 256 ||
            !Enum.IsDefined(library.Mode) ||
            !Enum.IsDefined(library.CollisionPolicy) ||
            library.Imports.IsDefault ||
            library.Imports.Length > MaximumImportsPerLibrary)
        {
            throw new InvalidOperationException(
                "An animation library has an invalid ID, display name, mode, import list, or collision policy.");
        }

        ValidateResourceName(library.ResourceName);
        if (library.Mode ==
            ProjectAnimationLibraryMode.ExistingScriptExtension)
        {
            if (library.ExistingScriptIdentity is not { } existing)
            {
                throw new InvalidOperationException(
                    $"Animation library '{library.DisplayName}' extends an existing script but has no fingerprinted base resource.");
            }

            ValidateRetailIdentity(existing);
            if (!string.Equals(
                    existing.ResourceName,
                    library.ResourceName,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"Animation library '{library.DisplayName}' must keep the resource name of its fingerprinted base script.");
            }
        }
        else if (library.ExistingScriptIdentity is not null)
        {
            throw new InvalidOperationException(
                $"Custom additive library '{library.DisplayName}' cannot carry a retail base-script identity.");
        }

        var projectIds = new HashSet<Guid>();
        var retailKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var importedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            library.ResourceName,
        };
        foreach (ProjectAnimationLibraryImport import in library.Imports)
        {
            switch (import.Kind)
            {
                case ProjectAnimationLibraryImportKind.ProjectLibrary
                    when import.ProjectLibraryId is { } projectId &&
                         projectId != Guid.Empty &&
                         import.RetailScriptIdentity is null:
                    if (!libraries.TryGetValue(
                            projectId,
                            out ProjectAnimationLibrary? dependency))
                    {
                        throw new InvalidOperationException(
                            $"Animation library '{library.DisplayName}' imports a missing project library.");
                    }

                    if (!projectIds.Add(projectId))
                    {
                        throw new InvalidOperationException(
                            $"Animation library '{library.DisplayName}' imports '{dependency.DisplayName}' more than once.");
                    }

                    if (!importedNames.Add(dependency.ResourceName))
                    {
                        throw new InvalidOperationException(
                            $"Animation library '{library.DisplayName}' has duplicate or self-referential imported script identity '{dependency.ResourceName}'.");
                    }

                    break;
                case ProjectAnimationLibraryImportKind.RetailScript
                    when import.ProjectLibraryId is null &&
                         import.RetailScriptIdentity is { } retail:
                    ValidateRetailIdentity(retail);
                    string retailKey = RetailIdentityKey(retail);
                    if (!retailKeys.Add(retailKey))
                    {
                        throw new InvalidOperationException(
                            $"Animation library '{library.DisplayName}' imports the same fingerprinted retail script more than once.");
                    }

                    if (!importedNames.Add(retail.ResourceName))
                    {
                        throw new InvalidOperationException(
                            $"Animation library '{library.DisplayName}' has duplicate or self-referential imported script identity '{retail.ResourceName}'.");
                    }

                    break;
                default:
                    throw new InvalidOperationException(
                        $"Animation library '{library.DisplayName}' has an incomplete import identity.");
            }
        }
    }

    private static void ValidateResourceName(string name)
    {
        if (string.IsNullOrWhiteSpace(name) ||
            name.Length > 128 ||
            name is "." or ".." ||
            Path.IsPathRooted(name) ||
            Path.HasExtension(name) ||
            name.IndexOfAny(['/', '\\', ':']) >= 0)
        {
            throw new InvalidOperationException(
                "Animation-library resource names must be extensionless single path components of at most 128 characters.");
        }

        ValidateExactDl1Name(name, 63, "animation-library resource");
    }

    private static void ValidateOutputName(string outputName)
    {
        if (string.IsNullOrWhiteSpace(outputName) ||
            outputName.Length > 132 ||
            outputName is ".anm2" ||
            Path.IsPathRooted(outputName) ||
            outputName.IndexOfAny(['/', '\\', ':']) >= 0 ||
            !string.Equals(
                Path.GetExtension(outputName),
                ".anm2",
                StringComparison.OrdinalIgnoreCase) ||
            Path.GetFileName(outputName) != outputName)
        {
            throw new InvalidOperationException(
                "The target-specific animation output must be one .anm2 filename without a directory.");
        }

        ValidateExactDl1Name(
            Path.GetFileNameWithoutExtension(outputName),
            63,
            "target-specific animation output");
    }

    private static void ValidateExactDl1Name(
        string name,
        int maximumUtf8Bytes,
        string description)
    {
        string normalized = Dl1SourceModelWriter.SanitizeName(
            name,
            maximumUtf8Bytes);
        if (!string.Equals(name, normalized, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"The {description} '{name}' is not an exact DL1 resource identity. Use only ASCII letters, digits, and underscores within {maximumUtf8Bytes:N0} bytes; output names are never silently renamed.");
        }
    }

    private static void ValidateRetailIdentity(
        ProjectRetailAssetIdentity identity)
    {
        if (identity.ResourceType != 322 ||
            string.IsNullOrWhiteSpace(identity.InstallFingerprint) ||
            string.IsNullOrWhiteSpace(identity.ProviderId) ||
            string.IsNullOrWhiteSpace(identity.ProviderPack) ||
            Path.IsPathRooted(identity.ProviderPack) ||
            identity.ProviderPack
                .Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries)
                .Any(static segment => segment == "..") ||
            string.IsNullOrWhiteSpace(identity.ResourceName) ||
            identity.ResourceIndex < 0 ||
            identity.ContentSha256.Length != 64 ||
            identity.ContentSha256.Any(static character =>
                !Uri.IsHexDigit(character)))
        {
            throw new InvalidOperationException(
                "Retail animation-script imports require an exact fingerprinted type-322 identity.");
        }
    }

    private static string RetailIdentityKey(
        ProjectRetailAssetIdentity identity) => string.Join(
        "|",
        identity.InstallFingerprint,
        identity.ProviderId,
        identity.ProviderPack,
        identity.ResourceType.ToString(CultureInfo.InvariantCulture),
        identity.ResourceIndex?.ToString(CultureInfo.InvariantCulture) ??
            string.Empty,
        identity.ResourceName,
        identity.ContentSha256);
}

internal static class AnimationLibraryEnumerableExtensions
{
    public static IEnumerable<T> AppendIfNotNull<T>(
        this IEnumerable<T> values,
        T? value)
        where T : class =>
        value is null ? values : values.Append(value);
}
