using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using ReAnimated.Codecs.Fed;
using ReAnimated.Core.ModelAuthoring;

namespace ReAnimated.Codecs.Models;

/// <summary>Model-owned native files, keyed by their relative source-build filename.</summary>
public sealed record Dl1NativeCompanionBuild(
    ImmutableDictionary<string, byte[]> Files,
    ImmutableArray<string> Notes);

/// <summary>
/// Publishes explicit facial poses and retained native cloth declarations. Preview spring
/// coefficients are deliberately not translated to the unrelated native physics parameters.
/// </summary>
public static class Dl1NativeCompanionWriter
{
    public static Dl1NativeCompanionBuild Build(CustomModelDocument model, string resourceName,
        IEnumerable<string> emittedBoneNames)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(emittedBoneNames);
        _ = Dl1SourceModelWriter.RequireExactResourceName(resourceName, 55, "model resource name");
        string[] bones = emittedBoneNames.ToArray();
        model.FacialPresets.Validate(model.MorphChannels.Select(channel => channel.Name));
        model.SecondaryMotion.Validate(bones);
        var files = ImmutableDictionary.CreateBuilder<string, byte[]>(StringComparer.Ordinal);
        var notes = ImmutableArray.CreateBuilder<string>();

        if (!model.FacialPresets.Presets.IsEmpty)
        {
            string[] targets = model.MorphChannels.Select(channel => channel.Name).ToArray();
            var expressions = new List<FedExpression>();
            foreach (FacialPresetDefinition preset in model.FacialPresets.Presets)
            {
                AddPose(preset.Name, preset.Weights);
                if (!preset.SpeechWeights.IsEmpty) AddPose(preset.Name + " speech", preset.SpeechWeights);
            }
            files.Add(resourceName + ".fed", FedWriter.Write(new(resourceName, expressions, []),
                new FedLimits { RejectDuplicateNames = true }));
            notes.Add($"Native FED exports {expressions.Count} named poses with all {targets.Length} target rows. " +
                "Pose names are preserved; name explicit presets normal, alarmed, dead, _NONE, BLINK or EYES_UP/DOWN/LEFT/RIGHT when those native aliases are needed.");

            void AddPose(string name, ImmutableDictionary<string, double> weights)
            {
                // Library names are case-insensitive; emit the exact compiled-source spelling.
                var lookup = weights.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);
                FedMorphWeight[] rows = targets.Select(target => new FedMorphWeight(target,
                    (float)lookup.GetValueOrDefault(target))).ToArray();
                if (rows.Count(row => Math.Abs(row.Weight) > 0.001f) > 64)
                    throw new InvalidDataException($"Native facial pose '{name}' exceeds the 64 simultaneously active target limit.");
                expressions.Add(new(name, rows));
            }
        }

        ImmutableArray<NativeClothSource> sources = model.SecondaryMotion.NativeSources;
        if (sources.IsEmpty)
        {
            if (!model.SecondaryMotion.Groups.IsEmpty)
                notes.Add("Secondary motion currently has preview settings only. Import the matching PHX and MPCloth scripts, then save a model copy to include native physics in deployment.");
            return new(files.ToImmutable(), notes.ToImmutable());
        }

        NativeClothSource[] physics = sources.Where(source => source.Kind == NativeClothSourceKind.Phx)
            .OrderBy(source => source.ResourceName, StringComparer.OrdinalIgnoreCase).ToArray();
        NativeClothSource[] wrappers = sources.Where(source => source.Kind == NativeClothSourceKind.MpCloth).ToArray();
        if (physics.Length == 0 || wrappers.Length != 1)
            throw new InvalidDataException("Native cloth export requires PHX sources and exactly one MPCloth wrapper for this model. Import both before saving the model copy.");

        var renamed = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (int index = 0; index < physics.Length; index++)
        {
            NativeClothSource source = physics[index];
            NativePhxDocument parsed = Dl1ClothCodec.ReadPhx(source.Text, bones);
            RequireValid(source.ResourceName, parsed.Diagnostics);
            ValidateNativeGridBoneRoles(model, source.ResourceName, parsed, notes);
            foreach (NativeClothCommand include in parsed.Syntax.Commands.Where(call => call.Name == "!include"))
            {
                // These are standalone source documents. Do not claim a portable closure when
                // a user-supplied include would resolve outside the packaged source inventory.
                if (include.Arguments.Length != 1 || include.Arguments[0] != "\"MeshPartCloth.def\"")
                    throw new InvalidDataException($"Native cloth '{source.ResourceName}' references an unpackaged include. Inline its definitions before exporting.");
            }
            string name = resourceName + "_" + index.ToString("D3", CultureInfo.InvariantCulture) + ".phx";
            renamed.Add(source.ResourceName.Replace('\\', '/'), name);
            files.Add(name, Encoding.UTF8.GetBytes(source.Text));
        }

        NativeClothSource wrapper = wrappers[0];
        NativeMpClothDocument binding = Dl1ClothCodec.ReadMpCloth(wrapper.Text);
        RequireValid(wrapper.ResourceName, binding.Diagnostics);
        if (binding.Syntax.Commands.Any(call => call.Name == "!include"))
            throw new InvalidDataException("Native MPCloth includes must be inlined before exporting this model.");
        var usedResources = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var replacements = new Dictionary<NativeClothQuotedArgument, string>();
        // Change only resource-name tokens; preserve inline comments and authored flag syntax.
        for (int index = binding.Syntax.Commands.Length - 1; index >= 0; index--)
        {
            NativeClothCommand call = binding.Syntax.Commands[index];
            if (call.Name != "MeshPartCloth") continue;
            NativeClothBinding entry = Dl1ClothCodec.ReadMpCloth(wrapper.Text.Substring(call.Start, call.Length)).Bindings.Single();
            string sourceName = entry.ResourceName.Replace('\\', '/');
            if (!renamed.TryGetValue(sourceName, out string? targetName))
                throw new InvalidDataException($"Native PHX '{sourceName}' is missing from this model's saved sources.");
            if (!usedResources.Add(sourceName))
                throw new InvalidDataException($"Native PHX '{sourceName}' is bound more than once in the model.");
            if (entry.Enabled == 0)
                notes.Add($"Native PHX '{sourceName}' is exported with its MPCloth binding disabled. It will not provide active garment physics until that authored binding is enabled.");
            replacements.Add(call.QuotedArguments.Single(a => a.ArgumentIndex == 0), targetName);
        }
        string[] unboundResources = renamed.Keys.Except(usedResources, StringComparer.OrdinalIgnoreCase)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase).ToArray();
        if (unboundResources.Length != 0)
            throw new InvalidDataException($"Native PHX source(s) have no MPCloth binding: {string.Join(", ", unboundResources)}.");
        files.Add(resourceName + ".mpcloth", Encoding.UTF8.GetBytes(binding.Syntax.ReplaceQuotedArguments(replacements).Write()));
        notes.Add($"Native cloth exports {physics.Length} PHX source(s) and a matching-basename MPCloth wrapper. " +
            "Native coefficients and binding flags are preserved; preview tuning does not modify them. " +
            "Imported scripts are not re-fitted to compiled bone frames or collision bounds. Check those against the current compiled model and validate physics in Player.");
        return new(files.ToImmutable(), notes.Distinct(StringComparer.Ordinal).ToImmutableArray());

        void RequireValid(string name, ImmutableArray<NativeClothDiagnostic> diagnostics)
        {
            string[] errors = diagnostics.Where(diagnostic => diagnostic.IsError).Select(diagnostic => diagnostic.Message).ToArray();
            if (errors.Length != 0)
                throw new InvalidDataException($"Native cloth '{name}': {string.Join("; ", errors)}");
            notes.AddRange(diagnostics.Where(diagnostic => !diagnostic.IsError)
                .Select(diagnostic => $"Native cloth '{name}': {diagnostic.Message}"));
        }
    }

    private static void ValidateNativeGridBoneRoles(
        CustomModelDocument model, string resourceName, NativePhxDocument phx, ImmutableArray<string>.Builder notes)
    {
        NativeClothNode[] namedGridNodes = phx.Nodes.Where(node => node.BoneName.Length > 0).ToArray();
        if (namedGridNodes.Length == 0) return;

        if (model.RiggingSession?.Recipe is not { } recipe || recipe.ProfileSnapshot is not { } profile ||
            recipe.Assignments.IsEmpty || recipe.Entities.IsEmpty)
        {
            notes.Add($"Native cloth '{resourceName}' has {namedGridNodes.Length} named PHX grid node(s) without saved semantic role assignments. Core-body grid safety is unverified; review these nodes before using the exported model in Player.");
            return;
        }

        // Use the saved role graph rather than guessing from imported bone spellings. PHX grid
        // Bone rows of either type affect the cloth/rig relationship, so core body bones are
        // rejected even when fixed. Collider references are separate declarations and allowed.
        var boneRolesById = profile.Roles
            .Where(role => role.Category != RigRoleCategory.Unknown &&
                role.EntityKind is RigNativeEntityKind.Bone or RigNativeEntityKind.Helper)
            .GroupBy(role => role.Id, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Select(role => role.Category).ToHashSet(), StringComparer.Ordinal);

        var entitiesById = recipe.Entities
            .Where(entity => entity.OwnerAssetId == model.ModelId &&
                entity.Kind is RigNativeEntityKind.Bone or RigNativeEntityKind.Helper or RigNativeEntityKind.Unknown)
            .GroupBy(entity => entity.EntityId)
            .ToDictionary(group => group.Key, group => group.Select(entity => entity.NativeName).Distinct(StringComparer.Ordinal).ToArray());
        var categoriesByBoneName = new Dictionary<string, HashSet<RigRoleCategory>>(StringComparer.Ordinal);
        foreach (RigRoleAssignment assignment in recipe.Assignments)
        {
            if (!boneRolesById.TryGetValue(assignment.RoleId, out HashSet<RigRoleCategory>? categories) ||
                !entitiesById.TryGetValue(assignment.EntityId, out string[]? names)) continue;
            foreach (string name in names)
            {
                if (!categoriesByBoneName.TryGetValue(name, out HashSet<RigRoleCategory>? assignedCategories))
                    categoriesByBoneName.Add(name, assignedCategories = []);
                assignedCategories.UnionWith(categories);
            }
        }

        int unclassifiedNodes = 0;
        foreach (NativeClothNode node in namedGridNodes)
        {
            if (!categoriesByBoneName.TryGetValue(node.BoneName, out HashSet<RigRoleCategory>? categories))
            {
                unclassifiedNodes++;
                continue;
            }
            if (categories.Contains(RigRoleCategory.Body))
                throw new InvalidDataException($"Native cloth '{resourceName}' uses core body bone '{node.BoneName}' as a PHX grid node (type {node.Type}). Keep core body bones as collider references and use explicitly classified extra-bone roots for the grid.");
        }
        if (unclassifiedNodes > 0)
            notes.Add($"Native cloth '{resourceName}' has {unclassifiedNodes} named PHX grid node(s) without a saved semantic role assignment. Core-body grid safety is unverified; review these nodes before using the exported model in Player.");
    }
}
