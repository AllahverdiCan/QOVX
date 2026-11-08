@echo off
setlocal EnableExtensions
chcp 65001 >nul
title QOVX v4.0.1.0 - Microsoft Store MSIX Build Pipeline
color 0B

echo ============================================================================
echo   QOVX v4.0.1.0 - MICROSOFT STORE (.MSIX) BUILD AND VERIFICATION PIPELINE
echo   Store Product ID : 9P6PL8R5TLKN
echo   Package Identity : ALLAHVERDCAN.QOVX
echo   Publisher ID     : CN=80F86ABB-4ABB-4524-A032-0CE77FF05712
echo   Publisher Name   : ALLAHVERDI CAN
echo ============================================================================
echo.

cd /d "%~dp0"
set "ROOT_DIR=%~dp0"
set "CSPROJ_PATH=%ROOT_DIR%Qovx\Qovx.csproj"
set "MANIFEST_PATH=%ROOT_DIR%Qovx\Package.appxmanifest"
set "PUBLISH_DIR=%ROOT_DIR%AppPublish"
set "LAYOUT_DIR=%ROOT_DIR%PackageLayout"
set "PKG_VERSION=4.0.1.0"
set "OUTPUT_MSIX=%ROOT_DIR%ALLAHVERDCAN.QOVX_4.0.1.0_x64.msix"

if not exist "%CSPROJ_PATH%" goto :err_no_csproj
if not exist "%MANIFEST_PATH%" goto :err_no_manifest

echo [1/7] Cleaning previous build folders and old MSIX package...
if exist "%OUTPUT_MSIX%" del /f /q "%OUTPUT_MSIX%"
if exist "%PUBLISH_DIR%" rmdir /s /q "%PUBLISH_DIR%"
if exist "%LAYOUT_DIR%" rmdir /s /q "%LAYOUT_DIR%"
if exist "%ROOT_DIR%Qovx\bin" rmdir /s /q "%ROOT_DIR%Qovx\bin"
if exist "%ROOT_DIR%Qovx\obj" rmdir /s /q "%ROOT_DIR%Qovx\obj"

echo [2/7] Verifying Microsoft Store Identity in Package.appxmanifest...
powershell -NoProfile -ExecutionPolicy Bypass -Command "$m = [xml](Get-Content -LiteralPath $env:MANIFEST_PATH -Encoding UTF8); if ($m.Package.Identity.Name -ne 'ALLAHVERDCAN.QOVX') { exit 10 }; if ($m.Package.Identity.Publisher -ne 'CN=80F86ABB-4ABB-4524-A032-0CE77FF05712') { exit 11 }; if ($m.Package.Identity.ProcessorArchitecture -ne 'x64') { exit 12 }; Write-Host '  [OK] Store Identity verified: ALLAHVERDCAN.QOVX (v4.0.1.0 x64)'"
if errorlevel 1 goto :err_manifest

echo [3/7] Restoring and Publishing QOVX via .NET SDK (Release x64)...
dotnet clean "%CSPROJ_PATH%" -c Release -v quiet
if errorlevel 1 goto :err_dotnet

dotnet restore "%CSPROJ_PATH%" -r win-x64
if errorlevel 1 goto :err_dotnet

dotnet publish "%CSPROJ_PATH%" -c Release -r win-x64 --self-contained false -o "%PUBLISH_DIR%" /p:PublishReadyToRun=false
if errorlevel 1 goto :err_dotnet

echo [4/7] Verifying compiled binary output...
if not exist "%PUBLISH_DIR%\Qovx.exe" goto :err_no_exe
echo   [OK] Binary compiled: Qovx.exe

echo [5/7] Preparing MSIX PackageLayout directory...
mkdir "%LAYOUT_DIR%" 2>nul
xcopy "%PUBLISH_DIR%\*" "%LAYOUT_DIR%\" /E /Y /Q >nul
copy /y "%MANIFEST_PATH%" "%LAYOUT_DIR%\AppxManifest.xml" >nul
if not exist "%ROOT_DIR%Qovx\Assets" goto :err_no_assets
mkdir "%LAYOUT_DIR%\Assets" 2>nul
xcopy "%ROOT_DIR%Qovx\Assets\*" "%LAYOUT_DIR%\Assets\" /E /Y /Q >nul

echo [6/7] Packing MSIX archive via MakeAppx.exe...
powershell -NoProfile -ExecutionPolicy Bypass -Command "$candidates = @('C:\Program Files (x86)\Windows Kits\10\bin\10.0.26100.0\x64\makeappx.exe', 'C:\Program Files (x86)\Windows Kits\10\bin\10.0.22621.0\x64\makeappx.exe', 'C:\Program Files (x86)\Windows Kits\10\bin\10.0.22000.0\x64\makeappx.exe', 'C:\Program Files (x86)\Windows Kits\10\bin\10.0.19041.0\x64\makeappx.exe', 'C:\Program Files (x86)\Windows Kits\10\App Certification Kit\makeappx.exe'); $exe = $candidates | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1; if (-not $exe) { $found = Get-ChildItem -Path 'C:\Program Files (x86)\Windows Kits\10\bin' -Filter makeappx.exe -Recurse -ErrorAction SilentlyContinue | Where-Object { $_.FullName -like '*\x64\*' } | Select-Object -First 1; if ($found) { $exe = $found.FullName } }; if (-not $exe) { Write-Host '  [ERROR] makeappx.exe not found in Windows Kits!'; exit 42 }; Write-Host ('  [Found] Using MakeAppx: ' + $exe); & $exe pack /d $env:LAYOUT_DIR /p $env:OUTPUT_MSIX /o; exit $LASTEXITCODE"
if errorlevel 1 goto :err_makeappx
if not exist "%OUTPUT_MSIX%" goto :err_makeappx

echo [7/7] Cleaning temporary staging directories...
if exist "%PUBLISH_DIR%" rmdir /s /q "%PUBLISH_DIR%"
if exist "%LAYOUT_DIR%" rmdir /s /q "%LAYOUT_DIR%"

echo.
echo ============================================================================
echo   [SUCCESS] MICROSOFT STORE MSIX PACKAGE CREATED SUCCESSFULLY!
echo ============================================================================
echo   Package File    : "%OUTPUT_MSIX%"
echo   Package Version : 4.0.1.0
echo   Store Identity  : ALLAHVERDCAN.QOVX
echo   Publisher ID    : CN=80F86ABB-4ABB-4524-A032-0CE77FF05712
echo ============================================================================
echo.
pause
exit /b 0

:err_no_csproj
color 0C
echo.
echo [ERROR] Qovx.csproj not found! Make sure you extracted the entire ZIP archive.
echo Expected path: "%CSPROJ_PATH%"
pause
exit /b 1

:err_no_manifest
color 0C
echo.
echo [ERROR] Package.appxmanifest not found! Make sure you extracted the entire ZIP archive.
pause
exit /b 1

:err_manifest
color 0C
echo.
echo [ERROR] Package.appxmanifest identity check failed.
pause
exit /b 1

:err_dotnet
color 0C
echo.
echo [ERROR] .NET build/publish failed! Please check the compiler output above.
pause
exit /b 1

:err_no_exe
color 0C
echo.
echo [ERROR] Qovx.exe was not found in the publish output directory.
pause
exit /b 1

:err_no_assets
color 0C
echo.
echo [ERROR] Assets folder is missing under Qovx\Assets.
pause
exit /b 1

:err_makeappx
color 0C
echo.
echo [ERROR] MakeAppx.exe packaging failed or Windows 10/11 SDK is not installed.
pause
exit /b 1
