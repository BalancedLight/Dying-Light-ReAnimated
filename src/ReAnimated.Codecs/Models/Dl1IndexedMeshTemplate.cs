using System.Collections.Immutable;
using System.Globalization;

namespace ReAnimated.Codecs.Models;

public sealed record Dl1IndexedMeshName(string Template, int Count, int Index, string Name);

public static class Dl1IndexedMeshTemplate
{
    public const int MaximumCount = 100;

    public static int ParseCount(string literal)
    {
        if (!int.TryParse(literal, NumberStyles.Integer, CultureInfo.InvariantCulture, out int count) ||
            count is < 0 or > MaximumCount)
            throw new FormatException("Indexed mesh count must be an integer from 0 to 100.");
        return count;
    }

    public static ImmutableArray<Dl1IndexedMeshName> Expand(string template, int count)
    {
        if (count is < 0 or > MaximumCount)
            throw new FormatException("Indexed mesh count must be an integer from 0 to 100.");
        if (string.IsNullOrWhiteSpace(template) || template.Length > 4096 || template.Any(char.IsControl))
            throw new FormatException("Indexed mesh template must be bounded nonempty text without control characters.");
        if (count == 0) return [];
        int marker = template.IndexOf("_xx.", StringComparison.Ordinal);
        if (marker < 0) marker = template.IndexOf("_XX.", StringComparison.Ordinal);
        if (marker < 0)
            throw new FormatException("Indexed mesh template requires an _xx. or _XX. marker.");
        var names = ImmutableArray.CreateBuilder<Dl1IndexedMeshName>(count);
        for (int index = 0; index < count; index++)
        {
            string name = template[..marker] + "_" + index.ToString("D2", CultureInfo.InvariantCulture) + "." + template[(marker + 4)..];
            int query = name.IndexOf('?', StringComparison.Ordinal);
            if (query >= 0) name = name[..query];
            if (name.Length == 0) throw new FormatException("Indexed mesh template produces an empty name.");
            names.Add(new(template, count, index, name));
        }
        return names.MoveToImmutable();
    }
}
