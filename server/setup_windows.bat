@echo off
rem ======================================================================
rem  VPTAligned3DGS - set up this PC (double-click). Safe to run again.
rem    1. Python environment for run.py (server\.venv + requirements.txt)
rem    2. WSL2 + CV environment (Ubuntu-24.04, ~/vptcv, torch, hloc)
rem    3. Firewall (UDP 50000) and the scene databases
rem  Arguments go to "python -m vptserver.setup_windows" (see --help).
rem
rem  Keep this file ASCII with CRLF line endings (.gitattributes), and do not
rem  expand paths inside ( ) blocks: a folder name such as "VPT (1)" breaks them.
rem ======================================================================
setlocal EnableExtensions
cd /d "%~dp0"
title VPTAligned3DGS setup
echo VPTAligned3DGS PC setup
echo.

rem ---- Python 3.9+ (py launcher first; the Microsoft Store "python" stub fails the check) ----
call :find_python
if defined PY goto :have_python
echo Python 3.9 or newer was not found on this PC.
where winget >nul 2>nul
if errorlevel 1 goto :no_python
choice /c YN /m "Install Python 3.12 now with winget (for this user only)"
if errorlevel 2 goto :no_python
winget install -e --id Python.Python.3.12 --scope user --accept-package-agreements --accept-source-agreements
call :find_python
if not defined PY goto :no_python

:have_python
echo Using Python: %PY%

rem ---- server\.venv (recreated when broken, e.g. after that Python was uninstalled) ----
if not exist ".venv\Scripts\python.exe" goto :make_venv
".venv\Scripts\python.exe" -c "import sys" >nul 2>nul
if not errorlevel 1 goto :venv_ok
echo The existing .venv does not work. Recreating it...
rmdir /s /q ".venv"
:make_venv
echo Creating the Python environment (.venv) ...
%PY% -m venv .venv
if errorlevel 1 goto :fail
:venv_ok

echo.
echo Installing Python packages for run.py ...
".venv\Scripts\python.exe" -m pip install --disable-pip-version-check -q --upgrade pip
".venv\Scripts\python.exe" -m pip install --disable-pip-version-check -r requirements.txt
if errorlevel 1 goto :fail

rem ---- WSL2 / CV / firewall / scenes ----
set PYTHONUTF8=1
".venv\Scripts\python.exe" -m vptserver.setup_windows %*
set "RC=%ERRORLEVEL%"
echo.
pause
exit /b %RC%

:no_python
echo.
echo Please install Python 3.12 from https://www.python.org/downloads/windows/
echo (tick "Add python.exe to PATH" in the installer), then run setup_windows.bat again.
echo.
pause
exit /b 1

:fail
echo.
echo Setup stopped because of the error above. Fix it and run setup_windows.bat again.
echo.
pause
exit /b 1

:find_python
set "PY="
py -3 -c "import sys; sys.exit(0 if sys.version_info >= (3, 9) else 1)" >nul 2>nul && set "PY=py -3" && exit /b 0
python -c "import sys; sys.exit(0 if sys.version_info >= (3, 9) else 1)" >nul 2>nul && set "PY=python" && exit /b 0
if exist "%LOCALAPPDATA%\Programs\Python\Python312\python.exe" set PY="%LOCALAPPDATA%\Programs\Python\Python312\python.exe"
exit /b 0
