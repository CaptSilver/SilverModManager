@echo off

:: Runs redMod.exe from the game folder. The app passes the game folder in
:: STEAM_APP_PATH, since the working directory of a program started through
:: Proton, umu or Wine is not reliably the game folder.

rem Check if STEAM_APP_PATH environment variable is set
if "%STEAM_APP_PATH%"=="" (
    echo Error: STEAM_APP_PATH environment variable is not set.
    echo This script is meant to be started by the app.
    pause
    exit /b 1
)

rem Navigate to the STEAM_APP_PATH directory
cd /d "%STEAM_APP_PATH%"

rem Change to the directory containing redMod.exe
cd tools\redmod\bin

rem Check if redMod.exe exists in the current directory
if not exist "redMod.exe" (
    echo Error: redMod.exe not found in the current directory.
    echo Make sure the STEAM_APP_PATH is set correctly and the redmod folder structure is intact.
    pause
    exit /b 1
)

echo Launching redMod.exe with deploy parameter...
redMod.exe %*
