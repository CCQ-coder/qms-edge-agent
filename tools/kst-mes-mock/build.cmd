@echo off
setlocal
set SCRIPT_DIR=%~dp0
set OUT_DIR=%SCRIPT_DIR%..\..\outputs\kst-mes-mock
if not exist "%OUT_DIR%" mkdir "%OUT_DIR%"

set CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe
if not exist "%CSC%" set CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe
if not exist "%CSC%" (
  echo .NET Framework 4.x compiler was not found.
  exit /b 1
)

"%CSC%" /nologo /target:winexe /main:KstMesMock /platform:anycpu /optimize+ /out:"%OUT_DIR%\KstMesMock.exe" /reference:System.dll /reference:System.Core.dll /reference:System.Web.Extensions.dll /reference:System.Windows.Forms.dll /reference:System.Drawing.dll "%SCRIPT_DIR%KstMesMock.cs" "%SCRIPT_DIR%KstMesMockCli.cs" "%SCRIPT_DIR%KstMesServer.cs" "%SCRIPT_DIR%KstConfig.cs" "%SCRIPT_DIR%KstMqtt.cs" "%SCRIPT_DIR%KstListenAddress.cs" "%SCRIPT_DIR%KstSourceRoutes.cs"
if errorlevel 1 exit /b %errorlevel%

if not exist "%OUT_DIR%\kst_server.pfx" goto :generate_certificate
if not exist "%OUT_DIR%\kst_server.cer" goto :generate_certificate
goto :certificate_ready

:generate_certificate
  pwsh -NoProfile -File "%SCRIPT_DIR%new-test-certificate.ps1" -OutputPath "%OUT_DIR%\kst_server.pfx"
  if errorlevel 1 exit /b %errorlevel%

:certificate_ready

if not exist "%OUT_DIR%\kst_server_config.json" copy /Y "%SCRIPT_DIR%kst_server_config.json" "%OUT_DIR%\kst_server_config.json" >nul
if not exist "%OUT_DIR%\kst_server_device_config.json" copy /Y "%SCRIPT_DIR%kst_server_device_config.json" "%OUT_DIR%\kst_server_device_config.json" >nul
if not exist "%OUT_DIR%\kst_mqtt_config.json" copy /Y "%SCRIPT_DIR%kst_mqtt_config.json" "%OUT_DIR%\kst_mqtt_config.json" >nul
copy /Y "%SCRIPT_DIR%kst_server_defaults.json" "%OUT_DIR%\kst_server_defaults.json" >nul
copy /Y "%SCRIPT_DIR%..\..\config\s1-kst-mqtt-template.json" "%OUT_DIR%\s1-kst-mqtt-template.json" >nul
copy /Y "%SCRIPT_DIR%start-mock.cmd" "%OUT_DIR%\start-mock.cmd" >nul
copy /Y "%SCRIPT_DIR%new-test-certificate.ps1" "%OUT_DIR%\new-test-certificate.ps1" >nul
copy /Y "%SCRIPT_DIR%kst_source_routes.example.json" "%OUT_DIR%\kst_source_routes.example.json" >nul
copy /Y "%SCRIPT_DIR%README.md" "%OUT_DIR%\README.md" >nul
echo Build complete: %OUT_DIR%\KstMesMock.exe
