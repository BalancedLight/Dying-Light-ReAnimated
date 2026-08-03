using System.IO;
using System.Numerics;
using ReAnimated.Codecs.CompactMesh;
using ReAnimated.Core.Domain;
using ReAnimated.Core.Mathematics;
using ReAnimated.DL1.Assets.Materials;
using ReAnimated.DL1.Assets.Meshes;
using ReAnimated.Renderer.D3D11;

namespace ReAnimated.App.Infrastructure;

public sealed record Dl1MeshPreviewPayload(
    Dl1MeshData Source,
    IReadOnlyList<MeshRenderData> Meshes,
    SkeletonRenderData? Skeleton,
    IReadOnlyList<string> MorphChannelNames,
    IReadOnlyList<string> Diagnostics,
    string? ResourceSha256 = null,
    Dl1RetailMeshProfile? Profile = null);

public static class Dl1MeshPreviewAdapter
{
    internal const string ValidatedPlayer1FppResourceSha256 =
        "fcadbe6419cee4e5b8065e5c14e324b2576ee9015c5a9125896efa945250525c";

    private static readonly HashSet<string>
        ValidatedPlayer1FppOmittedSurfaceNames =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "beard",
            "flashlight",
            "hair",
            "kevin_shirt",
            "player_1_hand_l_tpp",
            "player_1_hand_r_tpp",
            "player_4_head",
            "player_4_head_fpp",
        };

    public static Dl1MeshPreviewPayload Convert(
        Dl1MeshData source,
        string? resourceSha256 = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        List<string> diagnostics = source.Diagnostics
            .Select(diagnostic =>
                $"{diagnostic.Code}: {diagnostic.Message}")
            .ToList();
        bool bindPoseOnlyPreview =
            CanPublishBindPoseOnlyPreview(source);
        bool hasPreviewBlockingDiagnostic =
            !source.IsStructurallyValid &&
            !bindPoseOnlyPreview;
        if (hasPreviewBlockingDiagnostic)
        {
            diagnostics.Add(
                "The mesh hierarchy is structurally invalid; no preview buffers were published.");
            return new Dl1MeshPreviewPayload(
                source,
                [],
                null,
                source.MorphTargets
                    .Select(static target => target.Name)
                    .ToArray(),
                diagnostics,
                resourceSha256);
        }

        if (bindPoseOnlyPreview)
        {
            diagnostics.Add(
                "One or more non-TRS hierarchy entities are outside every effective skin palette. Only the validated raw bind-pose mesh preview was published; retargeting, animation evaluation, and bone editing remain unavailable.");
        }

        IReadOnlyList<CompactMatrix3x4> compactWorldMatrices =
            source.Hierarchy.ReconstructGlobalMatrices();
        int skeletonEntityCount = GetSkeletonEntityCount(source.Hierarchy);
        bool usesEmbeddedAnimatedPropRig =
            UsesEmbeddedAnimatedPropRig(
                source,
                skeletonEntityCount);
        SkeletonRenderData? skeleton = skeletonEntityCount == 0
            ? null
            : BuildSkeleton(
                source.Hierarchy,
                compactWorldMatrices,
                skeletonEntityCount,
                usesEmbeddedAnimatedPropRig);
        if (usesEmbeddedAnimatedPropRig)
        {
            diagnostics.Add(
                "The hierarchy uses the exact embedded skinned-mesh animated-prop layout: palette-driving Bone rows remain available for skinning and selection, but ordinary overlay presentation uses compact gold prop/helper pivots instead of character deform diamonds.");
        }

        Matrix4x4[] inverseBindMatrices = skeleton is null
            ? []
            : BuildInverseBindMatrices(skeleton, diagnostics);

        Dictionary<int, int> selectedLodByEntity = source.Surfaces
            .GroupBy(static surface => surface.EntityIndex)
            .ToDictionary(
                static group => group.Key,
                static group => group.Min(static surface =>
                    surface.LodIndex));
        bool useValidatedPlayer1FppSelection =
            IsValidatedPlayer1FppSelection(
                source.ResourceName,
                resourceSha256);
        if (!useValidatedPlayer1FppSelection &&
            string.Equals(
                source.ResourceName,
                "player_1_fpp",
                StringComparison.OrdinalIgnoreCase))
        {
            diagnostics.Add(
                "This player_1_fpp resource does not match the validated DL1 1.55 content fingerprint. Default skin/variant visibility was not guessed; the full decoded surface set is shown and may not match the in-game FPP model.");
        }

        List<MeshRenderData> meshes = [];
        foreach (Dl1MeshSurface surface in source.Surfaces)
        {
            if (source.SkinHiddenEntityIndexes.Contains(
                    surface.EntityIndex))
            {
                diagnostics.Add(
                    $"Surface '{surface.Name}' was omitted because exact retail skin '{source.AppliedSkinName ?? "Default"}' hides hierarchy entity {surface.EntityIndex}.");
                continue;
            }

            if (surface.LodIndex !=
                selectedLodByEntity[surface.EntityIndex])
            {
                diagnostics.Add(
                    $"Surface '{surface.Name}' LOD {surface.LodIndex} was omitted; LOD {selectedLodByEntity[surface.EntityIndex]} is the highest-detail decoded preview LOD for entity {surface.EntityIndex}.");
                continue;
            }

            if (useValidatedPlayer1FppSelection &&
                ValidatedPlayer1FppOmittedSurfaceNames.Contains(
                    surface.Name))
            {
                diagnostics.Add(
                    $"Surface '{surface.Name}' was omitted by the content-fingerprinted DL1 1.55 player_1_fpp stock FPP authoring subset.");
                continue;
            }

            BuildSurfaceMeshes(
                source,
                surface,
                compactWorldMatrices,
                skeletonEntityCount,
                inverseBindMatrices,
                meshes,
                diagnostics);
        }

        if (bindPoseOnlyPreview &&
            !HasIdentityBindPalettes(meshes, skeleton))
        {
            meshes.Clear();
            diagnostics.Add(
                "The bind-pose-only preview did not reproduce identity skin palettes and was suppressed.");
        }

        return new Dl1MeshPreviewPayload(
            source,
            meshes,
            skeleton,
            source.MorphTargets
                .Select(static target => target.Name)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(static name => name, StringComparer.OrdinalIgnoreCase)
                .ToArray(),
            diagnostics,
            resourceSha256);
    }

    internal static bool IsValidatedPlayer1FppSelection(
        string resourceName,
        string? resourceSha256) =>
        string.Equals(
            resourceName,
            "player_1_fpp",
            StringComparison.OrdinalIgnoreCase) &&
        string.Equals(
            resourceSha256,
            ValidatedPlayer1FppResourceSha256,
            StringComparison.OrdinalIgnoreCase);

    internal static bool CanPublishBindPoseOnlyPreview(
        Dl1MeshData source)
    {
        if (source.Rig is not null ||
            !source.Hierarchy.IsStructurallyValid ||
            source.Surfaces.Count == 0)
        {
            return false;
        }

        Dl1MeshDiagnostic[] errors = source.Diagnostics
            .Where(static diagnostic =>
                diagnostic.Severity ==
                    Dl1MeshDiagnosticSeverity.Error)
            .ToArray();
        if (errors.Length != 1 ||
            errors[0].Code != "DL1MESH014" ||
            !errors[0].Message.Contains(
                "singular or sheared local transform",
                StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        int skeletonEntityCount =
            GetSkeletonEntityCount(source.Hierarchy);
        if (skeletonEntityCount <= 0)
        {
            return false;
        }

        HashSet<int> paletteEntityIndexes = source.Surfaces
            .SelectMany(static surface => surface.Submeshes)
            .Where(static submesh =>
                submesh.SkinBindingMode !=
                Dl1SkinBindingMode
                    .StaticEntityTransformIgnoredPalette)
            .SelectMany(static submesh =>
                submesh.BonePaletteEntityIndexes)
            .Select(static index => (int)index)
            .ToHashSet();
        if (paletteEntityIndexes.Count == 0)
        {
            return false;
        }

        int animationEntityCount = Math.Clamp(
            source.Hierarchy.AnimationEntityCountCandidate,
            0,
            source.Hierarchy.Entities.Count);
        HashSet<int> nonTrsEntityIndexes = [];
        for (int index = 0;
             index < animationEntityCount;
             index++)
        {
            CompactMatrix3x4 local =
                source.Hierarchy.Entities[index].LocalMatrix;
            var matrix = new TransformMatrix(
                local.M11,
                local.M12,
                local.M13,
                local.M14,
                local.M21,
                local.M22,
                local.M23,
                local.M24,
                local.M31,
                local.M32,
                local.M33,
                local.M34,
                0,
                0,
                0,
                1);
            try
            {
                _ = matrix.Decompose(1.0e-4);
            }
            catch (InvalidOperationException)
            {
                nonTrsEntityIndexes.Add(index);
            }
        }

        if (nonTrsEntityIndexes.Count == 0 ||
            nonTrsEntityIndexes.Overlaps(
                paletteEntityIndexes))
        {
            return false;
        }

        IReadOnlyList<CompactMatrix3x4> worldMatrices =
            source.Hierarchy.ReconstructGlobalMatrices();
        foreach (int index in paletteEntityIndexes)
        {
            if (index < 0 ||
                index >= skeletonEntityCount ||
                index >= worldMatrices.Count ||
                !worldMatrices[index].IsFinite ||
                !Matrix4x4.Invert(
                    ConvertMatrix(worldMatrices[index]),
                    out Matrix4x4 inverse) ||
                !IsFinite(inverse))
            {
                return false;
            }
        }

        return true;
    }

    internal static bool HasIdentityBindPalettes(
        IReadOnlyList<MeshRenderData> meshes,
        SkeletonRenderData? skeleton)
    {
        if (skeleton is null)
        {
            return false;
        }

        bool foundSkinnedMesh = false;
        bool foundEffectiveWeight = false;
        foreach (MeshRenderData mesh in
                 meshes.Where(static mesh => mesh.IsSkinned))
        {
            foundSkinnedMesh = true;
            Matrix4x4[] palette;
            try
            {
                palette = GpuSkinningPalette.Build(
                    mesh,
                    skeleton);
            }
            catch (Exception exception) when (
                exception is ArgumentException or
                InvalidDataException or
                InvalidOperationException or
                OverflowException)
            {
                return false;
            }

            foreach (MeshVertex vertex in mesh.Vertices.Span)
            {
                ReadOnlySpan<float> weights =
                [
                    vertex.BoneWeights.X,
                    vertex.BoneWeights.Y,
                    vertex.BoneWeights.Z,
                    vertex.BoneWeights.W,
                ];
                ReadOnlySpan<float> indexes =
                [
                    vertex.BoneIndices.X,
                    vertex.BoneIndices.Y,
                    vertex.BoneIndices.Z,
                    vertex.BoneIndices.W,
                ];
                for (int component = 0;
                     component < weights.Length;
                     component++)
                {
                    float weight = weights[component];
                    if (!float.IsFinite(weight))
                    {
                        return false;
                    }

                    if (MathF.Abs(weight) <= 1.0e-6f)
                    {
                        continue;
                    }

                    float rawIndex = indexes[component];
                    if (!float.IsFinite(rawIndex) ||
                        rawIndex < 0 ||
                        rawIndex >= palette.Length ||
                        rawIndex != MathF.Truncate(rawIndex))
                    {
                        return false;
                    }

                    int index = (int)rawIndex;
                    if (!IsFinite(palette[index]) ||
                        !IsApproximatelyIdentity(palette[index]))
                    {
                        return false;
                    }

                    foundEffectiveWeight = true;
                }
            }
        }

        return foundSkinnedMesh &&
            foundEffectiveWeight;
    }

    private static bool IsApproximatelyIdentity(
        Matrix4x4 matrix)
    {
        ReadOnlySpan<float> values =
        [
            matrix.M11 - 1, matrix.M12, matrix.M13, matrix.M14,
            matrix.M21, matrix.M22 - 1, matrix.M23, matrix.M24,
            matrix.M31, matrix.M32, matrix.M33 - 1, matrix.M34,
            matrix.M41, matrix.M42, matrix.M43, matrix.M44 - 1,
        ];
        foreach (float value in values)
        {
            if (MathF.Abs(value) > 1.0e-4f)
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsFinite(Matrix4x4 matrix) =>
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

    public static Matrix4x4 ConvertMatrix(
        in CompactMatrix3x4 matrix)
    {
        return new Matrix4x4(
            matrix.M11,
            matrix.M21,
            matrix.M31,
            0.0f,
            matrix.M12,
            matrix.M22,
            matrix.M32,
            0.0f,
            matrix.M13,
            matrix.M23,
            matrix.M33,
            0.0f,
            matrix.M14,
            matrix.M24,
            matrix.M34,
            1.0f);
    }

    private static int GetSkeletonEntityCount(
        CompactMeshDocument hierarchy)
    {
        int candidate = Math.Clamp(
            hierarchy.AnimationEntityCountCandidate,
            0,
            hierarchy.Entities.Count);
        if (candidate > 0)
        {
            return candidate;
        }

        return hierarchy.Bones.Count == 0
            ? 0
            : Math.Min(
                hierarchy.Entities.Count,
                hierarchy.Bones.Max(static entity => entity.Index) + 1);
    }

    private static SkeletonRenderData BuildSkeleton(
        CompactMeshDocument hierarchy,
        IReadOnlyList<CompactMatrix3x4> worldMatrices,
        int entityCount,
        bool usesEmbeddedAnimatedPropRig)
    {
        BoneRenderData[] bones = new BoneRenderData[entityCount];
        for (int index = 0; index < bones.Length; index++)
        {
            CompactMeshEntity entity = hierarchy.Entities[index];
            int parentIndex = entity.ParentIndex >= 0
                && entity.ParentIndex < entityCount
                    ? entity.ParentIndex
                    : -1;
            bones[index] = new BoneRenderData(
                entity.Name,
                parentIndex,
                ConvertMatrix(entity.LocalMatrix),
                ConvertMatrix(worldMatrices[index]),
                false)
            {
                Role = ClassifyRenderRole(
                    entity,
                    usesEmbeddedAnimatedPropRig),
                IsHierarchyOverlayVisible =
                    !usesEmbeddedAnimatedPropRig ||
                    (!entity.EntityType.HasFlag(
                         CompactMeshEntityType.Bone) &&
                     !entity.EntityType.HasFlag(
                         CompactMeshEntityType.SkinnedMesh)),
            };
        }

        return new SkeletonRenderData(
            bones,
            Matrix4x4.Identity);
    }

    private static BoneRenderRole ClassifyRenderRole(
        CompactMeshEntity entity,
        bool usesEmbeddedAnimatedPropRig)
    {
        if (string.Equals(
                entity.Name,
                Dl1PreviewContract.EyeCameraBoneName,
                StringComparison.OrdinalIgnoreCase) ||
            string.Equals(
                entity.Name,
                Dl1PreviewContract.ReferenceCameraBoneName,
                StringComparison.OrdinalIgnoreCase))
        {
            return BoneRenderRole.Camera;
        }

        if (entity.EntityType.HasFlag(
                CompactMeshEntityType.Helper))
        {
            return BoneRenderRole.Helper;
        }

        if (entity.EntityType.HasFlag(
                CompactMeshEntityType.Bone))
        {
            return usesEmbeddedAnimatedPropRig
                ? BoneRenderRole.Prop
                : BoneRenderRole.Deform;
        }

        return BoneRenderRole.Prop;
    }

    internal static bool UsesEmbeddedAnimatedPropRig(
        Dl1MeshData source,
        int entityCount)
    {
        if (entityCount <= 0 ||
            entityCount > source.Hierarchy.Entities.Count)
        {
            return false;
        }

        HashSet<int> embeddedSkinnedMeshIndexes = source.Hierarchy
            .Entities
            .Take(entityCount)
            .Where(entity =>
                entity.ParentIndex >= 0 &&
                entity.ParentIndex < entityCount &&
                entity.EntityType.HasFlag(
                    CompactMeshEntityType.SkinnedMesh))
            .Select(static entity => entity.Index)
            .ToHashSet();
        if (embeddedSkinnedMeshIndexes.Count == 0)
        {
            return false;
        }

        HashSet<int> paletteBoneIndexes = source.Surfaces
            .Where(surface =>
                embeddedSkinnedMeshIndexes.Contains(
                    surface.EntityIndex))
            .SelectMany(static surface => surface.Submeshes)
            .Where(static submesh =>
                submesh.IndexCount > 0)
            .SelectMany(static submesh =>
                submesh.BonePaletteEntityIndexes)
            .Where(index =>
                index >= 0 &&
                index < entityCount &&
                source.Hierarchy.Entities[index]
                    .EntityType.HasFlag(
                        CompactMeshEntityType.Bone))
            .Select(static index => (int)index)
            .ToHashSet();
        return paletteBoneIndexes.Count > 0;
    }

    private static Matrix4x4[] BuildInverseBindMatrices(
        SkeletonRenderData skeleton,
        List<string> diagnostics)
    {
        Matrix4x4[] inverseBindMatrices =
            new Matrix4x4[skeleton.Bones.Count];
        for (int index = 0; index < inverseBindMatrices.Length; index++)
        {
            if (!Matrix4x4.Invert(
                    skeleton.Bones[index].WorldTransform
                    * skeleton.RootTransform,
                    out Matrix4x4 inverse))
            {
                inverse = Matrix4x4.Identity;
                diagnostics.Add(
                    $"Entity {index} ('{skeleton.Bones[index].Name}') has a singular bind transform; its inverse bind was replaced with identity.");
            }

            inverseBindMatrices[index] = inverse;
        }

        return inverseBindMatrices;
    }

    private static void BuildSurfaceMeshes(
        Dl1MeshData source,
        Dl1MeshSurface surface,
        IReadOnlyList<CompactMatrix3x4> compactWorldMatrices,
        int skeletonEntityCount,
        Matrix4x4[] inverseBindMatrices,
        List<MeshRenderData> meshes,
        List<string> diagnostics)
    {
        if (surface.EntityIndex < 0
            || surface.EntityIndex >= source.Hierarchy.Entities.Count)
        {
            diagnostics.Add(
                $"Surface '{surface.Name}' refers to entity {surface.EntityIndex}, which is outside the hierarchy.");
            return;
        }

        CompactMeshEntity entity =
            source.Hierarchy.Entities[surface.EntityIndex];
        bool isSkinned = entity.EntityType.HasFlag(
            CompactMeshEntityType.SkinnedMesh);
        if (surface.Submeshes.Count == 0)
        {
            if (IsNonDisplayMaterial(
                    surface.MaterialSlotIndex,
                    source.MaterialSlots))
            {
                Dl1MaterialSlot? slot =
                    FindMaterialSlot(
                        surface.MaterialSlotIndex,
                        source.MaterialSlots);
                diagnostics.Add(
                    $"Surface '{surface.Name}' was omitted because material slot {surface.MaterialSlotIndex} is a validated non-display DL1 material (active='{slot?.DatabaseName ?? "<missing>"}', declared='{slot?.DeclaredDatabaseName ?? "<none>"}').");
                return;
            }

            if (isSkinned)
            {
                diagnostics.Add(
                    $"Skinned surface '{surface.Name}' has no decoded bone palette and was not rendered as a static approximation.");
                return;
            }

            TryBuildMesh(
                source.ResourceName,
                surface,
                source.MorphTargets,
                source.MaterialSlots,
                submeshIndex: null,
                firstIndex: 0,
                indexCount: surface.Indices.Count,
                materialSlotIndex: surface.MaterialSlotIndex,
                bonePalette: [],
                skinBindingMode: Dl1SkinBindingMode.None,
                isSkinned: false,
                [],
                ConvertMatrix(compactWorldMatrices[surface.EntityIndex]),
                meshes,
                diagnostics);
            return;
        }

        foreach (Dl1MeshSubmesh submesh in surface.Submeshes)
        {
            if (IsNonDisplayMaterial(
                    submesh.MaterialSlotIndex,
                    source.MaterialSlots))
            {
                Dl1MaterialSlot? slot =
                    FindMaterialSlot(
                        submesh.MaterialSlotIndex,
                        source.MaterialSlots);
                diagnostics.Add(
                    $"Surface '{surface.Name}' submesh {submesh.Index} was omitted because material slot {submesh.MaterialSlotIndex} is a validated non-display DL1 material (active='{slot?.DatabaseName ?? "<missing>"}', declared='{slot?.DeclaredDatabaseName ?? "<none>"}').");
                continue;
            }

            bool staticIgnoredPalette =
                submesh.SkinBindingMode ==
                    Dl1SkinBindingMode
                        .StaticEntityTransformIgnoredPalette;
            if (staticIgnoredPalette &&
                !Dl1SkinBindingPolicy
                    .CanUseStaticEntityTransformIgnoredPalette(
                        entity,
                        compactWorldMatrices[
                            surface.EntityIndex]))
            {
                diagnostics.Add(
                    $"Surface '{surface.Name}' submesh {submesh.Index} declares the ignored-palette entity-transform path, but it is not a skinned-mesh entity with a finite reconstructed hierarchy-element world matrix. It was not rendered.");
                continue;
            }

            if (staticIgnoredPalette)
            {
                diagnostics.Add(
                    $"Surface '{surface.Name}' submesh {submesh.Index} uses the runtime-validated no-BlendIndices path: its serialized palette is retained but ignored, and its finite reconstructed entity/world transform is applied as a non-skinned preview. Bone editing is unavailable for this part.");
            }

            bool publishSkinned =
                isSkinned &&
                !staticIgnoredPalette;
            TryBuildMesh(
                source.ResourceName,
                surface,
                source.MorphTargets,
                source.MaterialSlots,
                submesh.Index,
                submesh.FirstIndex,
                submesh.IndexCount,
                submesh.MaterialSlotIndex,
                submesh.BonePaletteEntityIndexes,
                submesh.SkinBindingMode,
                publishSkinned,
                publishSkinned
                    ? inverseBindMatrices
                    : [],
                publishSkinned
                    ? Matrix4x4.Identity
                    : ConvertMatrix(
                        compactWorldMatrices[surface.EntityIndex]),
                meshes,
                diagnostics,
                skeletonEntityCount);
        }
    }

    private static bool IsNonDisplayMaterial(
        int materialSlotIndex,
        IReadOnlyList<Dl1MaterialSlot> materialSlots) =>
        Dl1PreviewMaterialPolicy.IsNonDisplayMaterial(
            FindMaterialSlot(
                materialSlotIndex,
                materialSlots));

    private static Dl1MaterialSlot? FindMaterialSlot(
        int materialSlotIndex,
        IReadOnlyList<Dl1MaterialSlot> materialSlots) =>
        materialSlots.FirstOrDefault(slot =>
            slot.Index == materialSlotIndex);

    private static void TryBuildMesh(
        string resourceName,
        Dl1MeshSurface surface,
        IReadOnlyList<Dl1MorphTarget> sourceMorphTargets,
        IReadOnlyList<Dl1MaterialSlot> materialSlots,
        int? submeshIndex,
        int firstIndex,
        int indexCount,
        int materialSlotIndex,
        IReadOnlyList<short> bonePalette,
        Dl1SkinBindingMode skinBindingMode,
        bool isSkinned,
        Matrix4x4[] inverseBindMatrices,
        Matrix4x4 localToWorld,
        List<MeshRenderData> meshes,
        List<string> diagnostics,
        int skeletonEntityCount = 0)
    {
        if (firstIndex < 0
            || indexCount <= 0
            || indexCount % 3 != 0
            || firstIndex > surface.Indices.Count - indexCount)
        {
            string part = submeshIndex?.ToString(
                System.Globalization.CultureInfo.InvariantCulture) ?? "all";
            diagnostics.Add(
                $"Surface '{surface.Name}' submesh {part} has an invalid triangle range.");
            return;
        }

        if (isSkinned && bonePalette.Count == 0)
        {
            diagnostics.Add(
                $"Surface '{surface.Name}' submesh {submeshIndex} has no decoded bone palette.");
            return;
        }

        if (isSkinned)
        {
            if (skinBindingMode is
                Dl1SkinBindingMode.None or
                Dl1SkinBindingMode.UnresolvedMissingBlendStreams)
            {
                diagnostics.Add(
                    $"Surface '{surface.Name}' submesh {submeshIndex} has unresolved skin binding data and was not rendered as a static approximation.");
                return;
            }
        }

        Matrix4x4[] drawInverseBindMatrices = [];
        int[] skinBoneIndices = [];
        if (isSkinned &&
            !TryBuildDrawSkinPalette(
                bonePalette,
                skeletonEntityCount,
                inverseBindMatrices,
                out drawInverseBindMatrices,
                out skinBoneIndices,
                out string? paletteError))
        {
            diagnostics.Add(
                $"Surface '{surface.Name}' submesh {submeshIndex} was not rendered: {paletteError}");
            return;
        }

        Dictionary<ushort, uint> remap = [];
        List<MeshVertex> vertices = [];
        uint[] indices = new uint[indexCount];
        for (int index = 0; index < indexCount; index++)
        {
            ushort sourceIndex = surface.Indices[firstIndex + index];
            if (sourceIndex >= surface.Vertices.Count)
            {
                diagnostics.Add(
                    $"Surface '{surface.Name}' submesh {submeshIndex} contains vertex index {sourceIndex}, outside {surface.Vertices.Count} vertices.");
                return;
            }

            if (!remap.TryGetValue(sourceIndex, out uint renderIndex))
            {
                Dl1MeshVertex sourceVertex = surface.Vertices[sourceIndex];
                Vector4 boneWeights = sourceVertex.BlendWeights;
                Dl1BoneIndex4 localBlendIndices =
                    sourceVertex.LocalBlendIndices;
                if (isSkinned &&
                         skinBindingMode ==
                         Dl1SkinBindingMode.RigidIndexedPalette)
                {
                    boneWeights = Vector4.UnitX;
                    localBlendIndices = new Dl1BoneIndex4(
                        localBlendIndices.X,
                        0,
                        0,
                        0);
                }

                if (!TryMapBoneIndices(
                        boneWeights,
                        localBlendIndices,
                        bonePalette,
                        isSkinned,
                        skeletonEntityCount,
                        out Vector4 boneIndices))
                {
                    diagnostics.Add(
                        $"Surface '{surface.Name}' submesh {submeshIndex} contains a weighted local bone index outside its decoded palette.");
                    return;
                }

                renderIndex = checked((uint)vertices.Count);
                remap.Add(sourceIndex, renderIndex);
                vertices.Add(new MeshVertex(
                    sourceVertex.Position,
                    sourceVertex.Normal,
                    sourceVertex.TextureCoordinate0,
                    boneWeights,
                    boneIndices));
            }

            indices[index] = renderIndex;
        }

        string submeshPart = submeshIndex?.ToString(
            System.Globalization.CultureInfo.InvariantCulture) ?? "all";
        string id = string.Create(
            System.Globalization.CultureInfo.InvariantCulture,
            $"{resourceName}/{surface.Name}/lod{surface.LodIndex}/part{submeshPart}");
        MorphTargetRenderData[] morphTargets = BuildMorphTargets(
            surface,
            sourceMorphTargets,
            remap,
            diagnostics);
        TextureRenderData? baseColorTexture =
            BuildBaseColorTexture(
                materialSlotIndex,
                materialSlots);
        meshes.Add(new MeshRenderData(
            id,
            vertices.ToArray(),
            indices,
            localToWorld,
            drawInverseBindMatrices,
            isSkinned)
        {
            Tint = baseColorTexture is null
                ? GetMaterialTint(materialSlotIndex)
                : Vector4.One,
            MorphTargets = morphTargets,
            BaseColorTexture = baseColorTexture,
            SkinBoneIndices = skinBoneIndices,
        });
    }

    private static TextureRenderData? BuildBaseColorTexture(
        int materialSlotIndex,
        IReadOnlyList<Dl1MaterialSlot> materialSlots)
    {
        Dl1TexturePreviewData? preview = materialSlots
            .FirstOrDefault(slot =>
                slot.Index == materialSlotIndex)
            ?.ResolvedMaterial
            ?.BaseColorPreview;
        if (preview is null)
        {
            return null;
        }

        TextureRenderFormat format = preview.Format switch
        {
            Dl1PreviewTextureFormat.Bc1Unorm =>
                TextureRenderFormat.Bc1Unorm,
            Dl1PreviewTextureFormat.Bc2Unorm =>
                TextureRenderFormat.Bc2Unorm,
            Dl1PreviewTextureFormat.Bc3Unorm =>
                TextureRenderFormat.Bc3Unorm,
            _ => throw new InvalidDataException(
                $"Texture '{preview.ResourceName}' has no renderer format."),
        };
        return new TextureRenderData(
            preview.AssetId.StableKey,
            preview.Width,
            preview.Height,
            format,
            preview.RowPitch,
            preview.BaseMipBytes);
    }

    private static MorphTargetRenderData[] BuildMorphTargets(
        Dl1MeshSurface surface,
        IReadOnlyList<Dl1MorphTarget> sourceMorphTargets,
        Dictionary<ushort, uint> vertexRemap,
        List<string> diagnostics)
    {
        List<MorphTargetRenderData> result = [];
        foreach (Dl1MorphTarget target in sourceMorphTargets)
        {
            Dl1MorphPositionDeltaSet[] sets = target.Bindings
                .Where(binding =>
                    binding.EntityIndex == surface.EntityIndex &&
                    binding.LodIndex == surface.LodIndex)
                .SelectMany(static binding =>
                    binding.PositionDeltaSets)
                .ToArray();
            if (sets.Length == 0)
            {
                continue;
            }

            if (sets.Length != 1)
            {
                diagnostics.Add(
                    $"Surface '{surface.Name}' maps morph '{target.Name}' to {sets.Length} local target payloads; the ambiguous target was omitted.");
                continue;
            }

            IReadOnlyList<Vector3> sourceDeltas =
                sets[0].PositionDeltas;
            if (sourceDeltas.Count != surface.Vertices.Count)
            {
                diagnostics.Add(
                    $"Surface '{surface.Name}' morph '{target.Name}' has {sourceDeltas.Count} deltas for {surface.Vertices.Count} vertices; the malformed target was omitted.");
                continue;
            }

            Vector3 surfaceMinimum = surface.Vertices
                .Select(static vertex => vertex.Position)
                .Aggregate(Vector3.Min);
            Vector3 surfaceMaximum = surface.Vertices
                .Select(static vertex => vertex.Position)
                .Aggregate(Vector3.Max);
            float surfaceDiagonal = Vector3.Distance(
                surfaceMinimum,
                surfaceMaximum);
            float maximumDelta = 0.0f;
            bool hasNonFiniteDelta = false;
            foreach (Vector3 delta in sourceDeltas)
            {
                if (!float.IsFinite(delta.X) ||
                    !float.IsFinite(delta.Y) ||
                    !float.IsFinite(delta.Z))
                {
                    hasNonFiniteDelta = true;
                    break;
                }

                maximumDelta = Math.Max(maximumDelta, delta.Length());
            }

            // The compact payload is preserved by the codec, but it is not
            // safe to publish an unverified interpretation to the renderer.
            // A single facial target moving a vertex farther than the entire
            // owning surface is a strong unit/layout violation and was the
            // direct cause of retail faces exploding into screen-sized
            // triangles. Fail closed until that payload contract is proven.
            if (hasNonFiniteDelta ||
                !float.IsFinite(surfaceDiagonal) ||
                surfaceDiagonal <= 1.0e-6f ||
                maximumDelta > surfaceDiagonal + 1.0e-5f)
            {
                diagnostics.Add(
                    $"Surface '{surface.Name}' morph '{target.Name}' has an unverified displacement payload (maximum {maximumDelta:R}, surface diagonal {surfaceDiagonal:R}); the unsafe target was preserved by the codec but omitted from preview.");
                continue;
            }

            Vector3[] deltas = new Vector3[vertexRemap.Count];
            foreach ((ushort sourceIndex, uint renderIndex) in vertexRemap)
            {
                deltas[checked((int)renderIndex)] =
                    sourceDeltas[sourceIndex];
            }

            result.Add(new MorphTargetRenderData(
                target.Name,
                deltas,
                ReadOnlyMemory<Vector3>.Empty));
        }

        return result.ToArray();
    }

    private static bool TryMapBoneIndices(
        Vector4 blendWeights,
        Dl1BoneIndex4 localBlendIndices,
        IReadOnlyList<short> palette,
        bool isSkinned,
        int skeletonEntityCount,
        out Vector4 boneIndices)
    {
        if (!isSkinned)
        {
            boneIndices = Vector4.Zero;
            return true;
        }

        float x;
        float y;
        float z;
        float w;
        bool validX = TryMapBoneIndex(
                localBlendIndices.X,
                blendWeights.X,
                palette,
                skeletonEntityCount,
                out x);
        bool validY = TryMapBoneIndex(
                localBlendIndices.Y,
                blendWeights.Y,
                palette,
                skeletonEntityCount,
                out y);
        bool validZ = TryMapBoneIndex(
                localBlendIndices.Z,
                blendWeights.Z,
                palette,
                skeletonEntityCount,
                out z);
        bool validW = TryMapBoneIndex(
                localBlendIndices.W,
                blendWeights.W,
                palette,
                skeletonEntityCount,
                out w);
        boneIndices = new Vector4(x, y, z, w);
        return validX && validY && validZ && validW;
    }

    private static bool TryMapBoneIndex(
        byte localIndex,
        float weight,
        IReadOnlyList<short> palette,
        int skeletonEntityCount,
        out float mappedIndex)
    {
        if (MathF.Abs(weight) <= 1.0e-6f)
        {
            mappedIndex = 0.0f;
            return true;
        }

        if (localIndex >= palette.Count)
        {
            mappedIndex = 0.0f;
            return false;
        }

        short entityIndex = palette[localIndex];
        mappedIndex = localIndex;
        return entityIndex >= 0
            && entityIndex < skeletonEntityCount;
    }

    private static bool TryBuildDrawSkinPalette(
        IReadOnlyList<short> bonePalette,
        int skeletonEntityCount,
        Matrix4x4[] inverseBindMatrices,
        out Matrix4x4[] drawInverseBindMatrices,
        out int[] skinBoneIndices,
        out string? error)
    {
        if (bonePalette.Count == 0)
        {
            drawInverseBindMatrices = [];
            skinBoneIndices = [];
            error = "the decoded bone palette is empty.";
            return false;
        }

        if (bonePalette.Count >
            GpuSkinningPalette.MaximumBoneCount)
        {
            drawInverseBindMatrices = [];
            skinBoneIndices = [];
            error =
                $"the decoded bone palette contains {bonePalette.Count} entries, exceeding the bounded {GpuSkinningPalette.MaximumBoneCount}-matrix D3D11 draw palette.";
            return false;
        }

        drawInverseBindMatrices =
            new Matrix4x4[bonePalette.Count];
        skinBoneIndices = new int[bonePalette.Count];
        for (int paletteIndex = 0;
             paletteIndex < bonePalette.Count;
             paletteIndex++)
        {
            int skeletonBoneIndex =
                bonePalette[paletteIndex];
            if ((uint)skeletonBoneIndex >=
                    (uint)skeletonEntityCount ||
                (uint)skeletonBoneIndex >=
                    (uint)inverseBindMatrices.Length)
            {
                drawInverseBindMatrices = [];
                skinBoneIndices = [];
                error =
                    $"palette entry {paletteIndex} references skeleton entity {skeletonBoneIndex} outside the {skeletonEntityCount}-row animation hierarchy.";
                return false;
            }

            drawInverseBindMatrices[paletteIndex] =
                inverseBindMatrices[skeletonBoneIndex];
            skinBoneIndices[paletteIndex] =
                skeletonBoneIndex;
        }

        error = null;
        return true;
    }

    private static Vector4 GetMaterialTint(int materialSlotIndex)
    {
        int normalized = Math.Abs(materialSlotIndex % 5);
        return normalized switch
        {
            0 => new Vector4(0.61f, 0.68f, 0.76f, 1.0f),
            1 => new Vector4(0.69f, 0.62f, 0.55f, 1.0f),
            2 => new Vector4(0.56f, 0.68f, 0.61f, 1.0f),
            3 => new Vector4(0.67f, 0.58f, 0.69f, 1.0f),
            _ => new Vector4(0.70f, 0.68f, 0.52f, 1.0f),
        };
    }
}
