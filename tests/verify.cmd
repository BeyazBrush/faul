@echo off
REM Build MSCSteamMP and run the offline verification suite.
REM
REM These checks run without the game: Harmony patch resolution, the binary wire format,
REM and the capture/batching/echo-guard logic. They do NOT replace a real two-player
REM session, which is the only way to prove the game side.
REM
REM Two deliberate differences from build.cmd, both because the probes run on the desktop
REM CLR rather than inside Unity:
REM
REM  * /optimize- : a release build lets the CLR JIT inline the four-line FsmBool.set_Value
REM    straight into the caller, so a Harmony patch of that setter is never executed and
REM    PatchProbe reports zero hits even though the patch applied. Unity's Mono JIT does not
REM    inline, so this is a property of the desktop test host, not of the mod.
REM  * .NET 4.x reference assemblies: the game's Managed folder ships reference-only
REM    System*.dll files that the 4.x CLR refuses to load (strong name verification), so the
REM    probes compile against the machine's framework and the game's own assemblies are used
REM    for everything game specific.
setlocal enabledelayedexpansion

set "ROOT=%~dp0..\"
set "TESTS=%~dp0"
set "GAME=%~1"
if "%GAME%"=="" set "GAME=D:\Steam\steamapps\common\My Summer Car"
set "MANAGED=%GAME%\mysummercar_Data\Managed"
set "BIN=%TESTS%bin"
set "REFASM=C:\Program Files (x86)\Reference Assemblies\Microsoft\Framework\.NETFramework\v4.8"
set "CSC=C:\Program Files\Microsoft Visual Studio\18\Community\MSBuild\Current\Bin\Roslyn\csc.exe"

if not exist "%CSC%" (
  echo [ERROR] C# compiler not found at "%CSC%".
  exit /b 1
)
if not exist "%MANAGED%\PlayMaker.dll" (
  echo [ERROR] Game not found at "%GAME%". Pass the path as the first argument.
  exit /b 1
)
if not exist "%REFASM%\mscorlib.dll" (
  echo [ERROR] .NET Framework reference assemblies not found at "%REFASM%".
  exit /b 1
)

echo [1/3] Building MSCSteamMP...
call "%ROOT%build.cmd" "%GAME%"
if errorlevel 1 exit /b 1

if not exist "%BIN%" mkdir "%BIN%"

rem The probes load the game assemblies by probing the folder next to the exe, so they are
rem copied in. The game's reference-only framework assemblies are removed again: they would
rem shadow the real ones and fail to load.
copy /Y "%MANAGED%\*.dll" "%BIN%\" >nul
del /Q "%BIN%\mscorlib.dll" "%BIN%\System.dll" "%BIN%\System.Core.dll" "%BIN%\System.Data.dll" "%BIN%\System.Xml.dll" "%BIN%\System.Drawing.dll" "%BIN%\System.Runtime.Serialization.dll" 2>nul
copy /Y "%ROOT%bin\Release\MSCSteamMP.dll" "%BIN%\" >nul

set FAILED=0

for %%P in (PatchProbe CoreProbe WireProbe MenuProbe InterpProbe DeathProbe) do (
  echo [2/3] Building %%P...
  rem Delayed expansion is required here: inside a for block a plain %RSP% would be expanded
  rem once, before the loop, so every probe would write to the same (or an empty) path.
  rem The quotes are written as literal text rather than kept in a variable: set "X=...""..."""
  rem does not survive the round trip, and an unquoted reference loses its path at the first
  rem space in "My Summer Car".
  set "RSP=!BIN!\%%P.rsp"
  > "!RSP!" echo /nologo /target:exe /nostdlib+ /langversion:latest /optimize- /define:TRACE;STEAMWORKS_WIN /nowarn:0169,0414,0649,0067
  >>"!RSP!" echo /out:"!BIN!\%%P.exe"
  for %%D in (mscorlib System System.Core System.Xml System.Data) do >>"!RSP!" echo /r:"%REFASM%\%%D.dll"
  for %%D in (UnityEngine UnityEngine.UI Assembly-CSharp Assembly-CSharp-firstpass PlayMaker cInput 0Harmony MSCLoader) do >>"!RSP!" echo /r:"%MANAGED%\%%D.dll"
  >>"!RSP!" echo /r:"!BIN!\MSCSteamMP.dll"
  >>"!RSP!" echo "!TESTS!%%P.cs"
  "%CSC%" /noconfig "@!RSP!"
  if !errorlevel! neq 0 (
    echo [ERROR] %%P failed to build.
    set FAILED=1
  )
)
if "%FAILED%"=="1" exit /b 1

echo.
for %%P in (PatchProbe CoreProbe WireProbe MenuProbe InterpProbe DeathProbe) do (
  echo [3/3] === %%P ===
  "%BIN%\%%P.exe"
  rem `if errorlevel 1` burada CALISMAZ ve tarihsel olarak calismiyordu:
  rem yakalanmamis bir .NET istisnasi prosesi 0xE0434352 ile kapatir,
  rem yani cmd onu -532462766 olarak gorur. `if errorlevel 1` ise bir
  rem IMZALI karsilastirma yapar ve -532462766 < 1 oldugu icin FALSE
  rem doner. Sonucu: bir probe COKERKEN verify.cmd "VERIFICATION
  rem PASSED" diyordu (CoreProbe, canli kanit). `!errorlevel! neq 0`
  rem delayed expansion ile ayni imzali degeri 0'a esitlik icin test
  rem eder ve negatif cikisi da yakalar.
  if !errorlevel! neq 0 set FAILED=1
  echo.
)

if "%FAILED%"=="1" (
  echo VERIFICATION FAILED
  exit /b 1
)
echo VERIFICATION PASSED
endlocal