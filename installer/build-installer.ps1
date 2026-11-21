# V-Talk kurulum programını (installer\out\VTalk-Setup.exe) üretir.
# Admin bilgisayarında, admin Windows hesabıyla çalıştırın: organizasyon açık anahtarı bu bilgisayardaki
# admin anahtarından otomatik alınır ve kuruluma gömülür (org.pub'ı ayrıca dağıtmak gerekmez).
#   .\installer\build-installer.ps1
#   .\installer\build-installer.ps1 -AdminHost ADMIN-PC -Networks "10.20.30.0/24,10.20.40.0/24"
#   .\installer\build-installer.ps1 -SkipPublish        # dist\VTalk.exe zaten güncelse
# Parametre verilmezse installer\build-installer.local.json okunur (depoya girmez; örnek: build-installer.local.example.json).
param(
    [string]$AdminHost = '',
    [string]$Networks = '',
    [switch]$SkipPublish
)

$ErrorActionPreference = 'Stop'

$localConfig = Join-Path $PSScriptRoot 'build-installer.local.json'
if (Test-Path $localConfig) {
    if ((Get-Item $localConfig).Length -gt 8KB) { throw "Yerel ayar dosyası çok büyük: $localConfig" }
    $local = Get-Content $localConfig -Raw -Encoding UTF8 | ConvertFrom-Json
    if (-not $PSBoundParameters.ContainsKey('AdminHost') -and $local.adminHost) { $AdminHost = [string]$local.adminHost }
    if (-not $PSBoundParameters.ContainsKey('Networks') -and $local.networks) { $Networks = (@($local.networks) | ForEach-Object { [string]$_ }) -join ',' }
    Write-Host "Yerel ayarlar kullanılıyor: $localConfig" -ForegroundColor Cyan
}
if (-not $AdminHost) { $AdminHost = $env:COMPUTERNAME }
$root = Split-Path $PSScriptRoot -Parent
$dist = Join-Path $root 'dist'
$out = Join-Path $PSScriptRoot 'out'
$work = Join-Path $out 'work'

$iscc = @(
    "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
    "$env:ProgramFiles\Inno Setup 6\ISCC.exe",
    "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe"
) | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $iscc) { throw "Inno Setup 6 bulunamadı: https://jrsoftware.org/isdl.php" }

if ($AdminHost -notmatch '^(?=.{1,253}$)([A-Za-z0-9]([A-Za-z0-9-]{0,61}[A-Za-z0-9])?)(\.[A-Za-z0-9]([A-Za-z0-9-]{0,61}[A-Za-z0-9])?)*$') {
    throw "Geçersiz admin bilgisayar adı: $AdminHost"
}
$Networks = (($Networks -split '[,;\s]+') | Where-Object { $_ }) -join ','
foreach ($n in ($Networks -split ',' | Where-Object { $_ })) {
    if ($n -notmatch '^(\d{1,3}\.){3}\d{1,3}(/([1-9]|[12]\d|3[0-2]))?$') { throw "Geçersiz ağ: $n" }
}

if (-not $SkipPublish) {
    Get-Process VTalk -ErrorAction SilentlyContinue | Where-Object { $_.Path -like "$dist*" } | ForEach-Object {
        Write-Host "dist\VTalk.exe çalışıyor, kapatılıyor..." -ForegroundColor Yellow
        $_ | Stop-Process -Force
        $_.WaitForExit(10000) | Out-Null
    }
    Start-Sleep -Milliseconds 500
    & (Join-Path $root 'publish.ps1')
}
$exe = Join-Path $dist 'VTalk.exe'
if (-not (Test-Path $exe)) { throw "dist\VTalk.exe bulunamadı. Önce publish.ps1 çalıştırın." }

New-Item -ItemType Directory -Force -Path $work | Out-Null
$orgPub = Join-Path $work 'org.pub'
Remove-Item $orgPub -ErrorAction SilentlyContinue

# 1) Bu bilgisayardaki admin anahtarından (en güvenilir kaynak).
$p = Start-Process -FilePath $exe -ArgumentList @('--export-org-pub', "`"$orgPub`"") -Wait -PassThru
switch ($p.ExitCode) {
    0 { Write-Host "Organizasyon anahtarı bu bilgisayardaki admin anahtarından alındı." -ForegroundColor Cyan }
    4 {
        throw ("Bu bilgisayardaki admin anahtarı, V-Talk'ın şu an güvendiği organizasyon anahtarından farklı. " +
               "Yanlış organizasyonla kurulum üretmemek için durduruldu. Admin panelindeki parmak izini kontrol edin.")
    }
    2 {
        # 2) Admin anahtarı yoksa proje kökündeki org.pub (Admin panelinden dışa aktarılmış).
        $rootPub = Join-Path $root 'org.pub'
        if (Test-Path $rootPub) {
            Copy-Item $rootPub $orgPub -Force
            Write-Host "Bu bilgisayarda admin anahtarı yok; proje kökündeki org.pub kullanılıyor." -ForegroundColor Yellow
        } else {
            # Anahtarsız kurulum, istemcilerin ağda ilk gördükleri dizine güvenmesi (TOFU) demektir: ağdaki bir
            # saldırgan admin'den önce davranırsa organizasyonun admini olur. Bu yüzden anahtarsız derleme yapılmaz.
            throw ("Bu bilgisayarda admin anahtarı yok. Installer'ı admin bilgisayarında admin Windows hesabıyla derleyin " +
                   "(önce Yönetim → Admin Girişi ile organizasyonu oluşturun) veya proje köküne org.pub koyun.")
        }
    }
    default { throw "Organizasyon anahtarı alınamadı (çıkış kodu $($p.ExitCode))." }
}

$isccArgs = @("/DSourceDir=$dist", "/DDefaultAdminHost=$AdminHost", "/DDefaultNetworks=$Networks", "/O$out")
if (-not (Test-Path $orgPub)) { throw "Organizasyon anahtarı (org.pub) hazırlanamadı; kurulum anahtarsız üretilmez." }
$pem = Get-Content $orgPub -Raw
$spki = [Convert]::FromBase64String(($pem -replace '-----[^-]+-----', '' -replace '\s', ''))
$sha = [System.Security.Cryptography.SHA256]::Create()
try { $hash = $sha.ComputeHash($spki) } finally { $sha.Dispose() }
$hex = -join ($hash[0..11] | ForEach-Object { $_.ToString('X2') })
$fingerprint = (($hex -split '(.{4})') | Where-Object { $_ }) -join '-'
$isccArgs += @("/DOrgPub=$orgPub", "/DOrgFingerprint=$fingerprint")
Write-Host "Organizasyon parmak izi: $fingerprint" -ForegroundColor Cyan

$version = ([xml](Get-Content (Join-Path $root 'src\VTalk\VTalk.csproj'))).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
if ($version) { $isccArgs += "/DAppVersion=$version" }

& $iscc @isccArgs (Join-Path $PSScriptRoot 'VTalk.iss')
if ($LASTEXITCODE -ne 0) { throw "Inno Setup derlemesi başarısız (çıkış kodu $LASTEXITCODE)." }
Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue

$setup = Join-Path $out 'VTalk-Setup.exe'
Write-Host ""
Write-Host "Kurulum hazır: $setup  ($([math]::Round((Get-Item $setup).Length / 1MB, 1)) MB)" -ForegroundColor Green
Write-Host "Varsayılan admin bilgisayarı: $AdminHost" -ForegroundColor Green
Write-Host "Varsayılan uzak ağlar: $(if ($Networks) { $Networks } else { '(yok)' })" -ForegroundColor Green
