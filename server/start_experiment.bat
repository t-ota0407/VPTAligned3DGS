@echo off
rem VPTAligned3DGS - PC server for the EVALUATION EXPERIMENT (double-click).
rem Starts run.py (receive / GUI) and the CV worker in WSL2 together; closing the
rem window stops both. Data is saved under server\received.
rem Extra arguments go to run.py, e.g.:  start_experiment.bat --scene scene_data/studio
rem Keep this file ASCII with CRLF line endings (.gitattributes).
setlocal
set "VPT_OUT=received"
set "VPT_TITLE=VPTAligned3DGS server - experiment"
call "%~dp0scripts\run_server.cmd" %*
