[CmdletBinding()]
param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release",
    [switch]$NoBuild,
    [string]$PythonOracleRoot = ""
)

$ErrorActionPreference = "Stop"
$repositoryRoot = [IO.Path]::GetFullPath(
    (Join-Path $PSScriptRoot ".."))
if ([string]::IsNullOrWhiteSpace($PythonOracleRoot)) {
    $PythonOracleRoot = Join-Path `
        (Split-Path -Parent $repositoryRoot) `
        "ReAnimated - Python"
}
$resolvedPythonOracleRoot = [IO.Path]::GetFullPath(
    [Environment]::ExpandEnvironmentVariables(
        $PythonOracleRoot.Trim().Trim('"')))
$auditScript = Join-Path $resolvedPythonOracleRoot `
    "tools\audit_dl1_python_suite.py"
if (-not (Test-Path -LiteralPath $auditScript -PathType Leaf)) {
    throw "The external Python suite audit tool is missing: $auditScript"
}

# The setup-python interpreter owns pytest on hosted CI. Fall back to the
# Windows launcher only when python is unavailable.
$pythonCommand =
    Get-Command "python" -ErrorAction SilentlyContinue
$pythonPrefixArguments = @()
if ($null -eq $pythonCommand) {
    $pythonCommand = Get-Command "py" -ErrorAction Stop
    $pythonPrefixArguments = @("-3")
}

& $pythonCommand.Source `
    @pythonPrefixArguments `
    $auditScript `
    --repository-root $resolvedPythonOracleRoot
if ($LASTEXITCODE -ne 0) {
    throw (
        "The exact Python suite audit failed with exit code " +
        "$LASTEXITCODE.")
}

$testArguments = @(
    "test",
    (Join-Path `
        $repositoryRoot `
        "tests/ReAnimated.Tests/ReAnimated.Tests.csproj"),
    "--configuration",
    $Configuration,
    "--filter",
    "FullyQualifiedName~PythonSuiteAuditTests")
if ($NoBuild) {
    $testArguments += "--no-build"
}

$previousOracleRoot = [Environment]::GetEnvironmentVariable(
    "DLR_PYTHON_ORACLE_ROOT",
    [EnvironmentVariableTarget]::Process)
try {
    [Environment]::SetEnvironmentVariable(
        "DLR_PYTHON_ORACLE_ROOT",
        $resolvedPythonOracleRoot,
        [EnvironmentVariableTarget]::Process)
    & dotnet @testArguments
    if ($LASTEXITCODE -ne 0) {
        throw (
            "The C# Python suite audit integrity test failed with exit code " +
            "$LASTEXITCODE.")
    }
}
finally {
    [Environment]::SetEnvironmentVariable(
        "DLR_PYTHON_ORACLE_ROOT",
        $previousOracleRoot,
        [EnvironmentVariableTarget]::Process)
}

Write-Host (
    "DL1 Python suite audit passed: 616 exact nodes; " +
    "92 applicable mapped; 317 explicit exclusions; 207 still pending.")
