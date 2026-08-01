[CmdletBinding()]
param(
    [ValidateSet("Release")]
    [string]$Configuration = "Release",
    [string]$ReceiptPath = "",
    [string]$CorpusReportPath = "",
    [string]$CachePath = "",
    [switch]$NoBuild
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version 2.0

$validatedBuildFingerprint =
    "89f98e5c77a2eb36767a614acf894a4f18f55e6efa8f912ca7ef66b45a0dfa13"
$validatedFileVersion = "1.55.0.0"
$validatedProductVersion = "1.55.0.0"
$expectedCorpusPackCount = 62
$expectedCorpusMeshResourceCount = 8738
$expectedCorpusPresentationCount = 8736
$expectedCorpusRenderableCount = 8714
$expectedCorpusNonDisplayGeometryCount = 22
$expectedCorpusRenderMeshCount = 73335
$expectedCorpusSkinnedRenderMeshCount = 6805
$expectedCorpusBlockedCount = 0
$receiptFormat = "dl-reanimated-installed-acceptance-receipt"
$receiptSchemaVersion = 1

$repositoryRoot = [System.IO.Path]::GetFullPath(
    (Join-Path $PSScriptRoot ".."))
$testProject = Join-Path $repositoryRoot `
    "tests\ReAnimated.Tests\ReAnimated.Tests.csproj"
$testAssembly = Join-Path $repositoryRoot `
    "tests\ReAnimated.Tests\bin\$Configuration\net10.0-windows10.0.19041.0\ReAnimated.Tests.dll"
$applicationAssembly = Join-Path $repositoryRoot `
    "src\ReAnimated.App\bin\$Configuration\net10.0-windows10.0.19041.0\DLReAnimated.dll"

if ([string]::IsNullOrWhiteSpace($ReceiptPath)) {
    $ReceiptPath = Join-Path $repositoryRoot `
        "artifacts\validation\dl1-installed-acceptance-1.55.json"
}
if ([string]::IsNullOrWhiteSpace($CorpusReportPath)) {
    $CorpusReportPath = Join-Path $repositoryRoot `
        "artifacts\validation\dl1-mesh-corpus-1.55.json"
}
$resolvedReceiptPath = [System.IO.Path]::GetFullPath($ReceiptPath)
$resolvedCorpusReportPath =
    [System.IO.Path]::GetFullPath($CorpusReportPath)
$resolvedCachePath = if ([string]::IsNullOrWhiteSpace($CachePath)) {
    Join-Path (
        [Environment]::GetFolderPath(
            [Environment+SpecialFolder]::LocalApplicationData)) `
        "DLReAnimated\AssetCache\Rp6l\InstalledAcceptance"
}
else {
    [System.IO.Path]::GetFullPath($CachePath)
}

function Get-FileSha256 {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path
    )

    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).
        Hash.ToLowerInvariant()
}

function Get-StringSha256 {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Value
    )

    $encoding = New-Object System.Text.UTF8Encoding($false)
    $bytes = $encoding.GetBytes($Value)
    try {
        $sha = [System.Security.Cryptography.SHA256]::Create()
        try {
            return (
                [BitConverter]::ToString(
                    $sha.ComputeHash($bytes)).
                    Replace("-", "").
                    ToLowerInvariant())
        }
        finally {
            $sha.Dispose()
        }
    }
    finally {
        [Array]::Clear($bytes, 0, $bytes.Length)
    }
}

function Write-AtomicUtf8Json {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path,
        [Parameter(Mandatory = $true)]
        [object]$Value
    )

    $parent = Split-Path -Parent $Path
    if ([string]::IsNullOrWhiteSpace($parent)) {
        throw "The receipt path has no parent directory."
    }
    New-Item -ItemType Directory -Path $parent -Force | Out-Null
    $temporaryPath = "{0}.tmp.{1}" -f `
        $Path,
        [Guid]::NewGuid().ToString("N")
    $backupPath = "{0}.bak.{1}" -f `
        $Path,
        [Guid]::NewGuid().ToString("N")
    $encoding = New-Object System.Text.UTF8Encoding($false)
    try {
        $json = $Value | ConvertTo-Json -Depth 20
        [System.IO.File]::WriteAllText(
            $temporaryPath,
            $json + [Environment]::NewLine,
            $encoding)
        if (Test-Path -LiteralPath $Path -PathType Leaf) {
            [System.IO.File]::Replace(
                $temporaryPath,
                $Path,
                $backupPath,
                $true)
            if (Test-Path -LiteralPath $backupPath -PathType Leaf) {
                Remove-Item -LiteralPath $backupPath -Force
            }
        }
        else {
            [System.IO.File]::Move($temporaryPath, $Path)
        }
    }
    finally {
        if (Test-Path -LiteralPath $temporaryPath -PathType Leaf) {
            Remove-Item -LiteralPath $temporaryPath -Force
        }
        if (Test-Path -LiteralPath $backupPath -PathType Leaf) {
            Remove-Item -LiteralPath $backupPath -Force
        }
    }
}

function Read-RequiredTrxResult {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path,
        [Parameter(Mandatory = $true)]
        [string]$ExpectedFullyQualifiedName
    )

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw "The required TRX result was not created: $Path"
    }
    [xml]$document = Get-Content -LiteralPath $Path -Raw
    $namespace = New-Object System.Xml.XmlNamespaceManager(
        $document.NameTable)
    $namespace.AddNamespace(
        "t",
        "http://microsoft.com/schemas/VisualStudio/TeamTest/2010")
    $definitions = @(
        $document.SelectNodes(
            "//t:TestDefinitions/t:UnitTest",
            $namespace))
    $matchingDefinitions = @(
        $definitions | Where-Object {
            $method = $_.SelectSingleNode("t:TestMethod", $namespace)
            $null -ne $method -and
            ("{0}.{1}" -f `
                $method.GetAttribute("className"),
                $method.GetAttribute("name")) -eq
                $ExpectedFullyQualifiedName
        })
    if ($matchingDefinitions.Count -ne 1) {
        throw (
            "Expected exactly one TRX definition for " +
            "'$ExpectedFullyQualifiedName', found " +
            "$($matchingDefinitions.Count).")
    }

    $testId = $matchingDefinitions[0].GetAttribute("id")
    $results = @(
        $document.SelectNodes(
            "//t:Results/t:UnitTestResult",
            $namespace) |
            Where-Object {
                $_.GetAttribute("testId") -eq $testId
            })
    if ($results.Count -ne 1) {
        throw (
            "Expected exactly one executed TRX result for " +
            "'$ExpectedFullyQualifiedName', found $($results.Count).")
    }
    $result = $results[0]
    $outcome = $result.GetAttribute("outcome")
    if ($outcome -ne "Passed") {
        throw (
            "Installed acceptance '$ExpectedFullyQualifiedName' " +
            "reported outcome '$outcome'.")
    }

    return [ordered]@{
        fullyQualifiedName = $ExpectedFullyQualifiedName
        outcome = $outcome
        duration = $result.GetAttribute("duration")
        startTime = $result.GetAttribute("startTime")
        endTime = $result.GetAttribute("endTime")
        computerName = $result.GetAttribute("computerName")
    }
}

$requiredTests = @(
    "ReAnimated.Tests.InstalledDl1MeshCorpusAcceptanceTests.ConfiguredInstalledType272CorpusIsFullyClassified"
    "ReAnimated.Tests.RpackInstalledCorpusTests.InstalledConfiguredPacksOpenWithoutMaterializingLogicalChunksWhenAvailable"
    "ReAnimated.Tests.FedRetailCorpusTests.InstalledPlayerFedCompatibilityIsExactAndWrongFamilyFailsClosed"
    "ReAnimated.Tests.FedRetailCorpusTests.InstalledFedCorpusParsesStrictlyWhenAvailable"
    "ReAnimated.Tests.RpackRetailValidationTests.InstalledCommonMeshesIndexesAndDecodesDemolisherWhenAvailable"
    "ReAnimated.Tests.RetailMorphValidationTests.InstalledPlayerTppDecodesRetailMorphDeltasWhenAvailable"
    "ReAnimated.Tests.RetailMaterialResolutionTests.InstalledArmoredResolvesRetailMaterialAndBaseColorWhenAvailable"
    "ReAnimated.Tests.InstalledArmoredSkeletonIntegrityTests.InstalledArmoredBindHierarchyAndSkinPalettesStayCoherent"
    "ReAnimated.Tests.InstalledDl1AuthoringEndToEndTests.InstalledRetailControlTraversesAuthoringAndRpackFlowWhenAvailable"
    "ReAnimated.Tests.InstalledDl1MeshOrientationTests.PlayerFppTriangleWindingAgreesWithDecodedNormals"
    "ReAnimated.Tests.InstalledDl1OrdinaryNonFiniteUvEvidenceTests.InstalledOrdinaryMaterialNonFiniteUvEvidenceIsLocked"
    "ReAnimated.Tests.InstalledDl1VisualReferenceControlTests.InstalledOversizedPhysicalRigRowsUseCompactPalettesOnWarp"
    "ReAnimated.Tests.InstalledDl1RigFamilyProfileTests.InstalledBuildExposesAndClassifiesBoundedFamilyControls"
    "ReAnimated.Tests.InstalledDl1SkinningLayoutEvidenceTests.InstalledZeroWeightAndHeadLodLayoutsRemainAuditable"
    "ReAnimated.Tests.InstalledDl1SurvivorBlendAssociationTests.InstalledNoBlendDeclarationsUseFiniteEntityWorldPath"
    "ReAnimated.Tests.InstalledDl1TrailingTriangleEvidenceTests.InstalledScHeadIndexTailRemainsAuditable"
    "ReAnimated.Tests.InstalledDl1VisualReferenceControlTests.InstalledVisualControlsDecodeIntoCoherentPreviewPayloads"
    "ReAnimated.Tests.InstalledDl1VisualReferenceControlTests.InstalledVisualControlsRenderLitMeshesAndSkeletonsOnWarp"
    "ReAnimated.Tests.InstalledDl1RigPromotionEvidenceTests.ClassifyBlockedNonTrsEntitiesByEffectiveSkinUse"
)

$receiptDirectory = Split-Path -Parent $resolvedReceiptPath
if ([string]::IsNullOrWhiteSpace($receiptDirectory)) {
    throw "The receipt path has no parent directory."
}
New-Item -ItemType Directory -Path $receiptDirectory -Force | Out-Null
$stageName = ".installed-acceptance-stage-{0}" -f `
    [Guid]::NewGuid().ToString("N")
$stageRoot = [System.IO.Path]::GetFullPath(
    (Join-Path $receiptDirectory $stageName))
$requiredStagePrefix =
    [System.IO.Path]::GetFullPath($receiptDirectory).TrimEnd(
        [System.IO.Path]::DirectorySeparatorChar,
        [System.IO.Path]::AltDirectorySeparatorChar) +
    [System.IO.Path]::DirectorySeparatorChar
if (-not $stageRoot.StartsWith(
        $requiredStagePrefix,
        [System.StringComparison]::OrdinalIgnoreCase) -or
    [System.IO.Path]::GetFileName($stageRoot) -ne $stageName) {
    throw "The installed-acceptance staging directory escaped its managed parent."
}
New-Item -ItemType Directory -Path $stageRoot | Out-Null

$priorCorpusRun = $env:DLR_RUN_INSTALLED_MESH_CORPUS
$priorCorpusReport = $env:DLR_MESH_CORPUS_REPORT_PATH
$priorCorpusCache = $env:DLR_MESH_CORPUS_CACHE_PATH
$priorSurvivorEvidence =
    $env:DLR_RUN_SURVIVOR_BLEND_ASSOCIATION
$priorRigPromotionEvidence =
    $env:DLR_RUN_INSTALLED_RIG_PROMOTION_EVIDENCE
$completed = $false
$startedUtc = [DateTimeOffset]::UtcNow
Push-Location $repositoryRoot
try {
    if (-not $NoBuild) {
        & dotnet restore $testProject --locked-mode
        if ($LASTEXITCODE -ne 0) {
            throw (
                "The locked installed-acceptance restore failed with " +
                "exit code $LASTEXITCODE.")
        }
        & dotnet build `
            $testProject `
            --configuration $Configuration `
            --no-restore `
            -p:AutoPublishSingleFileOnSolutionBuild=false
        if ($LASTEXITCODE -ne 0) {
            throw (
                "The installed-acceptance build failed with exit code " +
                "$LASTEXITCODE.")
        }
    }

    foreach ($requiredFile in @($testAssembly, $applicationAssembly)) {
        if (-not (Test-Path -LiteralPath $requiredFile -PathType Leaf)) {
            throw (
                "The release acceptance input is missing: " +
                "$requiredFile. Build Release or omit -NoBuild.")
        }
    }

    $fingerprintOutput = @(
        & dotnet $applicationAssembly "fingerprint-dl1" 2>&1)
    if ($LASTEXITCODE -ne 0) {
        throw (
            "The production DL1 fingerprint command failed with exit " +
            "code $LASTEXITCODE. " +
            ($fingerprintOutput -join [Environment]::NewLine))
    }
    $fingerprintJson = $fingerprintOutput -join [Environment]::NewLine
    $fingerprintReport = $fingerprintJson | ConvertFrom-Json
    if ($fingerprintReport.format -ne
            "dl-reanimated-dl1-build-fingerprint-v1" -or
        [bool]$fingerprintReport.gameProcessLaunched -or
        $fingerprintReport.fingerprint.buildFingerprint -ne
            $validatedBuildFingerprint -or
        $fingerprintReport.fingerprint.fileVersion -ne
            $validatedFileVersion -or
        $fingerprintReport.fingerprint.productVersion -ne
            $validatedProductVersion) {
        throw (
            "Installed acceptance requires the validated Windows DL1 1.55 " +
            "build fingerprint $validatedBuildFingerprint. The discovered " +
            "build does not match.")
    }

    $installPath = [System.IO.Path]::GetFullPath(
        [string]$fingerprintReport.fingerprint.installPath)
    $requiredRetailFiles = @(
        "DyingLightGame.exe"
        "DW\Data\common_meshes_PC.rpack"
        "DW\Data\common_cod_1_PC.rpack"
        "DW\Data\optimized_dx11.mp"
        "DW\Data0.pak"
        "DW_DLC17\Data\wasteland_final_PC.rpack"
        "DW_DLC17\Data\wasteland_PC.rpack"
        "DW_DLC49\Data\hellraid_PC.rpack"
    )
    $retailFileEvidence = @()
    foreach ($relativePath in $requiredRetailFiles) {
        $fullPath = [System.IO.Path]::GetFullPath(
            (Join-Path $installPath $relativePath))
        $installPrefix = $installPath.TrimEnd(
            [System.IO.Path]::DirectorySeparatorChar,
            [System.IO.Path]::AltDirectorySeparatorChar) +
            [System.IO.Path]::DirectorySeparatorChar
        if (-not $fullPath.StartsWith(
                $installPrefix,
                [System.StringComparison]::OrdinalIgnoreCase) -or
            -not (Test-Path -LiteralPath $fullPath -PathType Leaf)) {
            throw (
                "The validated installed acceptance file is missing or " +
                "outside the install root: $relativePath")
        }
        $file = Get-Item -LiteralPath $fullPath
        $retailFileEvidence += [ordered]@{
            relativePath = $relativePath.Replace("\", "/")
            length = [long]$file.Length
            lastWriteTimeUtc = $file.LastWriteTimeUtc.ToString("o")
        }
    }

    $env:DLR_RUN_INSTALLED_MESH_CORPUS = "1"
    $env:DLR_MESH_CORPUS_REPORT_PATH =
        $resolvedCorpusReportPath
    $env:DLR_MESH_CORPUS_CACHE_PATH = $resolvedCachePath
    $env:DLR_RUN_SURVIVOR_BLEND_ASSOCIATION = "1"
    $env:DLR_RUN_INSTALLED_RIG_PROMOTION_EVIDENCE = "1"

    $testResults = @()
    for ($index = 0; $index -lt $requiredTests.Count; $index++) {
        $testName = $requiredTests[$index]
        $resultFileName = "{0:D2}-{1}.trx" -f `
            ($index + 1),
            ($testName -replace '[^A-Za-z0-9_.-]', '_')
        $resultPath = Join-Path $stageRoot $resultFileName
        Write-Host (
            "[{0}/{1}] {2}" -f `
                ($index + 1),
                $requiredTests.Count,
                $testName)
        $arguments = @(
            "test"
            $testProject
            "--configuration"
            $Configuration
            "--no-build"
            "--no-restore"
            "--filter"
            "FullyQualifiedName=$testName"
            "--results-directory"
            $stageRoot
            "--logger"
            "trx;LogFileName=$resultFileName"
            "--logger"
            "console;verbosity=normal"
        )
        & dotnet @arguments
        if ($LASTEXITCODE -ne 0) {
            throw (
                "Required installed acceptance '$testName' failed with " +
                "exit code $LASTEXITCODE.")
        }
        $testResults += Read-RequiredTrxResult `
            -Path $resultPath `
            -ExpectedFullyQualifiedName $testName
    }

    if (-not (Test-Path `
            -LiteralPath $resolvedCorpusReportPath `
            -PathType Leaf)) {
        throw (
            "The required installed corpus report was not created: " +
            $resolvedCorpusReportPath)
    }
    $corpus = Get-Content `
        -LiteralPath $resolvedCorpusReportPath `
        -Raw | ConvertFrom-Json
    if (-not [bool]$corpus.complete -or
        $corpus.build.buildFingerprint -ne
            $validatedBuildFingerprint -or
        [int]$corpus.summary.packCount -ne
            $expectedCorpusPackCount -or
        [int]$corpus.summary.meshResourceCount -ne
            $expectedCorpusMeshResourceCount -or
        [int]$corpus.summary.presentationValidatedCount -ne
            $expectedCorpusPresentationCount -or
        [int]$corpus.summary.presentationRenderableCount -ne
            $expectedCorpusRenderableCount -or
        [int]$corpus.summary.nonDisplayGeometryCount -ne
            $expectedCorpusNonDisplayGeometryCount -or
        [int]$corpus.summary.renderMeshCount -ne
            $expectedCorpusRenderMeshCount -or
        [int]$corpus.summary.skinnedRenderMeshCount -ne
            $expectedCorpusSkinnedRenderMeshCount -or
        [int]$corpus.summary.blockedCount -ne
            $expectedCorpusBlockedCount) {
        throw (
            "The installed corpus report is incomplete or does not match " +
            "the validated Windows DL1 1.55 acceptance counts.")
    }

    $gitHead = (& git rev-parse HEAD).Trim()
    if ($LASTEXITCODE -ne 0 -or
        $gitHead -notmatch '^[0-9a-fA-F]{40}$') {
        throw "Could not resolve the current Git HEAD for the receipt."
    }
    $gitState = if (@(git status --porcelain).Count -eq 0) {
        "clean"
    }
    else {
        "dirty"
    }
    $dotnetSdk = (& dotnet --version).Trim()
    if ($LASTEXITCODE -ne 0 -or
        [string]::IsNullOrWhiteSpace($dotnetSdk)) {
        throw "Could not resolve the .NET SDK identity."
    }
    $testAssemblySha256 = Get-FileSha256 -Path $testAssembly
    $applicationAssemblySha256 =
        Get-FileSha256 -Path $applicationAssembly
    $corpusReportSha256 =
        Get-FileSha256 -Path $resolvedCorpusReportPath
    $runnerSha256 = Get-FileSha256 -Path $PSCommandPath

    $canonicalEvidence = @(
        $receiptFormat
        "schema=$receiptSchemaVersion"
        "profile=windows-dl1-1.55"
        "build=$validatedBuildFingerprint"
        "test-assembly=$testAssemblySha256"
        "application-assembly=$applicationAssemblySha256"
        "corpus=$corpusReportSha256"
        "runner=$runnerSha256"
        "git-head=$($gitHead.ToLowerInvariant())"
        "git-state=$gitState"
    )
    foreach ($testResult in $testResults) {
        $canonicalEvidence += (
            "test={0}|{1}" -f `
                $testResult.fullyQualifiedName,
                $testResult.outcome)
    }
    $evidenceSha256 = Get-StringSha256 `
        -Value ($canonicalEvidence -join "`n")
    $completedUtc = [DateTimeOffset]::UtcNow
    $receipt = [ordered]@{
        format = $receiptFormat
        schemaVersion = $receiptSchemaVersion
        complete = $true
        profile = "windows-dl1-1.55"
        startedUtc = $startedUtc.ToString("o")
        completedUtc = $completedUtc.ToString("o")
        elapsedSeconds = [Math]::Round(
            ($completedUtc - $startedUtc).TotalSeconds,
            3)
        gameLaunched = $false
        liveGameEvidence = $false
        evidenceSha256 = $evidenceSha256
        repository = [ordered]@{
            root = $repositoryRoot
            gitHead = $gitHead.ToLowerInvariant()
            gitState = $gitState
        }
        runner = [ordered]@{
            path = $PSCommandPath
            sha256 = $runnerSha256
            configuration = $Configuration
            dotnetSdk = $dotnetSdk
            testAssemblyPath = $testAssembly
            testAssemblySha256 = $testAssemblySha256
            applicationAssemblyPath = $applicationAssembly
            applicationAssemblySha256 =
                $applicationAssemblySha256
        }
        installedBuild = [ordered]@{
            installPath = $installPath
            executablePath =
                [string]$fingerprintReport.fingerprint.executablePath
            executableSize =
                [long]$fingerprintReport.fingerprint.executableSize
            executableSha256 =
                [string]$fingerprintReport.fingerprint.executableSha256
            fileVersion =
                [string]$fingerprintReport.fingerprint.fileVersion
            productVersion =
                [string]$fingerprintReport.fingerprint.productVersion
            buildFingerprint =
                [string]$fingerprintReport.fingerprint.buildFingerprint
            requiredFiles = $retailFileEvidence
        }
        tests = [ordered]@{
            expectedCount = $requiredTests.Count
            executedCount = $testResults.Count
            passedCount = @(
                $testResults | Where-Object {
                    $_.outcome -eq "Passed"
                }).Count
            results = $testResults
        }
        corpus = [ordered]@{
            reportPath = $resolvedCorpusReportPath
            reportSha256 = $corpusReportSha256
            packCount = [int]$corpus.summary.packCount
            meshResourceCount =
                [int]$corpus.summary.meshResourceCount
            geometryDecodedCount =
                [int]$corpus.summary.geometryDecodedCount
            presentationValidatedCount =
                [int]$corpus.summary.presentationValidatedCount
            presentationRenderableCount =
                [int]$corpus.summary.presentationRenderableCount
            nonDisplayGeometryCount =
                [int]$corpus.summary.nonDisplayGeometryCount
            renderMeshCount =
                [int]$corpus.summary.renderMeshCount
            skinnedRenderMeshCount =
                [int]$corpus.summary.skinnedRenderMeshCount
            blockedCount = [int]$corpus.summary.blockedCount
            peakWorkingSetBytes =
                [long]$corpus.summary.peakWorkingSetBytes
        }
        exclusions = @(
            [ordered]@{
                gate = "installed-blender-fbx"
                reason =
                    "Optional Blender dependency; validated separately by tools/validate_dl1_blender_handoff.ps1."
            }
            [ordered]@{
                gate = "live-dying-light"
                reason =
                    "This receipt is read-only offline evidence and never launches the game."
            }
            [ordered]@{
                gate = "physical-renderer-and-clean-machine"
                reason =
                    "Physical GPU, adapter-change, Remote Desktop, longevity, and clean-machine gates require separate environments."
            }
        )
    }

    Write-AtomicUtf8Json `
        -Path $resolvedReceiptPath `
        -Value $receipt
    $receiptSha256 = Get-FileSha256 -Path $resolvedReceiptPath
    Write-Host "Installed DL1 1.55 acceptance passed."
    Write-Host "Receipt: $resolvedReceiptPath"
    Write-Host "Receipt SHA-256: $receiptSha256"
    Write-Host "The game was not launched; this is not live-game proof."
    $completed = $true
}
finally {
    $env:DLR_RUN_INSTALLED_MESH_CORPUS = $priorCorpusRun
    $env:DLR_MESH_CORPUS_REPORT_PATH = $priorCorpusReport
    $env:DLR_MESH_CORPUS_CACHE_PATH = $priorCorpusCache
    $env:DLR_RUN_SURVIVOR_BLEND_ASSOCIATION =
        $priorSurvivorEvidence
    $env:DLR_RUN_INSTALLED_RIG_PROMOTION_EVIDENCE =
        $priorRigPromotionEvidence
    Pop-Location

    if ($completed -and
        (Test-Path -LiteralPath $stageRoot -PathType Container)) {
        Remove-Item -LiteralPath $stageRoot -Recurse -Force
    }
    elseif (Test-Path -LiteralPath $stageRoot -PathType Container) {
        Write-Warning (
            "Installed-acceptance diagnostics were preserved at " +
            "'$stageRoot'. No complete receipt was written.")
    }
}
