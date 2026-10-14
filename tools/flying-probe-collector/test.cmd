@echo off
setlocal
set SCRIPT_DIR=%~dp0
set TEST_DIR=%TEMP%\flying-probe-tests-%RANDOM%%RANDOM%
set CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe
if not exist "%CSC%" set CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe
if not exist "%CSC%" exit /b 1
set FPT_TEST_ROOT=%SCRIPT_DIR%..\..\outputs\flying-probe-testdata
mkdir "%TEST_DIR%" || exit /b 1
"%CSC%" /nologo /target:exe /main:CollectorTests /out:"%TEST_DIR%\CollectorTests.exe" /reference:System.dll /reference:System.Core.dll /reference:System.Web.Extensions.dll "%SCRIPT_DIR%CollectorTests.cs" "%SCRIPT_DIR%ProbeRecords.cs" "%SCRIPT_DIR%LocalStores.cs" "%SCRIPT_DIR%ProbeScanner.cs"
if errorlevel 1 goto :failure
"%TEST_DIR%\CollectorTests.exe"
if errorlevel 1 goto :failure
rmdir /S /Q "%TEST_DIR%"
exit /b 0
:failure
rmdir /S /Q "%TEST_DIR%"
exit /b 1
