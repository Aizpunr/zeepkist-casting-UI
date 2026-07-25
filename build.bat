@echo off
setlocal
set ZEEP=C:\Program Files (x86)\Steam\steamapps\common\Zeepkist
set MGD=%ZEEP%\Zeepkist_Data\Managed
rem Resolve ZeepSDK.dll dynamically: its folder is versioned and changes on every SDK update.
rem pushd into Mods first so the dir command carries no "(x86)" parens (those break for /f).
set "SDK="
pushd "%ZEEP%\BepInEx\plugins\Mods"
for /f "delims=" %%i in ('dir /b /s ZeepSDK.dll 2^>nul') do set "SDK=%%i"
popd
if not defined SDK echo BUILD FAILED: could not find ZeepSDK.dll under the Mods folder& exit /b 1
echo Using ZeepSDK: %SDK%
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
  -reference:"%MGD%\UnityEngine.ImageConversionModule.dll" ^
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
rem The DLL is file-locked while Zeepkist runs, so this copy fails silently and you end up testing a
rem STALE build. Check it and shout, rather than printing "Copied" either way.
copy /Y "%OUTDIR%\LobbyOverlay.dll" "%PLUGDIR%\LobbyOverlay.dll" >nul
if errorlevel 1 (
  echo.
  echo *** DEPLOY FAILED - could not overwrite the plugin DLL. ***
  echo *** Zeepkist is probably RUNNING. Close it and rerun, or you are testing the OLD build. ***
  exit /b 1
)
copy /Y "%~dp0overlay_pool.json" "%PLUGDIR%\overlay_pool.json" >nul
copy /Y "%~dp0showdown_pool.json" "%PLUGDIR%\showdown_pool.json" >nul
rem Team logos for the Showdown broadcast card. /I so xcopy treats the target as a folder unattended.
if not exist "%PLUGDIR%\logos" mkdir "%PLUGDIR%\logos"
xcopy /Y /I /Q "%~dp0logos\*.png" "%PLUGDIR%\logos\" >nul
echo Deployed DLL + pools + logos to the LobbyOverlay plugin folder.
endlocal
