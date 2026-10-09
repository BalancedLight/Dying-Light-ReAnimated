using System.Diagnostics;
using System.Text.Json;
using ReAnimated.Cli;

namespace ReAnimated.Tests;

public sealed class PackageCliDispatchContractTests
{
    private const string ContractFailure =
        "The packaged executable does not report the complete CLI dispatch contract.";

    [Fact]
    public async Task PackageValidatorAcceptsCurrentCliDispatchReport()
    {
        ValidationResult result = await ValidateReportAsync(
            CliApplication.SupportedCommands,
            CliApplication.DispatchContract);

        Assert.True(result.ExitCode == 0, result.StandardError);
        Assert.Contains(
            "package-cli-contract-accepted",
            result.StandardOutput,
            StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("inspect-source-msh")]
    [InlineData("bind-player-appearance")]
    [InlineData("character")]
    [InlineData("material-graph")]
    [InlineData("app")]
    public async Task PackageValidatorRejectsMissingCommand(string missingCommand)
    {
        Assert.Contains(missingCommand, CliApplication.SupportedCommands);
        string[] commands = CliApplication.SupportedCommands
            .Where(command => command != missingCommand)
            .ToArray();

        ValidationResult result = await ValidateReportAsync(
            commands,
            CliApplication.DispatchContract);

        AssertRejected(result);
        Assert.Contains(missingCommand, result.StandardError, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PackageValidatorRejectsUnknownCommandAtUnchangedCount()
    {
        string[] commands = CliApplication.SupportedCommands.ToArray();
        commands[^1] = "unknown-package-command";

        ValidationResult result = await ValidateReportAsync(
            commands,
            CliApplication.DispatchContract);

        AssertRejected(result);
        Assert.Contains(
            "unknown-package-command",
            result.StandardError,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task PackageValidatorRejectsDuplicateCommand()
    {
        ValidationResult result = await ValidateReportAsync(
            [.. CliApplication.SupportedCommands, CliApplication.SupportedCommands[0]],
            CliApplication.DispatchContract);

        AssertRejected(result);
    }

    [Fact]
    public async Task PackageValidatorRejectsWrongContractVersion()
    {
        const string wrongContract = "dl-reanimated-cli-dispatch-v999";
        ValidationResult result = await ValidateReportAsync(
            CliApplication.SupportedCommands,
            wrongContract);

        AssertRejected(result);
        Assert.Contains(wrongContract, result.StandardError, StringComparison.Ordinal);
    }

    private static void AssertRejected(ValidationResult result)
    {
        Assert.Equal(1, result.ExitCode);
        Assert.Contains(ContractFailure, result.StandardError, StringComparison.Ordinal);
        Assert.Contains("Expected:", result.StandardError, StringComparison.Ordinal);
        Assert.Contains("Reported:", result.StandardError, StringComparison.Ordinal);
        Assert.Contains("Contract:", result.StandardError, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "package-cli-contract-accepted",
            result.StandardOutput,
            StringComparison.Ordinal);
    }

    private static async Task<ValidationResult> ValidateReportAsync(
        IReadOnlyList<string> commands,
        string contract)
    {
        string directory = RpackTestData.CreateTemporaryDirectory();
        try
        {
            string reportPath = Path.Combine(directory, "report.json");
            await File.WriteAllTextAsync(
                reportPath,
                JsonSerializer.Serialize(new
                {
                    cliDispatchContract = contract,
                    cliCommands = commands,
                }));
            string driverPath = Path.Combine(directory, "validate-contract.ps1");
            await File.WriteAllTextAsync(driverPath, """
                param([string]$PackageScript, [string]$ReportPath)
                $ErrorActionPreference = "Stop"
                try {
                    $tokens = $null
                    $parseErrors = $null
                    $ast = [System.Management.Automation.Language.Parser]::ParseFile(
                        $PackageScript, [ref]$tokens, [ref]$parseErrors)
                    if ($parseErrors.Count -ne 0) {
                        throw "The package script has PowerShell parse errors."
                    }
                    $functions = @($ast.FindAll({
                        param($node)
                        $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and
                            $node.Name -eq "Assert-PackageCliDispatchContract"
                    }, $true))
                    if ($functions.Count -ne 1) {
                        throw "Expected exactly one package CLI dispatch validator."
                    }
                    # Load only the real validator; never execute the package build.
                    . ([scriptblock]::Create($functions[0].Extent.Text))
                    $report = Get-Content -LiteralPath $ReportPath -Raw | ConvertFrom-Json
                    Assert-PackageCliDispatchContract -Result $report
                    Write-Output "package-cli-contract-accepted"
                    exit 0
                }
                catch {
                    [Console]::Error.WriteLine($_.Exception.Message)
                    exit 1
                }
                """);

            var startInfo = new ProcessStartInfo
            {
                FileName = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.System),
                    "WindowsPowerShell", "v1.0", "powershell.exe"),
                WorkingDirectory = directory,
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            foreach (string argument in new[]
            {
                "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass",
                "-File", driverPath,
                "-PackageScript", Path.Combine(
                    TestRepositoryPaths.FindRepositoryRoot(), "package_csharp.ps1"),
                "-ReportPath", reportPath,
            })
            {
                startInfo.ArgumentList.Add(argument);
            }

            using var process = new Process { StartInfo = startInfo };
            Assert.True(process.Start(), "The package validator process did not start.");
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            Task<string> standardOutput = process.StandardOutput.ReadToEndAsync(timeout.Token);
            Task<string> standardError = process.StandardError.ReadToEndAsync(timeout.Token);
            try
            {
                await process.WaitForExitAsync(timeout.Token);
                return new ValidationResult(
                    process.ExitCode,
                    await standardOutput,
                    await standardError);
            }
            catch (OperationCanceledException) when (timeout.IsCancellationRequested)
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                    await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
                }
                throw new TimeoutException("The package CLI dispatch validator exceeded 30 seconds.");
            }
        }
        finally
        {
            RpackTestData.DeleteTemporaryDirectory(directory);
        }
    }

    private sealed record ValidationResult(
        int ExitCode,
        string StandardOutput,
        string StandardError);
}
