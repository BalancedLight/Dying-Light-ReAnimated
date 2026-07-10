$ErrorActionPreference = "Stop"
Set-Location $PSScriptRoot

if (Get-Command py -ErrorAction SilentlyContinue) {
    $launcher = "py"
    $launcherArgs = @("-3")
} elseif (Get-Command python -ErrorAction SilentlyContinue) {
    $launcher = "python"
    $launcherArgs = @()
} else {
    throw "Python 3.11+ was not found."
}

if (-not (Test-Path ".venv-build/Scripts/python.exe")) {
    & $launcher @launcherArgs -m venv .venv-build
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}

& ".venv-build/Scripts/python.exe" -m pip install --upgrade pip setuptools wheel
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

& ".venv-build/Scripts/python.exe" -m pip install -e ".[gui,build]"
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

if ($args -contains "--with-tests") {
    & ".venv-build/Scripts/python.exe" -m pip install "pytest>=8"
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}

& ".venv-build/Scripts/python.exe" tools/build_windows_exe.py @args
exit $LASTEXITCODE
