@echo off
REM Beast Build Script
REM Builds Beast as a self-contained .exe and the Agent as a Docker image (beastagent)

setlocal EnableDelayedExpansion

echo ============================================
echo   Beast CLI Build Script
echo ============================================
echo.

REM Check for .NET SDK
dotnet --version >nul 2>&1
if %ERRORLEVEL% neq 0 (
    echo ERROR: .NET SDK not found. Please install .NET 10.0 SDK or later.
    exit /b 1
)

echo .NET SDK version:
dotnet --version
echo.

REM Configuration
set BUILD_CONFIG=Release
set BEAST_PROJECT=Beast\Beast.csproj
set AGENT_PROJECT=Agent\Agent.csproj
set OUTPUT_DIR=bin\release

REM Clean previous builds
echo Cleaning previous builds...
if exist %OUTPUT_DIR% rmdir /s /q %OUTPUT_DIR%
dotnet clean KanBeast.slnx -c %BUILD_CONFIG% 2>nul
echo.

REM Restore NuGet packages
echo Restoring NuGet packages...
dotnet restore KanBeast.slnx
if %ERRORLEVEL% neq 0 (
    echo ERROR: Failed to restore packages
    exit /b 1
)
echo.

REM Publish Beast as self-contained .exe
echo Publishing beast.exe...
dotnet publish %BEAST_PROJECT% -c %BUILD_CONFIG% -r win-x64 --self-contained -o %OUTPUT_DIR%\beast-win-x64
if %ERRORLEVEL% neq 0 (
    echo ERROR: Beast publish failed
    exit /b 1
)
echo beast.exe published to %OUTPUT_DIR%\beast-win-x64\
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
echo   %OUTPUT_DIR%\beast-win-x64\beast.exe
echo   Docker image: beastagent:latest  (entrypoint: /app/Agent)
echo ============================================

endlocal
