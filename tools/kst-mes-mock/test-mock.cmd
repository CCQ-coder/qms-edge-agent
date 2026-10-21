@echo off
setlocal
set SCRIPT_DIR=%~dp0
set TEST_DIR=%TEMP%\kst-server-test-%RANDOM%%RANDOM%
set RESULT=1
set CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe
if not exist "%CSC%" set CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe
if not exist "%CSC%" exit /b 1
mkdir "%TEST_DIR%" || exit /b 1
pwsh -NoProfile -File "%SCRIPT_DIR%new-test-certificate.ps1" -OutputPath "%TEST_DIR%\kst_server.pfx"
if errorlevel 1 goto :cleanup
"%CSC%" /nologo /target:exe /main:ServerTests /out:"%TEST_DIR%\ServerTests.exe" /reference:System.dll /reference:System.Core.dll /reference:System.Web.Extensions.dll "%SCRIPT_DIR%ServerTests.cs" "%SCRIPT_DIR%KstMesServer.cs"
if errorlevel 1 goto :cleanup
"%TEST_DIR%\ServerTests.exe" "%TEST_DIR%\kst_server.pfx"
if errorlevel 1 goto :cleanup
"%CSC%" /nologo /target:exe /main:ListenAddressTests /out:"%TEST_DIR%\ListenAddressTests.exe" /reference:System.dll "%SCRIPT_DIR%ListenAddressTests.cs" "%SCRIPT_DIR%KstListenAddress.cs"
if errorlevel 1 goto :cleanup
"%TEST_DIR%\ListenAddressTests.exe"
if errorlevel 1 goto :cleanup
"%CSC%" /nologo /target:exe /main:KstMqttTests /out:"%TEST_DIR%\KstMqttTests.exe" /reference:System.dll /reference:System.Core.dll /reference:System.Web.Extensions.dll /reference:System.Windows.Forms.dll /reference:System.Drawing.dll "%SCRIPT_DIR%KstMqttTests.cs" "%SCRIPT_DIR%KstMqtt.cs" "%SCRIPT_DIR%KstMesServer.cs" "%SCRIPT_DIR%KstSourceRoutes.cs"
if errorlevel 1 goto :cleanup
"%TEST_DIR%\KstMqttTests.exe"
set RESULT=%ERRORLEVEL%

:cleanup
rmdir /S /Q "%TEST_DIR%"
exit /b %RESULT%
