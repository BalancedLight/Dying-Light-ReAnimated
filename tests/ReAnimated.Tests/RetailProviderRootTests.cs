using ReAnimated.Codecs.Rp6l;
using ReAnimated.DL1.Assets.Catalog;
using ReAnimated.DL1.Assets.Providers;
using System.IO.Compression;

namespace ReAnimated.Tests;

public sealed class RetailProviderRootTests
{
    [Fact]
    public async Task AdditionalRootOverridesBaseAndMalformedPackFailsLocally()
    {
        string directory = RpackTestData.CreateTemporaryDirectory();
        try
        {
            string baseData = Path.Combine(directory, "DW", "Data");
            Directory.CreateDirectory(baseData);
            File.WriteAllBytes(
                Path.Combine(directory, "DyingLightGame.exe"),
                []);
            using (ZipArchive archive = ZipFile.Open(
                       Path.Combine(directory, "DW", "Data0.pak"),
                       ZipArchiveMode.Create))
            {
            }
            string basePack = await RpackTestData.WriteArchiveAsync(
                baseData,
                "shared_mesh",
                Rp6lResourceTypes.Mesh,
                [new RpackTestItem(16, [1, 2, 3])],
                RpackTestCompression.None);

            string additionalRoot = Path.Combine(directory, "authoring-packs");
            string overridePack = await RpackTestData.WriteArchiveAsync(
                additionalRoot,
                "shared_mesh",
                Rp6lResourceTypes.Mesh,
                [new RpackTestItem(16, [4, 5, 6])],
                RpackTestCompression.None);
            string malformedPack = Path.Combine(
                additionalRoot,
                "malformed.rpack");
            await File.WriteAllTextAsync(
                malformedPack,
                "not an RP6L archive");
            string missingRoot = Path.Combine(directory, "missing-packs");
            string emptyRoot = Path.Combine(directory, "empty-packs");
            Directory.CreateDirectory(emptyRoot);

            await using Dl1RetailProviderSet providers =
                Dl1RetailProviderSet.Create(
                    directory,
                    additionalRpackRoots:
                    [
                        additionalRoot,
                        missingRoot,
                        emptyRoot,
                        basePack,
                    ]);
            RetailAssetCatalog catalog = await RetailAssetCatalog.BuildAsync(
                providers.Providers);

            RetailAssetLogicalId logicalId = RetailAssetLogicalId.Rpack(
                Rp6lResourceTypes.Mesh,
                "shared_mesh");
            RetailAssetRecord winner = Assert.IsType<RetailAssetRecord>(
                catalog.Resolve(logicalId));
            Assert.Equal(
                overridePack,
                winner.Source.ContainerPath,
                ignoreCase: true);
            Assert.Equal(2, catalog.GetCandidates(logicalId).Count);
            Assert.Contains(
                providers.Diagnostics,
                diagnostic =>
                    diagnostic.Code == "additional-rpack-root-missing" &&
                    string.Equals(
                        diagnostic.Path,
                        missingRoot,
                        StringComparison.OrdinalIgnoreCase));
            Assert.Contains(
                providers.Diagnostics,
                diagnostic =>
                    diagnostic.Code == "additional-rpack-root-empty" &&
                    string.Equals(
                        diagnostic.Path,
                        emptyRoot,
                        StringComparison.OrdinalIgnoreCase));
            Assert.Contains(
                providers.Diagnostics,
                diagnostic =>
                    diagnostic.Code == "additional-rpack-duplicate" &&
                    string.Equals(
                        diagnostic.Path,
                        basePack,
                        StringComparison.OrdinalIgnoreCase));
            RpackProviderError sourceError = Assert.Single(
                providers.RpackProvider.SourceErrors);
            Assert.Equal(
                malformedPack,
                sourceError.Path,
                ignoreCase: true);
            Assert.Null(sourceError.ResourceIndex);

            await using Stream payload = await catalog.OpenReadAsync(winner);
            using MemoryStream bytes = new();
            await payload.CopyToAsync(bytes);
            Assert.Equal(new byte[] { 4, 5, 6 }, bytes.ToArray());
        }
        finally
        {
            RpackTestData.DeleteTemporaryDirectory(directory);
        }
    }
}
