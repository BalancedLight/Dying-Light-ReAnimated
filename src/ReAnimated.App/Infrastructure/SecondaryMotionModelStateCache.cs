using ReAnimated.Core.ModelAuthoring;

namespace ReAnimated.App.Infrastructure;

public readonly record struct SecondaryMotionModelKey(Guid ProjectId, Guid ModelId, string SourceHash, string PackageHash);
public sealed record SecondaryMotionModelSelection(SecondaryMotionDefinition Definition, bool IdentityChanged, bool HasPendingEdits);

/// <summary>Retains preview edits across fresh decodes and clip switches of the same immutable model.</summary>
public sealed class SecondaryMotionModelStateCache
{
    private readonly Dictionary<SecondaryMotionModelKey, (SecondaryMotionDefinition Definition, bool Pending)> states = [];
    private SecondaryMotionModelKey? selected;
    private SecondaryMotionDefinition? loadedDefinition;
    private bool selectedPending;

    public SecondaryMotionModelSelection Select(SecondaryMotionModelKey? identity,
        SecondaryMotionDefinition packagedDefinition, SecondaryMotionDefinition currentDefinition)
    {
        ArgumentNullException.ThrowIfNull(packagedDefinition);
        ArgumentNullException.ThrowIfNull(currentDefinition);
        if (identity is { } key)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(key.SourceHash);
            ArgumentException.ThrowIfNullOrWhiteSpace(key.PackageHash);
            identity = key with { SourceHash = key.SourceHash.ToLowerInvariant(), PackageHash = key.PackageHash.ToLowerInvariant() };
        }
        if (selected == identity)
            return new(currentDefinition, false, selectedPending || !ReferenceEquals(currentDefinition, loadedDefinition));

        if (selected is { } previous)
            states[previous] = (currentDefinition, selectedPending || !ReferenceEquals(currentDefinition, loadedDefinition));

        selected = identity;
        if (identity is { } next && states.TryGetValue(next, out var saved))
        {
            loadedDefinition = saved.Definition;
            selectedPending = saved.Pending;
        }
        else
        {
            loadedDefinition = packagedDefinition;
            selectedPending = false;
        }
        return new(loadedDefinition, true, selectedPending);
    }
}
