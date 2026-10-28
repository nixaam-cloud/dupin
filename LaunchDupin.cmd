@echo off
if exist "%~dp0publish\Dupin.exe" (
    start "" "%~dp0publish\Dupin.exe"
    exit /b 0
)
if exist "%~dp0bin\Release\net8.0-windows10.0.19041.0\Dupin.exe" (
    start "" "%~dp0bin\Release\net8.0-windows10.0.19041.0\Dupin.exe"
    exit /b 0
)
if exist "%~dp0bin\Debug\net8.0-windows10.0.19041.0\Dupin.exe" (
    start "" "%~dp0bin\Debug\net8.0-windows10.0.19041.0\Dupin.exe"
    exit /b 0
)
set "DOTNET_EXE=%USERPROFILE%\.dotnet\dotnet.exe"
if not exist "%DOTNET_EXE%" set "DOTNET_EXE=dotnet"
start "" "%DOTNET_EXE%" "%~dp0bin\Debug\net8.0-windows10.0.19041.0\Dupin.dll"

