@echo off
setlocal
set SCRIPT_DIR=%~dp0
set REPO_DIR=%SCRIPT_DIR%..
set CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe
if not exist "%CSC%" set CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe
if not exist "%CSC%" exit /b 1
set TEST_EXE=%TEMP%\S1MqttTests-%RANDOM%.exe
"%CSC%" /nologo /target:exe /platform:anycpu /out:"%TEST_EXE%" /main:JingdeEapForwarder.S1MqttTests /reference:System.dll /reference:System.Core.dll /reference:System.Web.Extensions.dll /reference:System.Windows.Forms.dll /reference:System.Drawing.dll "%REPO_DIR%\src\JingdeEapForwarder.cs" "%REPO_DIR%\src\S1Mqtt.cs" "%REPO_DIR%\tests\S1MqttTests.cs"
if errorlevel 1 exit /b %errorlevel%
"%TEST_EXE%"
set RESULT=%ERRORLEVEL%
del /q "%TEST_EXE%" >nul 2>&1
exit /b %RESULT%
