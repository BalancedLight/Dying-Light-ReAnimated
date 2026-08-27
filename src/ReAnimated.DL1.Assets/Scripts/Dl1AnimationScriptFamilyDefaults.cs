using System.Collections.Immutable;
using ReAnimated.DL1.Assets.Meshes;

namespace ReAnimated.DL1.Assets.Scripts;

/// <summary>
/// A proposed stock animation script for a model, together with the reason it
/// was proposed. Every value of this type is a heuristic: nothing in shipped
/// retail data records which script animates which model, so this is a
/// starting point for the author, never a derived fact. <see cref="Basis"/>
/// carries the wording shown to the author.
/// </summary>
public sealed record Dl1AnimationScriptSuggestion(
    string ResourceName,
    string Basis);

/// <summary>
/// Curated model-family to stock-script defaults.
/// </summary>
/// <remarks>
/// <para>
/// The binding this table approximates does not exist in any shipped retail
/// artifact. Across all retail <c>.pak</c> archives there are no <c>.ascr</c>
/// alias scripts; retail <c>.chr</c> carry no script reference; the compiled
/// type-322 record stores only a name offset and timing; and the token
/// <c>AnimScript</c> appears nowhere in <c>Data0.pak</c>. Techland resolved the
/// binding at compile time from sources they did not ship.
/// </para>
/// <para>
/// So this table is a convenience keyed on the existing classification
/// evidence (<see cref="Dl1RigFamily"/> and <see cref="Dl1MeshPerspective"/>),
/// in the same spirit as <c>docs/DL1_RETAIL_RIG_PROFILES.md</c>, which states
/// that classification is evidence and not a runtime-correction profile. Every
/// surface that shows a suggestion from here must present it as a suggestion
/// the author can override.
/// </para>
/// </remarks>
public static class Dl1AnimationScriptFamilyDefaults
{
    /// <summary>
    /// Scripts a character animation pack is conventionally registered
    /// against, most general first. Used to order the picker so the useful
    /// entry points sit above the ~200 leaf scripts.
    /// </summary>
    public static readonly ImmutableArray<string> PreferredRoots =
    [
        "anims_player",
        "anims_man_all",
        "anims_man_zombie",
        "anims_woman_all",
        "anims_man_npc",
        "anims_kids_npc",
    ];

    public static Dl1AnimationScriptSuggestion? Suggest(
        Dl1RigFamily family,
        Dl1MeshPerspective perspective)
    {
        string basis =
            $"Suggested from the {Describe(family)} rig family" +
            (perspective == Dl1MeshPerspective.Unknown
                ? string.Empty
                : $" and {Describe(perspective)} perspective") +
            ". Retail data does not record which script animates a model, so " +
            "confirm this before exporting.";
        return family switch
        {
            Dl1RigFamily.Player => new Dl1AnimationScriptSuggestion(
                "anims_player",
                basis),
            Dl1RigFamily.GenericInfected or
                Dl1RigFamily.Volatile or
                Dl1RigFamily.Screamer or
                Dl1RigFamily.Demolisher or
                Dl1RigFamily.Goon => new Dl1AnimationScriptSuggestion(
                    "anims_man_zombie",
                    basis),
            Dl1RigFamily.GenericNpc => new Dl1AnimationScriptSuggestion(
                "anims_man_all",
                basis),
            _ => null,
        };
    }

    /// <summary>
    /// Proposes a stock script for a model by name, for the common case where
    /// the model has not been decoded and classified yet. It reuses the same
    /// bounded token vocabulary the mesh classifier hints with, so
    /// <c>player_1_tpp</c> proposes <c>anims_player</c> and
    /// <c>zombie_man_a</c> proposes <c>anims_man_zombie</c>.
    /// </summary>
    public static Dl1AnimationScriptSuggestion? SuggestFromModelName(
        string? modelName)
    {
        if (string.IsNullOrWhiteSpace(modelName))
        {
            return null;
        }

        return Suggest(
            Dl1RetailMeshClassificationService.HintFamilyFromName(modelName),
            Dl1RetailMeshClassificationService.HintPerspectiveFromName(
                modelName));
    }

    /// <summary>
    /// Orders an index's scripts for a picker: the preferred roots first, then
    /// any other script nothing includes, then every remaining leaf.
    /// </summary>
    public static ImmutableArray<Dl1RetailAnimationScript> OrderForPicker(
        Dl1RetailAnimationScriptIndex index)
    {
        ArgumentNullException.ThrowIfNull(index);
        Dictionary<string, int> rank = new(StringComparer.OrdinalIgnoreCase);
        for (int position = 0; position < PreferredRoots.Length; position++)
        {
            rank[PreferredRoots[position]] = position;
        }

        HashSet<string> roots = new(
            index.Roots.Select(static script => script.ResourceName),
            StringComparer.OrdinalIgnoreCase);
        return
        [
            .. index.Scripts
                .OrderBy(script => rank.TryGetValue(
                    script.ResourceName,
                    out int position)
                    ? position
                    : roots.Contains(script.ResourceName)
                        ? PreferredRoots.Length
                        : PreferredRoots.Length + 1)
                .ThenBy(
                    static script => script.ResourceName,
                    StringComparer.OrdinalIgnoreCase),
        ];
    }

    private static string Describe(Dl1RigFamily family) => family switch
    {
        Dl1RigFamily.Player => "player",
        Dl1RigFamily.GenericNpc => "generic NPC",
        Dl1RigFamily.GenericInfected => "generic infected",
        Dl1RigFamily.Volatile => "volatile",
        Dl1RigFamily.Screamer => "screamer",
        Dl1RigFamily.Demolisher => "demolisher",
        Dl1RigFamily.Goon => "goon",
        _ => "unclassified",
    };

    private static string Describe(Dl1MeshPerspective perspective) =>
        perspective switch
        {
            Dl1MeshPerspective.FirstPerson => "first-person",
            Dl1MeshPerspective.ThirdPerson => "third-person",
            _ => "unknown",
        };
}
