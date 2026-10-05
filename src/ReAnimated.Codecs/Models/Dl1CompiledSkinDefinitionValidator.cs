using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text.Json;
using ReAnimated.Codecs.CompactMesh;

namespace ReAnimated.Codecs.Models;

public sealed record Dl1CompiledSkinMaterialEvidence(string TargetSlotName, string ReplacementDatabaseName);
public sealed record Dl1CompiledSkinEntityEvidence(string EntityName, bool Hidden, bool Flag4000);

public sealed record Dl1CompiledSkinRowEvidence(
    string SkinName,
    ushort RawFeatures,
    ImmutableArray<Dl1CompiledSkinMaterialEvidence> MaterialReplacements,
    ImmutableArray<Dl1CompiledSkinEntityEvidence> EntityOverrides,
    int SurfaceOverrideCount,
    int RandomizedChildCount)
{
    public int VerifiedMaterialSlotCount { get; init; }
    public ImmutableArray<byte> Colors {get;init;}=[];
    public ImmutableArray<byte> Tags {get;init;}=[];
    public ImmutableArray<CompiledMeshSkinSurfaceOverride> Surfaces {get;init;}=[];
}

/// <summary>Serializable semantic read-back from one exact compiled model.</summary>
public sealed record Dl1CompiledSkinValidationEvidence(
    string ContractFingerprint,
    int VerifiedSkinCount,
    ImmutableArray<Dl1CompiledSkinRowEvidence> Skins);

/// <summary>
/// Compares complete skin records after joining material slots, database entries,
/// and hierarchy entities by names. Numeric indexes may change at compile time.
/// Compiled RawFeatures is compared bit-for-bit as opaque evidence; the source
/// parser's feature bits are a separate encoding until compiler mapping is proven.
/// </summary>
public static class Dl1CompiledSkinDefinitionValidator
{
    private static readonly StringComparer Names = StringComparer.OrdinalIgnoreCase;

    public static Dl1CompiledSkinValidationEvidence Validate(
        ImmutableArray<Dl1SkinGenerationDefinition> expected,
        CompactMeshDocument hierarchy,
        CompiledMeshGeometryDocument compiled)
    {
        ArgumentNullException.ThrowIfNull(hierarchy);
        ArgumentNullException.ThrowIfNull(compiled);
        if (expected.IsDefaultOrEmpty || expected.Length > 1024)
            throw new InvalidDataException("Expected skin definitions are absent or exceed the bounded count.");
        if (!hierarchy.IsStructurallyValid || hierarchy.Entities.Count == 0 ||
            compiled.Diagnostics.Any(static diagnostic => diagnostic.Severity == CompactMeshDiagnosticSeverity.Error))
            throw new InvalidDataException("Compiled mesh or hierarchy has invalid source data.");

        Dictionary<string, Dl1SkinGenerationDefinition> expectedByName = UniqueByName(
            expected, static skin => skin.Name, "expected skin");
        Dictionary<string, CompiledMeshSkinDefinition> actualByName = UniqueByName(
            compiled.SkinDefinitions, static skin => skin.Name, "compiled skin");
        if (expectedByName.Count != actualByName.Count ||
            expectedByName.Keys.Except(actualByName.Keys, Names).Any() ||
            actualByName.Keys.Except(expectedByName.Keys, Names).Any())
            throw new InvalidDataException("Compiled skin names differ from expected names.");
        if (compiled.VariantNames.Count != actualByName.Count ||
            compiled.VariantNames.Distinct(Names).Count() != compiled.VariantNames.Count ||
            compiled.VariantNames.Except(actualByName.Keys, Names).Any())
            throw new InvalidDataException("Compiled variant-name inventory differs from exact skin rows.");

        CompactMeshEntity[] entities = hierarchy.Entities.ToArray();
        if (entities.Select((entity, index) => entity.Index == index).Any(static valid => !valid))
            throw new InvalidDataException("Compiled hierarchy indexes are not unique and contiguous.");
        _ = UniqueByName(entities, static entity => entity.Name, "compiled hierarchy entity");

        CompiledMaterialDatabase database = compiled.MaterialDatabase;
        if (database.DeclaredSlotCount < 0 || database.DeclaredEntryCount < database.DeclaredSlotCount ||
            database.Entries.Count != database.DeclaredEntryCount ||
            database.Entries.Select((entry, index) => entry.Index == index).Any(static valid => !valid))
            throw new InvalidDataException("Compiled material database indexes or counts are inconsistent.");
        CompiledMaterialDatabaseEntry[] materialEntries = database.Entries.ToArray();
        foreach (var group in materialEntries.GroupBy(entry => entry.DatabaseName, Names))
        {
            RequireName(group.Key, "compiled material database entry");
            if (group.Select(entry => entry.DatabaseName).Distinct(StringComparer.Ordinal).Count() != 1 ||
                group.Select(entry => entry.RawLoadValue).Distinct().Count() != group.Count())
                throw new InvalidDataException("Compiled material instances have ambiguous names or load values.");
        }

        var rows = ImmutableArray.CreateBuilder<Dl1CompiledSkinRowEvidence>(expected.Length);
        foreach (string name in expectedByName.Keys.Order(Names))
        {
            Dl1SkinGenerationDefinition source = expectedByName[name];
            CompiledMeshSkinDefinition actual = actualByName[name];
            if (actual.Index < 0 || actual.Index >= actualByName.Count ||
                source.RawFeatures != actual.RawFeatures)
                throw new InvalidDataException($"Skin '{name}' has a changed compiled raw feature word.");
            if (source.SurfaceOverrideCount != actual.SurfaceOverrideCount ||
                source.RandomizedChildCount != actual.RandomizedChildCount ||
                source.RandomizedChildCount != 0 || source.VerifiedSurfaceReplacements.Length!=source.SurfaceOverrideCount)
                throw new InvalidDataException($"Skin '{name}' has missing, extra, or opaque surface/randomized records.");

            if(!source.ExpectedCompiledColors.IsEmpty && !source.ExpectedCompiledColors.SequenceEqual(actual.ColorBytes) ||
                !source.ExpectedTagBytes.IsEmpty && !source.ExpectedTagBytes.SequenceEqual(actual.TagBytes) ||
                source.OptionalStringsAndGroupsVerifiedAbsent && (!string.IsNullOrEmpty(actual.MorphsPreset) || !string.IsNullOrEmpty(actual.Character0) || !string.IsNullOrEmpty(actual.Character1)))
                throw new InvalidDataException($"Skin '{name}' color, tag or optional string data differs.");
            var expectedSurfaces=source.VerifiedSurfaceReplacements.Select(s=>new CompiledMeshSkinSurfaceOverride(s.ExpectedOriginalId,s.ExpectedReplacementId,s.ExpectedFlags)).ToArray();
            if(!expectedSurfaces.SequenceEqual(actual.SurfaceOverrides))throw new InvalidDataException($"Skin '{name}' surface override rows differ.");

            ImmutableArray<Dl1SkinMaterialReplacement> suppliedMaterials = source.MaterialReplacements.IsDefault
                ? [] : source.MaterialReplacements;
            var expectedMaterials = new Dictionary<string, string>(Names);
            foreach (Dl1SkinMaterialReplacement replacement in suppliedMaterials)
            {
                RequireName(replacement.OriginalMaterial, "expected material slot");
                RequireName(replacement.ReplacementMaterial, "expected replacement material");
                if (!expectedMaterials.TryAdd(replacement.OriginalMaterial, replacement.ReplacementMaterial))
                    throw new InvalidDataException($"Skin '{name}' repeats a material slot.");
            }
            var requiredTargets = materialEntries.Take(database.DeclaredSlotCount)
                .Where(entry => expectedMaterials.ContainsKey(entry.DatabaseName)).Select(entry => entry.Index).ToHashSet();
            var visitedTargets = new HashSet<int>();
            var actualMaterials = new Dictionary<string, string>(Names);
            foreach (CompiledMeshSkinMaterialOverride replacement in actual.MaterialOverrides)
            {
                if ((uint)replacement.TargetMaterialSlotIndex >= (uint)database.DeclaredSlotCount ||
                    (uint)replacement.ReplacementMaterialDatabaseEntryIndex >= (uint)materialEntries.Length)
                    throw new InvalidDataException($"Skin '{name}' references a material outside the compiled database.");
                string target = materialEntries[replacement.TargetMaterialSlotIndex].DatabaseName;
                string next = materialEntries[replacement.ReplacementMaterialDatabaseEntryIndex].DatabaseName;
                if (!requiredTargets.Contains(replacement.TargetMaterialSlotIndex) ||
                    !visitedTargets.Add(replacement.TargetMaterialSlotIndex))
                    throw new InvalidDataException($"Skin '{name}' repeats or adds an unexpected compiled material slot.");
                if (Names.Equals(target, next) &&
                    replacement.TargetMaterialSlotIndex != replacement.ReplacementMaterialDatabaseEntryIndex)
                    throw new InvalidDataException($"Skin '{name}' redirects a material instance to a different self slot.");
                if (materialEntries.Count(entry => Names.Equals(entry.DatabaseName, target)) > 1 &&
                    materialEntries[replacement.TargetMaterialSlotIndex].RawLoadValue !=
                    materialEntries[replacement.ReplacementMaterialDatabaseEntryIndex].RawLoadValue)
                    throw new InvalidDataException($"Skin '{name}' changes an aliased material load value.");
                if (actualMaterials.TryGetValue(target, out string? prior))
                {
                    if (!Names.Equals(prior, next))
                        throw new InvalidDataException($"Skin '{name}' gives material instances different replacements.");
                }
                else actualMaterials.Add(target, next);
            }
            if (!requiredTargets.SetEquals(visitedTargets))
                throw new InvalidDataException($"Skin '{name}' omits a compiled material instance.");
            ComparePairs(expectedMaterials, actualMaterials, $"Skin '{name}' material replacement");

            ImmutableArray<Dl1SkinEntityVisibility> suppliedEntities = source.EntityVisibility.IsDefault
                ? [] : source.EntityVisibility;
            if (suppliedEntities.Length != actual.EntityOverrides.Count)
                throw new InvalidDataException($"Skin '{name}' entity override count differs.");
            var expectedEntities = new Dictionary<string, ushort>(Names);
            foreach (Dl1SkinEntityVisibility visibility in suppliedEntities)
            {
                RequireName(visibility.EntityName, "expected entity");
                if (visibility.OriginalRawValue is not { } raw ||
                    ((raw & 0x8000) != 0) != visibility.Hidden ||
                    !expectedEntities.TryAdd(visibility.EntityName, (ushort)(raw & 0xC000)))
                    throw new InvalidDataException($"Skin '{name}' has missing or inconsistent original entity flags.");
            }
            var actualEntities = new Dictionary<string, ushort>(Names);
            foreach (CompiledMeshSkinEntityOverride visibility in actual.EntityOverrides)
            {
                if ((uint)visibility.EntityIndex >= (uint)entities.Length ||
                    (visibility.RawValue & 0x3FFF) != visibility.EntityIndex ||
                    !actualEntities.TryAdd(entities[visibility.EntityIndex].Name,
                        (ushort)(visibility.RawValue & 0xC000)))
                    throw new InvalidDataException($"Skin '{name}' has duplicate or inconsistent compiled entity flags.");
            }
            if (expectedEntities.Count != actualEntities.Count ||
                expectedEntities.Any(pair => !actualEntities.TryGetValue(pair.Key, out ushort raw) || raw != pair.Value))
                throw new InvalidDataException($"Skin '{name}' entity names or high flags differ.");

            rows.Add(new(Normalize(name), actual.RawFeatures,
                actualMaterials.OrderBy(static pair => pair.Key, Names)
                    .Select(static pair => new Dl1CompiledSkinMaterialEvidence(Normalize(pair.Key), Normalize(pair.Value)))
                    .ToImmutableArray(),
                actualEntities.OrderBy(static pair => pair.Key, Names)
                    .Select(static pair => new Dl1CompiledSkinEntityEvidence(Normalize(pair.Key),
                        (pair.Value & 0x8000) != 0, (pair.Value & 0x4000) != 0))
                    .ToImmutableArray(), actual.SurfaceOverrideCount, actual.RandomizedChildCount) {VerifiedMaterialSlotCount=visitedTargets.Count,Colors=actual.ColorBytes.ToImmutableArray(),Tags=actual.TagBytes.ToImmutableArray(),Surfaces=actual.SurfaceOverrides.ToImmutableArray()});
        }
        if (compiled.SkinDefinitions.Select(static skin => skin.Index).Distinct().Count() != compiled.SkinDefinitions.Count)
            throw new InvalidDataException("Compiled skin row indexes are duplicate or missing.");
        ImmutableArray<Dl1CompiledSkinRowEvidence> evidence = rows.ToImmutable();
        byte[] canonical = JsonSerializer.SerializeToUtf8Bytes(new { Schema = 1, Skins = evidence });
        string fingerprint = Convert.ToHexStringLower(SHA256.HashData(canonical));
        return new(fingerprint, evidence.Length, evidence);
    }

    private static Dictionary<string, T> UniqueByName<T>(IEnumerable<T> values,
        Func<T, string> name, string label)
    {
        var result = new Dictionary<string, T>(Names);
        foreach (T value in values)
        {
            string key = name(value);
            RequireName(key, label);
            if (!result.TryAdd(key, value))
                throw new InvalidDataException($"{label} names are duplicate or ambiguous.");
        }
        return result;
    }

    private static void ComparePairs(Dictionary<string, string> expected,
        Dictionary<string, string> actual, string label)
    {
        if (expected.Count != actual.Count || expected.Any(pair =>
                !actual.TryGetValue(pair.Key, out string? value) || !Names.Equals(pair.Value, value)))
            throw new InvalidDataException(label + " names differ.");
    }

    private static void RequireName(string name, string label)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new InvalidDataException(label + " name is empty.");
    }

    private static string Normalize(string name) => name.ToLowerInvariant();
}
