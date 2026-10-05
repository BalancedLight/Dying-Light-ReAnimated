using System.Collections.Immutable;
using System.Text;

namespace ReAnimated.Codecs.Models;

/// <summary>A quoted whole argument in a source script, with its original source span.</summary>
public sealed record NativeCharacterQuotedArgument(int ArgumentIndex, int Start, int Length, string Value);
public sealed record NativeCharacterArgumentSpan(int ArgumentIndex, int Start, int Length, string Text);

/// <summary>One syntactic call. ParentCallIndex identifies the call whose braces contain it.</summary>
public sealed record NativeCharacterCall(
    string Name,
    ImmutableArray<string> Arguments,
    int Start,
    int Length,
    int ParentCallIndex,
    ImmutableArray<NativeCharacterQuotedArgument> QuotedArguments)
{
    public ImmutableArray<NativeCharacterArgumentSpan> ArgumentSpans { get; init; } = [];
}

public sealed record NativeCharacterTokenReplacement(
    int CallIndex,
    int ArgumentIndex,
    string ExpectedValue,
    string NewValue);

public enum NativeCharacterNumericKind { Source, WholeNumber, RealNumber }

public sealed record NativeCharacterNumericReplacement(
    int CallIndex,
    int ArgumentIndex,
    string ExpectedLiteral,
    double NewValue)
{
    public NativeCharacterNumericKind Kind { get; init; }
}

public sealed record NativeCharacterDiagnostic(string Code, string Message, bool IsError = false);

public enum NativeCharacterReferenceKind
{
    Bone,
    JointPair,
    Helper,
    MeshEntity,
    PhysicsResource,
    EffectResource,
    MeshResource,
    BodyElementsResource,
    IncludeResource,
    Patch,
}

public sealed record NativeCharacterReference(
    NativeCharacterReferenceKind Kind,
    string Name,
    int CallIndex,
    int ArgumentIndex);

public sealed record NativeCharacterRenameResult(
    NativeCharacterScriptDocument Document,
    ImmutableArray<NativeCharacterDiagnostic> Diagnostics)
{
    public bool IsValid => Diagnostics.All(static diagnostic => !diagnostic.IsError);
}

/// <summary>
/// Bounded, source-preserving syntax for the character ragdoll, body-element and
/// damage-patch script families. Unknown calls and comments are retained verbatim.
/// Parsing a call does not establish that a native host executes it.
/// </summary>
public sealed record NativeCharacterScriptDocument(string Text, ImmutableArray<NativeCharacterCall> Calls)
{
    public string Write() => Text;

    /// <summary>Replace only re-verified, complete quoted arguments; leave all other source bytes intact.</summary>
    public NativeCharacterScriptDocument ReplaceQuotedArguments(
        IEnumerable<NativeCharacterTokenReplacement> replacements)
    {
        ArgumentNullException.ThrowIfNull(replacements);
        var edits = new List<(int Start, int Length, string Replacement)>();
        var used = new HashSet<(int Call, int Argument)>();
        foreach (NativeCharacterTokenReplacement replacement in replacements)
        {
            if ((uint)replacement.CallIndex >= (uint)Calls.Length ||
                !used.Add((replacement.CallIndex, replacement.ArgumentIndex)))
                throw new ArgumentException("A replacement must identify one distinct current call argument.", nameof(replacements));
            NativeCharacterQuotedArgument? token = Calls[replacement.CallIndex].QuotedArguments
                .FirstOrDefault(argument => argument.ArgumentIndex == replacement.ArgumentIndex);
            if (token is null || !string.Equals(token.Value, replacement.ExpectedValue, StringComparison.Ordinal))
                throw new ArgumentException("A replacement does not match the current quoted source token.", nameof(replacements));
            if (string.IsNullOrWhiteSpace(replacement.NewValue) || replacement.NewValue.Length > 4096 ||
                replacement.NewValue.Any(char.IsControl))
                throw new ArgumentException("Replacement names must be bounded, nonempty text without control characters.", nameof(replacements));
            edits.Add((token.Start, token.Length, NativeCharacterScriptCodec.Quote(replacement.NewValue)));
        }
        if (edits.Count == 0) return this;
        var result = new StringBuilder(Text.Length);
        int cursor = 0;
        foreach ((int start, int length, string value) in edits.OrderBy(static edit => edit.Start))
        {
            if (start < cursor) throw new ArgumentException("Replacement source spans overlap.", nameof(replacements));
            result.Append(Text, cursor, start - cursor).Append(value);
            cursor = start + length;
        }
        result.Append(Text, cursor, Text.Length - cursor);
        return NativeCharacterScriptCodec.Parse(result.ToString());
    }

    /// <summary>Replace only a re-verified finite numeric literal, keeping surrounding source exact.</summary>
    public NativeCharacterScriptDocument ReplaceNumericArguments(
        IEnumerable<NativeCharacterNumericReplacement> replacements)
    {
        ArgumentNullException.ThrowIfNull(replacements);
        var edits = new List<(int Start, int Length, string Replacement)>();
        var used = new HashSet<(int Call, int Argument)>();
        foreach (NativeCharacterNumericReplacement replacement in replacements)
        {
            if ((uint)replacement.CallIndex >= (uint)Calls.Length ||
                !used.Add((replacement.CallIndex, replacement.ArgumentIndex)) ||
                !double.IsFinite(replacement.NewValue))
                throw new ArgumentException("A numeric edit requires one distinct current call and finite value.", nameof(replacements));
            NativeCharacterArgumentSpan? argument = Calls[replacement.CallIndex].ArgumentSpans
                .FirstOrDefault(item => item.ArgumentIndex == replacement.ArgumentIndex);
            if (argument is null || !string.Equals(argument.Text, replacement.ExpectedLiteral, StringComparison.Ordinal))
                throw new ArgumentException("Numeric edit does not match the current source token.", nameof(replacements));
            _ = NativeCharacterScriptCodec.FiniteNumber(argument.Text);
            if (!Enum.IsDefined(replacement.Kind))
                throw new ArgumentException("Select a supported numeric literal kind.", nameof(replacements));
            string literal;
            if (replacement.Kind == NativeCharacterNumericKind.WholeNumber)
            {
                if (replacement.NewValue != Math.Truncate(replacement.NewValue) ||
                    replacement.NewValue < int.MinValue || replacement.NewValue > int.MaxValue)
                    throw new ArgumentException("The numeric edit requires a signed integer.", nameof(replacements));
                literal = ((int)replacement.NewValue).ToString(System.Globalization.CultureInfo.InvariantCulture);
            }
            else
            {
                if (replacement.Kind == NativeCharacterNumericKind.RealNumber && !float.IsFinite((float)replacement.NewValue))
                    throw new ArgumentException("The numeric edit exceeds the finite float range.", nameof(replacements));
                literal = replacement.NewValue.ToString("R", System.Globalization.CultureInfo.InvariantCulture);
                bool floating = replacement.Kind == NativeCharacterNumericKind.RealNumber ||
                    argument.Text.IndexOfAny(['.', 'e', 'E']) >= 0;
                if (floating && literal.IndexOfAny(['.', 'e', 'E']) < 0) literal += ".0";
            }
            edits.Add((argument.Start, argument.Length, literal));
        }
        if (edits.Count == 0) return this;
        var result = new StringBuilder(Text.Length);
        int cursor = 0;
        foreach ((int start, int length, string value) in edits.OrderBy(static edit => edit.Start))
        {
            if (start < cursor) throw new ArgumentException("Numeric edit source spans overlap.", nameof(replacements));
            result.Append(Text, cursor, start - cursor).Append(value);
            cursor = start + length;
        }
        result.Append(Text, cursor, Text.Length - cursor);
        return NativeCharacterScriptCodec.Parse(result.ToString());
    }
}

public static class NativeCharacterScriptCodec
{
    public const int MaximumCharacters = 4 * 1024 * 1024;
    public const int MaximumCalls = 65_536;
    public const int MaximumBlockDepth = 64;

    /// <summary>
    /// Rename only references selected by a family reader. Unclassified quoted
    /// occurrences are left in place and reported for review.
    /// </summary>
    public static NativeCharacterRenameResult RenameReferences(
        NativeCharacterScriptDocument source,
        IEnumerable<NativeCharacterReference> references,
        NativeCharacterReferenceKind kind,
        string oldName,
        string newName,
        IEnumerable<string>? availableNewNames = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(references);
        ArgumentException.ThrowIfNullOrWhiteSpace(oldName);
        ArgumentException.ThrowIfNullOrWhiteSpace(newName);
        NativeCharacterReference[] inventory = references.ToArray();
        var diagnostics = ImmutableArray.CreateBuilder<NativeCharacterDiagnostic>();
        if (availableNewNames is not null &&
            !availableNewNames.Contains(newName, StringComparer.Ordinal))
            diagnostics.Add(new("character_rename_target_missing",
                $"Replacement '{newName}' is absent from the supplied {kind} inventory.", true));
        NativeCharacterReference[] selected = inventory
            .Where(reference => reference.Kind == kind &&
                string.Equals(reference.Name, oldName, StringComparison.Ordinal))
            .ToArray();
        if (selected.Length == 0)
            diagnostics.Add(new("character_rename_source_missing",
                $"No verified {kind} reference named '{oldName}' exists.", true));
        if (diagnostics.Any(static diagnostic => diagnostic.IsError))
            return new(source, diagnostics.ToImmutable());
        var selectedPositions = selected.Select(static reference =>
            (reference.CallIndex, reference.ArgumentIndex)).ToHashSet();
        for (int callIndex = 0; callIndex < source.Calls.Length; callIndex++)
        foreach (NativeCharacterQuotedArgument token in source.Calls[callIndex].QuotedArguments)
        {
            if (string.Equals(token.Value, oldName, StringComparison.Ordinal) &&
                !selectedPositions.Contains((callIndex, token.ArgumentIndex)))
                diagnostics.Add(new("character_rename_unclassified_token",
                    $"Call {callIndex} contains an unclassified '{oldName}' token; review it after renaming."));
        }
        NativeCharacterScriptDocument changed = source.ReplaceQuotedArguments(selected.Select(
            reference => new NativeCharacterTokenReplacement(reference.CallIndex,
                reference.ArgumentIndex, oldName, newName)));
        return new(changed, diagnostics.ToImmutable());
    }

    public static NativeCharacterScriptDocument Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (text.Length > MaximumCharacters || text.Contains('\0'))
            throw new FormatException("Character script is too large or contains NUL.");
        var calls = ImmutableArray.CreateBuilder<NativeCharacterCall>();
        var parents = new Stack<int>();
        int cursor = 0;
        int precedingCall = -1;
        while (cursor < text.Length)
        {
            if (SkipTrivia(text, ref cursor)) continue;
            if (text[cursor] == '{')
            {
                if (parents.Count >= MaximumBlockDepth)
                    throw new FormatException("Character script block nesting exceeds its bound.");
                parents.Push(precedingCall);
                precedingCall = -1;
                cursor++;
                continue;
            }
            if (text[cursor] == '}')
            {
                if (!parents.TryPop(out _)) throw new FormatException("Unbalanced character script braces.");
                precedingCall = -1;
                cursor++;
                continue;
            }
            if (text[cursor] == ';') { precedingCall = -1; cursor++; continue; }
            int start = cursor;
            if (text[cursor] is '!' or '$') cursor++;
            int identifierStart = cursor;
            while (cursor < text.Length && (char.IsLetterOrDigit(text[cursor]) || text[cursor] == '_')) cursor++;
            if (cursor == identifierStart) throw new FormatException($"Expected a character-script call at character {start}.");
            string name = text[start..cursor];
            if (name == "sub")
            {
                if (cursor >= text.Length || !char.IsWhiteSpace(text[cursor]))
                    throw new FormatException("A script subroutine declaration requires a name.");
                while (SkipTrivia(text, ref cursor)) { }
                int declarationNameStart = cursor;
                while (cursor < text.Length && (char.IsLetterOrDigit(text[cursor]) || text[cursor] == '_')) cursor++;
                if (cursor == declarationNameStart)
                    throw new FormatException("A script subroutine declaration requires a name.");
                name = text[declarationNameStart..cursor];
            }
            while (SkipTrivia(text, ref cursor)) { }
            if (name == "import" && cursor < text.Length && text[cursor] == '"')
            {
                int tokenStart = cursor;
                SkipString(text, ref cursor);
                string literal = text[tokenStart..cursor];
                calls.Add(new NativeCharacterCall(name, [literal], start, cursor - start,
                    parents.Count == 0 ? -1 : parents.Peek(),
                    [new NativeCharacterQuotedArgument(0, tokenStart, cursor - tokenStart, Quoted(literal))])
                { ArgumentSpans = [new NativeCharacterArgumentSpan(0, tokenStart, cursor - tokenStart, literal)] });
                if (calls.Count > MaximumCalls) throw new FormatException("Too many character-script calls.");
                precedingCall = -1;
                continue;
            }
            if (cursor >= text.Length || text[cursor] != '(')
                throw new FormatException($"Expected arguments for '{name}'.");
            int argumentsStart = ++cursor;
            int callDepth = 1;
            while (cursor < text.Length && callDepth > 0)
            {
                if (SkipComment(text, ref cursor)) continue;
                if (text[cursor] == '"') { SkipString(text, ref cursor); continue; }
                if (text[cursor] == '(') callDepth++;
                if (text[cursor] == ')') callDepth--;
                cursor++;
            }
            if (callDepth != 0) throw new FormatException($"Unterminated character-script call '{name}'.");
            (ImmutableArray<string> arguments, ImmutableArray<NativeCharacterQuotedArgument> quoted,
                ImmutableArray<NativeCharacterArgumentSpan> spans) =
                ReadArguments(text, argumentsStart, cursor - 1);
            calls.Add(new NativeCharacterCall(name, arguments, start, cursor - start,
                parents.Count == 0 ? -1 : parents.Peek(), quoted) { ArgumentSpans = spans });
            if (calls.Count > MaximumCalls) throw new FormatException("Too many character-script calls.");
            precedingCall = calls.Count - 1;
        }
        if (parents.Count != 0) throw new FormatException("Unbalanced character script braces.");
        return new NativeCharacterScriptDocument(text, calls.ToImmutable());
    }

    internal static (NativeCharacterScriptDocument Document, int AddedCallIndex) InsertBodyMeshDisable(
        NativeCharacterScriptDocument document, int bodyElementCallIndex, string entityName)
    {
        ArgumentNullException.ThrowIfNull(document);
        if ((uint)bodyElementCallIndex >= (uint)document.Calls.Length ||
            document.Calls[bodyElementCallIndex].Name != "BodyElement")
            throw new ArgumentException("One current BodyElement call is required.", nameof(bodyElementCallIndex));
        if (string.IsNullOrWhiteSpace(entityName) || entityName.Length > 4096 ||
            entityName.Any(char.IsControl) || entityName.IndexOfAny(['"', '\\', '/', '(', ')', '{', '}', ';']) >= 0)
            throw new ArgumentException("An exact emitted mesh entity name is required.", nameof(entityName));
        string text = document.Text;
        NativeCharacterCall header = document.Calls[bodyElementCallIndex];
        int cursor = header.Start + header.Length;
        while (SkipTrivia(text, ref cursor)) { }
        bool terminated = cursor < text.Length && text[cursor] == ';';
        if (terminated)
        {
            cursor++;
            while (SkipTrivia(text, ref cursor)) { }
        }
        bool braced = cursor < text.Length && text[cursor] == '{';
        if (terminated && braced)
            throw new FormatException("A BodyElement followed by a semicolon and block is ambiguous.");
        if (braced) cursor++;
        int parent = braced ? bodyElementCallIndex : header.ParentCallIndex;
        int lineStart = text.LastIndexOf('\n', Math.Max(0, header.Start - 1));
        lineStart = lineStart < 0 ? 0 : lineStart + 1;
        int indentationEnd = lineStart;
        while (indentationEnd < header.Start && text[indentationEnd] is ' ' or '\t') indentationEnd++;
        string indent = text[lineStart..indentationEnd] + (braced ? "    " : "");
        string newline = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        string inserted = newline + indent + "AddMesh2Disable(" + Quote(entityName) + ")" + newline + indent;
        NativeCharacterScriptDocument changed = Parse(text.Insert(cursor, inserted));
        int added = bodyElementCallIndex + 1;
        if (changed.Calls.Length != document.Calls.Length + 1 ||
            changed.Calls[added].Name != "AddMesh2Disable" ||
            changed.Calls[added].ParentCallIndex != parent ||
            changed.Calls[added].QuotedArguments.Length != 1 ||
            changed.Calls[added].QuotedArguments[0].Value != entityName)
            throw new FormatException("The inserted hide statement has unexpected ownership.");
        for (int index = 0; index < document.Calls.Length; index++)
        {
            NativeCharacterCall oldCall = document.Calls[index];
            NativeCharacterCall newCall = changed.Calls[index < added ? index : index + 1];
            int mappedParent = oldCall.ParentCallIndex < added ? oldCall.ParentCallIndex : oldCall.ParentCallIndex + 1;
            if (oldCall.Name != newCall.Name || oldCall.ParentCallIndex < -1 ||
                newCall.ParentCallIndex != mappedParent || !oldCall.Arguments.SequenceEqual(newCall.Arguments) ||
                oldCall.Length != newCall.Length ||
                !text.AsSpan(oldCall.Start, oldCall.Length).SequenceEqual(
                    changed.Text.AsSpan(newCall.Start, newCall.Length)))
                throw new FormatException("The insertion changed another source call or block relationship.");
        }
        return (changed, added);
    }

    internal static string Quoted(string argument)
    {
        if (argument.Length < 2 || argument[0] != '"') throw new FormatException("Quoted string required.");
        int cursor = 0;
        SkipString(argument, ref cursor);
        if (cursor != argument.Length) throw new FormatException("One complete quoted string required.");
        var value = new StringBuilder(argument.Length - 2);
        for (int index = 1; index < argument.Length - 1; index++)
        {
            if (argument[index] == '\\' && index + 1 < argument.Length - 1 &&
                argument[index + 1] is '\\' or '"')
            {
                index++;
            }
            value.Append(argument[index]);
        }
        return value.ToString();
    }

    internal static string Quote(string value) => "\"" + value.Replace("\\", "\\\\", StringComparison.Ordinal)
        .Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";

    internal static double FiniteNumber(string value)
    {
        if (!double.TryParse(value, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out double number) || !double.IsFinite(number))
            throw new FormatException($"Finite numeric literal required: '{value}'.");
        return number;
    }

    internal static int Integer(string value)
    {
        double number = FiniteNumber(value);
        if (number != Math.Truncate(number) || number < int.MinValue || number > int.MaxValue)
            throw new FormatException($"Integer literal required: '{value}'.");
        return (int)number;
    }

    private static (ImmutableArray<string>, ImmutableArray<NativeCharacterQuotedArgument>,
        ImmutableArray<NativeCharacterArgumentSpan>) ReadArguments(
        string text, int start, int end)
    {
        var arguments = ImmutableArray.CreateBuilder<string>();
        var quoted = ImmutableArray.CreateBuilder<NativeCharacterQuotedArgument>();
        var spans = ImmutableArray.CreateBuilder<NativeCharacterArgumentSpan>();
        if (start == end) return (arguments.ToImmutable(), quoted.ToImmutable(), spans.ToImmutable());
        int cursor = start, fieldStart = start, depth = 0;
        while (cursor <= end)
        {
            if (cursor == end || text[cursor] == ',' && depth == 0)
            {
                int first = fieldStart;
                while (first < cursor && char.IsWhiteSpace(text[first])) first++;
                int last = cursor;
                while (last > first && char.IsWhiteSpace(text[last - 1])) last--;
                if (first == last) throw new FormatException("Empty character-script argument.");
                string argument = text[first..last];
                if (argument[0] == '"')
                {
                    string value = Quoted(argument);
                    quoted.Add(new NativeCharacterQuotedArgument(arguments.Count, first, last - first, value));
                }
                spans.Add(new NativeCharacterArgumentSpan(arguments.Count, first, last - first, argument));
                arguments.Add(argument);
                fieldStart = cursor + 1;
                cursor++;
                continue;
            }
            if (SkipComment(text, ref cursor)) continue;
            if (text[cursor] == '"') { SkipString(text, ref cursor); continue; }
            if (text[cursor] is '(' or '[') depth++;
            if (text[cursor] is ')' or ']')
            {
                if (--depth < 0) throw new FormatException("Unbalanced character-script argument.");
            }
            cursor++;
        }
        if (depth != 0) throw new FormatException("Unbalanced character-script argument.");
        return (arguments.ToImmutable(), quoted.ToImmutable(), spans.ToImmutable());
    }

    private static bool SkipTrivia(string text, ref int cursor)
    {
        if (cursor < text.Length && char.IsWhiteSpace(text[cursor])) { cursor++; return true; }
        return SkipComment(text, ref cursor);
    }

    private static bool SkipComment(string text, ref int cursor)
    {
        if (cursor + 1 >= text.Length || text[cursor] != '/') return false;
        if (text[cursor + 1] == '/')
        {
            cursor += 2;
            while (cursor < text.Length && text[cursor] != '\n') cursor++;
            return true;
        }
        if (text[cursor + 1] != '*') return false;
        int end = text.IndexOf("*/", cursor + 2, StringComparison.Ordinal);
        if (end < 0) throw new FormatException("Unterminated character-script comment.");
        cursor = end + 2;
        return true;
    }

    private static void SkipString(string text, ref int cursor)
    {
        cursor++;
        while (cursor < text.Length)
        {
            if (text[cursor] == '\\') { cursor += 2; continue; }
            if (text[cursor++] == '"') return;
        }
        throw new FormatException("Unterminated character-script string.");
    }
}
