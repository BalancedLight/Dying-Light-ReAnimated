using System.IO;
using ReAnimated.Codecs.Fbx;

namespace ReAnimated.App.Infrastructure;

public sealed class BlenderFbxOutputValidator :
    IBlenderFbxOutputValidator
{
    private const long MaximumValidatedFbxBytes =
        256L * 1024 * 1024;
    private static readonly FbxReadLimits ReadLimits = new()
    {
        MaximumFileBytes =
            checked((int)MaximumValidatedFbxBytes),
        MaximumArrayBytes =
            64 * 1024 * 1024,
        MaximumDecodedAllocationBytes =
            256L * 1024 * 1024,
    };

    public async Task ValidateAsync(
        string outputFbxPath,
        IReadOnlyList<BlenderFbxJobBone> expectedBones,
        IReadOnlyList<BlenderFbxJobClip> expectedClips,
        IReadOnlyList<BlenderFbxJobMesh> expectedMeshes,
        IReadOnlyList<BlenderFbxJobTexture> expectedTextures,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(
            outputFbxPath);
        ArgumentNullException.ThrowIfNull(expectedBones);
        ArgumentNullException.ThrowIfNull(expectedClips);
        ArgumentNullException.ThrowIfNull(expectedMeshes);
        ArgumentNullException.ThrowIfNull(expectedTextures);
        if (expectedMeshes.Count == 0)
        {
            throw new ArgumentException(
                "At least one expected retail mesh is required.",
                nameof(expectedMeshes));
        }

        long outputBytes =
            new FileInfo(outputFbxPath).Length;
        if (outputBytes <= 0 ||
            outputBytes > MaximumValidatedFbxBytes)
        {
            throw new InvalidDataException(
                $"Written FBX is {outputBytes:N0} bytes; strict post-write validation is limited to {MaximumValidatedFbxBytes:N0} bytes.");
        }

        FbxStrictExportInspection inspection =
            await FbxStrictExportInspector.InspectFileAsync(
                outputFbxPath,
                ReadLimits,
                cancellationToken)
                .ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        ValidateAnimationStacks(
            inspection,
            expectedClips,
            expectedBones);
        ValidateBonesAndBindPose(
            inspection,
            expectedBones);
        ValidateRetailGeometry(
            inspection,
            expectedMeshes,
            expectedBones,
            expectedTextures);
        ValidateTextures(
            inspection,
            expectedTextures);
    }

    private static void ValidateAnimationStacks(
        FbxStrictExportInspection inspection,
        IReadOnlyList<BlenderFbxJobClip> expectedClips,
        IReadOnlyList<BlenderFbxJobBone> expectedBones)
    {
        string[] expected = expectedClips
            .Select(static clip =>
                clip.ActionName)
            .ToArray();
        if (expected.Length == 0)
        {
            if (inspection.AnimationStackNames.Length != 0)
            {
                throw new InvalidDataException(
                    "Written mesh-only FBX unexpectedly contains AnimationStacks.");
            }

            return;
        }

        if (expected.Any(
                string.IsNullOrWhiteSpace) ||
            expected.Distinct(
                    StringComparer.Ordinal)
                .Count() != expected.Length)
        {
            throw new InvalidDataException(
                "Requested Blender Actions must have unique non-empty names.");
        }

        if (inspection.AnimationStackNames.Length !=
                expected.Length ||
            !inspection.AnimationStackNames
                .ToHashSet(StringComparer.Ordinal)
                .SetEquals(expected))
        {
            throw new InvalidDataException(
                $"Written FBX AnimationStacks do not match the requested Actions. Expected [{string.Join(", ", expected)}]; found [{string.Join(", ", inspection.AnimationStackNames)}].");
        }

        HashSet<long> expectedBoneIds = expectedBones
            .Select(bone =>
                inspection.LimbModelIds.TryGetValue(
                    bone.Name,
                    out long objectId)
                    ? objectId
                    : throw new InvalidDataException(
                        $"Written FBX is missing expected animation LimbNode '{bone.Name}'."))
            .ToHashSet();
        foreach (BlenderFbxJobClip clip in expectedClips)
        {
            if (!inspection.AnimationStacks.TryGetValue(
                    clip.ActionName,
                    out FbxAnimationStackInspection? stack))
            {
                throw new InvalidDataException(
                    $"Written FBX has no inspected AnimationStack '{clip.ActionName}'.");
            }

            ValidateAnimationStack(
                stack,
                clip,
                expectedBoneIds);
        }
    }

    private static void ValidateAnimationStack(
        FbxAnimationStackInspection stack,
        BlenderFbxJobClip expectedClip,
        HashSet<long> expectedBoneIds)
    {
        if (expectedClip.FbxFrameCount <= 0 ||
            !double.IsFinite(
                expectedClip.FbxOutputFps) ||
            expectedClip.FbxOutputFps <= 0.0)
        {
            throw new InvalidDataException(
                $"Requested Action '{expectedClip.ActionName}' has invalid output timing.");
        }

        if (stack.LayerCount != 1 ||
            stack.CurveCount <= 0 ||
            stack.BoneCurveCount <= 0 ||
            stack.MinimumKeyTick is not long minimumKeyTick ||
            stack.MaximumKeyTick is not long maximumKeyTick)
        {
            throw new InvalidDataException(
                $"Written FBX Action '{expectedClip.ActionName}' does not contain one non-empty animation layer with bone curves.");
        }

        if (!stack.CurveModelIds.SetEquals(
                expectedBoneIds))
        {
            throw new InvalidDataException(
                $"Written FBX Action '{expectedClip.ActionName}' does not contain animation curves for the exact retail bone set.");
        }

        double frameTicks =
            FbxBinaryDocument.TicksPerSecond /
            expectedClip.FbxOutputFps;
        double expectedStopTick =
            (expectedClip.FbxFrameCount - 1) *
            frameTicks;
        double tolerance =
            Math.Max(
                2.0,
                frameTicks * 0.51);
        if (Math.Abs(minimumKeyTick) > tolerance ||
            Math.Abs(
                maximumKeyTick -
                expectedStopTick) > tolerance)
        {
            throw new InvalidDataException(
                $"Written FBX Action '{expectedClip.ActionName}' key range [{minimumKeyTick:N0}, {maximumKeyTick:N0}] does not match its requested {expectedClip.FbxFrameCount:N0}-frame span at {expectedClip.FbxOutputFps:G9} fps.");
        }

        if (stack.StopTick < stack.StartTick ||
            Math.Abs(stack.StartTick) > tolerance ||
            Math.Abs(
                stack.StopTick -
                expectedStopTick) > tolerance)
        {
            throw new InvalidDataException(
                $"Written FBX Action '{expectedClip.ActionName}' declares invalid or mismatched LocalStart/LocalStop timing [{stack.StartTick:N0}, {stack.StopTick:N0}].");
        }
    }

    private static void ValidateBonesAndBindPose(
        FbxStrictExportInspection inspection,
        IReadOnlyList<BlenderFbxJobBone> expectedBones)
    {
        string[] expectedNames = expectedBones
            .Select(static bone =>
                bone.Name)
            .ToArray();
        if (expectedNames.Length == 0)
        {
            if (inspection.LimbModelIds.Count != 0)
            {
                throw new InvalidDataException(
                    "Written static FBX unexpectedly contains LimbNode bones.");
            }

            return;
        }

        if (expectedNames.Any(
                string.IsNullOrWhiteSpace) ||
            expectedNames.Distinct(
                    StringComparer.Ordinal)
                .Count() != expectedNames.Length ||
            expectedBones
                .Select(static bone =>
                    bone.Index)
                .Distinct()
                .Count() != expectedBones.Count)
        {
            throw new InvalidDataException(
                "Requested retail bones must have unique non-empty names and unique indices.");
        }

        if (inspection.LimbModelIds.Count !=
                expectedNames.Length ||
            !inspection.LimbModelIds.Keys
                .ToHashSet(StringComparer.Ordinal)
                .SetEquals(expectedNames))
        {
            throw new InvalidDataException(
                $"Written FBX LimbNode set does not exactly match the expected {expectedNames.Length:N0}-bone retail armature.");
        }

        HashSet<long> expectedIds = expectedNames
            .Select(name =>
                inspection.LimbModelIds[name])
            .ToHashSet();
        BlenderFbxJobBone[] rootCandidates =
            expectedBones
                .Where(static bone =>
                    bone.ParentIndex < 0 &&
                    bone.Root)
                .ToArray();
        if (rootCandidates.Length != 1 ||
            !inspection.LimbParentModelIds.TryGetValue(
                rootCandidates[0].Name,
                out long? armatureModelId) ||
            armatureModelId is null ||
            expectedIds.Contains(armatureModelId.Value))
        {
            throw new InvalidDataException(
                "Written FBX does not expose one non-limb armature Model parent for the retail Root LimbNode.");
        }

        Dictionary<int, BlenderFbxJobBone> expectedByIndex =
            expectedBones.ToDictionary(
                static bone =>
                    bone.Index);
        foreach (BlenderFbxJobBone bone in expectedBones)
        {
            if (!inspection.LimbParentModelIds.TryGetValue(
                    bone.Name,
                    out long? actualParentId) ||
                actualParentId is null)
            {
                throw new InvalidDataException(
                    $"Written FBX LimbNode '{bone.Name}' has no Model parent.");
            }

            long expectedParentId;
            if (bone.ParentIndex < 0)
            {
                expectedParentId =
                    armatureModelId.Value;
            }
            else
            {
                if (!expectedByIndex.TryGetValue(
                        bone.ParentIndex,
                        out BlenderFbxJobBone? expectedParent))
                {
                    throw new InvalidDataException(
                        $"Requested retail bone '{bone.Name}' refers to missing parent index {bone.ParentIndex}.");
                }

                expectedParentId =
                    inspection.LimbModelIds[
                        expectedParent.Name];
            }

            if (actualParentId.Value !=
                expectedParentId)
            {
                throw new InvalidDataException(
                    $"Written FBX LimbNode '{bone.Name}' has the wrong hierarchy parent.");
            }
        }

        bool complete = inspection.BindPoses.Any(pose =>
        {
            HashSet<long> poseLimbIds = pose.NodeMatrices
                .Keys
                .Where(expectedIds.Contains)
                .ToHashSet();
            return poseLimbIds.SetEquals(expectedIds) &&
                pose.NodeMatrices.ContainsKey(
                    armatureModelId.Value);
        });
        if (!complete)
        {
            throw new InvalidDataException(
                $"Written FBX has no finite, nonsingular BindPose matrix table covering the exact {expectedIds.Count:N0}-bone retail armature plus its armature Model.");
        }
    }

    private static void ValidateRetailGeometry(
        FbxStrictExportInspection inspection,
        IReadOnlyList<BlenderFbxJobMesh> expectedMeshes,
        IReadOnlyList<BlenderFbxJobBone> expectedBones,
        IReadOnlyList<BlenderFbxJobTexture> expectedTextures)
    {
        string[] expectedModels = expectedBones.Count == 0
            ? expectedMeshes
                .Select(static mesh => mesh.Name)
                .ToArray()
            : expectedMeshes
                .Select(static mesh => mesh.Name)
                .Append("DLR_BindPoseGuard")
                .ToArray();
        string[] expectedGeometry = expectedBones.Count == 0
            ? expectedMeshes
                .Select(static mesh => mesh.Name + "_Mesh")
                .ToArray()
            : expectedMeshes
                .Select(static mesh => mesh.Name + "_Mesh")
                .Append("DLR_BindPoseGuard_Mesh")
                .ToArray();
        if (inspection.MeshModelCount !=
                expectedModels.Length ||
            inspection.MeshGeometryCount !=
                expectedGeometry.Length ||
            !inspection.MeshModelNames.SetEquals(
                expectedModels) ||
            !inspection.MeshGeometryNames.SetEquals(
                expectedGeometry))
        {
            throw new InvalidDataException(
                "Written FBX mesh Model/Geometry sets do not exactly match the requested retail parts plus the BindPose guard.");
        }

        var texturesByKey =
            new Dictionary<
                string,
                BlenderFbxJobTexture>(
                StringComparer.Ordinal);
        foreach (BlenderFbxJobTexture texture in
                 expectedTextures)
        {
            if (string.IsNullOrWhiteSpace(
                    texture.Key) ||
                !texturesByKey.TryAdd(
                    texture.Key,
                    texture))
            {
                throw new InvalidDataException(
                    "Requested decoded base-color texture keys must be unique and non-empty.");
            }
        }

        HashSet<long> expectedBoneIds = expectedBones
            .Select(bone =>
                inspection.LimbModelIds[bone.Name])
            .ToHashSet();
        foreach (BlenderFbxJobMesh expectedMesh in
                 expectedMeshes)
        {
            string geometryName =
                expectedMesh.Name + "_Mesh";
            FbxMeshGeometryInspection geometry =
                inspection.MeshGeometries[
                    geometryName];
            if (!string.Equals(
                    geometry.MeshModelName,
                    expectedMesh.Name,
                    StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    $"Written FBX Geometry '{geometryName}' is not connected to its expected Mesh Model '{expectedMesh.Name}'.");
            }

            if (geometry.VertexCount !=
                    expectedMesh.VertexCount ||
                geometry.PolygonVertexIndexCount !=
                    expectedMesh.IndexCount ||
                geometry.PolygonCount !=
                    expectedMesh.IndexCount / 3)
            {
                throw new InvalidDataException(
                    $"Written FBX Geometry '{geometryName}' topology does not match the requested {expectedMesh.VertexCount:N0} vertices and {expectedMesh.IndexCount:N0} triangle indices.");
            }

            if (geometry.NormalVectorCount <= 0 ||
                geometry.TextureCoordinateCount <= 0 ||
                (geometry.NormalIndexCount > 0 &&
                 geometry.NormalIndexCount !=
                    expectedMesh.IndexCount) ||
                (geometry.TextureCoordinateIndexCount > 0 &&
                 geometry.TextureCoordinateIndexCount !=
                    expectedMesh.IndexCount))
            {
                throw new InvalidDataException(
                    $"Written FBX Geometry '{geometryName}' does not contain complete finite normals and UVs for its exported topology.");
            }

            ValidateSkin(
                geometry,
                expectedMesh,
                expectedBoneIds);
            ValidateMeshTexture(
                geometry,
                expectedMesh,
                texturesByKey,
                inspection);
        }
    }

    private static void ValidateSkin(
        FbxMeshGeometryInspection geometry,
        BlenderFbxJobMesh expectedMesh,
        HashSet<long> expectedBoneIds)
    {
        if (!expectedMesh.Skinned)
        {
            if (!geometry.Skins.IsEmpty)
            {
                throw new InvalidDataException(
                    $"Written static FBX Geometry '{geometry.Name}' unexpectedly contains a Skin deformer.");
            }

            return;
        }

        if (geometry.Skins.Length != 1)
        {
            throw new InvalidDataException(
                $"Written skinned FBX Geometry '{geometry.Name}' must connect to exactly one Skin deformer.");
        }

        FbxSkinInspection skin =
            geometry.Skins[0];
        if (skin.Clusters.IsEmpty ||
            skin.CoveredVertexCount !=
                geometry.VertexCount ||
            skin.Clusters.Any(cluster =>
                cluster.InfluenceCount <= 0 ||
                !expectedBoneIds.Contains(
                    cluster.BoneModelId)) ||
            skin.Clusters
                .Select(static cluster =>
                    cluster.BoneModelId)
                .Distinct()
                .Count() != skin.Clusters.Length)
        {
            throw new InvalidDataException(
                $"Written skinned FBX Geometry '{geometry.Name}' has incomplete, duplicate, or non-retail Cluster bindings.");
        }
    }

    private static void ValidateMeshTexture(
        FbxMeshGeometryInspection geometry,
        BlenderFbxJobMesh expectedMesh,
        Dictionary<
            string,
            BlenderFbxJobTexture> texturesByKey,
        FbxStrictExportInspection inspection)
    {
        if (expectedMesh.TextureKey is not
            { } textureKey)
        {
            return;
        }

        if (!texturesByKey.TryGetValue(
                textureKey,
                out BlenderFbxJobTexture? expectedTexture))
        {
            throw new InvalidDataException(
                $"Requested mesh '{expectedMesh.Name}' refers to unknown texture key '{textureKey}'.");
        }

        if (geometry.MaterialIds.IsEmpty ||
            geometry.TextureIds.IsEmpty ||
            geometry.VideoIds.IsEmpty)
        {
            throw new InvalidDataException(
                $"Written FBX Geometry '{geometry.Name}' has no connected Material/Texture/Video chain for decoded base color '{expectedTexture.FileName}'.");
        }

        if (expectedTexture.EmbeddedInFbx)
        {
            ValidateEmbeddedTexture(
                geometry.ExternalFileReferences,
                inspection.EmbeddedVideos,
                expectedTexture.FileName,
                $"FBX Geometry '{geometry.Name}' Material/Texture/Video chain");
        }
        else
        {
            ValidatePortableTextureReference(
                geometry.ExternalFileReferences,
                expectedTexture.FileName,
                $"FBX Geometry '{geometry.Name}' Material/Texture/Video chain");
        }
    }

    private static void ValidateTextures(
        FbxStrictExportInspection inspection,
        IReadOnlyList<BlenderFbxJobTexture> expectedTextures)
    {
        if (expectedTextures.Count == 0)
        {
            return;
        }

        if (expectedTextures
                .Select(static texture =>
                    texture.Key)
                .Distinct(
                    StringComparer.Ordinal)
                .Count() != expectedTextures.Count)
        {
            throw new InvalidDataException(
                "Requested decoded base-color texture keys are not unique.");
        }

        bool expectEmbeddedTextures = expectedTextures[0]
            .EmbeddedInFbx;
        if (expectedTextures.Any(texture =>
                texture.EmbeddedInFbx != expectEmbeddedTextures))
        {
            throw new InvalidDataException(
                "An FBX export cannot mix embedded and external decoded base-color textures.");
        }

        if (inspection.TextureObjectCount <
                expectedTextures.Count ||
            inspection.VideoObjectCount <
                expectedTextures.Count)
        {
            throw new InvalidDataException(
                $"Written FBX contains {inspection.TextureObjectCount:N0} Texture and {inspection.VideoObjectCount:N0} Video objects; {expectedTextures.Count:N0} decoded base-color references were required.");
        }

        foreach (BlenderFbxJobTexture texture in
                 expectedTextures)
        {
            if (expectEmbeddedTextures)
            {
                ValidateEmbeddedTexture(
                    inspection.ExternalFileReferences,
                    inspection.EmbeddedVideos,
                    texture.FileName,
                    "Written FBX Texture/Video objects");
            }
            else
            {
                ValidatePortableTextureReference(
                    inspection.ExternalFileReferences,
                    texture.FileName,
                    "Written FBX Texture/Video objects");
            }
        }
    }

    private static void ValidateEmbeddedTexture(
        IReadOnlyList<FbxExternalFileReferenceInspection>
            references,
        IReadOnlyList<FbxEmbeddedVideoInspection> embeddedVideos,
        string expectedFileName,
        string context)
    {
        long[] matchingVideoIds = references
            .Where(reference =>
                string.Equals(
                    GetPortableFileName(reference.Value),
                    expectedFileName,
                    StringComparison.OrdinalIgnoreCase))
            .Select(static reference => reference.ObjectId)
            .Distinct()
            .ToArray();
        if (matchingVideoIds.Length == 0)
        {
            throw new InvalidDataException(
                $"{context} does not identify decoded base-color file '{expectedFileName}' for embedding.");
        }

        if (!embeddedVideos.Any(video =>
                video.ContentByteCount > 0 &&
                matchingVideoIds.Contains(video.ObjectId)))
        {
            throw new InvalidDataException(
                $"{context} does not contain embedded image bytes for decoded base-color file '{expectedFileName}'.");
        }
    }

    private static void ValidatePortableTextureReference(
        IReadOnlyList<FbxExternalFileReferenceInspection>
            references,
        string expectedFileName,
        string context)
    {
        FbxExternalFileReferenceInspection[] matching =
            references
                .Where(reference =>
                    string.Equals(
                        GetPortableFileName(
                            reference.Value),
                        expectedFileName,
                        StringComparison.OrdinalIgnoreCase))
                .ToArray();
        if (matching.Length == 0)
        {
            throw new InvalidDataException(
                $"{context} does not reference decoded base-color file '{expectedFileName}'.");
        }

        if (!matching.Any(reference =>
                IsSafeSiblingReference(
                    reference.Value,
                    expectedFileName)))
        {
            string evidence = string.Join(
                ", ",
                matching
                    .Take(4)
                    .Select(reference =>
                        $"{reference.ObjectType} {reference.PropertyName}='{reference.Value}'"));
            throw new InvalidDataException(
                $"{context} references decoded base color '{expectedFileName}' only through unsafe paths ({evidence}). A Texture or Video FileName/RelativeFilename must be a non-rooted same-directory relative path; absolute, staging-directory, and parent-traversal paths are rejected.");
        }
    }

    private static bool IsSafeSiblingReference(
        string value,
        string expectedFileName)
    {
        if (string.IsNullOrWhiteSpace(value) ||
            value.Contains('\0') ||
            IsPortableRooted(value))
        {
            return false;
        }

        string[] segments = value.Split(
            ['/', '\\'],
            StringSplitOptions.None);
        if (segments.Length == 0 ||
            segments.Any(static segment =>
                segment.Length == 0 ||
                string.Equals(
                    segment,
                    "..",
                    StringComparison.Ordinal)))
        {
            return false;
        }

        for (int index = 0;
             index < segments.Length - 1;
             index++)
        {
            if (!string.Equals(
                    segments[index],
                    ".",
                    StringComparison.Ordinal))
            {
                return false;
            }
        }

        return string.Equals(
            segments[^1],
            expectedFileName,
            StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsPortableRooted(
        string value)
    {
        if (Path.IsPathRooted(value) ||
            value[0] is '/' or '\\')
        {
            return true;
        }

        return value.Length >= 2 &&
            IsAsciiLetter(value[0]) &&
            value[1] == ':';
    }

    private static bool IsAsciiLetter(
        char value) =>
        value is >= 'A' and <= 'Z' or
            >= 'a' and <= 'z';

    private static string GetPortableFileName(
        string value)
    {
        int slash = value.LastIndexOf('/');
        int backslash = value.LastIndexOf('\\');
        int separator = Math.Max(
            slash,
            backslash);
        return separator < 0
            ? value
            : value[(separator + 1)..];
    }
}
