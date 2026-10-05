using System.Collections.Immutable;
using System.Text;

namespace ReAnimated.Codecs.Models;

/// <summary>Bits observed in the exact Editor source-skin parser's in-memory record.</summary>
[Flags]
public enum Dl1SkinSourceFeatures : ushort
{
    None = 0,
    ShowAll = 0x0001,
    HideAll = 0x0002,
    FilterInEditor = 0x0004,
    Randomized = 0x0008,
    EnableNewSkins = 0x0040,
    MaterialReplacements = 0x0080,
    Color0Rgb = 0x0100,
    Color1Rgb = 0x0200,
    Color0Alpha = 0x0400,
    Color1Alpha = 0x0800,
}

public sealed record Dl1SkinMaterialReplacement(string OriginalMaterial, string ReplacementMaterial, int CallIndex = -1);

/// <summary>OriginalRawValue is optional evidence from a compiled entity row, not a source-skin field.</summary>
public sealed record Dl1SkinEntityVisibility(string EntityName, bool Hidden, ushort? OriginalRawValue = null, int CallIndex = -1);

public sealed record Dl1SkinSurfaceReplacement(string OriginalSurface, string ReplacementSurface, string FlagsToken, int CallIndex);

/// <summary>The source tokens plus the exact four-byte row expected after Editor packing.</summary>
public sealed record Dl1SkinSurfaceMapping(string OriginalSurface, string ReplacementSurface,
    string FlagsToken, byte ExpectedOriginalId, byte ExpectedReplacementId, ushort ExpectedFlags);

public sealed record Dl1SkinColorCommand(string Name, int Slot, ImmutableArray<int> Channels, int CallIndex);

public enum Dl1SkinReferenceKind { UseSkin, IncludeSkin, SourceInclude }

public sealed record Dl1SkinReference(Dl1SkinReferenceKind Kind, string Name, int? SelectionIndex, int SkinCallIndex, int CallIndex);

public sealed record Dl1SkinBlock(
    string Name,
    int CallIndex,
    ImmutableArray<Dl1SkinMaterialReplacement> MaterialReplacements,
    ImmutableArray<Dl1SkinEntityVisibility> EntityVisibility,
    ImmutableArray<Dl1SkinSurfaceReplacement> SurfaceReplacements,
    ImmutableArray<Dl1SkinColorCommand> ColorCommands,
    Dl1SkinSourceFeatures Features,
    ImmutableArray<string> UnclassifiedCalls);

public sealed record Dl1SkinDefinitionDocument(
    NativeCharacterScriptDocument Syntax,
    ImmutableArray<Dl1SkinBlock> Skins,
    ImmutableArray<Dl1SkinReference> References,
    ImmutableArray<NativeCharacterDiagnostic> Diagnostics)
{
    public bool IsValid => Diagnostics.All(static diagnostic => !diagnostic.IsError);
    public bool RequiresReview => Skins.Any(static skin => !skin.UnclassifiedCalls.IsEmpty);
    public string Write() => Syntax.Write();
}

/// <summary>
/// Inputs for complete source generation after the caller maps original material
/// and entity identities to exact names. Opaque compiled arrays remain counts so
/// generation can fail closed rather than silently discard them.
/// </summary>
public sealed record Dl1SkinGenerationDefinition(
    string Name,
    ushort RawFeatures,
    ImmutableArray<Dl1SkinMaterialReplacement> MaterialReplacements,
    ImmutableArray<Dl1SkinEntityVisibility> EntityVisibility,
    int SurfaceOverrideCount,
    int RandomizedChildCount)
{
    /// <summary>
    /// Explicitly reviewed source-parser commands for this compiled raw word.
    /// Null means the source-to-compiled feature mapping is unverified.
    /// </summary>
    public Dl1SkinSourceFeatures? VerifiedSourceFeatures { get; init; }

    /// <summary>EnableNewSkins is a top-level switch affecting this and every following Skin.</summary>
    public bool VerifiedEnableNewSkins { get; init; }

    /// <summary>Exact compact row+12 eight-byte BGRA color block, supplied by decoded read-back.</summary>
    public ImmutableArray<byte> ExpectedCompiledColors { get; init; } = [];

    /// <summary>Exact compact row+4 tag value; only all-zero tags are source-generatable here.</summary>
    public ImmutableArray<byte> ExpectedTagBytes { get; init; } = [];

    /// <summary>Reviewer has verified absent MorphsPreset, Character strings and UseSkin groups.</summary>
    public bool OptionalStringsAndGroupsVerifiedAbsent { get; init; }

    public ImmutableArray<Dl1SkinSurfaceMapping> VerifiedSurfaceReplacements { get; init; } = [];
}

/// <summary>
/// Source-preserving reader for the native .skn DSL and a narrow canonical writer.
/// Source-parser feature bits are known; they are distinct from stock compact-
/// mesh raw bits until an exact compiler read-back establishes the mapping.
/// </summary>
public static class Dl1SkinDefinitionCodec
{
    public const ushort SupportedSourceFeatureMask =
        (ushort)(Dl1SkinSourceFeatures.ShowAll | Dl1SkinSourceFeatures.HideAll |
                 Dl1SkinSourceFeatures.FilterInEditor | Dl1SkinSourceFeatures.Randomized |
                 Dl1SkinSourceFeatures.EnableNewSkins | Dl1SkinSourceFeatures.MaterialReplacements |
                 Dl1SkinSourceFeatures.Color0Rgb | Dl1SkinSourceFeatures.Color1Rgb |
                 Dl1SkinSourceFeatures.Color0Alpha | Dl1SkinSourceFeatures.Color1Alpha);

    public static Dl1SkinDefinitionDocument Read(string text)
    {
        NativeCharacterScriptDocument syntax = NativeCharacterScriptCodec.Parse(text);
        var diagnostics = ImmutableArray.CreateBuilder<NativeCharacterDiagnostic>();
        var references = ImmutableArray.CreateBuilder<Dl1SkinReference>();
        var builders = new Dictionary<int, SkinBuilder>();
        for (int index = 0; index < syntax.Calls.Length; index++)
        {
            NativeCharacterCall call = syntax.Calls[index];
            if (!call.Name.Equals("Skin", StringComparison.OrdinalIgnoreCase)) continue;
            try
            {
                RequireCount(call, 1);
                string name = Quoted(call, 0);
                if (name.Length == 0) throw new FormatException("Skin name is empty.");
                builders.Add(index, new SkinBuilder(name, index));
            }
            catch (FormatException exception)
            {
                diagnostics.Add(new("skin_statement_invalid", $"Skin call {index}: {exception.Message}", true));
            }
        }
        if (builders.Values.Select(static value => value.Name)
            .Distinct(StringComparer.OrdinalIgnoreCase).Count() != builders.Count)
            diagnostics.Add(new("skin_name_duplicate", "Skin names are ambiguous.", true));

        for (int index = 0; index < syntax.Calls.Length; index++)
        {
            NativeCharacterCall call = syntax.Calls[index];
            if (call.Name.Equals("Skin", StringComparison.OrdinalIgnoreCase)) continue;
            int parentSkin = FindParentSkin(syntax, index);
            builders.TryGetValue(parentSkin, out SkinBuilder? block);
            try
            {
                switch (call.Name.ToLowerInvariant())
                {
                    case "replace" when block is not null:
                        RequireCount(call, 2);
                        block.Materials.Add(new(Quoted(call, 0), Quoted(call, 1), index));
                        block.Features |= Dl1SkinSourceFeatures.MaterialReplacements;
                        break;
                    case "hide" when block is not null:
                    case "show" when block is not null:
                        RequireCount(call, 1);
                        block.Entities.Add(new(Quoted(call, 0), call.Name.Equals("Hide", StringComparison.OrdinalIgnoreCase), null, index));
                        break;
                    case "replacesurface" when block is not null:
                        RequireCount(call, 3);
                        block.Surfaces.Add(new(Quoted(call, 0), Quoted(call, 1), Quoted(call, 2, allowEmpty: true), index));
                        break;
                    case "coloralpha" when block is not null:
                    case "colori" when block is not null:
                    case "coloria" when block is not null:
                        int count = call.Name.Equals("ColorAlpha", StringComparison.OrdinalIgnoreCase) ? 2 :
                            call.Name.Equals("ColorI", StringComparison.OrdinalIgnoreCase) ? 4 : 5;
                        RequireCount(call, count);
                        int slot = NativeCharacterScriptCodec.Integer(call.Arguments[0]);
                        if (slot is < 0 or > 1) throw new FormatException("Only color slots 0 and 1 are supported.");
                        ImmutableArray<int> channels = call.Arguments.Skip(1)
                            .Select(NativeCharacterScriptCodec.Integer).ToImmutableArray();
                        if (channels.Any(static value => value is < 0 or > 255))
                            throw new FormatException("Canonical color channels must be bytes.");
                        block.Colors.Add(new(call.Name, slot, channels, index));
                        Dl1SkinSourceFeatures rgb = slot == 0 ? Dl1SkinSourceFeatures.Color0Rgb : Dl1SkinSourceFeatures.Color1Rgb;
                        Dl1SkinSourceFeatures alpha = slot == 0 ? Dl1SkinSourceFeatures.Color0Alpha : Dl1SkinSourceFeatures.Color1Alpha;
                        block.Features |= count switch { 2 => alpha, 4 => rgb, _ => rgb | alpha };
                        break;
                    case "showall" when block is not null:
                    case "hideall" when block is not null:
                    case "filterineditor" when block is not null:
                    case "randomized" when block is not null:
                        RequireCount(call, 0);
                        block.Features |= call.Name.ToLowerInvariant() switch
                        {
                            "showall" => Dl1SkinSourceFeatures.ShowAll,
                            "hideall" => Dl1SkinSourceFeatures.HideAll,
                            "filterineditor" => Dl1SkinSourceFeatures.FilterInEditor,
                            _ => Dl1SkinSourceFeatures.Randomized,
                        };
                        break;
                    case "useskin":
                        RequireCount(call, 2);
                        references.Add(new(Dl1SkinReferenceKind.UseSkin, Quoted(call, 0),
                            NativeCharacterScriptCodec.Integer(call.Arguments[1]), parentSkin, index));
                        break;
                    case "includeskin":
                        RequireCount(call, 1);
                        references.Add(new(Dl1SkinReferenceKind.IncludeSkin, Quoted(call, 0), null, parentSkin, index));
                        break;
                    case "!include":
                        RequireCount(call, 1);
                        references.Add(new(Dl1SkinReferenceKind.SourceInclude, Quoted(call, 0), null, parentSkin, index));
                        break;
                    default:
                        if (block is not null) block.Unknown.Add(call.Name);
                        break;
                }
            }
            catch (Exception exception) when (exception is FormatException or ArgumentException or OverflowException)
            {
                diagnostics.Add(new("skin_statement_invalid", $"'{call.Name}' at call {index}: {exception.Message}", true));
            }
        }
        foreach (Dl1SkinReference reference in references)
            if (reference.Kind is Dl1SkinReferenceKind.UseSkin or Dl1SkinReferenceKind.IncludeSkin &&
                !builders.Values.Any(block => block.Name.Equals(reference.Name, StringComparison.OrdinalIgnoreCase)))
                diagnostics.Add(new("skin_reference_unresolved", $"Skin '{reference.Name}' is not declared in this source; an external or later definition may supply it."));
        return new(syntax, builders.Values.OrderBy(static block => block.CallIndex)
            .Select(static block => block.Build()).ToImmutableArray(),
            references.ToImmutable(), diagnostics.ToImmutable());
    }

    /// <summary>
    /// Emit only explicitly reviewed source-parser features and decoded row
    /// fields. The exact Editor finalizer copies its source feature word into
    /// the compiled row, but this does not recover the original commands.
    /// </summary>
    public static Dl1SkinDefinitionDocument GenerateCanonical(
        IEnumerable<Dl1SkinGenerationDefinition> definitions,
        string? sourceInclude = null)
    {
        ArgumentNullException.ThrowIfNull(definitions);
        Dl1SkinGenerationDefinition[] skins = definitions.ToArray();
        if (skins.Length is 0 or > 1024 || skins.Select(static skin => skin.Name)
            .Distinct(StringComparer.OrdinalIgnoreCase).Count() != skins.Length)
            throw new InvalidDataException("Canonical skin generation requires distinct named skins.");
        var text = new StringBuilder();
        if (sourceInclude is not null)
            text.Append("!include(").Append(QuoteName(sourceInclude)).AppendLine(");");
        bool enableNewSkins = skins[0].VerifiedEnableNewSkins;
        if (skins.Any(skin => skin.VerifiedEnableNewSkins != enableNewSkins))
            throw new InvalidDataException("A mixed EnableNewSkins order needs an explicit source timeline.");
        if (enableNewSkins) text.AppendLine("EnableNewSkins();");
        foreach (Dl1SkinGenerationDefinition skin in skins)
        {
            if (skin.RawFeatures != 0 && skin.VerifiedSourceFeatures is null)
                throw new InvalidDataException($"Skin '{skin.Name}' has no reviewed compiled-to-source feature mapping.");
            Dl1SkinSourceFeatures sourceFeatures = skin.VerifiedSourceFeatures ?? Dl1SkinSourceFeatures.None;
            ushort unknown = (ushort)((ushort)sourceFeatures & ~SupportedSourceFeatureMask);
            if (unknown != 0 || (ushort)sourceFeatures != skin.RawFeatures ||
                skin.RandomizedChildCount != 0)
                throw new InvalidDataException($"Skin '{skin.Name}' contains unverified features or opaque surface/randomized records.");
            if (((sourceFeatures & Dl1SkinSourceFeatures.EnableNewSkins) != 0) != enableNewSkins ||
                !enableNewSkins && (sourceFeatures & Dl1SkinSourceFeatures.ShowAll) == 0)
                throw new InvalidDataException($"Skin '{skin.Name}' has an unverified EnableNewSkins/default ShowAll transition.");
            if (skin.ExpectedTagBytes.Length != 8 || skin.ExpectedTagBytes.Any(static value => value != 0) ||
                !skin.OptionalStringsAndGroupsVerifiedAbsent || skin.ExpectedCompiledColors.Length != 8)
                throw new InvalidDataException($"Skin '{skin.Name}' has unverified tag, color, optional-string or group fields.");
            if (skin.VerifiedSurfaceReplacements.IsDefault ||
                skin.VerifiedSurfaceReplacements.Length != skin.SurfaceOverrideCount)
                throw new InvalidDataException($"Skin '{skin.Name}' has unverified surface rows.");
            bool materialBit = (sourceFeatures & Dl1SkinSourceFeatures.MaterialReplacements) != 0;
            if (materialBit != !skin.MaterialReplacements.IsDefaultOrEmpty)
                throw new InvalidDataException($"Skin '{skin.Name}' material feature bit differs from its named replacement rows.");
            if ((sourceFeatures & (Dl1SkinSourceFeatures.ShowAll | Dl1SkinSourceFeatures.HideAll)) ==
                (Dl1SkinSourceFeatures.ShowAll | Dl1SkinSourceFeatures.HideAll))
                throw new InvalidDataException($"Skin '{skin.Name}' requests both ShowAll and HideAll.");
            string name = QuoteName(skin.Name);
            text.Append("Skin(").Append(name).AppendLine(")");
            text.AppendLine("{");
            AppendFlag(Dl1SkinSourceFeatures.ShowAll, "ShowAll");
            AppendFlag(Dl1SkinSourceFeatures.HideAll, "HideAll");
            AppendFlag(Dl1SkinSourceFeatures.FilterInEditor, "FilterInEditor");
            AppendFlag(Dl1SkinSourceFeatures.Randomized, "Randomized");
            for (int slot = 0; slot < 2; slot++) AppendColor(slot);
            var materials = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (Dl1SkinMaterialReplacement replacement in skin.MaterialReplacements.IsDefault
                         ? [] : skin.MaterialReplacements)
            {
                if (!materials.Add(replacement.OriginalMaterial))
                    throw new InvalidDataException($"Skin '{skin.Name}' has duplicate original material names.");
                text.Append("    Replace(").Append(QuoteName(replacement.OriginalMaterial)).Append(", ")
                    .Append(QuoteName(replacement.ReplacementMaterial)).AppendLine(");");
            }
            var entities = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (Dl1SkinEntityVisibility visibility in skin.EntityVisibility.IsDefault
                         ? [] : skin.EntityVisibility)
            {
                if (!entities.Add(visibility.EntityName))
                    throw new InvalidDataException($"Skin '{skin.Name}' has duplicate entity visibility rows.");
                if (visibility.OriginalRawValue is { } raw &&
                    (raw & 0xC000) != (visibility.Hidden ? 0xC000 : 0))
                    throw new InvalidDataException($"Skin '{skin.Name}' has raw entity flags that Hide/Show cannot reproduce.");
                text.Append("    ").Append(visibility.Hidden ? "Hide(" : "Show(")
                    .Append(QuoteName(visibility.EntityName)).AppendLine(");");
            }
            foreach (Dl1SkinSurfaceMapping surface in skin.VerifiedSurfaceReplacements)
            {
                byte oldId = SurfaceId(surface.OriginalSurface);
                byte newId = surface.ReplacementSurface.Length == 0 ? oldId : SurfaceId(surface.ReplacementSurface);
                if (surface.FlagsToken.Length != 0 || surface.ExpectedFlags != 0 ||
                    surface.ExpectedOriginalId != oldId || surface.ExpectedReplacementId != newId)
                    throw new InvalidDataException($"Skin '{skin.Name}' has an unsupported or mismatched surface row.");
                text.Append("    ReplaceSurface(").Append(QuoteName(surface.OriginalSurface)).Append(", ")
                    .Append(NativeCharacterScriptCodec.Quote(surface.ReplacementSurface)).Append(", ")
                    .Append(NativeCharacterScriptCodec.Quote(surface.FlagsToken)).AppendLine(");");
            }
            text.AppendLine("}");

            void AppendFlag(Dl1SkinSourceFeatures flag, string command)
            {
                if ((sourceFeatures & flag) != 0)
                    text.Append("    ").Append(command).AppendLine("();");
            }

            void AppendColor(int slot)
            {
                int offset = 4 * slot;
                byte blue = skin.ExpectedCompiledColors[offset];
                byte green = skin.ExpectedCompiledColors[offset + 1];
                byte red = skin.ExpectedCompiledColors[offset + 2];
                byte alpha = skin.ExpectedCompiledColors[offset + 3];
                Dl1SkinSourceFeatures rgbFlag = slot == 0 ? Dl1SkinSourceFeatures.Color0Rgb : Dl1SkinSourceFeatures.Color1Rgb;
                Dl1SkinSourceFeatures alphaFlag = slot == 0 ? Dl1SkinSourceFeatures.Color0Alpha : Dl1SkinSourceFeatures.Color1Alpha;
                bool rgb = (sourceFeatures & rgbFlag) != 0;
                bool hasAlpha = (sourceFeatures & alphaFlag) != 0;
                if (!rgb && (red != 0 || green != 0 || blue != 0) || !hasAlpha && alpha != 255)
                    throw new InvalidDataException($"Skin '{skin.Name}' color slot {slot} differs from native default without a source command.");
                if (rgb && hasAlpha)
                    text.Append("    ColorIA(").Append(slot).Append(", ").Append(red).Append(", ")
                        .Append(green).Append(", ").Append(blue).Append(", ").Append(alpha).AppendLine(");");
                else if (rgb)
                    text.Append("    ColorI(").Append(slot).Append(", ").Append(red).Append(", ")
                        .Append(green).Append(", ").Append(blue).AppendLine(");");
                else if (hasAlpha)
                    text.Append("    ColorAlpha(").Append(slot).Append(", ").Append(alpha).AppendLine(");");
            }
        }
        Dl1SkinDefinitionDocument result = Read(text.ToString());
        if (!result.IsValid || result.RequiresReview)
            throw new InvalidDataException("Generated skin source did not pass source-preserving read-back.");
        return result;
    }

    private static byte SurfaceId(string token) => token.ToLowerInvariant() switch
    {
        "unknown" => 0,
        "water" => 3,
        "flesh" => 10,
        _ => throw new InvalidDataException("Surface token lacks a verified representative mapping."),
    };

    private static int FindParentSkin(NativeCharacterScriptDocument syntax, int index)
    {
        int parent = syntax.Calls[index].ParentCallIndex;
        while (parent >= 0)
        {
            if (syntax.Calls[parent].Name.Equals("Skin", StringComparison.OrdinalIgnoreCase)) return parent;
            parent = syntax.Calls[parent].ParentCallIndex;
        }
        return -1;
    }

    private static void RequireCount(NativeCharacterCall call, int expected)
    {
        if (call.Arguments.Length != expected)
            throw new FormatException($"Expected {expected} arguments, got {call.Arguments.Length}.");
    }

    private static string Quoted(NativeCharacterCall call, int argument, bool allowEmpty = false)
    {
        string value = NativeCharacterScriptCodec.Quoted(call.Arguments[argument]);
        if (value.Length == 0 && !allowEmpty) throw new FormatException("A nonempty quoted name is required.");
        return value;
    }

    private static string QuoteName(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 4096 || value.Any(char.IsControl))
            throw new InvalidDataException("Skin source names must be bounded nonempty text without control characters.");
        return NativeCharacterScriptCodec.Quote(value);
    }

    private sealed class SkinBuilder(string name, int callIndex)
    {
        public string Name { get; } = name;
        public int CallIndex { get; } = callIndex;
        public List<Dl1SkinMaterialReplacement> Materials { get; } = [];
        public List<Dl1SkinEntityVisibility> Entities { get; } = [];
        public List<Dl1SkinSurfaceReplacement> Surfaces { get; } = [];
        public List<Dl1SkinColorCommand> Colors { get; } = [];
        public List<string> Unknown { get; } = [];
        public Dl1SkinSourceFeatures Features { get; set; }

        public Dl1SkinBlock Build() => new(Name, CallIndex, Materials.ToImmutableArray(),
            Entities.ToImmutableArray(), Surfaces.ToImmutableArray(), Colors.ToImmutableArray(),
            Features, Unknown.ToImmutableArray());
    }
}
