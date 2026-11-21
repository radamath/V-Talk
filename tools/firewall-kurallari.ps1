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

# -File ile çağrıldığında liste tek metin olarak gelebilir; virgül/noktalı virgül/boşlukla ayrılır ve doğrulanır.
$subnets = @($RemoteSubnets | ForEach-Object { $_ -split '[,;\s]+' } | Where-Object { $_ })
foreach ($s in $subnets) {
    if ($s -notmatch '^(\d{1,3}\.){3}\d{1,3}(/(\d|[12]\d|3[0-2]))?$') { throw "Geçersiz alt ağ: $s" }
    $ip = ($s -split '/')[0]
    if (($ip -split '\.') | Where-Object { [int]$_ -gt 255 }) { throw "Geçersiz alt ağ: $s" }
}

# Normalde yalnızca yerel alt ağ; VPN/uzak şube alt ağları -RemoteSubnets ile eklenir. -AllowAllSubnets her yeri açar.
$remote = if ($AllowAllSubnets) {
    'Any'
} elseif ($subnets.Count -gt 0) {
    @('LocalSubnet') + $subnets
} else {
    'LocalSubnet'
}
$common = @{ Group = 'V-Talk'; Direction = 'Inbound'; Action = 'Allow'; Profile = 'Domain,Private'; Program = $ExePath; RemoteAddress = $remote }

New-NetFirewallRule @common -DisplayName 'V-Talk - Keşif (UDP 49050)' -Protocol UDP -LocalPort 49050 | Out-Null
New-NetFirewallRule @common -DisplayName 'V-Talk - Mesajlaşma (TCP)' -Protocol TCP | Out-Null

Write-Host "Kurallar eklendi: $ExePath (uzak adres: $($remote -join ', '))" -ForegroundColor Green
