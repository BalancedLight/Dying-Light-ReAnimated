using ReAnimated.DL1.Assets.Discovery;

namespace ReAnimated.Tests;

public sealed class AssetSteamDiscoveryTests
{
    [Fact]
    public void ModernLibraryFoldersDoesNotTreatNumericAppMetadataAsLibraries()
    {
        string directory = RpackTestData.CreateTemporaryDirectory();
        try
        {
            string steamApps = Path.Combine(directory, "steamapps");
            string library = Path.Combine(directory, "SecondaryLibrary");
            Directory.CreateDirectory(steamApps);
            Directory.CreateDirectory(library);
            string escapedLibrary = library.Replace(
                "\\",
                "\\\\",
                StringComparison.Ordinal);
            File.WriteAllText(
                Path.Combine(steamApps, "libraryfolders.vdf"),
                $$"""
                "libraryfolders"
                {
                    "0"
                    {
                        "path" "{{escapedLibrary}}"
                        "label" ""
                        "apps"
                        {
                            "239140" "52000000000"
                            "570" "19000000000"
                        }
                    }
                }
                """);

            IReadOnlyList<Dl1InstallLocation> discovered =
                SteamInstallDiscovery.Discover(
                    additionalSteamRoots: [directory],
                    explicitInstallPaths: []);

            Assert.Contains(
                discovered,
                location => string.Equals(
                    location.SteamLibraryPath,
                    library,
                    StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(
                discovered,
                location =>
                    location.SteamLibraryPath.EndsWith(
                        $"{Path.DirectorySeparatorChar}570",
                        StringComparison.OrdinalIgnoreCase) ||
                    location.SteamLibraryPath.EndsWith(
                        $"{Path.DirectorySeparatorChar}52000000000",
                        StringComparison.OrdinalIgnoreCase) ||
                    location.SteamLibraryPath.EndsWith(
                        $"{Path.DirectorySeparatorChar}19000000000",
                        StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            RpackTestData.DeleteTemporaryDirectory(directory);
        }
    }

    [Fact]
    public void LegacyNumericLibraryPairStillAcceptsAbsolutePath()
    {
        string directory = RpackTestData.CreateTemporaryDirectory();
        try
        {
            string steamApps = Path.Combine(directory, "steamapps");
            string library = Path.Combine(directory, "LegacyLibrary");
            Directory.CreateDirectory(steamApps);
            Directory.CreateDirectory(library);
            string escapedLibrary = library.Replace(
                "\\",
                "\\\\",
                StringComparison.Ordinal);
            File.WriteAllText(
                Path.Combine(steamApps, "libraryfolders.vdf"),
                $$"""
                "libraryfolders"
                {
                    "1" "{{escapedLibrary}}"
                }
                """);

            IReadOnlyList<Dl1InstallLocation> discovered =
                SteamInstallDiscovery.Discover(
                    additionalSteamRoots: [directory],
                    explicitInstallPaths: []);

            Assert.Contains(
                discovered,
                location => string.Equals(
                    location.SteamLibraryPath,
                    library,
                    StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            RpackTestData.DeleteTemporaryDirectory(directory);
        }
    }

    [Fact]
    public void ValidatesAndReturnsExplicitInstall()
    {
        string directory = RpackTestData.CreateTemporaryDirectory();
        try
        {
            Directory.CreateDirectory(Path.Combine(directory, "DW", "Data"));
            File.WriteAllBytes(
                Path.Combine(directory, "DyingLightGame.exe"),
                []);
            File.WriteAllBytes(
                Path.Combine(directory, "DW", "Data0.pak"),
                []);
            Assert.True(SteamInstallDiscovery.IsDyingLightInstall(directory));

            IReadOnlyList<Dl1InstallLocation> discovered =
                SteamInstallDiscovery.Discover(
                    additionalSteamRoots: [],
                    explicitInstallPaths: [directory]);
            Dl1InstallLocation explicitInstall = Assert.Single(
                discovered,
                location => string.Equals(
                    location.InstallPath,
                    directory,
                    StringComparison.OrdinalIgnoreCase));
            Assert.True(explicitInstall.IsValid);
            Assert.Equal("explicit", explicitInstall.Source);
        }
        finally
        {
            RpackTestData.DeleteTemporaryDirectory(directory);
        }
    }
}
