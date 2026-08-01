[CmdletBinding()]
param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release",
    [switch]$NoBuild,
    [switch]$Force
)

$ErrorActionPreference = "Stop"
$repositoryRoot = [IO.Path]::GetFullPath(
    (Join-Path $PSScriptRoot ".."))
$testProject = Join-Path $repositoryRoot `
    "tests\ReAnimated.Tests\ReAnimated.Tests.csproj"
if (-not $NoBuild) {
    & (Join-Path $repositoryRoot "build_csharp.ps1") `
        -Configuration $Configuration `
        -SkipTests
    if ($LASTEXITCODE -ne 0) {
        throw "The installed animation-control build failed with exit $LASTEXITCODE."
    }
}

$previous = [Environment]::GetEnvironmentVariable(
    "DLR_RUN_INSTALLED_ANIMATION_PLAYBACK",
    [EnvironmentVariableTarget]::Process)
try {
    [Environment]::SetEnvironmentVariable(
        "DLR_RUN_INSTALLED_ANIMATION_PLAYBACK",
        "1",
        [EnvironmentVariableTarget]::Process)
    & dotnet test $testProject `
        --configuration $Configuration `
        --no-build `
        --filter `
        "FullyQualifiedName~InstalledDl1AnimationPlaybackControlTests" `
        --logger "console;verbosity=minimal"
    if ($LASTEXITCODE -ne 0) {
        throw "Installed DL1 animation playback controls failed with exit $LASTEXITCODE."
    }
}
finally {
    [Environment]::SetEnvironmentVariable(
        "DLR_RUN_INSTALLED_ANIMATION_PLAYBACK",
        $previous,
        [EnvironmentVariableTarget]::Process)
}

Write-Host "Installed DL1 1.55 named animation playback controls passed."
