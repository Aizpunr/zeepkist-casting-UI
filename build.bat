@echo off
setlocal
set ZEEP=C:\Program Files (x86)\Steam\steamapps\common\Zeepkist
set MGD=%ZEEP%\Zeepkist_Data\Managed
set SDK=%ZEEP%\BepInEx\plugins\Mods\3082296_7274361\ZeepSDK.dll
set CSC=C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe
set OUTDIR=%~dp0bin
set PLUGDIR=%ZEEP%\BepInEx\plugins\LobbyOverlay

if not exist "%OUTDIR%" mkdir "%OUTDIR%"

"%CSC%" -target:library -noconfig -nostdlib ^
  -out:"%OUTDIR%\LobbyOverlay.dll" ^
  -reference:"%MGD%\mscorlib.dll" ^
  -reference:"%MGD%\netstandard.dll" ^
  -reference:"%MGD%\System.dll" ^
  -reference:"%MGD%\System.Core.dll" ^
  -reference:"%ZEEP%\BepInEx\core\BepInEx.dll" ^
  -reference:"%MGD%\UnityEngine.dll" ^
  -reference:"%MGD%\UnityEngine.CoreModule.dll" ^
  -reference:"%MGD%\UnityEngine.IMGUIModule.dll" ^
  -reference:"%MGD%\UnityEngine.UIModule.dll" ^
  -reference:"%MGD%\UnityEngine.TextRenderingModule.dll" ^
  -reference:"%MGD%\UnityEngine.InputLegacyModule.dll" ^
  -reference:"%MGD%\ZeepkistNetworking.dll" ^
  -reference:"%MGD%\Zeepkist.dll" ^
  -reference:"%SDK%" ^
  -reference:"%MGD%\Newtonsoft.Json.dll" ^
  -optimize "%~dp0src\Plugin.cs"

if errorlevel 1 (
  echo BUILD FAILED
  exit /b 1
)

echo Build OK: %OUTDIR%\LobbyOverlay.dll

rem Sideload: copy DLL + data pool into a plugin folder (game must be CLOSED).
rem NOTE: PLUGDIR contains "(x86)" so it must never appear inside an if(...) block.
if not exist "%PLUGDIR%" mkdir "%PLUGDIR%"
copy /Y "%OUTDIR%\LobbyOverlay.dll" "%PLUGDIR%\LobbyOverlay.dll" >nul
copy /Y "%~dp0overlay_pool.json" "%PLUGDIR%\overlay_pool.json" >nul
echo Copied DLL + overlay_pool.json to the LobbyOverlay plugin folder.
echo (If the game was running, the copy may have failed - close it and rerun.)
endlocal
