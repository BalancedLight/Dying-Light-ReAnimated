using System.Collections.Immutable;

namespace ReAnimated.Codecs.Models;

public sealed record Dl1PlayerAppearance(
    string CharacterId,
    string HeadId,
    string BodyId,
    string AppearanceId,
    string FirstPersonMesh,
    string ThirdPersonMesh,
    string SkinName,
    int CallIndex)
{
    public Dl1PlayerAppearanceAvailability Availability { get; init; } =
        Dl1PlayerAppearanceAvailability.Empty;
}

public sealed record Dl1PlayerAppearanceUnlock(
    string Name,
    ImmutableArray<string> Arguments,
    int CallIndex);

public sealed record Dl1PlayerAppearanceAvailability(
    bool AvailableOnStart,
    bool AvailableOnPrologue,
    bool IsDefault,
    ImmutableArray<Dl1PlayerAppearanceUnlock> Unlocks)
{
    public static Dl1PlayerAppearanceAvailability Empty { get; } =
        new(false, false, false, []);

    public bool IsAvailableOnStart => AvailableOnStart;
    public bool IsAvailableOnPrologue => AvailableOnPrologue;
}

/// <summary>Source-preserving selection of native Player FPP/TPP resources.</summary>
public sealed record Dl1PlayerAppearanceDocument(
    NativeCharacterScriptDocument Syntax,
    ImmutableArray<Dl1PlayerAppearance> Appearances)
{
    /// <summary>Changes only the three binding arguments of one existing appearance.</summary>
    public Dl1PlayerAppearanceDocument Bind(
        string characterId, string appearanceId,
        string firstPersonMesh, string thirdPersonMesh, string skinName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(characterId);
        ArgumentException.ThrowIfNullOrWhiteSpace(appearanceId);
        ValidateMesh(firstPersonMesh);
        ValidateMesh(thirdPersonMesh);
        _ = Dl1SourceModelWriter.RequireExactResourceName(skinName, 63, nameof(skinName));
        Dl1PlayerAppearance[] selected = Appearances.Where(row =>
            row.CharacterId == characterId && row.AppearanceId == appearanceId).ToArray();
        if (selected.Length != 1)
            throw new InvalidDataException("Select one unique existing character and appearance before assigning perspective resources.");
        Dl1PlayerAppearance row = selected[0];
        NativeCharacterTokenReplacement Replacement(string name, string value)
        {
            int index = Enumerable.Range(0, Syntax.Calls.Length).Single(index =>
                Syntax.Calls[index].ParentCallIndex == row.CallIndex && Syntax.Calls[index].Name == name);
            NativeCharacterCall call = Syntax.Calls[index];
            return new(index, 0, call.QuotedArguments.Single().Value, value);
        }
        NativeCharacterScriptDocument changed = Syntax.ReplaceQuotedArguments([
            Replacement("MeshFpp", firstPersonMesh),
            Replacement("MeshTpp", thirdPersonMesh),
            Replacement("Skin", skinName),
        ]);
        return Dl1PlayerAppearanceCodec.Read(changed.Write());
    }

    private static void ValidateMesh(string mesh)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(mesh);
        if (!mesh.EndsWith(".msh", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Player mesh bindings require a .msh resource identity.", nameof(mesh));
        _ = Dl1SourceModelWriter.RequireExactResourceName(mesh[..^4], 55, nameof(mesh));
    }
}

public static class Dl1PlayerAppearanceCodec
{
    /// <summary>Reads existing appearances without changing comments, unrelated calls, or selection flags.</summary>
    public static Dl1PlayerAppearanceDocument Read(string text)
    {
        NativeCharacterScriptDocument syntax = NativeCharacterScriptCodec.Parse(text);
        var rows = ImmutableArray.CreateBuilder<Dl1PlayerAppearance>();
        string Quoted(NativeCharacterCall call, int argument, int count)
        {
            if (call.Arguments.Length != count || call.QuotedArguments.Length != count)
                throw new InvalidDataException($"{call.Name} requires {count} complete quoted argument(s).");
            return call.QuotedArguments.Single(token => token.ArgumentIndex == argument).Value;
        }
        ImmutableArray<(NativeCharacterCall Call, int CallIndex)> unlockConditions =
            ReadUnlockConditions(syntax);
        for (int index = 0; index < syntax.Calls.Length; index++)
        {
            NativeCharacterCall call = syntax.Calls[index];
            if (call.Name != "Appearance") continue;
            if (call.ParentCallIndex < 0 || syntax.Calls[call.ParentCallIndex].Name != "Character")
                throw new InvalidDataException("A Player appearance must belong directly to a Character section.");
            string character = Quoted(syntax.Calls[call.ParentCallIndex], 0, 1);
            string Child(string name)
            {
                NativeCharacterCall[] children = syntax.Calls.Where(child =>
                    child.ParentCallIndex == index && child.Name == name).ToArray();
                if (children.Length != 1)
                    throw new InvalidDataException($"Each Player appearance requires one direct {name} binding.");
                return Quoted(children[0], 0, 1);
            }
            Dl1PlayerAppearanceAvailability availability =
                ReadAvailability(
                    syntax,
                    index,
                    character,
                    Quoted(call, 2, 3),
                    unlockConditions);
            rows.Add(new(character, Quoted(call, 0, 3), Quoted(call, 1, 3), Quoted(call, 2, 3),
                Child("MeshFpp"), Child("MeshTpp"), Child("Skin"), index)
            {
                Availability = availability,
            });
        }
        if (rows.GroupBy(row => (row.CharacterId, row.AppearanceId)).Any(group => group.Count() != 1))
            throw new InvalidDataException("Player appearance identities are ambiguous within a character.");
        return new(syntax, rows.ToImmutable());
    }

    private static Dl1PlayerAppearanceAvailability ReadAvailability(
        NativeCharacterScriptDocument syntax,
        int appearanceCallIndex,
        string characterId,
        string appearanceId,
        ImmutableArray<(NativeCharacterCall Call, int CallIndex)> unlockConditions)
    {
        NativeCharacterCall[] directChildren = syntax.Calls
            .Where(call => call.ParentCallIndex == appearanceCallIndex)
            .ToArray();
        bool Flag(string name) => directChildren
            .Where(call => string.Equals(
                call.Name,
                name,
                StringComparison.OrdinalIgnoreCase))
            .Any(call =>
                (name != "AvailableOnPrologue" &&
                    call.Arguments.Length == 0) ||
                (name == "AvailableOnPrologue" && call.Arguments.Length == 1 &&
                    bool.TryParse(call.Arguments[0], out bool value) && value));
        return new(
            Flag("AvailableOnStart"),
            Flag("AvailableOnPrologue"),
            Flag("Default"),
            unlockConditions
                .Where(entry => HasOrderedIdentity(
                    entry.Call,
                    characterId,
                    appearanceId))
                .Select(entry => new Dl1PlayerAppearanceUnlock(
                    entry.Call.Name,
                    entry.Call.Arguments,
                    entry.CallIndex))
                .ToImmutableArray());
    }

    private static ImmutableArray<(NativeCharacterCall Call, int CallIndex)>
        ReadUnlockConditions(NativeCharacterScriptDocument syntax)
    {
        HashSet<int> unlockSectionIndices = syntax.Calls
            .Select((call, callIndex) => (call, callIndex))
            .Where(entry => string.Equals(
                entry.call.Name,
                "unlock",
                StringComparison.OrdinalIgnoreCase))
            .SelectMany(entry => syntax.Calls
                .Select((_, callIndex) => callIndex)
                .Where(callIndex => IsDescendantOf(
                    syntax.Calls,
                    callIndex,
                    entry.callIndex)))
            .ToHashSet();
        return syntax.Calls
            .Select((call, callIndex) => (call, callIndex))
            .Where(entry => unlockSectionIndices.Contains(entry.callIndex))
            .ToImmutableArray();
    }

    private static bool IsDescendantOf(
        ImmutableArray<NativeCharacterCall> calls,
        int callIndex,
        int ancestorIndex)
    {
        for (int parent = calls[callIndex].ParentCallIndex;
             parent >= 0;
             parent = calls[parent].ParentCallIndex)
        {
            if (parent == ancestorIndex)
                return true;
        }
        return false;
    }

    private static bool HasOrderedIdentity(
        NativeCharacterCall call,
        string characterId,
        string appearanceId)
    {
        NativeCharacterQuotedArgument[] quoted = call.QuotedArguments
            .OrderBy(static argument => argument.ArgumentIndex)
            .ToArray();
        return quoted.Zip(
                quoted.Skip(1),
                static (first, second) =>
                    (Character: first.Value, Appearance: second.Value))
            .Any(pair =>
                string.Equals(pair.Character, characterId, StringComparison.Ordinal) &&
                string.Equals(pair.Appearance, appearanceId, StringComparison.Ordinal));
    }
}
