using System.Text.Json.Serialization;
using ReAnimated.Core.Mathematics;

namespace ReAnimated.Core.Domain;

public enum AttachmentScope
{
    AuthoredExportable,
    PreviewOnly,
}

/// <summary>
/// Binds a referenced prop, weapon, or diagnostic mesh to a target bone.
/// The binding contains identity and transforms only, never retail asset bytes.
/// </summary>
public sealed record AttachmentBinding
{
    public const int MaximumPerAnimation = 32;

    public const int MaximumNameLength = 128;

    [JsonConstructor]
    public AttachmentBinding(
        Guid id,
        Guid assetId,
        string name,
        int parentBoneIndex,
        TransformTRS localOffset,
        AttachmentScope scope,
        string? parentBoneName = null)
    {
        if (id == Guid.Empty)
        {
            throw new ArgumentException("Attachment identifiers cannot be empty.", nameof(id));
        }

        if (assetId == Guid.Empty)
        {
            throw new ArgumentException("Attachment asset identifiers cannot be empty.", nameof(assetId));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (name.Length > MaximumNameLength)
        {
            throw new ArgumentException(
                $"Attachment names cannot exceed {MaximumNameLength} characters.",
                nameof(name));
        }

        ArgumentOutOfRangeException.ThrowIfNegative(parentBoneIndex);
        if (!localOffset.IsFinite)
        {
            throw new ArgumentException("Attachment offsets must be finite.", nameof(localOffset));
        }

        if (Math.Abs(localOffset.Scale.X) <= 1e-12 ||
            Math.Abs(localOffset.Scale.Y) <= 1e-12 ||
            Math.Abs(localOffset.Scale.Z) <= 1e-12)
        {
            throw new ArgumentException(
                "Attachment offsets must have non-zero scale on every axis.",
                nameof(localOffset));
        }

        if (parentBoneName is { Length: > 0 } &&
            string.IsNullOrWhiteSpace(parentBoneName))
        {
            throw new ArgumentException(
                "Attachment parent-bone names cannot contain only whitespace.",
                nameof(parentBoneName));
        }

        Id = id;
        AssetId = assetId;
        Name = name;
        ParentBoneIndex = parentBoneIndex;
        ParentBoneName = parentBoneName;
        LocalOffset = localOffset.Normalized();
        Scope = scope;
    }

    public Guid Id { get; }

    public Guid AssetId { get; }

    public string Name { get; }

    public int ParentBoneIndex { get; }

    /// <summary>
    /// Optional stable guard for the numeric parent index. New C# authoring
    /// workflows always populate it; early schema-1 documents that only
    /// carried an index remain readable.
    /// </summary>
    public string? ParentBoneName { get; }

    public TransformTRS LocalOffset { get; }

    public AttachmentScope Scope { get; }
}
