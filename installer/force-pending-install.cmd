@echo off
rem TAKILI istemci kurtarma: pending MSI'yi dogrulamali SYSTEM yolu ile kurar.
rem Yonetici CMD: force-pending-install.cmd
rem Uzaktan: psexec \\PC -s cmd /c "\\paylasim\force-pending-install.cmd"
rem
rem msiexec pending klasorundeki dosyaya dogrudan calistirilmaz: o klasor kullanicilara yazilabilir.
rem VTalk.exe --apply-update paketi yonetici klasorune kopyalar, imzasini dogrular ve kopyayi kurar.

setlocal EnableExtensions
set "EXE=%ProgramFiles%\V-Talk\VTalk.exe"
set "MSI=%ProgramData%\VTalk\update\pending\VTalk.msi"

if not exist "%MSI%" (
  echo Pending MSI yok: %MSI%
  echo Bunun yerine: msiexec /i "VTalk-x.y.z.msi" /qn
  exit /b 1
)
if not exist "%EXE%" (
  echo VTalk.exe bulunamadi: %EXE%
  exit /b 1
)

"%EXE%" --apply-update
set ERR=%ERRORLEVEL%
echo apply-update=%ERR% (ayrinti: "%ProgramFiles%\V-Talk\update-staging\apply.log")
exit /b %ERR%
