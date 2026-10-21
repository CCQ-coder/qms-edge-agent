@echo off
setlocal
set SCRIPT_DIR=%~dp0
set CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe
if not exist "%CSC%" set CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe
if not exist "%CSC%" exit /b 1
set TEST_EXE=%TEMP%\BtfMonitorTests-%RANDOM%.exe
"%CSC%" /nologo /target:exe /platform:anycpu /out:"%TEST_EXE%" /reference:System.dll /reference:System.Core.dll /reference:System.Web.Extensions.dll /reference:System.Windows.Forms.dll /reference:System.Drawing.dll "%SCRIPT_DIR%BtfTests.cs" "%SCRIPT_DIR%BtfProtocol.cs" "%SCRIPT_DIR%BtfQueue.cs" "%SCRIPT_DIR%BtfMqtt.cs" "%SCRIPT_DIR%BtfSettings.cs" "%SCRIPT_DIR%BtfDelivery.cs" "%SCRIPT_DIR%BtfServer.cs" "%SCRIPT_DIR%BtfForm.cs"
if errorlevel 1 exit /b %errorlevel%
"%TEST_EXE%"
set RESULT=%ERRORLEVEL%
del /q "%TEST_EXE%" 2>nul
exit /b %RESULT%
