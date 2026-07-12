@echo off
setlocal EnableExtensions
cd /d "%~dp0"

echo ============================================================
echo  DL ReAnimated 0.4.0a2 - repair and launch
 echo ============================================================

if exist ".venv\.dl_reanimated_ready" del /q ".venv\.dl_reanimated_ready" >nul 2>nul
call run_gui.bat --setup
if errorlevel 1 exit /b 1
call run_gui.bat
exit /b %errorlevel%
