@echo off
echo === Ultrabot Mod Installer ===

set GAME_DIR=C:\Steam\steamapps\common\ULTRAKILL
set PLUGIN_DIR=%GAME_DIR%\BepInEx\plugins\UltrabotMod
set BUILD_DIR=Plugin\bin\Debug\net471

echo Building mod...
cd /d "%~dp0Plugin"
dotnet build
if errorlevel 1 (
    echo BUILD FAILED
    pause
    exit /b 1
)

echo Creating plugin directory...
if not exist "%PLUGIN_DIR%" mkdir "%PLUGIN_DIR%"

echo Copying DLL...
copy /Y "%BUILD_DIR%\UltrabotMod.dll" "%PLUGIN_DIR%\"

echo.
echo === Installed! ===
echo DLL copied to: %PLUGIN_DIR%\UltrabotMod.dll
echo.
echo Next steps:
echo   1. Launch ULTRAKILL
echo   2. Load a level (try 0-1 for testing)
echo   3. Run: cd Python ^&^& python train.py
echo.
pause
