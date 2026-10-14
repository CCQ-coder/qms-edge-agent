@echo off
cd /d "%~dp0"
if /I "%~1"=="--headless" (
  start "Jingde EAP QMS Forwarder" /min "%~dp0JingdeEapForwarder.exe" --headless
) else (
  start "Jingde EAP QMS Forwarder" "%~dp0JingdeEapForwarder.exe"
)
