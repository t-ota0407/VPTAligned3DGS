@echo off
rem VPTAligned3DGS - PC server for the DEMO EXHIBITION (double-click).
rem Starts run.py (receive / GUI) and the CV worker in WSL2 together; closing the
rem window stops both. Data is saved under server\received_demo (kept apart from
rem the experiment data in server\received).
rem Extra arguments go to run.py, e.g.:  start_demo.bat --scene scene_data/studio
rem Keep this file ASCII with CRLF line endings (.gitattributes).
setlocal
set "VPT_OUT=received_demo"
set "VPT_TITLE=VPTAligned3DGS server - demo"
call "%~dp0scripts\run_server.cmd" %*
