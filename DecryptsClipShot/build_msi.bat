@echo off
setlocal
cd /d "%~dp0"

rem Usage:  build_msi.bat [version]      e.g.  build_msi.bat 1.2.0
rem Players never run this. It makes the files you upload to a GitHub release.
rem (The GitHub workflow in .github\workflows\release.yml runs it for you.)
set CLIPSHOT_VERSION=%~1
if "%CLIPSHOT_VERSION%"=="" set CLIPSHOT_VERSION=1.0.0
echo Building Decrypt's ClipShot %CLIPSHOT_VERSION%

call build_gui.bat
if errorlevel 1 exit /b 1

if not exist dist\DecryptsClipShot\ffmpeg.exe (
    echo.
    echo ffmpeg.exe is missing from dist\DecryptsClipShot.
    echo Put ffmpeg.exe beside build_msi.bat and run this script again.
    exit /b 1
)

rem ---- 1. MSI package (the actual installation) ----
dotnet build installer\DecryptsClipShot.Installer.wixproj -c Release -p:ClipShotVersion=%CLIPSHOT_VERSION%
if errorlevel 1 exit /b 1

if not exist dist\Installer\DecryptsClipShot-Setup.msi (
    echo.
    echo The MSI was not created at dist\Installer\DecryptsClipShot-Setup.msi.
    exit /b 1
)

rem ---- 2. One-button setup wizard with the MSI inside it ----
dotnet publish setup\DecryptsClipShot.Setup\DecryptsClipShot.Setup.csproj -c Release -r win-x64 --self-contained true ^
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true ^
    -p:Version=%CLIPSHOT_VERSION% ^
    -p:ClipShotMsi="%CD%\dist\Installer\DecryptsClipShot-Setup.msi" ^
    -o dist\SetupWizard
if errorlevel 1 exit /b 1

copy /Y dist\SetupWizard\DecryptsClipShot-Setup.exe dist\Installer\DecryptsClipShot-Setup.exe >nul
if errorlevel 1 exit /b 1

echo.
echo Done. Upload these from dist\Installer to your GitHub release:
echo   DecryptsClipShot-Setup.exe   - the download for players (one-click setup)
echo   DecryptsClipShot-Setup.msi   - optional, for silent or managed installs
