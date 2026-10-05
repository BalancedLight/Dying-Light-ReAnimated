using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text.Json;
using ReAnimated.Codecs.CompactMesh;

namespace ReAnimated.Codecs.Models;

public sealed record Dl1CompiledChrObjectIdentityReadBack(
    int SourceObjectIndex,
    string SourceObjectName,
    string CompiledObjectName,
    int CompiledEntityIndex);

public sealed record Dl1CompiledChrEntityOverrideIdentityReadBack(
    int CompiledEntityIndex,
    string CompiledObjectName,
    ushort RawValue);

public sealed record Dl1CompiledChrVariantIdentityReadBack(
    int CompiledSkinIndex,
    string SourceVariantName,
    string CompiledVariantName,
    ushort RawFeatures,
    int SurfaceOverrideCount,
    int RandomizedChildCount,
    ImmutableArray<Dl1CompiledChrEntityOverrideIdentityReadBack> EntityOverrides);

public sealed record Dl1CompiledChrIdentityReadBackEvidence(
    string ContractFingerprint,
    bool ObjectOrderMatches,
    ImmutableArray<Dl1CompiledChrObjectIdentityReadBack> Objects,
    ImmutableArray<Dl1CompiledChrVariantIdentityReadBack> Variants);

/// <summary>
/// Verifies CHR object identity and the source-writer's object-order contract,
/// then joins compiled skin variants by name. This proves the current generated
/// CHR-to-compact table binding order; it does not compare or validate transforms.
/// Source skin names may be supplied separately because CHR transform variants and
/// .skn material/visibility skins are different systems. Variant feature bits,
/// material substitutions, entity flag bits, and opaque
/// overrides are preserved as evidence without interpretation.
/// </summary>
public static class Dl1CompiledChrIdentityValidator
{
    private static readonly StringComparer IdentityComparer = StringComparer.OrdinalIgnoreCase;
    private static readonly JsonSerializerOptions FingerprintJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public static Dl1CompiledChrIdentityReadBackEvidence Validate(
        Dl1ChrV4Document sourceChr,
        CompactMeshDocument compiledHierarchy,
        CompiledMeshGeometryDocument compiledGeometry,
        IEnumerable<string>? sourceSkinNames = null)
    {
        ArgumentNullException.ThrowIfNull(sourceChr);
        ArgumentNullException.ThrowIfNull(compiledHierarchy);
        ArgumentNullException.ThrowIfNull(compiledGeometry);
        _ = Dl1ChrV4Codec.Build(sourceChr);
        if (!compiledHierarchy.IsStructurallyValid || compiledHierarchy.Entities.Count == 0)
            throw new InvalidDataException("CHR identity read-back requires a valid compiled compact hierarchy.");

        var sourceNames = new HashSet<string>(IdentityComparer);
        foreach (string name in sourceChr.ObjectNames)
        {
            if (string.IsNullOrWhiteSpace(name) || !sourceNames.Add(name))
                throw new InvalidDataException("Source CHR object identities are empty or ambiguous.");
        }

        var compiledByName = new Dictionary<string, CompactMeshEntity>(IdentityComparer);
        var compiledIndexes = new HashSet<int>();
        for (int entityPosition = 0; entityPosition < compiledHierarchy.Entities.Count; entityPosition++)
        {
            CompactMeshEntity entity = compiledHierarchy.Entities[entityPosition];
            if (entity.Index != entityPosition || !compiledIndexes.Add(entity.Index))
                throw new InvalidDataException("Compiled entity indexes are not unique and contiguous for compact object-table lookup.");

            if (string.IsNullOrWhiteSpace(entity.Name) || !compiledByName.TryAdd(entity.Name, entity))
            {
                throw new InvalidDataException("Compiled object identities are empty or ambiguous for CHR read-back.");
            }
        }

        if (sourceNames.Count != compiledByName.Count ||
            sourceNames.Except(compiledByName.Keys, IdentityComparer).Any() ||
            compiledByName.Keys.Except(sourceNames, IdentityComparer).Any())
        {
            string[] missing = sourceNames.Except(compiledByName.Keys, IdentityComparer).Order(IdentityComparer).ToArray();
            string[] extra = compiledByName.Keys.Except(sourceNames, IdentityComparer).Order(IdentityComparer).ToArray();
            throw new InvalidDataException(
                $"Compiled CHR object identity inventory differs from compact output (missing [{string.Join(", ", missing.Take(8))}], " +
                $"extra [{string.Join(", ", extra.Take(8))}]). No model RPack was published.");
        }

        bool objectOrderMatches = sourceChr.ObjectNames
            .Select((name, index) => IdentityComparer.Equals(name, compiledHierarchy.Entities[index].Name))
            .All(static matches => matches);
        if (!objectOrderMatches)
            throw new InvalidDataException("Compiled object order differs from the source CHR order required by the generated CHR contract. No model RPack was published.");

        // An empty definition array means the item-18 skin table was absent or
        // not decoded exactly; printable-string fallback names are insufficient
        // evidence for this identity comparison.
        if (compiledGeometry.SkinDefinitions.Count == 0)
            throw new InvalidDataException("The compiled mesh has no exact decoded skin-definition table for CHR variant comparison.");

        string[] sourceVariantNames = (sourceSkinNames ?? sourceChr.Variants.Select(static variant => variant.Name)).ToArray();
        string[] compiledVariantNames = compiledGeometry.SkinDefinitions.Select(static skin => skin.Name).ToArray();
        var sourceVariants = new HashSet<string>(IdentityComparer);
        foreach (string variant in sourceVariantNames)
        {
            if (!sourceVariants.Add(variant))
                throw new InvalidDataException("Source CHR variant identities are ambiguous.");
        }

        var compiledVariantSet = new HashSet<string>(IdentityComparer);
        foreach (string name in compiledVariantNames)
        {
            if (string.IsNullOrWhiteSpace(name) || !compiledVariantSet.Add(name))
                throw new InvalidDataException("Compiled skin variant identities are empty or ambiguous.");
        }

        var decodedNameSet = new HashSet<string>(compiledGeometry.VariantNames, IdentityComparer);
        if (compiledGeometry.VariantNames.Count != compiledVariantNames.Length ||
            !decodedNameSet.SetEquals(compiledVariantSet) ||
            sourceVariants.Count != compiledVariantSet.Count ||
            sourceVariants.Except(compiledVariantSet, IdentityComparer).Any() ||
            compiledVariantSet.Except(sourceVariants, IdentityComparer).Any())
        {
            throw new InvalidDataException(
                $"Compiled skin variant identities differ from source CHR (source [{string.Join(", ", sourceVariantNames)}], " +
                $"compiled [{string.Join(", ", compiledVariantNames)}]). No model RPack was published.");
        }

        var compiledSkinIndexes = new HashSet<int>();
        var variantRows = ImmutableArray.CreateBuilder<Dl1CompiledChrVariantIdentityReadBack>(
            compiledGeometry.SkinDefinitions.Count);
        foreach (CompiledMeshSkinDefinition skin in compiledGeometry.SkinDefinitions)
        {
            if ((uint)skin.Index >= (uint)compiledGeometry.SkinDefinitions.Count ||
                !compiledSkinIndexes.Add(skin.Index) ||
                !sourceVariants.TryGetValue(skin.Name, out string? sourceVariant))
            {
                throw new InvalidDataException("A compiled skin row cannot be uniquely joined to a source CHR variant.");
            }

            var entityRows = ImmutableArray.CreateBuilder<Dl1CompiledChrEntityOverrideIdentityReadBack>(
                skin.EntityOverrides.Count);
            foreach (CompiledMeshSkinEntityOverride entityOverride in skin.EntityOverrides)
            {
                if ((uint)entityOverride.EntityIndex >= (uint)compiledHierarchy.Entities.Count ||
                    (entityOverride.RawValue & 0x3FFF) != entityOverride.EntityIndex)
                {
                    throw new InvalidDataException(
                        $"Compiled skin variant '{skin.Name}' has an entity override that cannot be joined to the compact object table.");
                }

                CompactMeshEntity entity = compiledHierarchy.Entities[entityOverride.EntityIndex];
                if (!sourceNames.Contains(entity.Name))
                    throw new InvalidDataException($"Compiled skin variant '{skin.Name}' references object '{entity.Name}' absent from CHR.");

                entityRows.Add(new(
                    entityOverride.EntityIndex,
                    entity.Name,
                    entityOverride.RawValue));
            }

            variantRows.Add(new(
                skin.Index,
                sourceVariant,
                skin.Name,
                skin.RawFeatures,
                skin.SurfaceOverrideCount,
                skin.RandomizedChildCount,
                entityRows.ToImmutable()));
        }

        ImmutableArray<Dl1CompiledChrObjectIdentityReadBack> objectRows = sourceChr.ObjectNames
            .Select((name, index) =>
            {
                CompactMeshEntity entity = compiledByName[name];
                return new Dl1CompiledChrObjectIdentityReadBack(
                    index,
                    name,
                    entity.Name,
                    entity.Index);
            })
            .ToImmutableArray();
        var fingerprintInput = new
        {
            objectOrderMatches,
            objects = objectRows,
            variants = variantRows.ToImmutable(),
        };
        byte[] fingerprintBytes = JsonSerializer.SerializeToUtf8Bytes(
            fingerprintInput,
            FingerprintJsonOptions);
        string fingerprint = Convert.ToHexStringLower(SHA256.HashData(fingerprintBytes));
        return new(fingerprint, objectOrderMatches, objectRows, fingerprintInput.variants);
    }
}
