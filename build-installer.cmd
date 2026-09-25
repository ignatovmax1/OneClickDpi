@echo off
REM Hello DPI - Build and Installer Script
setlocal EnableDelayedExpansion

echo ========================================
echo   Hello DPI - Build & Installer
echo ========================================
echo.

REM Check if dotnet is available
where dotnet >nul 2>nul
if %ERRORLEVEL% neq 0 (
    echo [ERROR] .NET SDK not found!
    pause
    exit /b 1
)

echo [1/4] Restoring packages...
dotnet restore HelloDpi.slnx
if %ERRORLEVEL% neq 0 (
    echo [ERROR] Restore failed!
    pause
    exit /b 1
)
echo [OK] Packages restored.
echo.

echo [2/4] Building in Release mode...
dotnet build HelloDpi.slnx -c Release --no-restore
if %ERRORDIR% neq 0 (
    echo [ERROR] Build failed!
    pause
    exit /b 1
)
echo [OK] Build complete.
echo.

echo [3/4] Publishing for Windows x64...
set PUBLISH_DIR=%~dp0publish
if exist "%PUBLISH_DIR%" rmdir /s /q "%PUBLISH_DIR%"
dotnet publish src\HelloDpi.App\HelloDpi.App.csproj ^
    -c Release ^
    -r win-x64 ^
    --self-contained true ^
    -p:PublishSingleFile=true ^
    -p:IncludeNativeLibrariesForSelfExtract=true ^
    -p:PublishReadyToRun=false ^
    -p:IncludeAllContentForSelfExtract=true ^
    -o "%PUBLISH_DIR%"
if %ERRORLEVEL% neq 0 (
    echo [ERROR] Publish failed!
    pause
    exit /b 1
)
echo [OK] Published to: %PUBLISH_DIR%
echo.

echo [4/4] Creating installer...
REM Check if NSIS is installed
where makensis >nul 2>nul
if %ERRORLEVEL% neq 0 (
    echo [WARN] NSIS not found in PATH. Creating ZIP package instead.
    echo.
    goto :createzip
)

REM Copy publish files for installer
if not exist "%~dp0installer-build" mkdir "%~dp0installer-build"
xcopy /E /I /Y "%PUBLISH_DIR%\*" "%~dp0installer-build\"

REM Copy icon if exists
if exist "%~dp0src\HelloDpi.App\Assets\appicon.ico" (
    copy /Y "%~dp0src\HelloDpi.App\Assets\appicon.ico" "%~dp0installer-build\appicon.ico" >nul
)

REM Copy license
if exist "%~dp0LICENSE" (
    copy /Y "%~dp0LICENSE" "%~dp0installer-build\LICENSE" >nul
)

REM Build installer
makensis "%~dp0tools\HelloDpi.Installer\HelloDpi-installer.nsi"
if %ERRORLEVEL% neq 0 (
    echo [ERROR] NSIS build failed!
    pause
    exit /b 1
)

echo [OK] Installer created: %~dp0HelloDpi-Installer.exe
echo.

:createzip
REM Create ZIP package
set VERSION=0.6.33
set ZIP_NAME=HelloDpi-MVP-%VERSION%-win-x64.zip

if exist "%~dp0%ZIP_NAME%" del /q "%~dp0%ZIP_NAME%"

powershell -Command "Compress-Archive -Path '%PUBLISH_DIR%\*' -DestinationPath '%~dp0%ZIP_NAME%' -Force"
if %ERRORLEVEL% neq 0 (
    echo [ERROR] ZIP creation failed!
    pause
    exit /b 1
)

echo [OK] ZIP package created: %~dp0%ZIP_NAME%
echo.

echo ========================================
echo   Build complete!
echo   Output: %PUBLISH_DIR%
echo   ZIP: %~dp0%ZIP_NAME%
echo ========================================
echo.
pause
