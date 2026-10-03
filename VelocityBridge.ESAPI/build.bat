@echo off
chcp 65001 >nul
setlocal

echo ============================================
echo  AutoFLC ESAPI - Build Script
echo ============================================
echo.

set "PROJ=VelocityBridge.ESAPI.csproj"
set "OUTDIR=bin\Debug"
set "EXE=%OUTDIR%\AutoFLC.exe"

where dotnet >nul 2>nul
if %ERRORLEVEL% NEQ 0 (
    echo ERROR: dotnet CLI not found. Please install .NET SDK or use Visual Studio.
    if not defined NOPAUSE pause
    exit /b 1
)

echo Building with dotnet...
dotnet build "%PROJ%" -c Debug -o "%OUTDIR%"

if %ERRORLEVEL% NEQ 0 (
    echo.
    echo Build failed.
    if not defined NOPAUSE pause
    exit /b %ERRORLEVEL%
)

echo.
echo Build successful: %EXE%
echo.

if not defined NOPAUSE pause
