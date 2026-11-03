@echo off
REM Beast Build Script (Windows)
REM Builds beast as a self-contained single-file executable for this machine's CPU, and the Agent as a
REM Docker image (beastagent) for the Docker VM's CPU. build.sh is the macOS/Linux equivalent;
REM build-all.bat builds every platform.

setlocal EnableDelayedExpansion
pushd "%~dp0"

echo ============================================
echo   Beast CLI Build Script
echo ============================================
echo.

REM Check for Docker
docker --version >nul 2>&1
if %ERRORLEVEL% neq 0 (
    echo ERROR: Docker not found. Please install Docker Desktop.
    popd
    exit /b 1
)

echo Docker version:
docker --version
echo.

REM Runtime identifier for this machine. PROCESSOR_ARCHITEW6432 holds the real CPU when this script runs
REM in an emulated cmd on a 64-bit host.
set HOST_ARCH=%PROCESSOR_ARCHITECTURE%
if defined PROCESSOR_ARCHITEW6432 set HOST_ARCH=%PROCESSOR_ARCHITEW6432%
set BEAST_RID=
if /i "%HOST_ARCH%"=="AMD64" set BEAST_RID=win-x64
if /i "%HOST_ARCH%"=="ARM64" set BEAST_RID=win-arm64
if "%BEAST_RID%"=="" (
    echo ERROR: Unsupported CPU architecture: %HOST_ARCH%
    popd
    exit /b 1
)

REM Publish beast inside the .NET SDK container; the source tree is bind-mounted at /src and
REM Beast/publish.sh does the work. The local NuGet package cache is mounted to avoid redundant downloads.
echo Building beast for %BEAST_RID% inside Docker (mcr.microsoft.com/dotnet/sdk:10.0)...
docker run --rm -v "%CD%:/src" -v "%USERPROFILE%/.nuget/packages:/root/.nuget/packages" mcr.microsoft.com/dotnet/sdk:10.0 sh /src/Beast/publish.sh %BEAST_RID%
if %ERRORLEVEL% neq 0 (
    echo ERROR: Docker build failed
    popd
    exit /b 1
)
echo.

REM Install to %USERPROFILE%\.local\bin, where Claude Code's installer also puts claude, so it is usually on
REM the PATH already. A running beast.exe can't be overwritten but can be renamed, so the old one is moved aside.
set INSTALL_DIR=%USERPROFILE%\.local\bin
if not exist "%INSTALL_DIR%" mkdir "%INSTALL_DIR%"
if exist "%INSTALL_DIR%\beast.exe.old" del /f /q "%INSTALL_DIR%\beast.exe.old" >nul 2>&1
if exist "%INSTALL_DIR%\beast.exe" move /y "%INSTALL_DIR%\beast.exe" "%INSTALL_DIR%\beast.exe.old" >nul
copy /y bin\beast.exe "%INSTALL_DIR%\beast.exe" >nul
if %ERRORLEVEL% neq 0 (
    echo ERROR: Could not install %INSTALL_DIR%\beast.exe
    popd
    exit /b 1
)
echo Installed %INSTALL_DIR%\beast.exe
echo ;%PATH%; | find /i ";%INSTALL_DIR%;" >nul
if %ERRORLEVEL% neq 0 (
    echo NOTE: %INSTALL_DIR% is not on your PATH. Add it under Settings, System, About, Advanced system
    echo       settings, Environment Variables, then open a new terminal.
)
echo.

REM Build Docker image (contains Agent at /app/Agent as entrypoint). docker build targets the Docker VM's
REM own CPU, so the image is amd64 or arm64 to match this machine.
echo ============================================
echo Building Docker image: beastagent...
echo ============================================
echo.
docker build -t beastagent:latest -f Dockerfile .
if %ERRORLEVEL% neq 0 (
    echo ERROR: Docker build failed
    popd
    exit /b 1
)
echo.

REM Summary
echo ============================================
echo Build Complete!
echo ============================================
echo.
echo Outputs:
echo   %INSTALL_DIR%\beast.exe  (%BEAST_RID%, also in %CD%\bin)
echo   Docker image: beastagent:latest  (entrypoint: /app/Agent)
echo ============================================

popd
endlocal
