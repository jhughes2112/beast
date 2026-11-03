@echo off
REM Beast Build-All Script (Windows), mainly for testing the cross-platform builds.
REM Builds beast for every supported OS and CPU, the native beastagent:latest image, and an image for each
REM CPU (beastagent:linux-amd64, beastagent:linux-arm64). The non-native image builds under emulation,
REM which is slow, especially the Agent's AOT compile.

setlocal EnableDelayedExpansion
pushd "%~dp0"

echo ============================================
echo   Beast Build-All Script
echo ============================================
echo.

REM Check for Docker
docker --version >nul 2>&1
if %ERRORLEVEL% neq 0 (
    echo ERROR: Docker not found. Please install Docker Desktop.
    popd
    exit /b 1
)

REM Every platform publishes to the same bin\beast.exe or bin\beast, each overwriting the last, so
REM this machine's own runtime identifier goes last and the bin\beast.exe left behind is the one that runs here.
set HOST_ARCH=%PROCESSOR_ARCHITECTURE%
if defined PROCESSOR_ARCHITEW6432 set HOST_ARCH=%PROCESSOR_ARCHITEW6432%
if /i "%HOST_ARCH%"=="ARM64" (
    set BEAST_RIDS=linux-x64 linux-arm64 osx-x64 osx-arm64 win-x64 win-arm64
) else (
    set BEAST_RIDS=linux-x64 linux-arm64 osx-x64 osx-arm64 win-arm64 win-x64
)

echo Building beast for %BEAST_RIDS% inside Docker (mcr.microsoft.com/dotnet/sdk:10.0)...
docker run --rm -v "%CD%:/src" -v "%USERPROFILE%/.nuget/packages:/root/.nuget/packages" mcr.microsoft.com/dotnet/sdk:10.0 sh /src/Beast/publish.sh %BEAST_RIDS%
if %ERRORLEVEL% neq 0 (
    echo ERROR: Docker build failed
    popd
    exit /b 1
)
echo.

REM The native image first, as beastagent:latest, so it is usable even if an emulated build fails.
echo ============================================
echo Building Docker image: beastagent:latest (native)...
echo ============================================
docker build -t beastagent:latest -f Dockerfile .
if %ERRORLEVEL% neq 0 (
    echo ERROR: Docker build failed
    popd
    exit /b 1
)
echo.

REM One image per CPU. Each is a separate single-platform build because the classic Docker image store
REM cannot load a multi-platform image; the native one is served from the cache of the build above.
for %%P in (amd64 arm64) do (
    echo ============================================
    echo Building Docker image: beastagent:linux-%%P...
    echo ============================================
    docker buildx build --platform linux/%%P -t beastagent:linux-%%P -f Dockerfile --load .
    if !ERRORLEVEL! neq 0 (
        echo ERROR: Docker build failed for linux/%%P
        popd
        exit /b 1
    )
    echo.
)

echo ============================================
echo Build-All Complete!
echo ============================================
echo.
echo Outputs:
echo   %CD%\bin\beast.exe  (this machine's Windows build)
echo   %CD%\bin\beast      (last of the macOS/Linux builds; every platform was published and verified to build)
echo   Docker images: beastagent:latest, beastagent:linux-amd64, beastagent:linux-arm64
echo ============================================

popd
endlocal
