@echo off
REM ---------------------------------------------------------------------
REM Unity 5.0.0f4 lisans aktivasyonu (manuel yol, Unity hesabi gerekmez
REM sadece bir kez web'e yukleyip indirme).
REM
REM SORUN
REM   Log:  "Machine identification is invalid for current license"
REM         HF2M5PQ5NQHOUZSN != 00000_00_000000_00_007C71_08_293920_B1.4
REM   Sebep: C:\ProgramData\Unity\Unity_lic.ulf icindeki makine baglamasi
REM         50:ee:32:ae:fe:56, ama bu makinenin MAC'i 6E-5C-9C-25-44-E7.
REM         Lisans baska bir makine/makine degistirilmisken uretilmis.
REM   NOT: Sistem saatini geri almak COZUM DEGIL; sorun saat degil, MAC.
REM
REM YOL
REM   1) Bu scripti calistir -> Unity.alf uretilir
REM   2) https://license.unity3d.com/manual adresine gir, .alf'i yukle
REM   3) Inen .ulf dosyasini asagidaki klasore kopyala:
REM        C:\ProgramData\Unity\Unity_lic.ulf
REM   4) build_bundle.cmd calistir
REM ---------------------------------------------------------------------
setlocal

set UNITY=C:\Program Files\Unity\Hub\Editor\5.0.0f4\Editor\Unity.exe
set WORK=%~dp0unity\license
set LOG=%WORK%\activation.log

if not exist "%UNITY%" (
  echo HATA: Unity 5.0.0f4 bulunamadi: %UNITY%
  exit /b 1
)

if not exist "%WORK%" mkdir "%WORK%"

echo Unity 5.0.0f4 icin manuel aktivasyon dosyasi uretiliyor...
echo klasor: %WORK%

"%UNITY%" -batchmode -quit -nographics ^
  -createManualActivationFile ^
  -logFile "%LOG%"

echo.
if exist "%WORK%\Unity_v5.x.alf" (
  echo TAMAM: %WORK%\Unity_v5.x.alf uretildi
) else (
  for %%F in ("%WORK%\*.alf") do echo uretilen dosya: %%F
)

echo.
echo SONRAKI ADIMLAR
echo  1) Tarayicida ac:  https://license.unity3d.com/manual
echo  2) Unity_v5.x.alf dosyasini yukle
echo  3) Inen .ulf dosyasini su klasore kopyala:
echo        C:\ProgramData\Unity\Unity_lic.ulf
echo     (yoksa klasoru elle olustur)
echo  4) Sonra su komutu calistir:  build_bundle.cmd
echo.
echo Aktivasyon gunlugu: %LOG%
endlocal