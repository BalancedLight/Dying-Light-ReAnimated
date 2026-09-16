using System.Collections.Immutable;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace ReAnimated.Codecs.Models;

public sealed record Dl1ScaleTrial(string PresetName, double BodyScale);
public sealed record Dl1PresetField(string Name, string ArgumentText, string? LiteralValue);
public sealed record Dl1ScaleTrialReceipt(string PresetName, double BodyScale, string PresetTextSha256);
public sealed record Dl1ScaleExperimentSource(
    string Source, string SourceTextSha256, string ResultTextSha256,
    string DefinitionName, string ControlPresetName,
    ImmutableArray<Dl1PresetField> ControlFields,
    ImmutableArray<Dl1ScaleTrialReceipt> Trials);

/// <summary>
/// Creates isolated HumanAI spawn-size trials from a supplied preset source.
/// Only trial names and the two forced body-scale fields change. No native
/// supported range, post-spawn behavior or deployment is implied by this writer.
/// </summary>
public static class Dl1HumanAiScaleExperiments
{
    public const string MinimumField = "m_ForcedBodyScaleMin";
    public const string MaximumField = "m_ForcedBodyScaleMax";
    private const int MaximumSourceLength = 8 * 1024 * 1024;
    private const int MaximumTokens = 1_000_000;
    private sealed record Token(string Text, string? Literal, int Start, int End);
    private sealed record Block(string Name, int NameToken, int OpenToken, int CloseToken);
    private sealed record Field(Dl1PresetField Value, int ValueStart, int ValueEnd);

    public static ImmutableArray<string> ListPresets(string source, string definitionName)
    {
        var tokens = Tokenize(source); var pairs = MatchPairs(tokens);
        var definition = SelectDefinition(tokens, pairs, definitionName);
        return Blocks(tokens, pairs, "Preset", definition.OpenToken + 1, definition.CloseToken)
            .Select(static block => block.Name).ToImmutableArray();
    }

    public static Dl1ScaleExperimentSource Build(string source, string definitionName,
        string controlPresetName, IReadOnlyList<Dl1ScaleTrial> trials)
    {
        ArgumentNullException.ThrowIfNull(trials);
        if (trials.Count is < 1 or > 64) throw new ArgumentException("Provide between one and 64 scale trials.", nameof(trials));
        var tokens = Tokenize(source); var pairs = MatchPairs(tokens);
        var definition = SelectDefinition(tokens, pairs, definitionName);
        var presets = Blocks(tokens, pairs, "Preset", definition.OpenToken + 1, definition.CloseToken);
        var controls = presets.Where(p => p.Name == controlPresetName).ToArray();
        if (controls.Length != 1) throw new InvalidDataException("The selected preset must identify exactly one control in the selected definition.");
        var control = controls[0]; var fields = Fields(source, tokens, pairs, control);
        var names = presets.Select(static p => p.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (fields.GroupBy(static field => field.Value.Name, StringComparer.Ordinal).Any(g =>
                IsScaleField(g.Key) && g.Count() != 1))
            throw new InvalidDataException("Repeated body-scale fields make the control ambiguous.");
        foreach (string required in new[] { MinimumField, MaximumField })
            if (!Declared(tokens, pairs, definition, required))
                throw new InvalidDataException($"The selected definition does not directly declare '{required}'.");

        string newline = source.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var generated = new StringBuilder(); var receipts = ImmutableArray.CreateBuilder<Dl1ScaleTrialReceipt>(trials.Count);
        int controlStart = tokens[control.NameToken - 2].Start;
        int controlEnd = tokens[control.CloseToken].End;
        foreach (var trial in trials)
        {
            ArgumentNullException.ThrowIfNull(trial);
            ValidateName(trial.PresetName);
            if (!double.IsFinite(trial.BodyScale) || trial.BodyScale <= 0 ||
                !float.IsFinite((float)trial.BodyScale) || (float)trial.BodyScale <= 0)
                throw new ArgumentException("Body scale must be positive, finite and representable as a native float.", nameof(trials));
            if (!names.Add(trial.PresetName)) throw new InvalidDataException($"Preset name '{trial.PresetName}' already exists.");
            string value = ((float)trial.BodyScale).ToString("R", CultureInfo.InvariantCulture);
            var edits = new List<(int Start, int End, string Value)>
            {
                (tokens[control.NameToken].Start, tokens[control.NameToken].End, Quote(trial.PresetName)),
            };
            var absent = new StringBuilder();
            foreach (string fieldName in new[] { MinimumField, MaximumField })
            {
                var field = fields.SingleOrDefault(f => f.Value.Name == fieldName);
                if (field is not null) edits.Add((field.ValueStart, field.ValueEnd, Quote(value)));
                else absent.Append(newline).Append("                SetField(").Append(Quote(fieldName)).Append(", ").Append(Quote(value)).Append(");");
            }
            if (absent.Length > 0) edits.Add((tokens[control.CloseToken].Start, tokens[control.CloseToken].Start, absent + newline + "        "));
            var text = new StringBuilder(source[controlStart..controlEnd]);
            foreach (var edit in edits.OrderByDescending(static e => e.Start))
            {
                text.Remove(edit.Start - controlStart, edit.End - edit.Start);
                text.Insert(edit.Start - controlStart, edit.Value);
            }
            string candidate = text.ToString();
            if ((long)source.Length + generated.Length + candidate.Length + 8 + 2 * newline.Length > MaximumSourceLength)
                throw new InvalidDataException("Generated preset source exceeds the study limit.");
            generated.Append(newline).Append("        ").Append(candidate).Append(newline);
            receipts.Add(new(trial.PresetName, (float)trial.BodyScale, Hash(candidate)));
        }
        string output = source.Insert(tokens[definition.CloseToken].Start, generated.ToString());
        // Parse the generated text again; never return syntactically unbalanced output.
        var parsed = Tokenize(output); _ = MatchPairs(parsed);
        return new(output, Hash(source), Hash(output), definitionName, controlPresetName,
            fields.Select(static field => field.Value).ToImmutableArray(), receipts.MoveToImmutable());
    }

    private static bool IsScaleField(string name) => name is MinimumField or MaximumField;
    private static string Hash(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    private static string Quote(string value) => "\"" + value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";
    private static void ValidateName(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (name.Length > 256 || name.Any(char.IsControl)) throw new ArgumentException("Preset names must have at most 256 non-control characters.", nameof(name));
    }
    private static Block SelectDefinition(List<Token> tokens, Dictionary<int, int> pairs, string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var definitions = Blocks(tokens, pairs, "PresetDef", 0, tokens.Count).Where(b => b.Name == name).ToArray();
        if (definitions.Length != 1) throw new InvalidDataException("The preset definition is absent or ambiguous.");
        return definitions[0];
    }
    private static List<Block> Blocks(List<Token> tokens, Dictionary<int, int> pairs, string call, int start, int end)
    {
        var result = new List<Block>();
        for (int i = start; i + 4 < end; i++)
            if (tokens[i].Text == call && tokens[i + 1].Text == "(")
            {
                if (tokens[i + 2].Literal is not { } name || tokens[i + 3].Text != ")" || tokens[i + 4].Text != "{")
                    throw new InvalidDataException($"{call} requires a literal name and a block for an isolated study.");
                int close = pairs[i + 4];
                if (close >= end) throw new InvalidDataException("Preset block escapes its definition.");
                result.Add(new(name, i + 2, i + 4, close)); i = close;
            }
        return result;
    }
    private static List<Field> Fields(string source, List<Token> tokens, Dictionary<int, int> pairs, Block preset)
    {
        var result = new List<Field>(); int depth = 0;
        for (int i = preset.OpenToken + 1; i < preset.CloseToken; i++)
        {
            if (tokens[i].Text == "{") depth++;
            if (tokens[i].Text == "}") depth--;
            if (tokens[i].Text != "SetField" || i + 5 >= preset.CloseToken || tokens[i + 1].Text != "(") continue;
            int close = pairs[i + 1];
            if (tokens[i + 2].Literal is not { } name || tokens[i + 3].Text != "," || close <= i + 4)
                throw new InvalidDataException("SetField requires a literal name and a value.");
            if (depth != 0 && IsScaleField(name)) throw new InvalidDataException("A nested scale assignment cannot be isolated safely.");
            if (depth != 0) continue;
            int valueStart = tokens[i + 4].Start, valueEnd = tokens[close - 1].End;
            for (int k = i + 4; k < close; k++)
            {
                if (tokens[k].Text == ",") throw new InvalidDataException("SetField has extra top-level arguments.");
                if (tokens[k].Text == "(") k = pairs[k];
            }
            result.Add(new(new(name, source[valueStart..valueEnd], close == i + 5 ? tokens[i + 4].Literal : null), valueStart, valueEnd));
            i = close;
        }
        return result;
    }
    private static bool Declared(List<Token> tokens, Dictionary<int, int> pairs, Block definition, string name)
    {
        for (int i = definition.OpenToken + 1; i + 3 < definition.CloseToken; i++)
        {
            if (tokens[i].Text == "{") { i = pairs[i]; continue; }
            if (tokens[i].Text == "AddField" && tokens[i + 1].Text == "(" && tokens[i + 2].Literal == name && tokens[i + 3].Text == ",") return true;
        }
        return false;
    }
    private static Dictionary<int, int> MatchPairs(List<Token> tokens)
    {
        var stack = new Stack<int>(); var pairs = new Dictionary<int, int>();
        for (int i = 0; i < tokens.Count; i++)
        {
            string token = tokens[i].Text;
            if (tokens[i].Literal is not null) continue;
            if (token is "{" or "(" or "[")
            {
                if (stack.Count >= 128) throw new InvalidDataException("Preset nesting exceeds its limit.");
                stack.Push(i);
            }
            else if (token is "}" or ")" or "]")
            {
                if (!stack.TryPop(out int open) || tokens[open].Text != (token == "}" ? "{" : token == ")" ? "(" : "["))
                    throw new InvalidDataException("Preset source has unbalanced delimiters.");
                pairs.Add(open, i);
            }
        }
        if (stack.Count != 0) throw new InvalidDataException("Preset source has unterminated delimiters.");
        return pairs;
    }
    private static List<Token> Tokenize(string source)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (source.Length > MaximumSourceLength) throw new InvalidDataException("Preset source exceeds its limit.");
        var tokens = new List<Token>();
        for (int i = 0; i < source.Length;)
        {
            if (char.IsWhiteSpace(source[i])) { i++; continue; }
            if (i + 1 < source.Length && source[i] == '/' && source[i + 1] == '/')
            { while (i < source.Length && source[i] != '\n') i++; continue; }
            if (i + 1 < source.Length && source[i] == '/' && source[i + 1] == '*')
            {
                int end = source.IndexOf("*/", i + 2, StringComparison.Ordinal);
                if (end < 0) throw new InvalidDataException("Unterminated preset comment.");
                i = end + 2; continue;
            }
            int start = i; string? literal = null;
            if (source[i] == '"')
            {
                var decoded = new StringBuilder(); i++;
                while (i < source.Length && source[i] != '"')
                {
                    if (source[i] == '\\' && i + 1 < source.Length && source[i + 1] is '\\' or '"') i++;
                    decoded.Append(source[i++]);
                }
                if (i == source.Length) throw new InvalidDataException("Unterminated preset string.");
                i++; literal = decoded.ToString();
            }
            else if (char.IsLetterOrDigit(source[i]) || source[i] == '_')
            { while (i < source.Length && (char.IsLetterOrDigit(source[i]) || source[i] == '_')) i++; }
            else i++;
            if (tokens.Count >= MaximumTokens) throw new InvalidDataException("Preset token count exceeds its limit.");
            tokens.Add(new(source[start..i], literal, start, i));
        }
        return tokens;
    }
}
