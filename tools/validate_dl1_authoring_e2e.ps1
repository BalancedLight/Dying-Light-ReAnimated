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
        "Gate=DL1AuthoringEndToEnd"
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
            "The redistributable DL1 authoring regression failed with " +
            "exit code $LASTEXITCODE.")
    }

    Write-Host (
        "DL1 redistributable authoring end-to-end regression passed.")
}
finally {
    Pop-Location
}
