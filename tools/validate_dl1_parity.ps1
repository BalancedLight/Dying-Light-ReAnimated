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
foreach ($requiredPath in @("dlanm2_gui", "tests", "tools")) {
    if (-not (Test-Path -LiteralPath (
            Join-Path $resolvedPythonOracleRoot $requiredPath))) {
        throw (
            "The external Python oracle is missing '$requiredPath' below " +
            "$resolvedPythonOracleRoot")
    }
}
$temporaryRoot = [IO.Path]::GetFullPath(
    (Join-Path ([IO.Path]::GetTempPath()) (
        "dl-reanimated-parity-" + [Guid]::NewGuid().ToString("N"))))
$expectedTemporaryPrefix = [IO.Path]::GetFullPath(
    ([IO.Path]::GetTempPath())).TrimEnd(
        [IO.Path]::DirectorySeparatorChar,
        [IO.Path]::AltDirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
if (-not $temporaryRoot.StartsWith(
        $expectedTemporaryPrefix,
        [StringComparison]::OrdinalIgnoreCase)) {
    throw "Refusing to use a parity temporary directory outside the OS temp directory."
}

$previousOracle = [Environment]::GetEnvironmentVariable(
    "DLR_PYTHON_PARITY_ORACLE",
    [EnvironmentVariableTarget]::Process)
$previousSemanticOracle = [Environment]::GetEnvironmentVariable(
    "DLR_PYTHON_SEMANTIC_PARITY_ORACLE",
    [EnvironmentVariableTarget]::Process)
$previousFedOracle = [Environment]::GetEnvironmentVariable(
    "DLR_PYTHON_FED_PARITY_ORACLE",
    [EnvironmentVariableTarget]::Process)
$previousSuiteRoot = [Environment]::GetEnvironmentVariable(
    "DLR_PYTHON_ORACLE_ROOT",
    [EnvironmentVariableTarget]::Process)
try {
    [IO.Directory]::CreateDirectory($temporaryRoot) | Out-Null
    $generatedOracle = Join-Path $temporaryRoot "dl1_python_csharp_parity_v1.json"
    $oracleScript = Join-Path $resolvedPythonOracleRoot `
        "tools\dl1_python_parity_oracle.py"
    $generatedSemanticOracle = Join-Path $temporaryRoot `
        "dl1_python_csharp_semantic_parity_v1.json"
    $semanticOracleScript = Join-Path $resolvedPythonOracleRoot `
        "tools\dl1_python_semantic_parity_oracle.py"
    $generatedFedOracle = Join-Path $temporaryRoot `
        "dl1_python_csharp_fed_parity_v1.json"
    $fedOracleScript = Join-Path $resolvedPythonOracleRoot `
        "tools\dl1_python_fed_parity_oracle.py"
    $suiteAuditScript = Join-Path $resolvedPythonOracleRoot `
        "tools\audit_dl1_python_suite.py"

    # The setup-python interpreter owns pytest in CI. Prefer it for the exact
    # 616-node audit even when the Windows py launcher resolves elsewhere.
    $auditPythonCommand =
        Get-Command "python" -ErrorAction SilentlyContinue
    $auditPythonPrefixArguments = @()
    if ($null -eq $auditPythonCommand) {
        $auditPythonCommand =
            Get-Command "py" -ErrorAction Stop
        $auditPythonPrefixArguments = @("-3")
    }
    & $auditPythonCommand.Source `
        @auditPythonPrefixArguments `
        $suiteAuditScript `
        --repository-root $resolvedPythonOracleRoot
    if ($LASTEXITCODE -ne 0) {
        throw (
            "The exact Python suite audit failed with exit code " +
            "$LASTEXITCODE.")
    }

    $pythonCommand = Get-Command "py" -ErrorAction SilentlyContinue
    if ($null -ne $pythonCommand) {
        & $pythonCommand.Source -3 $oracleScript `
            --repository-root $resolvedPythonOracleRoot `
            --output $generatedOracle
        if ($LASTEXITCODE -ne 0) {
            throw "The Python ANM2 parity oracle failed with exit code $LASTEXITCODE."
        }
        & $pythonCommand.Source -3 $semanticOracleScript `
            --repository-root $resolvedPythonOracleRoot `
            --output $generatedSemanticOracle
        if ($LASTEXITCODE -ne 0) {
            throw "The Python semantic parity oracle failed with exit code $LASTEXITCODE."
        }
        & $pythonCommand.Source -3 $fedOracleScript `
            --repository-root $resolvedPythonOracleRoot `
            --output $generatedFedOracle
    }
    else {
        $pythonCommand = Get-Command "python" -ErrorAction Stop
        & $pythonCommand.Source $oracleScript `
            --repository-root $resolvedPythonOracleRoot `
            --output $generatedOracle
        if ($LASTEXITCODE -ne 0) {
            throw "The Python ANM2 parity oracle failed with exit code $LASTEXITCODE."
        }
        & $pythonCommand.Source $semanticOracleScript `
            --repository-root $resolvedPythonOracleRoot `
            --output $generatedSemanticOracle
        if ($LASTEXITCODE -ne 0) {
            throw "The Python semantic parity oracle failed with exit code $LASTEXITCODE."
        }
        & $pythonCommand.Source $fedOracleScript `
            --repository-root $resolvedPythonOracleRoot `
            --output $generatedFedOracle
    }
    if ($LASTEXITCODE -ne 0) {
        throw "The Python FED parity oracle failed with exit code $LASTEXITCODE."
    }

    $checkedOracle = Join-Path $repositoryRoot `
        "tests/fixtures/dl1_python_csharp_parity_v1.json"
    $generatedHash = (
        Get-FileHash -LiteralPath $generatedOracle -Algorithm SHA256).Hash
    $checkedHash = (
        Get-FileHash -LiteralPath $checkedOracle -Algorithm SHA256).Hash
    if ($generatedHash -ne $checkedHash) {
        throw (
            "The checked parity oracle is stale. Generated SHA256: " +
            "$generatedHash; checked SHA256: $checkedHash. Review the Python " +
            "oracle change before replacing the fixture.")
    }

    $checkedSemanticOracle = Join-Path $repositoryRoot `
        "tests/fixtures/dl1_python_csharp_semantic_parity_v1.json"
    $generatedSemanticHash = (
        Get-FileHash -LiteralPath $generatedSemanticOracle -Algorithm SHA256).Hash
    $checkedSemanticHash = (
        Get-FileHash -LiteralPath $checkedSemanticOracle -Algorithm SHA256).Hash
    if ($generatedSemanticHash -ne $checkedSemanticHash) {
        throw (
            "The checked semantic parity oracle is stale. Generated SHA256: " +
            "$generatedSemanticHash; checked SHA256: $checkedSemanticHash. " +
            "Review the Python oracle change before replacing the fixture.")
    }

    $checkedFedOracle = Join-Path $repositoryRoot `
        "tests/fixtures/dl1_python_csharp_fed_parity_v1.json"
    $generatedFedHash = (
        Get-FileHash -LiteralPath $generatedFedOracle -Algorithm SHA256).Hash
    $checkedFedHash = (
        Get-FileHash -LiteralPath $checkedFedOracle -Algorithm SHA256).Hash
    if ($generatedFedHash -ne $checkedFedHash) {
        throw (
            "The checked FED parity oracle is stale. Generated SHA256: " +
            "$generatedFedHash; checked SHA256: $checkedFedHash. " +
            "Review the Python oracle change before replacing the fixture.")
    }

    [Environment]::SetEnvironmentVariable(
        "DLR_PYTHON_PARITY_ORACLE",
        $generatedOracle,
        [EnvironmentVariableTarget]::Process)
    [Environment]::SetEnvironmentVariable(
        "DLR_PYTHON_SEMANTIC_PARITY_ORACLE",
        $generatedSemanticOracle,
        [EnvironmentVariableTarget]::Process)
    [Environment]::SetEnvironmentVariable(
        "DLR_PYTHON_FED_PARITY_ORACLE",
        $generatedFedOracle,
        [EnvironmentVariableTarget]::Process)
    [Environment]::SetEnvironmentVariable(
        "DLR_PYTHON_ORACLE_ROOT",
        $resolvedPythonOracleRoot,
        [EnvironmentVariableTarget]::Process)
    $testArguments = @(
        "test",
        (Join-Path $repositoryRoot "tests/ReAnimated.Tests/ReAnimated.Tests.csproj"),
        "--configuration",
        $Configuration,
        "--filter",
        (
            "FullyQualifiedName~PythonOracleParityTests|" +
            "FullyQualifiedName~PythonSemanticParityTests|" +
            "FullyQualifiedName~PythonFedParityTests|" +
            "FullyQualifiedName~PythonSuiteAuditTests")
    )
    if ($NoBuild) {
        $testArguments += "--no-build"
    }

    & dotnet @testArguments
    if ($LASTEXITCODE -ne 0) {
        throw "The C# parity test failed with exit code $LASTEXITCODE."
    }

    Write-Host (
        "DL1 bounded Python/C# parity passed. ANM2 oracle SHA256: " +
        $generatedHash + "; semantic oracle SHA256: " +
        $generatedSemanticHash + "; FED oracle SHA256: " +
        $generatedFedHash +
        "; exact Python suite audit: 616 classified nodes")
}
finally {
    [Environment]::SetEnvironmentVariable(
        "DLR_PYTHON_PARITY_ORACLE",
        $previousOracle,
        [EnvironmentVariableTarget]::Process)
    [Environment]::SetEnvironmentVariable(
        "DLR_PYTHON_SEMANTIC_PARITY_ORACLE",
        $previousSemanticOracle,
        [EnvironmentVariableTarget]::Process)
    [Environment]::SetEnvironmentVariable(
        "DLR_PYTHON_FED_PARITY_ORACLE",
        $previousFedOracle,
        [EnvironmentVariableTarget]::Process)
    [Environment]::SetEnvironmentVariable(
        "DLR_PYTHON_ORACLE_ROOT",
        $previousSuiteRoot,
        [EnvironmentVariableTarget]::Process)
    if ([IO.Directory]::Exists($temporaryRoot)) {
        $resolvedTemporaryRoot = [IO.Path]::GetFullPath($temporaryRoot)
        if ($resolvedTemporaryRoot.StartsWith(
                $expectedTemporaryPrefix,
                [StringComparison]::OrdinalIgnoreCase)) {
            Remove-Item -LiteralPath $resolvedTemporaryRoot -Recurse -Force
        }
    }
}
