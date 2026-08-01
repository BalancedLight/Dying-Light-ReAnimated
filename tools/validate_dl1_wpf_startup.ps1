[CmdletBinding()]
param(
    [string]$ExecutablePath = "",
    [ValidateRange(5, 120)]
    [int]$TimeoutSeconds = 30
)

$ErrorActionPreference = "Stop"

function Test-ExpectedViewportDiagnostics {
    param(
        [Parameter(Mandatory = $true)]
        [object]$Viewport
    )

    $viewportDiagnostics =
        @($Viewport.diagnostics)
    return (
        $viewportDiagnostics.Count -eq 0 -or
        (
            [string]$Viewport.adapterMode -eq "Warp" -and
            $viewportDiagnostics.Count -eq 1 -and
            [string]$viewportDiagnostics[0] -eq
                "Hardware D3D11 initialization failed; using the WARP software adapter."
        ))
}

function Test-ViewportSizeEvidence {
    param(
        [Parameter(Mandatory = $true)]
        [object]$Viewport
    )

    $calculatedPixelWidth =
        [Math]::Max(
            1,
            [int][Math]::Ceiling(
                [double]$Viewport.actualWidth *
                [double]$Viewport.dpiScaleX))
    $calculatedPixelHeight =
        [Math]::Max(
            1,
            [int][Math]::Ceiling(
                [double]$Viewport.actualHeight *
                [double]$Viewport.dpiScaleY))
    return (
        [double]$Viewport.actualWidth -gt 0 -and
        [double]$Viewport.actualHeight -gt 0 -and
        [double]$Viewport.dpiScaleX -gt 0 -and
        [double]$Viewport.dpiScaleY -gt 0 -and
        [int]$Viewport.expectedPixelWidth -gt 0 -and
        [int]$Viewport.expectedPixelHeight -gt 0 -and
        [int]$Viewport.expectedPixelWidth -eq
            $calculatedPixelWidth -and
        [int]$Viewport.expectedPixelHeight -eq
            $calculatedPixelHeight -and
        [int]$Viewport.rendererPixelWidth -eq
            [int]$Viewport.expectedPixelWidth -and
        [int]$Viewport.rendererPixelHeight -eq
            [int]$Viewport.expectedPixelHeight)
}

$repositoryRoot =
    [System.IO.Path]::GetFullPath(
        (Split-Path -Parent $PSScriptRoot))
if ([string]::IsNullOrWhiteSpace($ExecutablePath)) {
    $ExecutablePath =
        Join-Path `
            $repositoryRoot `
            "artifacts\csharp\win-x64\DLReAnimated.exe"
}
$resolvedExecutable =
    [System.IO.Path]::GetFullPath($ExecutablePath)
if (-not (Test-Path -LiteralPath $resolvedExecutable -PathType Leaf)) {
    throw "The WPF startup-smoke executable does not exist: $resolvedExecutable"
}
if ([System.IO.Path]::GetExtension($resolvedExecutable) -ne ".exe") {
    throw "The WPF startup-smoke target must be a Windows executable."
}
if ($resolvedExecutable.Contains('"')) {
    throw "The WPF startup-smoke executable path cannot contain a quote."
}

$validationRoot =
    [System.IO.Path]::GetFullPath(
        (Join-Path $repositoryRoot "artifacts\validation"))
New-Item `
    -ItemType Directory `
    -Path $validationRoot `
    -Force | Out-Null
$stageName =
    ".wpf-startup-stage-{0}" -f
        [System.Guid]::NewGuid().ToString("N")
$stageRoot =
    [System.IO.Path]::GetFullPath(
        (Join-Path $validationRoot $stageName))
$requiredPrefix =
    $validationRoot.TrimEnd(
        [System.IO.Path]::DirectorySeparatorChar,
        [System.IO.Path]::AltDirectorySeparatorChar) +
    [System.IO.Path]::DirectorySeparatorChar
if (-not $stageRoot.StartsWith(
        $requiredPrefix,
        [System.StringComparison]::OrdinalIgnoreCase) -or
    [System.IO.Path]::GetFileName($stageRoot) -ne $stageName) {
    throw "The managed WPF startup-smoke stage escaped the validation directory."
}

$smokeOutput = Join-Path $stageRoot "smoke-output"
$localData = Join-Path $stageRoot "LocalAppData"
$roamingData = Join-Path $stageRoot "RoamingAppData"
$temporaryData = Join-Path $stageRoot "Temp"
$bundleData = Join-Path $stageRoot "Bundle"
$workingDirectory = Join-Path $stageRoot "Working"
$finalReceipt =
    Join-Path `
        $validationRoot `
        "dl1-wpf-startup-smoke.json"
$temporaryFinalReceipt =
    Join-Path `
        $validationRoot `
        (".dl1-wpf-startup-smoke-{0}.tmp" -f
            [System.Guid]::NewGuid().ToString("N"))
$backupFinalReceipt =
    Join-Path `
        $validationRoot `
        (".dl1-wpf-startup-smoke-{0}.bak" -f
            [System.Guid]::NewGuid().ToString("N"))

New-Item -ItemType Directory -Path $stageRoot | Out-Null
foreach ($directory in @(
        $smokeOutput,
        $localData,
        $roamingData,
        $temporaryData,
        $bundleData,
        $workingDirectory)) {
    New-Item -ItemType Directory -Path $directory | Out-Null
}

$committed = $false
try {
    $processStart =
        New-Object System.Diagnostics.ProcessStartInfo
    $processStart.FileName = $resolvedExecutable
    $processStart.Arguments =
        '"--wpf-startup-smoke" "{0}" "{1}"' -f
            $smokeOutput,
            $TimeoutSeconds
    $processStart.WorkingDirectory = $workingDirectory
    $processStart.UseShellExecute = $false
    $processStart.CreateNoWindow = $true
    $processStart.EnvironmentVariables["LOCALAPPDATA"] =
        $localData
    $processStart.EnvironmentVariables["APPDATA"] =
        $roamingData
    $processStart.EnvironmentVariables["TEMP"] =
        $temporaryData
    $processStart.EnvironmentVariables["TMP"] =
        $temporaryData
    $processStart.EnvironmentVariables[
        "DOTNET_BUNDLE_EXTRACT_BASE_DIR"] =
        $bundleData
    $processStart.EnvironmentVariables["PATH"] =
        "{0}\System32;{0}" -f $env:SystemRoot
    foreach ($name in @(
            "DOTNET_ROOT",
            "DOTNET_ROOT_X64",
            "MSBuildSDKsPath",
            "NUGET_PACKAGES")) {
        $processStart.EnvironmentVariables.Remove($name)
    }

    $startedUtc =
        [DateTimeOffset]::UtcNow
    $process =
        New-Object System.Diagnostics.Process
    $process.StartInfo = $processStart
    try {
        if (-not $process.Start()) {
            throw "The packaged WPF startup-smoke process did not start."
        }

        $waitMilliseconds =
            [Math]::Min(
                [int]::MaxValue,
                ($TimeoutSeconds + 15) * 1000)
        if (-not $process.WaitForExit($waitMilliseconds)) {
            try {
                $process.Kill()
                $process.WaitForExit()
            }
            catch {
                Write-Warning `
                    "Could not terminate the timed-out WPF startup-smoke process: $($_.Exception.Message)"
            }
            throw (
                "The packaged WPF startup-smoke process exceeded " +
                "$($TimeoutSeconds + 15) seconds.")
        }

        $exitCode = $process.ExitCode
    }
    finally {
        $process.Dispose()
    }

    $completedUtc =
        [DateTimeOffset]::UtcNow
    $sourceReceipt =
        Join-Path `
            $smokeOutput `
            "DL_REANIMATED_WPF_STARTUP_SMOKE.json"
    if (-not (Test-Path -LiteralPath $sourceReceipt -PathType Leaf)) {
        throw (
            "The packaged WPF startup-smoke process wrote no receipt " +
            "and exited with code $exitCode.")
    }

    $utf8 =
        New-Object System.Text.UTF8Encoding($false, $true)
    $smoke =
        [System.IO.File]::ReadAllText(
            $sourceReceipt,
            $utf8) |
        ConvertFrom-Json
    if ($exitCode -ne 0 -or
        $smoke.format -ne "dl-reanimated-wpf-startup-smoke" -or
        [int]$smoke.schemaVersion -ne 3 -or
        -not [bool]$smoke.complete -or
        -not [bool]$smoke.animationLibraryRowMaterialized -or
        [string]$smoke.processArchitecture -ne "X64" -or
        [int]$smoke.requiredViewportCount -ne 2 -or
        [long]$smoke.requiredPresentedFrames -lt 3 -or
        [int]$smoke.requiredResizeStepCount -ne 6) {
        throw (
            "The packaged WPF startup-smoke receipt is incomplete or " +
            "invalid; process exit code was $exitCode.")
    }

    $viewports = @($smoke.viewports)
    if ($viewports.Count -ne 2) {
        throw "The packaged WPF startup smoke did not report exactly two viewports."
    }
    foreach ($viewport in $viewports) {
        if ([string]$viewport.state -ne "Ready" -or
            [string]$viewport.adapterMode -notin @(
                "Hardware",
                "Warp") -or
            [long]$viewport.presentedFrames -lt 3 -or
            -not (Test-ViewportSizeEvidence $viewport) -or
            -not (Test-ExpectedViewportDiagnostics $viewport)) {
            throw (
                "A packaged WPF viewport did not reach a clean Ready " +
                "state with presented D3D11 frames and matching pixel dimensions.")
        }
    }

    $resizeSteps = @($smoke.resizeSteps)
    if ($resizeSteps.Count -ne
        [int]$smoke.requiredResizeStepCount) {
        throw (
            "The packaged WPF startup smoke did not report the " +
            "required resize-step evidence.")
    }

    $lastPresentedFrames = @{}
    for ($stepIndex = 0;
         $stepIndex -lt $resizeSteps.Count;
         $stepIndex++) {
        $step = $resizeSteps[$stepIndex]
        if ([int]$step.stepIndex -ne $stepIndex -or
            [double]$step.requestedWindowWidth -le 0 -or
            [double]$step.requestedWindowHeight -le 0 -or
            [double]$step.actualWindowWidth -le 0 -or
            [double]$step.actualWindowHeight -le 0 -or
            [Math]::Abs(
                [double]$step.actualWindowWidth -
                [double]$step.requestedWindowWidth) -gt 0.01 -or
            [Math]::Abs(
                [double]$step.actualWindowHeight -
                [double]$step.requestedWindowHeight) -gt 0.01) {
            throw (
                "WPF resize step $stepIndex has invalid window-size " +
                "or ordering evidence.")
        }
        if (($stepIndex % 2) -eq 1) {
            $compactStep =
                $resizeSteps[$stepIndex - 1]
            if ([double]$step.requestedWindowWidth -le
                    [double]$compactStep.requestedWindowWidth -or
                [double]$step.requestedWindowHeight -le
                    [double]$compactStep.requestedWindowHeight) {
                throw (
                    "WPF resize step $stepIndex did not expand both " +
                    "dimensions after its compact step.")
            }
        }

        $stepViewports = @($step.viewports)
        if ($stepViewports.Count -ne 2) {
            throw (
                "WPF resize step $stepIndex did not report exactly " +
                "two hosted viewports.")
        }

        $reportedIndices =
            @($stepViewports |
                ForEach-Object {
                    [int]$_.index
                } |
                Sort-Object -Unique)
        if ($reportedIndices.Count -ne 2 -or
            $reportedIndices[0] -ne 0 -or
            $reportedIndices[1] -ne 1) {
            throw (
                "WPF resize step $stepIndex did not identify both " +
                "hosted viewports exactly once.")
        }

        foreach ($viewport in $stepViewports) {
            $index = [int]$viewport.index
            $baseline =
                [long]$viewport.baselinePresentedFrames
            $presented =
                [long]$viewport.presentedFrames
            if ([string]$viewport.state -ne "Ready" -or
                [string]$viewport.adapterMode -notin @(
                    "Hardware",
                    "Warp") -or
                $presented -le $baseline -or
                -not (Test-ViewportSizeEvidence $viewport) -or
                -not (Test-ExpectedViewportDiagnostics $viewport)) {
                throw (
                    "Hosted viewport $index did not remain Ready, " +
                    "advance a frame, and publish the matching swap-chain " +
                    "pixel dimensions at resize step $stepIndex.")
            }

            if ($lastPresentedFrames.ContainsKey($index) -and
                $baseline -lt
                    [long]$lastPresentedFrames[$index]) {
                throw (
                    "Hosted viewport $index frame evidence regressed " +
                    "between resize steps.")
            }
            $lastPresentedFrames[$index] = $presented
        }
    }

    foreach ($viewport in $viewports) {
        $index = [int]$viewport.index
        if (-not $lastPresentedFrames.ContainsKey($index) -or
            [long]$viewport.presentedFrames -lt
                [long]$lastPresentedFrames[$index]) {
            throw (
                "Final hosted viewport evidence does not include the " +
                "completed resize sequence.")
        }
    }

    $crashDirectory =
        Join-Path `
            $localData `
            "DLReAnimated\CrashReports"
    if (Test-Path -LiteralPath $crashDirectory -PathType Container) {
        $crashFiles =
            @(Get-ChildItem -LiteralPath $crashDirectory -File)
        if ($crashFiles.Count -ne 0) {
            throw "The isolated WPF startup smoke generated a crash report."
        }
    }

    $executableFile =
        Get-Item -LiteralPath $resolvedExecutable
    $sourceReceiptHash =
        (Get-FileHash `
            -LiteralPath $sourceReceipt `
            -Algorithm SHA256).Hash.ToLowerInvariant()
    $executableHash =
        (Get-FileHash `
            -LiteralPath $resolvedExecutable `
            -Algorithm SHA256).Hash.ToLowerInvariant()
    $acceptance =
        [ordered]@{
            format =
                "dl-reanimated-wpf-startup-acceptance"
            schemaVersion = 2
            complete = $true
            startedUtc =
                $startedUtc.ToString("o")
            completedUtc =
                $completedUtc.ToString("o")
            processExitCode = $exitCode
            isolatedProfile = $true
            developerSdkPathsRemoved = $true
            interactiveComputerControlUsed = $false
            executable =
                [ordered]@{
                    path = $resolvedExecutable
                    length = $executableFile.Length
                    sha256 = $executableHash
                }
            smokeReceiptSha256 = $sourceReceiptHash
            smoke = $smoke
        }
    $json =
        $acceptance |
        ConvertTo-Json -Depth 12
    [System.IO.File]::WriteAllText(
        $temporaryFinalReceipt,
        $json,
        $utf8)
    if (Test-Path -LiteralPath $finalReceipt -PathType Leaf) {
        [System.IO.File]::Replace(
            $temporaryFinalReceipt,
            $finalReceipt,
            $backupFinalReceipt)
    }
    else {
        [System.IO.File]::Move(
            $temporaryFinalReceipt,
            $finalReceipt)
    }

    $finalHash =
        (Get-FileHash `
            -LiteralPath $finalReceipt `
            -Algorithm SHA256).Hash.ToLowerInvariant()
    $committed = $true
    Write-Host "Packaged WPF/D3D11 startup acceptance passed."
    Write-Host "Receipt: $finalReceipt"
    Write-Host "Receipt SHA-256: $finalHash"
    Write-Host (
        "Adapters: " +
        (($viewports |
            ForEach-Object {
                "{0}:{1}:{2}frames" -f
                    $_.index,
                    $_.adapterMode,
                    $_.presentedFrames
            }) -join ", "))
    Write-Host (
        ("Hosted resize steps: {0}; per-step swap-chain pixels matched " +
         "DPI-scaled arranged host sizes.") -f
            $resizeSteps.Count)
}
finally {
    if (Test-Path -LiteralPath $temporaryFinalReceipt -PathType Leaf) {
        Remove-Item -LiteralPath $temporaryFinalReceipt -Force
    }
    if ($committed -and
        (Test-Path -LiteralPath $backupFinalReceipt -PathType Leaf)) {
        Remove-Item -LiteralPath $backupFinalReceipt -Force
    }
    elseif (Test-Path -LiteralPath $backupFinalReceipt -PathType Leaf) {
        Write-Warning `
            "The previous WPF startup receipt backup was preserved at $backupFinalReceipt"
    }
    if ($committed -and
        (Test-Path -LiteralPath $stageRoot -PathType Container)) {
        $resolvedCleanup =
            [System.IO.Path]::GetFullPath($stageRoot)
        if (-not $resolvedCleanup.StartsWith(
                $requiredPrefix,
                [System.StringComparison]::OrdinalIgnoreCase) -or
            [System.IO.Path]::GetFileName($resolvedCleanup) -ne
                $stageName) {
            throw "Refusing to clean an unexpected WPF startup-smoke path."
        }
        Remove-Item `
            -LiteralPath $resolvedCleanup `
            -Recurse `
            -Force
    }
    elseif (Test-Path -LiteralPath $stageRoot -PathType Container) {
        Write-Warning `
            "WPF startup-smoke diagnostics were preserved at $stageRoot"
    }
}
