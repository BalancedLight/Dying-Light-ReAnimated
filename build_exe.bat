@echo off
setlocal EnableExtensions
cd /d "%~dp0"

echo ============================================================
echo  DL ReAnimated - Windows EXE build
echo ============================================================

set "PY_CMD="
where py >nul 2>nul
if not errorlevel 1 set "PY_CMD=py -3"
if not defined PY_CMD (
    where python >nul 2>nul
    if not errorlevel 1 set "PY_CMD=python"
)
if not defined PY_CMD (
    echo Python 3.11 or newer was not found.
    pause
    exit /b 1
)

%PY_CMD% -c "import sys; raise SystemExit(0 if sys.version_info >= (3,11) else 1)"
if errorlevel 1 (
    echo DL ReAnimated requires Python 3.11 or newer.
    pause
    exit /b 1
)

if not exist ".venv-build\Scripts\python.exe" (
    echo Creating .venv-build ...
    %PY_CMD% -m venv .venv-build
    if errorlevel 1 goto :failed
)

call ".venv-build\Scripts\activate.bat"
python -m pip install --upgrade pip setuptools wheel
if errorlevel 1 goto :failed
python -m pip install -e ".[gui,build]"
if errorlevel 1 goto :failed

set "WITH_TESTS=0"
for %%A in (%*) do (
    if /I "%%~A"=="--with-tests" set "WITH_TESTS=1"
)
if "%WITH_TESTS%"=="1" (
    echo Installing optional test dependency ...
    python -m pip install "pytest>=8"
    if errorlevel 1 goto :failed
)

python tools\build_windows_exe.py %*
if errorlevel 1 goto :failed

echo.
echo Build complete.
echo Portable EXE folder: dist\DL-ReAnimated
echo Release ZIP: dist\DL-ReAnimated-Windows-x64.zip
pause
exit /b 0

:failed
echo.
echo EXE build failed. Review the error above.
pause
exit /b 1
