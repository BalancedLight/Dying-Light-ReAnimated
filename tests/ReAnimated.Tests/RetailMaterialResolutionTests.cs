using System.Buffers.Binary;
using ReAnimated.App.Infrastructure;
using ReAnimated.Codecs.Rp6l;
using ReAnimated.DL1.Assets.Catalog;
using ReAnimated.DL1.Assets.Discovery;
using ReAnimated.DL1.Assets.Materials;
using ReAnimated.DL1.Assets.Meshes;
using ReAnimated.DL1.Assets.Providers;
using ReAnimated.Renderer.D3D11;
using Xunit.Abstractions;

namespace ReAnimated.Tests;

public sealed class RetailMaterialResolutionTests
{
    private const string ValidatedBuildFingerprint =
        "89f98e5c77a2eb36767a614acf894a4f18f55e6efa8f912ca7ef66b45a0dfa13";

    private readonly ITestOutputHelper _output;

    public RetailMaterialResolutionTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Theory]
    [InlineData("armored_torso.mat", 0x1E8CE70CU)]
    [InlineData(@"data\materials\SHADOW_CASTER.MAT", 0x4C5847F1U)]
    [InlineData("default.mat", 0x58EA6D3CU)]
    public void RuntimeNameHashMatchesDecompiledRetailControls(
        string name,
        uint expected)
    {
        Assert.Equal(expected, Dl1ResourceNameHash.Compute(name));
    }

    [Theory]
    [InlineData(
        "brecken_tshirt_dif_wing",
        "player_11_tshirt.mat",
        Dl1MaterialTextureSemantic.BaseColor)]
    [InlineData(
        "unturnedhead_player_diff",
        "unturned_head.mat",
        Dl1MaterialTextureSemantic.BaseColor)]
    [InlineData(
        "surface_nrm_warning",
        "surface.mat",
        Dl1MaterialTextureSemantic.Normal)]
    [InlineData(
        "surface_spc_variant",
        "surface.mat",
        Dl1MaterialTextureSemantic.Specular)]
    [InlineData(
        "surface_msk_winter",
        "surface.mat",
        Dl1MaterialTextureSemantic.Mask)]
    [InlineData(
        "surface_grd_blue",
        "surface.mat",
        Dl1MaterialTextureSemantic.Gradient)]
    [InlineData(
        "surface",
        "surface.mat",
        Dl1MaterialTextureSemantic.BaseColor)]
    [InlineData(
        "white",
        "surface.mat",
        Dl1MaterialTextureSemantic.Unknown)]
    public void TextureClassifierAcceptsDelimitedRetailVariantTokens(
        string textureName,
        string materialName,
        Dl1MaterialTextureSemantic expected)
    {
        Assert.Equal(
            expected,
            Dl1MaterialTextureClassifier.Classify(
                textureName,
                materialName));
    }

    [Fact]
    public async Task SyntheticAbdmReaderDecodesBoundedTextureRows()
    {
        string directory = RpackTestData.CreateTemporaryDirectory();
        try
        {
            const string materialName = "test_surface.mat";
            uint textureHash =
                Dl1ResourceNameHash.ComputeTextureResource(
                    "test_surface_clr");
            string path = Path.Combine(directory, "synthetic.mp");
            await File.WriteAllBytesAsync(
                path,
                BuildMaterialPack(
                    materialName,
                    samplerState: 0x00850000,
                    textureHash,
                    loadFlags: 5));

            await using Dl1MaterialPackReader reader =
                await Dl1MaterialPackReader.OpenAsync(path);
            Dl1MaterialPackMaterialRecord material =
                Assert.IsType<Dl1MaterialPackMaterialRecord>(
                    await reader.ReadMaterialAsync(
                        materialName.ToUpperInvariant()));
            Dl1MaterialPackTextureRecord texture =
                Assert.Single(material.Textures);

            Assert.Equal(1, reader.MaterialCount);
            Assert.Equal(materialName, material.ResourceName);
            Assert.Equal(
                Dl1ResourceNameHash.Compute(materialName),
                material.NameHash);
            Assert.Equal((ushort)1, material.TechniqueCount);
            Assert.Equal(0x00850000U, texture.SamplerState);
            Assert.Equal(textureHash, texture.TextureNameHash);
            Assert.Equal(5U, texture.LoadFlags);
            Assert.Null(await reader.ReadMaterialAsync("missing.mat"));
        }
        finally
        {
            RpackTestData.DeleteTemporaryDirectory(directory);
        }
    }

    [Fact]
    public async Task AbdmReaderRejectsUnboundedContainerInventory()
    {
        string directory = RpackTestData.CreateTemporaryDirectory();
        try
        {
            byte[] header = new byte[16];
            BinaryPrimitives.WriteUInt32LittleEndian(
                header,
                0x4D444241);
            BinaryPrimitives.WriteUInt32LittleEndian(
                header.AsSpan(4),
                2);
            BinaryPrimitives.WriteUInt32LittleEndian(
                header.AsSpan(8),
                16);
            string path = Path.Combine(directory, "unbounded.mp");
            await File.WriteAllBytesAsync(path, header);

            InvalidDataException exception =
                await Assert.ThrowsAsync<InvalidDataException>(
                    async () =>
                    {
                        await using Dl1MaterialPackReader _ =
                            await Dl1MaterialPackReader.OpenAsync(
                                path,
                                new Dl1MaterialPackLimits
                                {
                                    MaximumContainerCount = 1,
                                });
                    });

            Assert.Contains(
                "exceeds",
                exception.Message,
                StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            RpackTestData.DeleteTemporaryDirectory(directory);
        }
    }

    [Fact]
    public async Task TextureDecoderReadsOnlyValidatedBcBaseMip()
    {
        string directory = RpackTestData.CreateTemporaryDirectory();
        try
        {
            byte[] metadata = BuildTextureMetadata(
                width: 4,
                height: 4,
                mipCount: 1,
                format: 17);
            byte[] baseMip =
                [0x00, 0xF8, 0xE0, 0x07, 0x00, 0x00, 0x00, 0x00];
            string path = await RpackTestData.WriteArchiveAsync(
                directory,
                "synthetic_texture",
                Rp6lResourceTypes.Texture,
                [
                    new RpackTestItem(42, metadata),
                    new RpackTestItem(42, baseMip),
                    new RpackTestItem(42, [0]),
                ],
                RpackTestCompression.None);
            Rp6lArchive archive = await Rp6lArchive.OpenAsync(path);
            Rp6lResourceDescriptor resource =
                Assert.Single(archive.Resources);
            RetailAssetRecord asset =
                CreateTextureAsset(archive, resource);
            await using Rp6lChunkCache cache = new(
                new Rp6lChunkCacheOptions
                {
                    CacheDirectory = Path.Combine(directory, "cache"),
                    MaximumMemoryBytes = 0,
                    MaximumMemoryEntryBytes = 0,
                    MaximumDiskBytes = 1024 * 1024,
                });

            Dl1TexturePreviewData preview =
                await Dl1TexturePreviewDecoder.DecodeBaseMipAsync(
                    asset,
                    archive,
                    resource,
                    cache);

            Assert.Equal(4, preview.Width);
            Assert.Equal(4, preview.Height);
            Assert.Equal(1, preview.MipCount);
            Assert.Equal(Dl1PreviewTextureFormat.Bc1Unorm, preview.Format);
            Assert.Equal(8, preview.RowPitch);
            Assert.Equal(baseMip, preview.BaseMipBytes.ToArray());
            Assert.Equal(asset.Id, preview.AssetId);
        }
        finally
        {
            RpackTestData.DeleteTemporaryDirectory(directory);
        }
    }

    [Fact(Timeout = 180_000)]
    public async Task InstalledArmoredResolvesRetailMaterialAndBaseColorWhenAvailable()
    {
        Dl1InstallLocation? install = SteamInstallDiscovery
            .Discover()
            .FirstOrDefault(static location => location.IsValid);
        if (install is null)
        {
            return;
        }

        string materialPack = Path.Combine(
            install.DataPath,
            "optimized_dx11.mp");
        string meshPack = Path.Combine(
            install.DataPath,
            "common_meshes_PC.rpack");
        if (!File.Exists(materialPack) || !File.Exists(meshPack))
        {
            return;
        }

        string directory = RpackTestData.CreateTemporaryDirectory();
        try
        {
            await using var cache = new Rp6lChunkCache(
                new Rp6lChunkCacheOptions
                {
                    CacheDirectory = Path.Combine(directory, "cache"),
                    MaximumMemoryBytes = 32 * 1024 * 1024,
                    MaximumMemoryEntryBytes = 16 * 1024 * 1024,
                    MaximumDiskBytes = 1024L * 1024 * 1024,
                });
            await using Dl1RetailProviderSet providers =
                Dl1RetailProviderSet.Create(
                    install.InstallPath,
                    cache);
            RetailAssetCatalog catalog =
                await RetailAssetCatalog.BuildAsync(
                    providers.Providers);
            RetailAssetRecord meshAsset =
                Assert.IsType<RetailAssetRecord>(
                    catalog.Resolve(
                        RetailAssetLogicalId.Rpack(
                            Rp6lResourceTypes.Mesh,
                            "armored")));
            Rp6lArchive archive =
                await providers.RpackProvider.GetArchiveAsync(
                    meshAsset.Source.ContainerPath);
            Rp6lResourceDescriptor resource =
                archive.Resources[
                    Assert.IsType<int>(
                        meshAsset.Source.ResourceIndex)];
            Dl1MeshData raw =
                await Dl1MeshResourceDecoder.DecodeAsync(
                    archive,
                    resource,
                    cache);
            var resolver = new Dl1MaterialTextureResolver(
                catalog,
                providers.RpackProvider,
                cache,
                materialPack);

            Dl1MeshData resolved =
                await resolver.ResolveAsync(raw);

            Dl1MaterialSlot torso = Assert.Single(
                resolved.MaterialSlots,
                static slot =>
                    slot.DatabaseName.Equals(
                        "armored_torso.mat",
                        StringComparison.OrdinalIgnoreCase));
            Dl1ResolvedMaterial material =
                Assert.IsType<Dl1ResolvedMaterial>(
                    torso.ResolvedMaterial);
            Assert.Equal(
                Dl1MaterialBindingStatus.Resolved,
                torso.BindingStatus);
            Assert.Equal("armored_torso.mat", torso.MaterialResourceName);
            Assert.Equal(0x1E8CE70CU, material.NameHash);
            Assert.Equal(6, material.TextureBindings.Count);
            Assert.Collection(
                material.TextureBindings,
                static binding =>
                    Assert.Equal("blood_a_grd", binding.ResourceName),
                static binding =>
                    Assert.Equal("viral_head_blood_a", binding.ResourceName),
                static binding =>
                    Assert.Equal("armored_torso_clr", binding.ResourceName),
                static binding =>
                    Assert.Equal("armored_torso_nrm", binding.ResourceName),
                static binding =>
                    Assert.Equal(
                        "armored_torso_skin_msk",
                        binding.ResourceName),
                static binding =>
                    Assert.Equal("armored_torso_spc", binding.ResourceName));
            Assert.All(
                material.TextureBindings,
                static binding =>
                    Assert.NotNull(binding.AssetId));
            Dl1TexturePreviewData preview =
                Assert.IsType<Dl1TexturePreviewData>(
                    material.BaseColorPreview);
            Assert.Equal("armored_torso_clr", preview.ResourceName);
            Assert.Equal(
                Rp6lResourceTypes.Texture,
                preview.AssetId.ResourceType);
            Assert.Equal(2048, preview.Width);
            Assert.Equal(2048, preview.Height);
            Assert.Equal(12, preview.MipCount);
            Assert.Equal(
                Dl1PreviewTextureFormat.Bc1Unorm,
                preview.Format);
            Assert.Equal(4096, preview.RowPitch);
            Assert.Equal(
                2 * 1024 * 1024,
                preview.BaseMipBytes.Length);

            Dl1MaterialSlot eyes = Assert.Single(
                resolved.MaterialSlots,
                static slot =>
                    slot.DatabaseName.Equals(
                        "zombie_eyes_a.mat",
                        StringComparison.OrdinalIgnoreCase));
            Assert.Equal("EYES_DEF.MAT", eyes.DeclaredDatabaseName);
            Assert.Equal(9, eyes.SkinReplacementDatabaseEntryIndex);
            Assert.Equal("Default", eyes.AppliedSkinName);
            Assert.Equal("zombie_eyes_a.mat", eyes.MaterialResourceName);
            Assert.NotNull(eyes.ResolvedMaterial);
            Assert.Equal(
                Dl1MaterialBindingStatus.Resolved,
                eyes.BindingStatus);
            Assert.DoesNotContain(
                resolved.Diagnostics,
                static diagnostic =>
                    diagnostic.Code == "DL1MAT002"
                    && diagnostic.EntityIndex == 3);

            Dl1MeshPreviewPayload renderPayload =
                Dl1MeshPreviewAdapter.Convert(resolved);
            TextureRenderData rendererTexture =
                Assert.IsType<TextureRenderData>(
                    renderPayload.Meshes
                        .First(static mesh =>
                            mesh.BaseColorTexture is not null)
                        .BaseColorTexture);
            Assert.Equal(
                TextureRenderFormat.Bc1Unorm,
                rendererTexture.Format);
            Assert.Equal(
                preview.BaseMipBytes.Length,
                rendererTexture.BaseMipBytes.Length);
            _output.WriteLine(
                $"Resolved {resolved.MaterialSlots.Count(static slot => slot.ResolvedMaterial is not null)} material slots; renderer received {rendererTexture.Width}x{rendererTexture.Height} {rendererTexture.Format}.");
        }
        finally
        {
            RpackTestData.DeleteTemporaryDirectory(directory);
        }
    }

    [Fact(Timeout = 180_000)]
    public async Task InstalledPlayerBeardTextureRemainsUnboundAndUnsupported()
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
                $"Installed beard-material control skipped for build {build.BuildFingerprint}.");
            return;
        }

        string materialPack = Path.Combine(
            install.DataPath,
            "optimized_dx11.mp");
        Assert.True(File.Exists(materialPack), materialPack);
        await using (Dl1MaterialPackReader reader =
                     await Dl1MaterialPackReader.OpenAsync(
                         materialPack))
        {
            Assert.Null(
                await reader.ReadMaterialAsync("beard.mat"));
        }

        Assert.Equal(
            Dl1MaterialTextureSemantic.Unknown,
            Dl1MaterialTextureClassifier.Classify(
                "player_beard",
                "beard.mat"));

        string directory = RpackTestData.CreateTemporaryDirectory();
        try
        {
            await using var cache = new Rp6lChunkCache(
                new Rp6lChunkCacheOptions
                {
                    CacheDirectory = Path.Combine(directory, "cache"),
                    MaximumMemoryBytes = 32 * 1024 * 1024,
                    MaximumMemoryEntryBytes = 16 * 1024 * 1024,
                    MaximumDiskBytes = 1024L * 1024 * 1024,
                });
            await using Dl1RetailProviderSet providers =
                Dl1RetailProviderSet.Create(
                    install.InstallPath,
                    cache);
            RetailAssetCatalog catalog =
                await RetailAssetCatalog.BuildAsync(
                    providers.Providers);
            RetailAssetRecord textureAsset =
                Assert.IsType<RetailAssetRecord>(
                    catalog.Resolve(
                        RetailAssetLogicalId.Rpack(
                            Rp6lResourceTypes.Texture,
                            "player_beard")));
            Assert.Equal(
                "common_cod_1_PC.rpack",
                Path.GetFileName(
                    textureAsset.Source.ContainerPath));
            Rp6lArchive archive =
                await providers.RpackProvider.GetArchiveAsync(
                    textureAsset.Source.ContainerPath);
            Rp6lResourceDescriptor resource =
                archive.Resources[
                    Assert.IsType<int>(
                        textureAsset.Source.ResourceIndex)];
            Assert.Equal(2, resource.Items.Count);
            InvalidDataException exception =
                await Assert.ThrowsAsync<InvalidDataException>(
                    () =>
                        Dl1TexturePreviewDecoder.DecodeBaseMipAsync(
                            textureAsset,
                            archive,
                            resource,
                            cache));
            Assert.Contains(
                "has 2 items",
                exception.Message,
                StringComparison.Ordinal);
            _output.WriteLine(
                $"Installed player_beard candidate: index={textureAsset.Source.ResourceIndex}, " +
                $"items={resource.Items.Count}. No beard.mat ABDM record binds " +
                "this texture to the mesh, and its two-item layout is outside " +
                "the validated preview decoder.");
        }
        finally
        {
            RpackTestData.DeleteTemporaryDirectory(directory);
        }
    }

    private static byte[] BuildMaterialPack(
        string materialName,
        uint samplerState,
        uint textureHash,
        uint loadFlags)
    {
        const int headerSize = 16;
        const int containerSize = 48;
        const int entrySize = 16;
        const int materialSize = 36;
        int entryOffset = headerSize + containerSize;
        int materialOffset = entryOffset + entrySize;
        byte[] bytes = new byte[materialOffset + materialSize];
        BinaryPrimitives.WriteUInt32LittleEndian(
            bytes,
            0x4D444241);
        BinaryPrimitives.WriteUInt32LittleEndian(
            bytes.AsSpan(4),
            1);
        BinaryPrimitives.WriteUInt32LittleEndian(
            bytes.AsSpan(8),
            headerSize);
        "materials"u8.CopyTo(bytes.AsSpan(headerSize, 32));
        BinaryPrimitives.WriteUInt32LittleEndian(
            bytes.AsSpan(headerSize + 32),
            1);
        BinaryPrimitives.WriteUInt32LittleEndian(
            bytes.AsSpan(headerSize + 36),
            1);
        BinaryPrimitives.WriteUInt32LittleEndian(
            bytes.AsSpan(headerSize + 40),
            checked((uint)entryOffset));

        uint materialHash =
            Dl1ResourceNameHash.Compute(materialName);
        BinaryPrimitives.WriteUInt32LittleEndian(
            bytes.AsSpan(entryOffset),
            materialHash);
        BinaryPrimitives.WriteUInt32LittleEndian(
            bytes.AsSpan(entryOffset + 4),
            checked((uint)materialOffset));
        BinaryPrimitives.WriteUInt32LittleEndian(
            bytes.AsSpan(entryOffset + 8),
            materialSize);
        BinaryPrimitives.WriteUInt32LittleEndian(
            bytes.AsSpan(entryOffset + 12),
            materialSize);

        Span<byte> material =
            bytes.AsSpan(materialOffset, materialSize);
        BinaryPrimitives.WriteUInt32LittleEndian(
            material,
            materialHash);
        BinaryPrimitives.WriteUInt16LittleEndian(
            material[16..],
            1);
        BinaryPrimitives.WriteUInt16LittleEndian(
            material[18..],
            1);
        BinaryPrimitives.WriteUInt16LittleEndian(
            material[22..],
            2);
        BinaryPrimitives.WriteUInt32LittleEndian(
            material[24..],
            samplerState);
        BinaryPrimitives.WriteUInt32LittleEndian(
            material[28..],
            textureHash);
        BinaryPrimitives.WriteUInt32LittleEndian(
            material[32..],
            loadFlags);
        return bytes;
    }

    private static byte[] BuildTextureMetadata(
        ushort width,
        ushort height,
        ushort mipCount,
        uint format)
    {
        byte[] metadata = new byte[16];
        BinaryPrimitives.WriteUInt16LittleEndian(
            metadata,
            width);
        BinaryPrimitives.WriteUInt16LittleEndian(
            metadata.AsSpan(2),
            height);
        BinaryPrimitives.WriteUInt16LittleEndian(
            metadata.AsSpan(4),
            1);
        BinaryPrimitives.WriteUInt16LittleEndian(
            metadata.AsSpan(6),
            1);
        BinaryPrimitives.WriteUInt16LittleEndian(
            metadata.AsSpan(8),
            mipCount);
        BinaryPrimitives.WriteUInt32LittleEndian(
            metadata.AsSpan(12),
            format);
        return metadata;
    }

    private static RetailAssetRecord CreateTextureAsset(
        Rp6lArchive archive,
        Rp6lResourceDescriptor resource)
    {
        const string providerId = "synthetic-rpack";
        const int priority = 10;
        RetailAssetLogicalId logicalId =
            RetailAssetLogicalId.Rpack(
                Rp6lResourceTypes.Texture,
                resource.Name);
        return new RetailAssetRecord(
            RetailAssetId.Create(
                logicalId,
                "synthetic-install",
                providerId,
                resource.Index,
                priority,
                archive.CacheIdentity),
            resource.Name,
            new RetailAssetSource(
                providerId,
                RetailAssetSourceKind.Rpack,
                priority,
                archive.Path,
                $"{resource.Name}#{resource.Index}",
                resource.Index,
                resource.Items.Sum(static item =>
                    (long)item.SizeOrHash),
                archive.File.Length,
                archive.File.LastWriteTimeUtc));
    }
}
