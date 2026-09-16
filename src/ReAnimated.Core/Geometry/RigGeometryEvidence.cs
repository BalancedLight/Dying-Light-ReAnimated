using System.Collections.Immutable;
using ReAnimated.Core.Domain;
using ReAnimated.Core.Mathematics;

namespace ReAnimated.Core.Geometry;

/// <summary>Current binding support in selected anatomy components, independent of source naming.</summary>
public sealed record RigBoneGeometrySupport(int BoneIndex, int ControlPointCount, double SurfaceMass,
    Vector3D Centroid, Vector3D Min, Vector3D Max);

/// <summary>
/// Snapshot of one current rig and its current rest geometry. Names and parents guard row identity;
/// exact global matrices avoid deriving correspondence from a projected affine bind pose.
/// This is evidence for mapping, never an instruction to move or rebind the character.
/// </summary>
public sealed record RigGeometryEvidence(string SourceSha256, string GeometryBasis,
    ImmutableArray<string> BoneNames, ImmutableArray<int> ParentIndices,
    ImmutableArray<TransformTRS> RigLocalBindPoses, ImmutableArray<TransformMatrix> GlobalBindMatrices,
    ImmutableArray<RigBoneGeometrySupport> Supports)
{
    public void Validate(RigDefinition rig)
    {
        ArgumentNullException.ThrowIfNull(rig);
        if (SourceSha256 is not { Length: 64 } || SourceSha256.Any(static c => !char.IsAsciiHexDigit(c)) ||
            string.IsNullOrWhiteSpace(GeometryBasis) || BoneNames.IsDefault || ParentIndices.IsDefault ||
            RigLocalBindPoses.IsDefault || RigLocalBindPoses.Length != rig.BoneCount ||
            GlobalBindMatrices.IsDefault || Supports.IsDefault || BoneNames.Length != rig.BoneCount ||
            ParentIndices.Length != rig.BoneCount || GlobalBindMatrices.Length != rig.BoneCount)
            throw new ArgumentException("Geometry correspondence evidence is incomplete.");
        for (int i = 0; i < rig.BoneCount; i++)
        {
            if (BoneNames[i] != rig.Bones[i].Name || ParentIndices[i] != rig.Bones[i].ParentIndex ||
                RigLocalBindPoses[i] != rig.Bones[i].LocalBindPose || !GlobalBindMatrices[i].IsFinite)
                throw new ArgumentException("Geometry correspondence evidence belongs to a different rig or invalid frame.");
            var local = ParentIndices[i] < 0 ? GlobalBindMatrices[i] : GlobalBindMatrices[ParentIndices[i]].InvertedAffine() * GlobalBindMatrices[i];
            if ((local.Translation - RigLocalBindPoses[i].Translation).Length > 1e-8)
                throw new ArgumentException("Geometry correspondence global frames disagree with the current rig's local joint positions.");
        }
        var seen = new HashSet<int>();
        foreach (var support in Supports)
            if (support is null || (uint)support.BoneIndex >= (uint)rig.BoneCount || !seen.Add(support.BoneIndex) ||
                support.ControlPointCount < 0 || !double.IsFinite(support.SurfaceMass) || support.SurfaceMass < 0 ||
                !support.Centroid.IsFinite || !support.Min.IsFinite || !support.Max.IsFinite ||
                support.Min.X > support.Max.X || support.Min.Y > support.Max.Y || support.Min.Z > support.Max.Z)
                throw new ArgumentException("Geometry correspondence contains invalid bone influence support.");
    }
}
