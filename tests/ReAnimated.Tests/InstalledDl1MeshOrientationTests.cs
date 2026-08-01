using System.Numerics;
using ReAnimated.App.Infrastructure;
using ReAnimated.Codecs.Rp6l;
using ReAnimated.DL1.Assets.Discovery;
using ReAnimated.DL1.Assets.Meshes;
using ReAnimated.Renderer.D3D11;
using Xunit.Abstractions;

namespace ReAnimated.Tests;

public sealed class InstalledDl1MeshOrientationTests
{
    private readonly ITestOutputHelper _output;

    public InstalledDl1MeshOrientationTests(
        ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public async Task PlayerFppTriangleWindingAgreesWithDecodedNormals()
    {
        Dl1InstallLocation? install = SteamInstallDiscovery
            .Discover()
            .FirstOrDefault(static location =>
                location.IsValid);
        if (install is null)
        {
            return;
        }

        string packPath = Path.Combine(
            install.DataPath,
            "common_cod_1_PC.rpack");
        if (!File.Exists(packPath))
        {
            return;
        }

        Rp6lArchive archive =
            await Rp6lArchive.OpenAsync(packPath);
        Rp6lResourceDescriptor? resource =
            archive.FindResource(
                Rp6lResourceTypes.Mesh,
                "player_1_fpp");
        if (resource is null)
        {
            return;
        }

        string cacheDirectory =
            RpackTestData.CreateTemporaryDirectory();
        try
        {
            await using var cache =
                new Rp6lChunkCache(
                    new Rp6lChunkCacheOptions
                    {
                        CacheDirectory =
                            Path.Combine(
                                cacheDirectory,
                                "cache"),
                        MaximumMemoryBytes = 0,
                        MaximumMemoryEntryBytes = 0,
                        MaximumDiskBytes =
                            512L * 1024 * 1024,
                    });
            Dl1MeshData decoded =
                await Dl1MeshResourceDecoder.DecodeAsync(
                    archive,
                    resource,
                    cache);
            Dl1MeshPreviewPayload preview =
                Dl1MeshPreviewAdapter.Convert(decoded);

            double alignedArea = 0.0;
            double opposedArea = 0.0;
            double weightedDot = 0.0;
            int triangleCount = 0;
            int reflectedSkinMatrixCount = 0;
            foreach (MeshRenderData mesh in preview.Meshes)
            {
                if (mesh.IsSkinned)
                {
                    foreach (Matrix4x4 matrix in
                             GpuSkinningPalette.Build(
                                 mesh,
                                 preview.Skeleton!))
                    {
                        if (matrix.GetDeterminant() < 0.0f)
                        {
                            reflectedSkinMatrixCount++;
                        }
                    }
                }

                CpuDeformedVertex[] vertices =
                    CpuMeshDeformationEvaluator.Evaluate(
                        mesh,
                        preview.Skeleton,
                        []);
                ReadOnlySpan<uint> indices =
                    mesh.Indices.Span;
                for (int offset = 0;
                     offset < indices.Length;
                     offset += 3)
                {
                    CpuDeformedVertex first =
                        vertices[checked((int)indices[offset])];
                    CpuDeformedVertex second =
                        vertices[checked((int)indices[offset + 1])];
                    CpuDeformedVertex third =
                        vertices[checked((int)indices[offset + 2])];
                    Vector3 cross = Vector3.Cross(
                        second.Position - first.Position,
                        third.Position - first.Position);
                    float twiceArea = cross.Length();
                    Vector3 stored =
                        first.Normal +
                        second.Normal +
                        third.Normal;
                    if (!float.IsFinite(twiceArea) ||
                        twiceArea <= 1.0e-10f ||
                        stored.LengthSquared() <= 1.0e-10f)
                    {
                        continue;
                    }

                    double dot = Vector3.Dot(
                        cross / twiceArea,
                        Vector3.Normalize(stored));
                    weightedDot += dot * twiceArea;
                    if (dot >= 0.0)
                    {
                        alignedArea += twiceArea;
                    }
                    else
                    {
                        opposedArea += twiceArea;
                    }

                    triangleCount++;
                }
            }

            double totalArea =
                alignedArea + opposedArea;
            double alignment =
                totalArea <= 0.0
                    ? 0.0
                    : weightedDot / totalArea;
            double opposedRatio =
                totalArea <= 0.0
                    ? 1.0
                    : opposedArea / totalArea;
            _output.WriteLine(
                $"player_1_fpp triangles={triangleCount:N0}, deformed normal/winding alignment={alignment:F6}, opposed area={opposedRatio:P3}, reflected bind skin matrices={reflectedSkinMatrixCount:N0}");
            Assert.True(
                triangleCount > 0,
                "Installed player_1_fpp produced no nondegenerate preview triangles.");
            Assert.True(
                alignment >= 0.75,
                $"Decoded player_1_fpp has incoherent triangle winding and normals: {alignment:F6}.");
            Assert.True(
                opposedRatio <= 0.01,
                $"Decoded player_1_fpp has {opposedRatio:P3} of its surface area opposed to decoded normals.");
            Assert.Equal(0, reflectedSkinMatrixCount);
        }
        finally
        {
            RpackTestData.DeleteTemporaryDirectory(
                cacheDirectory);
        }
    }
}
