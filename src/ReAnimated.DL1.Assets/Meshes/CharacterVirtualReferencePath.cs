namespace ReAnimated.DL1.Assets.Meshes;

/// <summary>A catalog path derived from source text, without touching the host filesystem.</summary>
public sealed record CharacterVirtualReferencePathResult(
    string CanonicalName,
    bool IsRelative,
    bool RequiresExactLookup);

public static class CharacterVirtualReferencePath
{
    private const int MaximumNameLength = 4096;

    public static CharacterVirtualReferencePathResult Resolve(
        string request, string? declaringVirtualFileName = null)
    {
        string name = Normalize(request, nameof(request));
        string[] segments = Split(name, nameof(request));
        if (segments[^1] is "." or "..")
            throw new InvalidDataException("A virtual reference must end in a resource name.");
        bool dottedRelative = segments[0] is "." or "..";
        bool bareName = segments.Length == 1 && !dottedRelative;
        if (!dottedRelative && segments.Any(segment => segment is "." or ".."))
            throw new InvalidDataException("Dot segments must begin an explicitly relative virtual reference.");

        string[] declaringSegments = declaringVirtualFileName is null
            ? [] : Split(Normalize(declaringVirtualFileName, nameof(declaringVirtualFileName)), nameof(declaringVirtualFileName));
        if (declaringSegments.Any(segment => segment is "." or ".."))
            throw new InvalidDataException("The declaring virtual file path must already be canonical.");
        if (dottedRelative && declaringSegments.Length == 0)
            throw new InvalidDataException("A dotted relative reference needs a declaring virtual file.");

        var output = new List<string>();
        if (bareName || dottedRelative)
            output.AddRange(declaringSegments.Take(Math.Max(0, declaringSegments.Length - 1)));
        foreach (string segment in segments)
        {
            if (segment == ".") continue;
            if (segment == "..")
            {
                if (output.Count == 0)
                    throw new InvalidDataException("A virtual reference escapes above the catalog root.");
                output.RemoveAt(output.Count - 1);
            }
            else output.Add(segment);
        }
        if (output.Count == 0)
            throw new InvalidDataException("A virtual reference must name a resource.");
        string canonical = string.Join('/', output);
        if (canonical.Length > MaximumNameLength)
            throw new InvalidDataException("A virtual reference exceeds the catalog path bound.");
        return new(canonical, bareName || dottedRelative, !bareName);
    }

    private static string Normalize(string value, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(value, parameterName);
        if (value.Length == 0 || value.Length > MaximumNameLength || value.Any(char.IsControl))
            throw new InvalidDataException("A virtual reference is empty, oversized, or contains control characters.");
        string normalized = value.Trim().Replace('\\', '/');
        if (normalized.Length == 0 || normalized.StartsWith('/') || normalized.Contains(':'))
            throw new InvalidDataException("A virtual reference cannot be absolute or empty.");
        return normalized;
    }

    private static string[] Split(string value, string parameterName)
    {
        string[] segments = value.Split('/');
        if (segments.Any(string.IsNullOrEmpty))
            throw new InvalidDataException($"{parameterName} has an empty virtual path segment.");
        return segments;
    }
}
