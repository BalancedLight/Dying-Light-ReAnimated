using System.Buffers.Binary;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace ReAnimated.DL1.Assets.Materials;

public sealed record Dl1MaterialPackLimits
{
    public static Dl1MaterialPackLimits Default { get; } = new();

    public int MaximumContainerCount { get; init; } = 128;

    public int MaximumMaterialCount { get; init; } = 1_000_000;

    public int MaximumTableBytes { get; init; } = 32 * 1024 * 1024;

    public int MaximumMaterialBytes { get; init; } = 1024 * 1024;

    public int MaximumTexturesPerMaterial { get; init; } = 256;

    internal void Validate()
    {
        if (MaximumContainerCount <= 0
            || MaximumMaterialCount <= 0
            || MaximumTableBytes <= 0
            || MaximumMaterialBytes <= 0
            || MaximumTexturesPerMaterial <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(Dl1MaterialPackLimits),
                "All material-pack limits must be positive.");
        }
    }
}

public sealed record Dl1MaterialPackTextureRecord(
    uint SamplerState,
    uint TextureNameHash,
    uint LoadFlags);

public sealed record Dl1MaterialPackMaterialRecord(
    string ResourceName,
    uint NameHash,
    ushort TechniqueCount,
    IReadOnlyList<Dl1MaterialPackTextureRecord> Textures);

/// <summary>
/// Bounded reader for the retail ABDM material container. Only the requested
/// material payload is read; the multi-megabyte pack body is never buffered.
/// </summary>
public sealed class Dl1MaterialPackReader : IAsyncDisposable
{
    private const uint Magic = 0x4D444241;
    private const int HeaderSize = 16;
    private const int ContainerRowSize = 48;
    private const int MaterialEntryRowSize = 16;
    private const int TextureRowSize = 12;

    private readonly FileStream _stream;
    private readonly Dl1MaterialPackLimits _limits;
    private readonly MaterialEntry[] _materials;
    private readonly SemaphoreSlim _readGate = new(1, 1);
    private bool _disposed;

    private Dl1MaterialPackReader(
        string path,
        FileStream stream,
        Dl1MaterialPackLimits limits,
        MaterialEntry[] materials)
    {
        Path = path;
        _stream = stream;
        _limits = limits;
        _materials = materials;
    }

    public string Path { get; }

    public int MaterialCount => _materials.Length;

    public static async Task<Dl1MaterialPackReader> OpenAsync(
        string path,
        Dl1MaterialPackLimits? limits = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        limits ??= Dl1MaterialPackLimits.Default;
        limits.Validate();
        string fullPath = System.IO.Path.GetFullPath(path);
        FileStream stream = new(
            fullPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 4096,
            FileOptions.Asynchronous | FileOptions.RandomAccess);
        try
        {
            if (stream.Length < HeaderSize)
            {
                throw new InvalidDataException(
                    "The DL1 material pack is shorter than its header.");
            }

            byte[] header = GC.AllocateUninitializedArray<byte>(HeaderSize);
            await ReadExactlyAtAsync(
                stream.SafeFileHandle,
                header,
                0,
                cancellationToken).ConfigureAwait(false);
            ReadOnlySpan<byte> headerData = header;
            if (BinaryPrimitives.ReadUInt32LittleEndian(headerData) != Magic)
            {
                throw new InvalidDataException(
                    $"'{fullPath}' is not an ABDM DL1 material pack.");
            }

            int containerCount = ReadBoundedCount(
                headerData[4..],
                limits.MaximumContainerCount,
                "container");
            long containerOffset =
                BinaryPrimitives.ReadUInt32LittleEndian(headerData[8..]);
            uint headerFlags =
                BinaryPrimitives.ReadUInt32LittleEndian(headerData[12..]);
            if (headerFlags != 0)
            {
                throw new InvalidDataException(
                    $"ABDM header flags 0x{headerFlags:X8} are not supported.");
            }

            int containerBytes = checked(
                containerCount * ContainerRowSize);
            if (containerBytes > limits.MaximumTableBytes)
            {
                throw new InvalidDataException(
                    "The ABDM container table exceeds the configured bound.");
            }

            ValidateRange(
                containerOffset,
                containerBytes,
                stream.Length,
                "ABDM container table");
            byte[] table =
                GC.AllocateUninitializedArray<byte>(containerBytes);
            await ReadExactlyAtAsync(
                stream.SafeFileHandle,
                table,
                containerOffset,
                cancellationToken).ConfigureAwait(false);

            ContainerRow? materialContainer = null;
            for (int index = 0; index < containerCount; index++)
            {
                ReadOnlySpan<byte> row =
                    table.AsSpan(index * ContainerRowSize, ContainerRowSize);
                string name = DecodeContainerName(row[..32], index);
                int count = ReadBoundedCount(
                    row[32..],
                    limits.MaximumMaterialCount,
                    $"'{name}' entry");
                int declaredCount = ReadBoundedCount(
                    row[36..],
                    limits.MaximumMaterialCount,
                    $"'{name}' declared entry");
                if (count != declaredCount)
                {
                    throw new InvalidDataException(
                        $"ABDM container '{name}' has inconsistent entry counts.");
                }

                uint reserved =
                    BinaryPrimitives.ReadUInt32LittleEndian(row[44..]);
                if (reserved != 0)
                {
                    throw new InvalidDataException(
                        $"ABDM container '{name}' uses an unsupported row layout.");
                }

                if (name.Equals(
                        "materials",
                        StringComparison.OrdinalIgnoreCase))
                {
                    if (materialContainer is not null)
                    {
                        throw new InvalidDataException(
                            "The ABDM pack contains more than one materials container.");
                    }

                    materialContainer = new ContainerRow(
                        count,
                        BinaryPrimitives.ReadUInt32LittleEndian(row[40..]));
                }
            }

            ContainerRow selected = materialContainer
                ?? throw new InvalidDataException(
                    "The ABDM pack has no materials container.");
            int materialTableBytes = checked(
                selected.Count * MaterialEntryRowSize);
            if (materialTableBytes > limits.MaximumTableBytes)
            {
                throw new InvalidDataException(
                    "The ABDM material table exceeds the configured bound.");
            }

            ValidateRange(
                selected.TableOffset,
                materialTableBytes,
                stream.Length,
                "ABDM material table");
            byte[] materialTable =
                GC.AllocateUninitializedArray<byte>(materialTableBytes);
            await ReadExactlyAtAsync(
                stream.SafeFileHandle,
                materialTable,
                selected.TableOffset,
                cancellationToken).ConfigureAwait(false);
            MaterialEntry[] materials =
                new MaterialEntry[selected.Count];
            uint priorHash = 0;
            for (int index = 0; index < materials.Length; index++)
            {
                ReadOnlySpan<byte> row = materialTable.AsSpan(
                    index * MaterialEntryRowSize,
                    MaterialEntryRowSize);
                uint hash = BinaryPrimitives.ReadUInt32LittleEndian(row);
                long dataOffset =
                    BinaryPrimitives.ReadUInt32LittleEndian(row[4..]);
                int logicalSize = ReadBoundedCount(
                    row[8..],
                    limits.MaximumMaterialBytes,
                    "material byte");
                int storedSize = ReadBoundedCount(
                    row[12..],
                    limits.MaximumMaterialBytes,
                    "stored material byte");
                if (logicalSize > storedSize)
                {
                    throw new InvalidDataException(
                        $"ABDM material 0x{hash:X8} is larger than its stored extent.");
                }

                ValidateRange(
                    dataOffset,
                    storedSize,
                    stream.Length,
                    $"ABDM material 0x{hash:X8}");
                if (index > 0 && hash <= priorHash)
                {
                    throw new InvalidDataException(
                        "The ABDM material hash inventory is not strictly ordered.");
                }

                priorHash = hash;
                materials[index] =
                    new MaterialEntry(hash, dataOffset, logicalSize);
            }

            return new Dl1MaterialPackReader(
                fullPath,
                stream,
                limits,
                materials);
        }
        catch
        {
            await stream.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public async Task<Dl1MaterialPackMaterialRecord?> ReadMaterialAsync(
        string resourceName,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        string normalized =
            Dl1ResourceNameHash.NormalizeFileName(resourceName);
        uint hash = Dl1ResourceNameHash.Compute(normalized);
        int index = Array.BinarySearch(
            _materials,
            new MaterialEntry(hash, 0, 0),
            MaterialEntryHashComparer.Instance);
        if (index < 0)
        {
            return null;
        }

        MaterialEntry entry = _materials[index];
        byte[] payload =
            GC.AllocateUninitializedArray<byte>(entry.LogicalSize);
        await _readGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            await ReadExactlyAtAsync(
                _stream.SafeFileHandle,
                payload,
                entry.Offset,
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _readGate.Release();
        }

        return ParseMaterial(normalized, hash, payload);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        await _readGate.WaitAsync().ConfigureAwait(false);
        try
        {
            await _stream.DisposeAsync().ConfigureAwait(false);
        }
        finally
        {
            _readGate.Release();
            _readGate.Dispose();
        }
    }

    private Dl1MaterialPackMaterialRecord ParseMaterial(
        string resourceName,
        uint expectedHash,
        byte[] payload)
    {
        ReadOnlySpan<byte> data = payload;
        if (data.Length < 24)
        {
            throw new InvalidDataException(
                $"ABDM material '{resourceName}' is shorter than its fixed fields.");
        }

        uint serializedHash =
            BinaryPrimitives.ReadUInt32LittleEndian(data);
        if (serializedHash != expectedHash)
        {
            throw new InvalidDataException(
                $"ABDM material '{resourceName}' stores hash 0x{serializedHash:X8}, expected 0x{expectedHash:X8}.");
        }

        ushort techniqueCount =
            BinaryPrimitives.ReadUInt16LittleEndian(data[16..]);
        ushort textureCount =
            BinaryPrimitives.ReadUInt16LittleEndian(data[18..]);
        if (textureCount > _limits.MaximumTexturesPerMaterial)
        {
            throw new InvalidDataException(
                $"ABDM material '{resourceName}' declares {textureCount} textures.");
        }

        int relativeTextureOffset =
            BinaryPrimitives.ReadUInt16LittleEndian(data[22..]);
        int textureOffset = checked(22 + relativeTextureOffset);
        int textureBytes = checked(textureCount * TextureRowSize);
        if (textureCount > 0
            && (textureOffset < 24
                || textureOffset > data.Length - textureBytes))
        {
            throw new InvalidDataException(
                $"ABDM material '{resourceName}' has an invalid texture table.");
        }

        Dl1MaterialPackTextureRecord[] textures =
            new Dl1MaterialPackTextureRecord[textureCount];
        for (int index = 0; index < textures.Length; index++)
        {
            ReadOnlySpan<byte> row =
                data.Slice(textureOffset + index * TextureRowSize, TextureRowSize);
            textures[index] = new Dl1MaterialPackTextureRecord(
                BinaryPrimitives.ReadUInt32LittleEndian(row),
                BinaryPrimitives.ReadUInt32LittleEndian(row[4..]),
                BinaryPrimitives.ReadUInt32LittleEndian(row[8..]));
        }

        return new Dl1MaterialPackMaterialRecord(
            resourceName,
            expectedHash,
            techniqueCount,
            textures);
    }

    private static string DecodeContainerName(
        ReadOnlySpan<byte> bytes,
        int index)
    {
        int terminator = bytes.IndexOf((byte)0);
        if (terminator < 0)
        {
            throw new InvalidDataException(
                $"ABDM container {index} has no terminated name.");
        }

        if (bytes[(terminator + 1)..].ContainsAnyExcept((byte)0)
            || !IsPrintableAscii(bytes[..terminator]))
        {
            throw new InvalidDataException(
                $"ABDM container {index} has an invalid ASCII name.");
        }

        return Encoding.ASCII.GetString(bytes[..terminator]);
    }

    private static bool IsPrintableAscii(ReadOnlySpan<byte> bytes)
    {
        foreach (byte value in bytes)
        {
            if (value is < 0x20 or > 0x7E)
            {
                return false;
            }
        }

        return true;
    }

    private static int ReadBoundedCount(
        ReadOnlySpan<byte> bytes,
        int maximum,
        string label)
    {
        uint value = BinaryPrimitives.ReadUInt32LittleEndian(bytes);
        if (value > maximum)
        {
            throw new InvalidDataException(
                $"ABDM {label} count {value:N0} exceeds the {maximum:N0} limit.");
        }

        return checked((int)value);
    }

    private static void ValidateRange(
        long offset,
        long length,
        long fileLength,
        string label)
    {
        if (offset < 0
            || length < 0
            || offset > fileLength
            || length > fileLength - offset)
        {
            throw new InvalidDataException(
                $"{label} extends beyond the material pack.");
        }
    }

    private static async ValueTask ReadExactlyAtAsync(
        SafeFileHandle handle,
        Memory<byte> buffer,
        long offset,
        CancellationToken cancellationToken)
    {
        int read = 0;
        while (read < buffer.Length)
        {
            int current = await RandomAccess.ReadAsync(
                handle,
                buffer[read..],
                checked(offset + read),
                cancellationToken).ConfigureAwait(false);
            if (current == 0)
            {
                throw new EndOfStreamException(
                    "The DL1 material pack ended during a bounded read.");
            }

            read += current;
        }
    }

    private readonly record struct ContainerRow(
        int Count,
        long TableOffset);

    private readonly record struct MaterialEntry(
        uint Hash,
        long Offset,
        int LogicalSize);

    private sealed class MaterialEntryHashComparer :
        IComparer<MaterialEntry>
    {
        public static MaterialEntryHashComparer Instance { get; } = new();

        public int Compare(MaterialEntry x, MaterialEntry y) =>
            x.Hash.CompareTo(y.Hash);
    }
}
