using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ReAnimated.Codecs.Fbx;
using ReAnimated.Codecs.Models;
using ReAnimated.Core.ModelAuthoring;
using ReAnimated.DL1.Assets.Discovery;

namespace ReAnimated.Tests;

public sealed class NativeCompanionCompilerReceiptTests
{
    private const string FirstName = "character-resources/data/characters/body.bel";
    private const string SecondName = "character-resources/data/other/body.bel";

    [Theory]
    [InlineData("character-resources/sequence.fx","data/characters/_retained_fx_sources/sequence.fx")]
    [InlineData("character-resources/effects/sequence.fx","data/characters/_retained_fx_sources/effects/sequence.fx")]
    [InlineData("character-resources/data/parts/part.phx","data/parts/part.phx")]
    public void RetainedSourcePathPreservesGlobalEffectNamesInsidePortableStaging(string name,string expected)
    {Assert.Equal(expected,Dl1NativeCompanionWriter.PreservedVirtualPath(name));}

    [Fact]
    [Trait("ValidationTier", "Hermetic")]
    [Trait("Gate", "CustomModelDeployment")]
    public async Task SourceBuildKeepsSameBasenameCompanionsAtDistinctRelativePathsAndHashes()
    {
        string directory = RpackTestData.CreateTemporaryDirectory();
        try
        {
            FbxModelAuthoringImportResult model = CreateModelWithSameBasenameCompanions();
            Dl1SourceModelBuildResult source = await Dl1SourceModelWriter.WriteAsync(new()
            {
                Model = model,
                OutputDirectory = directory,
                ResourceName = "generic_companion_receipt",
                AllowIncompleteCharacterDiagnostics = true,
            });

            Assert.Contains(FirstName, source.NativeCompanionFiles);
            Assert.Contains(SecondName, source.NativeCompanionFiles);
            Assert.NotEqual(source.OutputSha256[FirstName], source.OutputSha256[SecondName]);
            foreach (string name in new[] { FirstName, SecondName })
            {
                string path = Path.Combine(directory, name.Replace('/', Path.DirectorySeparatorChar));
                Assert.True(File.Exists(path));
                Assert.Equal(Sha(await File.ReadAllBytesAsync(path)), source.OutputSha256[name]);
            }
        }
        finally { RpackTestData.DeleteTemporaryDirectory(directory); }
    }

    [InstalledDl1ModelCompilerFact]
    [Trait("ValidationTier", "Release")]
    [Trait("Gate", "InstalledDl1ModelCompiler")]
    public async Task InstalledCompilerReceiptUsesFullRelativePathForSameBasenameCompanions()
    {
        string? compiler = Dl1OfficialModelCompiler.FindDefaultCompilerExecutable();
        Assert.True(File.Exists(compiler), "The Dying Light Developer Tools compiler was not discovered.");
        Dl1InstallLocation? install = SteamInstallDiscovery.Discover()
            .FirstOrDefault(static candidate => candidate.IsValid);
        Assert.NotNull(install);
        string data0Pak = Path.Combine(install!.InstallPath, "DW", "Data0.pak");
        Assert.True(File.Exists(data0Pak), "The retail compiler bootstrap was not found.");

        string? artifactRoot = Environment.GetEnvironmentVariable("DLR_SKINNING_READBACK_ARTIFACT_ROOT");
        bool preserveArtifacts = !string.IsNullOrWhiteSpace(artifactRoot);
        string directory = preserveArtifacts
            ? Path.Combine(Path.GetFullPath(artifactRoot!), "companion-receipt-" + Guid.NewGuid().ToString("N"))
            : RpackTestData.CreateTemporaryDirectory();
        Directory.CreateDirectory(directory);
        try
        {
            FbxModelAuthoringImportResult model = CreateModelWithSameBasenameCompanions();
            Dl1OfficialModelCompilerResult compiled = await Dl1OfficialModelCompiler.CompileAsync(new()
            {
                Model = model,
                CompilerExecutablePath = compiler!,
                RetailData0PakPath = data0Pak,
                OutputRpackPath = Path.Combine(directory, "generic_companion_receipt_pc.rpack"),
                ResourceName = "generic_companion_receipt",
                WorkingDirectoryRoot = Path.Combine(Path.GetDirectoryName(directory)!, "w"),
                AllowIncompleteCharacterDiagnostics = true,
            });

            Assert.Equal(CustomModelBuildState.CompilerValidated, compiled.BuildReceipt.State);
            using JsonDocument receipt = JsonDocument.Parse(await File.ReadAllBytesAsync(compiled.ReceiptPath));
            Dictionary<string, string> hashes = receipt.RootElement.GetProperty("nativeCompanions")
                .EnumerateArray().ToDictionary(
                    item => item.GetProperty("path").GetString()!,
                    item => item.GetProperty("sha256").GetString()!,
                    StringComparer.Ordinal);
            string first = "native-companions/data/characters/body.bel";
            string second = "native-companions/data/other/body.bel";
            Assert.Contains(first, hashes.Keys);
            Assert.Contains(second, hashes.Keys);
            Assert.NotEqual(hashes[first], hashes[second]);
            foreach (string name in new[] { first, second })
            {
                string path = Path.Combine(directory, name.Replace('/', Path.DirectorySeparatorChar));
                Assert.Equal(Sha(await File.ReadAllBytesAsync(path)), hashes[name]);
            }
        }
        finally { if (!preserveArtifacts) RpackTestData.DeleteTemporaryDirectory(directory); }
    }

    private static FbxModelAuthoringImportResult CreateModelWithSameBasenameCompanions()
    {
        CustomModelPackage package = CharacterBodyRegionAuthoringTests.CreateGenericBodyRegionPackage();
        CharacterResourceRecord first = package.Document.CharacterResources!.Resources.Single(resource => resource.Id == "body");
        Assert.Equal("data/characters/body.bel", first.LogicalName);
        byte[] secondBytes = Encoding.UTF8.GetBytes("BodyElement(_OTHER, 1, 0, 0., 10., \"root\")\n");
        string hash = Sha(secondBytes);
        const string entry = "character/resources/body-second.bin";
        CharacterResourceRecord second = first with
        {
            Id = "body-second",
            LogicalName = "data/other/body.bel",
            ContentSha256 = hash,
            EntryPath = entry,
            ByteLength = secondBytes.Length,
        };
        CharacterResourceInventory inventory = package.Document.CharacterResources with
        { Resources = package.Document.CharacterResources.Resources.Add(second) };
        package = package with
        {
            Document = package.Document with { CharacterResources = inventory },
            CompanionPayloads = package.CompanionPayloads.Add(entry, ImmutableArray.Create(secondBytes)),
        };
        return FbxModelAuthoringImporter.ImportPackage(package);
    }

    private static string Sha(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
}
