[CmdletBinding()]
param(
    [ValidateSet("Focused", "Hermetic", "Release")]
    [string]$Tier = "Focused",
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Debug",
    [switch]$NoBuild,
    [switch]$ForceAll,
    [string]$CandidateSourceSha256 = "",
    [string]$ReceiptDirectory = "",
    [string]$BlenderExecutable = "",
    [switch]$SkipUnavailableOptionalBlender
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version 2.0

$repositoryRoot = [IO.Path]::GetFullPath(
    (Join-Path $PSScriptRoot ".."))
$testProject = Join-Path $repositoryRoot `
    "tests\ReAnimated.Tests\ReAnimated.Tests.csproj"
if ([string]::IsNullOrWhiteSpace($ReceiptDirectory)) {
    $ReceiptDirectory = Join-Path $repositoryRoot `
        "artifacts\validation\csharp-gates"
}
$resolvedReceiptDirectory = [IO.Path]::GetFullPath(
    [Environment]::ExpandEnvironmentVariables($ReceiptDirectory))
$artifactsRoot = [IO.Path]::GetFullPath(
    (Join-Path $repositoryRoot "artifacts"))
$artifactsPrefix = $artifactsRoot.TrimEnd(
    [IO.Path]::DirectorySeparatorChar,
    [IO.Path]::AltDirectorySeparatorChar) +
    [IO.Path]::DirectorySeparatorChar
if (-not $resolvedReceiptDirectory.StartsWith(
        $artifactsPrefix,
        [StringComparison]::OrdinalIgnoreCase)) {
    throw "Validation receipts must remain below $artifactsRoot."
}

function Get-StringSha256 {
    param([Parameter(Mandatory = $true)][string]$Value)

    $utf8 = New-Object System.Text.UTF8Encoding($false, $true)
    $sha = [Security.Cryptography.SHA256]::Create()
    try {
        return ([BitConverter]::ToString(
            $sha.ComputeHash($utf8.GetBytes($Value)))).
            Replace("-", "").ToLowerInvariant()
    }
    finally {
        $sha.Dispose()
    }
}

function Get-FileSha256 {
    param([Parameter(Mandatory = $true)][string]$Path)

    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).
        Hash.ToLowerInvariant()
}

function Write-AtomicJson {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][object]$Value
    )

    $fullPath = [IO.Path]::GetFullPath($Path)
    $directory = Split-Path -Parent $fullPath
    [IO.Directory]::CreateDirectory($directory) | Out-Null
    $temporary = Join-Path $directory (
        ".{0}.{1}.tmp" -f
            [IO.Path]::GetFileName($fullPath),
            [Guid]::NewGuid().ToString("N"))
    $backup = Join-Path $directory (
        ".{0}.{1}.bak" -f
            [IO.Path]::GetFileName($fullPath),
            [Guid]::NewGuid().ToString("N"))
    $utf8 = New-Object System.Text.UTF8Encoding($false)
    try {
        [IO.File]::WriteAllText(
            $temporary,
            ($Value | ConvertTo-Json -Depth 20),
            $utf8)
        if (Test-Path -LiteralPath $fullPath -PathType Leaf) {
            [IO.File]::Replace($temporary, $fullPath, $backup)
            Remove-Item -LiteralPath $backup -Force
        }
        else {
            [IO.File]::Move($temporary, $fullPath)
        }
    }
    finally {
        if (Test-Path -LiteralPath $temporary -PathType Leaf) {
            Remove-Item -LiteralPath $temporary -Force
        }
        if (Test-Path -LiteralPath $backup -PathType Leaf) {
            Remove-Item -LiteralPath $backup -Force
        }
    }
}

function Get-GateInputs {
    param([Parameter(Mandatory = $true)][object]$Gate)

    $files = New-Object `
        "System.Collections.Generic.Dictionary[string,string]" `
        ([StringComparer]::Ordinal)
    $rootPrefix = $repositoryRoot.TrimEnd(
        [IO.Path]::DirectorySeparatorChar,
        [IO.Path]::AltDirectorySeparatorChar) +
        [IO.Path]::DirectorySeparatorChar
    $addFile = {
        param([string]$FilePath)

        $fullPath = [IO.Path]::GetFullPath($FilePath)
        if (-not $fullPath.StartsWith(
                $rootPrefix,
                [StringComparison]::OrdinalIgnoreCase) -or
            -not (Test-Path -LiteralPath $fullPath -PathType Leaf)) {
            throw "Validation gate '$($Gate.Name)' has an invalid input: $fullPath"
        }
        $relative = $fullPath.Substring($rootPrefix.Length).
            Replace(
                [IO.Path]::DirectorySeparatorChar,
                [char]'/')
        if ($relative -match
            '(^|/)(bin|obj|TestResults|artifacts|__pycache__|\.pytest_cache)(/|$)') {
            return
        }
        if (-not $files.ContainsKey($relative)) {
            $files.Add($relative, (Get-FileSha256 $fullPath))
        }
    }

    foreach ($relative in @(
            "DLReAnimated.slnx",
            "Directory.Build.props",
            "Directory.Packages.props",
            "global.json",
            "tools\validate_csharp.ps1")) {
        & $addFile (Join-Path $repositoryRoot $relative)
    }
    foreach ($relative in @($Gate.InputFiles)) {
        & $addFile (Join-Path $repositoryRoot $relative)
    }
    foreach ($relativeRoot in @($Gate.InputRoots)) {
        $fullRoot = Join-Path $repositoryRoot $relativeRoot
        if (-not (Test-Path -LiteralPath $fullRoot -PathType Container)) {
            throw "Validation input root is missing: $fullRoot"
        }
        foreach ($file in Get-ChildItem `
                     -LiteralPath $fullRoot `
                     -File `
                     -Force `
                     -Recurse) {
            & $addFile $file.FullName
        }
    }

    return @($files.GetEnumerator() |
        Sort-Object Key |
        ForEach-Object {
            [ordered]@{
                path = $_.Key
                sha256 = $_.Value
            }
        })
}

function Get-ValidationEnvironment {
    $renderer = "unavailable"
    try {
        $renderer = @(Get-CimInstance Win32_VideoController `
            -ErrorAction Stop |
            Sort-Object PNPDeviceID |
            ForEach-Object {
                "{0}|{1}|{2}" -f
                    $_.Name,
                    $_.DriverVersion,
                    $_.PNPDeviceID
            }) -join ";"
    }
    catch {
        $renderer = "unavailable:$($_.Exception.GetType().Name)"
    }

    $installedBuild = "unavailable"
    $corpusPath = Join-Path $repositoryRoot `
        "artifacts\validation\dl1-mesh-corpus-1.55.json"
    try {
        if (Test-Path -LiteralPath $corpusPath -PathType Leaf) {
            $corpus = Get-Content -LiteralPath $corpusPath -Raw |
                ConvertFrom-Json
            $executable = [string]$corpus.build.executablePath
            if (Test-Path -LiteralPath $executable -PathType Leaf) {
                $installedBuild = "{0}|{1}" -f
                    [string]$corpus.build.buildFingerprint,
                    (Get-FileSha256 $executable)
            }
        }
    }
    catch {
        $installedBuild = "unavailable:$($_.Exception.GetType().Name)"
    }

    return [ordered]@{
        dotnetSdk = (& dotnet --version).Trim()
        runtime = [Environment]::Version.ToString()
        operatingSystem = [Environment]::OSVersion.VersionString
        processArchitecture = [Runtime.InteropServices.RuntimeInformation]::ProcessArchitecture.ToString()
        configuration = $Configuration
        renderer = $renderer
        installedDl1 = $installedBuild
        blender = if ([string]::IsNullOrWhiteSpace(
                $script:resolvedBlenderExecutable)) {
            "unavailable"
        }
        else {
            "{0}|{1}" -f
                $script:resolvedBlenderExecutable,
                (Get-FileSha256 $script:resolvedBlenderExecutable)
        }
    }
}

function Get-ReceiptReuse {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$Identity
    )

    if ($ForceAll) {
        return [pscustomobject]@{ Reuse = $false; Reason = "force-all" }
    }
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        return [pscustomobject]@{ Reuse = $false; Reason = "receipt-missing" }
    }
    try {
        $receipt = Get-Content -LiteralPath $Path -Raw |
            ConvertFrom-Json
        if ($receipt.format -ne
                "dl-reanimated-validation-gate-receipt-v1" -or
            [int]$receipt.schemaVersion -ne 1 -or
            $receipt.status -ne "passing") {
            return [pscustomobject]@{
                Reuse = $false
                Reason = "receipt-not-an-atomic-passing-v1-receipt"
            }
        }
        if ($receipt.identitySha256 -ne $Identity) {
            return [pscustomobject]@{
                Reuse = $false
                Reason = "content-or-environment-identity-changed"
            }
        }
        return [pscustomobject]@{ Reuse = $true; Reason = "identity-match" }
    }
    catch {
        return [pscustomobject]@{
            Reuse = $false
            Reason = "receipt-malformed:$($_.Exception.GetType().Name)"
        }
    }
}

function Invoke-TestGate {
    param([Parameter(Mandatory = $true)][object]$Gate)

    $arguments = @(
        "test",
        $testProject,
        "--configuration",
        $Configuration,
        "--no-build",
        "--filter",
        [string]$Gate.Filter,
        "--logger",
        "console;verbosity=minimal")
    & dotnet @arguments
    if ($LASTEXITCODE -ne 0) {
        throw "Gate '$($Gate.Name)' failed with exit code $LASTEXITCODE."
    }
}

function Invoke-ScriptGate {
    param([Parameter(Mandatory = $true)][object]$Gate)

    $scriptPath = Join-Path $repositoryRoot ([string]$Gate.Script)
    $parameters = @{
        Configuration = $Configuration
        NoBuild = $true
    }
    & $scriptPath @parameters
    if ($LASTEXITCODE -ne 0) {
        throw "Gate '$($Gate.Name)' failed with exit code $LASTEXITCODE."
    }
}

function Resolve-BlenderExecutable {
    $candidate = $BlenderExecutable
    if ([string]::IsNullOrWhiteSpace($candidate)) {
        $candidate = [string]$env:DLR_BLENDER_EXECUTABLE
    }
    if ([string]::IsNullOrWhiteSpace($candidate)) {
        $candidate = [string]$env:BLENDER_EXECUTABLE
    }
    if ([string]::IsNullOrWhiteSpace($candidate)) {
        $command = Get-Command blender.exe -ErrorAction SilentlyContinue
        if ($null -ne $command) {
            $candidate = $command.Source
        }
    }
    if ([string]::IsNullOrWhiteSpace($candidate)) {
        return ""
    }

    $resolved = [IO.Path]::GetFullPath(
        [Environment]::ExpandEnvironmentVariables(
            $candidate.Trim().Trim('"')))
    if (-not (Test-Path -LiteralPath $resolved -PathType Leaf) -or
        -not [string]::Equals(
            [IO.Path]::GetFileName($resolved),
            "blender.exe",
            [StringComparison]::OrdinalIgnoreCase)) {
        throw "The configured Blender executable is not an existing blender.exe: $resolved"
    }
    return $resolved
}

function Invoke-BlenderGate {
    param([Parameter(Mandatory = $true)][object]$Gate)

    $scriptPath = Join-Path $repositoryRoot ([string]$Gate.Script)
    & $scriptPath `
        -BlenderExecutable $script:resolvedBlenderExecutable `
        -Configuration $Configuration `
        -NoBuild
    if ($LASTEXITCODE -ne 0) {
        throw "Gate '$($Gate.Name)' failed with exit code $LASTEXITCODE."
    }
}

function Invoke-MeshCorpusReceiptGate {
    $corpusPath = Join-Path $repositoryRoot `
        "artifacts\validation\dl1-mesh-corpus-1.55.json"
    $acceptancePath = Join-Path $repositoryRoot `
        "artifacts\validation\dl1-installed-acceptance-1.55.json"
    if (-not (Test-Path -LiteralPath $corpusPath -PathType Leaf) -or
        -not (Test-Path -LiteralPath $acceptancePath -PathType Leaf)) {
        throw "The unchanged 8,738-mesh corpus cannot be reused because its local evidence is missing."
    }
    $corpus = Get-Content -LiteralPath $corpusPath -Raw |
        ConvertFrom-Json
    $acceptance = Get-Content -LiteralPath $acceptancePath -Raw |
        ConvertFrom-Json
    $corpusHash = Get-FileSha256 $corpusPath
    if ($corpus.format -ne "dl-reanimated-dl1-type272-corpus-v2" -or
        -not [bool]$corpus.complete -or
        [int]$corpus.summary.descriptorMeshResourceCount -ne 8738 -or
        [int]$corpus.summary.blockedCount -ne 0 -or
        $acceptance.corpus.reportSha256 -ne $corpusHash -or
        $acceptance.installedBuild.buildFingerprint -ne
            $corpus.build.buildFingerprint) {
        throw "The local mesh-corpus evidence is malformed, incomplete, changed, or for a different DL1 build."
    }
    Write-Host (
        "Verified and reused the unchanged 8,738-mesh corpus report; " +
        "this animation pass did not execute the corpus.")
}

function New-Gate {
    param(
        [string]$Name,
        [string]$Category,
        [string]$Action,
        [string]$Filter = "",
        [string]$Script = "",
        [string[]]$InputRoots = @(),
        [string[]]$InputFiles = @()
    )

    return [pscustomobject]@{
        Name = $Name
        Category = $Category
        Action = $Action
        Filter = $Filter
        Script = $Script
        InputRoots = $InputRoots
        InputFiles = $InputFiles
    }
}

if (-not $NoBuild) {
    & (Join-Path $repositoryRoot "build_csharp.ps1") `
        -Configuration $Configuration `
        -SkipTests
    if ($LASTEXITCODE -ne 0) {
        throw "The validation build failed with exit code $LASTEXITCODE."
    }
}

$codecRoots = @(
    "src\ReAnimated.Core",
    "src\ReAnimated.Codecs",
    "src\ReAnimated.DL1.Assets",
    "src\ReAnimated.Retargeting",
    "src\ReAnimated.Evaluation")
$viewModelRoots = @(
    $codecRoots +
    @("src\ReAnimated.App"))
$rendererRoots = @(
    "src\ReAnimated.Renderer.D3D11",
    "src\ReAnimated.App",
    "src\ReAnimated.Core",
    "src\ReAnimated.Evaluation")
$testRoot = @("tests\ReAnimated.Tests")
$testProjectInputs = @(
    "tests\ReAnimated.Tests\ReAnimated.Tests.csproj",
    "tests\ReAnimated.Tests\RendererGlobalUsings.cs")

function Select-TestInputFiles {
    param([Parameter(Mandatory = $true)][string]$NamePattern)

    return @(
        Get-ChildItem `
            -LiteralPath (Join-Path $repositoryRoot `
                "tests\ReAnimated.Tests") `
            -File `
            -Filter "*.cs" |
        Where-Object {
            [IO.Path]::GetFileNameWithoutExtension($_.Name) -match
                $NamePattern
        } |
        Sort-Object Name |
        ForEach-Object {
            "tests\ReAnimated.Tests\$($_.Name)"
        })
}

$focusedCodecTests = @(Select-TestInputFiles (
    "^(AnimationPlaybackCorrectness|" +
    "AuthoritativeRootMotionTrailSampler|MimicProjectWorkflow|" +
    "Anm2Codec|EvaluationPipeline)Tests$"))
$focusedViewModelTests = @(Select-TestInputFiles (
    "^(AnimationExplorerViewModel|ViewModelTimeline|" +
    "CoreAnimationProject)Tests$"))
$focusedRendererTests = @(Select-TestInputFiles (
    "^(RendererSceneSource|LinkedTargetExternalPreview|" +
    "RendererCpuReference|RendererGpuSkinning)Tests$"))
$hermeticCodecTests = @(Select-TestInputFiles (
    "(Anm2|AnimationScr|AnimationDocument|AuthoringPolicy|CoreAnimation|Evaluation|" +
    "Retarget|RootMotion|Mimic|Morph|IkConstraint|Fbx)"))
$hermeticViewModelTests = @(Select-TestInputFiles (
    "^(AnimationExplorerViewModel|ViewModel|EditorUsability|" +
    "FppControlSurface|FacialPreviewPolicyViewModel|" +
    "AttachmentAuthoring|AppPersistence|ComboBoxTemplate|" +
    "TreeViewSelection)Tests$"))
$hermeticRendererTests = @(Select-TestInputFiles (
    "^(Renderer(?!AuthoringStageGolden)|" +
    "LinkedTargetExternalPreview)"))

$focusedGates = @(
    (New-Gate `
        -Name "focused-codec-evaluation" `
        -Category "codec/evaluation" `
        -Action "test" `
        -Filter "FullyQualifiedName~AnimationPlaybackCorrectnessTests|FullyQualifiedName~AuthoritativeRootMotionTrailSamplerTests|FullyQualifiedName~MimicProjectWorkflowTests|FullyQualifiedName~Anm2CodecTests|FullyQualifiedName~EvaluationPipelineTests" `
        -InputRoots @($codecRoots) `
        -InputFiles @($testProjectInputs + $focusedCodecTests)),
    (New-Gate `
        -Name "focused-viewmodel-wpf" `
        -Category "ViewModel/WPF" `
        -Action "test" `
        -Filter "FullyQualifiedName~AnimationExplorerViewModelTests|FullyQualifiedName~ViewModelTimelineTests|FullyQualifiedName~CoreAnimationProjectTests" `
        -InputRoots @($viewModelRoots) `
        -InputFiles @($testProjectInputs + $focusedViewModelTests)),
    (New-Gate `
        -Name "focused-renderer" `
        -Category "renderer" `
        -Action "test" `
        -Filter "FullyQualifiedName~RendererSceneSourceTests|FullyQualifiedName~LinkedTargetExternalPreviewTests|FullyQualifiedName~RendererCpuReferenceTests|FullyQualifiedName~RendererGpuSkinningTests" `
        -InputRoots @($rendererRoots) `
        -InputFiles @($testProjectInputs + $focusedRendererTests))
)

$hermeticGates = @(
    (New-Gate `
        -Name "hermetic-codec-evaluation" `
        -Category "codec/evaluation" `
        -Action "test" `
        -Filter "FullyQualifiedName~Anm2|FullyQualifiedName~AnimationScr|FullyQualifiedName~AnimationDocument|FullyQualifiedName~AuthoringPolicy|FullyQualifiedName~CoreAnimation|FullyQualifiedName~Evaluation|FullyQualifiedName~Retarget|FullyQualifiedName~RootMotion|FullyQualifiedName~Mimic|FullyQualifiedName~Morph|FullyQualifiedName~IkConstraint|FullyQualifiedName~Fbx" `
        -InputRoots @($codecRoots + @("tests\fixtures")) `
        -InputFiles @($testProjectInputs + $hermeticCodecTests)),
    (New-Gate `
        -Name "hermetic-viewmodel-wpf" `
        -Category "ViewModel/WPF" `
        -Action "test" `
        -Filter "FullyQualifiedName~AnimationExplorerViewModelTests|FullyQualifiedName~ViewModel|FullyQualifiedName~EditorUsability|FullyQualifiedName~FppControlSurface|FullyQualifiedName~FacialPreviewPolicy|FullyQualifiedName~AttachmentAuthoring|FullyQualifiedName~AppPersistence|FullyQualifiedName~ComboBoxTemplate|FullyQualifiedName~TreeViewSelection" `
        -InputRoots @($viewModelRoots) `
        -InputFiles @($testProjectInputs + $hermeticViewModelTests)),
    (New-Gate `
        -Name "hermetic-renderer" `
        -Category "renderer" `
        -Action "test" `
        -Filter "FullyQualifiedName~Renderer&FullyQualifiedName!~RendererAuthoringStageGoldenTests|FullyQualifiedName~LinkedTargetExternalPreviewTests" `
        -InputRoots @($rendererRoots) `
        -InputFiles @($testProjectInputs + $hermeticRendererTests))
)

$releaseGates = @(
    $hermeticGates +
    @(
        (New-Gate `
            -Name "renderer-authoring-goldens" `
            -Category "renderer" `
            -Action "script" `
            -Script "tools\validate_renderer_authoring_goldens.ps1" `
            -InputRoots @(
                $rendererRoots +
                @("tests\ReAnimated.Tests\Fixtures")) `
            -InputFiles @(
                $testProjectInputs +
                @(
                    "tests\ReAnimated.Tests\RendererAuthoringStageGoldenTests.cs",
                    "tests\fixtures\renderer_authoring_stage_goldens_v1.json",
                    "tools\validate_renderer_authoring_goldens.ps1"))),
        (New-Gate `
            -Name "installed-dl1-animation-controls" `
            -Category "installed DL1" `
            -Action "script" `
            -Script "tools\validate_dl1_animation_playback.ps1" `
            -InputRoots @($codecRoots + $rendererRoots) `
            -InputFiles @(
                $testProjectInputs +
                @(
                    "tests\ReAnimated.Tests\InstalledDl1AnimationPlaybackControlTests.cs",
                    "tests\ReAnimated.Tests\RpackTestData.cs",
                    "tools\validate_dl1_animation_playback.ps1"))),
        (New-Gate `
            -Name "reuse-mesh-corpus" `
            -Category "installed DL1" `
            -Action "mesh-receipt" `
            -InputRoots @(
                "src\ReAnimated.Codecs\CompactMesh",
                "src\ReAnimated.DL1.Assets\Meshes") `
            -InputFiles @("tools\validate_dl1_mesh_corpus.ps1")),
        (New-Gate `
            -Name "blender-handoff" `
            -Category "Blender" `
            -Action "blender" `
            -Script "tools\validate_dl1_blender_handoff.ps1" `
            -InputRoots @(
                "src\ReAnimated.App\Blender",
                "src\ReAnimated.Codecs") `
            -InputFiles @(
                $testProjectInputs +
                @(
                    "tests\ReAnimated.Tests\InstalledBlenderFbxAcceptanceTests.cs",
                    "tests\ReAnimated.Tests\RpackTestData.cs",
                    "tools\validate_dl1_blender_handoff.ps1")))
    ))

$gates = switch ($Tier) {
    "Focused" { $focusedGates; break }
    "Hermetic" { $hermeticGates; break }
    "Release" { $releaseGates; break }
}
$script:resolvedBlenderExecutable = Resolve-BlenderExecutable
$environment = Get-ValidationEnvironment
$results = @()
[IO.Directory]::CreateDirectory($resolvedReceiptDirectory) | Out-Null

foreach ($gate in $gates) {
    if ($gate.Action -eq "blender" -and
        [string]::IsNullOrWhiteSpace($script:resolvedBlenderExecutable)) {
        if (-not $SkipUnavailableOptionalBlender) {
            throw (
                "Gate '$($gate.Name)' requires Blender. Supply " +
                "-BlenderExecutable, DLR_BLENDER_EXECUTABLE, or " +
                "-SkipUnavailableOptionalBlender.")
        }
        Write-Warning (
            "[Skipped] $($gate.Category) / $($gate.Name): no Blender " +
            "executable is available; no passing receipt was written.")
        $results += [ordered]@{
            gate = $gate.Name
            category = $gate.Category
            disposition = "skipped-optional-unavailable"
            invalidationReason = "blender-unavailable"
            identitySha256 = ""
            receiptPath = ""
        }
        continue
    }
    $inputs = @(Get-GateInputs $gate)
    $identityPayload = [ordered]@{
        format = "dl-reanimated-validation-gate-identity-v1"
        gate = $gate.Name
        category = $gate.Category
        action = $gate.Action
        filter = $gate.Filter
        script = $gate.Script
        inputs = $inputs
        environment = $environment
    }
    $identityJson = $identityPayload | ConvertTo-Json -Depth 20 -Compress
    $identity = Get-StringSha256 $identityJson
    $receiptPath = Join-Path $resolvedReceiptDirectory (
        "$($gate.Name).json")
    $reuse = Get-ReceiptReuse $receiptPath $identity
    $started = [DateTimeOffset]::UtcNow
    if ($reuse.Reuse) {
        Write-Host (
            "[Reused] {0} / {1}: {2}" -f
                $gate.Category,
                $gate.Name,
                $reuse.Reason)
        $disposition = "reused"
    }
    else {
        Write-Host (
            "[Run] {0} / {1}; invalidation: {2}" -f
                $gate.Category,
                $gate.Name,
                $reuse.Reason)
        switch ($gate.Action) {
            "test" { Invoke-TestGate $gate; break }
            "script" { Invoke-ScriptGate $gate; break }
            "blender" { Invoke-BlenderGate $gate; break }
            "mesh-receipt" { Invoke-MeshCorpusReceiptGate; break }
            default { throw "Unknown gate action '$($gate.Action)'." }
        }
        $completed = [DateTimeOffset]::UtcNow
        $receipt = [ordered]@{
            format = "dl-reanimated-validation-gate-receipt-v1"
            schemaVersion = 1
            status = "passing"
            gate = $gate.Name
            category = $gate.Category
            tier = $Tier
            identitySha256 = $identity
            candidateSourceSha256 = $CandidateSourceSha256
            startedUtc = $started.ToString("O")
            completedUtc = $completed.ToString("O")
            elapsedSeconds = ($completed - $started).TotalSeconds
            inputs = $inputs
            environment = $environment
        }
        Write-AtomicJson $receiptPath $receipt
        $disposition = "ran"
    }
    $results += [ordered]@{
        gate = $gate.Name
        category = $gate.Category
        disposition = $disposition
        invalidationReason = $reuse.Reason
        identitySha256 = $identity
        receiptPath = $receiptPath
    }
}

$summaryPath = Join-Path (
    Split-Path -Parent $resolvedReceiptDirectory) (
        "csharp-validation-{0}-latest.json" -f $Tier.ToLowerInvariant())
$summary = [ordered]@{
    format = "dl-reanimated-validation-summary-v1"
    schemaVersion = 1
    tier = $Tier
    configuration = $Configuration
    completedUtc = [DateTimeOffset]::UtcNow.ToString("O")
    forceAll = [bool]$ForceAll
    candidateSourceSha256 = $CandidateSourceSha256
    gates = $results
}
Write-AtomicJson $summaryPath $summary
Write-Host (
    "C# {0} validation passed: {1} ran, {2} reused. Summary: {3}" -f
        $Tier,
        @($results | Where-Object disposition -eq "ran").Count,
        @($results | Where-Object disposition -eq "reused").Count,
        $summaryPath)
