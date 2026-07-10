@echo off
setlocal EnableExtensions
cd /d "%~dp0"

echo ============================================================
echo  DL ReAnimated - first-time GUI setup
echo ============================================================

set "PY_CMD="
where py >nul 2>nul
if not errorlevel 1 set "PY_CMD=py -3"
if not defined PY_CMD (
    where python >nul 2>nul
    if not errorlevel 1 set "PY_CMD=python"
)
if not defined PY_CMD (
    echo.
    echo Python 3.11 or newer was not found.
    echo Install Python from python.org and enable "Add Python to PATH".
    pause
    exit /b 1
)

%PY_CMD% -c "import sys; raise SystemExit(0 if sys.version_info >= (3,11) else 1)"
if errorlevel 1 (
    echo.
    echo DL ReAnimated requires Python 3.11 or newer.
    pause
    exit /b 1
)

if not exist ".venv\Scripts\python.exe" (
    echo Creating .venv ...
    %PY_CMD% -m venv .venv
    if errorlevel 1 goto :failed
)

call ".venv\Scripts\activate.bat"
echo Updating pip and installing GUI dependencies ...
python -m pip install --upgrade pip setuptools wheel
if errorlevel 1 goto :failed
python -m pip install -e ".[gui]"
if errorlevel 1 goto :failed

echo Verifying NumPy, PySide6, bundled references, and build pipeline imports ...
python -m dlanm2_gui.environment_check --gui --pipeline
if errorlevel 1 goto :failed

> ".venv\.dl_reanimated_ready" echo %date% %time%
echo.
echo Setup complete. Run run_gui.bat to start DL ReAnimated.
exit /b 0

:failed
if exist ".venv\.dl_reanimated_ready" del /q ".venv\.dl_reanimated_ready" >nul 2>nul
echo.
echo Setup failed. Review the error above.
pause
exit /b 1
