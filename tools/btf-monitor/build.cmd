@echo off
setlocal
set SCRIPT_DIR=%~dp0
set OUT_DIR=%SCRIPT_DIR%..\..\outputs\btf-monitor
if not exist "%OUT_DIR%" mkdir "%OUT_DIR%"
set CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe
if not exist "%CSC%" set CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe
if not exist "%CSC%" (
  echo .NET Framework 4.x compiler was not found.
  exit /b 1
)
set SOURCES="%SCRIPT_DIR%BtfProgram.cs" "%SCRIPT_DIR%BtfSettings.cs" "%SCRIPT_DIR%BtfProtocol.cs" "%SCRIPT_DIR%BtfQueue.cs" "%SCRIPT_DIR%BtfMqtt.cs" "%SCRIPT_DIR%BtfDelivery.cs" "%SCRIPT_DIR%BtfServer.cs" "%SCRIPT_DIR%BtfForm.cs"
"%CSC%" /nologo /target:winexe /platform:anycpu /optimize+ /out:"%OUT_DIR%\BtfMonitor.exe" /reference:System.dll /reference:System.Core.dll /reference:System.Web.Extensions.dll /reference:System.Windows.Forms.dll /reference:System.Drawing.dll %SOURCES%
if errorlevel 1 exit /b %errorlevel%
"%CSC%" /nologo /target:exe /platform:anycpu /optimize+ /out:"%OUT_DIR%\BtfMonitor.Console.exe" /reference:System.dll /reference:System.Core.dll /reference:System.Web.Extensions.dll /reference:System.Windows.Forms.dll /reference:System.Drawing.dll %SOURCES%
if errorlevel 1 exit /b %errorlevel%
if not exist "%OUT_DIR%\BtfMonitor.json" copy /Y "%SCRIPT_DIR%BtfMonitor.json" "%OUT_DIR%\BtfMonitor.json" >nul
copy /Y "%SCRIPT_DIR%s1-btf-model-template.json" "%OUT_DIR%\s1-btf-model-template.json" >nul
copy /Y "%SCRIPT_DIR%start-monitor.cmd" "%OUT_DIR%\start-monitor.cmd" >nul
copy /Y "%SCRIPT_DIR%README.md" "%OUT_DIR%\README.md" >nul
echo Build complete: %OUT_DIR%\BtfMonitor.exe
