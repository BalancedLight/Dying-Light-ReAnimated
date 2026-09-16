using System.Buffers;
using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using ReAnimated.Codecs.Models;
using ReAnimated.Codecs.Rp6l;

namespace ReAnimated.DL1.Assets.Catalog;

public enum Dl1CorpusReadStatus { Unverified, Read, Missing, Failed }
public enum Dl1CorpusDependencyStatus { UniqueCatalogMatch, Missing, Ambiguous, Dynamic }

public sealed record Dl1CorpusCandidate(
    RetailAssetRecord Asset, bool SelectedByCatalog, Dl1CorpusReadStatus Status,
    long? BytesRead, string? ContentSha256, string HashDomain, string? Error);

public sealed record Dl1CorpusDependency(
    string Kind, string RawReference, Dl1CorpusDependencyStatus Status,
    ImmutableArray<RetailAssetLogicalId> Candidates);

public sealed record Dl1CorpusResource(
    RetailAssetLogicalId LogicalId, Dl1CorpusReadStatus Status,
    ImmutableArray<Dl1CorpusCandidate> Candidates, ImmutableArray<Dl1CorpusDependency> Dependencies,
    ImmutableArray<AnimationScriptSeqTrack> SourceTracks, Dl1ChrV4Document? Character,
    ImmutableArray<string> Unknowns, Dl1CompiledCorpusEvidence? Compiled = null);

public sealed record Dl1RigCorpusReport(
    string BuildFingerprint, ImmutableArray<RetailAssetLogicalId> Roots,
    ImmutableArray<Dl1CorpusResource> Resources)
{
    public string Format { get; } = "dl-reanimated-rig-corpus-v1";
    public string PrecedenceEvidence { get; } = "Configured catalog precedence; native lookup order is unverified.";
    public bool RuntimeBindingVerified { get; }
    public bool DependencySemanticsComplete { get; }
}

public sealed record Dl1RigCorpusLimits
{
    public int MaximumResources { get; init; } = 20_000;
    public int MaximumCandidatesPerResource { get; init; } = 64;
    public long MaximumAssetBytes { get; init; } = 256L * 1024 * 1024;
    public long MaximumTotalBytes { get; init; } = 2L * 1024 * 1024 * 1024;
    public int MaximumTextBytes { get; init; } = 8 * 1024 * 1024;
}

/// <summary>
/// Read-only, bounded corpus capture over existing providers. Full read hashes
/// are separate from provider snapshot fingerprints. Name matches are catalog
/// correlations, never proof of runtime binding or native search semantics.
/// </summary>
public static class Dl1RigCorpusCollector
{
    public static async Task<Dl1RigCorpusReport> CollectAsync(IRetailAssetCatalog catalog,
        IEnumerable<RetailAssetLogicalId> roots, string buildFingerprint, Dl1RigCorpusLimits? limits = null,
        Func<RetailAssetRecord, string, CancellationToken, Task<Dl1CompiledCorpusEvidence>>? inspectCompiled = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(roots);
        if (buildFingerprint is null || buildFingerprint.Length != 64 || buildFingerprint.Any(static c => !Uri.IsHexDigit(c)))
            throw new ArgumentException("An exact build fingerprint is required.", nameof(buildFingerprint));
        limits ??= new();
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limits.MaximumResources);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limits.MaximumCandidatesPerResource);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limits.MaximumAssetBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limits.MaximumTotalBytes);
        if (limits.MaximumTextBytes is <= 0 or > 8 * 1024 * 1024) throw new ArgumentOutOfRangeException(nameof(limits));
        var selectedRoots = roots.Distinct().OrderBy(static id => id.StableKey, StringComparer.Ordinal).ToImmutableArray();
        if (selectedRoots.Length > limits.MaximumResources) throw new InvalidDataException("Corpus root count exceeds its bound.");
        var pending = new Queue<RetailAssetLogicalId>(selectedRoots);
        var queued = selectedRoots.ToHashSet();
        var resources = ImmutableArray.CreateBuilder<Dl1CorpusResource>();
        long totalBytes = 0;
        byte[] buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
        try
        {
            while (pending.TryDequeue(out RetailAssetLogicalId id))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var dependencies = ImmutableArray.CreateBuilder<Dl1CorpusDependency>();
                var unknowns = ImmutableArray.CreateBuilder<string>();
                var candidates = ImmutableArray.CreateBuilder<Dl1CorpusCandidate>();
                ImmutableArray<AnimationScriptSeqTrack> tracks = [];
                Dl1ChrV4Document? character = null;
                Dl1CompiledCorpusEvidence? compiled = null;
                RetailAssetRecord? winner = catalog.Resolve(id);
                IReadOnlyList<RetailAssetRecord> available = catalog.GetCandidates(id);
                if (available.Count > limits.MaximumCandidatesPerResource) throw new InvalidDataException("Corpus candidate count exceeds its bound.");
                foreach (RetailAssetRecord candidate in available)
                {
                    bool selected = winner?.Id == candidate.Id;
                    string extension = id.Namespace == RetailAssetNamespace.VirtualFile ? Path.GetExtension(id.Name) : string.Empty;
                    bool captureBytes = selected && extension is ".scr" or ".ascr" or ".def" or ".chr";
                    using var payload = captureBytes ? new MemoryStream() : null;
                    string hashDomain = id.Namespace == RetailAssetNamespace.RpackResource ? "concatenated-readable-rpack-items-v1" : "virtual-file-bytes-v1";
                    try
                    {
                        if (candidate.Source.Length > limits.MaximumAssetBytes) throw new InvalidDataException("Asset exceeds the corpus byte limit.");
                        await using Stream input = await catalog.OpenReadAsync(candidate, cancellationToken).ConfigureAwait(false);
                        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                        long count = 0;
                        int read;
                        while ((read = await input.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false)) != 0)
                        {
                            count = checked(count + read); totalBytes = checked(totalBytes + read);
                            if (totalBytes > limits.MaximumTotalBytes) throw new CorpusBudgetException();
                            if (count > limits.MaximumAssetBytes) throw new InvalidDataException("Asset exceeds the corpus byte limit.");
                            hash.AppendData(buffer, 0, read);
                            if (payload is not null)
                            {
                                long limit = extension == ".chr" ? limits.MaximumAssetBytes : limits.MaximumTextBytes;
                                if (count > limit) throw new InvalidDataException("Inspectable source exceeds its payload limit.");
                                payload.Write(buffer, 0, read);
                            }
                        }
                        if (candidate.Source.Length != count) throw new InvalidDataException("Read length differs from the catalog snapshot.");
                        string digest = Convert.ToHexStringLower(hash.GetHashAndReset());
                        if (candidate.Id.ContentFingerprint is { } expected && !digest.Equals(expected, StringComparison.OrdinalIgnoreCase))
                            throw new InvalidDataException("Read hash differs from the catalog content fingerprint.");
                        candidates.Add(new(candidate, selected, Dl1CorpusReadStatus.Read, count, digest, hashDomain, null));
                    }
                    catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException)
                    {
                        if (exception is CorpusBudgetException) throw;
                        candidates.Add(new(candidate, selected, Dl1CorpusReadStatus.Failed, null, null, hashDomain, exception.Message));
                        continue;
                    }
                    if (payload is not null)
                    {
                        try
                        {
                            byte[] bytes = payload.ToArray();
                            if (extension == ".chr") character = Dl1ChrV4Codec.Parse(bytes);
                            else
                            {
                                using var reader = new StreamReader(new MemoryStream(bytes), new UTF8Encoding(false, true), detectEncodingFromByteOrderMarks: true);
                                string source = await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
                                foreach (AnimationSourceDependency directive in AnimationScriptDependencyReader.Read(source))
                                    AddDependency(directive.Kind.ToString(), directive.ArgumentText, directive.LiteralName,
                                        directive.Kind == AnimationSourceDependencyKind.AnimationScriptAlias ? Rp6lResourceTypes.AnimationScript : null);
                                if (extension == ".scr")
                                {
                                    tracks = AnimationScriptSourceParser.ParseSeqTracks(source);
                                    foreach (AnimationScriptSeqTrack track in tracks)
                                        AddDependency("SequenceClip", track.Anm2Name, track.Anm2Name, Rp6lResourceTypes.Animation);
                                    if (tracks.Any(static t => t.HasEventBlock)) unknowns.Add("Event blocks are retained as opaque source behavior; event dependencies are unverified.");
                                    if (tracks.Any(static t => t.StartFrame.IsSymbolic || t.EndFrame.IsSymbolic || t.FramesPerSecond.IsSymbolic || t.Enabled.IsSymbolic || t.Blend.IsSymbolic))
                                        unknowns.Add("Symbolic sequence values require definition evaluation; their raw tokens are retained.");
                                }
                                unknowns.Add("Only include, AnimScriptAlias and SeqTrack dependency syntax is inspected; other script consumers remain unverified.");
                            }
                        }
                        catch (Exception exception) when (exception is InvalidDataException or ArgumentException or OverflowException)
                        {
                            unknowns.Add("Source decode incomplete: " + exception.Message);
                        }
                    }
                }
                Dl1CorpusReadStatus status = winner is null ? Dl1CorpusReadStatus.Missing :
                    candidates.Any(static c => c.SelectedByCatalog && c.Status == Dl1CorpusReadStatus.Read) ? Dl1CorpusReadStatus.Read : Dl1CorpusReadStatus.Failed;
                if (id.Namespace == RetailAssetNamespace.RpackResource)
                {
                    Dl1CorpusCandidate? selected = candidates.SingleOrDefault(static c => c.SelectedByCatalog && c.Status == Dl1CorpusReadStatus.Read);
                    if (inspectCompiled is not null && selected?.ContentSha256 is { } digest &&
                        id.ResourceType is Rp6lResourceTypes.Mesh or Rp6lResourceTypes.AnimationScript)
                    {
                        try
                        {
                            Dl1CompiledCorpusEvidence inspected = await inspectCompiled(selected.Asset, digest, cancellationToken).ConfigureAwait(false);
                            if (inspected.AssetId != selected.Asset.Id || !inspected.ContentSha256.Equals(digest, StringComparison.OrdinalIgnoreCase))
                                throw new InvalidDataException("Compiled evidence does not match the selected physical asset and content hash.");
                            compiled = inspected;
                            if (compiled.EmbeddedAnimationScriptAlias is { } alias)
                                AddDependency("CompiledAnimationScriptAlias", alias, alias, Rp6lResourceTypes.AnimationScript);
                        }
                        catch (Exception exception) when (exception is IOException or InvalidDataException or ArgumentException or OverflowException)
                        {
                            if (exception is CorpusBudgetException) throw;
                            unknowns.Add("Compiled inspection incomplete: " + exception.Message);
                        }
                    }
                    if (compiled is null)
                        unknowns.Add("The full readable resource stream was hashed; item boundaries, compiled references and semantic inventory require compiled read-back.");
                }
                resources.Add(new(id, status, candidates.ToImmutable(), dependencies.ToImmutable(), tracks, character, unknowns.ToImmutable(), compiled));

                void AddDependency(string kind, string raw, string? literal, short? resourceType)
                {
                    ImmutableArray<RetailAssetLogicalId> matches = literal is null ? [] : Match(catalog, literal, resourceType);
                    var status = literal is null ? Dl1CorpusDependencyStatus.Dynamic : matches.Length switch
                    {
                        0 => Dl1CorpusDependencyStatus.Missing, 1 => Dl1CorpusDependencyStatus.UniqueCatalogMatch, _ => Dl1CorpusDependencyStatus.Ambiguous,
                    };
                    dependencies.Add(new(kind, raw, status, matches));
                    if (matches.Length != 1) return;
                    RetailAssetLogicalId dependency = matches[0];
                    if (!queued.Add(dependency)) return;
                    if (queued.Count > limits.MaximumResources) throw new CorpusBudgetException();
                    pending.Enqueue(dependency);
                }
            }
        }
        finally { ArrayPool<byte>.Shared.Return(buffer); }
        return new(buildFingerprint.ToLowerInvariant(), selectedRoots,
            resources.OrderBy(static r => r.LogicalId.StableKey, StringComparer.Ordinal).ToImmutableArray());
    }

    private static ImmutableArray<RetailAssetLogicalId> Match(IRetailAssetCatalog catalog, string reference, short? resourceType)
    {
        string normalized = reference.Replace('\\', '/').Trim().ToLowerInvariant();
        if (normalized.Length == 0 || normalized.Split('/').Any(static segment => segment is "." or "..")) return [];
        if (resourceType is { } type)
        {
            string name = Path.GetFileNameWithoutExtension(normalized);
            RetailAssetLogicalId id = RetailAssetLogicalId.Rpack(type, name);
            return catalog.Resolve(id) is null ? [] : [id];
        }
        return catalog.Assets.Where(a => a.Id.Namespace == RetailAssetNamespace.VirtualFile &&
                (normalized.Contains('/') ? a.Id.Name == normalized || a.Id.Name.EndsWith('/' + normalized, StringComparison.Ordinal)
                    : Path.GetFileName(a.Id.Name) == normalized))
            .Select(static a => a.Id.LogicalId).Distinct().OrderBy(static id => id.StableKey, StringComparer.Ordinal).ToImmutableArray();
    }

    private sealed class CorpusBudgetException() : IOException("Corpus traversal exceeded its resource or total-byte budget.");
}
