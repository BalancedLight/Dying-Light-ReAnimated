namespace ReAnimated.Codecs.CompactMesh;

[Flags]
public enum CompactMeshEntityType : byte
{
    Unknown = 0,
    Mesh = 1,
    SkinnedMesh = 2,
    Helper = 4,
    Bone = 8,
    Hull = 16,
}

public enum CompactMeshDiagnosticSeverity
{
    Information,
    Warning,
    Error,
}

public sealed record CompactMeshDiagnostic(
    string Code,
    CompactMeshDiagnosticSeverity Severity,
    string Message,
    int? EntityIndex = null);

public readonly record struct CompactBounds(
    float CenterX,
    float CenterY,
    float CenterZ,
    float HalfX,
    float HalfY,
    float HalfZ)
{
    public bool IsFinite =>
        float.IsFinite(CenterX) &&
        float.IsFinite(CenterY) &&
        float.IsFinite(CenterZ) &&
        float.IsFinite(HalfX) &&
        float.IsFinite(HalfY) &&
        float.IsFinite(HalfZ);
}

public readonly record struct CompactMatrix3x4(
    float M11,
    float M12,
    float M13,
    float M14,
    float M21,
    float M22,
    float M23,
    float M24,
    float M31,
    float M32,
    float M33,
    float M34)
{
    public static CompactMatrix3x4 Identity { get; } = new(
        1, 0, 0, 0,
        0, 1, 0, 0,
        0, 0, 1, 0);

    public bool IsFinite =>
        float.IsFinite(M11) &&
        float.IsFinite(M12) &&
        float.IsFinite(M13) &&
        float.IsFinite(M14) &&
        float.IsFinite(M21) &&
        float.IsFinite(M22) &&
        float.IsFinite(M23) &&
        float.IsFinite(M24) &&
        float.IsFinite(M31) &&
        float.IsFinite(M32) &&
        float.IsFinite(M33) &&
        float.IsFinite(M34);

    public static CompactMatrix3x4 Multiply(
        in CompactMatrix3x4 left,
        in CompactMatrix3x4 right) =>
        new(
            left.M11 * right.M11 + left.M12 * right.M21 + left.M13 * right.M31,
            left.M11 * right.M12 + left.M12 * right.M22 + left.M13 * right.M32,
            left.M11 * right.M13 + left.M12 * right.M23 + left.M13 * right.M33,
            left.M11 * right.M14 + left.M12 * right.M24 + left.M13 * right.M34 + left.M14,
            left.M21 * right.M11 + left.M22 * right.M21 + left.M23 * right.M31,
            left.M21 * right.M12 + left.M22 * right.M22 + left.M23 * right.M32,
            left.M21 * right.M13 + left.M22 * right.M23 + left.M23 * right.M33,
            left.M21 * right.M14 + left.M22 * right.M24 + left.M23 * right.M34 + left.M24,
            left.M31 * right.M11 + left.M32 * right.M21 + left.M33 * right.M31,
            left.M31 * right.M12 + left.M32 * right.M22 + left.M33 * right.M32,
            left.M31 * right.M13 + left.M32 * right.M23 + left.M33 * right.M33,
            left.M31 * right.M14 + left.M32 * right.M24 + left.M33 * right.M34 + left.M34);

    public (float X, float Y, float Z) TransformPoint(
        float x,
        float y,
        float z) =>
        (
            M11 * x + M12 * y + M13 * z + M14,
            M21 * x + M22 * y + M23 * z + M24,
            M31 * x + M32 * y + M33 * z + M34
        );
}

public sealed record CompactMeshEntity(
    int Index,
    string Name,
    uint Flags,
    CompactBounds Bounds,
    short ParentIndex,
    CompactMeshEntityType EntityType,
    byte ChildCount,
    byte LodCount,
    CompactMatrix3x4 LocalMatrix,
    CompactMatrix3x4 ReferenceMatrix,
    ulong LodTablePointer,
    int MeshLinkPointer)
{
    /// <summary>
    /// Opaque serialized entity fields at offsets 0x90 and 0x98. The named
    /// DL1 runtime queries these fields as entity bone-index data. They are
    /// preserved verbatim for audit without inventing pointer semantics; the
    /// no-BlendIndices draw path does not consult them.
    /// </summary>
    public ulong RawBoneIndexPointer0 { get; init; }

    public ulong RawBoneIndexPointer1 { get; init; }

    public bool IsPlainStaticRoot =>
        ParentIndex < 0 &&
        EntityType == CompactMeshEntityType.Mesh;
}

public sealed record CompactMeshDocument(
    int DeclaredEntityCount,
    int DeclaredRootCount,
    int EntityTableOffset,
    IReadOnlyList<CompactMeshEntity> Entities,
    IReadOnlyList<CompactMeshDiagnostic> Diagnostics)
{
    public bool IsStructurallyValid =>
        Diagnostics.All(static diagnostic =>
            diagnostic.Severity != CompactMeshDiagnosticSeverity.Error);

    public int ObservedRootCount =>
        Entities.Count(static entity => entity.ParentIndex < 0);

    public int AnimationEntityCountCandidate
    {
        get
        {
            CompactMeshEntity? firstRootSkin = Entities.FirstOrDefault(
                static entity =>
                    entity.EntityType.HasFlag(
                        CompactMeshEntityType.SkinnedMesh) &&
                    entity.ParentIndex < 0);
            return firstRootSkin is null || firstRootSkin.Index == 0
                ? Entities.Count
                : firstRootSkin.Index;
        }
    }

    public IReadOnlyList<CompactMeshEntity> Bones =>
        Entities
            .Where(static entity =>
                entity.EntityType.HasFlag(CompactMeshEntityType.Bone))
            .ToArray();

    public IReadOnlyList<CompactMeshEntity> Helpers =>
        Entities
            .Where(static entity =>
                entity.EntityType.HasFlag(CompactMeshEntityType.Helper))
            .ToArray();

    public IReadOnlyList<CompactMeshEntity> SkinnedMeshes =>
        Entities
            .Where(static entity =>
                entity.EntityType.HasFlag(CompactMeshEntityType.SkinnedMesh))
            .ToArray();

    public IReadOnlyList<IReadOnlyList<int>> BuildChildIndex()
    {
        List<int>[] children = Enumerable
            .Range(0, Entities.Count)
            .Select(static _ => new List<int>())
            .ToArray();
        foreach (CompactMeshEntity entity in Entities)
        {
            if (entity.ParentIndex >= 0 &&
                entity.ParentIndex < Entities.Count)
            {
                children[entity.ParentIndex].Add(entity.Index);
            }
        }

        return children;
    }

    public IReadOnlyList<CompactMatrix3x4> ReconstructGlobalMatrices()
    {
        if (!IsStructurallyValid)
        {
            throw new InvalidDataException(
                "Cannot reconstruct matrices for an invalid compact hierarchy.");
        }

        CompactMatrix3x4?[] resolved =
            new CompactMatrix3x4?[Entities.Count];
        bool[] visiting = new bool[Entities.Count];

        CompactMatrix3x4 Resolve(int index)
        {
            if (resolved[index] is { } matrix)
            {
                return matrix;
            }

            if (visiting[index])
            {
                throw new InvalidDataException(
                    $"Compact hierarchy cycle at entity {index}.");
            }

            visiting[index] = true;
            CompactMeshEntity entity = Entities[index];
            matrix = entity.ParentIndex < 0
                ? entity.LocalMatrix
                : CompactMatrix3x4.Multiply(
                    Resolve(entity.ParentIndex),
                    entity.LocalMatrix);
            visiting[index] = false;
            resolved[index] = matrix;
            return matrix;
        }

        CompactMatrix3x4[] result =
            new CompactMatrix3x4[Entities.Count];
        for (int index = 0; index < result.Length; index++)
        {
            result[index] = Resolve(index);
        }

        return result;
    }
}
