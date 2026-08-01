namespace ReAnimated.Codecs.Anm2;

/// <summary>
/// Chrome Engine's case-insensitive 32-bit resource/name hash used by DL1
/// ANM2 bone and mimic descriptors. Implicit descriptor names must be ASCII;
/// callers with another naming scheme need an explicit descriptor.
/// </summary>
public static class Dl1NameHash
{
    public static uint Compute(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        uint value = 0;
        foreach (char character in name)
        {
            if (character > 0x7f)
            {
                throw new ArgumentException(
                    "DL1 implicit descriptor names must contain only ASCII characters.",
                    nameof(name));
            }

            char lower = char.ToLowerInvariant(character);
            value = unchecked(
                (uint)(byte)lower +
                (41u * value));
        }

        return value;
    }
}
