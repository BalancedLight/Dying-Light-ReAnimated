using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using ReAnimated.Core.ModelAuthoring;

namespace ReAnimated.Codecs.Models;

public enum CharacterCompanionFamily { Ragdoll, BodyElements, DamagePatches }

public sealed record ReviewedCharacterNumericEdit(
    string ResourceId,
    CharacterCompanionFamily Family,
    string ExpectedSha256,
    int CallIndex,
    int ArgumentIndex,
    string ExpectedLiteral,
    double NewValue);

public sealed record ReviewedCharacterNameEdit(
    string ResourceId,
    CharacterCompanionFamily Family,
    string ExpectedSha256,
    NativeCharacterReferenceKind Kind,
    string OldName,
    string NewName,
    ImmutableArray<string> ReviewedTargetNames);

/// <summary>One exact, typed source reference; unlike a declaration rename, other occurrences stay unchanged.</summary>
public sealed record ReviewedCharacterReferenceEdit(
    string ResourceId,
    CharacterCompanionFamily Family,
    string ExpectedSha256,
    int CallIndex,
    int ArgumentIndex,
    NativeCharacterReferenceKind Kind,
    string ExpectedName,
    string NewName,
    ImmutableArray<string> ReviewedTargetNames);

/// <summary>One exact PHX shape token with an explicit reviewed target choice.</summary>
public sealed record ReviewedRagdollShapeEdit(
    string ResourceId,
    string ExpectedSha256,
    int CallIndex,
    string ExpectedShape,
    string NewShape,
    ImmutableArray<string> ReviewedSourceShapeTokens);

public sealed record ReviewedBodyMeshDisableAddition(
    string ResourceId,
    string ExpectedSha256,
    int BodyElementCallIndex,
    string ExpectedElementToken,
    string ExpectedHelperName,
    string EntityName,
    ImmutableArray<string> ReviewedAvailableEntityNames,
    bool Reviewed);

public sealed record ReviewedCharacterResourceRename(
    string ResourceId,
    string ExpectedSha256,
    string NewFileName);

public sealed record CharacterCompanionReadResult(
    CharacterResourceRecord Resource,
    NativeCharacterScriptDocument Syntax,
    Dl1RagdollDocument? Ragdoll,
    Dl1BodyElementsDocument? BodyElements,
    Dl1DamagePatchDocument? DamagePatches,
    ImmutableArray<NativeCharacterDiagnostic> Diagnostics)
{
    public bool IsValid => Diagnostics.All(static diagnostic => !diagnostic.IsError);
}

public sealed record CharacterCompanionAuthoringResult(
    CustomModelPackage Package,
    ImmutableArray<string> ChangedResourceIds,
    ImmutableArray<NativeCharacterDiagnostic> Diagnostics)
{
    public bool Applied => !ChangedResourceIds.IsDefaultOrEmpty &&
        Diagnostics.All(static diagnostic => !diagnostic.IsError);
}

/// <summary>
/// Applies reviewed source-token changes to immutable local character packages.
/// All edited resources are marked for semantic review and compiled/loaded/Player
/// receipts are invalidated. Source syntax is not native behavior evidence.
/// </summary>
public static class CharacterCompanionAuthoring
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public static ImmutableArray<CharacterResourceRecord> SelectCandidates(
        CustomModelPackage package, CharacterCompanionFamily family)
    {
        CharacterResourceInventory inventory = RequireInventory(package);
        return inventory.Resources.Where(resource => !resource.IsOriginalArchive &&
            resource.EntryPath is not null &&
            Supports(resource, family)).OrderBy(static resource => resource.LogicalName,
                StringComparer.OrdinalIgnoreCase).ToImmutableArray();
    }

    public static CharacterCompanionReadResult Read(
        CustomModelPackage package, string resourceId, CharacterCompanionFamily family,
        IEnumerable<string>? availableNames = null,
        IEnumerable<string>? availableResources = null)
    {
        CharacterResourceRecord resource = RequireResource(package, resourceId);
        if (!Supports(resource, family))
            throw new InvalidDataException($"Resource '{resource.LogicalName}' is not a {family} source.");
        string text = DecodeText(RequirePayload(package, resource), out _);
        return ParseSelected(resource, text, family, availableNames, availableResources);
    }

    private static CharacterCompanionReadResult ParseSelected(
        CharacterResourceRecord resource, string text, CharacterCompanionFamily family,
        IEnumerable<string>? availableNames = null,
        IEnumerable<string>? availableResources = null)
    {
        return family switch
        {
            CharacterCompanionFamily.Ragdoll => FromRagdoll(),
            CharacterCompanionFamily.BodyElements => FromBodyElements(),
            CharacterCompanionFamily.DamagePatches => FromDamagePatches(),
            _ => throw new ArgumentOutOfRangeException(nameof(family)),
        };

        CharacterCompanionReadResult FromRagdoll()
        {
            Dl1RagdollDocument read = Dl1RagdollCodec.Read(text, availableNames, availableResources);
            return new(resource, read.Syntax, read, null, null, read.Diagnostics);
        }
        CharacterCompanionReadResult FromBodyElements()
        {
            Dl1BodyElementsDocument read = Dl1BodyElementsCodec.Read(text, availableNames, availableResources);
            return new(resource, read.Syntax, null, read, null, read.Diagnostics);
        }
        CharacterCompanionReadResult FromDamagePatches()
        {
            Dl1DamagePatchDocument read = Dl1DamagePatchCodec.Read(text, availableNames);
            return new(resource, read.Syntax, null, null, read, read.Diagnostics);
        }
    }

    private static bool Supports(CharacterResourceRecord resource, CharacterCompanionFamily family)
    {
        string extension = Path.GetExtension(resource.LogicalName);
        return family switch
        {
            CharacterCompanionFamily.Ragdoll =>
                extension.Equals(".phx", StringComparison.OrdinalIgnoreCase) &&
                resource.Subsystem == CharacterSubsystem.Ragdoll,
            CharacterCompanionFamily.BodyElements =>
                extension.Equals(".bel", StringComparison.OrdinalIgnoreCase) ||
                extension.Equals(".scr", StringComparison.OrdinalIgnoreCase),
            CharacterCompanionFamily.DamagePatches =>
                extension.Equals(".scr", StringComparison.OrdinalIgnoreCase),
            _ => false,
        };
    }

    public static CharacterCompanionAuthoringResult ApplyNumericEdit(
        CustomModelPackage package, ReviewedCharacterNumericEdit proposal)
    {
        ArgumentNullException.ThrowIfNull(package);
        ArgumentNullException.ThrowIfNull(proposal);
        try
        {
            CharacterCompanionReadResult selected = Read(package, proposal.ResourceId, proposal.Family);
            VerifyExpectedHash(selected.Resource, proposal.ExpectedSha256);
            if (!selected.IsValid)
                return Refuse(package, selected.Diagnostics);
            if ((uint)proposal.CallIndex >= (uint)selected.Syntax.Calls.Length ||
                !NumericArgumentIsMeasured(selected.Syntax.Calls[proposal.CallIndex],
                    proposal.Family, proposal.ArgumentIndex))
                return Refuse(package, "character_numeric_unclassified",
                    "The selected numeric argument has no measured edit contract.");
            NativeCharacterCall call = selected.Syntax.Calls[proposal.CallIndex];
            if (RequiresInteger(call, proposal.Family, proposal.ArgumentIndex) &&
                (proposal.NewValue != Math.Truncate(proposal.NewValue) ||
                 proposal.NewValue < int.MinValue || proposal.NewValue > int.MaxValue))
                return Refuse(package, "character_numeric_integer_required",
                    "The selected source field requires an integer literal.");
            NativeCharacterNumericKind kind = RequiresInteger(call, proposal.Family, proposal.ArgumentIndex)
                ? NativeCharacterNumericKind.WholeNumber
                : proposal.Family == CharacterCompanionFamily.Ragdoll &&
                  (call.Name == "UseBoneScale" && proposal.ArgumentIndex == 3 ||
                   call.Name == "Mass" && proposal.ArgumentIndex == 0)
                    ? NativeCharacterNumericKind.RealNumber : NativeCharacterNumericKind.Source;
            NativeCharacterScriptDocument changed = selected.Syntax.ReplaceNumericArguments(
                [new(proposal.CallIndex, proposal.ArgumentIndex,
                    proposal.ExpectedLiteral, proposal.NewValue) { Kind = kind }]);
            CharacterCompanionReadResult reopened = ParseSelected(selected.Resource,
                changed.Write(), proposal.Family);
            if (!reopened.IsValid)
                return Refuse(package, reopened.Diagnostics);
            return CommitTextChanges(package,
                [(selected.Resource.Id, changed.Write())],
                [new("character_numeric_review_pending",
                    "The source token changed; compiler and native physics/damage behavior require renewed review.")]);
        }
        catch (Exception exception) when (exception is ArgumentException or FormatException or InvalidDataException or OverflowException)
        {
            return Refuse(package, "character_numeric_edit_refused", exception.Message);
        }
    }

    public static CharacterCompanionAuthoringResult ApplyNameEdit(
        CustomModelPackage package, ReviewedCharacterNameEdit proposal)
    {
        ArgumentNullException.ThrowIfNull(package);
        ArgumentNullException.ThrowIfNull(proposal);
        try
        {
            if (proposal.ReviewedTargetNames.IsDefaultOrEmpty ||
                !proposal.ReviewedTargetNames.Contains(proposal.NewName, StringComparer.Ordinal))
                return Refuse(package, "character_name_target_missing",
                    "The replacement is absent from the reviewed target-name inventory.");
            CharacterCompanionReadResult selected = Read(package, proposal.ResourceId, proposal.Family);
            VerifyExpectedHash(selected.Resource, proposal.ExpectedSha256);
            if (!selected.IsValid) return Refuse(package, selected.Diagnostics);
            NativeCharacterRenameResult renamed = proposal.Family switch
            {
                CharacterCompanionFamily.Ragdoll when proposal.Kind == NativeCharacterReferenceKind.Bone =>
                    selected.Ragdoll!.RenameBone(proposal.OldName, proposal.NewName, proposal.ReviewedTargetNames),
                CharacterCompanionFamily.BodyElements =>
                    selected.BodyElements!.RenameReference(proposal.Kind,
                        proposal.OldName, proposal.NewName, proposal.ReviewedTargetNames),
                CharacterCompanionFamily.DamagePatches when proposal.Kind == NativeCharacterReferenceKind.Patch =>
                    selected.DamagePatches!.RenamePatch(proposal.OldName, proposal.NewName,
                        proposal.ReviewedTargetNames),
                CharacterCompanionFamily.DamagePatches when proposal.Kind == NativeCharacterReferenceKind.Helper =>
                    selected.DamagePatches!.RenameHelper(proposal.OldName, proposal.NewName,
                        proposal.ReviewedTargetNames),
                _ => throw new InvalidDataException("This name kind has no reviewed source-edit route."),
            };
            if (!renamed.IsValid || renamed.Diagnostics.Any(static diagnostic =>
                    diagnostic.Code == "character_rename_unclassified_token"))
                return Refuse(package, renamed.Diagnostics.Select(static diagnostic =>
                    diagnostic.Code == "character_rename_unclassified_token"
                        ? diagnostic with { IsError = true } : diagnostic).ToImmutableArray());
            CharacterCompanionReadResult reopened = ParseSelected(selected.Resource,
                renamed.Document.Write(), proposal.Family);
            if (!reopened.IsValid) return Refuse(package, reopened.Diagnostics);
            return CommitTextChanges(package,
                [(selected.Resource.Id, renamed.Document.Write())],
                renamed.Diagnostics.Add(new("character_name_review_pending",
                    "The source name changed; native consumers and dependent assets require renewed review.")));
        }
        catch (Exception exception) when (exception is ArgumentException or FormatException or InvalidDataException or OverflowException)
        {
            return Refuse(package, "character_name_edit_refused", exception.Message);
        }
    }

    public static CharacterCompanionAuthoringResult ApplyReferenceEdit(
        CustomModelPackage package, ReviewedCharacterReferenceEdit proposal)
    {
        ArgumentNullException.ThrowIfNull(package);
        ArgumentNullException.ThrowIfNull(proposal);
        try
        {
            if (!ScopedKindAllowed(proposal.Family, proposal.Kind))
                return Refuse(package, "character_reference_kind_unscoped",
                    "This reference kind requires a declaration-wide edit or has no reviewed local contract.");
            if (proposal.ReviewedTargetNames.IsDefaultOrEmpty ||
                !proposal.ReviewedTargetNames.Contains(proposal.NewName, StringComparer.Ordinal))
                return Refuse(package, "character_reference_target_missing",
                    "The replacement is absent from the reviewed exact target-name inventory.");
            CharacterCompanionReadResult selected = Read(package, proposal.ResourceId, proposal.Family);
            VerifyExpectedHash(selected.Resource, proposal.ExpectedSha256);
            if (!selected.IsValid) return Refuse(package, selected.Diagnostics);
            IEnumerable<NativeCharacterReference> references = proposal.Family switch
            {
                CharacterCompanionFamily.Ragdoll => selected.Ragdoll!.References,
                CharacterCompanionFamily.BodyElements => selected.BodyElements!.References,
                CharacterCompanionFamily.DamagePatches => selected.DamagePatches!.References,
                _ => [],
            };
            NativeCharacterReference[] exact = references.Where(reference =>
                reference.CallIndex == proposal.CallIndex &&
                reference.ArgumentIndex == proposal.ArgumentIndex &&
                reference.Kind == proposal.Kind &&
                string.Equals(reference.Name, proposal.ExpectedName, StringComparison.Ordinal)).ToArray();
            if (exact.Length != 1)
                return Refuse(package, "character_reference_source_changed",
                    "The selected call argument is not one exact current typed reference.");
            NativeCharacterScriptDocument changed = selected.Syntax.ReplaceQuotedArguments(
                [new(proposal.CallIndex, proposal.ArgumentIndex, proposal.ExpectedName, proposal.NewName)]);
            CharacterCompanionReadResult reopened = ParseSelected(selected.Resource,
                changed.Write(), proposal.Family);
            if (!reopened.IsValid) return Refuse(package, reopened.Diagnostics);
            return CommitTextChanges(package,
                [(selected.Resource.Id, changed.Write())],
                [new("character_scoped_reference_review_pending",
                    "One source reference changed; dependent geometry, compiler output and native behavior require renewed review.")]);
        }
        catch (Exception exception) when (exception is ArgumentException or FormatException or InvalidDataException or OverflowException)
        {
            return Refuse(package, "character_reference_edit_refused", exception.Message);
        }
    }

    public static CharacterCompanionAuthoringResult ApplyBodyMeshDisableAddition(
        CustomModelPackage package, ReviewedBodyMeshDisableAddition proposal)
    {
        ArgumentNullException.ThrowIfNull(package);
        ArgumentNullException.ThrowIfNull(proposal);
        try
        {
            if (!proposal.Reviewed || proposal.ReviewedAvailableEntityNames.IsDefaultOrEmpty ||
                !proposal.ReviewedAvailableEntityNames.Contains(proposal.EntityName, StringComparer.Ordinal))
                return Refuse(package, "body_mesh_disable_review_required", "Review one exact available mesh entity.");
            CharacterCompanionReadResult selected = Read(package, proposal.ResourceId, CharacterCompanionFamily.BodyElements);
            VerifyExpectedHash(selected.Resource, proposal.ExpectedSha256);
            if (!selected.IsValid) return Refuse(package, selected.Diagnostics);
            Dl1BodyElementsDocument body = selected.BodyElements!;
            Dl1BodyElement? element = body.Elements.SingleOrDefault(row => row.CallIndex == proposal.BodyElementCallIndex);
            if (element is null || element.ElementToken != proposal.ExpectedElementToken ||
                element.HelperName != proposal.ExpectedHelperName)
                return Refuse(package, "body_mesh_disable_region_changed", "The selected body region has changed.");
            if (body.MeshDisables.Any(row => row.BodyElementCallIndex == element.CallIndex &&
                !row.FromRelic && row.EntityName == proposal.EntityName))
                return Refuse(package, "body_mesh_disable_duplicate", "This body region already hides the selected mesh.");
            (NativeCharacterScriptDocument changed, int added) = NativeCharacterScriptCodec.InsertBodyMeshDisable(
                selected.Syntax, element.CallIndex, proposal.EntityName);
            CharacterCompanionReadResult reopened = ParseSelected(selected.Resource, changed.Write(), CharacterCompanionFamily.BodyElements);
            if (!reopened.IsValid) return Refuse(package, reopened.Diagnostics);
            Dl1BodyElementsDocument after = reopened.BodyElements!;
            Dl1BodyMeshDisable? inserted = after.MeshDisables.SingleOrDefault(row => row.CallIndex == added);
            if (inserted is null || inserted.FromRelic || inserted.EntityName != proposal.EntityName ||
                inserted.BodyElementCallIndex != element.CallIndex || !OriginalRelationshipsMatch())
                return Refuse(package, "body_mesh_disable_scope_changed", "The addition changed another body relationship.");
            return CommitTextChanges(package, [(selected.Resource.Id, changed.Write())], []);

            int Map(int index) => index < added ? index : index + 1;
            bool OriginalRelationshipsMatch()
            {
                if (body.Elements.Length != after.Elements.Length) return false;
                for (int index = 0; index < body.Elements.Length; index++)
                {
                    Dl1BodyElement beforeElement = body.Elements[index], afterElement = after.Elements[index];
                    if (Map(beforeElement.CallIndex) != afterElement.CallIndex ||
                        beforeElement.ElementToken != afterElement.ElementToken ||
                        beforeElement.HelperName != afterElement.HelperName ||
                        !beforeElement.NumericArguments.SequenceEqual(afterElement.NumericArguments)) return false;
                }
                return body.Relics.Select(row => row with
                    { CallIndex = Map(row.CallIndex), BodyElementCallIndex = Map(row.BodyElementCallIndex) }).SequenceEqual(after.Relics) &&
                    body.MeshDisables.Select(row => row with
                    { CallIndex = Map(row.CallIndex), BodyElementCallIndex = Map(row.BodyElementCallIndex) })
                        .SequenceEqual(after.MeshDisables.Where(row => row.CallIndex != added)) &&
                    body.MeatParts.Select(row => row with
                    { CallIndex = Map(row.CallIndex), MeatPartsCallIndex = Map(row.MeatPartsCallIndex) }).SequenceEqual(after.MeatParts) &&
                    body.References.Select(row => row with { CallIndex = Map(row.CallIndex) })
                        .SequenceEqual(after.References.Where(row => row.CallIndex != added));
            }
        }
        catch (Exception exception) when (exception is ArgumentException or FormatException or InvalidDataException or OverflowException)
        {
            return Refuse(package, "body_mesh_disable_addition_refused", exception.Message);
        }
    }

    public static CharacterCompanionAuthoringResult ApplyRagdollShapeEdit(
        CustomModelPackage package, ReviewedRagdollShapeEdit proposal)
    {
        ArgumentNullException.ThrowIfNull(package);
        ArgumentNullException.ThrowIfNull(proposal);
        try
        {
            CharacterCompanionReadResult selected = Read(package, proposal.ResourceId, CharacterCompanionFamily.Ragdoll);
            VerifyExpectedHash(selected.Resource, proposal.ExpectedSha256);
            if (!selected.IsValid) return Refuse(package, selected.Diagnostics);
            Dl1RagdollBoneUse[] exact = selected.Ragdoll!.Bones.Where(bone =>
                bone.CallIndex == proposal.CallIndex &&
                string.Equals(bone.ShapeToken, proposal.ExpectedShape, StringComparison.Ordinal)).ToArray();
            if (exact.Length != 1 || (uint)proposal.CallIndex >= (uint)selected.Syntax.Calls.Length ||
                selected.Syntax.Calls[proposal.CallIndex].Name is not ("UseBone" or "UseBoneScale"))
                return Refuse(package, "ragdoll_shape_source_changed",
                    "The selected call no longer has the expected physical-bone shape token.");
            if (string.IsNullOrWhiteSpace(proposal.NewShape) ||
                string.Equals(proposal.NewShape, proposal.ExpectedShape, StringComparison.Ordinal) ||
                proposal.ReviewedSourceShapeTokens.IsDefaultOrEmpty ||
                !proposal.ReviewedSourceShapeTokens.Contains(proposal.NewShape, StringComparer.Ordinal) ||
                !Dl1RagdollShapeInputCalculator.SupportedShapeTokens.Contains(proposal.NewShape, StringComparer.Ordinal))
                return Refuse(package, "ragdoll_shape_target_unreviewed",
                    "Choose a different supported shape and review it explicitly.");
            NativeCharacterScriptDocument changed = selected.Syntax.ReplaceQuotedArguments(
                [new(proposal.CallIndex, 1, proposal.ExpectedShape, proposal.NewShape)]);
            CharacterCompanionReadResult reopened = ParseSelected(selected.Resource,
                changed.Write(), CharacterCompanionFamily.Ragdoll);
            if (!reopened.IsValid) return Refuse(package, reopened.Diagnostics);
            return CommitTextChanges(package,
                [(selected.Resource.Id, changed.Write())],
                [new("ragdoll_shape_review_pending",
                    "One retained shape token changed; native dimensions, compiler output and Player physics require renewed review.")]);
        }
        catch (Exception exception) when (exception is ArgumentException or FormatException or InvalidDataException or OverflowException)
        {
            return Refuse(package, "ragdoll_shape_edit_refused", exception.Message);
        }
    }

    private static bool ScopedKindAllowed(CharacterCompanionFamily family, NativeCharacterReferenceKind kind) =>
        family switch
        {
            CharacterCompanionFamily.Ragdoll => kind is
                NativeCharacterReferenceKind.PhysicsResource or NativeCharacterReferenceKind.IncludeResource,
            CharacterCompanionFamily.BodyElements => kind is
                NativeCharacterReferenceKind.Helper or NativeCharacterReferenceKind.MeshEntity or
                NativeCharacterReferenceKind.PhysicsResource or NativeCharacterReferenceKind.EffectResource or
                NativeCharacterReferenceKind.MeshResource or NativeCharacterReferenceKind.BodyElementsResource or
                NativeCharacterReferenceKind.IncludeResource,
            CharacterCompanionFamily.DamagePatches => kind == NativeCharacterReferenceKind.Helper,
            _ => false,
        };

    private static bool NumericArgumentIsMeasured(
        NativeCharacterCall call, CharacterCompanionFamily family, int argument)
    {
        if (argument < 0 || argument >= call.Arguments.Length) return false;
        return family switch
        {
            CharacterCompanionFamily.Ragdoll => call.Name switch
            {
                "UseBone" => argument == 2,
                "UseBoneScale" => argument is 2 or 3,
                "DefineJoint" => argument is >= 3 and <= 8,
                "CollisionHelper" => argument == 2,
                "SelfCollisionGeom" => argument == 2,
                "FloatingGeom" or "GeomFrictionMul" or "FixBone" => argument == 1,
                "Set1DOFStops" or "Set3DOFStops" or "SetAnchorPosition" or
                    "SetFrictionForce" => argument > 0,
                "Mass" => argument == 0 && call.ParentCallIndex >= 0,
                "QuickStepNumIterations" => argument == 0 && call.ParentCallIndex >= 0,
                _ => false,
            },
            CharacterCompanionFamily.BodyElements => call.Name switch
            {
                "BodyElement" => argument is >= 1 and <= 4,
                "AddMeatPart" or "DestroyedHeadParts" => argument == 1,
                _ => false,
            },
            CharacterCompanionFamily.DamagePatches => call.Name == "Xform" && argument < 12,
            _ => false,
        };
    }

    private static bool RequiresInteger(
        NativeCharacterCall call, CharacterCompanionFamily family, int argument) =>
        family switch
        {
            CharacterCompanionFamily.Ragdoll => call.Name == "QuickStepNumIterations" ||
                call.Name is "UseBone" or "UseBoneScale" && argument == 2,
            CharacterCompanionFamily.BodyElements =>
                call.Name == "BodyElement" && argument is 1 or 2 ||
                call.Name is "AddMeatPart" or "DestroyedHeadParts",
            _ => false,
        };

    public static CharacterCompanionAuthoringResult ApplyResourceRename(
        CustomModelPackage package, ReviewedCharacterResourceRename proposal)
    {
        ArgumentNullException.ThrowIfNull(package);
        ArgumentNullException.ThrowIfNull(proposal);
        try
        {
            CharacterResourceInventory inventory = RequireInventory(package);
            CharacterResourceRecord target = RequireResource(package, proposal.ResourceId);
            VerifyExpectedHash(target, proposal.ExpectedSha256);
            string oldLeaf = Leaf(target.LogicalName);
            string newLeaf = proposal.NewFileName;
            if (string.IsNullOrWhiteSpace(newLeaf) || newLeaf.Length > 255 ||
                newLeaf is "." or ".." || newLeaf.Any(char.IsControl) ||
                newLeaf.IndexOfAny(['/', '\\', ':', '"']) >= 0 ||
                !Path.GetExtension(oldLeaf).Equals(Path.GetExtension(newLeaf), StringComparison.OrdinalIgnoreCase))
                return Refuse(package, "character_resource_name_invalid",
                    "Replacement must be one bounded filename with the same extension.");
            if (inventory.Resources.Any(resource => !resource.IsOriginalArchive &&
                    resource.Id != target.Id &&
                    Leaf(resource.LogicalName).Equals(newLeaf, StringComparison.OrdinalIgnoreCase)))
                return Refuse(package, "character_resource_name_conflict",
                    $"Another original resource already uses '{newLeaf}'.");
            if (oldLeaf.Equals(newLeaf, StringComparison.Ordinal))
                return Refuse(package, "character_resource_name_unchanged",
                    "The replacement name is unchanged.");
            var changedText = new List<(string ResourceId, string Text)>();
            var diagnostics = ImmutableArray.CreateBuilder<NativeCharacterDiagnostic>();
            foreach (CharacterResourceRecord source in inventory.Resources.Where(static record =>
                         !record.IsOriginalArchive && record.EntryPath is not null &&
                         IsTextCompanion(record.LogicalName)))
            {
                ImmutableArray<byte> payload = RequirePayload(package, source);
                string text = DecodeText(payload, out _);
                if (!text.Contains(oldLeaf, StringComparison.OrdinalIgnoreCase)) continue;
                NativeCharacterScriptDocument syntax = NativeCharacterScriptCodec.Parse(text);
                ImmutableArray<NativeCharacterReference> references = CollectKnownReferences(source, text, syntax);
                NativeCharacterReferenceKind[] allowedKinds = KindsForExtension(Path.GetExtension(oldLeaf));
                NativeCharacterReference[] selected = references
                    .Where(reference => allowedKinds.Contains(reference.Kind) &&
                        Leaf(reference.Name).Equals(oldLeaf, StringComparison.OrdinalIgnoreCase))
                    .GroupBy(static reference => (reference.CallIndex, reference.ArgumentIndex))
                    .Select(static group => group.First())
                    .ToArray();
                var selectedPositions = selected.Select(static reference =>
                    (reference.CallIndex, reference.ArgumentIndex)).ToHashSet();
                for (int callIndex = 0; callIndex < syntax.Calls.Length; callIndex++)
                foreach (NativeCharacterQuotedArgument token in syntax.Calls[callIndex].QuotedArguments)
                {
                    if (Leaf(token.Value).Equals(oldLeaf, StringComparison.OrdinalIgnoreCase) &&
                        !selectedPositions.Contains((callIndex, token.ArgumentIndex)))
                        diagnostics.Add(new("character_resource_reference_unclassified",
                            $"Resource '{source.LogicalName}' has an unclassified old-name token in call {callIndex}.", true));
                }
                if (diagnostics.Any(static diagnostic => diagnostic.IsError))
                    return Refuse(package, diagnostics.ToImmutable());
                if (selected.Length == 0) continue;
                NativeCharacterScriptDocument changed = syntax.ReplaceQuotedArguments(
                    selected.Select(reference => new NativeCharacterTokenReplacement(
                        reference.CallIndex, reference.ArgumentIndex, reference.Name,
                        reference.Name[..^oldLeaf.Length] + newLeaf)));
                changedText.Add((source.Id, changed.Write()));
            }
            string normalized = target.LogicalName.Replace('\\', '/');
            int slash = normalized.LastIndexOf('/');
            string newLogicalName = normalized[..(slash + 1)] + newLeaf;
            diagnostics.Add(new("character_resource_rename_review_pending",
                "Resource identity and recognized references changed; opaque consumers and native behavior require renewed review."));
            return CommitTextChanges(package, changedText, diagnostics.ToImmutable(),
                (target.Id, newLogicalName));
        }
        catch (Exception exception) when (exception is ArgumentException or FormatException or InvalidDataException or OverflowException)
        {
            return Refuse(package, "character_resource_rename_refused", exception.Message);
        }
    }

    private static ImmutableArray<NativeCharacterReference> CollectKnownReferences(
        CharacterResourceRecord resource, string text, NativeCharacterScriptDocument syntax)
    {
        string extension = Path.GetExtension(resource.LogicalName).ToLowerInvariant();
        var references = ImmutableArray.CreateBuilder<NativeCharacterReference>();
        if (extension == ".phx")
        {
            Dl1RagdollDocument read = Dl1RagdollCodec.Read(text);
            if (!read.IsValid) throw new InvalidDataException($"Ragdoll script '{resource.LogicalName}' is malformed.");
            references.AddRange(read.References);
        }
        if (extension is ".bel" or ".scr")
        {
            Dl1BodyElementsDocument body = Dl1BodyElementsCodec.Read(text);
            if (!body.IsValid) throw new InvalidDataException($"Body-element script '{resource.LogicalName}' is malformed.");
            references.AddRange(body.References);
        }
        if (extension == ".scr")
        {
            Dl1DamagePatchDocument damage = Dl1DamagePatchCodec.Read(text);
            if (!damage.IsValid) throw new InvalidDataException($"Damage script '{resource.LogicalName}' is malformed.");
            references.AddRange(damage.References);
        }
        for (int index = 0; index < syntax.Calls.Length; index++)
        {
            NativeCharacterCall call = syntax.Calls[index];
            if (call.Arguments.Length == 0 || call.QuotedArguments.All(static token => token.ArgumentIndex != 0))
                continue;
            NativeCharacterReferenceKind? kind = call.Name switch
            {
                "!include" => NativeCharacterReferenceKind.IncludeResource,
                "MeshPartCloth" when extension == ".mpcloth" => NativeCharacterReferenceKind.PhysicsResource,
                "AnimScriptAlias" => NativeCharacterReferenceKind.IncludeResource,
                _ => null,
            };
            if (kind is { } value)
                references.Add(new(value, NativeCharacterScriptCodec.Quoted(call.Arguments[0]), index, 0));
        }
        return references.ToImmutable();
    }

    private static NativeCharacterReferenceKind[] KindsForExtension(string extension) =>
        extension.ToLowerInvariant() switch
        {
            ".phx" => [NativeCharacterReferenceKind.PhysicsResource,
                NativeCharacterReferenceKind.IncludeResource],
            ".bel" => [NativeCharacterReferenceKind.BodyElementsResource,
                NativeCharacterReferenceKind.IncludeResource],
            ".fx" => [NativeCharacterReferenceKind.EffectResource,
                NativeCharacterReferenceKind.IncludeResource],
            ".msh" => [NativeCharacterReferenceKind.MeshResource,
                NativeCharacterReferenceKind.IncludeResource],
            ".scr" or ".def" or ".ascr" or ".bscr" =>
                [NativeCharacterReferenceKind.IncludeResource],
            _ => [],
        };

    private static bool IsTextCompanion(string name) =>
        Path.GetExtension(name).ToLowerInvariant() is
            ".phx" or ".bel" or ".scr" or ".def" or ".mpcloth" or ".ascr" or ".bscr" or ".fx";

    private static CharacterCompanionAuthoringResult CommitTextChanges(
        CustomModelPackage package,
        IEnumerable<(string ResourceId, string Text)> changes,
        ImmutableArray<NativeCharacterDiagnostic> diagnostics,
        (string ResourceId, string NewLogicalName)? renamedResource = null)
    {
        CharacterResourceInventory inventory = RequireInventory(package);
        var records = inventory.Resources.ToArray();
        ImmutableDictionary<string, ImmutableArray<byte>> payloads = package.CompanionPayloads;
        var changedTexts = changes.ToDictionary(change => change.ResourceId,
            change => change.Text, StringComparer.Ordinal);
        var changedIds = new HashSet<string>(StringComparer.Ordinal);
        var affectedSubsystems = new HashSet<CharacterSubsystem>();
        foreach ((string resourceId, string text) in changedTexts)
        {
            int index = Array.FindIndex(records, record => record.Id == resourceId);
            if (index < 0) throw new InvalidDataException($"Resource '{resourceId}' left the package inventory.");
            CharacterResourceRecord record = records[index];
            ImmutableArray<byte> original = RequirePayload(package, record);
            _ = DecodeText(original, out bool hasBom);
            byte[] bytes = EncodeText(text, hasBom);
            if (original.AsSpan().SequenceEqual(bytes)) continue;
            changedIds.Add(resourceId);
            affectedSubsystems.Add(record.Subsystem);
        }
        if (renamedResource is { } rename)
        {
            int index = Array.FindIndex(records, record => record.Id == rename.ResourceId);
            if (index < 0) throw new InvalidDataException($"Resource '{rename.ResourceId}' left the package inventory.");
            CharacterResourceRecord record = records[index];
            changedIds.Add(rename.ResourceId);
            affectedSubsystems.Add(record.Subsystem);
        }
        if (changedIds.Count == 0)
            return Refuse(package, "character_edit_unchanged", "The reviewed edit did not change source bytes or identity.");
        var archives = new List<CharacterResourceRecord>();
        foreach (string resourceId in changedIds.Order(StringComparer.Ordinal))
        {
            int index = Array.FindIndex(records, record => record.Id == resourceId);
            CharacterResourceRecord record = records[index];
            ImmutableArray<byte> previous = RequirePayload(package, record);
            byte[] activeBytes;
            if (changedTexts.TryGetValue(resourceId, out string? newText))
            {
                _ = DecodeText(previous, out bool hasBom);
                activeBytes = EncodeText(newText, hasBom);
            }
            else activeBytes = previous.ToArray();
            string archiveId = "original:" + resourceId;
            CharacterResourceRecord? existingArchive = records.SingleOrDefault(row => row.Id == archiveId);
            if (existingArchive is null)
            {
                string archivePath = ChooseEntryPath("original", record.ContentSha256!,
                    resourceId, records, archives, payloads, null);
                archives.Add(record with
                {
                    Id = archiveId,
                    EntryPath = archivePath,
                    Required = false,
                    IsOriginalArchive = true,
                    Status = CharacterDependencyStatus.Preserved,
                    Detail = "Immutable original bytes before reviewed companion editing.",
                });
                payloads = payloads.Add(archivePath, previous);
            }
            else if (!existingArchive.IsOriginalArchive || existingArchive.EntryPath is null ||
                     !package.CompanionPayloads.TryGetValue(existingArchive.EntryPath,
                         out ImmutableArray<byte> archiveBytes) ||
                     archiveBytes.IsDefault || archiveBytes.Length != existingArchive.ByteLength ||
                     !Convert.ToHexStringLower(SHA256.HashData(archiveBytes.AsSpan()))
                         .Equals(existingArchive.ContentSha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The original-custody resource is missing or has changed.");
            string editedHash = Convert.ToHexStringLower(SHA256.HashData(activeBytes));
            string editedPath = ChooseEntryPath("edited", editedHash,
                resourceId, records, archives, payloads, record.EntryPath);
            payloads = payloads.Remove(record.EntryPath!).SetItem(editedPath,
                ImmutableArray.Create(activeBytes));
            records[index] = record with
            {
                LogicalName = renamedResource is { } renamed && renamed.ResourceId == resourceId
                    ? renamed.NewLogicalName : record.LogicalName,
                EntryPath = editedPath,
                ContentSha256 = editedHash,
                SupplementalSource = null,
                PackedEffect = null,
                Material = null,
                NativeResource = null,
                ByteLength = activeBytes.Length,
                Status = CharacterDependencyStatus.Ambiguous,
                Detail = "Reviewed source edit retained; compiler and native consumers require renewed validation.",
            };
        }
        ImmutableArray<CharacterSubsystemReview> reviews = inventory.Subsystems.Select(review =>
            affectedSubsystems.Contains(review.Subsystem)
                ? review with
                {
                    Status = CharacterDependencyStatus.Ambiguous,
                    Detail = "Source changed; compiler readback and native behavior are pending.",
                }
                : review).ToImmutableArray();
        CharacterResourceInventory updatedInventory = inventory with
        {
            Resources = [.. records, .. archives],
            ActorSourceReviews = inventory.ActorSourceReviews.Where(r => !changedIds.Contains(r.ActorResourceId)).ToImmutableArray(),
            BodyRegionReviews = inventory.BodyRegionReviews.Select(review => changedIds.Contains(review.BodyResourceId) || review.DetachedAssets.Concat(review.PhysicsAssets).Concat(review.EffectAssets).Any(asset => changedIds.Contains(asset.ResourceId)) ? review with { Accepted = false } : review).ToImmutableArray(),
            Subsystems = reviews,
            CompiledSemanticSha256 = null,
            LoadedResourceSha256 = null,
            VerifiedPlayerScenarios = [],
        };
        updatedInventory.Validate();
        CustomModelPackage updated = package with
        {
            Document = package.Document with
            {
                CharacterResources = updatedInventory,
                LastBuildReceipt = null,
            },
            CompanionPayloads = payloads,
        };
        return new(updated, [.. changedIds.Order(StringComparer.Ordinal)], diagnostics);
    }

    private static CharacterResourceInventory RequireInventory(CustomModelPackage package)
    {
        ArgumentNullException.ThrowIfNull(package);
        CharacterResourceInventory inventory = package.Document.CharacterResources ??
            throw new InvalidDataException("The model package has no stock character resource inventory.");
        inventory.Validate();
        return inventory;
    }

    private static CharacterResourceRecord RequireResource(CustomModelPackage package, string resourceId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(resourceId);
        CharacterResourceRecord? record = RequireInventory(package).Resources
            .SingleOrDefault(resource => resource.Id == resourceId);
        if (record?.EntryPath is null || record.IsOriginalArchive)
            throw new InvalidDataException($"Source resource '{resourceId}' has no preserved payload.");
        return record;
    }

    private static ImmutableArray<byte> RequirePayload(CustomModelPackage package, CharacterResourceRecord record)
    {
        if (record.EntryPath is null ||
            !package.CompanionPayloads.TryGetValue(record.EntryPath, out ImmutableArray<byte> payload) ||
            payload.IsDefault || payload.Length != record.ByteLength ||
            !Convert.ToHexStringLower(SHA256.HashData(payload.AsSpan()))
                .Equals(record.ContentSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"Preserved resource '{record.LogicalName}' differs from its inventory fingerprint.");
        return payload;
    }

    private static void VerifyExpectedHash(CharacterResourceRecord record, string expected)
    {
        if (string.IsNullOrWhiteSpace(expected) ||
            !string.Equals(record.ContentSha256, expected, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The reviewed source fingerprint is stale.");
    }

    private static string DecodeText(ImmutableArray<byte> payload, out bool hasBom)
    {
        ReadOnlySpan<byte> bytes = payload.AsSpan();
        hasBom = bytes.Length >= 3 && bytes[0] == 0xEF &&
            bytes[1] == 0xBB && bytes[2] == 0xBF;
        if (hasBom) bytes = bytes[3..];
        if (bytes.Length > NativeCharacterScriptCodec.MaximumCharacters)
            throw new InvalidDataException("The preserved source exceeds the character-script bound.");
        try { return StrictUtf8.GetString(bytes); }
        catch (DecoderFallbackException exception)
        { throw new InvalidDataException("The preserved source is not lossless UTF-8.", exception); }
    }

    private static byte[] EncodeText(string text, bool hasBom)
    {
        byte[] encoded = StrictUtf8.GetBytes(text);
        if (!hasBom) return encoded;
        byte[] result = new byte[encoded.Length + 3];
        result[0] = 0xEF; result[1] = 0xBB; result[2] = 0xBF;
        encoded.CopyTo(result, 3);
        return result;
    }

    private static string Leaf(string name) => Path.GetFileName(name.Replace('\\', '/'));

    private static string ChooseEntryPath(
        string prefix, string contentSha256, string resourceId,
        IEnumerable<CharacterResourceRecord> records,
        IEnumerable<CharacterResourceRecord> pendingArchives,
        ImmutableDictionary<string, ImmutableArray<byte>> payloads,
        string? ownCurrentPath)
    {
        string hash = contentSha256.ToLowerInvariant();
        string path = $"character/resources/{prefix}-{hash}.bin";
        if (!Conflicts(path)) return path;
        string idHash = Convert.ToHexStringLower(
            SHA256.HashData(Encoding.UTF8.GetBytes(resourceId)))[..12];
        path = $"character/resources/{prefix}-{hash}-{idHash}.bin";
        if (!Conflicts(path)) return path;
        throw new InvalidDataException($"{prefix} resource entry path conflicts with another resource.");

        bool Conflicts(string candidate) => candidate != ownCurrentPath &&
            (records.Any(record => record.EntryPath == candidate) ||
             pendingArchives.Any(record => record.EntryPath == candidate) ||
             payloads.ContainsKey(candidate));
    }

    private static CharacterCompanionAuthoringResult Refuse(
        CustomModelPackage package, string code, string message) =>
        new(package, [], [new(code, message, true)]);

    private static CharacterCompanionAuthoringResult Refuse(
        CustomModelPackage package, ImmutableArray<NativeCharacterDiagnostic> diagnostics) =>
        new(package, [], diagnostics.IsDefaultOrEmpty
            ? [new("character_edit_refused", "The reviewed source edit did not pass validation.", true)]
            : diagnostics);
}
