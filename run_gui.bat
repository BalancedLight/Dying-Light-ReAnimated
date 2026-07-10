@echo off
setlocal EnableExtensions
cd /d "%~dp0"

if /I "%~1"=="--setup" (
    call setup_gui.bat
    exit /b %errorlevel%
)

if not exist ".venv\Scripts\python.exe" goto :setup
if not exist ".venv\.dl_reanimated_ready" goto :setup

".venv\Scripts\python.exe" -m dlanm2_gui.environment_check --gui >nul 2>nul
if errorlevel 1 goto :setup

goto :launch

:setup
call setup_gui.bat
if errorlevel 1 exit /b 1

:launch
call ".venv\Scripts\activate.bat"
python -m dlanm2_gui
if errorlevel 1 (
    echo.
    echo DL ReAnimated exited with an error.
    echo Run "run_gui.bat --setup" to repair the environment.
    pause
    exit /b 1
)
