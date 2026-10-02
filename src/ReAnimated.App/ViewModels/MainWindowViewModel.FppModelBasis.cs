using ReAnimated.Core.Domain;
using ReAnimated.Core.Mathematics;
using ReAnimated.Retargeting.Mapping;

namespace ReAnimated.App.ViewModels;

public sealed partial class MainWindowViewModel
{
    internal static (Vector3D Left, Vector3D Forward) ResolveFppModelAxes(RigDefinition? rig)
    {
        Vector3D fallbackLeft = -Vector3D.UnitX;
        Vector3D fallbackForward = -Vector3D.UnitZ;
        if (rig is null) return (fallbackLeft, fallbackForward);
        SkeletonPose bind = rig.CreateBindPose();
        foreach (var pair in new[] { (Left: "arm.left.clavicle", Right: "arm.right.clavicle"),
                     (Left: "leg.left.upper", Right: "leg.right.upper") })
        {
            int[] left = rig.Bones.Where(bone => Role(bone) == pair.Left).Select(bone => bone.Index).ToArray();
            int[] right = rig.Bones.Where(bone => Role(bone) == pair.Right).Select(bone => bone.Index).ToArray();
            if (left.Length != 1 || right.Length != 1) continue;
            Vector3D delta = bind.GlobalMatrices[left[0]].Translation - bind.GlobalMatrices[right[0]].Translation;
            Vector3D horizontal = delta - Vector3D.UnitY * Vector3D.Dot(delta, Vector3D.UnitY);
            if (!horizontal.TryNormalize(out Vector3D modelLeft)) continue;
            if (!Vector3D.Cross(modelLeft, Vector3D.UnitY).TryNormalize(out Vector3D modelForward)) continue;
            return (modelLeft, modelForward);
        }
        return (fallbackLeft, fallbackForward);
        static string? Role(BoneDefinition bone) =>
            HumanoidBoneSemanticClassifier.Classify(bone.SemanticRole ?? bone.Name)?.Role;
    }
}
