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
echo   %CD%\build\bin\release\beast-%BEAST_RID%\beast.exe
echo   Docker image: beastagent:latest  (entrypoint: /app/Agent)
echo ============================================

popd
endlocal
