using System.Collections.Immutable;
using ReAnimated.Codecs.Rp6l;

namespace ReAnimated.Codecs.Models;

public sealed record Dl1ProjectAnimationRpackConflict(
    string RelativePath,
    ImmutableArray<string> ResourceIdentities);

public static partial class Dl1DeveloperToolsProjectDeployer
{
    public const int MaximumProjectAnimationRpackCount = 4_096;

    public static async Task<ImmutableArray<Dl1ProjectAnimationRpackConflict>>
        ScanProjectAnimationRpackConflictsAsync(
            string projectRoot,
            string animationLibraryName,
            IEnumerable<string> animationNames,
            CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(animationLibraryName);
        ArgumentNullException.ThrowIfNull(animationNames);
        string root = Path.GetFullPath(projectRoot);
        if (!Directory.Exists(root))
        {
            throw new DirectoryNotFoundException(
                $"Developer Tools project directory does not exist: {root}");
        }

        RejectRpackScanReparsePoint(root);
        ImmutableHashSet<string> targetAnimations = animationNames
            .Select(static name => name?.Trim() ?? string.Empty)
            .Where(static name => name.Length > 0)
            .ToImmutableHashSet(StringComparer.OrdinalIgnoreCase);
        if (targetAnimations.IsEmpty)
        {
            throw new InvalidDataException("Animation RPack conflict scan has no animation identities.");
        }

        string[] roots = ["data", "assets_pc"];
        string[] packs = roots
            .Select(relative => Path.Combine(root, relative))
            .Where(Directory.Exists)
            .SelectMany(EnumerateRpackFilesWithoutReparsePoints)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (packs.Length > MaximumProjectAnimationRpackCount)
        {
            throw new InvalidDataException(
                $"The project contains more than {MaximumProjectAnimationRpackCount:N0} active RPack files; " +
                "animation identity conflict scanning stopped before deployment.");
        }

        var conflicts = ImmutableArray.CreateBuilder<Dl1ProjectAnimationRpackConflict>();
        var limits = new Rp6lLimits
        {
            MaximumTableCount = 1_000_000,
            MaximumNameBlobBytes = 32 * 1024 * 1024,
            MaximumTableBytes = 128 * 1024 * 1024,
            MaximumLogicalChunkBytes = 512L * 1024L * 1024L,
            MaximumStoredChunkBytes = 512L * 1024L * 1024L,
            MaximumItemBytes = 256 * 1024 * 1024,
        };
        foreach (string path in packs)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RejectRpackScanReparsePoint(path);
            Rp6lArchive archive;
            try
            {
                archive = await Rp6lArchive.OpenAsync(
                    path,
                    limits,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (
                exception is InvalidDataException or IOException or UnauthorizedAccessException)
            {
                string relative = CanonicalRpackProjectRelativePath(root, path);
                throw new InvalidDataException(
                    $"Active project RPack '{relative}' could not be inspected for animation identity conflicts.",
                    exception);
            }

            ImmutableArray<string> matched = archive.Resources
                .Where(resource =>
                    resource.ResourceType == Rp6lResourceTypes.Animation &&
                    targetAnimations.Contains(resource.Name) ||
                    resource.ResourceType == Rp6lResourceTypes.AnimationScript &&
                    string.Equals(
                        resource.Name,
                        animationLibraryName,
                        StringComparison.OrdinalIgnoreCase))
                .Select(static resource => $"type-{resource.ResourceType}:{resource.Name}")
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Order(StringComparer.OrdinalIgnoreCase)
                .ToImmutableArray();
            if (!matched.IsEmpty)
            {
                conflicts.Add(new Dl1ProjectAnimationRpackConflict(
                    CanonicalRpackProjectRelativePath(root, path),
                    matched));
            }
        }

        return conflicts.ToImmutable();
    }

    private static IEnumerable<string> EnumerateRpackFilesWithoutReparsePoints(string root)
    {
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.TryPop(out string? directory))
        {
            RejectRpackScanReparsePoint(directory);
            foreach (string file in Directory.EnumerateFiles(directory, "*.rpack", SearchOption.TopDirectoryOnly))
            {
                RejectRpackScanReparsePoint(file);
                yield return file;
            }

            foreach (string child in Directory.EnumerateDirectories(directory))
            {
                RejectRpackScanReparsePoint(child);
                pending.Push(child);
            }
        }
    }

    private static string CanonicalRpackProjectRelativePath(string projectRoot, string path) =>
        Path.GetRelativePath(projectRoot, path).Replace('\\', '/');

    private static void RejectRpackScanReparsePoint(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidDataException(
                $"Animation RPack scan refuses reparse point '{path}'.");
        }
    }
}
