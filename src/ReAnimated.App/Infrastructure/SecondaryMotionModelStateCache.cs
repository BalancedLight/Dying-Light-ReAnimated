using System.Collections.Immutable;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using ReAnimated.Core.ModelAuthoring;

namespace ReAnimated.App.Infrastructure;

public readonly record struct SecondaryMotionModelKey(Guid ProjectId, Guid ModelId, string SourceHash, string PackageHash, string RigSignature);
public sealed record SecondaryMotionModelSelection(SecondaryMotionDefinition Definition, bool IdentityChanged, bool HasPendingEdits);

public sealed record PendingSecondaryMotionEdit
{
    public Guid ProjectId { get; init; }
    public Guid ModelId { get; init; }
    public string SourceHash { get; init; } = string.Empty;
    public string PackageHash { get; init; } = string.Empty;
    public string RigSignature { get; init; } = string.Empty;
    public string BaselineDefinitionSha256 { get; init; } = string.Empty;
    public string DefinitionSha256 { get; init; } = string.Empty;
    public SecondaryMotionDefinition Definition { get; init; } = new();

    public SecondaryMotionModelKey Key => new(ProjectId, ModelId, SourceHash, PackageHash, RigSignature);

    public static PendingSecondaryMotionEdit Create(
        SecondaryMotionModelKey key,
        SecondaryMotionDefinition definition,
        SecondaryMotionDefinition? baselineDefinition = null,
        string? baselineDefinitionSha256 = null)
    {
        ArgumentNullException.ThrowIfNull(definition);
        var normalized = SecondaryMotionModelStateCache.NormalizeKey(key);
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(definition);
        if (bytes.Length > MaximumDefinitionBytes)
            throw new InvalidDataException("A pending secondary-motion definition exceeds its recovery size limit.");
        string baselineHash;
        if (baselineDefinitionSha256 is not null)
        {
            if (!IsSha256(baselineDefinitionSha256))
                throw new InvalidDataException("A pending secondary-motion baseline has an invalid hash.");
            baselineHash = baselineDefinitionSha256.ToLowerInvariant();
        }
        else
        {
            byte[] baselineBytes = JsonSerializer.SerializeToUtf8Bytes(baselineDefinition ?? definition);
            if (baselineBytes.Length > MaximumDefinitionBytes)
                throw new InvalidDataException("A pending secondary-motion baseline exceeds its recovery size limit.");
            baselineHash = Convert.ToHexStringLower(SHA256.HashData(baselineBytes));
        }
        return new()
        {
            ProjectId = normalized.ProjectId,
            ModelId = normalized.ModelId,
            SourceHash = normalized.SourceHash,
            PackageHash = normalized.PackageHash,
            RigSignature = normalized.RigSignature,
            BaselineDefinitionSha256 = baselineHash,
            DefinitionSha256 = Convert.ToHexStringLower(SHA256.HashData(bytes)),
            Definition = definition,
        };
    }

    public const int MaximumDefinitionBytes = 16 * 1024 * 1024;

    public static string ComputeDefinitionSha256(SecondaryMotionDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(definition);
        if (bytes.Length > MaximumDefinitionBytes)
            throw new InvalidDataException("A secondary-motion definition exceeds its recovery size limit.");
        return Convert.ToHexStringLower(SHA256.HashData(bytes));
    }

    public static ImmutableArray<PendingSecondaryMotionEdit> ValidateSnapshotEdits(
        IEnumerable<PendingSecondaryMotionEdit>? edits)
    {
        PendingSecondaryMotionEdit[] rows = edits?.ToArray() ?? [];
        if (rows.Length > 256)
            throw new InvalidDataException("A recovery snapshot contains too many pending secondary-motion models.");
        var identities = new HashSet<SecondaryMotionModelKey>();
        long totalBytes = 0;
        foreach (PendingSecondaryMotionEdit edit in rows)
        {
            ArgumentNullException.ThrowIfNull(edit);
            edit.Validate();
            totalBytes += JsonSerializer.SerializeToUtf8Bytes(edit.Definition).Length;
            if (totalBytes > 64L * 1024 * 1024)
                throw new InvalidDataException("Pending secondary-motion definitions exceed the recovery size limit.");
            if (!identities.Add(SecondaryMotionModelStateCache.NormalizeKey(edit.Key)))
                throw new InvalidDataException("A recovery snapshot contains duplicate pending secondary-motion model identities.");
        }
        return rows.ToImmutableArray();
    }

    public void Validate()
    {
        _ = SecondaryMotionModelStateCache.NormalizeKey(Key);
        if (!IsSha256(BaselineDefinitionSha256) || !IsSha256(DefinitionSha256))
            throw new InvalidDataException("A pending secondary-motion definition has an invalid hash.");
        if (Definition is null)
            throw new InvalidDataException("A pending secondary-motion definition is missing.");
        try { Definition.Validate(); }
        catch (ArgumentException exception)
        { throw new InvalidDataException("A pending secondary-motion definition is invalid.", exception); }
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(Definition);
        if (bytes.Length > MaximumDefinitionBytes ||
            !Convert.ToHexStringLower(SHA256.HashData(bytes)).Equals(DefinitionSha256, StringComparison.Ordinal))
            throw new InvalidDataException("A pending secondary-motion definition failed its SHA-256 check.");
    }

    private static bool IsSha256(string? value) => value is { Length: 64 } &&
        value.All(static character => character is >= '0' and <= '9' or >= 'a' and <= 'f');
}

/// <summary>Retains preview edits across fresh decodes and clip switches of the same immutable model.</summary>
public sealed class SecondaryMotionModelStateCache
{
    private sealed record State(SecondaryMotionDefinition Definition, string BaselineDefinitionSha256, bool Pending);

    private readonly Dictionary<SecondaryMotionModelKey, State> states = [];
    private SecondaryMotionModelKey? selected;
    private SecondaryMotionDefinition? loadedDefinition;
    private string? loadedDefinitionSha256;
    private SecondaryMotionDefinition? lastEvaluatedDefinition;
    private bool lastEvaluatedPending;
    private bool selectedPending;

    public SecondaryMotionModelSelection Select(SecondaryMotionModelKey? identity,
        SecondaryMotionDefinition packagedDefinition, SecondaryMotionDefinition currentDefinition)
    {
        ArgumentNullException.ThrowIfNull(packagedDefinition);
        ArgumentNullException.ThrowIfNull(currentDefinition);
        if (identity is { } key) identity = NormalizeKey(key);
        if (selected == identity)
            return new(currentDefinition, false, IsSelectedPending(currentDefinition));

        StoreSelected(currentDefinition);
        selected = identity;
        loadedDefinition = packagedDefinition;
        loadedDefinitionSha256 = HashDefinition(packagedDefinition);
        lastEvaluatedDefinition = null;
        if (identity is { } next && states.TryGetValue(next, out State? saved) && saved.Pending)
        {
            selectedPending = true;
            return new(saved.Definition, true, true);
        }

        if (identity is { } compatible && TryTakeCompatibleState(compatible, loadedDefinitionSha256, out State? compatibleState))
        {
            selectedPending = compatibleState.Pending;
            states[compatible] = compatibleState;
            return new(compatibleState.Definition, true, compatibleState.Pending);
        }

        selectedPending = false;
        return new(packagedDefinition, true, false);
    }

    public ImmutableArray<PendingSecondaryMotionEdit> CapturePendingEdits(SecondaryMotionDefinition currentDefinition)
    {
        ArgumentNullException.ThrowIfNull(currentDefinition);
        StoreSelected(currentDefinition);
        return states.Where(static pair => pair.Value.Pending)
            .OrderBy(static pair => pair.Key.ProjectId)
            .ThenBy(static pair => pair.Key.ModelId)
            .Select(static pair => PendingSecondaryMotionEdit.Create(pair.Key, pair.Value.Definition,
                baselineDefinitionSha256: pair.Value.BaselineDefinitionSha256))
            .ToImmutableArray();
    }

    public bool HasPendingEdits(SecondaryMotionDefinition currentDefinition)
    {
        ArgumentNullException.ThrowIfNull(currentDefinition);
        StoreSelected(currentDefinition);
        return states.Values.Any(static state => state.Pending);
    }

    public void RestorePendingEdits(IEnumerable<PendingSecondaryMotionEdit> edits)
    {
        ArgumentNullException.ThrowIfNull(edits);
        var replacement = new Dictionary<SecondaryMotionModelKey, State>();
        foreach (PendingSecondaryMotionEdit edit in PendingSecondaryMotionEdit.ValidateSnapshotEdits(edits))
        {
            SecondaryMotionModelKey key = NormalizeKey(edit.Key);
            if (!replacement.TryAdd(key, new(edit.Definition, edit.BaselineDefinitionSha256, true)))
                throw new InvalidDataException("A recovery snapshot contains duplicate pending secondary-motion model identities.");
        }

        states.Clear();
        foreach ((SecondaryMotionModelKey key, State state) in replacement) states.Add(key, state);
        selected = null;
        loadedDefinition = null;
        loadedDefinitionSha256 = null;
        lastEvaluatedDefinition = null;
        selectedPending = false;
    }

    public void MarkPersisted(IEnumerable<PendingSecondaryMotionEdit> edits, SecondaryMotionDefinition currentDefinition)
    {
        ArgumentNullException.ThrowIfNull(edits);
        ArgumentNullException.ThrowIfNull(currentDefinition);
        foreach (PendingSecondaryMotionEdit edit in edits)
        {
            edit.Validate();
            SecondaryMotionModelKey key = NormalizeKey(edit.Key);
            if (states.TryGetValue(key, out State? state) && DefinitionsEqual(state.Definition, edit.Definition))
                states.Remove(key);
            if (selected == key)
            {
                string persistedDefinitionSha256 = HashDefinition(edit.Definition);
                loadedDefinition = edit.Definition;
                loadedDefinitionSha256 = persistedDefinitionSha256;
                lastEvaluatedDefinition = currentDefinition;
                selectedPending = !DefinitionsEqual(currentDefinition, edit.Definition);
                lastEvaluatedPending = selectedPending;
                if (selectedPending) states[key] = new(currentDefinition, persistedDefinitionSha256, true);
            }
        }
    }

    internal static SecondaryMotionModelKey NormalizeKey(SecondaryMotionModelKey key)
    {
        if (key.ProjectId == Guid.Empty || key.ModelId == Guid.Empty || !IsSha256(key.SourceHash) ||
            !IsSha256(key.PackageHash) || !IsSha256(key.RigSignature))
            throw new InvalidDataException("A secondary-motion model identity requires model ids and source, package, and rig SHA-256 hashes.");
        return key with { SourceHash = key.SourceHash.ToLowerInvariant(), PackageHash = key.PackageHash.ToLowerInvariant(), RigSignature = key.RigSignature.ToLowerInvariant() };
    }

    private void StoreSelected(SecondaryMotionDefinition currentDefinition)
    {
        if (selected is not { } identity) return;
        bool pending = IsSelectedPending(currentDefinition);
        if (pending) states[identity] = new(currentDefinition, loadedDefinitionSha256 ?? HashDefinition(loadedDefinition ?? currentDefinition), true);
        else states.Remove(identity);
    }

    private bool IsSelectedPending(SecondaryMotionDefinition currentDefinition)
    {
        if (ReferenceEquals(currentDefinition, loadedDefinition)) return selectedPending;
        if (ReferenceEquals(currentDefinition, lastEvaluatedDefinition)) return lastEvaluatedPending;
        lastEvaluatedDefinition = currentDefinition;
        lastEvaluatedPending = loadedDefinitionSha256 is null ||
            !HashDefinition(currentDefinition).Equals(loadedDefinitionSha256, StringComparison.OrdinalIgnoreCase);
        return lastEvaluatedPending;
    }

    private bool TryTakeCompatibleState(SecondaryMotionModelKey identity, string? baselineDefinitionSha256, out State state)
    {
        KeyValuePair<SecondaryMotionModelKey, State>[] matches = states
            .Where(pair => pair.Value.Pending && pair.Key.ProjectId == identity.ProjectId &&
                pair.Key.ModelId == identity.ModelId &&
                pair.Key.SourceHash.Equals(identity.SourceHash, StringComparison.OrdinalIgnoreCase) &&
                pair.Key.RigSignature.Equals(identity.RigSignature, StringComparison.OrdinalIgnoreCase) &&
                pair.Value.BaselineDefinitionSha256.Equals(baselineDefinitionSha256, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (matches.Length == 1)
        {
            states.Remove(matches[0].Key);
            state = matches[0].Value;
            return true;
        }
        state = null!;
        return false;
    }

    private static bool DefinitionsEqual(SecondaryMotionDefinition current, SecondaryMotionDefinition? baseline)
    {
        if (baseline is null) return false;
        return JsonSerializer.Serialize(current).AsSpan().SequenceEqual(JsonSerializer.Serialize(baseline));
    }

    private static string HashDefinition(SecondaryMotionDefinition definition) =>
        Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(definition)));

    private static bool IsSha256(string? value) => value is { Length: 64 } &&
        value.All(static character => Uri.IsHexDigit(character));
}
