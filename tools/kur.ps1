# V-Talk kurulumu.
#  - Yönetici olarak çalıştırılırsa: C:\Program Files\V-Talk'a kurar, tüm kullanıcılar için Başlat menüsü
#    kısayolu oluşturur, güvenlik duvarı kurallarını ekler ve (varsa) org.pub'ı güven kökü olarak dağıtır.
#  - Normal kullanıcı olarak: %LOCALAPPDATA%\Programs\V-Talk'a kurar (güvenlik duvarı izni ilk açılışta sorulur).
param([switch]$NoStart)

$ErrorActionPreference = 'Stop'
$src = $PSScriptRoot
$exe = Join-Path $src 'VTalk.exe'
if (-not (Test-Path $exe)) { throw "VTalk.exe bu betikle aynı klasörde olmalı." }

$principal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
$isAdmin = $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)

if ($isAdmin) {
    $target = Join-Path $env:ProgramFiles 'V-Talk'
    $startMenu = Join-Path $env:ProgramData 'Microsoft\Windows\Start Menu\Programs'
} else {
    $target = Join-Path $env:LOCALAPPDATA 'Programs\V-Talk'
    $startMenu = Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs'
}

Get-Process VTalk -ErrorAction SilentlyContinue | Where-Object { $_.Path -like "$target*" } | Stop-Process -Force
New-Item -ItemType Directory -Force -Path $target | Out-Null
Copy-Item $exe $target -Force

$orgPub = Join-Path $src 'org.pub'
if (Test-Path $orgPub) {
    Copy-Item $orgPub $target -Force
    Write-Host "Organizasyon güven kökü (org.pub) kuruldu." -ForegroundColor Cyan
}

if ($isAdmin) {
    # V-Talk, güven kökü dosyasını yalnızca sahibi ve yazma yetkilileri Administrators/SYSTEM ise kabul eder.
    & icacls.exe $target /setowner '*S-1-5-32-544' /T /C /Q | Out-Null
    & icacls.exe $target /reset /T /C /Q | Out-Null
}

$shell = New-Object -ComObject WScript.Shell
$lnk = $shell.CreateShortcut((Join-Path $startMenu 'V-Talk.lnk'))
$lnk.TargetPath = Join-Path $target 'VTalk.exe'
$lnk.WorkingDirectory = $target
$lnk.Description = 'V-Talk LAN Messenger'
$lnk.Save()

if ($isAdmin) {
    & (Join-Path $src 'firewall-kurallari.ps1') -ExePath (Join-Path $target 'VTalk.exe')
} else {
    Write-Host "Not: Yönetici olmadığınız için güvenlik duvarı kuralı eklenmedi. İlk açılışta Windows izin isteyebilir." -ForegroundColor Yellow
}

Write-Host "V-Talk kuruldu: $target" -ForegroundColor Green
if (-not $NoStart) { Start-Process (Join-Path $target 'VTalk.exe') }
