using System.Collections.Immutable;
using System.IO;
using System.Numerics;
using ReAnimated.Core.Domain;
using ReAnimated.Core.Mathematics;
using ReAnimated.Evaluation;

namespace ReAnimated.Renderer.D3D11;

/// <summary>
/// A decoded retail mesh retained outside the animation document. The document
/// carries only the project asset identity; no retail bytes are serialized.
/// </summary>
public sealed record AttachmentRenderAsset(
    Guid ProjectAssetId,
    string DisplayName,
    IReadOnlyList<MeshRenderData> Meshes,
    SkeletonRenderData? BindSkeleton);

public sealed record AttachmentRenderDiagnostic(
    string Code,
    Guid BindingId,
    string Message);

public sealed record AttachmentSceneComposition(
    ImmutableArray<MeshRenderData> Meshes,
    ImmutableArray<AttachmentRenderDiagnostic> Diagnostics);

/// <summary>
/// Produces one immutable D3D scene handoff for the animated target plus rigid
/// prop/weapon attachments. Independently skinned attachment assets are baked
/// once in their decoded bind pose and then follow the evaluated parent as a
/// rigid object. This prevents an attachment skeleton from being interpreted as
/// the target actor's skin palette.
/// </summary>
public static class AttachmentSceneComposer
{
    public const int MaximumDrawSurfaces = 8_192;

    public static AttachmentSceneComposition Compose(
        IReadOnlyList<MeshRenderData> targetMeshes,
        IReadOnlyList<EvaluatedAttachment> evaluatedAttachments,
        IReadOnlyDictionary<Guid, AttachmentRenderAsset> decodedAssets,
        TransformMatrix? actorWorldTransform = null)
    {
        ArgumentNullException.ThrowIfNull(targetMeshes);
        ArgumentNullException.ThrowIfNull(evaluatedAttachments);
        ArgumentNullException.ThrowIfNull(decodedAssets);

        var meshes = ImmutableArray.CreateBuilder<MeshRenderData>();
        var diagnostics =
            ImmutableArray.CreateBuilder<AttachmentRenderDiagnostic>();
        foreach (MeshRenderData targetMesh in
                 targetMeshes.Take(MaximumDrawSurfaces))
        {
            meshes.Add(targetMesh);
        }

        if (targetMeshes.Count > MaximumDrawSurfaces)
        {
            diagnostics.Add(
                new(
                    "attachment_scene_surface_limit",
                    Guid.Empty,
                    $"The animated target alone exceeds the bounded {MaximumDrawSurfaces:N0}-surface scene limit; attachments were not published."));
            return new AttachmentSceneComposition(
                meshes.ToImmutable(),
                diagnostics.ToImmutable());
        }

        foreach (EvaluatedAttachment attachment in evaluatedAttachments
                     .Take(AttachmentBinding.MaximumPerAnimation))
        {
            if (!decodedAssets.TryGetValue(
                    attachment.AssetId,
                    out AttachmentRenderAsset? asset))
            {
                diagnostics.Add(
                    new(
                        "attachment_asset_unresolved",
                        attachment.BindingId,
                        $"Attachment '{attachment.Name}' could not render because project asset {attachment.AssetId} is not decoded for this DL1 installation."));
                continue;
            }

            TransformMatrix actorWorld =
                actorWorldTransform ?? TransformMatrix.Identity;
            Matrix4x4 attachmentWorld =
                ToSystemMatrix(
                    actorWorld * attachment.WorldTransform);
            if (!IsFinite(attachmentWorld))
            {
                diagnostics.Add(
                    new(
                        "attachment_transform_invalid",
                        attachment.BindingId,
                        $"Attachment '{attachment.Name}' evaluated to a non-finite world transform."));
                continue;
            }

            int published = 0;
            foreach (MeshRenderData sourceMesh in asset.Meshes)
            {
                if (meshes.Count >= MaximumDrawSurfaces)
                {
                    diagnostics.Add(
                        new(
                            "attachment_scene_surface_limit",
                            attachment.BindingId,
                            $"Attachment '{attachment.Name}' was truncated at the bounded {MaximumDrawSurfaces:N0}-surface scene limit."));
                    break;
                }

                try
                {
                    meshes.Add(CreateRigidMesh(
                        sourceMesh,
                        asset.BindSkeleton,
                        attachment,
                        attachmentWorld));
                    published++;
                }
                catch (Exception exception) when (
                    exception is ArgumentException or
                    InvalidDataException or
                    InvalidOperationException or
                    OverflowException)
                {
                    diagnostics.Add(
                        new(
                            "attachment_mesh_invalid",
                            attachment.BindingId,
                            $"Attachment '{attachment.Name}' surface '{sourceMesh.Id}' was skipped: {exception.Message}"));
                }
            }

            if (published == 0)
            {
                diagnostics.Add(
                    new(
                        "attachment_no_renderable_surfaces",
                        attachment.BindingId,
                        $"Attachment '{attachment.Name}' has no renderable decoded surfaces."));
            }
        }

        if (evaluatedAttachments.Count >
            AttachmentBinding.MaximumPerAnimation)
        {
            diagnostics.Add(
                new(
                    "attachment_count_limit",
                    Guid.Empty,
                    $"Only the first {AttachmentBinding.MaximumPerAnimation} attachments were considered."));
        }

        return new AttachmentSceneComposition(
            meshes.ToImmutable(),
            diagnostics.ToImmutable());
    }

    private static MeshRenderData CreateRigidMesh(
        MeshRenderData source,
        SkeletonRenderData? bindSkeleton,
        EvaluatedAttachment attachment,
        Matrix4x4 attachmentWorld)
    {
        string id =
            $"attachment/{attachment.BindingId:N}/{source.Id}";
        if (!source.IsSkinned)
        {
            if (!RenderMeshValidation.TryValidate(
                    source,
                    skeleton: null,
                    out string? validationError))
            {
                throw new InvalidDataException(validationError);
            }

            return source with
            {
                Id = id,
                LocalToWorld =
                    source.LocalToWorld * attachmentWorld,
                InverseBindMatrices =
                    ReadOnlyMemory<Matrix4x4>.Empty,
                SkinBoneIndices =
                    ReadOnlyMemory<int>.Empty,
                IsSkinned = false,
                IsSelected = false,
                MorphTargets =
                    Array.Empty<MorphTargetRenderData>(),
            };
        }

        if (bindSkeleton is null)
        {
            throw new InvalidDataException(
                $"Skinned attachment asset surface '{source.Id}' has no decoded bind skeleton.");
        }

        CpuDeformedVertex[] bindVertices =
            CpuMeshDeformationEvaluator.Evaluate(
                source,
                bindSkeleton,
                Array.Empty<MorphWeight>());
        MeshVertex[] rigidVertices =
            bindVertices.Select(static vertex =>
                new MeshVertex(
                    vertex.Position,
                    vertex.Normal,
                    vertex.TextureCoordinate,
                    Vector4.Zero,
                    Vector4.Zero))
                .ToArray();
        return new MeshRenderData(
            id,
            rigidVertices,
            source.Indices,
            attachmentWorld,
            ReadOnlyMemory<Matrix4x4>.Empty,
            IsSkinned: false)
        {
            Tint = source.Tint,
            IsSelected = false,
            MorphTargets =
                Array.Empty<MorphTargetRenderData>(),
            BaseColorTexture = source.BaseColorTexture,
            ProjectionRole = source.ProjectionRole,
        };
    }

    private static Matrix4x4 ToSystemMatrix(
        in TransformMatrix matrix) =>
        new(
            checked((float)matrix.M11),
            checked((float)matrix.M21),
            checked((float)matrix.M31),
            checked((float)matrix.M41),
            checked((float)matrix.M12),
            checked((float)matrix.M22),
            checked((float)matrix.M32),
            checked((float)matrix.M42),
            checked((float)matrix.M13),
            checked((float)matrix.M23),
            checked((float)matrix.M33),
            checked((float)matrix.M43),
            checked((float)matrix.M14),
            checked((float)matrix.M24),
            checked((float)matrix.M34),
            checked((float)matrix.M44));

    private static bool IsFinite(in Matrix4x4 matrix) =>
        float.IsFinite(matrix.M11) &&
        float.IsFinite(matrix.M12) &&
        float.IsFinite(matrix.M13) &&
        float.IsFinite(matrix.M14) &&
        float.IsFinite(matrix.M21) &&
        float.IsFinite(matrix.M22) &&
        float.IsFinite(matrix.M23) &&
        float.IsFinite(matrix.M24) &&
        float.IsFinite(matrix.M31) &&
        float.IsFinite(matrix.M32) &&
        float.IsFinite(matrix.M33) &&
        float.IsFinite(matrix.M34) &&
        float.IsFinite(matrix.M41) &&
        float.IsFinite(matrix.M42) &&
        float.IsFinite(matrix.M43) &&
        float.IsFinite(matrix.M44);
}
