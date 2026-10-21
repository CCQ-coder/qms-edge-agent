@echo off
setlocal
set SCRIPT_DIR=%~dp0
set TEST_DIR=%TEMP%\hoson-layout-test-%RANDOM%%RANDOM%
set RESULT=1
set CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe
if not exist "%CSC%" set CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe
if not exist "%CSC%" (
  echo .NET Framework 4.x compiler was not found.
  exit /b 1
)

mkdir "%TEST_DIR%" || exit /b 1
copy /Y "%SCRIPT_DIR%mes_server_config.json" "%TEST_DIR%\mes_server_config.json" >nul || goto :cleanup
copy /Y "%SCRIPT_DIR%mes_server_defaults.json" "%TEST_DIR%\mes_server_defaults.json" >nul || goto :cleanup
"%CSC%" /nologo /target:exe /main:ParserTests /out:"%TEST_DIR%\ParserTests.exe" /reference:System.dll /reference:System.Core.dll /reference:System.Web.Extensions.dll "%SCRIPT_DIR%ParserTests.cs" "%SCRIPT_DIR%HosonMesServer.cs"
if errorlevel 1 goto :cleanup
pushd "%TEST_DIR%"
ParserTests.exe
set RESULT=%ERRORLEVEL%
popd
if not "%RESULT%" == "0" goto :cleanup
"%CSC%" /nologo /target:exe /main:RuntimeBehaviorTests /out:"%TEST_DIR%\RuntimeBehaviorTests.exe" /reference:System.dll /reference:System.Core.dll /reference:System.Web.Extensions.dll "%SCRIPT_DIR%RuntimeBehaviorTests.cs" "%SCRIPT_DIR%HosonMesServer.cs" "%SCRIPT_DIR%MesConfig.cs"
if errorlevel 1 goto :cleanup
pushd "%TEST_DIR%"
RuntimeBehaviorTests.exe
set RESULT=%ERRORLEVEL%
popd
if not "%RESULT%" == "0" goto :cleanup
"%CSC%" /nologo /target:exe /main:LayoutTests /out:"%TEST_DIR%\LayoutTests.exe" /reference:System.dll /reference:System.Core.dll /reference:System.Web.Extensions.dll /reference:System.Windows.Forms.dll /reference:System.Drawing.dll "%SCRIPT_DIR%LayoutTests.cs" "%SCRIPT_DIR%HosonMesMock.cs" "%SCRIPT_DIR%HosonMesMockCli.cs" "%SCRIPT_DIR%HosonMesServer.cs" "%SCRIPT_DIR%MesConfig.cs"
if errorlevel 1 goto :cleanup
pushd "%TEST_DIR%"
LayoutTests.exe
set RESULT=%ERRORLEVEL%
popd

:cleanup
rmdir /S /Q "%TEST_DIR%"
exit /b %RESULT%
