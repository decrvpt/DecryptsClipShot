@echo off
setlocal
cd /d "%~dp0"
cmake -S . -B build -G Ninja -DCMAKE_BUILD_TYPE=Release
if errorlevel 1 exit /b 1
cmake --build build
if errorlevel 1 exit /b 1
echo.
echo Built Decrypt's ClipShot engine: build\TrickClip.exe
