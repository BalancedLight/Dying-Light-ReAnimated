using System.Collections.Immutable;
using ReAnimated.Core.Mathematics;
using ReAnimated.Core.ModelAuthoring;

namespace ReAnimated.Codecs.Models;

public sealed record Dl1CompanionReferenceTransferResult(SecondaryMotionDefinition Definition, int PreviewBindings, int NativeBindings,
    ImmutableArray<string> Notes);

/// <summary>Reconciles known bone references without translating preview tuning into native physics parameters.</summary>
public static class Dl1CompanionReferenceTransfer
{
    public static Dl1CompanionReferenceTransferResult Apply(SecondaryMotionDefinition source,
        ImmutableArray<CustomModelBone> before, ImmutableArray<CustomModelBone> after,
        ImmutableArray<int> sourceToTarget, ImmutableArray<TransformMatrix> transfers, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(source); token.ThrowIfCancellationRequested();
        if (before.IsDefault || after.IsDefault || sourceToTarget.IsDefault || transfers.IsDefault ||
            before.Length != sourceToTarget.Length || before.Length != transfers.Length ||
            sourceToTarget.Any(i => i < -1 || i >= after.Length))
            throw new ArgumentException("Companion transfer requires the complete source-to-target hierarchy map.");
        source.Validate(before.Select(b => b.Name));
        if (source.Groups.IsEmpty && source.NativeSources.IsEmpty) return new(source, 0, 0, []);
        var lookup = before.ToDictionary(b => b.Name, b => b.Index, StringComparer.Ordinal);
        var oldGlobals = Globals(before); var newGlobals = Globals(after);
        var targetNames = after.Select(b => b.Name).ToArray();
        int previewCount = 0, nativeCount = 0;
        var groups = source.Groups.Select(group => group with
        {
            Particles = group.Particles.Select(p =>
            {
                token.ThrowIfCancellationRequested();
                var binding = Move(p.ReferenceBoneName, p.LocalPosition); previewCount++;
                return p with { ReferenceBoneName = binding.Name, LocalPosition = binding.Local,
                    DrivenBoneName = p.DrivenBoneName is { } driven ? Rename(driven) : null };
            }).ToImmutableArray(),
            Colliders = group.Colliders.Select(c =>
            {
                token.ThrowIfCancellationRequested(); var start = Move(c.BoneName, c.LocalPosition); previewCount++;
                var end = c.EndBoneName is { } endName ? Move(endName, c.EndLocalPosition) : default;
                return c with { BoneName = start.Name, LocalPosition = start.Local, EndBoneName = c.EndBoneName is null ? null : end.Name,
                    EndLocalPosition = c.EndBoneName is null ? c.EndLocalPosition : end.Local };
            }).ToImmutableArray(),
        }).ToImmutableArray();
        var sources = ImmutableArray.CreateBuilder<NativeClothSource>(source.NativeSources.Length);
        foreach (var native in source.NativeSources)
        {
            token.ThrowIfCancellationRequested();
            if (native.Kind != NativeClothSourceKind.Phx) { sources.Add(native); continue; }
            var parsed = Dl1ClothCodec.ReadPhx(native.Text, before.Select(b => b.Name));
            RequireValid(native.ResourceName, parsed.Diagnostics);
            var replacements = new Dictionary<NativeClothQuotedArgument, string>();
            foreach (var call in parsed.Syntax.Commands)
            {
                token.ThrowIfCancellationRequested();
                int[] slots = call.Name switch
                {
                    "Bone" => [2],
                    "CollisionSphere" or "CollisionSphereShift" or "CollisionCapsule" => [0],
                    "CollisionCapsuleBetween" => [0, 2],
                    _ => [],
                };
                foreach (var text in call.QuotedArguments)
                {
                    if (slots.Contains(text.ArgumentIndex))
                    {
                        if (call.Name == "Bone" && text.Value.Length == 0) continue;
                        string name = Rename(text.Value);
                        if (name != text.Value) { replacements.Add(text, name); nativeCount++; }
                    }
                    else if (call.Name != "!include" && lookup.TryGetValue(text.Value, out int oldIndex) &&
                        (sourceToTarget[oldIndex] < 0 || after[sourceToTarget[oldIndex]].Name != text.Value))
                        throw new InvalidDataException($"Native cloth '{native.ResourceName}' has an unsupported '{call.Name}' argument referring to changed bone '{text.Value}'. Review that statement explicitly; the source was not changed.");
                }
            }
            string updated = parsed.Syntax.ReplaceQuotedArguments(replacements).Write();
            RequireValid(native.ResourceName, Dl1ClothCodec.ReadPhx(updated, targetNames).Diagnostics);
            sources.Add(updated == native.Text ? native : native with { Text = updated, OriginalText = native.OriginalText ?? native.Text });
        }
        var definition = source with { Groups = groups, NativeSources = sources.MoveToImmutable() };
        definition.Validate(targetNames);
        var notes = ImmutableArray.CreateBuilder<string>();
        if (!source.Groups.IsEmpty)
            notes.Add("Editor secondary-motion anchors were transported with the conformed rig. Constraint distances, radii and tuning were retained; review the resulting garment shape and contacts.");
        if (!source.NativeSources.IsEmpty)
            notes.Add("Known native bone references were reconciled. Native coefficients, collision attachment modes, distances and binding flags were retained; compiled bounds and physics behavior require a fresh native review.");
        return new(definition, previewCount, nativeCount, notes.ToImmutable());

        string Rename(string name)
        {
            if (!lookup.TryGetValue(name, out int index)) throw new InvalidDataException($"Companion bone '{name}' is absent from the source hierarchy.");
            int target = sourceToTarget[index];
            if (target < 0) throw new InvalidDataException($"Conformance would drop companion bone '{name}'. Retain or explicitly remap this bone before conforming.");
            return after[target].Name;
        }
        (string Name, Vector3D Local) Move(string name, Vector3D local)
        {
            string targetName = Rename(name); int index = lookup[name]; int target = sourceToTarget[index];
            if (oldGlobals[index].Equals(newGlobals[target]) && transfers[index].Equals(TransformMatrix.Identity))
                return (targetName, local);
            var value = newGlobals[target].InvertedAffine().TransformPoint(transfers[index].TransformPoint(oldGlobals[index].TransformPoint(local)));
            if (!value.IsFinite) throw new InvalidDataException($"Companion offset for '{name}' becomes nonfinite.");
            return (targetName, value);
        }
    }

    private static TransformMatrix[] Globals(ImmutableArray<CustomModelBone> bones)
    {
        var result = new TransformMatrix[bones.Length];
        for (int index = 0; index < bones.Length; index++)
        {
            var bone = bones[index];
            if (bone.Index != index || bone.ParentIndex < -1 || bone.ParentIndex >= bone.Index)
                throw new InvalidDataException("Companion transfer requires a parent-first hierarchy.");
            result[bone.Index] = bone.ParentIndex < 0 ? bone.ExactLocalBindMatrix : result[bone.ParentIndex] * bone.ExactLocalBindMatrix;
            if (!result[bone.Index].IsFinite || !double.IsFinite(result[bone.Index].LinearDeterminant) || result[bone.Index].LinearDeterminant == 0)
                throw new InvalidDataException("Companion transfer encountered an invalid source or target frame.");
        }
        return result;
    }
    private static void RequireValid(string name, ImmutableArray<NativeClothDiagnostic> diagnostics)
    {
        var errors = diagnostics.Where(d => d.IsError).Select(d => d.Message).ToArray();
        if (errors.Length > 0) throw new InvalidDataException($"Native cloth '{name}': {string.Join("; ", errors)}");
    }
}
