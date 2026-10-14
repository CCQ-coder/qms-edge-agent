@echo off
setlocal
set TASK_NAME=QMS-Jingde-Eap-Forwarder
set PROGRAM="%~dp0JingdeEapForwarder.exe" --headless

echo This command must be run from an Administrator command prompt.
schtasks /create /tn "%TASK_NAME%" /tr "%PROGRAM%" /sc onstart /ru SYSTEM /rl HIGHEST /f
if errorlevel 1 (
  echo Failed to create the startup task.
  exit /b %errorlevel%
)
echo Startup task created: %TASK_NAME%
echo To remove it later: schtasks /delete /tn "%TASK_NAME%" /f
