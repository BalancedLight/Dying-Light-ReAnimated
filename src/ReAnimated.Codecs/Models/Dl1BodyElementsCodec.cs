using System.Collections.Immutable;

namespace ReAnimated.Codecs.Models;

public sealed record Dl1BodyElement(
    string ElementToken, ImmutableArray<double> NumericArguments, string HelperName,
    int CallIndex);

public sealed record Dl1BodyRelic(
    string Name, string PhysicsModeToken, string PhysicsResource,
    string EffectResource, string OffsetToken, string RotationToken,
    bool WithDestroyedChild, int BodyElementCallIndex, int CallIndex);

public sealed record Dl1BodyMeshDisable(
    string EntityName, bool FromRelic, int BodyElementCallIndex, int CallIndex);

public sealed record Dl1MeatPart(
    string MeshResource, double NumericArgument, int MeatPartsCallIndex, int CallIndex);

public sealed record Dl1DestroyedHeadPartsDeclaration(int CallIndex, string Template, int Count);
public sealed record Dl1BodyIndexedMeshDependency(int CallIndex, string Template, int Count, int Index, string Name);

public sealed record Dl1BodyElementsDocument(
    NativeCharacterScriptDocument Syntax,
    ImmutableArray<Dl1BodyElement> Elements,
    ImmutableArray<Dl1BodyRelic> Relics,
    ImmutableArray<Dl1BodyMeshDisable> MeshDisables,
    ImmutableArray<Dl1MeatPart> MeatParts,
    ImmutableArray<NativeCharacterReference> References,
    ImmutableArray<NativeCharacterDiagnostic> Diagnostics)
{
    public bool ForceGenericRelics { get; init; }
    public ImmutableArray<int> ForceGenericRelicsCallIndexes { get; init; } = [];
    public ImmutableArray<Dl1DestroyedHeadPartsDeclaration> DestroyedHeadPartDeclarations { get; init; } = [];
    public ImmutableArray<Dl1BodyIndexedMeshDependency> IndexedMeshDependencies { get; init; } = [];

    public bool IsValid => Diagnostics.All(static diagnostic => !diagnostic.IsError);

    public NativeCharacterRenameResult RenameReference(
        NativeCharacterReferenceKind kind, string oldName, string newName,
        IEnumerable<string>? availableNewNames = null) =>
        NativeCharacterScriptCodec.RenameReferences(
            Syntax, References, kind, oldName, newName, availableNewNames);
}

/// <summary>
/// Source-preserving BEL reader. Numeric arguments and physics-mode symbols are
/// retained as syntax fields; their native damage/severing effects are not inferred.
/// </summary>
public static class Dl1BodyElementsCodec
{
    public static Dl1BodyElementsDocument Read(
        string text, IEnumerable<string>? availableHelpers = null,
        IEnumerable<string>? availableResources = null)
    {
        NativeCharacterScriptDocument syntax = NativeCharacterScriptCodec.Parse(text);
        var elements = ImmutableArray.CreateBuilder<Dl1BodyElement>();
        var relics = ImmutableArray.CreateBuilder<Dl1BodyRelic>();
        var disables = ImmutableArray.CreateBuilder<Dl1BodyMeshDisable>();
        var meatParts = ImmutableArray.CreateBuilder<Dl1MeatPart>();
        var headParts = ImmutableArray.CreateBuilder<Dl1DestroyedHeadPartsDeclaration>();
        var indexedMeshes = ImmutableArray.CreateBuilder<Dl1BodyIndexedMeshDependency>();
        var genericRelicCalls = ImmutableArray.CreateBuilder<int>();
        var references = ImmutableArray.CreateBuilder<NativeCharacterReference>();
        var diagnostics = ImmutableArray.CreateBuilder<NativeCharacterDiagnostic>();
        HashSet<string>? helpers = availableHelpers?.ToHashSet(StringComparer.Ordinal);
        HashSet<string>? resources = availableResources?.ToHashSet(StringComparer.OrdinalIgnoreCase);
        int currentElement = -1;
        for (int index = 0; index < syntax.Calls.Length; index++)
        {
            NativeCharacterCall call = syntax.Calls[index];
            try
            {
                switch (call.Name)
                {
                    case "!include":
                        RequireCount(call, 1);
                        AddReference(NativeCharacterReferenceKind.IncludeResource, 0);
                        break;
                    case "LoadBodyElements":
                        RequireCount(call, 1);
                        AddReference(NativeCharacterReferenceKind.BodyElementsResource, 0);
                        break;
                    case "BodyElement":
                        RequireCount(call, 6);
                        string elementToken = call.Arguments[0];
                        if (string.IsNullOrWhiteSpace(elementToken) || elementToken[0] == '"')
                            throw new FormatException("A symbolic body-element token is required.");
                        ImmutableArray<double> numeric = call.Arguments.Skip(1).Take(4)
                            .Select(NativeCharacterScriptCodec.FiniteNumber).ToImmutableArray();
                        string helper = AddReference(NativeCharacterReferenceKind.Helper, 5);
                        if (helpers is not null && !helpers.Contains(helper))
                            diagnostics.Add(new("body_helper_missing",
                                $"Helper '{helper}' is absent from the supplied hierarchy.", true));
                        elements.Add(new(elementToken, numeric, helper, index));
                        currentElement = index;
                        break;
                    case "AddRelics":
                    case "AddRelicsWithDestroyedChild":
                        RequireCount(call, 6);
                        RequireElement();
                        string relicName = NativeCharacterScriptCodec.Quoted(call.Arguments[0]);
                        string physicsMode = call.Arguments[1];
                        string physics = AddReference(NativeCharacterReferenceKind.PhysicsResource, 2);
                        string effect = NativeCharacterScriptCodec.Quoted(call.Arguments[3]);
                        if (effect.Length > 0) AddReference(NativeCharacterReferenceKind.EffectResource, 3);
                        if (resources is not null && !resources.Contains(physics))
                            diagnostics.Add(new("body_physics_resource_missing",
                                $"Physics resource '{physics}' is absent from the supplied inventory.", true));
                        if (resources is not null && effect.Length > 0 && !resources.Contains(effect))
                            diagnostics.Add(new("body_effect_resource_missing",
                                $"Effect resource '{effect}' is absent from the supplied inventory.", true));
                        relics.Add(new(relicName, physicsMode, physics, effect,
                            call.Arguments[4], call.Arguments[5],
                            call.Name == "AddRelicsWithDestroyedChild", currentElement, index));
                        break;
                    case "AddMesh2Disable":
                    case "AddMesh2DisableFromRelic":
                        RequireCount(call, 1);
                        RequireElement();
                        disables.Add(new(AddReference(NativeCharacterReferenceKind.MeshEntity, 0),
                            call.Name == "AddMesh2DisableFromRelic", currentElement, index));
                        break;
                    case var name when name.Equals("ForceGenericRelics", StringComparison.OrdinalIgnoreCase):
                        RequireCount(call, 0);
                        genericRelicCalls.Add(index);
                        break;
                    case "DestroyedHeadParts":
                        RequireCount(call, 2);
                        string template = AddReference(NativeCharacterReferenceKind.MeshResource, 0);
                        int count = Dl1IndexedMeshTemplate.ParseCount(call.Arguments[1]);
                        ImmutableArray<Dl1IndexedMeshName> expanded = Dl1IndexedMeshTemplate.Expand(template, count);
                        headParts.Add(new(index, template, count));
                        foreach (Dl1IndexedMeshName dependency in expanded)
                            indexedMeshes.Add(new(index, dependency.Template, dependency.Count, dependency.Index, dependency.Name));
                        break;
                    case "AddMeatPart":
                        RequireCount(call, 2);
                        if (call.ParentCallIndex < 0 || syntax.Calls[call.ParentCallIndex].Name != "MeatParts")
                            throw new FormatException("AddMeatPart requires an enclosing MeatParts block.");
                        meatParts.Add(new(AddReference(NativeCharacterReferenceKind.MeshResource, 0),
                            NativeCharacterScriptCodec.FiniteNumber(call.Arguments[1]),
                            call.ParentCallIndex, index));
                        break;
                }
            }
            catch (Exception exception) when (exception is FormatException or ArgumentException or OverflowException)
            {
                diagnostics.Add(new("body_statement_invalid", $"'{call.Name}': {exception.Message}", true));
            }

            string AddReference(NativeCharacterReferenceKind kind, int argument)
            {
                string value = NativeCharacterScriptCodec.Quoted(call.Arguments[argument]);
                if (value.Length == 0 && kind != NativeCharacterReferenceKind.EffectResource)
                    throw new FormatException("A nonempty resource or entity name is required.");
                references.Add(new(kind, value, index, argument));
                return value;
            }

            void RequireElement()
            {
                if (currentElement < 0) throw new FormatException("A preceding BodyElement is required.");
            }
        }
        if (elements.Select(static element => element.ElementToken)
            .Distinct(StringComparer.Ordinal).Count() != elements.Count)
            diagnostics.Add(new("body_element_duplicate", "BodyElement tokens must be distinct.", true));
        return new(syntax, elements.ToImmutable(), relics.ToImmutable(), disables.ToImmutable(),
            meatParts.ToImmutable(), references.ToImmutable(), diagnostics.ToImmutable())
        {
            DestroyedHeadPartDeclarations = headParts.ToImmutable(), IndexedMeshDependencies = indexedMeshes.ToImmutable(),
            ForceGenericRelics = genericRelicCalls.Count > 0, ForceGenericRelicsCallIndexes = genericRelicCalls.ToImmutable(),
        };
    }

    private static void RequireCount(NativeCharacterCall call, int expected)
    {
        if (call.Arguments.Length != expected)
            throw new FormatException($"Expected {expected} arguments, got {call.Arguments.Length}.");
    }
}
