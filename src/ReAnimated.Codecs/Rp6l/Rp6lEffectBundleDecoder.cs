using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;

namespace ReAnimated.Codecs.Rp6l;

public sealed record Rp6lEffectDefinition(string Name, sbyte Kind, string SourceText,
    int EntryOffset, int TextOffset, int TextByteLength, string ContentSha256, int EntryByteLength);

public static class Rp6lEffectBundleDecoder
{
    public const int MaximumBytes = 64 * 1024 * 1024;
    public const int MaximumDefinitions = 100_000;
    public const int MaximumNameBytes = 4096;
    public const int MaximumSourceBytes = 4 * 1024 * 1024;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public static ImmutableArray<Rp6lEffectDefinition> Decode(ReadOnlySpan<byte> payload,
        CancellationToken cancellationToken = default)
    {
        if(payload.IsEmpty || payload.Length > MaximumBytes) throw new InvalidDataException("Invalid effect bundle size.");
        var definitions=ImmutableArray.CreateBuilder<Rp6lEffectDefinition>();
        var names=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int offset=0;
        while(offset < payload.Length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int start=offset;
            int nameLength=payload[offset..].IndexOf((byte)0);
            if(nameLength < 0 || nameLength > MaximumNameBytes) throw new InvalidDataException("Effect name is unterminated or oversized.");
            if(nameLength == 0)
            {
                if(offset != payload.Length-1) throw new InvalidDataException("Effect bundle has bytes after its terminator.");
                return definitions.ToImmutable();
            }
            if(definitions.Count >= MaximumDefinitions) throw new InvalidDataException("Too many effect definitions.");
            string name=DecodeUtf8(payload.Slice(offset,nameLength));
            if(name.Any(char.IsControl) || name != name.Trim() || name.StartsWith('/') || name.Contains('\\') || name.Contains(':') ||
                name.Split('/').Any(part=>part is "" or "." or "..") || !names.Add(name))
                throw new InvalidDataException("Effect names are unsafe or ambiguous.");
            offset=checked(offset+nameLength+1);
            if(offset >= payload.Length) throw new InvalidDataException("Effect kind is missing.");
            sbyte kind=unchecked((sbyte)payload[offset++]);
            int textOffset=offset;
            int textLength=payload[offset..].IndexOf((byte)0);
            if(textLength < 0 || textLength > MaximumSourceBytes) throw new InvalidDataException("Effect source is unterminated or oversized.");
            var textBytes=payload.Slice(offset,textLength);
            string source=DecodeUtf8(textBytes);
            offset=checked(offset+textLength+1);
            definitions.Add(new(name,kind,source,start,textOffset,textLength,
                Convert.ToHexStringLower(SHA256.HashData(textBytes)),offset-start));
        }
        throw new InvalidDataException("Effect bundle terminator is missing.");
    }

    private static string DecodeUtf8(ReadOnlySpan<byte> bytes)
    {
        try { return StrictUtf8.GetString(bytes); }
        catch(DecoderFallbackException exception){throw new InvalidDataException("Effect definition is not valid UTF-8.",exception);}
    }
}