@echo off
REM Builds MSCSteamMP.dll with the C# compiler directly against the game's own assemblies.
REM
REM This replaces the MSBuild + ".NET Framework 3.5 targeting pack" route, which fails on a
REM machine that only has modern .NET SDKs installed. The game itself ships the reference
REM assemblies the mod has to compile against:
REM
REM   mysummercar_Data\Managed\mscorlib.dll  -> version 2.0.5.0
REM
REM That is the reason for compiling against them rather than against a targeting pack: Unity
REM 5.0.0f4 runs Mono on a 2.5 profile and Mono refuses to load an assembly referencing a newer
REM mscorlib. Compiling this way makes the requirement structural instead of a comment somebody
REM has to remember.
REM
REM The game path contains spaces ("My Summer Car"), so the references go into a response file
REM where the quoting survives. /noconfig has to stay on the command line: csc ignores it inside
REM a .rsp, which would let the machine's csc.rsp add its own System.dll back in and collide
REM with the game's copy.
REM
REM Usage:  build.cmd  ["<path to My Summer Car>"]
setlocal enabledelayedexpansion

set "ROOT=%~dp0"
set "GAME=%~1"
if "%GAME%"=="" set "GAME=D:\Steam\steamapps\common\My Summer Car"
set "MANAGED=%GAME%\mysummercar_Data\Managed"
set "OUT=%ROOT%bin\Release\MSCSteamMP.dll"
set "CSC=C:\Program Files\Microsoft Visual Studio\18\Community\MSBuild\Current\Bin\Roslyn\csc.exe"

if not exist "%CSC%" (
  echo [ERROR] C# compiler not found at "%CSC%".
  echo         Install Visual Studio, or point CSC at another csc.exe.
  exit /b 1
)
if not exist "%MANAGED%\PlayMaker.dll" (
  echo [ERROR] Game not found at "%GAME%". Pass the path as the first argument.
  exit /b 1
)
if not exist "%ROOT%bin\Release" mkdir "%ROOT%bin\Release"
if not exist "%ROOT%assets\mpplayer" (
  echo [ERROR] assets\mpplayer not found.
  echo         The remote player model is embedded into the DLL by the /resource
  echo         line below; without it there is no model to load at runtime.
  exit /b 1
)
if not exist "%ROOT%assets\menu_bg.jpg" (
  echo [ERROR] assets\menu_bg.jpg not found.
  echo         The menu background is embedded into the DLL by the /resource
  echo         line below; without it the menu falls back to the gradient.
  exit /b 1
)

set "RSP=%ROOT%bin\build.rsp"
> "%RSP%" echo /nologo
>>"%RSP%" echo /target:library
>>"%RSP%" echo /nostdlib+
>>"%RSP%" echo /langversion:latest
>>"%RSP%" echo /optimize+
>>"%RSP%" echo /debug:pdbonly
>>"%RSP%" echo /define:TRACE;STEAMWORKS_WIN
rem 0169 field never assigned, 0414 assigned but unused, 0649 never assigned to a field,
rem 0067 unused event - all harmless for a mod that also reflects at runtime.
>>"%RSP%" echo /nowarn:0169,0414,0649,0067
>>"%RSP%" echo /out:"%OUT%"

REM Oyuncu modeli DLL'in icine gomulur. Unity asset bundle'i, Unity 5.0.0f4 ile
REM derlenmis (oyunun kendi Unity surumu) ve runtime'da
REM AssetBundle.CreateFromMemory ile acilir. Dosya kurulum klasorunde durmaz,
REM bu yuzden "no asset bundle" kurali ve tek DLL dagitimi bozulmaz.
REM Gerekce: FBX runtime'da yuklenemez, yalnizca derlenmis bundle okunur.
>>"%RSP%" echo /resource:"%ROOT%assets\mpplayer","SteamMP.player.bundle"
>>"%RSP%" echo /resource:"%ROOT%assets\menu_bg.jpg","SteamMP.menu_bg.jpg"
if exist "%ROOT%assets\cursor.png" >>"%RSP%" echo /resource:"%ROOT%assets\cursor.png","SteamMP.cursor.png"
rem NPC oyuncu modeli (araba suren NPC'den cikarilan mesh/iskelet/animasyon).
rem tools\make_char_data.py + build_bundle.cmd ile uretilir; ayni Unity surumu.
if exist "%ROOT%assets\mpnpc" >>"%RSP%" echo /resource:"%ROOT%assets\mpnpc","SteamMP.npc.bundle"

rem Framework assemblies come from the game, not from the machine: that is what pins the
rem output to mscorlib 2.0.
for %%D in (mscorlib System System.Core System.Xml System.Data) do >>"%RSP%" echo /r:"%MANAGED%\%%D.dll"
for %%D in (UnityEngine UnityEngine.UI Assembly-CSharp Assembly-CSharp-firstpass PlayMaker cInput 0Harmony MSCLoader) do >>"%RSP%" echo /r:"%MANAGED%\%%D.dll"

for %%F in ("%ROOT%src\MSCSteamMP\*.cs" "%ROOT%src\MSCSteamMP\Core\*.cs" "%ROOT%src\MSCSteamMP\Net\*.cs" "%ROOT%src\MSCSteamMP\Satsuma\*.cs" "%ROOT%src\MSCSteamMP\Vehicle\*.cs" "%ROOT%src\MSCSteamMP\Player\*.cs" "%ROOT%src\MSCSteamMP\View\*.cs" "%ROOT%src\MSCSteamMP\Properties\*.cs" "%ROOT%src\MSCSteamMP\Interaction\*.cs" "%ROOT%src\MSCSteamMP\Integration\*.cs" "%ROOT%src\MSCSteamMP\UI\*.cs") do >>"%RSP%" echo "%%~F"

echo [1/1] Compiling MSCSteamMP.dll against "%GAME%"
"%CSC%" /noconfig "@%RSP%"
if errorlevel 1 (
  echo [ERROR] Build failed.
  exit /b 1
)

echo.
echo Done: "%OUT%"
endlocal