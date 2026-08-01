using System.Buffers.Binary;
using System.Text;
using ReAnimated.Codecs.Rp6l;
using ReAnimated.DL1.Assets.Meshes;
using ReAnimated.DL1.Assets.Providers;

namespace ReAnimated.Tests;

public sealed class Dl1ShadowCasterCorpusPolicyTests
{
    private const int FixturePaletteTableOffset = 0x350;
    private const int FixturePaletteValuesOffset = 0x370;
    private const int ReplacementMaterialNameOffset = 0x4A0;

    [Theory]
    [InlineData("shadow_caster.mat", "DL1CORPUS058")]
    [InlineData("SHADOW_CASTER.MAT", "DL1CORPUS058")]
    [InlineData("shadowcaster.mat", "DL1CORPUS058")]
    [InlineData("shadow_caster_2s.mat", "DL1CORPUS058")]
    [InlineData("null.mat", "DL1CORPUS058")]
    [InlineData("DEFAULT.MAT", "DL1CORPUS058")]
    [InlineData("characters_body", "DL1CORPUS066")]
    public async Task NoBlendPaletteUsesNonDisplayOmissionOrStaticRuntimePath(
        string materialName,
        string expectedWarning)
    {
        string directory =
            RpackTestData.CreateTemporaryDirectory();
        try
        {
            CompiledMeshTestFixture fixture =
                RpackTestData.BuildCompiledMeshFixture(
                    includeBlendStreams: false);
            byte[] metadata = fixture.Metadata.ToArray();
            Array.Resize(
                ref metadata,
                ReplacementMaterialNameOffset + 32);
            BinaryPrimitives.WriteInt32LittleEndian(
                metadata.AsSpan(
                    FixturePaletteTableOffset + 8),
                2);
            BinaryPrimitives.WriteInt16LittleEndian(
                metadata.AsSpan(
                    FixturePaletteValuesOffset +
                    sizeof(short)),
                0);
            BinaryPrimitives.WriteUInt64LittleEndian(
                metadata.AsSpan(
                    RpackTestData
                        .CompiledMeshMaterialDatabaseEntriesOffset +
                    2 * 24),
                ReplacementMaterialNameOffset + 1);
            Encoding.ASCII
                .GetBytes(materialName + '\0')
                .CopyTo(
                    metadata.AsSpan(
                        ReplacementMaterialNameOffset));
            string archivePath =
                await RpackTestData.WriteArchiveAsync(
                    directory,
                    "multi_palette_fixture",
                    Rp6lResourceTypes.Mesh,
                    [
                        new RpackTestItem(42, metadata),
                        new RpackTestItem(
                            42,
                            fixture.Variants),
                        new RpackTestItem(42, []),
                        new RpackTestItem(
                            42,
                            fixture.Vertices),
                        new RpackTestItem(
                            42,
                            fixture.Indices),
                    ],
                    RpackTestCompression.None);
            Rp6lArchive archive =
                await Rp6lArchive.OpenAsync(archivePath);
            await using Rp6lChunkCache cache =
                CreateCache(directory);
            Rp6lResourceDescriptor resource =
                Assert.Single(archive.Resources);
            Dl1MeshData mesh =
                await Dl1MeshResourceDecoder.DecodeAsync(
                    archive,
                    resource,
                    cache);
            Dl1MeshSubmesh submesh = Assert.Single(
                Assert.Single(mesh.Surfaces).Submeshes);
            Assert.Equal(
                Dl1SkinBindingMode
                    .StaticEntityTransformIgnoredPalette,
                submesh.SkinBindingMode);
            Assert.Equal(2, submesh.BonePaletteEntityIndexes.Count);

            var validator =
                new Dl1MeshCorpusValidator(cache);
            Dl1MeshCorpusResourceResult result =
                validator.ValidateDecodedMesh(resource, mesh);

            Assert.True(
                result.Passed,
                string.Join(
                    Environment.NewLine,
                    result.Issues.Select(static issue =>
                        $"{issue.Code}: {issue.Message}")));
            Assert.Contains(
                result.Issues,
                issue =>
                    issue.Code == expectedWarning &&
                    issue.Severity ==
                        Dl1MeshCorpusIssueSeverity.Warning);
            Assert.DoesNotContain(
                result.Issues,
                static issue =>
                    issue.Code == "DL1CORPUS055");
        }
        finally
        {
            RpackTestData.DeleteTemporaryDirectory(directory);
        }
    }

    private static Rp6lChunkCache CreateCache(
        string directory) =>
        new(new Rp6lChunkCacheOptions
        {
            CacheDirectory =
                Path.Combine(directory, "cache"),
            MaximumMemoryBytes = 0,
            MaximumMemoryEntryBytes = 0,
            MaximumDiskBytes = 64 * 1024 * 1024,
        });
}
