using System.Collections.Immutable;
using System.Text.Json;
using ReAnimated.Codecs.Models;
using ReAnimated.Codecs.Rp6l;

namespace ReAnimated.Tests;

public sealed class Dl1AnimationContentContractTests
{
    [Fact]
    [Trait("ValidationTier", "Hermetic")]
    [Trait("Gate", "CustomModelDeployment")]
    public void ManifestUsesFrozenLoaderWireShapeAndExactByteFingerprint()
    {
        const string deploymentId = "0123456789abcdef01234567";
        string runtimePack = Dl1DeveloperToolsProjectDeployer.GetAnimationRuntimePackRelativePath(
            "generic runtime pack"u8);
        Dl1AnimationContentManifestBytes result =
            Dl1DeveloperToolsProjectDeployer.CreateAnimationContentManifest(
                deploymentId,
                "generic_character",
                "GenericModel",
                "GenericLibrary",
                DateTimeOffset.UnixEpoch,
                CreateArtifacts(runtimePack));

        Assert.Equal(
            $".dl-reanimated/animation-refresh/manifests/{deploymentId}.json",
            result.ManifestRelativePath);
        Assert.Equal(64, result.Sha256.Length);
        using JsonDocument json = JsonDocument.Parse(result.Utf8Json.ToArray());
        JsonElement root = json.RootElement;
        Assert.Equal(
            "dl-reanimated-animation-content-manifest",
            root.GetProperty("format").GetString());
        Assert.Equal(2, root.GetProperty("schemaVersion").GetInt32());
        Assert.Equal("1970-01-01T00:00:00Z", root.GetProperty("createdUtc").GetString());
        Assert.Equal("generic_character", root.GetProperty("characterId").GetString());
        Assert.Equal("GenericModel", root.GetProperty("modelResourceName").GetString());
        Assert.Equal("GenericLibrary", root.GetProperty("animationLibraryName").GetString());
        Assert.False(root.TryGetProperty("route", out _));
        Assert.Equal(
            ["AliasScript", "AnimationScript", "Animation", "AnimationRuntimePack"],
            root.GetProperty("artifacts")
                .EnumerateArray()
                .Select(static row => row.GetProperty("role").GetString()!)
                .ToArray());
    }

    [Fact]
    [Trait("ValidationTier", "Hermetic")]
    [Trait("Gate", "CustomModelDeployment")]
    public void ManifestEnforcesSharedLoaderArtifactCapAndCanonicalPaths()
    {
        var overLimit = ImmutableArray.CreateBuilder<Dl1AnimationContentArtifact>();
        overLimit.AddRange(CreateArtifacts(
            ".dl-reanimated/animation-refresh/packages/" + new string('f', 64) + ".rpack"));
        for (int index = 1; index <= 125; index++)
        {
            overLimit.Add(new Dl1AnimationContentArtifact(
                Dl1AnimationContentArtifactRole.Animation,
                $"data/characters/animations/clip_{index:D3}.anm2",
                new string('a', 64),
                $"clip_{index:D3}"));
        }

        InvalidDataException cap = Assert.Throws<InvalidDataException>(() =>
            Dl1DeveloperToolsProjectDeployer.CreateAnimationContentManifest(
                "0123456789abcdef01234567",
                "generic_character",
                "GenericModel",
                "GenericLibrary",
                DateTimeOffset.UnixEpoch,
                overLimit));
        Assert.Contains("128", cap.Message, StringComparison.Ordinal);

        ImmutableArray<Dl1AnimationContentArtifact> invalid = CreateArtifacts(
                ".dl-reanimated/animation-refresh/packages/" + new string('f', 64) + ".rpack")
            .SetItem(2, new Dl1AnimationContentArtifact(
                Dl1AnimationContentArtifactRole.Animation,
                "data\\characters\\animations\\clip.anm2",
                new string('a', 64),
                "clip"));
        Assert.Throws<InvalidDataException>(() =>
            Dl1DeveloperToolsProjectDeployer.CreateAnimationContentManifest(
                "0123456789abcdef01234567",
                "generic_character",
                "GenericModel",
                "GenericLibrary",
                DateTimeOffset.UnixEpoch,
                invalid));
    }

    [Theory]
    [InlineData(Dl1AnimationContentArtifactRole.AliasScript, "OtherModel")]
    [InlineData(Dl1AnimationContentArtifactRole.AliasScript, "genericmodel")]
    [InlineData(Dl1AnimationContentArtifactRole.AnimationScript, "OtherLibrary")]
    [InlineData(Dl1AnimationContentArtifactRole.AnimationScript, "genericlibrary")]
    [InlineData(Dl1AnimationContentArtifactRole.AnimationRuntimePack, "OtherLibrary")]
    [InlineData(Dl1AnimationContentArtifactRole.AnimationRuntimePack, "genericlibrary")]
    [Trait("ValidationTier", "Hermetic")]
    [Trait("Gate", "CustomModelDeployment")]
    public void ManifestRejectsMismatchedCrossContractResourceIdentities(
        Dl1AnimationContentArtifactRole role,
        string replacementIdentity)
    {
        ImmutableArray<Dl1AnimationContentArtifact> artifacts = CreateArtifacts(
            ".dl-reanimated/animation-refresh/packages/" + new string('f', 64) + ".rpack");
        int index = Enumerable.Range(0, artifacts.Length).Single(
            candidate => artifacts[candidate].Role == role);
        artifacts = artifacts.SetItem(
            index,
            artifacts[index] with { ResourceIdentity = replacementIdentity });

        InvalidDataException error = Assert.Throws<InvalidDataException>(() =>
            Dl1DeveloperToolsProjectDeployer.CreateAnimationContentManifest(
                "0123456789abcdef01234567",
                "generic_character",
                "GenericModel",
                "GenericLibrary",
                DateTimeOffset.UnixEpoch,
                artifacts));

        Assert.Contains(role.ToString(), error.Message, StringComparison.Ordinal);
        Assert.Contains("identity", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    [Trait("ValidationTier", "Hermetic")]
    [Trait("Gate", "CustomModelDeployment")]
    public async Task ProjectRpackScanFindsAnimationAndScriptIdentityConflicts()
    {
        string directory = RpackTestData.CreateTemporaryDirectory();
        try
        {
            string project = Path.Combine(directory, "project");
            string data = Path.Combine(project, "data");
            Directory.CreateDirectory(data);
            string conflict = Path.Combine(data, "generic_existing_pc.rpack");
            await File.WriteAllBytesAsync(
                conflict,
                Rp6lAnimationLibraryCodec.Build(
                    new Dictionary<string, byte[]>
                    {
                        ["generic_clip"] = "ANM2"u8.ToArray(),
                        ["unrelated_clip"] = "ANM2"u8.ToArray(),
                    },
                    new Dictionary<string, Rp6lAnimationScript>
                    {
                        ["GenericLibrary"] = new("header"u8.ToArray(), "body"u8.ToArray()),
                    }));

            ImmutableArray<Dl1ProjectAnimationRpackConflict> conflicts =
                await Dl1DeveloperToolsProjectDeployer.ScanProjectAnimationRpackConflictsAsync(
                    project,
                    "GenericLibrary",
                    ["generic_clip"]);

            Dl1ProjectAnimationRpackConflict found = Assert.Single(conflicts);
            Assert.Equal("data/generic_existing_pc.rpack", found.RelativePath);
            Assert.Equal(
                ["type-320:generic_clip", "type-322:GenericLibrary"],
                found.ResourceIdentities.ToArray());
        }
        finally
        {
            RpackTestData.DeleteTemporaryDirectory(directory);
        }
    }

    [Fact]
    [Trait("ValidationTier", "Hermetic")]
    [Trait("Gate", "CustomModelDeployment")]
    public void ManifestRejectsCaseInsensitiveDuplicateStableAnimationIdentities()
    {
        string runtimePack =
            ".dl-reanimated/animation-refresh/packages/" +
            new string('f', 64) +
            ".rpack";
        ImmutableArray<Dl1AnimationContentArtifact> artifacts =
            CreateArtifacts(runtimePack).Add(new(
                Dl1AnimationContentArtifactRole.Animation,
                "data/characters/animations/GENERIC_CLIP.anm2",
                new string('e', 64),
                "GENERIC_CLIP"));

        InvalidDataException error = Assert.Throws<InvalidDataException>(() =>
            Dl1DeveloperToolsProjectDeployer.CreateAnimationContentManifest(
                "0123456789abcdef01234567",
                "generic_character",
                "GenericModel",
                "GenericLibrary",
                DateTimeOffset.UnixEpoch,
                artifacts));

        Assert.Contains("repeats animation identity", error.Message, StringComparison.Ordinal);
    }

    private static ImmutableArray<Dl1AnimationContentArtifact> CreateArtifacts(string runtimePack) =>
    [
        new(
            Dl1AnimationContentArtifactRole.AliasScript,
            "data/characters/generic_character/GenericModel.ascr",
            new string('a', 64),
            "GenericModel"),
        new(
            Dl1AnimationContentArtifactRole.AnimationScript,
            "data/characters/animations/animscripts/GenericLibrary.scr",
            new string('b', 64),
            "GenericLibrary"),
        new(
            Dl1AnimationContentArtifactRole.Animation,
            "data/characters/animations/generic_clip.anm2",
            new string('c', 64),
            "generic_clip"),
        new(
            Dl1AnimationContentArtifactRole.AnimationRuntimePack,
            runtimePack,
            Path.GetFileNameWithoutExtension(runtimePack),
            "GenericLibrary"),
    ];
}
