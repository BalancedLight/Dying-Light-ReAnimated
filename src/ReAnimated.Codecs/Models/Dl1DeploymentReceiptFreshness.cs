using System.Collections.Immutable;
using System.Security.Cryptography;

namespace ReAnimated.Codecs.Models;

public sealed record Dl1DeploymentReceiptFreshness(
    bool IsStale,
    ImmutableArray<string> StaleArtifactPaths);

public static partial class Dl1DeveloperToolsProjectDeployer
{
    public static Dl1DeploymentReceiptFreshness InspectDeploymentReceiptFreshness(
        Dl1DeveloperToolsDeploymentReceipt receipt,
        string projectRoot,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        ArgumentException.ThrowIfNullOrWhiteSpace(projectRoot);
        string root = Path.GetFullPath(projectRoot);
        if (!Directory.Exists(root))
        {
            throw new DirectoryNotFoundException(
                $"Developer Tools project directory does not exist: {root}");
        }

        string rootedPrefix = root.EndsWith(Path.DirectorySeparatorChar)
            ? root
            : root + Path.DirectorySeparatorChar;
        var stale = ImmutableArray.CreateBuilder<string>();
        foreach (Dl1DeveloperToolsDeploymentReceiptArtifact artifact in receipt.Artifacts)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string relative = artifact.RelativePath.Replace('/', Path.DirectorySeparatorChar);
            string path = Path.GetFullPath(Path.Combine(root, relative));
            if (!path.StartsWith(rootedPrefix, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException(
                    $"Deployment receipt artifact '{artifact.RelativePath}' resolves outside the project.");
            }

            RejectFreshnessReparseComponents(root, path);
            if (!File.Exists(path) ||
                !string.Equals(
                    HashFreshnessFile(path),
                    artifact.DeployedSha256,
                    StringComparison.OrdinalIgnoreCase))
            {
                stale.Add(artifact.RelativePath);
            }
        }

        ImmutableArray<string> paths = stale
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToImmutableArray();
        return new Dl1DeploymentReceiptFreshness(!paths.IsEmpty, paths);
    }

    private static string HashFreshnessFile(string path)
    {
        using FileStream stream = new(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            128 * 1024,
            FileOptions.SequentialScan);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }

    private static void RejectFreshnessReparseComponents(string projectRoot, string target)
    {
        if ((File.GetAttributes(projectRoot) & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidDataException("Developer Tools project root cannot be a reparse point.");
        }

        string relative = Path.GetRelativePath(projectRoot, target);
        string current = projectRoot;
        foreach (string component in relative.Split(
                     Path.DirectorySeparatorChar,
                     StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, component);
            if ((File.Exists(current) || Directory.Exists(current)) &&
                (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidDataException(
                    $"Deployment receipt freshness check refuses reparse point '{current}'.");
            }
        }
    }
}
