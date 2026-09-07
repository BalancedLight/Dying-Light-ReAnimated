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
        NativeClothSyntax rewritten = binding.Syntax;
        // Replace calls backwards so source offsets stay valid and unrelated text stays exact.
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
            string replacement = Dl1ClothCodec.WriteMpCloth([entry with { ResourceName = targetName }]).TrimEnd('\r', '\n');
            rewritten = rewritten.ReplaceCommand(index, replacement);
        }
        files.Add(resourceName + ".mpcloth", Encoding.UTF8.GetBytes(rewritten.Write()));
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
}
