using System.Security.Cryptography;
using ReAnimated.Core.Domain;

namespace ReAnimated.Codecs.Anm2;

public sealed record SynchronizedMimicAnimation(
    AnimationClip Mimic,
    AnimationClip Synchronized,
    string Sha256)
{
    public FacialClipTiming Timing { get; init; } = new();

    public Anm2TrackPartition? Partition { get; init; }
}

/// <summary>
/// Hash-checks and decodes a separately stored mimic ANM2 against one exact
/// retail target rig, then combines it with the body clip on the saved
/// rational timeline.
/// </summary>
public static class SynchronizedMimicAnm2Loader
{
    public static async Task<SynchronizedMimicAnimation> LoadAsync(
        string path,
        string expectedSha256,
        RigDefinition targetRig,
        AnimationClip body,
        FrameRate expectedFrameRate,
        long expectedFrameCount,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedSha256);
        ArgumentNullException.ThrowIfNull(targetRig);
        ArgumentNullException.ThrowIfNull(body);
        if (body.FrameRate != expectedFrameRate ||
            body.FrameCount != expectedFrameCount)
        {
            throw new InvalidDataException(
                "The loaded body rational frame rate or frame count differs from the saved animation.");
        }

        if (expectedSha256.Length != 64 ||
            expectedSha256.Any(static value => !Uri.IsHexDigit(value)))
        {
            throw new ArgumentException(
                "The expected mimic SHA-256 must contain 64 hexadecimal characters.",
                nameof(expectedSha256));
        }

        string fullPath = Path.GetFullPath(path);
        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException(
                "The mimic ANM2 was not found.",
                fullPath);
        }

        if (!string.Equals(
                Path.GetExtension(fullPath),
                ".anm2",
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                "A synchronized mimic asset must be a DL1 ANM2 file.");
        }

        string actualSha256;
        await using (FileStream stream = new(
                         fullPath,
                         FileMode.Open,
                         FileAccess.Read,
                         FileShare.Read,
                         128 * 1024,
                         FileOptions.Asynchronous |
                         FileOptions.SequentialScan))
        {
            actualSha256 = Convert.ToHexString(
                    await SHA256.HashDataAsync(
                        stream,
                        cancellationToken).ConfigureAwait(false))
                .ToLowerInvariant();
        }

        if (!string.Equals(
                actualSha256,
                expectedSha256,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                "The mimic ANM2 SHA-256 differs from the saved project asset.");
        }

        Anm2Clip source = await new Anm2Decoder()
            .DecodeFileAsync(
                fullPath,
                cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        Anm2PartitionedImportResult partitioned =
            Anm2TrackPartitioner.Partition(
                source,
                targetRig,
                expectedFrameRate,
                cancellationToken);
        if (partitioned.Partition.RequiresReview)
        {
            throw new InvalidDataException(
                "Mimic ANM2 contains duplicated or bone/morph-colliding descriptors that require explicit review: " +
                string.Join(", ", partitioned.Partition.AmbiguousDescriptors.Select(
                    static descriptor => $"0x{descriptor:X8}")) +
                ".");
        }

        AnimationClip mimic = partitioned.FacialClip;
        FacialClipTiming timing = FacialClipTiming.ForClip(mimic);
        AnimationClip synchronized =
            AnimationClipSynchronization.Synchronize(
                body,
                mimic,
                timing);
        if (synchronized.FrameRate != expectedFrameRate ||
            synchronized.FrameCount != expectedFrameCount)
        {
            throw new InvalidDataException(
                "The synchronized body and mimic rational frame rate or frame count differs from the saved animation.");
        }

        return new SynchronizedMimicAnimation(
            mimic,
            synchronized,
            actualSha256)
        {
            Timing = timing,
            Partition = partitioned.Partition,
        };
    }
}
