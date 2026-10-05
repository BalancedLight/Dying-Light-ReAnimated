using System.Collections.Immutable;
using ReAnimated.Core.ModelAuthoring;

namespace ReAnimated.Codecs.Models;

/// <summary>Maps immutable compact skin indexes to named source-script identities.</summary>
public static class Dl1CharacterSkinAuthoring
{
    public static ImmutableArray<Dl1SkinGenerationDefinition> FromInventory(CharacterResourceInventory? inventory,IEnumerable<string>? emittedMaterialSlots=null)
    {
        if(inventory is null || inventory.SkinVariants.IsEmpty) return [];
        string Material(int index)=>inventory.OriginalMaterials.SingleOrDefault(m=>m.Index==index)?.Name
            ?? throw new InvalidDataException("Original skin material index has no exact name mapping.");
        string Entity(int index)=>inventory.OriginalEntities.SingleOrDefault(e=>e.Index==index)?.Name
            ?? throw new InvalidDataException("Original skin entity index has no exact name mapping.");
        int originalSlots=inventory.OriginalMaterialSlotCount>0?inventory.OriginalMaterialSlotCount:inventory.SkinVariants.SelectMany(v=>v.MaterialOverrides).Select(m=>m.TargetMaterialSlotIndex+1).DefaultIfEmpty(0).Max();
        var appended=(emittedMaterialSlots??[]).Skip(originalSlots).Select(name=>new Dl1SkinMaterialReplacement(name,name)).ToImmutableArray();
        return inventory.SkinVariants.Select(v=>new Dl1SkinGenerationDefinition(v.Name,v.RawFeatures,
            v.MaterialOverrides.Select(m=>new Dl1SkinMaterialReplacement(Material(m.TargetMaterialSlotIndex),Material(m.ReplacementDatabaseEntryIndex))).Concat(appended).ToImmutableArray(),
            v.EntityOverrides.Select(e=>new Dl1SkinEntityVisibility(Entity(e.SourceEntityIndex),(e.RawValue&0x8000)!=0,e.RawValue)).ToImmutableArray(),
            v.SurfaceOverrideCount,v.RandomizedChildCount)
        {
            // Exact Editor source finalization copies this word unchanged. Only
            // modeled command bits, absent groups/strings/tags and mapped surfaces
            // may reach canonical generation; compiler readback is mandatory.
            VerifiedSourceFeatures=(Dl1SkinSourceFeatures)v.RawFeatures,
            VerifiedEnableNewSkins=(v.RawFeatures&0x40)!=0,
            ExpectedCompiledColors=v.ColorBytes,ExpectedTagBytes=v.TagBytes,
            OptionalStringsAndGroupsVerifiedAbsent=v.RandomizedChildCount==0 && string.IsNullOrEmpty(v.MorphsPreset) && string.IsNullOrEmpty(v.Character0) && string.IsNullOrEmpty(v.Character1),
            VerifiedSurfaceReplacements=v.SurfaceOverrides.Select(s=>new Dl1SkinSurfaceMapping(SurfaceToken(s.OriginalSurfaceId),SurfaceToken(s.ReplacementSurfaceId),s.Flags==0?string.Empty:throw new InvalidDataException("Unmapped original surface flags."),s.OriginalSurfaceId,s.ReplacementSurfaceId,s.Flags)).ToImmutableArray(),
        }).ToImmutableArray();
    }

    private static string SurfaceToken(byte id)=>id switch {0=>"Unknown",3=>"Water",10=>"Flesh",_=>throw new InvalidDataException("Original surface ID has no verified source representative.")};
}
