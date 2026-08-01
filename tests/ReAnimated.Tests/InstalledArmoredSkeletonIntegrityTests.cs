using System.Numerics;
using ReAnimated.App.Infrastructure;
using ReAnimated.Codecs.CompactMesh;
using ReAnimated.Codecs.Rp6l;
using ReAnimated.DL1.Assets.Discovery;
using ReAnimated.DL1.Assets.Meshes;
using ReAnimated.Renderer.D3D11;
using Xunit.Abstractions;

namespace ReAnimated.Tests;

public sealed class InstalledArmoredSkeletonIntegrityTests
{
    private const string ValidatedBuildFingerprint =
        "89f98e5c77a2eb36767a614acf894a4f18f55e6efa8f912ca7ef66b45a0dfa13";

    private readonly ITestOutputHelper _output;

    public InstalledArmoredSkeletonIntegrityTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public async Task InstalledArmoredBindHierarchyAndSkinPalettesStayCoherent()
    {
        Dl1InstallLocation? install = SteamInstallDiscovery
            .Discover()
            .FirstOrDefault(static location => location.IsValid);
        if (install is null)
        {
            return;
        }

        Dl1InstalledBuildFingerprint build =
            await new Dl1InstalledBuildFingerprintService()
                .ReadAsync(install.InstallPath);
        if (!string.Equals(
                build.BuildFingerprint,
                ValidatedBuildFingerprint,
                StringComparison.OrdinalIgnoreCase))
        {
            _output.WriteLine(
                $"Installed armored control skipped for build {build.BuildFingerprint}.");
            return;
        }

        string packPath = Path.Combine(
            install.DataPath,
            "common_meshes_PC.rpack");
        if (!File.Exists(packPath))
        {
            return;
        }

        Rp6lArchive archive = await Rp6lArchive.OpenAsync(packPath);
        Rp6lResourceDescriptor descriptor =
            Assert.IsType<Rp6lResourceDescriptor>(
                archive.FindResource(
                    Rp6lResourceTypes.Mesh,
                    "armored"));
        string temporaryDirectory =
            RpackTestData.CreateTemporaryDirectory();
        try
        {
            await using var cache = new Rp6lChunkCache(
                new Rp6lChunkCacheOptions
                {
                    CacheDirectory =
                        Path.Combine(temporaryDirectory, "cache"),
                    MaximumMemoryBytes = 0,
                    MaximumMemoryEntryBytes = 0,
                    MaximumDiskBytes = 512L * 1024 * 1024,
                });
            Dl1MeshData mesh =
                await Dl1MeshResourceDecoder.DecodeAsync(
                    archive,
                    descriptor,
                    cache);
            Dl1MeshPreviewPayload preview =
                Dl1MeshPreviewAdapter.Convert(mesh);
            SkeletonRenderData skeleton =
                Assert.IsType<SkeletonRenderData>(preview.Skeleton);
            IReadOnlyList<CompactMatrix3x4> compactWorld =
                mesh.Hierarchy.ReconstructGlobalMatrices();

            Assert.Equal(77, skeleton.Bones.Count);
            Assert.Equal(57, mesh.Hierarchy.Bones.Count);
            Assert.Equal(20, mesh.Hierarchy.Helpers.Count);
            Assert.Equal(
                57,
                skeleton.Bones.Count(static bone =>
                    bone.Role == BoneRenderRole.Deform));
            Assert.Equal(
                18,
                skeleton.Bones.Count(static bone =>
                    bone.Role == BoneRenderRole.Helper));
            Assert.Equal(
                2,
                skeleton.Bones.Count(static bone =>
                    bone.Role == BoneRenderRole.Camera));
            Assert.Equal(
                20,
                skeleton.Bones.Count(static bone =>
                    bone.Role is
                        BoneRenderRole.Helper or
                        BoneRenderRole.Camera));
            Assert.Equal(
                [
                    "eyecamera",
                    "refcamera",
                ],
                skeleton.Bones
                    .Where(static bone =>
                        bone.Role == BoneRenderRole.Camera)
                    .Select(static bone => bone.Name)
                    .OrderBy(static name => name));
            Assert.DoesNotContain(
                skeleton.Bones,
                static bone =>
                    bone.Role == BoneRenderRole.Prop);
            Assert.DoesNotContain(preview.Diagnostics, static diagnostic =>
                diagnostic.Contains(
                    "singular bind",
                    StringComparison.OrdinalIgnoreCase));

            float maximumReferenceIdentityError = 0.0f;
            string maximumReferenceIdentityEntity = string.Empty;
            for (int index = 0; index < skeleton.Bones.Count; index++)
            {
                CompactMeshEntity entity = mesh.Hierarchy.Entities[index];
                BoneRenderData bone = skeleton.Bones[index];
                Assert.Equal(index, entity.Index);
                Assert.Equal(entity.Name, bone.Name);
                Assert.Equal(
                    entity.ParentIndex,
                    bone.ParentIndex);
                AssertMatrixClose(
                    Dl1MeshPreviewAdapter.ConvertMatrix(
                        compactWorld[index]),
                    bone.WorldTransform);
                Matrix4x4 reference =
                    Dl1MeshPreviewAdapter.ConvertMatrix(
                        entity.ReferenceMatrix);
                Matrix4x4 identityCandidate =
                    reference * bone.WorldTransform;
                AssertMatrixClose(
                    Matrix4x4.Identity,
                    identityCandidate);
                float identityError =
                    MaximumIdentityError(identityCandidate);
                if (identityError > maximumReferenceIdentityError)
                {
                    maximumReferenceIdentityError = identityError;
                    maximumReferenceIdentityEntity = entity.Name;
                }
            }

            Assert.InRange(
                maximumReferenceIdentityError,
                0.0f,
                1.0e-5f);
            _output.WriteLine(
                $"maximum global/reference identity error " +
                $"{maximumReferenceIdentityError:E6} at " +
                $"{maximumReferenceIdentityEntity}.");

            int paletteCount = 0;
            HashSet<int> paletteEntities = [];
            foreach (Dl1MeshSubmesh submesh in mesh.Surfaces
                         .SelectMany(static surface => surface.Submeshes))
            {
                foreach (short entityIndex in
                         submesh.BonePaletteEntityIndexes)
                {
                    Assert.InRange(
                        entityIndex,
                        0,
                        skeleton.Bones.Count - 1);
                    paletteEntities.Add(entityIndex);
                    paletteCount++;
                }
            }

            Assert.True(paletteCount > 0);
            Assert.DoesNotContain(
                paletteEntities,
                index => !mesh.Hierarchy.Entities[index].EntityType
                    .HasFlag(CompactMeshEntityType.Bone));

            foreach (MeshRenderData renderMesh in
                     preview.Meshes.Where(static item => item.IsSkinned))
            {
                Matrix4x4[] bindPalette =
                    GpuSkinningPalette.Build(renderMesh, skeleton);
                Assert.All(bindPalette, static matrix =>
                    AssertMatrixClose(Matrix4x4.Identity, matrix));
            }

            WriteAndValidateSymmetry(
                mesh,
                skeleton);
            WriteMeshBounds(preview.Meshes, skeleton);
        }
        finally
        {
            RpackTestData.DeleteTemporaryDirectory(
                temporaryDirectory);
        }
    }

    private void WriteAndValidateSymmetry(
        Dl1MeshData mesh,
        SkeletonRenderData skeleton)
    {
        Dictionary<string, int> byName = skeleton.Bones
            .Select(static (bone, index) => (bone.Name, Index: index))
            .ToDictionary(
                static item => item.Name,
                static item => item.Index,
                StringComparer.OrdinalIgnoreCase);
        int pairCount = 0;
        float maximumMirrorResidual = 0.0f;
        string maximumMirrorPair = string.Empty;
        foreach ((string leftName, int leftIndex) in byName
                     .Where(static item =>
                         item.Key.StartsWith(
                             "l_",
                             StringComparison.OrdinalIgnoreCase))
                     .OrderBy(static item => item.Key))
        {
            string rightName = $"r_{leftName[2..]}";
            if (!byName.TryGetValue(rightName, out int rightIndex))
            {
                continue;
            }

            Vector3 left =
                skeleton.Bones[leftIndex].WorldTransform.Translation;
            Vector3 right =
                skeleton.Bones[rightIndex].WorldTransform.Translation;
            float residual = MathF.Max(
                MathF.Abs(left.X + right.X),
                MathF.Max(
                    MathF.Abs(left.Y - right.Y),
                    MathF.Abs(left.Z - right.Z)));
            if (residual > maximumMirrorResidual)
            {
                maximumMirrorResidual = residual;
                maximumMirrorPair = $"{leftName}/{rightName}";
            }

            pairCount++;
        }

        Assert.Equal(27, pairCount);
        Assert.InRange(maximumMirrorResidual, 0.0f, 0.006f);
        _output.WriteLine(
            $"{pairCount} bilateral pairs; maximum mirror residual " +
            $"{maximumMirrorResidual:E6} at {maximumMirrorPair}; " +
            $"{mesh.Hierarchy.Helpers.Count} unweighted helpers are " +
            "retained as animation entities.");
    }

    private void WriteMeshBounds(
        IReadOnlyList<MeshRenderData> meshes,
        SkeletonRenderData skeleton)
    {
        Vector3 minimum =
            new(float.PositiveInfinity);
        Vector3 maximum =
            new(float.NegativeInfinity);
        long vertexCount = 0;
        foreach (MeshRenderData mesh in meshes)
        {
            CpuDeformedVertex[] vertices =
                CpuMeshDeformationEvaluator.Evaluate(
                    mesh,
                    skeleton,
                    []);
            foreach (CpuDeformedVertex vertex in vertices)
            {
                Vector3 position = vertex.Position;
                minimum = Vector3.Min(minimum, position);
                maximum = Vector3.Max(maximum, position);
            }

            vertexCount += vertices.Length;
        }

        Assert.Equal(12_928, vertexCount);
        foreach (BoneRenderData bone in skeleton.Bones)
        {
            Vector3 position = bone.WorldTransform.Translation;
            Assert.InRange(
                position.X,
                minimum.X - 0.05f,
                maximum.X + 0.05f);
            Assert.InRange(
                position.Y,
                minimum.Y - 0.05f,
                maximum.Y + 0.05f);
            Assert.InRange(
                position.Z,
                minimum.Z - 0.05f,
                maximum.Z + 0.05f);
        }

        _output.WriteLine(
            $"selected visible render bounds for {vertexCount:N0} remapped vertices: " +
            $"min=({minimum.X:F6},{minimum.Y:F6},{minimum.Z:F6}), " +
            $"max=({maximum.X:F6},{maximum.Y:F6},{maximum.Z:F6}), " +
            $"center={((minimum + maximum) * 0.5f)}, " +
            $"size={(maximum - minimum)}");
    }

    private static void AssertMatrixClose(
        Matrix4x4 expected,
        Matrix4x4 actual)
    {
        ReadOnlySpan<float> expectedValues =
        [
            expected.M11, expected.M12, expected.M13, expected.M14,
            expected.M21, expected.M22, expected.M23, expected.M24,
            expected.M31, expected.M32, expected.M33, expected.M34,
            expected.M41, expected.M42, expected.M43, expected.M44,
        ];
        ReadOnlySpan<float> actualValues =
        [
            actual.M11, actual.M12, actual.M13, actual.M14,
            actual.M21, actual.M22, actual.M23, actual.M24,
            actual.M31, actual.M32, actual.M33, actual.M34,
            actual.M41, actual.M42, actual.M43, actual.M44,
        ];
        for (int index = 0; index < expectedValues.Length; index++)
        {
            Assert.InRange(
                MathF.Abs(
                    expectedValues[index] - actualValues[index]),
                0.0f,
                1.0e-4f);
        }
    }

    private static float MaximumIdentityError(Matrix4x4 matrix)
    {
        ReadOnlySpan<float> values =
        [
            matrix.M11 - 1.0f, matrix.M12, matrix.M13, matrix.M14,
            matrix.M21, matrix.M22 - 1.0f, matrix.M23, matrix.M24,
            matrix.M31, matrix.M32, matrix.M33 - 1.0f, matrix.M34,
            matrix.M41, matrix.M42, matrix.M43, matrix.M44 - 1.0f,
        ];
        float maximum = 0.0f;
        foreach (float value in values)
        {
            maximum = MathF.Max(maximum, MathF.Abs(value));
        }

        return maximum;
    }
}
