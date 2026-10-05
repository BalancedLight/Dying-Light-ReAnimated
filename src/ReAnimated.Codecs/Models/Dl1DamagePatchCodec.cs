using System.Collections.Immutable;

namespace ReAnimated.Codecs.Models;

public sealed record Dl1DamagePatch(
    string PatchName,
    string HelperName,
    ImmutableArray<double> XformValues,
    bool UseGenericPatch,
    int CallIndex);

public sealed record Dl1DamagePatchDocument(
    NativeCharacterScriptDocument Syntax,
    ImmutableArray<Dl1DamagePatch> Patches,
    ImmutableArray<NativeCharacterReference> References,
    ImmutableArray<NativeCharacterDiagnostic> Diagnostics)
{
    public bool IsValid => Diagnostics.All(static diagnostic => !diagnostic.IsError);

    public NativeCharacterRenameResult RenamePatch(
        string oldName, string newName, IEnumerable<string>? availablePatches = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(oldName);
        ArgumentException.ThrowIfNullOrWhiteSpace(newName);
        if (Patches.Any(patch => patch.PatchName == newName && patch.PatchName != oldName))
            return new(Syntax, [new NativeCharacterDiagnostic("damage_patch_rename_conflict",
                $"Patch '{newName}' is already declared.", true)]);
        return NativeCharacterScriptCodec.RenameReferences(
            Syntax, References, NativeCharacterReferenceKind.Patch,
            oldName, newName, availablePatches);
    }

    public NativeCharacterRenameResult RenameHelper(
        string oldName, string newName, IEnumerable<string>? availableHelpers = null) =>
        NativeCharacterScriptCodec.RenameReferences(
            Syntax, References, NativeCharacterReferenceKind.Helper,
            oldName, newName, availableHelpers);
}

/// <summary>
/// Source-preserving reader of Damage(name, helper) blocks and their 12 literal
/// Xform values. Their native geometry/effect interpretation is not inferred.
/// </summary>
public static class Dl1DamagePatchCodec
{
    public static Dl1DamagePatchDocument Read(
        string text, IEnumerable<string>? availableHelpers = null)
    {
        NativeCharacterScriptDocument syntax = NativeCharacterScriptCodec.Parse(text);
        var patches = new Dictionary<int, PatchDraft>();
        var references = ImmutableArray.CreateBuilder<NativeCharacterReference>();
        var diagnostics = ImmutableArray.CreateBuilder<NativeCharacterDiagnostic>();
        HashSet<string>? helpers = availableHelpers?.ToHashSet(StringComparer.Ordinal);
        for (int index = 0; index < syntax.Calls.Length; index++)
        {
            NativeCharacterCall call = syntax.Calls[index];
            try
            {
                switch (call.Name)
                {
                    case "Damage":
                        RequireCount(call, 2);
                        string patch = NativeCharacterScriptCodec.Quoted(call.Arguments[0]);
                        string helper = NativeCharacterScriptCodec.Quoted(call.Arguments[1]);
                        if (string.IsNullOrWhiteSpace(patch) || string.IsNullOrWhiteSpace(helper))
                            throw new FormatException("Patch and helper names must be nonempty.");
                        references.Add(new(NativeCharacterReferenceKind.Patch, patch, index, 0));
                        references.Add(new(NativeCharacterReferenceKind.Helper, helper, index, 1));
                        if (helpers is not null && !helpers.Contains(helper))
                            diagnostics.Add(new("damage_helper_missing",
                                $"Helper '{helper}' is absent from the supplied hierarchy.", true));
                        patches.Add(index, new PatchDraft(patch, helper, index));
                        break;
                    case "Xform":
                        RequireCount(call, 12);
                        PatchDraft xformOwner = Owner();
                        if (!xformOwner.Xform.IsDefault)
                            throw new FormatException("A Damage block has more than one Xform call.");
                        xformOwner.Xform = call.Arguments
                            .Select(NativeCharacterScriptCodec.FiniteNumber).ToImmutableArray();
                        break;
                    case "UseGenericPatch":
                        RequireCount(call, 0);
                        PatchDraft genericOwner = Owner();
                        if (genericOwner.UseGenericPatch)
                            throw new FormatException("A Damage block repeats UseGenericPatch.");
                        genericOwner.UseGenericPatch = true;
                        break;
                }
            }
            catch (Exception exception) when (exception is FormatException or ArgumentException or OverflowException)
            {
                diagnostics.Add(new("damage_statement_invalid", $"'{call.Name}': {exception.Message}", true));
            }

            PatchDraft Owner()
            {
                if (call.ParentCallIndex < 0 || !patches.TryGetValue(call.ParentCallIndex, out PatchDraft? owner))
                    throw new FormatException("A Damage block is required.");
                return owner;
            }
        }
        Dl1DamagePatch[] ordered = patches.Values.OrderBy(static patch => patch.CallIndex)
            .Select(static patch => new Dl1DamagePatch(patch.Name, patch.Helper,
                patch.Xform.IsDefault ? [] : patch.Xform, patch.UseGenericPatch, patch.CallIndex))
            .ToArray();
        if (ordered.Select(static patch => patch.PatchName).Distinct(StringComparer.Ordinal).Count() != ordered.Length)
            diagnostics.Add(new("damage_patch_duplicate", "Damage patch names must be distinct.", true));
        foreach (Dl1DamagePatch patch in ordered.Where(static patch => patch.XformValues.IsEmpty))
            diagnostics.Add(new("damage_xform_missing",
                $"Damage patch '{patch.PatchName}' has no measured Xform call."));
        return new(syntax, [.. ordered], references.ToImmutable(), diagnostics.ToImmutable());
    }

    private static void RequireCount(NativeCharacterCall call, int expected)
    {
        if (call.Arguments.Length != expected)
            throw new FormatException($"Expected {expected} arguments, got {call.Arguments.Length}.");
    }

    private sealed class PatchDraft(string name, string helper, int callIndex)
    {
        public string Name { get; } = name;
        public string Helper { get; } = helper;
        public int CallIndex { get; } = callIndex;
        public ImmutableArray<double> Xform { get; set; }
        public bool UseGenericPatch { get; set; }
    }
}
