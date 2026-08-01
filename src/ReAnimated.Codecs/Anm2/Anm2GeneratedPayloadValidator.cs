namespace ReAnimated.Codecs.Anm2;

/// <summary>
/// Applies the strict structural checks required for newly generated DL1 ANM2
/// payloads. The general reader remains tolerant of stock compatibility length
/// fields, while authoring output must describe its page layout exactly.
/// </summary>
public static class Anm2GeneratedPayloadValidator
{
    public static Anm2Clip Validate(
        ReadOnlySpan<byte> payload,
        string resourceName = "",
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var displayName = string.IsNullOrWhiteSpace(resourceName)
            ? "<generated>"
            : resourceName.Trim();
        Anm2Clip clip;
        try
        {
            clip = Anm2Reader.Read(
                payload,
                displayName,
                cancellationToken: cancellationToken);
        }
        catch (InvalidDataException exception)
        {
            throw new InvalidDataException(
                $"Generated animation '{displayName}' is not a supported DL1 ANM2 clip: " +
                exception.Message,
                exception);
        }

        var errors = new List<string>();
        cancellationToken.ThrowIfCancellationRequested();
        if (clip.Header.PageCount == 0)
        {
            errors.Add("page_count is zero");
        }

        if (clip.Header.DeclaredLength != payload.Length)
        {
            errors.Add(
                $"declared_length {clip.Header.DeclaredLength} != actual length {payload.Length}");
        }

        var pageBytes =
            (long)clip.Header.DeclaredLength - clip.Header.PageOffset;
        if (pageBytes < 0)
        {
            errors.Add("declared_length ends before page_offset");
        }
        else
        {
            var expectedPageCount =
                (pageBytes + Anm2Header.PageSize - 1) /
                Anm2Header.PageSize;
            if (clip.Header.PageCount != expectedPageCount)
            {
                errors.Add(
                    $"page_count {clip.Header.PageCount} != " +
                    "ceil((declared_length - page_offset) / 65536) " +
                    expectedPageCount);
            }
        }

        var expectedSpan = clip.Header.FrameCount - 1;
        var actualSpan = clip.PageFrameSpans.Sum(static span => (int)span);
        if (actualSpan != expectedSpan)
        {
            errors.Add(
                $"page spans cover {actualSpan} frames, expected {expectedSpan}");
        }

        if ((clip.Header.DurationKeyCount & 0xFFFF) == 0)
        {
            errors.Add("duration/control key count is zero");
        }

        for (var pageIndex = 0; pageIndex < clip.Header.PageCount; pageIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var pageOffset =
                (long)clip.Header.PageOffset +
                ((long)Anm2Header.PageSize * pageIndex);
            if (pageOffset + 32 > clip.Header.DeclaredLength)
            {
                errors.Add($"page {pageIndex} starts past the declared data");
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (errors.Count != 0)
        {
            throw new InvalidDataException(
                $"Generated animation '{displayName}' has an invalid ANM2 page layout: " +
                string.Join("; ", errors));
        }

        return clip;
    }
}
