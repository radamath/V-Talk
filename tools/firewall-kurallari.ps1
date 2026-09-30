# V-Talk için Windows Güvenlik Duvarı gelen bağlantı kurallarını ekler (yönetici olarak çalıştırın).
# Varsayılan olarak yalnızca yerel alt ağdan ve Etki Alanı/Özel ağ profillerinde izin verilir (genel Wi-Fi'de kapalı).
#   .\firewall-kurallari.ps1 -ExePath "C:\Program Files\V-Talk\VTalk.exe"
#   .\firewall-kurallari.ps1 -ExePath ... -RemoteSubnets 10.20.30.0/24,10.20.40.0/24   # VPN/uzak şube alt ağları
#   .\firewall-kurallari.ps1 -ExePath ... -AllowAllSubnets   # her yerden (en gevşek; önerilmez)
#   .\firewall-kurallari.ps1 -Remove
param(
    [string]$ExePath = (Join-Path $PSScriptRoot 'VTalk.exe'),
    [string[]]$RemoteSubnets = @(),
    [switch]$AllowAllSubnets,
    [switch]$Remove
)

$ErrorActionPreference = 'Stop'
$principal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw "Bu betik yönetici olarak çalıştırılmalıdır."
}

Get-NetFirewallRule -Group 'V-Talk' -ErrorAction SilentlyContinue | Remove-NetFirewallRule
if ($Remove) { Write-Host "V-Talk güvenlik duvarı kuralları kaldırıldı."; return }

$ExePath = (Resolve-Path $ExePath).Path
# Normalde yalnızca yerel alt ağ; VPN/uzak şube alt ağları -RemoteSubnets ile eklenir. -AllowAllSubnets her yeri açar.
$remote = if ($AllowAllSubnets) {
    'Any'
} elseif ($RemoteSubnets.Count -gt 0) {
    @('LocalSubnet') + ($RemoteSubnets | ForEach-Object { $_.Trim() } | Where-Object { $_ })
} else {
    'LocalSubnet'
}
$common = @{ Group = 'V-Talk'; Direction = 'Inbound'; Action = 'Allow'; Profile = 'Domain,Private'; Program = $ExePath; RemoteAddress = $remote }

New-NetFirewallRule @common -DisplayName 'V-Talk - Keşif (UDP 50505)' -Protocol UDP -LocalPort 50505 | Out-Null
New-NetFirewallRule @common -DisplayName 'V-Talk - Mesajlaşma (TCP)' -Protocol TCP | Out-Null

Write-Host "Kurallar eklendi: $ExePath (uzak adres: $($remote -join ', '))" -ForegroundColor Green
