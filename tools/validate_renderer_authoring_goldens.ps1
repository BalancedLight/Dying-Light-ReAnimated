[CmdletBinding()]
param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release",
    [string]$OutputDirectory =
        "artifacts\validation\renderer-authoring-goldens",
    [switch]$NoBuild
)

$ErrorActionPreference = "Stop"
$repositoryRoot = [IO.Path]::GetFullPath(
    (Join-Path $PSScriptRoot ".."))
$artifactsRoot = [IO.Path]::GetFullPath(
    (Join-Path $repositoryRoot "artifacts"))
$expandedOutput = [Environment]::ExpandEnvironmentVariables(
    $OutputDirectory.Trim())
$resolvedOutput = if ([IO.Path]::IsPathRooted($expandedOutput)) {
    [IO.Path]::GetFullPath($expandedOutput)
}
else {
    [IO.Path]::GetFullPath(
        (Join-Path $repositoryRoot $expandedOutput))
}
$artifactsPrefix =
    $artifactsRoot.TrimEnd(
        [IO.Path]::DirectorySeparatorChar,
        [IO.Path]::AltDirectorySeparatorChar) +
    [IO.Path]::DirectorySeparatorChar
if (-not $resolvedOutput.StartsWith(
        $artifactsPrefix,
        [StringComparison]::OrdinalIgnoreCase)) {
    throw (
        "OutputDirectory must resolve below the repository artifacts " +
        "directory: $artifactsRoot")
}

$testProject = Join-Path $repositoryRoot `
    "tests\ReAnimated.Tests\ReAnimated.Tests.csproj"
$previousOutput = [Environment]::GetEnvironmentVariable(
    "DLR_RENDERER_GOLDEN_OUTPUT",
    [EnvironmentVariableTarget]::Process)
$expectedStages = @(
    "retarget",
    "root_motion",
    "bone_edit",
    "hand_ik",
    "authored_morph",
    "fed_expression",
    "fpp_projection",
    "helper_overlay",
    "attachment"
)
$outputParent = [IO.Path]::GetFullPath(
    (Split-Path -Parent $resolvedOutput))
$outputLeaf = [IO.Path]::GetFileName(
    $resolvedOutput.TrimEnd(
        [IO.Path]::DirectorySeparatorChar,
        [IO.Path]::AltDirectorySeparatorChar))
if ([string]::IsNullOrWhiteSpace($outputLeaf)) {
    throw "OutputDirectory must name a directory below the artifacts root."
}
$stageOutput = [IO.Path]::GetFullPath(
    (Join-Path $outputParent (
        ".{0}.stage-{1}" -f
            $outputLeaf,
            [Guid]::NewGuid().ToString("N"))))
$backupOutput = [IO.Path]::GetFullPath(
    (Join-Path $outputParent (
        ".{0}.backup-{1}" -f
            $outputLeaf,
            [Guid]::NewGuid().ToString("N"))))
foreach ($privatePath in @($stageOutput, $backupOutput)) {
    if (-not $privatePath.StartsWith(
            $artifactsPrefix,
            [StringComparison]::OrdinalIgnoreCase)) {
        throw "A renderer-golden private path escaped the artifacts root."
    }
}
$published = $false
$previousOutputMoved = $false

[IO.Directory]::CreateDirectory($outputParent) | Out-Null
if ([IO.Directory]::Exists($stageOutput) -or
    [IO.Directory]::Exists($backupOutput)) {
    throw "A supposedly unique renderer-golden staging path already exists."
}
[IO.Directory]::CreateDirectory($stageOutput) | Out-Null
Push-Location $repositoryRoot
try {
    $env:DLR_RENDERER_GOLDEN_OUTPUT = $stageOutput
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
        "Gate=RendererAuthoringGoldens",
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
            "The renderer authoring-stage golden gate failed with exit " +
            "code $LASTEXITCODE.")
    }

    $manifestPath = Join-Path $stageOutput "manifest.json"
    if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf)) {
        throw (
            "The capture test passed without producing its required " +
            "manifest: $manifestPath")
    }

    $manifestFile = Get-Item -LiteralPath $manifestPath
    if ($manifestFile.Length -le 0 -or
        $manifestFile.Length -gt 131072) {
        throw (
            "The capture manifest size is outside the bounded 1-131072 " +
            "byte range: $($manifestFile.Length)")
    }

    $manifest = Get-Content -LiteralPath $manifestPath -Raw |
        ConvertFrom-Json
    if ($manifest.format -ne
        "dl-reanimated-renderer-authoring-stage-captures-v1") {
        throw "The capture manifest format marker is invalid."
    }

    if ($manifest.goldenFormat -ne
        "dl-reanimated-renderer-authoring-stage-goldens-v1") {
        throw "The capture manifest golden-format marker is invalid."
    }

    if ($manifest.width -ne 192 -or $manifest.height -ne 144) {
        throw (
            "The capture manifest dimensions changed from the required " +
            "192x144 matrix.")
    }

    $actualStages = @($manifest.stages)
    if ($actualStages.Count -ne $expectedStages.Count) {
        throw (
            "The capture manifest contains $($actualStages.Count) stages; " +
            "expected $($expectedStages.Count).")
    }

    $actualNames = @($actualStages | ForEach-Object { $_.name })
    if ([string]::Join("|", $actualNames) -ne
        [string]::Join("|", $expectedStages)) {
        throw (
            "The capture manifest stage order is invalid: " +
            [string]::Join(", ", $actualNames))
    }

    $expectedBitmapNames = @(
        $expectedStages |
            ForEach-Object { "$_.bmp" } |
            Sort-Object)
    $actualBitmapNames = @(
        Get-ChildItem -LiteralPath $stageOutput -File -Filter "*.bmp" |
            Select-Object -ExpandProperty Name |
            Sort-Object)
    if ([string]::Join("|", $actualBitmapNames) -ne
        [string]::Join("|", $expectedBitmapNames)) {
        throw (
            "The capture directory contains a stale, missing, or unexpected " +
            "BMP set: " +
            [string]::Join(", ", $actualBitmapNames))
    }
    $expectedEntryNames = @(
        @("manifest.json") +
        $expectedBitmapNames |
            Sort-Object)
    $stageEntries = @(
        Get-ChildItem -LiteralPath $stageOutput -Force)
    if ($stageEntries |
        Where-Object {
            $_.PSIsContainer -or
            (($_.Attributes -band
                    [IO.FileAttributes]::ReparsePoint) -ne 0)
        }) {
        throw (
            "The renderer capture stage contains a directory or reparse " +
            "point.")
    }
    $actualEntryNames = @(
        $stageEntries |
            Select-Object -ExpandProperty Name |
            Sort-Object)
    if ([string]::Join("|", $actualEntryNames) -ne
        [string]::Join("|", $expectedEntryNames)) {
        throw (
            "The renderer capture stage contains an unexpected file set: " +
            [string]::Join(", ", $actualEntryNames))
    }

    foreach ($stage in $actualStages) {
        $expectedFileName = "$($stage.name).bmp"
        if ($stage.file -ne $expectedFileName -or
            [IO.Path]::GetFileName($stage.file) -ne $stage.file) {
            throw (
                "Stage '$($stage.name)' has an unsafe or unexpected " +
                "capture filename '$($stage.file)'.")
        }

        if ($stage.pixelSha256 -notmatch "^[0-9a-f]{64}$" -or
            $stage.coverageSha256 -notmatch "^[0-9a-f]{64}$" -or
            $stage.bmpSha256 -notmatch "^[0-9a-f]{64}$") {
            throw (
                "Stage '$($stage.name)' has an invalid SHA-256 field.")
        }

        if ($stage.pixelCount -le 0 -or
            $stage.left -lt 0 -or
            $stage.top -lt 0 -or
            $stage.right -ge $manifest.width -or
            $stage.bottom -ge $manifest.height -or
            $stage.right -lt $stage.left -or
            $stage.bottom -lt $stage.top) {
            throw (
                "Stage '$($stage.name)' has invalid capture bounds.")
        }

        $capturePath = Join-Path $stageOutput $stage.file
        if (-not (Test-Path -LiteralPath $capturePath -PathType Leaf)) {
            throw (
                "Stage '$($stage.name)' did not produce '$capturePath'.")
        }

        $captureFile = Get-Item -LiteralPath $capturePath
        $expectedBitmapBytes =
            14 + 40 + ($manifest.width * $manifest.height * 4)
        if ($captureFile.Length -ne $expectedBitmapBytes) {
            throw (
                "Stage '$($stage.name)' bitmap length " +
                "$($captureFile.Length) differs from " +
                "$expectedBitmapBytes.")
        }

        $actualHash = (
            Get-FileHash -LiteralPath $capturePath -Algorithm SHA256
        ).Hash.ToLowerInvariant()
        if ($actualHash -ne $stage.bmpSha256) {
            throw (
                "Stage '$($stage.name)' bitmap hash differs from its " +
                "atomic manifest entry.")
        }
    }

    if (Test-Path -LiteralPath $resolvedOutput -PathType Leaf) {
        throw (
            "The renderer-golden output path is an existing file, not a " +
            "replaceable capture directory: $resolvedOutput")
    }
    if ([IO.Directory]::Exists($resolvedOutput)) {
        $existingDirectory = Get-Item -LiteralPath $resolvedOutput -Force
        if (($existingDirectory.Attributes -band
                [IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw (
                "Refusing to replace a renderer-golden output reparse " +
                "point: $resolvedOutput")
        }

        $existingEntries = @(
            Get-ChildItem -LiteralPath $resolvedOutput -Force)
        $unsafeExistingEntries = @(
            $existingEntries |
                Where-Object {
                    $_.PSIsContainer -or
                    (($_.Attributes -band
                            [IO.FileAttributes]::ReparsePoint) -ne 0) -or
                    $_.Name -notin $expectedEntryNames
                })
        if ($unsafeExistingEntries.Count -ne 0) {
            throw (
                "Refusing to replace a renderer-golden directory with " +
                "unexpected entries: " +
                [string]::Join(
                    ", ",
                    @($unsafeExistingEntries |
                        Select-Object -ExpandProperty Name)))
        }

        [IO.Directory]::Move(
            $resolvedOutput,
            $backupOutput)
        $previousOutputMoved = $true
    }

    try {
        [IO.Directory]::Move(
            $stageOutput,
            $resolvedOutput)
        $published = $true
    }
    catch {
        if ($previousOutputMoved -and
            -not [IO.Directory]::Exists($resolvedOutput) -and
            [IO.Directory]::Exists($backupOutput)) {
            [IO.Directory]::Move(
                $backupOutput,
                $resolvedOutput)
            $previousOutputMoved = $false
        }
        throw
    }

    if ($previousOutputMoved -and
        [IO.Directory]::Exists($backupOutput)) {
        try {
            foreach ($entry in Get-ChildItem `
                         -LiteralPath $backupOutput `
                         -Force) {
                if ($entry.PSIsContainer -or
                    (($entry.Attributes -band
                            [IO.FileAttributes]::ReparsePoint) -ne 0)) {
                    throw (
                        "The renderer-golden backup changed during " +
                        "cleanup: $($entry.FullName)")
                }
                [IO.File]::Delete($entry.FullName)
            }
            [IO.Directory]::Delete($backupOutput, $false)
            $previousOutputMoved = $false
        }
        catch {
            Write-Warning (
                "The previous renderer-golden capture backup was " +
                "preserved at ${backupOutput}: $($_.Exception.Message)")
        }
    }

    $manifestPath = Join-Path $resolvedOutput "manifest.json"
    Write-Host (
        "Renderer authoring-stage golden gate passed; inspectable captures: " +
        $manifestPath)
}
finally {
    if ($null -eq $previousOutput) {
        Remove-Item Env:DLR_RENDERER_GOLDEN_OUTPUT `
            -ErrorAction SilentlyContinue
    }
    else {
        $env:DLR_RENDERER_GOLDEN_OUTPUT = $previousOutput
    }

    if (-not $published -and
        [IO.Directory]::Exists($stageOutput)) {
        $stageEntries = @(
            Get-ChildItem -LiteralPath $stageOutput -Force)
        $unsafeStageEntries = @(
            $stageEntries |
                Where-Object {
                    $_.PSIsContainer -or
                    (($_.Attributes -band
                            [IO.FileAttributes]::ReparsePoint) -ne 0) -or
                    $_.Name -notin $expectedEntryNames
                })
        if ($unsafeStageEntries.Count -eq 0) {
            foreach ($entry in $stageEntries) {
                [IO.File]::Delete($entry.FullName)
            }
            [IO.Directory]::Delete($stageOutput, $false)
        }
        else {
            Write-Warning (
                "The failed renderer-golden stage contains unexpected " +
                "entries and was preserved at $stageOutput.")
        }
    }
    if ($previousOutputMoved -and
        [IO.Directory]::Exists($backupOutput)) {
        Write-Warning (
            "The previous renderer-golden output backup was preserved at " +
            $backupOutput)
    }

    Pop-Location
}
