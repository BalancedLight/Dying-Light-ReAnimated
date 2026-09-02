using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using ReAnimated.Core.Domain;
using ReAnimated.Core.Mathematics;

namespace ReAnimated.Core.ModelAuthoring;

/// <summary>
/// One entity of a Dying Light target skeleton, extracted from a decoded
/// retail hierarchy. The template carries only skeleton shape and naming; it
/// never retains geometry, textures, or any other retail payload.
/// </summary>
public sealed record Dl1RigTemplateEntity
{
    public required int Index { get; init; }

    public required string Name { get; init; }

    public required int ParentIndex { get; init; }

    public required BoneKind Kind { get; init; }

    /// <summary>
    /// True when the retail hierarchy marks this entity as a deforming bone
    /// rather than an unweighted helper, camera, or prop pivot.
    /// </summary>
    public required bool IsDeform { get; init; }

    public required TransformMatrix LocalRestMatrix { get; init; }

    public required TransformMatrix GlobalRestMatrix { get; init; }

    /// <summary>
    /// The shared humanoid role string when the entity is one of the bounded
    /// anchors, otherwise <see langword="null"/>. Role spellings match
    /// <c>Dl1RigDefinitionFactory</c> exactly because they are hashed into
    /// persisted rig signatures.
    /// </summary>
    public string? SemanticRole { get; init; }
}

/// <summary>
/// An immutable Dying Light target skeleton used as the conformance goal when
/// converting an arbitrary rigged model into a DL1-capable model.
/// </summary>
/// <remarks>
/// A template is derived at runtime from the user's own installed game and
/// cached machine-locally. It is never committed to the repository or embedded
/// in a release, matching the retail-data boundary the rest of the project
/// keeps.
/// </remarks>
public sealed class Dl1RigTemplate
{
    private const double MatrixTolerance = 5e-5;

    private readonly ImmutableDictionary<string, int> _indexByName;
    private readonly ImmutableDictionary<string, int> _indexByRole;

    public Dl1RigTemplate(
        string profileName,
        string sourceResourceName,
        string sourceFingerprint,
        IEnumerable<Dl1RigTemplateEntity> entities)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(profileName);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceResourceName);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceFingerprint);
        ArgumentNullException.ThrowIfNull(entities);

        ImmutableArray<Dl1RigTemplateEntity> ordered = entities.ToImmutableArray();
        Validate(ordered);

        ProfileName = profileName;
        SourceResourceName = sourceResourceName;
        SourceFingerprint = sourceFingerprint;
        Entities = ordered;
        _indexByName = ordered.ToImmutableDictionary(
            static entity => entity.Name,
            static entity => entity.Index,
            StringComparer.OrdinalIgnoreCase);
        _indexByRole = ordered
            .Where(static entity => !string.IsNullOrEmpty(entity.SemanticRole))
            .ToImmutableDictionary(
                static entity => entity.SemanticRole!,
                static entity => entity.Index,
                StringComparer.Ordinal);
        ReferenceHeight = ComputeReferenceHeight(ordered);
        TemplateId = string.Concat(
            "dl1rig:",
            HashTemplate(profileName, ordered).AsSpan(0, 24));
    }

    public string TemplateId { get; }

    public string ProfileName { get; }

    /// <summary>The retail resource the template was extracted from.</summary>
    public string SourceResourceName { get; }

    /// <summary>
    /// The installed-build fingerprint the extraction was taken under. A
    /// template from a different build does not inherit an earlier control.
    /// </summary>
    public string SourceFingerprint { get; }

    public ImmutableArray<Dl1RigTemplateEntity> Entities { get; }

    public int EntityCount => Entities.Length;

    /// <summary>
    /// Vertical extent of the rest skeleton in template units (meters). This is
    /// the denominator of the uniform scale solve, not a mesh bound.
    /// </summary>
    public double ReferenceHeight { get; }

    public int DeformCount => Entities.Count(static entity => entity.IsDeform);

    public Dl1RigTemplateEntity this[int index] => Entities[index];

    public int? TryFindByName(string? name) =>
        !string.IsNullOrWhiteSpace(name) &&
        _indexByName.TryGetValue(name, out int index)
            ? index
            : null;

    public int? TryFindByRole(string? role) =>
        !string.IsNullOrWhiteSpace(role) &&
        _indexByRole.TryGetValue(role, out int index)
            ? index
            : null;

    /// <summary>
    /// Returns the same skeleton with every rest translation multiplied by
    /// <paramref name="scale"/>. Rotations are untouched, so bone directions
    /// and the Chrome local frames survive exactly while proportions stay
    /// self-consistent.
    /// </summary>
    public Dl1RigTemplate CreateScaled(double scale)
    {
        if (!double.IsFinite(scale) || scale <= 0.0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(scale),
                scale,
                "A rig template scale must be finite and positive.");
        }

        if (Math.Abs(scale - 1.0) <= double.Epsilon)
        {
            return this;
        }

        var scaled = ImmutableArray.CreateBuilder<Dl1RigTemplateEntity>(Entities.Length);
        foreach (Dl1RigTemplateEntity entity in Entities)
        {
            scaled.Add(entity with
            {
                LocalRestMatrix = WithScaledTranslation(entity.LocalRestMatrix, scale),
                GlobalRestMatrix = WithScaledTranslation(entity.GlobalRestMatrix, scale),
            });
        }

        return new Dl1RigTemplate(
            ProfileName,
            SourceResourceName,
            SourceFingerprint,
            scaled.MoveToImmutable());
    }

    /// <summary>
    /// Straight-line rest distance between an entity and its parent. This is
    /// the segment length the conformance solver preserves so that stock DL1
    /// animation keys do not stretch the converted mesh.
    /// </summary>
    public double GetRestSegmentLength(int index)
    {
        Dl1RigTemplateEntity entity = Entities[index];
        return entity.ParentIndex < 0
            ? 0.0
            : Vector3D.Distance(
                Entities[entity.ParentIndex].GlobalRestMatrix.Translation,
                entity.GlobalRestMatrix.Translation);
    }

    public ImmutableArray<int> BuildChildIndexes(int index)
    {
        var children = ImmutableArray.CreateBuilder<int>();
        foreach (Dl1RigTemplateEntity entity in Entities)
        {
            if (entity.ParentIndex == index)
            {
                children.Add(entity.Index);
            }
        }

        return children.ToImmutable();
    }

    private static TransformMatrix WithScaledTranslation(
        TransformMatrix value,
        double scale) =>
        new(
            value.M11, value.M12, value.M13, value.M14 * scale,
            value.M21, value.M22, value.M23, value.M24 * scale,
            value.M31, value.M32, value.M33, value.M34 * scale,
            value.M41, value.M42, value.M43, value.M44);

    private static double ComputeReferenceHeight(
        ImmutableArray<Dl1RigTemplateEntity> entities)
    {
        double low = double.PositiveInfinity;
        double high = double.NegativeInfinity;
        foreach (Dl1RigTemplateEntity entity in entities)
        {
            double y = entity.GlobalRestMatrix.Translation.Y;
            low = Math.Min(low, y);
            high = Math.Max(high, y);
        }

        double height = high - low;
        return double.IsFinite(height) && height > 0.0 ? height : 0.0;
    }

    private static void Validate(ImmutableArray<Dl1RigTemplateEntity> entities)
    {
        if (entities.IsEmpty)
        {
            throw new ArgumentException(
                "A DL1 rig template must contain at least one entity.",
                nameof(entities));
        }

        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int index = 0; index < entities.Length; index++)
        {
            Dl1RigTemplateEntity entity = entities[index];
            if (entity.Index != index)
            {
                throw new ArgumentException(
                    "DL1 rig template entities must use contiguous zero-based indexes.",
                    nameof(entities));
            }

            ArgumentException.ThrowIfNullOrWhiteSpace(entity.Name, nameof(entities));
            if (!names.Add(entity.Name))
            {
                throw new ArgumentException(
                    $"DL1 rig template entity '{entity.Name}' has a duplicate name.",
                    nameof(entities));
            }

            if (entity.ParentIndex < -1 || entity.ParentIndex >= index)
            {
                throw new ArgumentException(
                    $"DL1 rig template entity '{entity.Name}' is not in topological order.",
                    nameof(entities));
            }

            if (!entity.LocalRestMatrix.IsFinite ||
                !entity.GlobalRestMatrix.IsFinite ||
                Math.Abs(entity.LocalRestMatrix.LinearDeterminant) <= 1e-12)
            {
                throw new ArgumentException(
                    $"DL1 rig template entity '{entity.Name}' has an invalid rest matrix.",
                    nameof(entities));
            }

            TransformMatrix reconstructed = entity.ParentIndex < 0
                ? entity.LocalRestMatrix
                : entities[entity.ParentIndex].GlobalRestMatrix * entity.LocalRestMatrix;
            if (!reconstructed.NearlyEquals(entity.GlobalRestMatrix, MatrixTolerance))
            {
                throw new ArgumentException(
                    $"DL1 rig template entity '{entity.Name}' does not reconstruct from its parent and local rest.",
                    nameof(entities));
            }
        }
    }

    private static string HashTemplate(
        string profileName,
        ImmutableArray<Dl1RigTemplateEntity> entities)
    {
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true))
        {
            writer.Write(profileName);
            foreach (Dl1RigTemplateEntity entity in entities)
            {
                writer.Write(entity.Index);
                writer.Write(entity.Name.Normalize(NormalizationForm.FormKC).ToUpperInvariant());
                writer.Write(entity.ParentIndex);
                writer.Write((int)entity.Kind);
                writer.Write(entity.IsDeform);
                writer.Write(entity.SemanticRole ?? string.Empty);
                TransformMatrix local = entity.LocalRestMatrix;
                writer.Write(local.M11);
                writer.Write(local.M12);
                writer.Write(local.M13);
                writer.Write(local.M14);
                writer.Write(local.M21);
                writer.Write(local.M22);
                writer.Write(local.M23);
                writer.Write(local.M24);
                writer.Write(local.M31);
                writer.Write(local.M32);
                writer.Write(local.M33);
                writer.Write(local.M34);
            }
        }

        return Convert.ToHexString(
                SHA256.HashData(stream.GetBuffer().AsSpan(0, checked((int)stream.Length))))
            .ToLowerInvariant();
    }
}
