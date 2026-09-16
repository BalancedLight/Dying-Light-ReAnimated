using System.Collections.Immutable;

namespace ReAnimated.Codecs.Models;

public enum AnimationSourceDependencyKind { Include, AnimationScriptAlias }

public sealed record AnimationSourceDependency(
    AnimationSourceDependencyKind Kind, string ArgumentText, string? LiteralName);

/// <summary>
/// Reads dependency calls without interpreting macros or executing script code.
/// Nonliteral arguments remain visible as unresolved dependencies.
/// </summary>
public static class AnimationScriptDependencyReader
{
    public static ImmutableArray<AnimationSourceDependency> Read(string source)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (source.Length > 8 * 1024 * 1024) throw new InvalidDataException("Script source exceeds the dependency reader's bound.");
        ValidateTrivia(source);
        string text = AnimationScriptSourceParser.StripComments(source);
        var result = ImmutableArray.CreateBuilder<AnimationSourceDependency>();
        for (int i = 0; i < text.Length;)
        {
            if (text[i] == '"') { i = EndString(text, i) + 1; continue; }
            bool directive = text[i] == '!';
            if (directive) { i++; SkipSpace(text, ref i); }
            if (i == text.Length) break;
            if (!char.IsLetter(text[i]) && text[i] != '_') { i++; continue; }
            int begin = i++;
            while (i < text.Length && (char.IsLetterOrDigit(text[i]) || text[i] == '_')) i++;
            string name = text[begin..i];
            AnimationSourceDependencyKind? kind = directive && name.Equals("include", StringComparison.OrdinalIgnoreCase)
                ? AnimationSourceDependencyKind.Include
                : !directive && name.Equals("AnimScriptAlias", StringComparison.OrdinalIgnoreCase)
                    ? AnimationSourceDependencyKind.AnimationScriptAlias : null;
            if (kind is null) continue;
            SkipSpace(text, ref i);
            if (i == text.Length || text[i] != '(') throw new InvalidDataException($"Dependency call '{name}' has no argument list.");
            int start = ++i;
            int depth = 1;
            for (; i < text.Length; i++)
            {
                if (text[i] == '"') { i = EndString(text, i); continue; }
                if (text[i] == '(' && ++depth > 256) throw new InvalidDataException("Dependency expression nesting exceeds its bound.");
                if (text[i] == ')' && --depth == 0) break;
            }
            if (depth != 0) throw new InvalidDataException($"Dependency call '{name}' is unterminated.");
            string arguments = text[start..i].Trim();
            string? literal = arguments.Length >= 2 && arguments[0] == '"' && EndString(arguments, 0) == arguments.Length - 1
                ? arguments[1..^1] : null;
            if (string.IsNullOrWhiteSpace(literal) || literal.Contains("\\\"", StringComparison.Ordinal)) literal = null;
            result.Add(new(kind.Value, arguments, literal));
            i++;
        }
        return result.ToImmutable();
    }

    private static void SkipSpace(string text, ref int index)
    {
        while (index < text.Length && char.IsWhiteSpace(text[index])) index++;
    }

    private static void ValidateTrivia(string source)
    {
        for (int i = 0; i < source.Length; i++)
        {
            if (source[i] == '"') { i = EndString(source, i); continue; }
            if (source[i] != '/' || i + 1 == source.Length) continue;
            if (source[i + 1] == '/')
            {
                while (i < source.Length && source[i] != '\n') i++;
            }
            else if (source[i + 1] == '*')
            {
                int close = source.IndexOf("*/", i + 2, StringComparison.Ordinal);
                if (close < 0) throw new InvalidDataException("Script source contains an unterminated block comment.");
                i = close + 1;
            }
        }
    }

    private static int EndString(string text, int start)
    {
        for (int i = start + 1; i < text.Length; i++)
        {
            if (text[i] == '\\' && i + 1 < text.Length) { i++; continue; }
            if (text[i] == '"') return i;
        }
        throw new InvalidDataException("Script source contains an unterminated string literal.");
    }
}
