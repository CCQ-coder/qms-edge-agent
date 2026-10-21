@echo off
cd /d "%~dp0"
if "%~1"=="" (
  "%~dp0HosonMesMock.exe"
) else (
  "%~dp0HosonMesMock.exe" %*
  if errorlevel 1 pause
)
