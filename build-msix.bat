@echo off
setlocal EnableExtensions EnableDelayedExpansion
chcp 65001 >nul
title QOVX - Microsoft Store MSIX Build Pipeline
color 0B

echo ============================================================================
echo   QOVX - MICROSOFT STORE (.MSIX) BUILD AND VERIFICATION PIPELINE
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

if not exist "%CSPROJ_PATH%" (
    echo [ERROR] Project file not found: "%CSPROJ_PATH%"
    pause
    exit /b 1
)

if not exist "%MANIFEST_PATH%" (
    echo [ERROR] Package manifest not found: "%MANIFEST_PATH%"
    pause
    exit /b 1
)

for /f "usebackq tokens=*" %%v in (`powershell -NoProfile -Command "[xml]$m = Get-Content '%MANIFEST_PATH%'; $m.Package.Identity.Version"`) do set "PKG_VERSION=%%v"
if "%PKG_VERSION%"=="" set "PKG_VERSION=3.0.1.0"
set "OUTPUT_MSIX=%ROOT_DIR%ALLAHVERDCAN.QOVX_%PKG_VERSION%_x64.msix"

echo [1/7] Cleaning previous artifacts and old package (%PKG_VERSION%)...
if exist "%OUTPUT_MSIX%" del /f /q "%OUTPUT_MSIX%"
if exist "%PUBLISH_DIR%" rmdir /s /q "%PUBLISH_DIR%"
if exist "%LAYOUT_DIR%" rmdir /s /q "%LAYOUT_DIR%"
if exist "%ROOT_DIR%Qovx\bin" rmdir /s /q "%ROOT_DIR%Qovx\bin"
if exist "%ROOT_DIR%Qovx\obj" rmdir /s /q "%ROOT_DIR%Qovx\obj"

echo [2/7] Verifying Microsoft Store Immutable Identity in Manifest...
powershell -NoProfile -Command ^
  "[xml]$m = Get-Content '%MANIFEST_PATH%'; " ^
  "if ($m.Package.Identity.Name -ne 'ALLAHVERDCAN.QOVX') { throw 'Identity.Name mismatch!' }; " ^
  "if ($m.Package.Identity.Publisher -ne 'CN=80F86ABB-4ABB-4524-A032-0CE77FF05712') { throw 'Identity.Publisher mismatch!' }; " ^
  "if ($m.Package.Identity.ProcessorArchitecture -ne 'x64') { throw 'ProcessorArchitecture mismatch!' }; " ^
  "if ($m.Package.Properties.PublisherDisplayName -ne 'ALLAHVERDİ CAN') { throw 'PublisherDisplayName mismatch!' }; " ^
  "Write-Host '  [OK] Store Identity verified: ALLAHVERDCAN.QOVX (x64)'"
if %errorlevel% neq 0 (
    echo [ERROR] Manifest Store Identity verification failed. Aborting.
    pause
    exit /b 1
)

echo [3/7] Restoring and Building QOVX via .NET SDK (Release x64)...
dotnet clean "%CSPROJ_PATH%" -c Release -v quiet
if %errorlevel% neq 0 (
    echo [ERROR] dotnet clean failed!
    pause
    exit /b 1
)

dotnet restore "%CSPROJ_PATH%" -r win-x64
if %errorlevel% neq 0 (
    echo [ERROR] dotnet restore failed!
    pause
    exit /b 1
)

dotnet publish "%CSPROJ_PATH%" -c Release -r win-x64 --self-contained false -o "%PUBLISH_DIR%" /p:PublishReadyToRun=false
if %errorlevel% neq 0 (
    echo [ERROR] dotnet publish failed with exit code %errorlevel%!
    pause
    exit /b 1
)

echo [4/7] Verifying build output binary...
if not exist "%PUBLISH_DIR%\Qovx.exe" (
    echo [ERROR] Expected binary not found: "%PUBLISH_DIR%\Qovx.exe"
    pause
    exit /b 1
)
echo   [OK] Binary found: "%PUBLISH_DIR%\Qovx.exe"

echo [5/7] Staging MSIX Package Layout...
mkdir "%LAYOUT_DIR%"
xcopy "%PUBLISH_DIR%\*" "%LAYOUT_DIR%\" /E /Y /Q >nul
copy /y "%MANIFEST_PATH%" "%LAYOUT_DIR%\AppxManifest.xml" >nul

if not exist "%ROOT_DIR%Qovx\Assets" (
    echo [ERROR] Assets directory missing!
    pause
    exit /b 1
)
mkdir "%LAYOUT_DIR%\Assets" 2>nul
xcopy "%ROOT_DIR%Qovx\Assets\*" "%LAYOUT_DIR%\Assets\" /E /Y /Q >nul

echo [6/7] Packing MSIX via MakeAppx.exe...
set "MAKEAPPX_FOUND=0"
for %%P in (
    "C:\Program Files (x86)\Windows Kits\10\bin\10.0.26100.0\x64\makeappx.exe"
    "C:\Program Files (x86)\Windows Kits\10\bin\10.0.22621.0\x64\makeappx.exe"
    "C:\Program Files (x86)\Windows Kits\10\bin\10.0.22000.0\x64\makeappx.exe"
    "C:\Program Files (x86)\Windows Kits\10\bin\10.0.19041.0\x64\makeappx.exe"
    "C:\Program Files (x86)\Windows Kits\10\App Certification Kit\makeappx.exe"
) do (
    if exist %%P (
        echo   [Found] Using MakeAppx: %%P
        %%P pack /d "%LAYOUT_DIR%" /p "%OUTPUT_MSIX%" /o
        if !errorlevel! equ 0 set "MAKEAPPX_FOUND=1"
        goto :pack_done
    )
)

where makeappx >nul 2>&1
if %errorlevel% equ 0 (
    echo   [Found] Using MakeAppx from PATH
    makeappx pack /d "%LAYOUT_DIR%" /p "%OUTPUT_MSIX%" /o
    if !errorlevel! equ 0 set "MAKEAPPX_FOUND=1"
    goto :pack_done
)

:pack_done
if "!MAKEAPPX_FOUND!"=="0" (
    echo   [INFO] Searching Windows Kits directory for makeappx.exe...
    powershell -NoProfile -Command ^
      "$m = Get-ChildItem -Path 'C:\Program Files (x86)\Windows Kits\10\bin' -Filter makeappx.exe -Recurse -ErrorAction SilentlyContinue | Where-Object { $_.FullName -like '*\x64\*' } | Select-Object -First 1; " ^
      "if ($m) { Write-Host ('  [Found] ' + $m.FullName); & $m.FullName pack /d '%LAYOUT_DIR%' /p '%OUTPUT_MSIX%' /o; exit $LASTEXITCODE } else { exit 42 }"
    if !errorlevel! equ 0 set "MAKEAPPX_FOUND=1"
)

if not exist "%OUTPUT_MSIX%" (
    echo.
    echo [ERROR] MSIX packaging failed or MakeAppx.exe was not found.
    pause
    exit /b 1
)

echo [7/7] Cleaning temporary build folders...
if exist "%PUBLISH_DIR%" rmdir /s /q "%PUBLISH_DIR%"
if exist "%LAYOUT_DIR%" rmdir /s /q "%LAYOUT_DIR%"

echo.
echo ============================================================================
echo   [SUCCESS] MICROSOFT STORE MSIX PACKAGE CREATED AND READY FOR UPLOAD!
echo ============================================================================
echo   Store Package   : "%OUTPUT_MSIX%"
echo   Package Version : %PKG_VERSION%
echo   Store Identity  : ALLAHVERDCAN.QOVX
echo   Publisher ID    : CN=80F86ABB-4ABB-4524-A032-0CE77FF05712
echo.
echo   Partner Center'a (Submission 4) yuklemeye hazirdir!
echo ============================================================================
echo.
pause
exit /b 0
