@echo off
chcp 65001 >nul
setlocal
set "MOD_DIR=%~dp0NightbirdLiveAdder"
set "GAME_DIR=d:\SteamLibrary\steamapps\common\Touhou Mystia Izakaya"

echo [1/2] 正在编译 NightbirdLiveAdder.dll ...
dotnet build "%MOD_DIR%\NightbirdLiveAdder.csproj" -c Release -p:GameDir="%GAME_DIR%"
if errorlevel 1 (
    echo 编译失败！
    pause
    exit /b 1
)

echo [2/2] 正在部署到游戏 BepInEx\plugins\ ...
if not exist "%GAME_DIR%\BepInEx\plugins" mkdir "%GAME_DIR%\BepInEx\plugins"
copy /Y "%MOD_DIR%\bin\Release\NightbirdLiveAdder.dll" "%GAME_DIR%\BepInEx\plugins\NightbirdLiveAdder.dll" >nul
echo 部署成功：%GAME_DIR%\BepInEx\plugins\NightbirdLiveAdder.dll
pause
