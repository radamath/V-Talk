@echo off
rem PowerShell betik politikasini sistem genelinde degistirmeden, yalnizca bu calistirma icin izin verir.
rem Parametreler aynen aktarilir, ornek: build-installer.cmd -Networks "10.20.30.0/24"
rem Parametre verilmezse installer\build-installer.local.json kullanilir (depoya girmez).
chcp 65001 >nul
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0build-installer.ps1" %*
set RC=%ERRORLEVEL%
if not "%RC%"=="0" echo. & echo Derleme basarisiz (kod %RC%).
if "%~1"=="" pause
exit /b %RC%
