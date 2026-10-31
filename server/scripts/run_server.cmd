@echo off
rem Shared by start_experiment.bat / start_demo.bat (not meant to be run directly).
rem Runs run.py from server\.venv; run.py itself starts and stops the CV worker in WSL2
rem (vptserver/cv_launcher.py). VPT_OUT = data folder, VPT_TITLE = window title.
rem Keep this file ASCII with CRLF line endings, and do not expand paths inside ( ) blocks.
setlocal EnableExtensions
cd /d "%~dp0.."
if "%VPT_OUT%"=="" set "VPT_OUT=received"
if not "%VPT_TITLE%"=="" title %VPT_TITLE%
if exist ".venv\Scripts\python.exe" goto :run
echo.
echo The Python environment is not set up yet (server\.venv).
echo Double-click setup_windows.bat in the server folder first.
echo.
pause
exit /b 1

:run
set PYTHONUTF8=1
".venv\Scripts\python.exe" run.py --out "%VPT_OUT%" %*
set "RC=%ERRORLEVEL%"
if "%RC%"=="0" exit /b 0
echo.
echo run.py stopped with an error (exit code %RC%). Read the messages above.
echo If it says "No Python at ...", run setup_windows.bat again.
echo.
pause
exit /b %RC%
