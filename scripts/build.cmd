@echo off
setlocal
set SCRIPT_DIR=%~dp0
set REPO_DIR=%SCRIPT_DIR%..
set SRC_DIR=%REPO_DIR%\src
set CONFIG_DIR=%REPO_DIR%\config
set DOCS_DIR=%REPO_DIR%\docs
set OUT_DIR=%REPO_DIR%\outputs\jingde-eap-forwarder
if not exist "%OUT_DIR%" mkdir "%OUT_DIR%"

set CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe
if not exist "%CSC%" set CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe
if not exist "%CSC%" (
  echo .NET Framework 4.x compiler was not found.
  exit /b 1
)

"%CSC%" /nologo /target:winexe /platform:anycpu /optimize+ /out:"%OUT_DIR%\JingdeEapForwarder.exe" /reference:System.dll /reference:System.Core.dll /reference:System.Web.Extensions.dll /reference:System.Windows.Forms.dll /reference:System.Drawing.dll "%SRC_DIR%\JingdeEapForwarder.cs"
if errorlevel 1 exit /b %errorlevel%

if not exist "%OUT_DIR%\JingdeEapForwarder.ini" copy /Y "%CONFIG_DIR%\JingdeEapForwarder.ini" "%OUT_DIR%\JingdeEapForwarder.ini" >nul
copy /Y "%DOCS_DIR%\README.md" "%OUT_DIR%\README.md" >nul
copy /Y "%SCRIPT_DIR%start-gateway.cmd" "%OUT_DIR%\start-gateway.cmd" >nul
copy /Y "%SCRIPT_DIR%install-autostart.cmd" "%OUT_DIR%\install-autostart.cmd" >nul
echo Build complete: %OUT_DIR%\JingdeEapForwarder.exe
