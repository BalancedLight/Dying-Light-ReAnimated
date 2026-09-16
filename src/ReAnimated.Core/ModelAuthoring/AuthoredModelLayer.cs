using System.Collections.Immutable;
using System.Text;
using ReAnimated.Core.Mathematics;

namespace ReAnimated.Core.ModelAuthoring;

/// <summary>
/// Source-linked edits layered over an immutable model source. The layer stores
/// decisions and sparse edits only; it never duplicates base geometry or
/// triangles.
/// </summary>
public sealed record AuthoredModelLayer
{
    public const int CurrentVersion = 1;
    public const int MaximumSourceControlPoints = 4_000_000;
    public const int MaximumPolygonVertices = 8_000_000;
    public const int MaximumLayerEdits = 8_000_000;
    public const int MaximumStringBytes = 4_096;
    public const int MaximumBones = 4_096;
    public const int MaximumComponents = 65_536;
    public const int MaximumMorphsPerComponent = 4_096;

    public int Version { get; init; } = CurrentVersion;
    public string SourceSha256 { get; init; } = string.Empty;
    public string SourceGeometryFingerprint { get; init; } = string.Empty;
    public string TargetRigSignature { get; init; } = string.Empty;
    public ImmutableArray<AuthoredBoneIdentity> Bones { get; init; } = [];
    public ImmutableArray<AuthoredComponentEdits> Components { get; init; } = [];

    public void Validate()
    {
        if (Version != CurrentVersion)
        {
            throw new ArgumentException($"Only authored model layer version {CurrentVersion} is supported.", nameof(Version));
        }

        ValidateHash(SourceSha256, nameof(SourceSha256));
        ValidateHash(SourceGeometryFingerprint, nameof(SourceGeometryFingerprint));
        ValidateHash(TargetRigSignature, nameof(TargetRigSignature));
        if (Bones.IsDefault || Bones.Length > MaximumBones || Components.IsDefault || Components.Length > MaximumComponents)
        {
            throw new ArgumentException("Authored model layer collections are missing or exceed their bounds.");
        }

        var boneIds = new HashSet<Guid>();
        var boneNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (AuthoredBoneIdentity bone in Bones)
        {
            bone.Validate();
            if (!boneIds.Add(bone.Id) || !boneNames.Add(bone.Name))
            {
                throw new ArgumentException("Authored bone IDs and names must be unique.", nameof(Bones));
            }
        }

        var componentIds = new HashSet<string>(StringComparer.Ordinal);
        long totalEdits = 0;
        foreach (AuthoredComponentEdits component in Components)
        {
            component.Validate(boneIds, ref totalEdits);
            if (!componentIds.Add(component.ComponentId))
            {
                throw new ArgumentException(
                    $"Authored component '{component.ComponentId}' is duplicated.",
                    nameof(Components));
            }
        }

        if (totalEdits > MaximumLayerEdits)
        {
            throw new ArgumentException("Authored model layer edits exceed the bounded layer limit.", nameof(Components));
        }
    }

    internal static void ValidateHash(string value, string parameterName)
    {
        if (value is null || value.Length != 64 || value.Any(static character => !char.IsAsciiHexDigit(character)))
        {
            throw new ArgumentException("The value must be an exact 64-character hexadecimal SHA-256 string.", parameterName);
        }
    }

    internal static void ValidateText(string value, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        try
        {
            if (new UTF8Encoding(false, true).GetByteCount(value) > MaximumStringBytes)
            {
                throw new ArgumentOutOfRangeException(parameterName, "Authored layer strings exceed the UTF-8 byte limit.");
            }
        }
        catch (DecoderFallbackException exception)
        {
            throw new ArgumentException("Authored layer text contains an invalid UTF-16 sequence.", parameterName, exception);
        }
    }
}

public readonly record struct AuthoredBoneIdentity(Guid Id, string Name)
{
    internal void Validate()
    {
        if (Id == Guid.Empty)
        {
            throw new ArgumentException("Authored bone IDs cannot be empty.");
        }

        AuthoredModelLayer.ValidateText(Name, nameof(Name));
    }
}

public readonly record struct AuthoredSkinInfluence(Guid BoneId, double Weight);

public readonly record struct AuthoredInverseBind(Guid BoneId, TransformMatrix Matrix)
{
    internal void Validate(IReadOnlySet<Guid> boneIds)
    {
        if (BoneId == Guid.Empty || !boneIds.Contains(BoneId) ||
            !Matrix.IsFinite ||
            Math.Abs(Matrix.M41) > 1e-12 ||
            Math.Abs(Matrix.M42) > 1e-12 ||
            Math.Abs(Matrix.M43) > 1e-12 ||
            Math.Abs(Matrix.M44 - 1.0) > 1e-12 ||
            !double.IsFinite(Matrix.LinearDeterminant) ||
            Matrix.LinearDeterminant == 0.0)
        {
            throw new ArgumentException("Authored inverse binds must reference declared bones and be finite nonsingular affine matrices.");
        }
    }
}

public sealed record AuthoredPointEdit
{
    public AuthoredPointEdit()
    {
    }

    public AuthoredPointEdit(
        int controlPointIndex,
        Vector3D positionDelta,
        bool replaceWeights,
        ImmutableArray<AuthoredSkinInfluence> weights)
    {
        ControlPointIndex = controlPointIndex;
        PositionDelta = positionDelta;
        ReplaceWeights = replaceWeights;
        Weights = weights;
    }

    public int ControlPointIndex { get; init; }
    public Vector3D PositionDelta { get; init; }
    public bool ReplaceWeights { get; init; }
    public ImmutableArray<AuthoredSkinInfluence> Weights { get; init; } = [];

    internal void Validate(IReadOnlySet<Guid> boneIds)
    {
        if (ControlPointIndex < 0 || !PositionDelta.IsFinite || Weights.IsDefault)
        {
            throw new ArgumentException("Authored point edits contain an invalid index, delta, or weight collection.");
        }

        if (!ReplaceWeights && !Weights.IsEmpty)
        {
            throw new ArgumentException("A point edit that does not replace weights must have an empty weight collection.");
        }

        if (Weights.Length > 256)
        {
            throw new ArgumentOutOfRangeException(nameof(boneIds), "An authored point may contain at most 256 weights.");
        }

        var seen = new HashSet<Guid>();
        double total = 0.0;
        foreach (AuthoredSkinInfluence influence in Weights)
        {
            if (influence.BoneId == Guid.Empty || !boneIds.Contains(influence.BoneId) ||
                !seen.Add(influence.BoneId) || !double.IsFinite(influence.Weight) || influence.Weight <= 0.0)
            {
                throw new ArgumentException("Authored skin influences must reference declared bones with unique positive finite weights.");
            }

            total += influence.Weight;
        }

        if (!Weights.IsEmpty &&
            (!double.IsFinite(total) || Math.Abs(total - 1.0) > 1e-9))
        {
            throw new ArgumentException("Replacement authored skin weights must sum to one within 1e-9.");
        }
    }
}

public readonly record struct AuthoredVectorDelta(int Index, Vector3D Delta)
{
    internal void Validate()
    {
        if (Index < 0 || !Delta.IsFinite)
        {
            throw new ArgumentException("Authored vector deltas must have nonnegative indices and finite values.");
        }
    }
}

public sealed record AuthoredMorphEdits
{
    public uint DescriptorHash { get; init; }
    public ImmutableArray<AuthoredVectorDelta> PositionDeltas { get; init; } = [];
    public ImmutableArray<AuthoredVectorDelta> NormalDeltas { get; init; } = [];
    /// <summary>
    /// Null retains the source normal-array presence, true explicitly retains
    /// or creates the array (including an empty/all-zero array), and false
    /// explicitly removes it.
    /// </summary>
    public bool? HasNormalDeltas { get; init; }

    public AuthoredMorphEdits()
    {
    }

    public AuthoredMorphEdits(
        uint descriptorHash,
        ImmutableArray<AuthoredVectorDelta> positionDeltas,
        ImmutableArray<AuthoredVectorDelta> normalDeltas,
        bool? hasNormalDeltas = null)
    {
        DescriptorHash = descriptorHash;
        PositionDeltas = positionDeltas;
        NormalDeltas = normalDeltas;
        HasNormalDeltas = hasNormalDeltas;
    }

    internal void Validate(int controlPointCount, int polygonVertexCount, ref long totalEdits)
    {
        if (PositionDeltas.IsDefault || NormalDeltas.IsDefault)
        {
            throw new ArgumentException("Authored morph delta collections must be initialized.");
        }

        if (HasNormalDeltas == false && !NormalDeltas.IsEmpty)
        {
            throw new ArgumentException("A morph edit that removes normal deltas must have an empty normal-delta collection.");
        }

        ValidateDeltas(PositionDeltas, controlPointCount, ref totalEdits);
        ValidateDeltas(NormalDeltas, polygonVertexCount, ref totalEdits);
    }

    private static void ValidateDeltas(
        ImmutableArray<AuthoredVectorDelta> deltas,
        int maximumIndex,
        ref long totalEdits)
    {
        totalEdits = checked(totalEdits + deltas.Length);
        var seen = new HashSet<int>();
        foreach (AuthoredVectorDelta delta in deltas)
        {
            delta.Validate();
            if (delta.Index >= maximumIndex || !seen.Add(delta.Index))
            {
                throw new ArgumentException("Authored morph delta indices must be unique and within the source control-point inventory.");
            }
        }
    }
}

public sealed record AuthoredComponentEdits
{
    public AuthoredComponentEdits()
    {
    }

    public AuthoredComponentEdits(
        string componentId,
        int controlPointCount,
        int polygonVertexCount,
        ImmutableArray<AuthoredPointEdit> points,
        ImmutableArray<AuthoredVectorDelta> normals,
        ImmutableArray<AuthoredMorphEdits> morphs)
    {
        ComponentId = componentId;
        ControlPointCount = controlPointCount;
        PolygonVertexCount = polygonVertexCount;
        Points = points;
        Normals = normals;
        Morphs = morphs;
    }

    public AuthoredComponentEdits(
        string componentId,
        int controlPointCount,
        int polygonVertexCount,
        ImmutableArray<AuthoredPointEdit> points,
        ImmutableArray<AuthoredVectorDelta> normals,
        ImmutableArray<AuthoredMorphEdits> morphs,
        ImmutableArray<AuthoredInverseBind> inverseBinds)
        : this(componentId, controlPointCount, polygonVertexCount, points, normals, morphs)
    {
        InverseBinds = inverseBinds;
    }

    public string ComponentId { get; init; } = string.Empty;
    public int ControlPointCount { get; init; }
    public int PolygonVertexCount { get; init; }
    public ImmutableArray<AuthoredPointEdit> Points { get; init; } = [];
    public ImmutableArray<AuthoredVectorDelta> Normals { get; init; } = [];
    public ImmutableArray<AuthoredMorphEdits> Morphs { get; init; } = [];
    public ImmutableArray<AuthoredInverseBind> InverseBinds { get; init; } = [];

    internal void Validate(IReadOnlySet<Guid> boneIds, ref long totalEdits)
    {
        AuthoredModelLayer.ValidateText(ComponentId, nameof(ComponentId));
        if (ControlPointCount < 0 || ControlPointCount > AuthoredModelLayer.MaximumSourceControlPoints ||
            PolygonVertexCount < 0 || PolygonVertexCount > AuthoredModelLayer.MaximumPolygonVertices ||
            Points.IsDefault || Normals.IsDefault || Morphs.IsDefault || InverseBinds.IsDefault ||
            Points.Length > ControlPointCount || Normals.Length > PolygonVertexCount ||
            Morphs.Length > AuthoredModelLayer.MaximumMorphsPerComponent ||
            InverseBinds.Length > AuthoredModelLayer.MaximumBones)
        {
            throw new ArgumentException("Authored component edit counts or collections are invalid.", nameof(boneIds));
        }

        var pointIndices = new HashSet<int>();
        foreach (AuthoredPointEdit point in Points)
        {
            point.Validate(boneIds);
            if (point.ControlPointIndex >= ControlPointCount || !pointIndices.Add(point.ControlPointIndex))
            {
                throw new ArgumentException("Authored point edit indices must be unique and within the source control-point inventory.");
            }

            totalEdits = checked(totalEdits + point.Weights.Length);
        }

        totalEdits = checked(totalEdits + Points.Length);
        ValidateComponentDeltas(Normals, PolygonVertexCount, ref totalEdits);
        var inverseBindIds = new HashSet<Guid>();
        foreach (AuthoredInverseBind inverseBind in InverseBinds)
        {
            inverseBind.Validate(boneIds);
            if (!inverseBindIds.Add(inverseBind.BoneId))
            {
                throw new ArgumentException("Authored inverse-bind bone IDs must be unique.");
            }
        }

        var descriptors = new HashSet<uint>();
        foreach (AuthoredMorphEdits morph in Morphs)
        {
            if (!descriptors.Add(morph.DescriptorHash))
            {
                throw new ArgumentException("Authored morph descriptor hashes must be unique.");
            }

            morph.Validate(ControlPointCount, PolygonVertexCount, ref totalEdits);
        }
    }

    private static void ValidateComponentDeltas(
        ImmutableArray<AuthoredVectorDelta> deltas,
        int maximumIndex,
        ref long totalEdits)
    {
        var indices = new HashSet<int>();
        foreach (AuthoredVectorDelta delta in deltas)
        {
            delta.Validate();
            if (delta.Index >= maximumIndex || !indices.Add(delta.Index))
            {
                throw new ArgumentException("Authored normal delta indices must be unique and within the source polygon-vertex inventory.");
            }
        }

        totalEdits = checked(totalEdits + deltas.Length);
    }
}
