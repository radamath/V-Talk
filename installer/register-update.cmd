@echo off
rem MSI CustomAction (SYSTEM, deferred): yayimlanan paketi kopyalar, guncelleme klasorlerinin izinlerini
rem sertlestirir ve VTalkUpdate SYSTEM gorevini kaydeder.
rem Arguman: %1 = kurulan MSI, %2 = urun surumu, %3 = kurulan VTalk.exe
rem
rem Guvenlik: SYSTEM gorevi yalnizca korumali kurulum klasorundeki "VTalk.exe --apply-update" komutunu calistirir.
rem Kullanicilarin yazabildigi pending klasorunden hicbir betik/paket dogrudan calistirilmaz; VTalk.exe paketi
rem yonetici klasorune kopyalayip imzasini dogruladiktan sonra kurar.
setlocal EnableExtensions
if "%~1"=="" exit /b 0
if not exist "%~1" exit /b 0

set "VTALKDIR=%ProgramData%\VTalk"
set "ROOT=%VTALKDIR%\update"
set "DEST=%ROOT%\published"
set "PEND=%ROOT%\pending"
set "LOG=%ROOT%\register.log"
set "EXE=%~3"
if "%EXE%"=="" set "EXE=%ProgramFiles%\V-Talk\VTalk.exe"
set "VTALK_UPDATE_EXE=%EXE%"
set "VTALK_UPDATE_LOG=%LOG%"

rem Ust klasorler yoneticiye ait ve kullanicilara salt okunur olmali; aksi halde bir kullanici alt klasoru
rem bir baglanti noktasiyla (junction) degistirip SYSTEM'in icacls/copy islemlerini baska bir yere yonlendirebilir.
call :securedir "%VTALKDIR%" "*S-1-5-32-545:(OI)(CI)RX" || exit /b 0
call :securedir "%ROOT%" "*S-1-5-32-545:(OI)(CI)RX" || exit /b 0
call :securedir "%DEST%" "*S-1-5-32-545:(OI)(CI)RX" || exit /b 0
rem pending: istemci indirmeyi kullanici olarak yapar. Kullanicilar klasorde dosya olusturup alt ogeleri
rem degistirebilir, ancak klasorun kendisini silemez/yeniden adlandiramaz.
call :securedir "%PEND%" "*S-1-5-32-545:(OI)(CI)(IO)M" || exit /b 0
icacls "%PEND%" /grant *S-1-5-32-545:(RX,WD,AD) >nul 2>&1 || exit /b 0

>>"%LOG%" echo [%DATE% %TIME%] register-update start msi="%~1" ver=%~2 exe="%EXE%"

copy /Y "%~1" "%DEST%\VTalk.msi" >>"%LOG%" 2>&1
if errorlevel 1 (
  >>"%LOG%" echo copy published FAILED
  exit /b 0
)
> "%DEST%\version.txt" echo %~2

rem Eski surumlerin SYSTEM gorevine calistirttigi, kullanicilarin yazabildigi betikler.
del /f /q "%PEND%\install-pending.cmd" "%PEND%\launch-vtalk.ps1" "%PEND%\apply.cmd" 2>nul

rem Zamanlayicisiz SYSTEM gorevi: eylem = korumali VTalk.exe --apply-update.
powershell -NoProfile -ExecutionPolicy Bypass -Command "try { $ErrorActionPreference='Stop'; $exe=$env:VTALK_UPDATE_EXE; $log=$env:VTALK_UPDATE_LOG; function W([string]$m){ Add-Content -LiteralPath $log -Value $m }; Unregister-ScheduledTask -TaskName 'VTalkUpdate' -Confirm:$false -ErrorAction SilentlyContinue; $a=New-ScheduledTaskAction -Execute $exe -Argument '--apply-update'; $p=New-ScheduledTaskPrincipal -UserId 'SYSTEM' -LogonType ServiceAccount -RunLevel Highest; $s=New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -StartWhenAvailable -ExecutionTimeLimit (New-TimeSpan -Minutes 30) -MultipleInstances IgnoreNew; Register-ScheduledTask -TaskName 'VTalkUpdate' -Action $a -Principal $p -Settings $s -Force | Out-Null; $svc=New-Object -ComObject Schedule.Service; $svc.Connect(); $svc.GetFolder('\').GetTask('VTalkUpdate').SetSecurityDescriptor('D:(A;;FA;;;BA)(A;;FA;;;SY)(A;;FRFX;;;AU)',0); W 'VTalkUpdate registered (VTalk.exe --apply-update)' } catch { Add-Content -LiteralPath $env:VTALK_UPDATE_LOG -Value ('task FAILED: '+$_.Exception.Message); exit 1 }"
if errorlevel 1 (
  >>"%LOG%" echo PowerShell task failed; ONCE fallback
  schtasks /Create /F /TN VTalkUpdate /SC ONCE /ST 00:00 /SD 01/01/2099 /RU SYSTEM /RL HIGHEST /TR "\"%EXE%\" --apply-update" >>"%LOG%" 2>&1
)

schtasks /Query /TN VTalkUpdate /FO LIST >>"%LOG%" 2>&1
>>"%LOG%" echo [%DATE% %TIME%] register-update done
exit /b 0

rem :securedir <klasor> <Users ACE>
rem Baglanti noktasini kaldirir, klasoru olusturur, sahibini Administrators yapar ve ACL'yi yeniden kurar.
:securedir
fsutil reparsepoint query "%~1" >nul 2>&1 && rmdir "%~1"
fsutil reparsepoint query "%~1" >nul 2>&1 && exit /b 1
if not exist "%~1\" mkdir "%~1" || exit /b 1
icacls "%~1" /setowner *S-1-5-32-544 >nul 2>&1 || exit /b 1
icacls "%~1" /inheritance:r /grant:r *S-1-5-18:(OI)(CI)F *S-1-5-32-544:(OI)(CI)F %~2 >nul 2>&1 || exit /b 1
exit /b 0
