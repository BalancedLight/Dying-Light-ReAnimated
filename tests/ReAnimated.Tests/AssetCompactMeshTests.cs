using System.Buffers.Binary;
using ReAnimated.App.Infrastructure;
using ReAnimated.Codecs.CompactMesh;
using ReAnimated.Codecs.Rp6l;
using ReAnimated.DL1.Assets.Meshes;

namespace ReAnimated.Tests;

public sealed class AssetCompactMeshTests
{
    [Fact]
    public void DecodesHierarchyMatricesAndCapabilities()
    {
        CompactMeshDocument document = CompactMeshDecoder.Decode(
            RpackTestData.BuildCompactMeshPayload());
        Assert.True(document.IsStructurallyValid);
        Assert.Equal(4, document.Entities.Count);
        Assert.Equal(2, document.Bones.Count);
        Assert.Single(document.Helpers);
        Assert.Single(document.SkinnedMeshes);
        Assert.Equal(3, document.AnimationEntityCountCandidate);
        IReadOnlyList<CompactMatrix3x4> globals =
            document.ReconstructGlobalMatrices();
        Assert.Equal(0, globals[0].M14);
        Assert.Equal(1, globals[1].M14);
        Assert.Equal(3, globals[2].M14);
    }

    [Fact]
    public void ReportsHierarchyCycleWithoutRecursingForever()
    {
        byte[] payload = RpackTestData.BuildCompactMeshPayload();
        const int firstRow = 0xB0;
        BinaryPrimitives.WriteInt16LittleEndian(
            payload.AsSpan(firstRow + 0xC6),
            0);
        CompactMeshDocument document = CompactMeshDecoder.Decode(payload);
        Assert.False(document.IsStructurallyValid);
        Assert.Contains(
            document.Diagnostics,
            static diagnostic => diagnostic.Code == "CMESH005");
    }

    [Fact]
    public void PreservesSerializedRuntimeMatrixRows()
    {
        byte[] payload = RpackTestData.BuildCompactMeshPayload();
        const int firstRow = 0xB0;
        BinaryPrimitives.WriteInt32LittleEndian(
            payload.AsSpan(firstRow + 4),
            BitConverter.SingleToInt32Bits(2));
        BinaryPrimitives.WriteInt32LittleEndian(
            payload.AsSpan(firstRow + 16),
            BitConverter.SingleToInt32Bits(3));

        CompactMeshEntity root = CompactMeshDecoder
            .Decode(payload)
            .Entities[0];
        Assert.Equal(2, root.LocalMatrix.M12);
        Assert.Equal(3, root.LocalMatrix.M21);
    }

    [Fact]
    public void PreservesOpaqueEntityBoneIndexPointers()
    {
        byte[] payload =
            RpackTestData.BuildCompactMeshPayload();
        const int firstRow = 0xB0;
        const ulong pointer0 = 0x1122334455667788;
        const ulong pointer1 = 0x8877665544332211;
        BinaryPrimitives.WriteUInt64LittleEndian(
            payload.AsSpan(firstRow + 0x90),
            pointer0);
        BinaryPrimitives.WriteUInt64LittleEndian(
            payload.AsSpan(firstRow + 0x98),
            pointer1);

        CompactMeshEntity root = CompactMeshDecoder
            .Decode(payload)
            .Entities[0];

        Assert.Equal(pointer0, root.RawBoneIndexPointer0);
        Assert.Equal(pointer1, root.RawBoneIndexPointer1);
    }

    [Fact]
    public void DecodesRetailVertexSkinMaterialAndMorphTables()
    {
        CompiledMeshTestFixture fixture =
            RpackTestData.BuildCompiledMeshFixture();
        CompiledMeshGeometryDocument geometry =
            CompiledMeshGeometryDecoder.Decode(
                fixture.Metadata,
                fixture.Variants,
                fixture.Vertices,
                fixture.Indices);

        CompiledMeshSurface surface = Assert.Single(geometry.Surfaces);
        Assert.Equal(28, surface.VertexLayout.Stride);
        Assert.Equal(3, geometry.VertexCount);
        Assert.Equal(3, geometry.IndexCount);
        Assert.Equal(
            new System.Numerics.Vector3(1, 0, 0),
            surface.Vertices[1].Position);
        Assert.Equal(
            new CompiledBoneIndex4(0, 0, 0, 0),
            surface.Vertices[0].LocalBlendIndices);
        CompiledMeshSubmesh submesh = Assert.Single(surface.Submeshes);
        Assert.Equal((ushort)2, submesh.DeclaredMaterialSlotIndex);
        Assert.Equal((short)0, Assert.Single(
            submesh.BonePaletteEntityIndexes));
        Assert.Contains("Default", geometry.VariantNames);
        Assert.Equal(3, geometry.DeclaredMaterialSlotCount);
        Assert.Equal(4, geometry.MaterialDatabase.DeclaredEntryCount);
        Assert.True(geometry.MaterialDatabase.HasCompleteSlotNames);
        Assert.Collection(
            geometry.MaterialDatabase.Entries,
            entry =>
            {
                Assert.Equal(0, entry.Index);
                Assert.Equal("characters_body", entry.DatabaseName);
                Assert.Equal(0x00000011u, entry.RawLoadValue);
            },
            entry =>
            {
                Assert.Equal(1, entry.Index);
                Assert.Equal(string.Empty, entry.DatabaseName);
                Assert.Equal(0u, entry.RawLoadValue);
            },
            entry =>
            {
                Assert.Equal(2, entry.Index);
                Assert.Equal("body_cloth", entry.DatabaseName);
                Assert.Equal(0xAABBCCDDu, entry.RawLoadValue);
            },
            entry =>
            {
                Assert.Equal(3, entry.Index);
                Assert.Equal("body_cloth_wet", entry.DatabaseName);
                Assert.Equal(0x01020304u, entry.RawLoadValue);
            });
        CompiledMorphChannel morph = Assert.Single(
            geometry.MorphChannels);
        Assert.Equal("smile", morph.Name);
        CompiledNodeMorphBinding binding = Assert.Single(
            geometry.MorphBindings);
        Assert.Equal(3, binding.VertexCount);
        Assert.Equal(8, binding.DeltaByteStride);
        Assert.Equal(
            CompiledMorphDeltaFormat.SignedShort4Scale16384,
            binding.DeltaFormat);
        Assert.Equal((ushort)0, Assert.Single(
            binding.MorphChannelIndexes));
        CompiledMorphTargetDeltas deltas = Assert.Single(
            binding.TargetDeltas);
        Assert.Equal(new System.Numerics.Vector3(1, 0, 0),
            deltas.PositionDeltas[0]);
        Assert.Equal(new System.Numerics.Vector3(0, -0.5f, 0.25f),
            deltas.PositionDeltas[1]);
        Assert.Equal(System.Numerics.Vector3.Zero,
            deltas.PositionDeltas[2]);
    }

    [Fact]
    public async Task MapsDecodedRetailGeometryIntoAssetContracts()
    {
        string directory = RpackTestData.CreateTemporaryDirectory();
        try
        {
            CompiledMeshTestFixture fixture =
                RpackTestData.BuildCompiledMeshFixture();
            string path = await RpackTestData.WriteArchiveAsync(
                directory,
                "fixture_mesh",
                Rp6lResourceTypes.Mesh,
                [
                    new RpackTestItem(42, fixture.Metadata),
                    new RpackTestItem(42, fixture.Variants),
                    new RpackTestItem(42, [1, 2, 3]),
                    new RpackTestItem(42, fixture.Vertices),
                    new RpackTestItem(42, fixture.Indices),
                ],
                RpackTestCompression.None);
            Rp6lArchive archive = await Rp6lArchive.OpenAsync(path);
            await using Rp6lChunkCache cache = new(
                new Rp6lChunkCacheOptions
                {
                    CacheDirectory = Path.Combine(directory, "cache"),
                    MaximumMemoryBytes = 8 * 1024 * 1024,
                    MaximumMemoryEntryBytes = 8 * 1024 * 1024,
                    MaximumDiskBytes = 32 * 1024 * 1024,
                });
            Dl1MeshData mesh = await Dl1MeshResourceDecoder.DecodeAsync(
                archive,
                Assert.Single(archive.Resources),
                cache);

            Assert.True(mesh.HasDecodedGeometry);
            Assert.Equal(
                Dl1MeshContainerLayout.FiveItemSplitGpu,
                mesh.ContainerLayout);
            Dl1MeshSurface surface = Assert.Single(mesh.Surfaces);
            Dl1MeshSubmesh submesh = Assert.Single(surface.Submeshes);
            Assert.Equal(2, submesh.MaterialSlotIndex);
            Assert.Equal(
                Dl1MaterialBindingStatus.DatabaseNameDecoded,
                mesh.MaterialSlots[2].BindingStatus);
            Assert.Equal(
                "body_cloth",
                mesh.MaterialSlots[2].DatabaseName);
            Assert.Equal(
                0xAABBCCDDu,
                mesh.MaterialSlots[2].RawDatabaseLoadValue);
            Assert.True(mesh.HasDecodedMaterialSlotNames);
            Assert.True(mesh.HasDecodedMaterials);
            Assert.False(mesh.HasResolvedMaterialResources);
            Dl1MorphTarget morph = Assert.Single(mesh.MorphTargets);
            Assert.Equal("smile", morph.Name);
            Assert.Equal(
                Dl1MorphPayloadStatus.VertexDeltasDecoded,
                morph.PayloadStatus);
            Dl1MorphPositionDeltaSet deltaSet = Assert.Single(
                Assert.Single(morph.Bindings).PositionDeltaSets);
            Assert.Equal(
                new System.Numerics.Vector3(1, 0, 0),
                deltaSet.PositionDeltas[0]);
            Assert.Contains(
                mesh.Diagnostics,
                static diagnostic => diagnostic.Code == "DL1MESH010");
            Assert.Contains(
                mesh.Diagnostics,
                static diagnostic =>
                    diagnostic.Code == "DL1MESH011" &&
                    diagnostic.Message.Contains(
                        "3 of 3",
                        StringComparison.Ordinal));
        }
        finally
        {
            RpackTestData.DeleteTemporaryDirectory(directory);
        }
    }

    [Fact]
    public void DecodesExactBoundedSkinDefinitions()
    {
        CompiledMeshTestFixture fixture =
            RpackTestData.BuildCompiledMeshFixture();
        byte[] skinPayload =
            RpackTestData.BuildCompiledMeshSkinPayload(
                "Default",
                [(2, 3)],
                [0xC001],
                rawFeatures: 0x0481);

        CompiledMeshGeometryDocument geometry =
            CompiledMeshGeometryDecoder.Decode(
                fixture.Metadata,
                skinPayload,
                fixture.Vertices,
                fixture.Indices);

        Assert.Equal(["Default"], geometry.VariantNames);
        CompiledMeshSkinDefinition skin = Assert.Single(
            geometry.SkinDefinitions);
        Assert.Equal(0, skin.Index);
        Assert.Equal("Default", skin.Name);
        Assert.Equal((ushort)0x0481, skin.RawFeatures);
        CompiledMeshSkinMaterialOverride material = Assert.Single(
            skin.MaterialOverrides);
        Assert.Equal(2, material.TargetMaterialSlotIndex);
        Assert.Equal(
            3,
            material.ReplacementMaterialDatabaseEntryIndex);
        CompiledMeshSkinEntityOverride entity = Assert.Single(
            skin.EntityOverrides);
        Assert.Equal(1, entity.EntityIndex);
        Assert.Equal((ushort)0xC001, entity.RawValue);
        Assert.True(entity.IsHidden);
        Assert.True(entity.HasRuntimeFlag4000);
    }

    [Fact]
    public void MalformedExactSkinOverrideFailsLocally()
    {
        CompiledMeshTestFixture fixture =
            RpackTestData.BuildCompiledMeshFixture();
        byte[] skinPayload =
            RpackTestData.BuildCompiledMeshSkinPayload(
                "Default",
                [(99, 3)],
                []);

        CompiledMeshGeometryDocument geometry =
            CompiledMeshGeometryDecoder.Decode(
                fixture.Metadata,
                skinPayload,
                fixture.Vertices,
                fixture.Indices);

        Assert.Empty(geometry.SkinDefinitions);
        Assert.Empty(geometry.VariantNames);
        CompactMeshDiagnostic diagnostic = Assert.Single(
            geometry.Diagnostics,
            static diagnostic =>
                diagnostic.Code == "CMESHG015");
        Assert.Equal(
            CompactMeshDiagnosticSeverity.Error,
            diagnostic.Severity);
        Assert.Contains(
            "targets slot 99",
            diagnostic.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task DefaultSkinAppliesMaterialInventoryAndVisibility()
    {
        string directory = RpackTestData.CreateTemporaryDirectory();
        try
        {
            CompiledMeshTestFixture fixture =
                RpackTestData.BuildCompiledMeshFixture();
            byte[] skinPayload =
                RpackTestData.BuildCompiledMeshSkinPayload(
                    "Default",
                    [(2, 3)],
                    [0xC001]);
            string path = await RpackTestData.WriteArchiveAsync(
                directory,
                "skin_fixture",
                Rp6lResourceTypes.Mesh,
                [
                    new RpackTestItem(42, fixture.Metadata),
                    new RpackTestItem(42, skinPayload),
                    new RpackTestItem(42, [1, 2, 3]),
                    new RpackTestItem(42, fixture.Vertices),
                    new RpackTestItem(42, fixture.Indices),
                ],
                RpackTestCompression.None);
            Rp6lArchive archive = await Rp6lArchive.OpenAsync(path);
            await using Rp6lChunkCache cache = new(
                new Rp6lChunkCacheOptions
                {
                    CacheDirectory = Path.Combine(directory, "cache"),
                    MaximumMemoryBytes = 8 * 1024 * 1024,
                    MaximumMemoryEntryBytes = 8 * 1024 * 1024,
                    MaximumDiskBytes = 32 * 1024 * 1024,
                });

            Dl1MeshData mesh =
                await Dl1MeshResourceDecoder.DecodeAsync(
                    archive,
                    Assert.Single(archive.Resources),
                    cache);

            Assert.Equal("Default", mesh.AppliedSkinName);
            Assert.Equal([1], mesh.SkinHiddenEntityIndexes);
            Dl1MaterialSlot slot = mesh.MaterialSlots[2];
            Assert.Equal("body_cloth_wet", slot.DatabaseName);
            Assert.Equal(
                "body_cloth",
                slot.DeclaredDatabaseName);
            Assert.Equal(0x01020304u, slot.RawDatabaseLoadValue);
            Assert.Equal(
                3,
                slot.SkinReplacementDatabaseEntryIndex);
            Assert.Equal("Default", slot.AppliedSkinName);
            Dl1MeshPreviewPayload preview =
                Dl1MeshPreviewAdapter.Convert(mesh);
            Assert.Empty(preview.Meshes);
            Assert.Contains(
                preview.Diagnostics,
                static diagnostic =>
                    diagnostic.Contains(
                        "exact retail skin 'Default' hides hierarchy entity 1",
                        StringComparison.Ordinal));
        }
        finally
        {
            RpackTestData.DeleteTemporaryDirectory(directory);
        }
    }

    [Fact]
    public void MaterialDatabaseNameFailureIsLocalAndDoesNotFabricateAName()
    {
        CompiledMeshTestFixture fixture =
            RpackTestData.BuildCompiledMeshFixture();
        byte[] metadata = fixture.Metadata.ToArray();
        const int corruptedEntryIndex = 2;
        BinaryPrimitives.WriteUInt64LittleEndian(
            metadata.AsSpan(
                RpackTestData.CompiledMeshMaterialDatabaseEntriesOffset +
                corruptedEntryIndex * 24),
            ulong.MaxValue);

        CompiledMeshGeometryDocument geometry =
            CompiledMeshGeometryDecoder.Decode(
                metadata,
                fixture.Variants,
                fixture.Vertices,
                fixture.Indices);

        Assert.Equal(3, geometry.MaterialDatabase.DeclaredSlotCount);
        Assert.Equal(4, geometry.MaterialDatabase.DeclaredEntryCount);
        Assert.False(geometry.MaterialDatabase.HasCompleteSlotNames);
        Assert.DoesNotContain(
            geometry.MaterialDatabase.Entries,
            static entry => entry.Index == corruptedEntryIndex);
        Assert.Contains(
            geometry.MaterialDatabase.Entries,
            static entry =>
                entry.Index == 3 &&
                entry.DatabaseName == "body_cloth_wet");
        CompactMeshDiagnostic diagnostic = Assert.Single(
            geometry.Diagnostics,
            static diagnostic => diagnostic.Code == "CMESHG012");
        Assert.Equal(
            CompactMeshDiagnosticSeverity.Error,
            diagnostic.Severity);
        Assert.Contains(
            "material database entry 2 name pointer",
            diagnostic.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void MorphDeltaDecodeHonorsItsBoundedAllocationLimit()
    {
        CompiledMeshTestFixture fixture =
            RpackTestData.BuildCompiledMeshFixture();

        CompiledMeshGeometryDocument geometry =
            CompiledMeshGeometryDecoder.Decode(
                fixture.Metadata,
                fixture.Variants,
                fixture.Vertices,
                fixture.Indices,
                new CompiledMeshDecodeLimits
                {
                    MaximumDecodedMorphDeltaBytes = 35,
                });

        Assert.Empty(geometry.MorphBindings);
        CompactMeshDiagnostic diagnostic = Assert.Single(
            geometry.Diagnostics,
            static diagnostic => diagnostic.Code == "CMESHG008");
        Assert.Equal(
            CompactMeshDiagnosticSeverity.Error,
            diagnostic.Severity);
        Assert.Contains(
            "35-byte limit",
            diagnostic.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void MorphDeltaRejectsUnexplainedNonzeroShort4W()
    {
        CompiledMeshTestFixture fixture =
            RpackTestData.BuildCompiledMeshFixture();
        byte[] metadata = fixture.Metadata.ToArray();
        BinaryPrimitives.WriteInt16LittleEndian(
            metadata.AsSpan(
                RpackTestData.CompiledMeshMorphPayloadOffset + 6),
            1);

        CompiledMeshGeometryDocument geometry =
            CompiledMeshGeometryDecoder.Decode(
                metadata,
                fixture.Variants,
                fixture.Vertices,
                fixture.Indices);

        Assert.Empty(geometry.MorphBindings);
        CompactMeshDiagnostic diagnostic = Assert.Single(
            geometry.Diagnostics,
            static diagnostic => diagnostic.Code == "CMESHG008");
        Assert.Equal(
            CompactMeshDiagnosticSeverity.Error,
            diagnostic.Severity);
        Assert.Contains(
            "unsupported nonzero SHORT4 W value 1",
            diagnostic.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void MorphDeltaPayloadMustRemainInsideMetadata()
    {
        CompiledMeshTestFixture fixture =
            RpackTestData.BuildCompiledMeshFixture();
        byte[] metadata = fixture.Metadata.ToArray();
        BinaryPrimitives.WriteUInt64LittleEndian(
            metadata.AsSpan(
                RpackTestData.CompiledMeshMorphLodTableOffset),
            checked((ulong)(metadata.Length - 8) + 1));

        CompiledMeshGeometryDocument geometry =
            CompiledMeshGeometryDecoder.Decode(
                metadata,
                fixture.Variants,
                fixture.Vertices,
                fixture.Indices);

        Assert.Empty(geometry.MorphBindings);
        CompactMeshDiagnostic diagnostic = Assert.Single(
            geometry.Diagnostics,
            static diagnostic => diagnostic.Code == "CMESHG008");
        Assert.Equal(
            CompactMeshDiagnosticSeverity.Error,
            diagnostic.Severity);
        Assert.Contains(
            "morph payload range",
            diagnostic.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void MaterialDatabaseRejectsCountsThatCannotCoverDeclaredSlots()
    {
        CompiledMeshTestFixture fixture =
            RpackTestData.BuildCompiledMeshFixture();
        byte[] metadata = fixture.Metadata.ToArray();
        BinaryPrimitives.WriteUInt16LittleEndian(
            metadata.AsSpan(
                RpackTestData.CompiledMeshMaterialDatabaseHolderOffset + 10),
            2);

        CompiledMeshGeometryDocument geometry =
            CompiledMeshGeometryDecoder.Decode(
                metadata,
                fixture.Variants,
                fixture.Vertices,
                fixture.Indices);

        Assert.Equal(3, geometry.MaterialDatabase.DeclaredSlotCount);
        Assert.Equal(2, geometry.MaterialDatabase.DeclaredEntryCount);
        Assert.False(geometry.MaterialDatabase.HasCompleteSlotNames);
        CompactMeshDiagnostic diagnostic = Assert.Single(
            geometry.Diagnostics,
            static diagnostic => diagnostic.Code == "CMESHG010");
        Assert.Equal(
            CompactMeshDiagnosticSeverity.Error,
            diagnostic.Severity);
        Assert.Contains(
            "3 slots but only 2 entries",
            diagnostic.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void MaterialDatabaseHolderFailureIsLocal()
    {
        CompiledMeshTestFixture fixture =
            RpackTestData.BuildCompiledMeshFixture();
        byte[] metadata = fixture.Metadata.ToArray();
        BinaryPrimitives.WriteUInt64LittleEndian(
            metadata.AsSpan(0x18),
            ulong.MaxValue);

        CompiledMeshGeometryDocument geometry =
            CompiledMeshGeometryDecoder.Decode(
                metadata,
                fixture.Variants,
                fixture.Vertices,
                fixture.Indices);

        Assert.Equal(0, geometry.MaterialDatabase.DeclaredSlotCount);
        Assert.Equal(0, geometry.MaterialDatabase.DeclaredEntryCount);
        Assert.Empty(geometry.MaterialDatabase.Entries);
        CompactMeshDiagnostic diagnostic = Assert.Single(
            geometry.Diagnostics,
            static diagnostic => diagnostic.Code == "CMESHG009");
        Assert.Equal(
            CompactMeshDiagnosticSeverity.Error,
            diagnostic.Severity);
        Assert.Contains(
            "material database holder pointer",
            diagnostic.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void MaterialDatabaseEntryTableFailureRetainsDeclaredCounts()
    {
        CompiledMeshTestFixture fixture =
            RpackTestData.BuildCompiledMeshFixture();
        byte[] metadata = fixture.Metadata.ToArray();
        BinaryPrimitives.WriteUInt64LittleEndian(
            metadata.AsSpan(
                RpackTestData.CompiledMeshMaterialDatabaseHolderOffset),
            ulong.MaxValue);

        CompiledMeshGeometryDocument geometry =
            CompiledMeshGeometryDecoder.Decode(
                metadata,
                fixture.Variants,
                fixture.Vertices,
                fixture.Indices);

        Assert.Equal(3, geometry.MaterialDatabase.DeclaredSlotCount);
        Assert.Equal(4, geometry.MaterialDatabase.DeclaredEntryCount);
        Assert.Empty(geometry.MaterialDatabase.Entries);
        CompactMeshDiagnostic diagnostic = Assert.Single(
            geometry.Diagnostics,
            static diagnostic => diagnostic.Code == "CMESHG011");
        Assert.Equal(
            CompactMeshDiagnosticSeverity.Error,
            diagnostic.Severity);
        Assert.Contains(
            "material database entries pointer",
            diagnostic.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task ClassifiesThreeItemMetadataOnlyRetailLayout()
    {
        string directory = RpackTestData.CreateTemporaryDirectory();
        try
        {
            CompiledMeshTestFixture fixture =
                RpackTestData.BuildCompiledMeshFixture();
            string path = await RpackTestData.WriteArchiveAsync(
                directory,
                "metadata_only",
                Rp6lResourceTypes.Mesh,
                [
                    new RpackTestItem(7, fixture.Metadata),
                    new RpackTestItem(7, fixture.Variants),
                    new RpackTestItem(7, [1]),
                ],
                RpackTestCompression.None);
            Rp6lArchive archive = await Rp6lArchive.OpenAsync(path);
            await using Rp6lChunkCache cache = new(
                new Rp6lChunkCacheOptions
                {
                    CacheDirectory = Path.Combine(directory, "cache"),
                    MaximumMemoryBytes = 8 * 1024 * 1024,
                    MaximumMemoryEntryBytes = 8 * 1024 * 1024,
                    MaximumDiskBytes = 32 * 1024 * 1024,
                });
            Dl1MeshData mesh = await Dl1MeshResourceDecoder.DecodeAsync(
                archive,
                Assert.Single(archive.Resources),
                cache);

            Assert.Equal(
                Dl1MeshContainerLayout.ThreeItemMetadataOnly,
                mesh.ContainerLayout);
            Assert.False(mesh.HasDecodedGeometry);
            Assert.Contains(
                mesh.Buffers,
                static buffer =>
                    buffer.ResourceItemSlot == 2 &&
                    buffer.StorageGroupId == 7 &&
                    buffer.Role == Dl1MeshBufferRole.ResolverData);
        }
        finally
        {
            RpackTestData.DeleteTemporaryDirectory(directory);
        }
    }
}
