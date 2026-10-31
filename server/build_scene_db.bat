@echo off
rem VPTAligned3DGS - build the localization DB for scenes in server\scene_data (double-click).
rem Run this after adding a scene. Without arguments: every scene that has no DB yet
rem (asks first). With arguments: only those scenes, e.g.  build_scene_db.bat studio
rem The DB is built in WSL2 on the GPU (several to tens of minutes per scene).
rem Keep this file ASCII with CRLF line endings (.gitattributes).
setlocal EnableExtensions
cd /d "%~dp0"
title VPTAligned3DGS - build scene DB
if exist ".venv\Scripts\python.exe" goto :run
echo The Python environment is not set up yet. Double-click setup_windows.bat first.
pause
exit /b 1

:run
set PYTHONUTF8=1
".venv\Scripts\python.exe" -m vptserver.setup_windows --build-db %*
set "RC=%ERRORLEVEL%"
echo.
pause
exit /b %RC%
