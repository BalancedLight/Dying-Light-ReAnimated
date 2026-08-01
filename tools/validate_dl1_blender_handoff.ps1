[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$BlenderExecutable,
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release",
    [switch]$NoBuild
)

$ErrorActionPreference = "Stop"
$repositoryRoot = [IO.Path]::GetFullPath(
    (Join-Path $PSScriptRoot ".."))
$testProject = Join-Path $repositoryRoot `
    "tests\ReAnimated.Tests\ReAnimated.Tests.csproj"
$resolvedBlender = [IO.Path]::GetFullPath(
    [Environment]::ExpandEnvironmentVariables(
        $BlenderExecutable.Trim().Trim('"')))
if (-not (Test-Path -LiteralPath $resolvedBlender -PathType Leaf) -or
    -not [string]::Equals(
        [IO.Path]::GetFileName($resolvedBlender),
        "blender.exe",
        [StringComparison]::OrdinalIgnoreCase)) {
    throw "BlenderExecutable must name an existing blender.exe."
}

$previousBlender = [Environment]::GetEnvironmentVariable(
    "BLENDER_EXECUTABLE",
    [EnvironmentVariableTarget]::Process)
$previousAcceptance = [Environment]::GetEnvironmentVariable(
    "DLR_RUN_INSTALLED_BLENDER_ACCEPTANCE",
    [EnvironmentVariableTarget]::Process)

Push-Location $repositoryRoot
try {
    & $resolvedBlender --background --factory-startup --version
    if ($LASTEXITCODE -ne 0) {
        throw (
            "The Blender background-mode probe failed with exit code " +
            "$LASTEXITCODE.")
    }

    $env:BLENDER_EXECUTABLE = $resolvedBlender
    $env:DLR_RUN_INSTALLED_BLENDER_ACCEPTANCE = "1"
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
        "Gate=DL1InstalledBlenderFbx",
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
            "The installed Blender/DL1 FBX handoff acceptance failed " +
            "with exit code $LASTEXITCODE.")
    }

    Write-Host (
        "Installed Blender/DL1 retail-mesh multi-Action FBX acceptance " +
        "passed; generated retail-derived files were deleted by the test.")
}
finally {
    if ($null -eq $previousBlender) {
        Remove-Item Env:BLENDER_EXECUTABLE -ErrorAction SilentlyContinue
    }
    else {
        $env:BLENDER_EXECUTABLE = $previousBlender
    }

    if ($null -eq $previousAcceptance) {
        Remove-Item Env:DLR_RUN_INSTALLED_BLENDER_ACCEPTANCE `
            -ErrorAction SilentlyContinue
    }
    else {
        $env:DLR_RUN_INSTALLED_BLENDER_ACCEPTANCE = $previousAcceptance
    }

    Pop-Location
}
