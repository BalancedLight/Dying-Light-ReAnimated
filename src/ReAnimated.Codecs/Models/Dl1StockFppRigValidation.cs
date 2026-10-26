using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using ReAnimated.Core.Mathematics;
using ReAnimated.Core.ModelAuthoring;

namespace ReAnimated.Codecs.Models;

/// <summary>Checks the stock reference contract retained by an applied first-person fit.</summary>
public static class Dl1StockFppRigValidation
{
    public static string FrameSignature(IEnumerable<CustomModelBone> rows)
    {
        CustomModelBone[] bones = rows.ToArray();
        using var stream = new MemoryStream(); using (var writer = new BinaryWriter(stream, Encoding.UTF8, true))
        {
            writer.Write(bones.Length);
            foreach (CustomModelBone b in bones)
            {
                writer.Write(b.Name); writer.Write(b.ParentIndex < 0 ? string.Empty : bones[b.ParentIndex].Name);
                TransformMatrix m = b.ExactLocalBindMatrix;
                foreach (double v in new[] { m.M11, m.M12, m.M13, m.M14, m.M21, m.M22, m.M23, m.M24, m.M31, m.M32, m.M33, m.M34, m.M41, m.M42, m.M43, m.M44 }) writer.Write(v);
            }
        }
        return Convert.ToHexStringLower(SHA256.HashData(stream.ToArray()));
    }

    public static void RequireApplied(CustomModelDocument document)
    {
        if (document.RigConformance is not { FitStrategy: CustomModelRigFitStrategy.StockFppRegional } record) return;
        if (record.StockFppNativeBoneCount <= 0 || record.StockFppNativeFrameSignature is null)
            throw new InvalidOperationException("Apply the stock first-person fit before exporting this model.");
        RequireBaseFrames(document, record);
    }

    public static void RequirePrepared(CustomModelDocument document, Dl1AuthoredRigContract contract)
    {
        if (document.RigConformance is not { FitStrategy: CustomModelRigFitStrategy.StockFppRegional } record ||
            record.StockFppNativeBoneCount <= 0 || record.StockFppNativeFrameSignature is null) return;
        ImmutableArray<CustomModelBone> native = RequireBaseFrames(document, record);
        var nodes = contract.Nodes.ToDictionary(n => n.Name, StringComparer.OrdinalIgnoreCase);
        var policies = Dl1BoneScriptPolicyResolver.Resolve(document, contract).ToDictionary(p => p.Name, StringComparer.OrdinalIgnoreCase);
        foreach (CustomModelBone bone in native)
        {
            if (!nodes.TryGetValue(bone.Name, out Dl1AuthoredRigNode? node) ||
                !node.LocalBindMatrix.NearlyEquals(bone.ExactLocalBindMatrix, 5e-6) ||
                (node.ParentPhysicalIndex < 0 ? null : contract.Nodes[node.ParentPhysicalIndex].Name) != (bone.ParentIndex < 0 ? null : native[bone.ParentIndex].Name))
                throw new InvalidOperationException($"Native first-person frame '{bone.Name}' was changed by a helper or frame override. Reapply the stock FPP fit.");
            RigAnimationComponents expected = bone.ParentIndex < 0 ? RigAnimationComponents.Position | RigAnimationComponents.Rotation | RigAnimationComponents.Scale :
                RigAnimationComponents.Position | RigAnimationComponents.Rotation;
            if (!policies.TryGetValue(bone.Name, out Dl1ResolvedBoneScriptPolicy? policy) || policy.Mask != expected)
                throw new InvalidOperationException($"Native first-person channels for '{bone.Name}' must retain {Dl1BoneScriptPolicyResolver.FormatComponents(expected)}. Reapply the stock FPP fit.");
        }
    }

    private static ImmutableArray<CustomModelBone> RequireBaseFrames(CustomModelDocument document, CustomModelRigConformance record)
    {
        if (record.StockFppNativeBoneCount > document.Bones.Length)
            throw new InvalidOperationException("The stock first-person skeleton is incomplete. Reapply its fit.");
        ImmutableArray<CustomModelBone> native = document.Bones.Take(record.StockFppNativeBoneCount).ToImmutableArray();
        if (native.Any(b => b.ParentIndex >= native.Length) || FrameSignature(native) != record.StockFppNativeFrameSignature)
            throw new InvalidOperationException("The native first-person joint frames or parent links were edited. Reapply the stock FPP fit to restore them.");
        return native;
    }
}
