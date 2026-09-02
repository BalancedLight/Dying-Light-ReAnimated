using System.Collections.Immutable;
using ReAnimated.Codecs.CompactMesh;
using ReAnimated.Core.Domain;
using ReAnimated.Core.Mathematics;
using ReAnimated.Core.ModelAuthoring;

namespace ReAnimated.DL1.Assets.Meshes;

/// <summary>
/// Extracts a <see cref="Dl1RigTemplate"/> from a decoded retail compact
/// hierarchy.
/// </summary>
/// <remarks>
/// <para>
/// The template is the skeleton half of a retail mesh: names, parents, entity
/// kinds, and rest transforms. Geometry, skin palettes, materials, textures,
/// morph deltas, and every other retail payload are deliberately not read.
/// </para>
/// <para>
/// Entity selection reuses the same animation-entity boundary the authoring
/// rig factory uses, so a template covers exactly the rows a DL1 animation can
/// address. On the validated Windows 1.55 build, both <c>player_1_tpp</c> and
/// <c>player_1_fpp</c> yield the identical 87-entity skeleton; the two
/// resources differ only in their skinned-mesh rows, which are excluded here.
/// </para>
/// </remarks>
public static class Dl1RigTemplateFactory
{
    /// <summary>The bounded profile name for the stock player skeleton.</summary>
    public const string PlayerProfileName = "player";

    /// <summary>
    /// The retail resource the <see cref="PlayerProfileName"/> profile is
    /// extracted from. The FPP resource shares this skeleton exactly.
    /// </summary>
    public const string PlayerSourceResourceName = "player_1_tpp";

    private const int MaximumTemplateEntities = 4_096;

    /// <summary>
    /// Builds a template, or returns <see langword="null"/> when the decoded
    /// hierarchy carries no usable animation skeleton. Structural problems in
    /// the retail data are reported as a refusal rather than a fabricated rig.
    /// </summary>
    public static Dl1RigTemplate? TryCreate(
        string profileName,
        string resourceName,
        string sourceFingerprint,
        CompactMeshDocument hierarchy)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(profileName);
        ArgumentException.ThrowIfNullOrWhiteSpace(resourceName);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceFingerprint);
        ArgumentNullException.ThrowIfNull(hierarchy);

        if (!hierarchy.IsStructurallyValid)
        {
            return null;
        }

        int entityCount = Math.Clamp(
            hierarchy.AnimationEntityCountCandidate,
            0,
            hierarchy.Entities.Count);
        if (entityCount is <= 0 or > MaximumTemplateEntities)
        {
            return null;
        }

        bool hasRigEntity = false;
        for (int index = 0; index < entityCount; index++)
        {
            CompactMeshEntity entity = hierarchy.Entities[index];
            if (entity.ParentIndex >= entityCount)
            {
                return null;
            }

            if (entity.EntityType.HasFlag(CompactMeshEntityType.Bone) ||
                entity.EntityType.HasFlag(CompactMeshEntityType.Helper))
            {
                hasRigEntity = true;
            }
        }

        if (!hasRigEntity)
        {
            return null;
        }

        var entities =
            ImmutableArray.CreateBuilder<Dl1RigTemplateEntity>(entityCount);
        var globals = new TransformMatrix[entityCount];
        for (int index = 0; index < entityCount; index++)
        {
            CompactMeshEntity entity = hierarchy.Entities[index];
            TransformMatrix local = ToTransformMatrix(entity.LocalMatrix);
            TransformMatrix global = entity.ParentIndex < 0
                ? local
                : globals[entity.ParentIndex] * local;
            globals[index] = global;

            entities.Add(new Dl1RigTemplateEntity
            {
                Index = index,
                Name = entity.Name,
                ParentIndex = entity.ParentIndex,
                Kind = ClassifyKind(entity),
                IsDeform =
                    entity.EntityType.HasFlag(CompactMeshEntityType.Bone),
                LocalRestMatrix = local,
                GlobalRestMatrix = global,
                SemanticRole =
                    Dl1RigDefinitionFactory.TryResolveSemanticRole(entity.Name),
            });
        }

        try
        {
            return new Dl1RigTemplate(
                profileName,
                resourceName,
                sourceFingerprint,
                entities.MoveToImmutable());
        }
        catch (ArgumentException)
        {
            // A retail hierarchy that cannot satisfy the template invariants
            // stays unknown instead of producing a rig the solver would trust.
            return null;
        }
    }

    /// <summary>
    /// Mirrors <c>Dl1RigDefinitionFactory.ClassifyBone</c> so a template row
    /// and its authoring-rig counterpart never disagree about an entity kind.
    /// </summary>
    private static BoneKind ClassifyKind(CompactMeshEntity entity)
    {
        if (entity.ParentIndex < 0)
        {
            return BoneKind.Root;
        }

        if (string.Equals(entity.Name, "eyecamera", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(entity.Name, "refcamera", StringComparison.OrdinalIgnoreCase))
        {
            return BoneKind.Camera;
        }

        return entity.EntityType.HasFlag(CompactMeshEntityType.Helper)
            ? BoneKind.Helper
            : entity.EntityType.HasFlag(CompactMeshEntityType.Bone)
                ? BoneKind.Deform
                : BoneKind.Prop;
    }

    /// <summary>
    /// Widens a serialized 3x4 Chrome row set into the column-vector 4x4 the
    /// authoring core uses. The three serialized rows are preserved verbatim;
    /// no transpose happens here.
    /// </summary>
    private static TransformMatrix ToTransformMatrix(CompactMatrix3x4 matrix) =>
        new(
            matrix.M11, matrix.M12, matrix.M13, matrix.M14,
            matrix.M21, matrix.M22, matrix.M23, matrix.M24,
            matrix.M31, matrix.M32, matrix.M33, matrix.M34,
            0.0, 0.0, 0.0, 1.0);
}
