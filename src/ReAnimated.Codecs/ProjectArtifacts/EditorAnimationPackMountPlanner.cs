using System.Security.Cryptography;
using ReAnimated.Codecs.Rp6l;

namespace ReAnimated.Codecs.ProjectArtifacts;

/// <summary>
/// Prepares an authored animation pack for the conventional project-data path
/// loaded by the DL1 Editor. An unrelated pack at that path is never replaced.
/// </summary>
public static class EditorAnimationPackMountPlanner
{
    public const string RelativePath = "data/common_anims_sp_PC.rpack";

    private const int MaximumReceiptsToInspect = 4096;

    public static async Task<EditorAnimationPackMountPlan> PrepareAsync(
        string projectRoot,
        Guid ownerProjectId,
        ReadOnlyMemory<byte> candidatePack,
        IReadOnlyDictionary<string, byte[]> animations,
        IReadOnlyDictionary<string, Rp6lAnimationScript> scripts,
        bool allowUnownedReplacement = false,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectRoot);
        ArgumentNullException.ThrowIfNull(animations);
        ArgumentNullException.ThrowIfNull(scripts);
        if (ownerProjectId == Guid.Empty)
            throw new ArgumentException("The owner project ID cannot be empty.", nameof(ownerProjectId));
        if (candidatePack.IsEmpty)
            throw new ArgumentException("The authored animation pack is empty.", nameof(candidatePack));

        string root = Path.GetFullPath(projectRoot);
        string destination = Path.Combine(root, "data", "common_anims_sp_PC.rpack");
        if (!File.Exists(destination))
            return new EditorAnimationPackMountPlan(candidatePack.ToArray(), false, 0);

        string currentHash;
        await using (FileStream stream = new(
                         destination,
                         FileMode.Open,
                         FileAccess.Read,
                         FileShare.Read,
                         128 * 1024,
                         FileOptions.Asynchronous | FileOptions.SequentialScan))
        {
            currentHash = Convert.ToHexStringLower(
                await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false));
        }

        string candidateHash = Convert.ToHexStringLower(SHA256.HashData(candidatePack.Span));
        if (string.Equals(currentHash, candidateHash, StringComparison.OrdinalIgnoreCase))
            return new EditorAnimationPackMountPlan(candidatePack.ToArray(), false, 0);

        bool owned = await IsOwnedActivePackAsync(
                root, ownerProjectId, currentHash, cancellationToken)
            .ConfigureAwait(false);
        if (!allowUnownedReplacement && !owned)
        {
            throw new InvalidOperationException(
                $"The existing {RelativePath} differs from this export and has no matching active receipt for this project. Use an isolated Developer Tools project or restore its prior owner before mounting the authored pack.");
        }

        await ValidatePreservableInventoryAsync(destination, cancellationToken)
            .ConfigureAwait(false);
        Rp6lAnimationLibrary existing = await Rp6lAnimationLibraryCodec.ExtractAsync(
                destination,
                cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        var mergedAnimations = new Dictionary<string, byte[]>(
            existing.Animations,
            StringComparer.OrdinalIgnoreCase);
        var mergedScripts = new Dictionary<string, Rp6lAnimationScript>(
            existing.AnimationScripts,
            StringComparer.OrdinalIgnoreCase);
        foreach ((string name, byte[] payload) in animations)
            mergedAnimations[name] = payload;
        foreach ((string name, Rp6lAnimationScript script) in scripts)
            mergedScripts[name] = script;

        byte[] mergedPack = Rp6lAnimationLibraryCodec.Build(
            mergedAnimations,
            mergedScripts);
        var updatedAnimationNames = new HashSet<string>(animations.Keys, StringComparer.OrdinalIgnoreCase);
        var updatedScriptNames = new HashSet<string>(scripts.Keys, StringComparer.OrdinalIgnoreCase);
        int preserved = existing.Animations.Keys.Count(name => !updatedAnimationNames.Contains(name)) +
                        existing.AnimationScripts.Keys.Count(name => !updatedScriptNames.Contains(name));
        return new EditorAnimationPackMountPlan(mergedPack, owned, preserved, currentHash)
        {
            ReplacesUnownedPack = !owned,
        };
    }

    private static async Task ValidatePreservableInventoryAsync(
        string path,
        CancellationToken cancellationToken)
    {
        Rp6lArchive archive;
        try
        {
            archive = await Rp6lArchive.OpenAsync(
                path,
                cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is EndOfStreamException or InvalidDataException)
        {
            throw new InvalidDataException(
                "The existing canonical animation pack is malformed or contains an unpreservable resource inventory.",
                exception);
        }
        foreach (Rp6lResourceDescriptor resource in archive.Resources)
        {
            cancellationToken.ThrowIfCancellationRequested();
            bool allowed = resource.ResourceType is
                Rp6lResourceTypes.Animation or Rp6lResourceTypes.AnimationScript ||
                resource.ResourceType == Rp6lResourceTypes.BuilderInformation &&
                resource.Name is "_ANIMATION_" or "_ANIMATION_SCR_";
            if (!allowed)
                throw new InvalidDataException(
                    $"The existing canonical animation pack contains unknown or unpreservable resource '{resource.Name}' (type {resource.ResourceType}).");
            int expectedItems = resource.ResourceType == Rp6lResourceTypes.Animation ? 1 : 2;
            if (resource.ResourceType is Rp6lResourceTypes.AnimationScript && resource.Items.Count != expectedItems ||
                resource.ResourceType == Rp6lResourceTypes.Animation && resource.Items.Count != expectedItems ||
                resource.ResourceType == Rp6lResourceTypes.BuilderInformation && resource.Items.Count != 1)
                throw new InvalidDataException(
                    $"The existing canonical animation pack resource '{resource.Name}' has an unpreservable item layout.");
        }
    }

    private static async Task<bool> IsOwnedActivePackAsync(
        string projectRoot,
        Guid ownerProjectId,
        string currentHash,
        CancellationToken cancellationToken)
    {
        string owner = ownerProjectId.ToString("N");
        string receiptDirectory = Path.Combine(
            projectRoot,
            ".dl-reanimated",
            "receipts",
            "project-artifacts",
            owner);
        if (!Directory.Exists(receiptDirectory))
            return false;

        string[] receiptPaths = Directory.GetFiles(
            receiptDirectory,
            "*.json",
            SearchOption.TopDirectoryOnly);
        if (receiptPaths.Length > MaximumReceiptsToInspect)
            throw new InvalidDataException("Too many project artifact receipts to establish animation-pack ownership.");

        foreach (string path in receiptPaths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string relative = $".dl-reanimated/receipts/project-artifacts/{owner}/{Path.GetFileName(path)}";
            ProjectArtifactTransactionReceipt receipt =
                await ProjectArtifactTransactionService.ReadReceiptAsync(
                        projectRoot,
                        ownerProjectId,
                        relative,
                        cancellationToken)
                    .ConfigureAwait(false);
            if (receipt.RolledBackUtc is not null)
                continue;
            if (receipt.Artifacts.Any(artifact =>
                    string.Equals(artifact.RelativePath, RelativePath, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(artifact.DeployedSha256, currentHash, StringComparison.OrdinalIgnoreCase)))
                return true;
        }

        return false;
    }
}

public sealed record EditorAnimationPackMountPlan(
    byte[] Payload,
    bool ReplacesOwnedPack,
    int PreservedResourceCount,
    string? ExistingSha256 = null)
{
    public bool ReplacesUnownedPack { get; init; }

    public bool ReplacesExistingPack =>
        ReplacesOwnedPack || ReplacesUnownedPack;
}
