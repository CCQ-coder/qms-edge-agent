@echo off
setlocal
set SCRIPT_DIR=%~dp0
set TEST_DIR=%TEMP%\kst-layout-test-%RANDOM%%RANDOM%
set RESULT=1
set CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe
if not exist "%CSC%" set CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe
if not exist "%CSC%" exit /b 1

mkdir "%TEST_DIR%" || exit /b 1
copy /Y "%SCRIPT_DIR%kst_server_config.json" "%TEST_DIR%\kst_server_config.json" >nul || goto :cleanup
copy /Y "%SCRIPT_DIR%kst_server_defaults.json" "%TEST_DIR%\kst_server_defaults.json" >nul || goto :cleanup
copy /Y "%SCRIPT_DIR%kst_server_device_config.json" "%TEST_DIR%\kst_server_device_config.json" >nul || goto :cleanup
copy /Y "%SCRIPT_DIR%kst_mqtt_config.json" "%TEST_DIR%\kst_mqtt_config.json" >nul || goto :cleanup
"%CSC%" /nologo /target:exe /main:LayoutTests /out:"%TEST_DIR%\LayoutTests.exe" /reference:System.dll /reference:System.Core.dll /reference:System.Web.Extensions.dll /reference:System.Windows.Forms.dll /reference:System.Drawing.dll "%SCRIPT_DIR%LayoutTests.cs" "%SCRIPT_DIR%KstMesMock.cs" "%SCRIPT_DIR%KstMesMockCli.cs" "%SCRIPT_DIR%KstMesServer.cs" "%SCRIPT_DIR%KstConfig.cs" "%SCRIPT_DIR%KstMqtt.cs" "%SCRIPT_DIR%KstListenAddress.cs" "%SCRIPT_DIR%KstSourceRoutes.cs"
if errorlevel 1 goto :cleanup
"%CSC%" /nologo /target:exe /main:StartupFormTests /out:"%TEST_DIR%\StartupFormTests.exe" /reference:System.dll /reference:System.Core.dll /reference:System.Web.Extensions.dll /reference:System.Windows.Forms.dll /reference:System.Drawing.dll "%SCRIPT_DIR%StartupFormTests.cs" "%SCRIPT_DIR%KstMesMock.cs" "%SCRIPT_DIR%KstMesMockCli.cs" "%SCRIPT_DIR%KstMesServer.cs" "%SCRIPT_DIR%KstConfig.cs" "%SCRIPT_DIR%KstMqtt.cs" "%SCRIPT_DIR%KstListenAddress.cs" "%SCRIPT_DIR%KstSourceRoutes.cs"
if errorlevel 1 goto :cleanup
pushd "%TEST_DIR%"
LayoutTests.exe
if errorlevel 1 (popd & goto :cleanup)
StartupFormTests.exe
set RESULT=%ERRORLEVEL%
popd

:cleanup
rmdir /S /Q "%TEST_DIR%"
exit /b %RESULT%
