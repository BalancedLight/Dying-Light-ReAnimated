$ErrorActionPreference = "Stop"
Set-Location $PSScriptRoot

if ($args.Count -gt 0 -and $args[0] -eq "--setup") {
    & "$PSScriptRoot/setup_gui.bat"
    exit $LASTEXITCODE
}

$python = ".venv/Scripts/python.exe"
$needsSetup = -not (Test-Path $python) -or -not (Test-Path ".venv/.dl_reanimated_ready")
if (-not $needsSetup) {
    & $python -m dlanm2_gui.environment_check --gui *> $null
    $needsSetup = $LASTEXITCODE -ne 0
}
if ($needsSetup) {
    & "$PSScriptRoot/setup_gui.bat"
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}
& $python -m dlanm2_gui
exit $LASTEXITCODE
