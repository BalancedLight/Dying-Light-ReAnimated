using System.Collections.Immutable;
using System.Security.Cryptography;

namespace ReAnimated.DL1.Assets.Materials;

public sealed record Dl1MaterialPayloadReceipt(
    string NormalizedName,
    uint NameHash,
    ushort TechniqueCount,
    int TableIndex,
    long Offset,
    int LogicalByteLength,
    int StoredSize,
    ImmutableArray<byte> PayloadBytes,
    string PayloadSha256,
    string ProviderSha256,
    ImmutableArray<Dl1MaterialPackTextureRecord> Textures);

public sealed partial class Dl1MaterialPackReader
{
    public const int MaximumMemoryProviderBytes = 256 * 1024 * 1024;

    public static async Task<Dl1MaterialPackReader> OpenMemoryAsync(ImmutableArray<byte> providerBytes,
        Dl1MaterialPackLimits? limits = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (providerBytes.IsDefaultOrEmpty || providerBytes.Length > MaximumMemoryProviderBytes)
            throw new InvalidDataException("The material provider is empty or exceeds 256 MiB.");
        limits ??= Dl1MaterialPackLimits.Default;
        limits.Validate();
        ValueTask ReadAt(Memory<byte> buffer, long offset, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            ValidateRange(offset, buffer.Length, providerBytes.Length, "ABDM memory read");
            providerBytes.AsSpan().Slice(checked((int)offset), buffer.Length).CopyTo(buffer.Span);
            return ValueTask.CompletedTask;
        }
        MaterialEntry[] materials = await ReadInventoryAsync(providerBytes.Length, ReadAt,
            limits, cancellationToken).ConfigureAwait(false);
        string providerHash = Convert.ToHexStringLower(SHA256.HashData(providerBytes.AsSpan()));
        cancellationToken.ThrowIfCancellationRequested();
        return new Dl1MaterialPackReader(string.Empty, null, limits, materials,
            providerBytes.Length, ReadAt, providerHash);
    }

    public async Task<Dl1MaterialPayloadReceipt?> ReadMaterialReceiptAsync(string resourceName,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        string normalized = Dl1ResourceNameHash.NormalizeFileName(resourceName);
        uint hash = Dl1ResourceNameHash.Compute(normalized);
        PayloadRead? read = await ReadPayloadAsync(hash, cancellationToken).ConfigureAwait(false);
        if (read is null) return null;
        Dl1MaterialPackMaterialRecord parsed = ParseMaterial(normalized, hash, read.Bytes);
        string providerHash = await GetProviderSha256Async(cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        return new(normalized, hash, parsed.TechniqueCount, read.Index, read.Entry.Offset,
            read.Entry.LogicalSize, read.Entry.StoredSize, ImmutableArray.CreateRange(read.Bytes),
            Convert.ToHexStringLower(SHA256.HashData(read.Bytes)), providerHash,
            parsed.Textures.ToImmutableArray());
    }

    public async Task<string> GetProviderSha256Async(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();
        if (_providerSha256 is not null) return _providerSha256;
        if (_providerLength > MaximumMemoryProviderBytes)
            throw new InvalidDataException("Material provider receipts are limited to 256 MiB.");
        await _readGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_providerSha256 is not null) return _providerSha256;
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            byte[] buffer = new byte[64 * 1024];
            long offset = 0;
            while (offset < _providerLength)
            {
                int count = checked((int)Math.Min(buffer.Length, _providerLength - offset));
                await _readAt(buffer.AsMemory(0, count), offset, cancellationToken).ConfigureAwait(false);
                hash.AppendData(buffer.AsSpan(0, count));
                offset += count;
            }
            _providerSha256 = Convert.ToHexStringLower(hash.GetHashAndReset());
            return _providerSha256;
        }
        finally { _readGate.Release(); }
    }
}
