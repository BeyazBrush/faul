@echo off
REM Build and install MSCSteamMP into the My Summer Car Mods folder.
REM
REM The build itself is in build.cmd; this only copies the result and checks that the
REM preconditions (MSCLoader installed, game not running) hold.
setlocal
set "ROOT=%~dp0"
set "OUT=%ROOT%bin\Release\MSCSteamMP.dll"

set "GAME=%~1"
if "%GAME%"=="" set "GAME=D:\Steam\steamapps\common\My Summer Car"

if not exist "%GAME%\mysummercar.exe" (
  echo [ERROR] "%GAME%\mysummercar.exe" not found.
  exit /b 1
)

echo [1/3] Building...
call "%ROOT%build.cmd" "%GAME%"
if errorlevel 1 exit /b 1

if not exist "%OUT%" (
  echo [ERROR] Expected output "%OUT%" was not produced.
  exit /b 1
)

echo [2/3] Checking MSCLoader...
if not exist "%GAME%\mysummercar_Data\Managed\MSCLoader.dll" (
  echo [ERROR] MSCLoader.dll not found. Install MSCLoader first.
  exit /b 1
)

echo [3/3] Installing...
if not exist "%GAME%\Mods" mkdir "%GAME%\Mods"

REM CANLI KANIT: dogrudan `copy` csc'in az once yazdigi DLL'i kopyalarken
REM antivirusun tarama kilidi tutmasi yuzunden arada basarisiz oluyor
REM ("islem baska bir surec tarafindan kullaniliyor"). Dosyanin KENDISI
REM kilitli degil: hedefi yeniden adlandirmak calisiyor. Bu yuzden once
REM gecici bir ada kopyalayip sonra yerine taşıyoruz; bu, hedefte
REM kullanımda olmadığını da dogrular.
set "STAGE=%GAME%\Mods\_smp_stage.dll"
set /a DEPLOY_TRIES=0
:install
copy /Y "%OUT%" "%STAGE%" >nul 2>&1
if not errorlevel 1 (
  move /Y "%STAGE%" "%GAME%\Mods\MSCSteamMP.dll" >nul
  if not errorlevel 1 goto :installed
)
del /Q "%STAGE%" >nul 2>&1
set /a DEPLOY_TRIES+=1
if %DEPLOY_TRIES% lss 8 (
  ping -n 2 127.0.0.1 >nul
  goto :install
)
echo [ERROR] Copy failed. Is the game running?
exit /b 1
:installed

echo.
echo Done: "%GAME%\Mods\MSCSteamMP.dll"
echo Both players need the same file. Host: 'smp host', friend: Steam invite accept or 'smp join ^<id^>'.
endlocal