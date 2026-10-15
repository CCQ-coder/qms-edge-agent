@echo off
setlocal
cd /d "%~dp0"
"%~dp0FlyingProbeCollector.exe" %*
set "COLLECTOR_EXIT=%ERRORLEVEL%"
if not "%COLLECTOR_EXIT%"=="0" (
  echo.
  echo Collector failed. Check FlyingProbeCollector.ini next to this script.
  pause
)
exit /b %COLLECTOR_EXIT%
