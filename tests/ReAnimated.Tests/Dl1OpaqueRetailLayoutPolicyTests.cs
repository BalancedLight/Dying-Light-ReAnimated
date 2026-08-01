using System.Buffers.Binary;
using System.Numerics;
using ReAnimated.App.Infrastructure;
using ReAnimated.Codecs.CompactMesh;
using ReAnimated.Codecs.Rp6l;
using ReAnimated.DL1.Assets.Meshes;

namespace ReAnimated.Tests;

public sealed class Dl1OpaqueRetailLayoutPolicyTests
{
    private const int EntityTableOffset = 0xB0;
    private const int EntityStride = 0xD0;
    private const int ReferenceMatrixOffset = 0x30;
    private const int ParentIndexOffset = 0xC6;
    private const int EntityTypeOffset = 0xC8;

    [Fact]
    public async Task PlainStaticRootRetainsOpaqueReferenceMatrixAsWarning()
    {
        string directory =
            RpackTestData.CreateTemporaryDirectory();
        try
        {
            byte[] metadata = BuildSingleEntityPayload(
                CompactMeshEntityType.Mesh);
            WriteNonFinite(
                metadata,
                EntityTableOffset + ReferenceMatrixOffset);
            string archivePath =
                await RpackTestData.WriteArchiveAsync(
                    directory,
                    "static_reference_fixture",
                    Rp6lResourceTypes.Mesh,
                    [
                        new RpackTestItem(42, metadata),
                        new RpackTestItem(42, []),
                        new RpackTestItem(42, []),
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

            CompactMeshEntity entity =
                Assert.Single(mesh.Hierarchy.Entities);
            Assert.True(entity.LocalMatrix.IsFinite);
            Assert.False(entity.ReferenceMatrix.IsFinite);
            Assert.True(mesh.Hierarchy.IsStructurallyValid);
            Assert.Contains(
                mesh.Hierarchy.Diagnostics,
                static diagnostic =>
                    diagnostic.Code == "CMESH011" &&
                    diagnostic.Severity ==
                        CompactMeshDiagnosticSeverity.Warning);
            Assert.DoesNotContain(
                mesh.Hierarchy.Diagnostics,
                static diagnostic =>
                    diagnostic.Code == "CMESH003");
            Assert.Equal(
                entity.LocalMatrix,
                Assert.Single(
                    mesh.Hierarchy
                        .ReconstructGlobalMatrices()));

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
                static issue =>
                    issue.Code == "CMESH011" &&
                    issue.Severity ==
                        Dl1MeshCorpusIssueSeverity.Warning);
            Assert.DoesNotContain(
                result.Issues,
                static issue =>
                    issue.Code is
                        "CMESH003" or
                        "DL1CORPUS012" or
                        "DL1CORPUS040");
        }
        finally
        {
            RpackTestData.DeleteTemporaryDirectory(directory);
        }
    }

    [Theory]
    [InlineData(CompactMeshEntityType.Bone)]
    [InlineData(CompactMeshEntityType.Helper)]
    [InlineData(CompactMeshEntityType.SkinnedMesh)]
    public void AnimationBearingRootRequiresFiniteReferenceMatrix(
        CompactMeshEntityType entityType)
    {
        byte[] payload =
            BuildSingleEntityPayload(entityType);
        WriteNonFinite(
            payload,
            EntityTableOffset + ReferenceMatrixOffset);

        CompactMeshDocument document =
            CompactMeshDecoder.Decode(payload);

        Assert.False(document.IsStructurallyValid);
        Assert.Contains(
            document.Diagnostics,
            static diagnostic =>
                diagnostic.Code == "CMESH003" &&
                diagnostic.Severity ==
                    CompactMeshDiagnosticSeverity.Error);
        Assert.DoesNotContain(
            document.Diagnostics,
            static diagnostic =>
                diagnostic.Code == "CMESH011");
    }

    [Fact]
    public void NonRootMeshRequiresFiniteReferenceMatrix()
    {
        byte[] payload =
            RpackTestData.BuildCompactMeshPayload();
        int entityOffset =
            EntityTableOffset + 2 * EntityStride;
        payload[entityOffset + EntityTypeOffset] =
            (byte)CompactMeshEntityType.Mesh;
        Assert.True(
            BinaryPrimitives.ReadInt16LittleEndian(
                payload.AsSpan(
                    entityOffset + ParentIndexOffset)) >= 0);
        WriteNonFinite(
            payload,
            entityOffset + ReferenceMatrixOffset);

        CompactMeshDocument document =
            CompactMeshDecoder.Decode(payload);

        Assert.False(document.IsStructurallyValid);
        Assert.Contains(
            document.Diagnostics,
            static diagnostic =>
                diagnostic.Code == "CMESH003" &&
                diagnostic.EntityIndex == 2);
        Assert.DoesNotContain(
            document.Diagnostics,
            static diagnostic =>
                diagnostic.Code == "CMESH011" &&
                diagnostic.EntityIndex == 2);
    }

    [Fact]
    public void PlainStaticRootStillRequiresFiniteLocalMatrix()
    {
        byte[] payload = BuildSingleEntityPayload(
            CompactMeshEntityType.Mesh);
        WriteNonFinite(payload, EntityTableOffset);

        CompactMeshDocument document =
            CompactMeshDecoder.Decode(payload);

        Assert.False(document.IsStructurallyValid);
        Assert.Contains(
            document.Diagnostics,
            static diagnostic =>
                diagnostic.Code == "CMESH003" &&
                diagnostic.Message.Contains(
                    "local matrix",
                    StringComparison.Ordinal));
        Assert.DoesNotContain(
            document.Diagnostics,
            static diagnostic =>
                diagnostic.Code == "CMESH011");
    }

    [Fact]
    public async Task SerializedHalfInfinityIsRetainedAndBlocksReferencedUv()
    {
        string directory =
            RpackTestData.CreateTemporaryDirectory();
        try
        {
            CompiledMeshTestFixture fixture =
                RpackTestData.BuildCompiledMeshFixture();
            byte[] vertices = fixture.Vertices.ToArray();
            const int firstVertexUvOffset = 24;
            BinaryPrimitives.WriteUInt16LittleEndian(
                vertices.AsSpan(firstVertexUvOffset),
                0x7C00);
            string archivePath =
                await RpackTestData.WriteArchiveAsync(
                    directory,
                    "nonfinite_uv_fixture",
                    Rp6lResourceTypes.Mesh,
                    [
                        new RpackTestItem(
                            42,
                            fixture.Metadata),
                        new RpackTestItem(
                            42,
                            fixture.Variants),
                        new RpackTestItem(42, []),
                        new RpackTestItem(42, vertices),
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
            Dl1MeshSurface surface =
                Assert.Single(mesh.Surfaces);

            Assert.True(
                float.IsPositiveInfinity(
                    surface.Vertices[0]
                        .TextureCoordinate0.X));

            var validator =
                new Dl1MeshCorpusValidator(cache);
            Dl1MeshCorpusResourceResult result =
                validator.ValidateDecodedMesh(resource, mesh);

            Assert.False(result.Passed);
            Assert.Contains(
                result.Issues,
                static issue =>
                    issue.Code == "DL1CORPUS028" &&
                    issue.Severity ==
                        Dl1MeshCorpusIssueSeverity.Error &&
                    issue.Message.Contains(
                        "referenced vertices with non-finite UV0",
                        StringComparison.Ordinal));
        }
        finally
        {
            RpackTestData.DeleteTemporaryDirectory(directory);
        }
    }

    [Theory]
    [InlineData("shadow_caster.mat")]
    [InlineData("SHADOWCASTER.MAT")]
    [InlineData("shadow_caster_2s.mat")]
    [InlineData("null.mat")]
    [InlineData("DEFAULT.MAT")]
    public void ExactNonDisplayPartCanRetainRawNonFiniteUv(
        string materialName)
    {
        string directory =
            RpackTestData.CreateTemporaryDirectory();
        try
        {
            using Rp6lChunkCache cache =
                CreateCache(directory);
            Dl1MeshData mesh = CreateUvPolicyMesh(
                materialName,
                shareBadVertexWithVisiblePart: false);
            var validator =
                new Dl1MeshCorpusValidator(cache);

            Dl1MeshCorpusResourceResult result =
                validator.ValidateDecodedMesh(
                    CreateResource("shadow_uv"),
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
                    issue.Code == "DL1CORPUS034" &&
                    issue.Severity ==
                        Dl1MeshCorpusIssueSeverity.Warning &&
                    issue.Message.Contains(
                        "Raw vertex values are retained",
                        StringComparison.Ordinal));
            Assert.DoesNotContain(
                result.Issues,
                static issue =>
                    issue.Code == "DL1CORPUS028");

            Dl1MeshPreviewPayload preview =
                Dl1MeshPreviewAdapter.Convert(mesh);
            Assert.Single(preview.Meshes);
            Assert.Contains(
                preview.Diagnostics,
                static diagnostic =>
                    diagnostic.Contains(
                        "validated non-display DL1",
                        StringComparison.Ordinal));
        }
        finally
        {
            RpackTestData.DeleteTemporaryDirectory(directory);
        }
    }

    [Fact]
    public void ResolvedZeroTechniquePartCanRetainRawNonFiniteUv()
    {
        string directory =
            RpackTestData.CreateTemporaryDirectory();
        try
        {
            using Rp6lChunkCache cache =
                CreateCache(directory);
            Dl1MeshData source = CreateUvPolicyMesh(
                "custom_zero.mat",
                shareBadVertexWithVisiblePart: false);
            Dl1MaterialSlot[] materialSlots =
                source.MaterialSlots.ToArray();
            materialSlots[1] = materialSlots[1] with
            {
                BindingStatus = Dl1MaterialBindingStatus.Resolved,
                ResolvedMaterial = new(
                    "custom_zero.mat",
                    0,
                    0,
                    []),
            };
            Dl1MeshData mesh = source with
            {
                MaterialSlots = materialSlots,
            };
            var validator =
                new Dl1MeshCorpusValidator(cache);

            Dl1MeshCorpusResourceResult result =
                validator.ValidateDecodedMesh(
                    CreateResource("zero_technique_uv"),
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
                    issue.Code == "DL1CORPUS034" &&
                    issue.Severity ==
                        Dl1MeshCorpusIssueSeverity.Warning &&
                    issue.Message.Contains(
                        "non-display material",
                        StringComparison.Ordinal));
            Assert.DoesNotContain(
                result.Issues,
                static issue =>
                    issue.Code == "DL1CORPUS028");

            Dl1MeshPreviewPayload preview =
                Dl1MeshPreviewAdapter.Convert(mesh);
            Assert.Single(preview.Meshes);
            Assert.Contains(
                preview.Diagnostics,
                static diagnostic =>
                    diagnostic.Contains(
                        "custom_zero.mat",
                        StringComparison.Ordinal));
        }
        finally
        {
            RpackTestData.DeleteTemporaryDirectory(directory);
        }
    }

    [Theory]
    [InlineData("shadow_caster_2s_hl.mat")]
    [InlineData("objects/shadow_caster.mat")]
    [InlineData("shadow-caster.mat")]
    public void SimilarMaterialNamesDoNotExpandNonDisplayPolicy(
        string materialName)
    {
        string directory =
            RpackTestData.CreateTemporaryDirectory();
        try
        {
            using Rp6lChunkCache cache =
                CreateCache(directory);
            var validator =
                new Dl1MeshCorpusValidator(cache);

            Dl1MeshCorpusResourceResult result =
                validator.ValidateDecodedMesh(
                    CreateResource("near_shadow_uv"),
                    CreateUvPolicyMesh(
                        materialName,
                        shareBadVertexWithVisiblePart: false));

            Assert.False(result.Passed);
            Assert.Contains(
                result.Issues,
                static issue =>
                    issue.Code == "DL1CORPUS028" &&
                    issue.Severity ==
                        Dl1MeshCorpusIssueSeverity.Error);
            Assert.DoesNotContain(
                result.Issues,
                static issue =>
                    issue.Code == "DL1CORPUS034");
        }
        finally
        {
            RpackTestData.DeleteTemporaryDirectory(directory);
        }
    }

    [Theory]
    [InlineData("furniture_bookshelf_a.mat")]
    [InlineData("ot_glass_a.mat")]
    [InlineData("horizon_town_constructions.mat")]
    [InlineData("slums_noise_barrier_a.mat")]
    public void OrdinaryRetailMaterialNamesKeepNonFiniteUvBlocked(
        string materialName)
    {
        string directory =
            RpackTestData.CreateTemporaryDirectory();
        try
        {
            using Rp6lChunkCache cache =
                CreateCache(directory);
            var validator =
                new Dl1MeshCorpusValidator(cache);

            Dl1MeshCorpusResourceResult result =
                validator.ValidateDecodedMesh(
                    CreateResource("ordinary_uv"),
                    CreateUvPolicyMesh(
                        materialName,
                        shareBadVertexWithVisiblePart: false));

            Assert.False(result.Passed);
            Assert.Contains(
                result.Issues,
                static issue =>
                    issue.Code == "DL1CORPUS028" &&
                    issue.Severity ==
                        Dl1MeshCorpusIssueSeverity.Error);
            Assert.DoesNotContain(
                result.Issues,
                static issue =>
                    issue.Code == "DL1CORPUS034");
        }
        finally
        {
            RpackTestData.DeleteTemporaryDirectory(directory);
        }
    }

    [Fact]
    public void SharedVisibleVertexKeepsNonFiniteUvBlocked()
    {
        string directory =
            RpackTestData.CreateTemporaryDirectory();
        try
        {
            using Rp6lChunkCache cache =
                CreateCache(directory);
            var validator =
                new Dl1MeshCorpusValidator(cache);

            Dl1MeshCorpusResourceResult result =
                validator.ValidateDecodedMesh(
                    CreateResource("shared_shadow_uv"),
                    CreateUvPolicyMesh(
                        "shadow_caster.mat",
                        shareBadVertexWithVisiblePart: true));

            Assert.False(result.Passed);
            Assert.Contains(
                result.Issues,
                static issue =>
                    issue.Code == "DL1CORPUS028" &&
                    issue.Severity ==
                        Dl1MeshCorpusIssueSeverity.Error);
            Assert.DoesNotContain(
                result.Issues,
                static issue =>
                    issue.Code == "DL1CORPUS034");
        }
        finally
        {
            RpackTestData.DeleteTemporaryDirectory(directory);
        }
    }

    private static byte[] BuildSingleEntityPayload(
        CompactMeshEntityType entityType)
    {
        const int nameOffset =
            EntityTableOffset + EntityStride;
        byte[] payload = new byte[nameOffset + 7];
        BinaryPrimitives.WriteUInt64LittleEndian(
            payload.AsSpan(0x08),
            EntityTableOffset + 1);
        BinaryPrimitives.WriteUInt32LittleEndian(
            payload.AsSpan(0x64),
            1);
        BinaryPrimitives.WriteUInt32LittleEndian(
            payload.AsSpan(0x68),
            1);
        Span<byte> entity = payload.AsSpan(
            EntityTableOffset,
            EntityStride);
        WriteIdentityMatrix(entity);
        WriteIdentityMatrix(
            entity[ReferenceMatrixOffset..]);
        BinaryPrimitives.WriteUInt64LittleEndian(
            entity[0x78..],
            nameOffset + 1);
        BinaryPrimitives.WriteInt16LittleEndian(
            entity[ParentIndexOffset..],
            -1);
        entity[EntityTypeOffset] = (byte)entityType;
        "static\0"u8.CopyTo(
            payload.AsSpan(nameOffset));
        return payload;
    }

    private static void WriteIdentityMatrix(
        Span<byte> destination)
    {
        BinaryPrimitives.WriteInt32LittleEndian(
            destination,
            BitConverter.SingleToInt32Bits(1));
        BinaryPrimitives.WriteInt32LittleEndian(
            destination[20..],
            BitConverter.SingleToInt32Bits(1));
        BinaryPrimitives.WriteInt32LittleEndian(
            destination[40..],
            BitConverter.SingleToInt32Bits(1));
    }

    private static void WriteNonFinite(
        byte[] payload,
        int offset) =>
        BinaryPrimitives.WriteInt32LittleEndian(
            payload.AsSpan(offset),
            BitConverter.SingleToInt32Bits(float.NaN));

    private static Rp6lResourceDescriptor CreateResource(
        string name) =>
        new(
            0,
            name,
            Rp6lResourceTypes.Mesh,
            0,
            0,
            5,
            []);

    private static Dl1MeshData CreateUvPolicyMesh(
        string shadowMaterialName,
        bool shareBadVertexWithVisiblePart)
    {
        var entity = new CompactMeshEntity(
            0,
            "static",
            0,
            new CompactBounds(0, 0, 0, 1, 1, 1),
            -1,
            CompactMeshEntityType.Mesh,
            0,
            1,
            CompactMatrix3x4.Identity,
            CompactMatrix3x4.Identity,
            0,
            0);
        var hierarchy = new CompactMeshDocument(
            1,
            1,
            0,
            [entity],
            []);
        Dl1MeshVertex[] vertices =
            Enumerable.Range(0, 6)
                .Select(index =>
                    new Dl1MeshVertex(
                        new Vector3(
                            index % 3,
                            index / 3,
                            0),
                        Vector3.UnitZ,
                        new Vector4(1, 0, 0, 1),
                        index == 0
                            ? new Vector2(
                                float.PositiveInfinity,
                                1)
                            : Vector2.Zero,
                        Vector2.Zero,
                        Vector4.One,
                        Vector4.Zero,
                        new Dl1BoneIndex4(0, 0, 0, 0)))
                .ToArray();
        ushort[] indices =
        [
            0,
            1,
            2,
            shareBadVertexWithVisiblePart
                ? (ushort)0
                : (ushort)3,
            4,
            5,
        ];
        var surface = new Dl1MeshSurface(
            "surface",
            0,
            0,
            0,
            new Dl1VertexLayout(
                16,
                [
                    new Dl1VertexElement(
                        Dl1VertexSemantic.Position,
                        0,
                        Dl1VertexElementFormat.Float3,
                        0,
                        0),
                    new Dl1VertexElement(
                        Dl1VertexSemantic.TextureCoordinate,
                        0,
                        Dl1VertexElementFormat.Half2,
                        0,
                        12),
                ]),
            new Dl1MeshBufferSlice(3, 0, 96, 16),
            new Dl1MeshBufferSlice(4, 0, 12, 2),
            vertices.Length,
            indices.Length,
            vertices,
            indices,
            [
                new Dl1MeshSubmesh(
                    0,
                    0,
                    3,
                    1,
                    []),
                new Dl1MeshSubmesh(
                    1,
                    3,
                    3,
                    0,
                    []),
            ]);
        return new Dl1MeshData(
            "uv_policy",
            Dl1MeshContainerLayout.FiveItemSplitGpu,
            hierarchy,
            null,
            [],
            [surface],
            [
                new Dl1MaterialSlot(
                    0,
                    "visible.mat",
                    0,
                    null,
                    Dl1MaterialBindingStatus.DatabaseNameDecoded),
                new Dl1MaterialSlot(
                    1,
                    shadowMaterialName,
                    0,
                    null,
                    Dl1MaterialBindingStatus.DatabaseNameDecoded),
            ],
            [],
            [],
            []);
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
