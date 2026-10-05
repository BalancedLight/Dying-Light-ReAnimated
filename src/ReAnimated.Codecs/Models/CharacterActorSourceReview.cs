using System.Collections.Immutable;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace ReAnimated.Codecs.Models;

/// <summary>An exact source call chosen by its parser index and original quoted identity.</summary>
public sealed record CharacterActorCallExpectation(int CallIndex, string CallName, string? FirstQuotedArgument = null);

public sealed record CharacterActorSourceReviewRequest(
    string ExpectedSourceSha256,
    string ExpectedModelName,
    int? ModelCallIndex = null,
    CharacterActorCallExpectation? EnclosingActor = null,
    int? ExpectedModelTokenStart = null,
    int? ExpectedModelTokenLength = null);

public sealed record CharacterActorReferenceExpectation(
    int CallIndex, int ArgumentIndex, int SourceStart, int SourceLength, string Value);

public enum CharacterActorReferenceScope { Scoped, UnverifiedLead }

/// <summary>Character offsets into the unchanged source text, not inferred runtime references.</summary>
public sealed record CharacterActorQuotedReference(
    string CallName,
    int CallIndex,
    int ParentCallIndex,
    int CallStart,
    int CallLength,
    int ArgumentIndex,
    int ArgumentStart,
    int ArgumentLength,
    string Value,
    CharacterActorReferenceScope Scope);

public sealed record CharacterActorSourceReviewResult(
    NativeCharacterScriptDocument Document,
    string SourceSha256,
    int ModelCallIndex,
    int ModelArgumentIndex,
    int ModelTokenStart,
    int ModelTokenLength,
    int? EnclosingActorCallIndex,
    ImmutableArray<CharacterActorQuotedReference> References)
{
    public ImmutableArray<CharacterActorQuotedReference> ScopedReferences =>
        References.Where(r => r.Scope == CharacterActorReferenceScope.Scoped).ToImmutableArray();

    public ImmutableArray<CharacterActorQuotedReference> UnverifiedLeads =>
        References.Where(r => r.Scope == CharacterActorReferenceScope.UnverifiedLead).ToImmutableArray();
}

/// <summary>
/// Reviews syntactic scope for a selected model declaration. A scoped quote is
/// source-custody evidence only; this does not identify native consumers,
/// loaded resources, actor class, facial data, or gameplay behavior.
/// </summary>
public static class CharacterActorSourceReview
{
    private static readonly HashSet<string> ResourceFields = new(StringComparer.Ordinal)
    {
        "MeshName", "m_FaceMimicFile", "m_MpcScript", "PhysicsScript",
    };
    private static readonly HashSet<string> ResourceCalls = new(StringComparer.Ordinal)
    {
        "!include", "import", "PhysicsScript", "FacialScript", "LoadBodyElements", "BehaviorSet", "MpcScript",
    };
    private static readonly HashSet<string> ResourceExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".scr", ".def", ".phx", ".mpcloth", ".bel", ".fed", ".msh", ".msh_obj",
        ".skn", ".ascr", ".bscr", ".fx",
    };

    public static CharacterActorSourceReviewResult Review(string sourceText, CharacterActorSourceReviewRequest request)
    {
        ArgumentNullException.ThrowIfNull(sourceText);
        ArgumentNullException.ThrowIfNull(request);
        string expectedHash = request.ExpectedSourceSha256;
        if (expectedHash is null || expectedHash.Length != 64 ||
            expectedHash.Any(c => !Uri.IsHexDigit(c)) || string.IsNullOrWhiteSpace(request.ExpectedModelName))
            throw new ArgumentException("Exact source SHA-256 and model name are required.", nameof(request));
        string hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(sourceText)));
        if (!hash.Equals(expectedHash, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The actor source changed since its expected SHA-256 was reviewed.");
        NativeCharacterScriptDocument document = NativeCharacterScriptCodec.Parse(sourceText);
        ImmutableArray<NativeCharacterCall> calls = document.Calls;
        int[] matches = Enumerable.Range(0, calls.Length).Where(i => IsSupportedModelDeclaration(calls, i) &&
            GetModelNameArgument(calls[i])!.Value == request.ExpectedModelName).ToArray();
        int modelIndex;
        if (request.ModelCallIndex is { } selected)
        {
            if ((uint)selected >= (uint)calls.Length || !matches.Contains(selected))
                throw new InvalidDataException("The selected model declaration call or its quoted name no longer matches.");
            modelIndex = selected;
        }
        else
        {
            if (matches.Length != 1)
                throw new InvalidDataException("Exactly one model declaration must match, or its call index must be selected explicitly.");
            modelIndex = matches[0];
        }
        NativeCharacterQuotedArgument modelToken = GetModelNameArgument(calls[modelIndex])!;
        bool presetModel = calls[modelIndex].Name == "SetField";
        if (request.ExpectedModelTokenStart.HasValue != request.ExpectedModelTokenLength.HasValue ||
            request.ExpectedModelTokenStart is { } expectedStart && modelToken.Start != expectedStart ||
            request.ExpectedModelTokenLength is { } expectedLength && modelToken.Length != expectedLength)
            throw new InvalidDataException("The selected model declaration token span changed.");

        int? actorIndex = null;
        if (request.EnclosingActor is { } actor)
        {
            if ((uint)actor.CallIndex >= (uint)calls.Length ||
                calls[actor.CallIndex].Name != actor.CallName ||
                actor.FirstQuotedArgument is not null && QuotedZero(calls[actor.CallIndex]) != actor.FirstQuotedArgument ||
                !IsDescendant(calls, modelIndex, actor.CallIndex))
                throw new InvalidDataException("The expected enclosing actor call, token, or scope no longer matches.");
            actorIndex = actor.CallIndex;
        }

        if (presetModel && (actorIndex is not { } presetOwner || calls[presetOwner].Name != "Preset" ||
            calls[modelIndex].ParentCallIndex != presetOwner))
            throw new InvalidDataException("Select the exact enclosing Preset for its MeshName declaration.");
        int modelCountInActor = actorIndex is { } owner
            ? Enumerable.Range(0, calls.Length).Count(i => (calls[i].Name is "Model" or "Mesh" ||
                calls[i].Name == "SetField" && QuotedZero(calls[i]) == "MeshName") &&
                (presetModel ? calls[i].ParentCallIndex == owner : IsDescendant(calls, i, owner)))
            : 0;
        bool actorPeersAreUnique = actorIndex is { } scope && calls[modelIndex].ParentCallIndex == scope && modelCountInActor == 1;
        bool soleModelInSource = calls.Count(call => call.Name is "Model" or "Mesh" ||
            call.Name == "SetField" && QuotedZero(call) == "MeshName") == 1;
        var references = ImmutableArray.CreateBuilder<CharacterActorQuotedReference>();
        for (int index = 0; index < calls.Length; index++)
        {
            NativeCharacterCall call = calls[index];
            foreach (NativeCharacterQuotedArgument quoted in call.QuotedArguments)
            {
                if (index == modelIndex && quoted.ArgumentIndex == modelToken.ArgumentIndex) continue;
                if (!ResourceExtensions.Contains(Path.GetExtension(quoted.Value))) continue;
                bool supported = ResourceCalls.Contains(call.Name) || call.Name == "SetField" &&
                    quoted.ArgumentIndex == 1 && ResourceFields.Contains(QuotedZero(call) ?? string.Empty);
                bool inModelBlock = IsDescendant(calls, index, modelIndex) && !CrossesPreset(calls, index, modelIndex);
                bool actorPeer = actorPeersAreUnique && call.ParentCallIndex == actorIndex;
                bool inheritedInclude = (call.Name is "!include" or "import") && call.ParentCallIndex == -1 &&
                    soleModelInSource && actorIndex is null && calls[modelIndex].ParentCallIndex == -1;
                CharacterActorReferenceScope referenceScope = supported && (inModelBlock || actorPeer || inheritedInclude)
                    ? CharacterActorReferenceScope.Scoped : CharacterActorReferenceScope.UnverifiedLead;
                references.Add(new(call.Name, index, call.ParentCallIndex, call.Start, call.Length,
                    quoted.ArgumentIndex, quoted.Start, quoted.Length, quoted.Value, referenceScope));
            }
        }
        return new(document, hash, modelIndex, modelToken.ArgumentIndex, modelToken.Start, modelToken.Length,
            actorIndex, references.ToImmutable());
    }

    /// <summary>Recheck an exact saved source selection and its reviewed, syntactically scoped tokens.</summary>
    public static CharacterActorSourceReviewResult Revalidate(string sourceText,
        CharacterActorSourceReviewRequest request, IEnumerable<CharacterActorReferenceExpectation> reviewedReferences)
    {
        ArgumentNullException.ThrowIfNull(reviewedReferences);
        CharacterActorSourceReviewResult result = Review(sourceText, request);
        var used = new HashSet<(int Call, int Argument)>();
        foreach (CharacterActorReferenceExpectation expected in reviewedReferences)
        {
            if (!used.Add((expected.CallIndex, expected.ArgumentIndex)) ||
                !result.ScopedReferences.Any(reference => reference.CallIndex == expected.CallIndex &&
                    reference.ArgumentIndex == expected.ArgumentIndex && reference.ArgumentStart == expected.SourceStart &&
                    reference.ArgumentLength == expected.SourceLength && reference.Value == expected.Value))
                throw new InvalidDataException("A reviewed actor source reference changed or is outside the selected scope.");
        }
        return result;
    }

    public static NativeCharacterQuotedArgument? GetModelNameArgument(NativeCharacterCall call)
    {
        ArgumentNullException.ThrowIfNull(call);
        int argument = call.Name is "Model" or "Mesh" ? 0 :
            call.Name == "SetField" && QuotedZero(call) == "MeshName" ? 1 : -1;
        return argument < 0 ? null : call.QuotedArguments.SingleOrDefault(quoted => quoted.ArgumentIndex == argument);
    }

    public static bool IsSupportedModelDeclaration(ImmutableArray<NativeCharacterCall> calls, int index)
    {
        if ((uint)index >= (uint)calls.Length || GetModelNameArgument(calls[index]) is null)
            return false;
        if (calls[index].Name != "SetField")
            return true;
        int preset = calls[index].ParentCallIndex;
        if (preset < 0 || calls[preset].Name != "Preset" || QuotedZero(calls[preset]) is null)
            return false;
        int definition = calls[preset].ParentCallIndex;
        return definition >= 0 && calls[definition].Name == "PresetDef" && QuotedZero(calls[definition]) is not null;
    }

    private static bool CrossesPreset(ImmutableArray<NativeCharacterCall> calls, int index, int ancestor)
    {
        for (int parent = calls[index].ParentCallIndex; parent >= 0 && parent != ancestor; parent = calls[parent].ParentCallIndex)
            if (calls[parent].Name == "Preset")
                return true;
        return false;
    }

    private static string? QuotedZero(NativeCharacterCall call) =>
        call.QuotedArguments.FirstOrDefault(q => q.ArgumentIndex == 0)?.Value;

    private static bool IsDescendant(ImmutableArray<NativeCharacterCall> calls, int callIndex, int ancestorIndex)
    {
        for (int parent = calls[callIndex].ParentCallIndex; parent >= 0; parent = calls[parent].ParentCallIndex)
            if (parent == ancestorIndex) return true;
        return false;
    }
}
