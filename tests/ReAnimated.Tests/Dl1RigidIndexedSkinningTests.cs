using System.Buffers.Binary;
using System.Numerics;
using ReAnimated.App.Infrastructure;
using ReAnimated.Codecs.Rp6l;
using ReAnimated.DL1.Assets.Meshes;

namespace ReAnimated.Tests;

public sealed class Dl1RigidIndexedSkinningTests
{
    [Fact]
    public async Task DecodesAndPublishesRigidIndexedPaletteWithoutRewritingRawWeights()
    {
        string directory =
            RpackTestData.CreateTemporaryDirectory();
        try
        {
            (Dl1MeshData mesh,
                Rp6lResourceDescriptor resource,
                Rp6lChunkCache cache) =
                await DecodeFixtureAsync(
                    directory,
                    "rigid_indexed",
                    static (vertices, vertexIndex) =>
                    {
                        ClearWeightAndIndexes(
                            vertices,
                            vertexIndex,
                            primaryIndex: 1);
                    });
            await using (cache)
            {
                Dl1MeshSurface surface =
                    Assert.Single(mesh.Surfaces);
                Dl1MeshSubmesh submesh =
                    Assert.Single(surface.Submeshes);
                Assert.Equal(
                    Dl1SkinBindingMode.RigidIndexedPalette,
                    submesh.SkinBindingMode);
                Assert.All(
                    surface.Vertices,
                    static vertex =>
                    {
                        Assert.Equal(
                            Vector4.Zero,
                            vertex.BlendWeights);
                        Assert.Equal(
                            new Dl1BoneIndex4(1, 0, 0, 0),
                            vertex.LocalBlendIndices);
                    });
                Assert.Contains(
                    mesh.Diagnostics,
                    static diagnostic =>
                        diagnostic.Code == "DL1MESH015" &&
                        diagnostic.Severity ==
                            Dl1MeshDiagnosticSeverity
                                .Information &&
                        diagnostic.Message.Contains(
                            "not yet live-game validated",
                            StringComparison.Ordinal));

                var validator =
                    new Dl1MeshCorpusValidator(cache);
                Dl1MeshCorpusResourceResult result =
                    validator.ValidateDecodedMesh(
                        resource,
                        mesh);
                Assert.True(
                    result.Passed,
                    string.Join(
                        Environment.NewLine,
                        result.Issues.Select(static issue =>
                            $"{issue.Code}: {issue.Message}")));
                Assert.Contains(
                    result.Issues,
                    static issue =>
                        issue.Code == "DL1CORPUS059" &&
                        issue.Severity ==
                            Dl1MeshCorpusIssueSeverity.Warning);
                Assert.DoesNotContain(
                    result.Issues,
                    static issue =>
                        issue.Code == "DL1CORPUS053");

                Dl1MeshPreviewPayload preview =
                    Dl1MeshPreviewAdapter.Convert(mesh);
                ReAnimated.Renderer.D3D11.MeshRenderData
                    renderMesh = Assert.Single(preview.Meshes);
                Assert.Equal(
                    [0, 0],
                    renderMesh.SkinBoneIndices.ToArray());
                Assert.All(
                    renderMesh.Vertices.ToArray(),
                    static vertex =>
                    {
                        Assert.Equal(
                            Vector4.UnitX,
                            vertex.BoneWeights);
                        Assert.Equal(
                            new Vector4(1, 0, 0, 0),
                            vertex.BoneIndices);
                    });

                Assert.All(
                    surface.Vertices,
                    static vertex =>
                        Assert.Equal(
                            Vector4.Zero,
                            vertex.BlendWeights));
            }
        }
        finally
        {
            RpackTestData.DeleteTemporaryDirectory(directory);
        }
    }

    [Theory]
    [InlineData(true, 1)]
    [InlineData(false, 2)]
    public async Task MixedOrOutOfPaletteZeroWeightsRemainBlocked(
        bool leaveWeightedVertices,
        byte zeroVertexPrimaryIndex)
    {
        string directory =
            RpackTestData.CreateTemporaryDirectory();
        try
        {
            (Dl1MeshData mesh,
                Rp6lResourceDescriptor resource,
                Rp6lChunkCache cache) =
                await DecodeFixtureAsync(
                    directory,
                    "unproven_zero_weights",
                    (vertices, vertexIndex) =>
                    {
                        if (vertexIndex == 0 ||
                            !leaveWeightedVertices)
                        {
                            ClearWeightAndIndexes(
                                vertices,
                                vertexIndex,
                                zeroVertexPrimaryIndex);
                        }
                    });
            await using (cache)
            {
                Dl1MeshSubmesh submesh = Assert.Single(
                    Assert.Single(mesh.Surfaces).Submeshes);
                Assert.Equal(
                    Dl1SkinBindingMode.ExplicitVertexWeights,
                    submesh.SkinBindingMode);
                Assert.DoesNotContain(
                    mesh.Diagnostics,
                    static diagnostic =>
                        diagnostic.Code == "DL1MESH015");

                var validator =
                    new Dl1MeshCorpusValidator(cache);
                Dl1MeshCorpusResourceResult result =
                    validator.ValidateDecodedMesh(
                        resource,
                        mesh);
                Assert.False(result.Passed);
                Assert.Contains(
                    result.Issues,
                    static issue =>
                        issue.Code == "DL1CORPUS053" &&
                        issue.Severity ==
                            Dl1MeshCorpusIssueSeverity.Error);
                Assert.DoesNotContain(
                    result.Issues,
                    static issue =>
                        issue.Code == "DL1CORPUS059");
            }
        }
        finally
        {
            RpackTestData.DeleteTemporaryDirectory(directory);
        }
    }

    private static async Task<(
        Dl1MeshData Mesh,
        Rp6lResourceDescriptor Resource,
        Rp6lChunkCache Cache)> DecodeFixtureAsync(
        string directory,
        string name,
        Action<byte[], int> mutateVertex)
    {
        CompiledMeshTestFixture fixture =
            RpackTestData.BuildCompiledMeshFixture();
        const int paletteTableOffset = 0x350;
        const int paletteValuesOffset = 0x370;
        BinaryPrimitives.WriteInt32LittleEndian(
            fixture.Metadata.AsSpan(
                paletteTableOffset + 8),
            2);
        BinaryPrimitives.WriteInt16LittleEndian(
            fixture.Metadata.AsSpan(
                paletteValuesOffset),
            0);
        BinaryPrimitives.WriteInt16LittleEndian(
            fixture.Metadata.AsSpan(
                paletteValuesOffset + sizeof(short)),
            0);
        for (int vertexIndex = 0;
             vertexIndex < 3;
             vertexIndex++)
        {
            mutateVertex(
                fixture.Vertices,
                vertexIndex);
        }

        string path = await RpackTestData.WriteArchiveAsync(
            directory,
            name,
            Rp6lResourceTypes.Mesh,
            [
                new RpackTestItem(42, fixture.Metadata),
                new RpackTestItem(42, fixture.Variants),
                new RpackTestItem(42, [1]),
                new RpackTestItem(42, fixture.Vertices),
                new RpackTestItem(42, fixture.Indices),
            ],
            RpackTestCompression.None);
        Rp6lArchive archive =
            await Rp6lArchive.OpenAsync(path);
        Rp6lResourceDescriptor resource =
            Assert.Single(archive.Resources);
        var cache = new Rp6lChunkCache(
            new Rp6lChunkCacheOptions
            {
                CacheDirectory =
                    Path.Combine(directory, "cache"),
                MaximumMemoryBytes = 0,
                MaximumMemoryEntryBytes = 0,
                MaximumDiskBytes = 8 * 1024 * 1024,
            });
        Dl1MeshData mesh =
            await Dl1MeshResourceDecoder.DecodeAsync(
                archive,
                resource,
                cache);
        return (mesh, resource, cache);
    }

    private static void ClearWeightAndIndexes(
        byte[] vertices,
        int vertexIndex,
        byte primaryIndex)
    {
        const int stride = 28;
        const int weightOffset = 12;
        const int indexOffset = 16;
        Span<byte> vertex = vertices.AsSpan(
            checked(vertexIndex * stride),
            stride);
        vertex.Slice(weightOffset, sizeof(uint)).Clear();
        vertex.Slice(indexOffset, sizeof(uint)).Clear();
        vertex[indexOffset] = primaryIndex;
    }
}
