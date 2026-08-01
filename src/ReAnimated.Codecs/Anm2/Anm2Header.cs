using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Security.Cryptography;

namespace ReAnimated.Codecs.Anm2;

public readonly record struct Anm2Header(
    ushort FormatVersion,
    ushort SamplerVersion,
    ushort FrameCount,
    ushort TrackCount,
    ushort PageCount,
    ushort PageOffset,
    uint DeclaredLength,
    uint DurationKeyCount,
    uint Unknown24,
    uint Unknown28)
{
    public const int Size = 32;
    public const ushort Dl1FormatVersion = 42;
    public const ushort Dl1SamplerVersion = 1;
    public const int PageSize = 0x10000;

    public static Anm2Header Parse(ReadOnlySpan<byte> data)
    {
        if (data.Length < Size)
        {
            throw new InvalidDataException($"ANM2 payload is smaller than the {Size}-byte header.");
        }

        if (!data[..4].SequenceEqual("ANM2"u8))
        {
            throw new InvalidDataException(
                $"Expected ANM2 magic, found {Convert.ToHexString(data[..4])}.");
        }

        return new Anm2Header(
            BinaryPrimitives.ReadUInt16LittleEndian(data[4..]),
            BinaryPrimitives.ReadUInt16LittleEndian(data[6..]),
            BinaryPrimitives.ReadUInt16LittleEndian(data[8..]),
            BinaryPrimitives.ReadUInt16LittleEndian(data[10..]),
            BinaryPrimitives.ReadUInt16LittleEndian(data[12..]),
            BinaryPrimitives.ReadUInt16LittleEndian(data[14..]),
            BinaryPrimitives.ReadUInt32LittleEndian(data[16..]),
            BinaryPrimitives.ReadUInt32LittleEndian(data[20..]),
            BinaryPrimitives.ReadUInt32LittleEndian(data[24..]),
            BinaryPrimitives.ReadUInt32LittleEndian(data[28..]));
    }

    public void Write(Span<byte> destination)
    {
        if (destination.Length < Size)
        {
            throw new ArgumentException($"Destination must contain at least {Size} bytes.", nameof(destination));
        }

        "ANM2"u8.CopyTo(destination);
        BinaryPrimitives.WriteUInt16LittleEndian(destination[4..], FormatVersion);
        BinaryPrimitives.WriteUInt16LittleEndian(destination[6..], SamplerVersion);
        BinaryPrimitives.WriteUInt16LittleEndian(destination[8..], FrameCount);
        BinaryPrimitives.WriteUInt16LittleEndian(destination[10..], TrackCount);
        BinaryPrimitives.WriteUInt16LittleEndian(destination[12..], PageCount);
        BinaryPrimitives.WriteUInt16LittleEndian(destination[14..], PageOffset);
        BinaryPrimitives.WriteUInt32LittleEndian(destination[16..], DeclaredLength);
        BinaryPrimitives.WriteUInt32LittleEndian(destination[20..], DurationKeyCount);
        BinaryPrimitives.WriteUInt32LittleEndian(destination[24..], Unknown24);
        BinaryPrimitives.WriteUInt32LittleEndian(destination[28..], Unknown28);
    }

    public bool HasKnownLength(int actualLength) =>
        DeclaredLength == actualLength || Unknown24 == actualLength || Unknown28 == actualLength;

    public void ValidateContainer(int actualLength)
    {
        if (FormatVersion != Dl1FormatVersion)
        {
            throw new InvalidDataException(
                $"ANM2 format {FormatVersion} is not the supported DL1 format {Dl1FormatVersion}.");
        }

        if (SamplerVersion != Dl1SamplerVersion)
        {
            throw new InvalidDataException(
                $"ANM2 sampler {SamplerVersion} is not the supported DL1 sampler {Dl1SamplerVersion}.");
        }

        if (FrameCount == 0 || TrackCount == 0)
        {
            throw new InvalidDataException("ANM2 frame and track counts must be non-zero.");
        }

        if (DeclaredLength > actualLength)
        {
            throw new InvalidDataException(
                $"ANM2 declares {DeclaredLength} bytes but only {actualLength} are available.");
        }

        var descriptorBytes = checked(TrackCount * sizeof(uint));
        var spanBytes = checked(PageCount * sizeof(ushort));
        if (PageOffset < Size + descriptorBytes + spanBytes || PageOffset > actualLength)
        {
            throw new InvalidDataException("ANM2 page offset overlaps header tables or exceeds the payload.");
        }
    }
}

public sealed record Anm2Clip(
    string Name,
    Anm2Header Header,
    ImmutableArray<uint> TrackDescriptors,
    ImmutableArray<ushort> PageFrameSpans,
    ImmutableArray<byte> OriginalBytes,
    string Sha256,
    ImmutableArray<string> Warnings)
{
    public ReadOnlyMemory<byte> EncodePreservingBody() => OriginalBytes.AsMemory();
}

public static class Anm2Reader
{
    public const int DefaultMaximumPayloadBytes = 512 * 1024 * 1024;

    public static Anm2Clip Read(
        ReadOnlySpan<byte> data,
        string name = "",
        int maximumPayloadBytes = DefaultMaximumPayloadBytes,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (data.Length > maximumPayloadBytes)
        {
            throw new InvalidDataException(
                $"ANM2 payload is {data.Length:N0} bytes; the configured limit is {maximumPayloadBytes:N0}.");
        }

        var header = Anm2Header.Parse(data);
        header.ValidateContainer(data.Length);

        var descriptorBytes = checked(header.TrackCount * sizeof(uint));
        var pageSpanBytes = checked(header.PageCount * sizeof(ushort));
        if (Anm2Header.Size + descriptorBytes + pageSpanBytes > data.Length)
        {
            throw new InvalidDataException("ANM2 header-side tables exceed the payload.");
        }

        var descriptors = ImmutableArray.CreateBuilder<uint>(header.TrackCount);
        var descriptorSpan = data.Slice(Anm2Header.Size, descriptorBytes);
        for (var index = 0; index < header.TrackCount; index++)
        {
            if ((index & 0xFF) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            descriptors.Add(BinaryPrimitives.ReadUInt32LittleEndian(descriptorSpan[(index * 4)..]));
        }

        var pageSpans = ImmutableArray.CreateBuilder<ushort>(header.PageCount);
        var pageSpanStart = Anm2Header.Size + descriptorBytes;
        var pageSpanSpan = data.Slice(pageSpanStart, pageSpanBytes);
        for (var index = 0; index < header.PageCount; index++)
        {
            if ((index & 0xFF) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            pageSpans.Add(BinaryPrimitives.ReadUInt16LittleEndian(pageSpanSpan[(index * 2)..]));
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (pageSpans.Sum(static value => (int)value) < header.FrameCount - 1)
        {
            throw new InvalidDataException("ANM2 page spans do not cover frame_count - 1.");
        }

        var warnings = ImmutableArray.CreateBuilder<string>();
        if (!header.HasKnownLength(data.Length))
        {
            warnings.Add($"No known ANM2 length field matches the actual length {data.Length}.");
        }
        else if (header.DeclaredLength != data.Length)
        {
            warnings.Add("The payload length is stored in a compatibility field rather than offset 0x10.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        var bytes = data.ToArray().ToImmutableArray();
        cancellationToken.ThrowIfCancellationRequested();
        var sha256 = ComputeSha256(data, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        return new Anm2Clip(
            name,
            header,
            descriptors.MoveToImmutable(),
            pageSpans.MoveToImmutable(),
            bytes,
            sha256,
            warnings.ToImmutable());
    }

    public static async Task<Anm2Clip> ReadFileAsync(
        string path,
        int maximumPayloadBytes = DefaultMaximumPayloadBytes,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);

        if (stream.Length > maximumPayloadBytes)
        {
            throw new InvalidDataException(
                $"ANM2 payload is {stream.Length:N0} bytes; the configured limit is {maximumPayloadBytes:N0}.");
        }

        var bytes = GC.AllocateUninitializedArray<byte>(checked((int)stream.Length));
        await stream.ReadExactlyAsync(bytes, cancellationToken).ConfigureAwait(false);
        return Read(
            bytes,
            Path.GetFileName(path),
            maximumPayloadBytes,
            cancellationToken);
    }

    private static string ComputeSha256(
        ReadOnlySpan<byte> data,
        CancellationToken cancellationToken)
    {
        const int chunkSize = 1024 * 1024;
        using IncrementalHash hash =
            IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        for (var offset = 0; offset < data.Length; offset += chunkSize)
        {
            cancellationToken.ThrowIfCancellationRequested();
            hash.AppendData(
                data.Slice(
                    offset,
                    Math.Min(chunkSize, data.Length - offset)));
        }

        cancellationToken.ThrowIfCancellationRequested();
        return Convert.ToHexString(hash.GetHashAndReset());
    }
}
