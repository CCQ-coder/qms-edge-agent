@echo off
setlocal
set SCRIPT_DIR=%~dp0
set OUT_DIR=%SCRIPT_DIR%..\..\outputs\flying-probe-collector
if not exist "%OUT_DIR%" mkdir "%OUT_DIR%"

set CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe
if not exist "%CSC%" set CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe
if not exist "%CSC%" (
  echo .NET Framework 4.x compiler was not found.
  exit /b 1
)

"%CSC%" /nologo /target:exe /main:Program /platform:anycpu /optimize+ /out:"%OUT_DIR%\FlyingProbeCollector.Console.exe" /reference:System.dll /reference:System.Core.dll /reference:System.Web.Extensions.dll "%SCRIPT_DIR%Program.cs" "%SCRIPT_DIR%SettingsDocument.cs" "%SCRIPT_DIR%ProbeRecords.cs" "%SCRIPT_DIR%LocalStores.cs" "%SCRIPT_DIR%ProbeScanner.cs" "%SCRIPT_DIR%HostStatus.cs" "%SCRIPT_DIR%QmsHttpPoster.cs" "%SCRIPT_DIR%HostStatusSender.cs" "%SCRIPT_DIR%QmsSender.cs" "%SCRIPT_DIR%SyncHistory.cs" "%SCRIPT_DIR%CollectorRuntime.cs"
if errorlevel 1 exit /b %errorlevel%
"%CSC%" /nologo /target:winexe /main:GuiProgram /platform:anycpu /optimize+ /out:"%OUT_DIR%\FlyingProbeCollector.exe" /reference:System.dll /reference:System.Core.dll /reference:System.Web.Extensions.dll /reference:System.Windows.Forms.dll /reference:System.Drawing.dll "%SCRIPT_DIR%GuiProgram.cs" "%SCRIPT_DIR%CollectorForm.cs" "%SCRIPT_DIR%SyncHistoryView.cs" "%SCRIPT_DIR%Program.cs" "%SCRIPT_DIR%SettingsDocument.cs" "%SCRIPT_DIR%ProbeRecords.cs" "%SCRIPT_DIR%LocalStores.cs" "%SCRIPT_DIR%ProbeScanner.cs" "%SCRIPT_DIR%HostStatus.cs" "%SCRIPT_DIR%QmsHttpPoster.cs" "%SCRIPT_DIR%HostStatusSender.cs" "%SCRIPT_DIR%QmsSender.cs" "%SCRIPT_DIR%SyncHistory.cs" "%SCRIPT_DIR%CollectorRuntime.cs"
if errorlevel 1 exit /b %errorlevel%

if not exist "%OUT_DIR%\FlyingProbeCollector.ini" copy /Y "%SCRIPT_DIR%FlyingProbeCollector.ini" "%OUT_DIR%\FlyingProbeCollector.ini" >nul
copy /Y "%SCRIPT_DIR%start-collector.cmd" "%OUT_DIR%\start-collector.cmd" >nul
copy /Y "%SCRIPT_DIR%README.md" "%OUT_DIR%\README.md" >nul
echo Build complete: %OUT_DIR%\FlyingProbeCollector.exe and FlyingProbeCollector.Console.exe
