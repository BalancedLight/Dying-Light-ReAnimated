[CmdletBinding()]
param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release",
    [switch]$NoBuild
)

$ErrorActionPreference = "Stop"
$repositoryRoot = [IO.Path]::GetFullPath(
    (Join-Path $PSScriptRoot ".."))
$testProject = Join-Path $repositoryRoot `
    "tests\ReAnimated.Tests\ReAnimated.Tests.csproj"

Push-Location $repositoryRoot
try {
    if (-not $NoBuild) {
        & dotnet restore $testProject --locked-mode
        if ($LASTEXITCODE -ne 0) {
            throw (
                "The locked C# restore failed with exit code " +
                "$LASTEXITCODE.")
        }
    }

    $testArguments = @(
        "test",
        $testProject,
        "--configuration",
        $Configuration,
        "--filter",
        "Gate=DL1InstalledAuthoringEndToEnd",
        "--logger",
        "console;verbosity=detailed"
    )
    if ($NoBuild) {
        $testArguments += "--no-build"
    }
    else {
        $testArguments += "--no-restore"
    }

    & dotnet @testArguments
    if ($LASTEXITCODE -ne 0) {
        throw (
            "The installed-retail DL1 authoring regression failed with " +
            "exit code $LASTEXITCODE.")
    }

    Write-Host (
        "Installed-retail DL1 authoring regression passed. " +
        "The detailed test output states EXERCISED when a complete Steam " +
        "installation was available, or NOT EXERCISED otherwise.")
}
finally {
    Pop-Location
}
