using System.Text;

namespace ReAnimated.DL1.Assets.Materials;

/// <summary>
/// Implements the filename-only, ASCII-lowercase seeded CRC32 lookup used by
/// DL1's material and texture managers.
/// </summary>
public static class Dl1ResourceNameHash
{
    public const uint RuntimeSeed = 0x811C9DC5;

    public static uint Compute(string resourceName)
    {
        string normalized = NormalizeFileName(resourceName);
        uint crc = RuntimeSeed ^ uint.MaxValue;
        foreach (byte value in Encoding.ASCII.GetBytes(normalized))
        {
            crc ^= value;
            for (int bit = 0; bit < 8; bit++)
            {
                uint mask = unchecked((uint)-(int)(crc & 1));
                crc = (crc >> 1) ^ (0xEDB88320U & mask);
            }
        }

        return crc ^ uint.MaxValue;
    }

    public static uint ComputeTextureResource(string resourceName)
    {
        string fileName = NormalizeFileName(resourceName);
        if (!fileName.EndsWith(".dds", StringComparison.Ordinal))
        {
            fileName = string.Concat(fileName, ".dds");
        }

        return Compute(fileName);
    }

    public static string NormalizeFileName(string resourceName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(resourceName);
        string fileName = resourceName
            .Replace('\\', '/')
            .Split(
                '/',
                StringSplitOptions.RemoveEmptyEntries |
                StringSplitOptions.TrimEntries)
            .LastOrDefault()
            ?? string.Empty;
        if (fileName.Length == 0
            || fileName.Contains('\0')
            || fileName.Any(static value => value > 0x7F))
        {
            throw new InvalidDataException(
                $"DL1 resource name '{resourceName}' is not a safe ASCII filename.");
        }

        return fileName.ToLowerInvariant();
    }
}
