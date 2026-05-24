@echo off
REM Beast Build Script
REM Builds Beast as a self-contained .exe and the Agent as a Docker image (beastagent)

setlocal EnableDelayedExpansion

echo ============================================
echo   Beast CLI Build Script
echo ============================================
echo.

REM Check for Docker
docker --version >nul 2>&1
if %ERRORLEVEL% neq 0 (
    echo ERROR: Docker not found. Please install Docker Desktop.
    exit /b 1
)

echo Docker version:
docker --version
echo.

REM Configuration
set BUILD_CONFIG=Release
set BEAST_PROJECT=Beast/Beast.csproj

REM Publish beast.exe inside the .NET SDK container; source tree is bind-mounted at /src.
echo Building beast.exe inside Docker (mcr.microsoft.com/dotnet/sdk:10.0)...
docker run --rm -v "%CD%:/src" -w /src mcr.microsoft.com/dotnet/sdk:10.0 sh -c "dotnet publish %BEAST_PROJECT% -c %BUILD_CONFIG% -r win-x64 --self-contained -o /tmp/beast-out && cp /tmp/beast-out/Beast.exe /src/beast.exe"
if %ERRORLEVEL% neq 0 (
    echo ERROR: Docker build failed
    exit /b 1
)
echo beast.exe copied to %CD%\beast.exe
echo.

REM Build Docker image (contains Agent at /app/Agent as entrypoint)
echo ============================================
echo Building Docker image: beastagent...
echo ============================================
echo.
docker build -t beastagent:latest -f Dockerfile .
if %ERRORLEVEL% neq 0 (
    echo ERROR: Docker build failed
    exit /b 1
)
echo.

REM Summary
echo ============================================
echo Build Complete!
echo ============================================
echo.
echo Outputs:
echo   %CD%\beast.exe
echo   Docker image: beastagent:latest  (entrypoint: /app/Agent)
echo ============================================

endlocal
