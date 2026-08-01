using System.Text.Json;
using ReAnimated.App.Infrastructure;
using ReAnimated.Cli;

namespace ReAnimated.Tests;

public sealed class CliDispatchTests
{
    private static readonly string[] ExpectedCommands =
    [
        "version",
        "inspect-anm2",
        "inspect-fbx",
        "inspect-rpack",
        "inspect-fed",
        "new-project",
        "validate-project",
        "discover-dl1",
        "fingerprint-dl1",
        "index-dl1",
        "build-animation-rpack",
        "export-project",
    ];

    [Fact]
    public void DispatchContractRecognizesEveryDeveloperCliVerbOnly()
    {
        Assert.Equal(
            "dl-reanimated-cli-dispatch-v1",
            CliApplication.DispatchContract);
        Assert.Equal(
            ExpectedCommands,
            CliApplication.SupportedCommands);

        foreach (string command in ExpectedCommands)
        {
            Assert.True(
                CliApplication.IsInvocation([command]));
            Assert.True(
                CliApplication.IsInvocation(
                    [command.ToUpperInvariant()]));
        }

        foreach (string help in
                 new[] { "-h", "--help", "help", "/?" })
        {
            Assert.True(
                CliApplication.IsInvocation([help]));
        }

        Assert.False(CliApplication.IsInvocation([]));
        Assert.False(
            CliApplication.IsInvocation(
                [PackageSelfTest.Switch]));
        Assert.False(
            CliApplication.IsInvocation(
                [WpfStartupSmoke.Switch]));
        Assert.False(
            CliApplication.IsInvocation(
                ["unrecognized-startup-argument"]));
    }

    [Fact]
    public async Task PackageSelfTestReportsEmbeddedCliDispatchContract()
    {
        string directory = Path.Combine(
            Path.GetTempPath(),
            $"dl-reanimated-cli-self-test-{Guid.NewGuid():N}");
        string? verifiedDirectory = null;
        try
        {
            await PackageSelfTest.RunAsync(
                [PackageSelfTest.Switch, directory]);

            string resultPath = Path.Combine(
                directory,
                PackageSelfTest.ResultFileName);
            using JsonDocument result =
                JsonDocument.Parse(
                    await File.ReadAllTextAsync(
                        resultPath));
            JsonElement root = result.RootElement;
            Assert.Equal(
                PackageSelfTest.SchemaVersion,
                root.GetProperty("schemaVersion")
                    .GetInt32());
            Assert.False(
                string.IsNullOrWhiteSpace(
                    root.GetProperty(
                            "assemblyInformationalVersion")
                        .GetString()));
            Assert.Equal(
                CliApplication.DispatchContract,
                root.GetProperty(
                        "cliDispatchContract")
                    .GetString());
            string[] reportedCommands =
                root.GetProperty("cliCommands")
                    .EnumerateArray()
                    .Select(static element =>
                        element.GetString() ??
                        string.Empty)
                    .ToArray();
            Assert.Equal(
                ExpectedCommands,
                reportedCommands);

            if (root.GetProperty("provenanceVerified")
                .GetBoolean())
            {
                verifiedDirectory = Path.Combine(
                    Path.GetTempPath(),
                    $"dl-reanimated-verified-self-test-{Guid.NewGuid():N}");
                await PackageSelfTest.RunAsync(
                    [
                        PackageSelfTest.Switch,
                        verifiedDirectory,
                        root.GetProperty(
                                "candidateSourceSha256")
                            .GetString()!,
                        root.GetProperty(
                                "candidateInputCount")
                            .GetInt32()
                            .ToString(
                                System.Globalization.CultureInfo.InvariantCulture),
                        root.GetProperty("gitHead")
                            .GetString()!,
                        root.GetProperty("gitState")
                            .GetString()!,
                        root.GetProperty(
                                "sourceIdentity")
                            .GetString()!,
                        root.GetProperty(
                                "assemblyInformationalVersion")
                            .GetString()!,
                    ]);
                Assert.True(
                    File.Exists(
                        Path.Combine(
                            verifiedDirectory,
                            PackageSelfTest.ResultFileName)));
            }
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(
                    directory,
                    recursive: true);
            }
            if (verifiedDirectory is not null &&
                Directory.Exists(verifiedDirectory))
            {
                Directory.Delete(
                    verifiedDirectory,
                    recursive: true);
            }
        }
    }

    [Fact]
    public async Task PackageSelfTestRejectsMismatchedReleaseProvenance()
    {
        string directory = Path.Combine(
            Path.GetTempPath(),
            $"dl-reanimated-provenance-self-test-{Guid.NewGuid():N}");
        string candidateSha256 = new('2', 64);
        string gitHead = new('1', 40);
        const int inputCount = 7;
        const string gitState = "dirty";
        string sourceIdentity =
            $"dl-reanimated-csharp-source-v1.git-{gitHead}.state-{gitState}.inputs-{inputCount}.sha256-{candidateSha256}";
        string informationalVersion =
            $"0.1.0+git.{gitHead}.candidate.{candidateSha256}.inputs.{inputCount}.{gitState}";
        try
        {
            await Assert.ThrowsAsync<InvalidDataException>(
                () => PackageSelfTest.RunAsync(
                    [
                        PackageSelfTest.Switch,
                        directory,
                        candidateSha256,
                        inputCount.ToString(
                            System.Globalization.CultureInfo.InvariantCulture),
                        gitHead,
                        gitState,
                        sourceIdentity,
                        informationalVersion,
                    ]));
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(
                    directory,
                    recursive: true);
            }
        }
    }
}
