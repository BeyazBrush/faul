@echo off
REM ---------------------------------------------------------------------
REM mpnpc.dat -> Unity 5.0.0f4 Asset Bundle
REM
REM ONCE kosulur (cikti surerli degildir, sonucu assets\mpnpc olarak tutar):
REM   1) pip install UnityPy
REM   2) python tools\extract_npc_dump.py
REM   3) python tools\extract_textures.py
REM   4) python tools\make_char_data.py --npc "<bodymesh yolu>"
REM   5) BU SCRIPT
REM
REM Unity 5.0.0f4 lisansi gerekli. Editor batchmode'da acilmazsa Unity
REM Personal aktivasyonu yapilmamis demektir (activate_license.cmd).
REM
REM CIKTI: proje kokunde "mpnpc" (AssetBundle), sonra assets\mpnpc'a kopyalanir.
REM Bundle icindeki prefab yolu: assets/mpnpc/mpnpc.prefab
REM ---------------------------------------------------------------------
setlocal enabledelayedexpansion

set UNITY=C:\Program Files\Unity\Hub\Editor\5.0.0f4\Editor\Unity.exe
set PROJ=%~dp0unity\MPNPCBuilder
set LOG=%~dp0unity\build_npc.log
set BUNDLE=%PROJ%\mpnpc
set DAT=D:\MSCSteamMP\extracted_npc\mpnpc.dat

if not exist "%UNITY%" (
  echo HATA: Unity 5.0.0f4 bulunamadi: %UNITY%
  exit /b 1
)

if not exist "%DAT%" (
  echo HATA: %DAT% yok.
  echo Once uret:
  echo   python tools\make_char_data.py --npc "TRAFFIC/VehiclesHighway/VICTRO/LOD/Passengers/Driver/bodymesh"
  exit /b 1
)

REM dokular proje icine (skin katalogundeki HEPSi, 47 adet)
if not exist "%PROJ%\Assets\MPNCTextures" mkdir "%PROJ%\Assets\MPNCTextures"
del /Q "%PROJ%\Assets\MPNCTextures\*.png" >nul 2>&1
copy /Y "D:\MSCSteamMP\extracted_npc\textures\*.png" "%PROJ%\Assets\MPNCTextures\" >nul
echo Dokular kopyalandi: %PROJ%\Assets\MPNCTextures

REM ---- eski Unity sureclerini kapat: projeyi kilitliyorlar ve sonraki
REM ---- calismalari engelliyorlar ("Successfully changed project path"
REM ---- yerine kilit hatasi gelir).
taskkill /F /IM Unity.exe >nul 2>&1
if exist "%BUNDLE%" del "%BUNDLE%"

echo Unity 5.0.0f4 ile bundle insa ediliyor...
echo proje: %PROJ%
echo log  : %LOG%
echo.

powershell -NoProfile -ExecutionPolicy Bypass -Command ^
  "$ErrorActionPreference='Stop';" ^
  "$p = Start-Process -FilePath '%UNITY%' -ArgumentList @('-batchmode','-quit','-nographics','-projectPath','%PROJ%','-executeMethod','BuildNPCBundle.Build','-logFile','%LOG%') -PassThru;" ^
  "if (-not $p.WaitForExit(600000)) { Write-Host 'ZAMAN ASIMI - Unity olduruluyor'; $p | Stop-Process -Force }"

echo.

if not exist "%LOG%" (
  echo HATA: Unity log dosyasi olusmadi.
  exit /b 1
)

findstr /C:"has not been activated" /C:"Machine identification" "%LOG%" >nul 2>&1
if "%ERRORLEVEL%"=="0" (
  echo HATA: Unity LIFSANSI YOK, build calismadi.
  echo   C:\ProgramData\Unity\Unity_lic.ulf bu makineye ait degil.
  echo   Cozum: activate_license.cmd calistir, uretilen .alf dosyasini
  echo          https://license.unity3d.com/manual adresine yukle.
  exit /b 2
)

findstr /C:"BUILD OK" "%LOG%" >nul 2>&1
if not "%ERRORLEVEL%"=="0" (
  echo HATA: Unity build'i BASARISIZ oldu.
  findstr /C:"[MPNPC]" /C:"error CS" "%LOG%"
  exit /b 1
)

if not exist "%BUNDLE%" (
  echo HATA: bundle dosyasi olusmadi: %BUNDLE%
  exit /b 1
)

for %%F in ("%BUNDLE%") do set BSIZE=%%~zF
echo Bundle: %BUNDLE%
echo Boyut : !BSIZE! bayt

REM Unity 5.0.0f4 kalici surecler birakabiliyor; temizle
taskkill /F /IM Unity.exe >nul 2>&1

if not exist "%~dp0assets" mkdir "%~dp0assets"
copy /Y "%BUNDLE%" "%~dp0assets\mpnpc" >nul
if not exist "%~dp0assets\mpnpc" (
  echo HATA: assets\mpnpc kopyalanamadi
  exit /b 1
)
echo Kopyalandi: %~dp0assets\mpnpc

echo.
echo TAMAM. Prefab yolu (PlayerAssetNpc.cs ile eslesmeli):
echo   assets/mpnpc/mpnpc.prefab
endlocal