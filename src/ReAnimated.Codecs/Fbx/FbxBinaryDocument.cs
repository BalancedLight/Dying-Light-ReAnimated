using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Diagnostics;
using System.IO.Compression;
using System.Text;

namespace ReAnimated.Codecs.Fbx;

public sealed record FbxReadLimits
{
    public static FbxReadLimits Default { get; } = new();

    public int MaximumFileBytes { get; init; } = 1024 * 1024 * 1024;

    public int MaximumNodes { get; init; } = 2_000_000;

    public int MaximumPropertiesPerNode { get; init; } = 1_000_000;

    public int MaximumArrayElements { get; init; } = 100_000_000;

    public int MaximumArrayBytes { get; init; } = 512 * 1024 * 1024;

    public long MaximumDecodedAllocationBytes { get; init; } =
        384L * 1024 * 1024;

    public int MaximumDepth { get; init; } = 256;

    internal void Validate()
    {
        if (MaximumFileBytes <= 0 ||
            MaximumNodes <= 0 ||
            MaximumPropertiesPerNode <= 0 ||
            MaximumArrayElements <= 0 ||
            MaximumArrayBytes <= 0 ||
            MaximumDecodedAllocationBytes <= 0 ||
            MaximumDepth <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(FbxReadLimits), "FBX limits must be positive.");
        }
    }
}

public enum FbxReadPurpose
{
    CompleteDocument,
    Animation,
}

/// <summary>
/// Selects which FBX domains are materialized by the bounded binary reader.
/// Animation import retains object headers, skeletons, bind data, animation
/// stacks/curves, and blend-shape channel objects while deliberately skipping
/// Geometry child payloads such as vertices and polygon topology.
/// </summary>
public sealed record FbxReadOptions
{
    public static FbxReadOptions CompleteDocument { get; } = new();

    public static FbxReadOptions Animation { get; } = new()
    {
        Purpose = FbxReadPurpose.Animation,
    };

    public FbxReadPurpose Purpose { get; init; } =
        FbxReadPurpose.CompleteDocument;

    internal void Validate()
    {
        if (!Enum.IsDefined(Purpose))
        {
            throw new ArgumentOutOfRangeException(
                nameof(Purpose),
                Purpose,
                "The FBX read purpose is invalid.");
        }
    }
}

public sealed record FbxProperty(char TypeCode, object Value)
{
    public T Get<T>() => Value is T typed
        ? typed
        : throw new InvalidCastException(
            $"FBX property {TypeCode} contains {Value.GetType().Name}, not {typeof(T).Name}.");
}

public sealed record FbxNode(
    string Name,
    ImmutableArray<FbxProperty> Properties,
    ImmutableArray<FbxNode> Children,
    long StartOffset,
    long EndOffset)
{
    /// <summary>
    /// True when the node header/properties were retained but its children
    /// were deliberately excluded by the selected read purpose.
    /// </summary>
    public bool ChildPayloadSkipped { get; init; }

    public FbxNode? FindChild(string name) =>
        Children.FirstOrDefault(child => string.Equals(child.Name, name, StringComparison.Ordinal));

    public IEnumerable<FbxNode> FindChildren(string name) =>
        Children.Where(child => string.Equals(child.Name, name, StringComparison.Ordinal));

    public string? FirstString() =>
        Properties.Length != 0 && Properties[0].Value is string value ? value : null;
}

public sealed record FbxBinaryDocument(
    uint Version,
    ImmutableArray<FbxNode> Nodes)
{
    public const long TicksPerSecond = 46_186_158_000;

    public FbxReadPurpose ReadPurpose { get; init; } =
        FbxReadPurpose.CompleteDocument;

    public FbxNode? FindTopLevel(string name) =>
        Nodes.FirstOrDefault(node => string.Equals(node.Name, name, StringComparison.Ordinal));

    public ImmutableArray<FbxNode> SkippedObjectPayloads =>
        FindTopLevel("Objects") is { } objects
            ? objects.Children
                .Where(static node => node.ChildPayloadSkipped)
                .ToImmutableArray()
            : [];

    public static string CleanObjectName(string value)
    {
        var nul = value.IndexOf('\0');
        if (nul >= 0)
        {
            value = value[..nul];
        }

        var separator = value.IndexOf("::", StringComparison.Ordinal);
        return separator >= 0 ? value[(separator + 2)..] : value;
    }
}

public static class FbxBinaryReader
{
    private static ReadOnlySpan<byte> Magic => "Kaydara FBX Binary  \0\u001a\0"u8;

    public static FbxBinaryDocument Read(
        ReadOnlySpan<byte> data,
        FbxReadLimits? limits = null,
        CancellationToken cancellationToken = default)
        => ReadCore(
            data,
            FbxReadOptions.CompleteDocument,
            limits,
            cancellationToken);

    public static FbxBinaryDocument ReadWithOptions(
        ReadOnlySpan<byte> data,
        FbxReadOptions options,
        FbxReadLimits? limits = null,
        CancellationToken cancellationToken = default)
        => ReadCore(
            data,
            options,
            limits,
            cancellationToken);

    private static FbxBinaryDocument ReadCore(
        ReadOnlySpan<byte> data,
        FbxReadOptions options,
        FbxReadLimits? limits,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        limits ??= FbxReadLimits.Default;
        limits.Validate();
        cancellationToken.ThrowIfCancellationRequested();
        if (data.Length > limits.MaximumFileBytes)
        {
            throw new InvalidDataException(
                $"FBX is {data.Length:N0} bytes; the configured limit is {limits.MaximumFileBytes:N0}.");
        }

        if (data.Length < 27 || !data[..Magic.Length].SequenceEqual(Magic))
        {
            throw new InvalidDataException(
                "Only binary FBX 2011-2020 files are supported; ASCII or truncated input was detected.");
        }

        var version = BinaryPrimitives.ReadUInt32LittleEndian(data[23..]);
        if (version is < 7100 or > 7700)
        {
            throw new InvalidDataException(
                $"Unsupported binary FBX version {version}; expected 7100..7700.");
        }

        var state = new ParseState(
            data,
            version,
            options,
            limits,
            cancellationToken);
        var (nodes, _) = state.ReadNodes(
            27,
            data.Length,
            0,
            parentName: null);
        cancellationToken.ThrowIfCancellationRequested();
        return new FbxBinaryDocument(
            version,
            nodes)
        {
            ReadPurpose = options.Purpose,
        };
    }

    public static async Task<FbxBinaryDocument> ReadFileAsync(
        string path,
        FbxReadLimits? limits = null,
        CancellationToken cancellationToken = default)
        => await ReadFileCoreAsync(
            path,
            FbxReadOptions.CompleteDocument,
            limits,
            cancellationToken).ConfigureAwait(false);

    public static async Task<FbxBinaryDocument> ReadFileWithOptionsAsync(
        string path,
        FbxReadOptions options,
        FbxReadLimits? limits = null,
        CancellationToken cancellationToken = default)
        => await ReadFileCoreAsync(
            path,
            options,
            limits,
            cancellationToken).ConfigureAwait(false);

    private static async Task<FbxBinaryDocument> ReadFileCoreAsync(
        string path,
        FbxReadOptions options,
        FbxReadLimits? limits,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        limits ??= FbxReadLimits.Default;
        limits.Validate();
        cancellationToken.ThrowIfCancellationRequested();

        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            128 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (stream.Length > limits.MaximumFileBytes)
        {
            throw new InvalidDataException(
                $"FBX is {stream.Length:N0} bytes; the configured limit is {limits.MaximumFileBytes:N0}.");
        }

        var bytes = GC.AllocateUninitializedArray<byte>(checked((int)stream.Length));
        await stream.ReadExactlyAsync(bytes, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        return ReadCore(
            bytes,
            options,
            limits,
            cancellationToken);
    }

    private ref struct ParseState
    {
        private const int EstimatedNodeAllocationBytes = 128;
        private const int EstimatedPropertyAllocationBytes = 32;
        private const int EstimatedValueAllocationBytes = 24;
        private readonly ReadOnlySpan<byte> _data;
        private readonly uint _version;
        private readonly FbxReadOptions _options;
        private readonly FbxReadLimits _limits;
        private readonly CancellationToken _cancellationToken;
        private int _nodeCount;
        private long _decodedAllocationBytes;

        public ParseState(
            ReadOnlySpan<byte> data,
            uint version,
            FbxReadOptions options,
            FbxReadLimits limits,
            CancellationToken cancellationToken)
        {
            _data = data;
            _version = version;
            _options = options;
            _limits = limits;
            _cancellationToken = cancellationToken;
            _nodeCount = 0;
            _decodedAllocationBytes = 0;
        }

        public (ImmutableArray<FbxNode> Nodes, int Offset) ReadNodes(
            int offset,
            int containingEnd,
            int depth,
            string? parentName)
        {
            if (depth > _limits.MaximumDepth)
            {
                throw new InvalidDataException($"FBX nesting exceeds {_limits.MaximumDepth} levels.");
            }

            var nodes = ImmutableArray.CreateBuilder<FbxNode>();
            var nullRecordLength = _version >= 7500 ? 25 : 13;
            while (offset < containingEnd)
            {
                _cancellationToken.ThrowIfCancellationRequested();
                EnsureRange(offset, nullRecordLength, containingEnd);
                if (IsZeroRecord(offset, nullRecordLength))
                {
                    return (nodes.ToImmutable(), offset + nullRecordLength);
                }

                var start = offset;
                ulong endOffset;
                ulong propertyCount;
                ulong propertyLength;
                if (_version >= 7500)
                {
                    EnsureRange(offset, 24, containingEnd);
                    endOffset = BinaryPrimitives.ReadUInt64LittleEndian(_data[offset..]);
                    propertyCount = BinaryPrimitives.ReadUInt64LittleEndian(_data[(offset + 8)..]);
                    propertyLength = BinaryPrimitives.ReadUInt64LittleEndian(_data[(offset + 16)..]);
                    offset += 24;
                }
                else
                {
                    EnsureRange(offset, 12, containingEnd);
                    endOffset = BinaryPrimitives.ReadUInt32LittleEndian(_data[offset..]);
                    propertyCount = BinaryPrimitives.ReadUInt32LittleEndian(_data[(offset + 4)..]);
                    propertyLength = BinaryPrimitives.ReadUInt32LittleEndian(_data[(offset + 8)..]);
                    offset += 12;
                }

                EnsureRange(offset, 1, containingEnd);
                var nameLength = _data[offset++];
                if (endOffset == 0)
                {
                    throw new InvalidDataException(
                        $"FBX node at 0x{start:X} contains a malformed null record.");
                }

                if (endOffset > (ulong)_data.Length ||
                    endOffset > (ulong)containingEnd ||
                    endOffset <= (ulong)start)
                {
                    throw new InvalidDataException(
                        $"FBX node at 0x{start:X} has invalid end offset 0x{endOffset:X}.");
                }

                if (propertyCount > (ulong)_limits.MaximumPropertiesPerNode)
                {
                    throw new InvalidDataException(
                        $"FBX node at 0x{start:X} declares {propertyCount:N0} properties.");
                }

                EnsureRange(offset, nameLength, checked((int)endOffset));
                var name = Encoding.UTF8.GetString(
                    _data.Slice(offset, nameLength));
                ReserveDecodedAllocation(
                    checked(
                        EstimatedNodeAllocationBytes +
                        (long)nameLength * sizeof(char) +
                        (long)propertyCount *
                        EstimatedPropertyAllocationBytes),
                    $"node '{name}'");
                offset += nameLength;

                var propertyEnd64 = checked((ulong)offset + propertyLength);
                if (propertyEnd64 > endOffset || propertyEnd64 > (ulong)_data.Length)
                {
                    throw new InvalidDataException($"FBX property block for '{name}' exceeds its node.");
                }

                var properties = ImmutableArray.CreateBuilder<FbxProperty>(checked((int)propertyCount));
                for (ulong index = 0; index < propertyCount; index++)
                {
                    if ((index & 0x3FF) == 0)
                    {
                        _cancellationToken.ThrowIfCancellationRequested();
                    }

                    var (property, nextOffset) = ReadProperty(offset, checked((int)propertyEnd64));
                    properties.Add(property);
                    offset = nextOffset;
                }

                if (offset != checked((int)propertyEnd64))
                {
                    throw new InvalidDataException(
                        $"FBX property length mismatch in '{name}': 0x{offset:X} != 0x{propertyEnd64:X}.");
                }

                bool skipChildPayload = ShouldSkipChildPayload(
                    parentName,
                    name,
                    properties);
                ImmutableArray<FbxNode> children = [];
                int declaredEnd = checked((int)endOffset);
                if (offset < declaredEnd)
                {
                    int parsedEnd;
                    if (skipChildPayload)
                    {
                        parsedEnd = ValidateSkippedChildren(
                            offset,
                            declaredEnd,
                            depth + 1);
                    }
                    else
                    {
                        (children, parsedEnd) = ReadNodes(
                            offset,
                            declaredEnd,
                            depth + 1,
                            name);
                    }

                    if (parsedEnd != declaredEnd)
                    {
                        throw new InvalidDataException(
                            $"FBX node '{name}' at 0x{start:X} terminates its child list at 0x{parsedEnd:X}, before its declared end 0x{declaredEnd:X}.");
                    }
                }

                offset = declaredEnd;
                RegisterNode();

                nodes.Add(new FbxNode(
                    name,
                    properties.MoveToImmutable(),
                    children,
                    start,
                    checked((long)endOffset))
                {
                    ChildPayloadSkipped = skipChildPayload,
                });
            }

            throw new InvalidDataException(
                $"FBX child list ending at 0x{containingEnd:X} has no terminal null record.");
        }

        private int ValidateSkippedChildren(
            int offset,
            int containingEnd,
            int depth)
        {
            if (depth > _limits.MaximumDepth)
            {
                throw new InvalidDataException(
                    $"FBX nesting exceeds {_limits.MaximumDepth} levels.");
            }

            int nullRecordLength = _version >= 7500 ? 25 : 13;
            while (offset < containingEnd)
            {
                _cancellationToken.ThrowIfCancellationRequested();
                EnsureRange(offset, nullRecordLength, containingEnd);
                if (IsZeroRecord(offset, nullRecordLength))
                {
                    return offset + nullRecordLength;
                }

                int start = offset;
                ulong endOffset;
                ulong propertyCount;
                ulong propertyLength;
                if (_version >= 7500)
                {
                    EnsureRange(offset, 24, containingEnd);
                    endOffset = BinaryPrimitives.ReadUInt64LittleEndian(
                        _data[offset..]);
                    propertyCount = BinaryPrimitives.ReadUInt64LittleEndian(
                        _data[(offset + 8)..]);
                    propertyLength = BinaryPrimitives.ReadUInt64LittleEndian(
                        _data[(offset + 16)..]);
                    offset += 24;
                }
                else
                {
                    EnsureRange(offset, 12, containingEnd);
                    endOffset = BinaryPrimitives.ReadUInt32LittleEndian(
                        _data[offset..]);
                    propertyCount = BinaryPrimitives.ReadUInt32LittleEndian(
                        _data[(offset + 4)..]);
                    propertyLength = BinaryPrimitives.ReadUInt32LittleEndian(
                        _data[(offset + 8)..]);
                    offset += 12;
                }

                EnsureRange(offset, 1, containingEnd);
                int nameLength = _data[offset++];
                if (endOffset == 0 ||
                    endOffset > (ulong)_data.Length ||
                    endOffset > (ulong)containingEnd ||
                    endOffset <= (ulong)start)
                {
                    throw new InvalidDataException(
                        $"Skipped FBX node at 0x{start:X} has invalid end offset 0x{endOffset:X}.");
                }

                if (propertyCount > (ulong)_limits.MaximumPropertiesPerNode)
                {
                    throw new InvalidDataException(
                        $"Skipped FBX node at 0x{start:X} declares {propertyCount:N0} properties.");
                }

                int declaredEnd = checked((int)endOffset);
                EnsureRange(offset, nameLength, declaredEnd);
                offset += nameLength;
                ulong propertyEnd64 = checked((ulong)offset + propertyLength);
                if (propertyEnd64 > endOffset ||
                    propertyEnd64 > (ulong)_data.Length)
                {
                    throw new InvalidDataException(
                        $"Skipped FBX property block at 0x{start:X} exceeds its node.");
                }

                int propertyEnd = checked((int)propertyEnd64);
                ValidateSkippedProperties(
                    ref offset,
                    propertyEnd,
                    propertyCount);
                if (offset != propertyEnd)
                {
                    throw new InvalidDataException(
                        $"Skipped FBX property block at 0x{start:X} does not match its declared length.");
                }

                if (offset < declaredEnd)
                {
                    int childEnd = ValidateSkippedChildren(
                        offset,
                        declaredEnd,
                        depth + 1);
                    if (childEnd != declaredEnd)
                    {
                        throw new InvalidDataException(
                            $"Skipped FBX node at 0x{start:X} terminates its child list at 0x{childEnd:X}, before its declared end 0x{declaredEnd:X}.");
                    }
                }

                offset = declaredEnd;
                RegisterNode();
            }

            throw new InvalidDataException(
                $"Skipped FBX child list ending at 0x{containingEnd:X} has no terminal null record.");
        }

        private void ValidateSkippedProperties(
            ref int offset,
            int propertyEnd,
            ulong propertyCount)
        {
            for (ulong index = 0; index < propertyCount; index++)
            {
                if ((index & 0x3FF) == 0)
                {
                    _cancellationToken.ThrowIfCancellationRequested();
                }

                EnsureRange(offset, 1, propertyEnd);
                char typeCode = (char)_data[offset++];
                switch (typeCode)
                {
                    case 'C':
                        EnsureRange(offset, 1, propertyEnd);
                        offset += 1;
                        break;
                    case 'Y':
                        EnsureRange(offset, 2, propertyEnd);
                        offset += 2;
                        break;
                    case 'F':
                    case 'I':
                        EnsureRange(offset, 4, propertyEnd);
                        offset += 4;
                        break;
                    case 'D':
                    case 'L':
                        EnsureRange(offset, 8, propertyEnd);
                        offset += 8;
                        break;
                    case 'R':
                    case 'S':
                        {
                            uint length = ReadUInt32(ref offset, propertyEnd);
                            SkipStoredBytes(
                                ref offset,
                                length,
                                propertyEnd,
                                $"'{typeCode}' property");
                            break;
                        }
                    case 'b':
                    case 'c':
                    case 'd':
                    case 'f':
                    case 'i':
                    case 'l':
                        {
                            _ = ReadUInt32(ref offset, propertyEnd);
                            uint encoding = ReadUInt32(ref offset, propertyEnd);
                            uint storedLength = ReadUInt32(
                                ref offset,
                                propertyEnd);
                            if (encoding is not 0 and not 1)
                            {
                                throw new InvalidDataException(
                                    $"Skipped FBX array uses unsupported encoding {encoding}.");
                            }

                            SkipStoredBytes(
                                ref offset,
                                storedLength,
                                propertyEnd,
                                $"'{typeCode}' array");
                            break;
                        }
                    default:
                        throw new InvalidDataException(
                            $"Unsupported FBX property type '{typeCode}' in skipped payload at 0x{offset - 1:X}.");
                }
            }
        }

        private void SkipStoredBytes(
            ref int offset,
            uint length,
            int containingEnd,
            string context)
        {
            if (length > int.MaxValue)
            {
                throw new InvalidDataException(
                    $"Skipped FBX {context} declares {length:N0} stored bytes.");
            }

            int byteLength = checked((int)length);
            EnsureRange(offset, byteLength, containingEnd);
            offset += byteLength;
        }

        private void RegisterNode()
        {
            _nodeCount++;
            if (_nodeCount > _limits.MaximumNodes)
            {
                throw new InvalidDataException(
                    $"FBX contains more than {_limits.MaximumNodes:N0} nodes.");
            }
        }

        private bool ShouldSkipChildPayload(
            string? parentName,
            string nodeName,
            ImmutableArray<FbxProperty>.Builder properties)
        {
            if (_options.Purpose != FbxReadPurpose.Animation ||
                !string.Equals(
                    parentName,
                    "Objects",
                    StringComparison.Ordinal))
            {
                return false;
            }

            if (nodeName is
                "Geometry" or
                "Material" or
                "Texture" or
                "LayeredTexture" or
                "Video")
            {
                return true;
            }

            if (!string.Equals(
                    nodeName,
                    "Deformer",
                    StringComparison.Ordinal))
            {
                return false;
            }

            // BlendShapeChannel owns DeformPercent and can receive facial
            // animation curves without requiring Shape or base-mesh payloads.
            // Skin, Cluster, and BlendShape child arrays are model-domain data.
            string? subtype = properties.Count >= 3
                ? properties[2].Value as string
                : null;
            return !string.Equals(
                subtype,
                "BlendShapeChannel",
                StringComparison.Ordinal);
        }

        private (FbxProperty Property, int Offset) ReadProperty(int offset, int propertyEnd)
        {
            EnsureRange(offset, 1, propertyEnd);
            var typeCode = (char)_data[offset++];
            return typeCode switch
            {
                'Y' => Scalar(typeCode, ReadInt16(ref offset, propertyEnd), offset),
                'I' => Scalar(typeCode, ReadInt32(ref offset, propertyEnd), offset),
                'F' => Scalar(typeCode, ReadSingle(ref offset, propertyEnd), offset),
                'D' => Scalar(typeCode, ReadDouble(ref offset, propertyEnd), offset),
                'L' => Scalar(typeCode, ReadInt64(ref offset, propertyEnd), offset),
                'C' => Scalar(typeCode, ReadBoolean(ref offset, propertyEnd), offset),
                'S' => ReadBlob(typeCode, offset, propertyEnd, decodeText: true),
                'R' => ReadBlob(typeCode, offset, propertyEnd, decodeText: false),
                'f' or 'd' or 'l' or 'i' or 'b' or 'c' =>
                    ReadArray(typeCode, offset, propertyEnd),
                _ => throw new InvalidDataException(
                    $"Unsupported FBX property type '{typeCode}' at 0x{offset - 1:X}."),
            };
        }

        private static (FbxProperty Property, int Offset) Scalar(
            char typeCode,
            object value,
            int offset) =>
            (new FbxProperty(typeCode, value), offset);

        private (FbxProperty Property, int Offset) ReadBlob(
            char typeCode,
            int offset,
            int propertyEnd,
            bool decodeText)
        {
            var length = ReadUInt32(ref offset, propertyEnd);
            if (length > _limits.MaximumArrayBytes)
            {
                throw new InvalidDataException($"FBX blob declares {length:N0} bytes.");
            }

            EnsureRange(offset, checked((int)length), propertyEnd);
            var raw = _data.Slice(offset, checked((int)length));
            offset += checked((int)length);
            if (decodeText)
            {
                ReserveDecodedAllocation(
                    checked(
                        EstimatedValueAllocationBytes +
                        (long)length * sizeof(char)),
                    "string property");
                _cancellationToken.ThrowIfCancellationRequested();
                string value = Encoding.UTF8.GetString(raw);
                _cancellationToken.ThrowIfCancellationRequested();
                return (new FbxProperty(typeCode, value), offset);
            }

            ReserveDecodedAllocation(
                checked(
                    EstimatedValueAllocationBytes +
                    length),
                "raw property");
            EnsureDecodedPeakAllocation(
                length,
                "raw property copy");
            _cancellationToken.ThrowIfCancellationRequested();
            ImmutableArray<byte> rawValue =
                raw.ToArray().ToImmutableArray();
            _cancellationToken.ThrowIfCancellationRequested();
            return (new FbxProperty(typeCode, rawValue), offset);
        }

        private (FbxProperty Property, int Offset) ReadArray(
            char typeCode,
            int offset,
            int propertyEnd)
        {
            var elementCount = ReadUInt32(ref offset, propertyEnd);
            var encoding = ReadUInt32(ref offset, propertyEnd);
            var storedLength = ReadUInt32(ref offset, propertyEnd);
            if (elementCount > _limits.MaximumArrayElements)
            {
                throw new InvalidDataException(
                    $"FBX array declares {elementCount:N0} elements and {storedLength:N0} stored bytes.");
            }

            var elementSize = typeCode switch
            {
                'f' or 'i' => 4,
                'd' or 'l' => 8,
                'b' or 'c' => 1,
                _ => throw new UnreachableException(),
            };
            var expectedLength = checked((int)elementCount * elementSize);
            if (expectedLength > _limits.MaximumArrayBytes)
            {
                throw new InvalidDataException($"FBX array expands to {expectedLength:N0} bytes.");
            }

            if (storedLength > _limits.MaximumArrayBytes)
            {
                throw new InvalidDataException(
                    $"FBX array stores {storedLength:N0} bytes; the configured limit is {_limits.MaximumArrayBytes:N0}.");
            }

            EnsureRange(offset, checked((int)storedLength), propertyEnd);
            var stored = _data.Slice(offset, checked((int)storedLength));
            offset += checked((int)storedLength);
            if (encoding is not 0 and not 1)
            {
                throw new InvalidDataException(
                    $"Unsupported FBX array encoding {encoding}.");
            }

            ReserveDecodedAllocation(
                checked(
                    EstimatedValueAllocationBytes +
                    (long)expectedLength),
                $"'{typeCode}' array");
            long temporaryBytes = encoding == 1
                ? checked(
                    (long)expectedLength +
                    storedLength)
                : expectedLength;
            EnsureDecodedPeakAllocation(
                temporaryBytes,
                $"'{typeCode}' array decode");
            _cancellationToken.ThrowIfCancellationRequested();
            var raw = encoding switch
            {
                0 when stored.Length == expectedLength => stored.ToArray(),
                0 => throw new InvalidDataException(
                    $"Uncompressed FBX array length {stored.Length} != expected {expectedLength}."),
                1 => Inflate(stored, expectedLength),
                _ => throw new UnreachableException(),
            };

            object value = typeCode switch
            {
                'f' => ReadSingles(raw, checked((int)elementCount)),
                'd' => ReadDoubles(raw, checked((int)elementCount)),
                'l' => ReadInt64s(raw, checked((int)elementCount)),
                'i' => ReadInt32s(raw, checked((int)elementCount)),
                'b' => ReadBooleans(raw, checked((int)elementCount)),
                'c' => ReadSBytes(raw, checked((int)elementCount)),
                _ => throw new UnreachableException(),
            };
            _cancellationToken.ThrowIfCancellationRequested();
            return (new FbxProperty(typeCode, value), offset);
        }

        private byte[] Inflate(ReadOnlySpan<byte> stored, int expectedLength)
        {
            using var input = new MemoryStream(stored.ToArray(), writable: false);
            using var zlib = new ZLibStream(input, CompressionMode.Decompress);
            var output = GC.AllocateUninitializedArray<byte>(expectedLength);
            var offset = 0;
            while (offset < output.Length)
            {
                _cancellationToken.ThrowIfCancellationRequested();
                int read = zlib.Read(
                    output,
                    offset,
                    Math.Min(
                        128 * 1024,
                        output.Length - offset));
                if (read == 0)
                {
                    throw new EndOfStreamException(
                        $"FBX compressed array ended after {offset:N0} of {expectedLength:N0} decoded bytes.");
                }

                offset += read;
            }

            _cancellationToken.ThrowIfCancellationRequested();
            if (zlib.ReadByte() != -1)
            {
                throw new InvalidDataException("FBX compressed array expands beyond its declared length.");
            }

            return output;
        }

        private ImmutableArray<float> ReadSingles(byte[] raw, int count)
        {
            var values = ImmutableArray.CreateBuilder<float>(count);
            for (var index = 0; index < count; index++)
            {
                CheckArrayCancellation(index);
                var bits = BinaryPrimitives.ReadInt32LittleEndian(raw.AsSpan(index * 4));
                values.Add(BitConverter.Int32BitsToSingle(bits));
            }

            return values.MoveToImmutable();
        }

        private ImmutableArray<double> ReadDoubles(byte[] raw, int count)
        {
            var values = ImmutableArray.CreateBuilder<double>(count);
            for (var index = 0; index < count; index++)
            {
                CheckArrayCancellation(index);
                var bits = BinaryPrimitives.ReadInt64LittleEndian(raw.AsSpan(index * 8));
                values.Add(BitConverter.Int64BitsToDouble(bits));
            }

            return values.MoveToImmutable();
        }

        private ImmutableArray<long> ReadInt64s(byte[] raw, int count)
        {
            var values = ImmutableArray.CreateBuilder<long>(count);
            for (var index = 0; index < count; index++)
            {
                CheckArrayCancellation(index);
                values.Add(BinaryPrimitives.ReadInt64LittleEndian(raw.AsSpan(index * 8)));
            }

            return values.MoveToImmutable();
        }

        private ImmutableArray<int> ReadInt32s(byte[] raw, int count)
        {
            var values = ImmutableArray.CreateBuilder<int>(count);
            for (var index = 0; index < count; index++)
            {
                CheckArrayCancellation(index);
                values.Add(BinaryPrimitives.ReadInt32LittleEndian(raw.AsSpan(index * 4)));
            }

            return values.MoveToImmutable();
        }

        private ImmutableArray<bool> ReadBooleans(byte[] raw, int count)
        {
            var values = ImmutableArray.CreateBuilder<bool>(count);
            for (var index = 0; index < count; index++)
            {
                CheckArrayCancellation(index);
                values.Add(raw[index] != 0);
            }

            return values.MoveToImmutable();
        }

        private ImmutableArray<sbyte> ReadSBytes(byte[] raw, int count)
        {
            var values = ImmutableArray.CreateBuilder<sbyte>(count);
            for (var index = 0; index < count; index++)
            {
                CheckArrayCancellation(index);
                values.Add(unchecked((sbyte)raw[index]));
            }

            return values.MoveToImmutable();
        }

        private void CheckArrayCancellation(int index)
        {
            if ((index & 0x3FFF) == 0)
            {
                _cancellationToken.ThrowIfCancellationRequested();
            }
        }

        private void ReserveDecodedAllocation(
            long byteCount,
            string context)
        {
            long next;
            try
            {
                next = checked(
                    _decodedAllocationBytes +
                    byteCount);
            }
            catch (OverflowException error)
            {
                throw new InvalidDataException(
                    $"FBX {context} overflows the aggregate decoded-allocation accounting.",
                    error);
            }

            if (next >
                _limits.MaximumDecodedAllocationBytes)
            {
                throw new InvalidDataException(
                    $"FBX {context} exceeds the aggregate decoded-allocation budget of {_limits.MaximumDecodedAllocationBytes:N0} bytes.");
            }

            _decodedAllocationBytes = next;
        }

        private void EnsureDecodedPeakAllocation(
            long temporaryBytes,
            string context)
        {
            long peak;
            try
            {
                peak = checked(
                    _decodedAllocationBytes +
                    temporaryBytes);
            }
            catch (OverflowException error)
            {
                throw new InvalidDataException(
                    $"FBX {context} overflows the aggregate decoded-allocation accounting.",
                    error);
            }

            if (peak >
                _limits.MaximumDecodedAllocationBytes)
            {
                throw new InvalidDataException(
                    $"FBX {context} exceeds the aggregate decoded-allocation budget of {_limits.MaximumDecodedAllocationBytes:N0} bytes.");
            }
        }

        private bool IsZeroRecord(int offset, int length)
        {
            for (var index = 0; index < length; index++)
            {
                if (_data[offset + index] != 0)
                {
                    return false;
                }
            }

            return true;
        }

        private void EnsureRange(int offset, int length, int containingEnd)
        {
            if (offset < 0 ||
                length < 0 ||
                offset > containingEnd - length ||
                containingEnd > _data.Length)
            {
                throw new EndOfStreamException(
                    $"FBX read 0x{offset:X}+{length} exceeds container ending at 0x{containingEnd:X}.");
            }
        }

        private short ReadInt16(ref int offset, int end)
        {
            EnsureRange(offset, 2, end);
            var value = BinaryPrimitives.ReadInt16LittleEndian(_data[offset..]);
            offset += 2;
            return value;
        }

        private int ReadInt32(ref int offset, int end)
        {
            EnsureRange(offset, 4, end);
            var value = BinaryPrimitives.ReadInt32LittleEndian(_data[offset..]);
            offset += 4;
            return value;
        }

        private uint ReadUInt32(ref int offset, int end)
        {
            EnsureRange(offset, 4, end);
            var value = BinaryPrimitives.ReadUInt32LittleEndian(_data[offset..]);
            offset += 4;
            return value;
        }

        private long ReadInt64(ref int offset, int end)
        {
            EnsureRange(offset, 8, end);
            var value = BinaryPrimitives.ReadInt64LittleEndian(_data[offset..]);
            offset += 8;
            return value;
        }

        private float ReadSingle(ref int offset, int end)
        {
            var bits = ReadInt32(ref offset, end);
            return BitConverter.Int32BitsToSingle(bits);
        }

        private double ReadDouble(ref int offset, int end)
        {
            var bits = ReadInt64(ref offset, end);
            return BitConverter.Int64BitsToDouble(bits);
        }

        private bool ReadBoolean(ref int offset, int end)
        {
            EnsureRange(offset, 1, end);
            return _data[offset++] != 0;
        }
    }
}
