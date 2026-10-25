@echo off
setlocal
set "LOGDIR=%LOCALAPPDATA%\AzPilot\logs"
echo AzPilot diagnostic folder: "%LOGDIR%"
if not exist "%LOGDIR%" mkdir "%LOGDIR%"
echo Windows: & ver
echo Architecture: %PROCESSOR_ARCHITECTURE%
echo.
echo If a startup log exists, open the newest startup-YYYYMMDD.log file.
echo If no log exists, check Event Viewer and security-software blocks.
start "" explorer "%LOGDIR%"
pause
