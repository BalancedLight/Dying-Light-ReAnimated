using System.Collections.Immutable;
using System.Globalization;

namespace ReAnimated.Codecs.Models;

public sealed record Dl1BodyElementSymbolDeclaration(int CallIndex, string Symbol, int Id);
public sealed record Dl1BodyElementSymbolMapResult(
    NativeCharacterScriptDocument Syntax,
    ImmutableDictionary<string, int> Symbols,
    ImmutableArray<Dl1BodyElementSymbolDeclaration> Declarations,
    ImmutableArray<NativeCharacterDiagnostic> Diagnostics)
{
    public bool IsValid => Diagnostics.All(static row => !row.IsError);
    public bool IsComplete => IsValid && Diagnostics.IsEmpty;
}

public static class Dl1BodyElementSymbolMap
{
    public const int MaximumBodyElementId = 26;
    public const int MaximumRequiredSymbols = 256;

    public static Dl1BodyElementSymbolMapResult Read(string text, IEnumerable<string> requiredBodySymbols)
    {
        ArgumentNullException.ThrowIfNull(requiredBodySymbols);
        NativeCharacterScriptDocument syntax = NativeCharacterScriptCodec.Parse(text);
        var required = new HashSet<string>(StringComparer.Ordinal);
        foreach (string symbol in requiredBodySymbols)
        {
            if (string.IsNullOrWhiteSpace(symbol) || symbol.Length > 256 || symbol[0] == '$' ||
                symbol.Any(character => !char.IsLetterOrDigit(character) && character != '_'))
                throw new ArgumentException("Required body symbols must be exact unprefixed identifiers.", nameof(requiredBodySymbols));
            required.Add(symbol);
            if (required.Count > MaximumRequiredSymbols)
                throw new ArgumentException("Too many required body symbols.", nameof(requiredBodySymbols));
        }
        var declarations = ImmutableArray.CreateBuilder<Dl1BodyElementSymbolDeclaration>();
        var diagnostics = ImmutableArray.CreateBuilder<NativeCharacterDiagnostic>();
        var attempted = new HashSet<string>(StringComparer.Ordinal);
        var symbols = ImmutableDictionary.CreateBuilder<string, int>(StringComparer.Ordinal);
        var invalid = new HashSet<string>(StringComparer.Ordinal);
        for (int index = 0; index < syntax.Calls.Length; index++)
        {
            NativeCharacterCall call = syntax.Calls[index];
            if (!call.Name.StartsWith('$') || !required.Contains(call.Name[1..])) continue;
            string symbol = call.Name[1..];
            if (!attempted.Add(symbol))
            {
                invalid.Add(symbol);
                diagnostics.Add(new("body_symbol_duplicate", $"Body symbol '{symbol}' has multiple declarations.", true));
            }
            if (call.ParentCallIndex != -1 || call.Arguments.Length != 2 || call.Arguments[0] != "i" ||
                !int.TryParse(call.Arguments[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int id) ||
                id is < 0 or > MaximumBodyElementId)
            {
                invalid.Add(symbol);
                diagnostics.Add(new("body_symbol_invalid", $"Body symbol '{symbol}' requires one root integer declaration from 0 to 26.", true));
                continue;
            }
            declarations.Add(new(index, symbol, id));
            symbols[symbol] = id;
        }
        foreach (var collision in symbols.GroupBy(pair => pair.Value).Where(group => group.Count() > 1))
        {
            foreach (var pair in collision) invalid.Add(pair.Key);
            diagnostics.Add(new("body_symbol_id_collision", $"Multiple required body symbols map to ID {collision.Key}.", true));
        }
        foreach (string symbol in invalid) symbols.Remove(symbol);
        foreach (string symbol in required.Order(StringComparer.Ordinal))
            if (!symbols.ContainsKey(symbol)) diagnostics.Add(new("body_symbol_missing", $"Exact mapping for body symbol '{symbol}' is unavailable."));
        return new(syntax, symbols.ToImmutable(), declarations.ToImmutable(), diagnostics.ToImmutable());
    }
}
