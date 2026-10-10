namespace ReAnimated.Core.Project;

public static class AnimationExportDefaults
{
    public const int DlcNumber = 99;
    public const string DeveloperToolsRpackFileName = "common_anims_sp_PC.rpack";

    public static string? StockScript(ProjectModelEntry model, ProjectAssetReference? asset)
    {
        if (asset?.Kind != ProjectAssetKind.RetailGameResource) return null;
        string name = asset.RetailIdentity?.ResourceName ?? model.Name;
        string lower = name.ToLowerInvariant();
        if (lower.Contains("player", StringComparison.Ordinal)) return "anims_player";
        if (lower.Contains("zombie", StringComparison.Ordinal) || lower.Contains("volatile", StringComparison.Ordinal) || lower.Contains("screamer", StringComparison.Ordinal) || lower.Contains("demolisher", StringComparison.Ordinal) || lower.Contains("goon", StringComparison.Ordinal)) return "anims_man_zombie";
        if (lower.Contains("woman", StringComparison.Ordinal)) return "anims_woman_all";
        if (lower.Contains("kid", StringComparison.Ordinal)) return "anims_kids_npc";
        if (lower.Contains("npc", StringComparison.Ordinal) || lower.Contains("human", StringComparison.Ordinal) || lower.StartsWith("man_", StringComparison.Ordinal)) return "anims_man_all";
        return null;
    }

    public static string DlcScript(string stockScript, int number = DlcNumber) =>
        $"{stockScript}_dlc{number.ToString(System.Globalization.CultureInfo.InvariantCulture)}";
}
