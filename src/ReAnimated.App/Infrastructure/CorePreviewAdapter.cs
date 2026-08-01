using System.Numerics;
using ReAnimated.Core.Domain;
using ReAnimated.Core.Mathematics;
using ReAnimated.Renderer.D3D11;

namespace ReAnimated.App.Infrastructure;

public static class CorePreviewAdapter
{
    public static SkeletonRenderData ToRenderSkeleton(
        SkeletonPose pose,
        int? selectedBoneIndex = null,
        TransformMatrix? actorWorldTransform = null)
    {
        ArgumentNullException.ThrowIfNull(pose);
        BoneRenderData[] bones = new BoneRenderData[pose.Rig.BoneCount];
        for (int index = 0; index < bones.Length; index++)
        {
            BoneDefinition bone = pose.Rig.Bones[index];
            bones[index] = new BoneRenderData(
                bone.Name,
                bone.ParentIndex,
                ToSystemMatrix(pose.LocalTransforms[index].ToMatrix()),
                ToSystemMatrix(pose.GlobalMatrices[index]),
                selectedBoneIndex == index)
            {
                Role = ToRenderRole(bone.Kind),
            };
        }

        return new SkeletonRenderData(
            bones,
            actorWorldTransform is { } actorWorld
                ? ToSystemMatrix(actorWorld)
                : Matrix4x4.Identity);
    }

    public static Matrix4x4 ToSystemMatrix(in TransformMatrix matrix) =>
        new(
            (float)matrix.M11,
            (float)matrix.M21,
            (float)matrix.M31,
            (float)matrix.M41,
            (float)matrix.M12,
            (float)matrix.M22,
            (float)matrix.M32,
            (float)matrix.M42,
            (float)matrix.M13,
            (float)matrix.M23,
            (float)matrix.M33,
            (float)matrix.M43,
            (float)matrix.M14,
            (float)matrix.M24,
            (float)matrix.M34,
            (float)matrix.M44);

    public static TransformMatrix ToCoreMatrix(in Matrix4x4 matrix) =>
        new(
            matrix.M11,
            matrix.M21,
            matrix.M31,
            matrix.M41,
            matrix.M12,
            matrix.M22,
            matrix.M32,
            matrix.M42,
            matrix.M13,
            matrix.M23,
            matrix.M33,
            matrix.M43,
            matrix.M14,
            matrix.M24,
            matrix.M34,
            matrix.M44);

    private static BoneRenderRole ToRenderRole(BoneKind kind) =>
        kind switch
        {
            BoneKind.Root or BoneKind.Deform =>
                BoneRenderRole.Deform,
            BoneKind.Helper =>
                BoneRenderRole.Helper,
            BoneKind.Camera =>
                BoneRenderRole.Camera,
            BoneKind.Prop =>
                BoneRenderRole.Prop,
            _ => throw new ArgumentOutOfRangeException(
                nameof(kind),
                kind,
                "The core bone kind is unknown."),
        };
}
