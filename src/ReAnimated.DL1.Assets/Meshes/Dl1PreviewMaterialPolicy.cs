namespace ReAnimated.DL1.Assets.Meshes;

public static class Dl1PreviewMaterialPolicy
{
    public static bool IsNonDisplayMaterial(
        Dl1MaterialSlot? slot) =>
        IsNonDisplayShadowCaster(slot) ||
        IsNonDisplayZeroTechnique(slot);

    public static bool IsNonDisplayShadowCaster(
        Dl1MaterialSlot? slot) =>
        slot is not null &&
        (IsNonDisplayShadowCaster(slot.DatabaseName) ||
         IsNonDisplayShadowCaster(slot.DeclaredDatabaseName));

    public static bool IsNonDisplayShadowCaster(
        string? databaseName) =>
        databaseName is not null &&
        (databaseName.Equals(
             "shadow_caster.mat",
             StringComparison.OrdinalIgnoreCase) ||
         databaseName.Equals(
             "shadowcaster.mat",
             StringComparison.OrdinalIgnoreCase) ||
         databaseName.Equals(
             "shadow_caster_2s.mat",
             StringComparison.OrdinalIgnoreCase));

    public static bool IsNonDisplayZeroTechnique(
        Dl1MaterialSlot? slot)
    {
        if (slot is null)
        {
            return false;
        }

        if (slot.ResolvedMaterial is not null)
        {
            return slot.ResolvedMaterial.TechniqueCount == 0;
        }

        if (IsKnownZeroTechniqueMaterial(slot.DatabaseName))
        {
            return true;
        }

        return slot.SkinReplacementDatabaseEntryIndex is null &&
               IsKnownZeroTechniqueMaterial(
                   slot.DeclaredDatabaseName);
    }

    public static bool IsKnownZeroTechniqueMaterial(
        string? databaseName) =>
        databaseName is not null &&
        (databaseName.Equals(
             "null.mat",
             StringComparison.OrdinalIgnoreCase) ||
         databaseName.Equals(
             "default.mat",
             StringComparison.OrdinalIgnoreCase));

    public static bool IsExactMissingBlendShadowCaster(
        Dl1MaterialSlot? slot) =>
        slot is not null &&
        (IsExactMissingBlendShadowCaster(slot.DatabaseName) ||
         IsExactMissingBlendShadowCaster(
             slot.DeclaredDatabaseName));

    public static bool IsExactMissingBlendShadowCaster(
        string? databaseName) =>
        databaseName?.Equals(
            "shadow_caster.mat",
            StringComparison.OrdinalIgnoreCase) == true;
}
