[CmdletBinding()]
param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release",
    [string]$ReportPath = "",
    [string]$CachePath = "",
    [switch]$NoBuild
)

$ErrorActionPreference = "Stop"
$repositoryRoot = Split-Path -Parent $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($ReportPath)) {
    $ReportPath = Join-Path $repositoryRoot "artifacts\validation\dl1-mesh-corpus-1.55.json"
}

$ReportPath = [System.IO.Path]::GetFullPath($ReportPath)
if (-not [string]::IsNullOrWhiteSpace($CachePath)) {
    $CachePath = [System.IO.Path]::GetFullPath($CachePath)
}

$priorRun = $env:DLR_RUN_INSTALLED_MESH_CORPUS
$priorReport = $env:DLR_MESH_CORPUS_REPORT_PATH
$priorCache = $env:DLR_MESH_CORPUS_CACHE_PATH
try {
    $env:DLR_RUN_INSTALLED_MESH_CORPUS = "1"
    $env:DLR_MESH_CORPUS_REPORT_PATH = $ReportPath
    if (-not [string]::IsNullOrWhiteSpace($CachePath)) {
        $env:DLR_MESH_CORPUS_CACHE_PATH = $CachePath
    }

    $arguments = @(
        "test",
        (Join-Path $repositoryRoot "tests\ReAnimated.Tests\ReAnimated.Tests.csproj"),
        "-c",
        $Configuration,
        "--filter",
        "FullyQualifiedName=ReAnimated.Tests.InstalledDl1MeshCorpusAcceptanceTests.ConfiguredInstalledType272CorpusIsFullyClassified",
        "--logger",
        "console;verbosity=normal"
    )
    if ($NoBuild) {
        $arguments += "--no-build"
    }

    & dotnet @arguments
    if ($LASTEXITCODE -ne 0) {
        throw "DL1 type-272 corpus validation failed. Inspect '$ReportPath'."
    }

    Write-Host "DL1 type-272 corpus validation passed."
    Write-Host "Report: $ReportPath"
}
finally {
    $env:DLR_RUN_INSTALLED_MESH_CORPUS = $priorRun
    $env:DLR_MESH_CORPUS_REPORT_PATH = $priorReport
    $env:DLR_MESH_CORPUS_CACHE_PATH = $priorCache
}
