using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace ReAnimated.Codecs.Models;

/// <summary>
/// One <c>!include</c> directive. <see cref="ResourceName"/> is extensionless
/// so it can be compared against a type-322 identity directly; the raw
/// extension is preserved because <c>.def</c> includes pull in event and
/// action vocabulary rather than another animation script.
/// </summary>
public sealed record AnimationScriptInclude(
    string ResourceName,
    string Extension)
{
    public bool IsAnimationScript => string.Equals(
        Extension,
        ".scr",
        StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// A numeric <c>SeqTrack</c> field. Stock scripts pass named constants from
/// their <c>.def</c> includes as well as literals - <c>m_fpp_hitreactions.scr</c>
/// blends on <c>CROWD_BUMP_BLENDIN_TIME</c>, for instance - so the raw token is
/// always preserved and <see cref="Number"/> is null when it is a symbol this
/// parser does not resolve.
/// </summary>
public readonly record struct AnimationScriptValue(string Text, float? Number)
{
    public bool IsSymbolic => Number is null;

    public override string ToString() => Text;
}

/// <summary>
/// One <c>SeqTrack</c> row. <see cref="HasEventBlock"/> records whether the
/// row was followed by a brace block - the event rows the binary type-322
/// writer cannot represent.
/// </summary>
public sealed record AnimationScriptSeqTrack(
    string Name,
    string Anm2Name,
    AnimationScriptValue StartFrame,
    AnimationScriptValue EndFrame,
    AnimationScriptValue FramesPerSecond,
    AnimationScriptValue Enabled,
    AnimationScriptValue Blend,
    bool HasEventBlock);

/// <summary>
/// Reads the loose <c>.scr</c> animation-script source grammar.
/// </summary>
/// <remarks>
/// <para>
/// Measured against the stock 1.55 animscript tree (204 scripts in
/// <c>Data0.pak</c> and <c>DataDLC49_0.pak</c>, 11,699 <c>SeqTrack</c> rows):
/// every row carries exactly seven arguments and none spans a newline inside
/// its parentheses, so the argument list is parsed as a single-line, 7-field
/// tuple and anything else is rejected rather than guessed at. The two string
/// fields are always quoted; the five numeric fields are literals except for
/// seven rows that pass a named constant, so those accept a symbol too.
/// </para>
/// <para>
/// Event blocks are recognised structurally only. Their contents stay opaque,
/// matching the position taken in <c>docs/DL1_ANIMATION_SCR_EVENT_PARITY.md</c>:
/// this parser never claims to understand what an <c>Event</c> row means.
/// </para>
/// </remarks>
public static partial class AnimationScriptSourceParser
{
    private const int MaximumSourceLength = 8 * 1024 * 1024;
    private const int SeqTrackArgumentCount = 7;

    [GeneratedRegex(
        "!\\s*include\\s*\\(\\s*\"(?<resource>[^\"\\r\\n]*)\"\\s*\\)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant |
        RegexOptions.IgnoreCase)]
    private static partial Regex IncludeDirective();

    [GeneratedRegex(
        "\\bSeqTrack\\s*\\(",
        RegexOptions.Compiled | RegexOptions.CultureInvariant |
        RegexOptions.IgnoreCase)]
    private static partial Regex SeqTrackOpening();

    /// <summary>
    /// Removes line and block comments while leaving string literals intact,
    /// so a commented-out directive such as
    /// <c>//!include( "Anims_Player.scr")</c> - which really does appear in
    /// stock <c>anims_man_all.scr</c> - is not mistaken for a live include.
    /// </summary>
    public static string StripComments(string source)
    {
        ArgumentNullException.ThrowIfNull(source);
        StringBuilder builder = new(source.Length);
        bool inString = false;
        for (int index = 0; index < source.Length; index++)
        {
            char character = source[index];
            if (inString)
            {
                if (character == '\\' && index + 1 < source.Length)
                {
                    builder.Append(character).Append(source[index + 1]);
                    index++;
                    continue;
                }

                if (character == '"')
                {
                    inString = false;
                }

                builder.Append(character);
                continue;
            }

            if (character == '"')
            {
                inString = true;
                builder.Append(character);
                continue;
            }

            if (character == '/' && index + 1 < source.Length)
            {
                if (source[index + 1] == '/')
                {
                    while (index < source.Length && source[index] != '\n')
                    {
                        index++;
                    }

                    if (index < source.Length)
                    {
                        builder.Append('\n');
                    }

                    continue;
                }

                if (source[index + 1] == '*')
                {
                    int close = source.IndexOf(
                        "*/",
                        index + 2,
                        StringComparison.Ordinal);
                    index = close < 0 ? source.Length : close + 1;
                    continue;
                }
            }

            builder.Append(character);
        }

        return builder.ToString();
    }

    /// <summary>
    /// Replaces the body of every string literal with filler, keeping the
    /// quotes, the length and every other character in place.
    /// </summary>
    /// <remarks>
    /// Directive and row matching has to run over a text where quoted spans
    /// cannot look like syntax. Stock event arguments are opaque and may
    /// contain anything - a sound name holding <c>SeqTrack(</c> would
    /// otherwise be read as a malformed row, and one holding
    /// <c>!include(...)</c> as a dependency the script does not have. Offsets
    /// are preserved so matches index back into the original text.
    /// </remarks>
    private static string MaskStringLiterals(string source)
    {
        char[] masked = source.ToCharArray();
        bool inString = false;
        for (int index = 0; index < masked.Length; index++)
        {
            char character = masked[index];
            if (!inString)
            {
                inString = character == '"';
                continue;
            }

            if (character == '\\' && index + 1 < masked.Length)
            {
                masked[index] = '_';
                masked[index + 1] = '_';
                index++;
                continue;
            }

            if (character == '"')
            {
                inString = false;
                continue;
            }

            masked[index] = '_';
        }

        return new string(masked);
    }

    public static ImmutableArray<AnimationScriptInclude> ParseIncludes(
        string source)
    {
        ArgumentNullException.ThrowIfNull(source);
        EnsureParseable(source);
        string clean = StripComments(source);
        string scan = MaskStringLiterals(clean);
        ImmutableArray<AnimationScriptInclude>.Builder includes =
            ImmutableArray.CreateBuilder<AnimationScriptInclude>();
        foreach (Match match in IncludeDirective().Matches(scan))
        {
            Group group = match.Groups["resource"];
            string resource = clean
                .Substring(group.Index, group.Length)
                .Trim();
            if (resource.Length == 0)
            {
                continue;
            }

            string extension = Path.GetExtension(resource);
            string name = string.IsNullOrEmpty(extension)
                ? resource
                : resource[..^extension.Length];
            if (name.Length == 0)
            {
                continue;
            }

            includes.Add(new AnimationScriptInclude(name, extension));
        }

        return includes.ToImmutable();
    }

    /// <summary>
    /// Parses every <c>SeqTrack</c> row. Throws
    /// <see cref="InvalidDataException"/> on a row that does not match the
    /// measured seven-argument shape rather than silently dropping it - a
    /// dropped row would become a missing animation at deployment time.
    /// </summary>
    public static ImmutableArray<AnimationScriptSeqTrack> ParseSeqTracks(
        string source)
    {
        ArgumentNullException.ThrowIfNull(source);
        EnsureParseable(source);
        string clean = StripComments(source);
        string scan = MaskStringLiterals(clean);
        ImmutableArray<AnimationScriptSeqTrack>.Builder tracks =
            ImmutableArray.CreateBuilder<AnimationScriptSeqTrack>();
        foreach (Match match in SeqTrackOpening().Matches(scan))
        {
            int argumentStart = match.Index + match.Length;
            int argumentEnd = FindClosingParenthesis(clean, argumentStart);
            if (argumentEnd < 0)
            {
                throw new InvalidDataException(
                    "A SeqTrack row has an unterminated argument list.");
            }

            string arguments = clean[argumentStart..argumentEnd];
            tracks.Add(ParseSeqTrackArguments(
                arguments,
                HasFollowingBlock(clean, argumentEnd + 1)));
        }

        return tracks.ToImmutable();
    }

    /// <summary>
    /// True when the source carries authored event blocks. Callers use this to
    /// warn that the binary type-322 resource cannot carry the rows, because
    /// <see cref="Anm2.AnimationScrCodec"/> always writes an event count of
    /// zero.
    /// </summary>
    public static bool ContainsEventBlocks(string source)
    {
        ArgumentNullException.ThrowIfNull(source);
        return ParseSeqTracks(source).Any(static track => track.HasEventBlock);
    }

    private static void EnsureParseable(string source)
    {
        if (source.Length > MaximumSourceLength)
        {
            throw new InvalidDataException(
                "An animation script source exceeds the supported size.");
        }
    }

    private static AnimationScriptSeqTrack ParseSeqTrackArguments(
        string arguments,
        bool hasEventBlock)
    {
        ImmutableArray<string> fields = SplitArguments(arguments);
        if (fields.Length != SeqTrackArgumentCount)
        {
            throw new InvalidDataException(
                $"A SeqTrack row declares {fields.Length} arguments; " +
                $"{SeqTrackArgumentCount} are required.");
        }

        return new AnimationScriptSeqTrack(
            ReadString(fields[0], "sequence name"),
            ReadString(fields[1], "animation name"),
            ReadValue(fields[2], "start frame"),
            ReadValue(fields[3], "end frame"),
            ReadValue(fields[4], "frames per second"),
            ReadValue(fields[5], "enabled flag"),
            ReadValue(fields[6], "blend"),
            hasEventBlock);
    }

    private static ImmutableArray<string> SplitArguments(string arguments)
    {
        ImmutableArray<string>.Builder fields =
            ImmutableArray.CreateBuilder<string>();
        StringBuilder current = new();
        int depth = 0;
        bool inString = false;
        for (int index = 0; index < arguments.Length; index++)
        {
            char character = arguments[index];
            if (inString)
            {
                if (character == '\\' && index + 1 < arguments.Length)
                {
                    current.Append(character).Append(arguments[index + 1]);
                    index++;
                    continue;
                }

                if (character == '"')
                {
                    inString = false;
                }

                current.Append(character);
                continue;
            }

            switch (character)
            {
                case '"':
                    inString = true;
                    current.Append(character);
                    continue;
                case '(' or '[':
                    depth++;
                    current.Append(character);
                    continue;
                case ')' or ']':
                    depth--;
                    current.Append(character);
                    continue;
                case ',' when depth == 0:
                    fields.Add(current.ToString().Trim());
                    current.Clear();
                    continue;
                default:
                    current.Append(character);
                    continue;
            }
        }

        fields.Add(current.ToString().Trim());
        return fields.ToImmutable();
    }

    private static int FindClosingParenthesis(string source, int start)
    {
        int depth = 1;
        bool inString = false;
        for (int index = start; index < source.Length; index++)
        {
            char character = source[index];
            if (inString)
            {
                if (character == '\\')
                {
                    index++;
                    continue;
                }

                if (character == '"')
                {
                    inString = false;
                }

                continue;
            }

            switch (character)
            {
                case '"':
                    inString = true;
                    continue;
                case '(' or '[':
                    depth++;
                    continue;
                case ')' or ']':
                    depth--;
                    if (depth == 0)
                    {
                        return index;
                    }

                    continue;
                default:
                    continue;
            }
        }

        return -1;
    }

    private static bool HasFollowingBlock(string source, int start)
    {
        for (int index = start; index < source.Length; index++)
        {
            char character = source[index];
            if (character == '{')
            {
                return true;
            }

            if (!char.IsWhiteSpace(character))
            {
                return false;
            }
        }

        return false;
    }

    private static string ReadString(string field, string description)
    {
        if (field.Length < 2 ||
            field[0] != '"' ||
            field[^1] != '"')
        {
            throw new InvalidDataException(
                $"A SeqTrack {description} must be a quoted string.");
        }

        string value = field[1..^1]
            .Replace("\\\"", "\"", StringComparison.Ordinal)
            .Replace("\\\\", "\\", StringComparison.Ordinal);
        if (value.Length == 0)
        {
            throw new InvalidDataException(
                $"A SeqTrack {description} cannot be empty.");
        }

        return value;
    }

    /// <summary>
    /// Reads a numeric field as either a literal or a named constant. A token
    /// that is neither is rejected, so genuine malformation still fails rather
    /// than being waved through as a symbol.
    /// </summary>
    private static AnimationScriptValue ReadValue(
        string field,
        string description)
    {
        if (float.TryParse(
                field,
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out float value) &&
            float.IsFinite(value))
        {
            return new AnimationScriptValue(field, value);
        }

        if (IsIdentifier(field))
        {
            return new AnimationScriptValue(field, null);
        }

        throw new InvalidDataException(
            $"A SeqTrack {description} is neither a finite number nor a named constant.");
    }

    private static bool IsIdentifier(string field)
    {
        if (field.Length == 0 ||
            (!char.IsLetter(field[0]) && field[0] != '_'))
        {
            return false;
        }

        foreach (char character in field)
        {
            if (!char.IsLetterOrDigit(character) && character != '_')
            {
                return false;
            }
        }

        return true;
    }
}
