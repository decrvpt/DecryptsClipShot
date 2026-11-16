@echo off
setlocal
cd /d "%~dp0"

where dotnet >nul 2>nul
if errorlevel 1 (
    echo The .NET SDK was not found. Install .NET 10 SDK from https://dotnet.microsoft.com/download/dotnet/10.0
    exit /b 1
)

dotnet --list-sdks | findstr /B "10." >nul
if errorlevel 1 (
    echo .NET 10 SDK is required to build the bundled local Fortnite replay parser.
    echo Install it from https://dotnet.microsoft.com/download/dotnet/10.0
    exit /b 1
)

call build.bat
if errorlevel 1 exit /b 1

rem Optional release version (set by build_msi.bat or the GitHub workflow).
set VERSION_ARGS=
if defined CLIPSHOT_VERSION set VERSION_ARGS=-p:Version=%CLIPSHOT_VERSION% -p:AssemblyVersion=%CLIPSHOT_VERSION%.0 -p:FileVersion=%CLIPSHOT_VERSION%.0

dotnet publish gui\DecryptsClipShot\DecryptsClipShot.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=false %VERSION_ARGS% -o dist\DecryptsClipShot
if errorlevel 1 exit /b 1

dotnet publish local_parser\DecryptsClipShot.ReplayParser\DecryptsClipShot.ReplayParser.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o dist\ReplayParser
if errorlevel 1 exit /b 1

dotnet publish audio_helper\DecryptsClipShot.Audio\DecryptsClipShot.Audio.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o dist\AudioHelper
if errorlevel 1 exit /b 1

copy /Y build\TrickClip.exe dist\DecryptsClipShot\TrickClip.exe >nul
copy /Y trickclip.ini dist\DecryptsClipShot\trickclip.ini >nul
copy /Y dist\ReplayParser\ClipShotReplayParser.exe dist\DecryptsClipShot\ClipShotReplayParser.exe >nul
copy /Y dist\AudioHelper\ClipShotAudio.exe dist\DecryptsClipShot\ClipShotAudio.exe >nul
copy /Y THIRD_PARTY_NOTICES.md dist\DecryptsClipShot\THIRD_PARTY_NOTICES.md >nul
if exist ffmpeg.exe copy /Y ffmpeg.exe dist\DecryptsClipShot\ffmpeg.exe >nul

echo.
echo Decrypt's ClipShot is ready in:
echo dist\DecryptsClipShot
echo.
echo Run DecryptsClipShot.exe
