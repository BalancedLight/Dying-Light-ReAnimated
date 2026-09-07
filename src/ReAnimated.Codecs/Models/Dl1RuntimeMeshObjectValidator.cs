using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using ReAnimated.Codecs.CompactMesh;
using ReAnimated.Codecs.Rp6l;

namespace ReAnimated.Codecs.Models;

/// <summary>
/// Offline evidence for a published mesh object. Dependency validation checks
/// the mesh's embedded script reference, not whether a running game mounted
/// the bank or created an animation binding.
/// </summary>
public sealed record Dl1RuntimeMeshObjectValidation(
    string ObjectPath,
    string ObjectSha256,
    string ResourceName,
    string? EmbeddedAnimationScriptAlias,
    int AnimationEntityCount,
    bool RuntimeObjectLayoutValidated,
    bool CompiledModelDependencyValidated,
    ImmutableArray<string> MeshItemSha256)
{
    public bool RuntimeBindingVerified { get; }
}

/// <summary>
/// Rejects compiler work units at the runtime publication boundary. Installed
/// animation-bank availability is a separate check; this validator never
/// claims live binding verification.
/// </summary>
public static class Dl1RuntimeMeshObjectValidator
{
    private const int AnimationScriptPointerOffset = 0x48;
    private const int MaximumScriptNameBytes = 1024;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public static async Task<Dl1RuntimeMeshObjectValidation> ValidateAsync(
        string path,
        string expectedResourceName,
        string? expectedAnimationBank = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        string resourceName = Dl1SourceModelWriter.RequireExactResourceName(
            expectedResourceName, 63, "mesh resource identity");
        string? expectedAlias = expectedAnimationBank is null ? null
            : Dl1SourceModelWriter.AnimationScriptFileName(expectedAnimationBank);
        string fullPath = Path.GetFullPath(path);

        await RejectCompilerAddressingAsync(fullPath, cancellationToken).ConfigureAwait(false);
        Rp6lArchive archive = await Rp6lArchive.OpenAsync(
            fullPath, cancellationToken: cancellationToken).ConfigureAwait(false);
        if (archive.Resources.Any(static resource => resource.ResourceType < 0 &&
                resource.ResourceType != Rp6lResourceTypes.BuilderInformation))
        {
            throw new InvalidDataException(
                "The runtime mesh object contains a compiler-only resource type. Publish a normalized object, not a compiler work unit.");
        }

        Rp6lResourceDescriptor[] matches = archive.Resources.Where(resource =>
            resource.ResourceType == Rp6lResourceTypes.Mesh &&
            resource.Name.Equals(resourceName, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (matches.Length != 1)
        {
            throw new InvalidDataException(
                $"The runtime mesh object has {matches.Length} resources matching mesh '{resourceName}'; exactly one is required.");
        }

        Rp6lResourceDescriptor mesh = matches[0];
        if (mesh.Items.Count == 0 || mesh.Items.Any(static item => !item.HasReadableSize))
        {
            throw new InvalidDataException("The runtime mesh object has missing or unreadable mesh payloads.");
        }
        if (mesh.Items.Any(static item => (item.Flags & 0x01) != 0))
        {
            throw new InvalidDataException(
                "The runtime mesh object suppresses loading of a mesh payload. Compiler-owned item load-suppression flags must be cleared before publication.");
        }

        await using var cache = new Rp6lChunkCache(new()
        {
            CacheDirectory = Path.Combine(Path.GetTempPath(), "DLReAnimated", "runtime-mesh-inspection"),
            MaximumMemoryBytes = 128L * 1024 * 1024,
            MaximumMemoryEntryBytes = 64 * 1024 * 1024,
            MaximumDiskBytes = 512L * 1024 * 1024,
        });
        byte[] metadata = await archive.ReadItemBytesAsync(mesh.Items[0], cache,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        CompactMeshDocument hierarchy = CompactMeshDecoder.Decode(metadata);
        if (!hierarchy.IsStructurallyValid)
        {
            throw new InvalidDataException("The runtime mesh hierarchy is invalid: " + string.Join("; ",
                hierarchy.Diagnostics.Where(static diagnostic =>
                    diagnostic.Severity == CompactMeshDiagnosticSeverity.Error).Select(static diagnostic => diagnostic.Message)));
        }

        string? embeddedAlias = ReadAnimationScriptAlias(metadata);
        if (expectedAlias is not null)
        {
            if (hierarchy.AnimationEntityCountCandidate <= 0)
                throw new InvalidDataException("A stock-bank mesh must contain animation entities.");
            if (!string.Equals(embeddedAlias, expectedAlias, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException(
                    $"The compiled mesh embeds animation script '{embeddedAlias ?? "<none>"}', expected '{expectedAlias}'.");
            }
        }

        var itemHashes = ImmutableArray.CreateBuilder<string>(mesh.Items.Count);
        itemHashes.Add(Convert.ToHexStringLower(SHA256.HashData(metadata)));
        foreach (Rp6lItemDescriptor item in mesh.Items.Skip(1))
        {
            byte[] bytes = await archive.ReadItemBytesAsync(item, cache,
                cancellationToken: cancellationToken).ConfigureAwait(false);
            itemHashes.Add(Convert.ToHexStringLower(SHA256.HashData(bytes)));
        }

        await using FileStream input = File.OpenRead(fullPath);
        string objectHash = Convert.ToHexStringLower(
            await SHA256.HashDataAsync(input, cancellationToken).ConfigureAwait(false));
        return new(fullPath, objectHash, mesh.Name, embeddedAlias,
            hierarchy.AnimationEntityCountCandidate, true, expectedAlias is not null, itemHashes.ToImmutable());
    }

    private static string? ReadAnimationScriptAlias(ReadOnlySpan<byte> metadata)
    {
        // DL1 compact model header: the ASCR alias is the fixup pointer at
        // +0x48. Like the hierarchy name pointers, it serializes offset + 1.
        // Reading only this field prevents a matching string elsewhere in the
        // geometry, material names or unused tail from validating a bad alias.
        ulong pointer = BinaryPrimitives.ReadUInt64LittleEndian(metadata[AnimationScriptPointerOffset..]);
        if (pointer == 0) return null;
        if (pointer - 1 >= (ulong)metadata.Length)
            throw new InvalidDataException("The compiled mesh animation script pointer is outside its metadata payload.");
        int offset = checked((int)(pointer - 1));
        ReadOnlySpan<byte> available = metadata.Slice(offset, Math.Min(MaximumScriptNameBytes, metadata.Length - offset));
        int terminator = available.IndexOf((byte)0);
        if (terminator < 0)
            throw new InvalidDataException("The compiled mesh animation script is not NUL terminated within its bounded field.");
        try
        {
            return StrictUtf8.GetString(available[..terminator]);
        }
        catch (DecoderFallbackException exception)
        {
            throw new InvalidDataException("The compiled mesh animation script is not valid UTF-8.", exception);
        }
    }

    private static async Task RejectCompilerAddressingAsync(string path, CancellationToken cancellationToken)
    {
        await using FileStream stream = File.OpenRead(path);
        byte[] header = new byte[36];
        await stream.ReadExactlyAsync(header, cancellationToken).ConfigureAwait(false);
        if (!header.AsSpan(0, 4).SequenceEqual("RP6L"u8))
            throw new InvalidDataException("The runtime mesh object is not an RP6L container.");
        int chunkCount = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(16));
        if (chunkCount < 0 || chunkCount > 256)
            throw new InvalidDataException("The runtime mesh object has an invalid chunk count.");
        byte[] chunks = new byte[checked(chunkCount * 20)];
        await stream.ReadExactlyAsync(chunks, cancellationToken).ConfigureAwait(false);
        for (int index = 0; index < chunkCount; index++)
        {
            if (BinaryPrimitives.ReadUInt32LittleEndian(chunks.AsSpan(index * 20 + 4)) == 0)
                throw new InvalidDataException("The runtime mesh object contains compiler-addressed zero-offset chunks. Publish a normalized object, not a compiler work unit.");
        }
    }
}
