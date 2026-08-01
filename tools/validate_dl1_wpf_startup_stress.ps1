[CmdletBinding()]
param(
    [string]$ExecutablePath = "",
    [ValidateRange(2, 20)]
    [int]$Iterations = 5,
    [ValidateRange(5, 120)]
    [int]$TimeoutSeconds = 30
)

$ErrorActionPreference = "Stop"
$repositoryRoot =
    [System.IO.Path]::GetFullPath(
        (Split-Path -Parent $PSScriptRoot))
$singleRunScript =
    Join-Path `
        $PSScriptRoot `
        "validate_dl1_wpf_startup.ps1"
if (-not (Test-Path -LiteralPath $singleRunScript -PathType Leaf)) {
    throw "The single-run WPF startup validator is missing."
}

$validationRoot =
    [System.IO.Path]::GetFullPath(
        (Join-Path $repositoryRoot "artifacts\validation"))
New-Item `
    -ItemType Directory `
    -Path $validationRoot `
    -Force | Out-Null
$singleReceipt =
    Join-Path `
        $validationRoot `
        "dl1-wpf-startup-smoke.json"
$finalReceipt =
    Join-Path `
        $validationRoot `
        "dl1-wpf-startup-stress.json"
$temporaryReceipt =
    Join-Path `
        $validationRoot `
        (".dl1-wpf-startup-stress-{0}.tmp" -f
            [System.Guid]::NewGuid().ToString("N"))
$backupReceipt =
    Join-Path `
        $validationRoot `
        (".dl1-wpf-startup-stress-{0}.bak" -f
            [System.Guid]::NewGuid().ToString("N"))
$utf8 =
    New-Object System.Text.UTF8Encoding($false, $true)
$runs =
    New-Object "System.Collections.Generic.List[object]"
$startedUtc =
    [DateTimeOffset]::UtcNow
try {
    for ($iteration = 1;
         $iteration -le $Iterations;
         $iteration++) {
        if ([string]::IsNullOrWhiteSpace($ExecutablePath)) {
            & $singleRunScript `
                -TimeoutSeconds $TimeoutSeconds
        }
        else {
            & $singleRunScript `
                -ExecutablePath $ExecutablePath `
                -TimeoutSeconds $TimeoutSeconds
        }
        if (-not (Test-Path -LiteralPath $singleReceipt -PathType Leaf)) {
            throw "WPF startup-smoke iteration $iteration wrote no receipt."
        }

        $receiptText =
            [System.IO.File]::ReadAllText(
                $singleReceipt,
                $utf8)
        $receipt =
            $receiptText |
            ConvertFrom-Json
        if ($receipt.format -ne
                "dl-reanimated-wpf-startup-acceptance" -or
            [int]$receipt.schemaVersion -ne 2 -or
            -not [bool]$receipt.complete -or
            [int]$receipt.processExitCode -ne 0 -or
            -not [bool]$receipt.isolatedProfile -or
            [bool]$receipt.interactiveComputerControlUsed) {
            throw (
                "WPF startup-smoke iteration $iteration produced an " +
                "invalid acceptance receipt.")
        }

        $viewports =
            @($receipt.smoke.viewports)
        if ($viewports.Count -ne 2) {
            throw (
                "WPF startup-smoke iteration $iteration did not report " +
                "two viewports.")
        }
        $resizeSteps =
            @($receipt.smoke.resizeSteps)
        if ([int]$receipt.smoke.requiredResizeStepCount -ne 6 -or
            $resizeSteps.Count -ne
                [int]$receipt.smoke.requiredResizeStepCount) {
            throw (
                "WPF startup-smoke iteration $iteration did not retain " +
                "the required hosted resize evidence.")
        }

        $runs.Add(
            [ordered]@{
                iteration = $iteration
                receiptSha256 =
                    (Get-FileHash `
                        -LiteralPath $singleReceipt `
                        -Algorithm SHA256).Hash.ToLowerInvariant()
                startedUtc =
                    [string]$receipt.startedUtc
                completedUtc =
                    [string]$receipt.completedUtc
                executableSha256 =
                    [string]$receipt.executable.sha256
                informationalVersion =
                    [string]$receipt.smoke.informationalVersion
                elapsedMilliseconds =
                    [double]$receipt.smoke.elapsedMilliseconds
                resizeStepCount =
                    $resizeSteps.Count
                viewports =
                    @($viewports |
                        ForEach-Object {
                            [ordered]@{
                                index = [int]$_.index
                                adapterMode =
                                    [string]$_.adapterMode
                                presentedFrames =
                                    [long]$_.presentedFrames
                                framesPerSecond =
                                    [double]$_.framesPerSecond
                                rendererPixelWidth =
                                    [int]$_.rendererPixelWidth
                                rendererPixelHeight =
                                    [int]$_.rendererPixelHeight
                            }
                        })
                resizeSteps =
                    @($resizeSteps |
                        ForEach-Object {
                            [ordered]@{
                                stepIndex =
                                    [int]$_.stepIndex
                                requestedWindowWidth =
                                    [double]$_.requestedWindowWidth
                                requestedWindowHeight =
                                    [double]$_.requestedWindowHeight
                                actualWindowWidth =
                                    [double]$_.actualWindowWidth
                                actualWindowHeight =
                                    [double]$_.actualWindowHeight
                                viewports =
                                    @($_.viewports |
                                        ForEach-Object {
                                            [ordered]@{
                                                index =
                                                    [int]$_.index
                                                adapterMode =
                                                    [string]$_.adapterMode
                                                baselinePresentedFrames =
                                                    [long]$_.baselinePresentedFrames
                                                presentedFrames =
                                                    [long]$_.presentedFrames
                                                actualWidth =
                                                    [double]$_.actualWidth
                                                actualHeight =
                                                    [double]$_.actualHeight
                                                dpiScaleX =
                                                    [double]$_.dpiScaleX
                                                dpiScaleY =
                                                    [double]$_.dpiScaleY
                                                expectedPixelWidth =
                                                    [int]$_.expectedPixelWidth
                                                expectedPixelHeight =
                                                    [int]$_.expectedPixelHeight
                                                rendererPixelWidth =
                                                    [int]$_.rendererPixelWidth
                                                rendererPixelHeight =
                                                    [int]$_.rendererPixelHeight
                                            }
                                        })
                            }
                        })
            })
    }

    $executableHashes =
        @($runs |
            ForEach-Object {
                [string]$_.executableSha256
            } |
            Select-Object -Unique)
    $informationalVersions =
        @($runs |
            ForEach-Object {
                [string]$_.informationalVersion
            } |
            Select-Object -Unique)
    if ($executableHashes.Count -ne 1 -or
        $executableHashes[0] -notmatch '^[0-9a-f]{64}$' -or
        $informationalVersions.Count -ne 1 -or
        [string]::IsNullOrWhiteSpace(
            $informationalVersions[0])) {
        throw (
            "Repeated WPF startup receipts did not remain bound to one " +
            "exact executable identity.")
    }

    $completedUtc =
        [DateTimeOffset]::UtcNow
    $result =
        [ordered]@{
            format =
                "dl-reanimated-wpf-startup-stress"
            schemaVersion = 2
            complete = $true
            startedUtc = $startedUtc.ToString("o")
            completedUtc = $completedUtc.ToString("o")
            iterationCount = $Iterations
            timeoutSeconds = $TimeoutSeconds
            executableSha256 = $executableHashes[0]
            informationalVersion =
                $informationalVersions[0]
            interactiveComputerControlUsed = $false
            runs = $runs.ToArray()
        }
    $json =
        $result |
        ConvertTo-Json -Depth 12
    [System.IO.File]::WriteAllText(
        $temporaryReceipt,
        $json,
        $utf8)
    if (Test-Path -LiteralPath $finalReceipt -PathType Leaf) {
        [System.IO.File]::Replace(
            $temporaryReceipt,
            $finalReceipt,
            $backupReceipt)
    }
    else {
        [System.IO.File]::Move(
            $temporaryReceipt,
            $finalReceipt)
    }

    $finalHash =
        (Get-FileHash `
            -LiteralPath $finalReceipt `
            -Algorithm SHA256).Hash.ToLowerInvariant()
    Write-Host (
        "Repeated packaged WPF/D3D11 startup acceptance passed: " +
        "$Iterations/$Iterations.")
    Write-Host "Receipt: $finalReceipt"
    Write-Host "Receipt SHA-256: $finalHash"
    if (Test-Path -LiteralPath $backupReceipt -PathType Leaf) {
        Remove-Item -LiteralPath $backupReceipt -Force
    }
}
finally {
    if (Test-Path -LiteralPath $temporaryReceipt -PathType Leaf) {
        Remove-Item -LiteralPath $temporaryReceipt -Force
    }
    if (Test-Path -LiteralPath $backupReceipt -PathType Leaf) {
        Write-Warning `
            "The previous WPF startup-stress receipt backup was preserved at $backupReceipt"
    }
}
