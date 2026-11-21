# V-Talk sürüm paketi: imzalı VTalk.exe, imzalı MSI (otomatik güncelleme için) ve imzalı VTalk-Setup.exe üretir.
# Admin bilgisayarında, admin Windows hesabıyla çalıştırın (organizasyon anahtarı ve imza sertifikası bu hesaptadır):
#   .\installer\build-release.ps1
#   .\installer\build-release.ps1 -SkipPublish     # dist\VTalk.exe zaten güncelse
# Admin bilgisayarı ve ağlar installer\build-installer.local.json dosyasından okunur (depoya girmez).
#
# İstemciler güncelleme paketini yalnızca src\VTalk\Security\PackageTrust.cs içindeki yayıncı parmak iziyle
# imzalıysa kurar; bu betik aynı parmak izini o dosyadan okur, iki yer birbirinden ayrışamaz.
param([switch]$SkipPublish)

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$dist = Join-Path $root 'dist'
$out = Join-Path $PSScriptRoot 'out'
$work = Join-Path $out 'work'
$timestampUrl = 'http://timestamp.digicert.com'

# ------------------------------------------------------------------ Ayarlar

$localConfig = Join-Path $PSScriptRoot 'build-installer.local.json'
if (-not (Test-Path $localConfig)) { throw "Kuruluş ayarları yok: $localConfig (örnek: build-installer.local.example.json)" }
if ((Get-Item $localConfig).Length -gt 8KB) { throw "Yerel ayar dosyası çok büyük: $localConfig" }
$local = Get-Content $localConfig -Raw -Encoding UTF8 | ConvertFrom-Json
$adminHost = [string]$local.adminHost
$networks = @($local.networks | ForEach-Object { ([string]$_).Trim() } | Where-Object { $_ })
if ($adminHost -notmatch '^(?=.{1,253}$)([A-Za-z0-9]([A-Za-z0-9-]{0,61}[A-Za-z0-9])?)(\.[A-Za-z0-9]([A-Za-z0-9-]{0,61}[A-Za-z0-9])?)*$') {
    throw "Geçersiz admin bilgisayar adı: $adminHost"
}
foreach ($n in $networks) {
    if ($n -notmatch '^(\d{1,3}\.){3}\d{1,3}(/([1-9]|[12]\d|3[0-2]))?$') { throw "Geçersiz ağ: $n" }
}

$trustSource = Get-Content (Join-Path $root 'src\VTalk\Security\PackageTrust.cs') -Raw
if ($trustSource -notmatch 'PublisherThumbprint\s*=\s*"([0-9A-Fa-f]{40})"') { throw "PackageTrust.cs içinde yayıncı parmak izi bulunamadı." }
$thumbprint = $Matches[1].ToUpperInvariant()
$cert = Get-ChildItem Cert:\CurrentUser\My, Cert:\LocalMachine\My -ErrorAction SilentlyContinue |
    Where-Object { $_.Thumbprint -eq $thumbprint -and $_.HasPrivateKey } | Select-Object -First 1
if (-not $cert) { throw "İmza sertifikası ($thumbprint) bu hesapta özel anahtarıyla birlikte yok." }
if ($cert.NotAfter -lt (Get-Date).AddDays(30)) { Write-Host "UYARI: imza sertifikası $($cert.NotAfter.ToString('d')) tarihinde sona eriyor." -ForegroundColor Yellow }

$signtool = Get-ChildItem "${env:ProgramFiles(x86)}\Windows Kits\10\bin" -Recurse -Filter signtool.exe -ErrorAction SilentlyContinue |
    Where-Object { $_.DirectoryName -like '*\x64' } | Sort-Object FullName -Descending | Select-Object -First 1
if (-not $signtool) { throw "signtool.exe bulunamadı (Windows SDK gerekli)." }
$wix = Join-Path $env:USERPROFILE '.dotnet\tools\wix.exe'
if (-not (Test-Path $wix)) { throw "WiX bulunamadı: dotnet tool install --global wix" }

$version = ([xml](Get-Content (Join-Path $root 'src\VTalk\VTalk.csproj'))).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
if (-not $version) { throw "VTalk.csproj içinde sürüm yok." }

function Invoke-Sign([string]$path) {
    # Zaman damgası: sertifika süresi dolsa da imza geçerli kalır.
    & $signtool.FullName sign /sha1 $thumbprint /fd SHA256 /tr $timestampUrl /td SHA256 /d 'V-Talk' $path | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "İmzalanamadı: $path (signtool çıkış kodu $LASTEXITCODE)" }
    Assert-Signature $path
}

function Assert-Signature([string]$path) {
    $sig = Get-AuthenticodeSignature $path
    # İç CA kökü bu bilgisayarda güvenilir olmayabilir (UnknownError); özet doğru ve imzalayan doğru olmalı.
    if ($sig.Status -notin 'Valid', 'UnknownError' -or $sig.SignerCertificate.Thumbprint -ne $thumbprint) {
        throw "İmza doğrulanamadı: $path ($($sig.Status): $($sig.StatusMessage))"
    }
    if (-not $sig.TimeStamperCertificate) { throw "Zaman damgası yok: $path" }
    Write-Host ("İmzalı: {0}  ({1})" -f (Split-Path $path -Leaf), $sig.Status) -ForegroundColor Cyan
}

# ------------------------------------------------------------------ Uygulama

if (-not $SkipPublish) {
    Get-Process VTalk -ErrorAction SilentlyContinue | Where-Object { $_.Path -like "$dist*" } | Stop-Process -Force
    & (Join-Path $root 'publish.ps1')
}
$exe = Join-Path $dist 'VTalk.exe'
if (-not (Test-Path $exe)) { throw "dist\VTalk.exe bulunamadı." }

# Karartma eşlemesi: sahadaki donma/hata günlüklerindeki karartılmış adları çözmek için sürümle saklanır
# (installer\out depoya girmez; publish.ps1 bir sonraki derlemede obj\Release'i sildiği için burada kopyalanır).
$mapping = Get-ChildItem (Join-Path $root 'src\VTalk\obj\Release') -Recurse -Filter Mapping.txt -ErrorAction SilentlyContinue |
    Sort-Object LastWriteTime -Descending | Select-Object -First 1
if ($mapping) {
    Copy-Item $mapping.FullName (Join-Path $out "obfuscation-map-$version.txt") -Force
    Write-Host "Karartma eşlemesi saklandı: obfuscation-map-$version.txt" -ForegroundColor Cyan
}
if ((Get-Item $exe).VersionInfo.ProductVersion -notlike "$version*") { throw "dist\VTalk.exe sürümü $version değil; -SkipPublish olmadan çalıştırın." }
Invoke-Sign $exe

# ------------------------------------------------------------------ Kuruluşa özel dosyalar

if (Test-Path $work) { Remove-Item $work -Recurse -Force }
New-Item -ItemType Directory -Force -Path $work | Out-Null

$orgPub = Join-Path $work 'org.pub'
$p = Start-Process -FilePath $exe -ArgumentList @('--export-org-pub', "`"$orgPub`"") -Wait -PassThru
switch ($p.ExitCode) {
    0 { }
    2 { throw "Bu hesapta admin anahtarı yok; paket organizasyon anahtarı olmadan üretilmez (TOFU riski)." }
    4 { throw "Admin anahtarı, V-Talk'ın güvendiği organizasyon anahtarından farklı; durduruldu." }
    default { throw "Organizasyon anahtarı alınamadı (çıkış kodu $($p.ExitCode))." }
}
$spki = [Convert]::FromBase64String(((Get-Content $orgPub -Raw) -replace '-----[^-]+-----', '' -replace '\s', ''))
$sha = [System.Security.Cryptography.SHA256]::Create()
try { $hash = $sha.ComputeHash($spki) } finally { $sha.Dispose() }
$fingerprint = ((-join ($hash[0..11] | ForEach-Object { $_.ToString('X2') })) -split '(.{4})' | Where-Object { $_ }) -join '-'

$deployJson = Join-Path $work 'deploy.json'
[IO.File]::WriteAllText($deployJson, (@{ adminHost = $adminHost; networks = ($networks -join ',') } | ConvertTo-Json),
    (New-Object System.Text.UTF8Encoding($false)))

# Güvenlik duvarı: yalnızca yerel alt ağ + kuruluş ağları, Etki Alanı ve Özel profiller (Genel ağda kapalı).
$remote = (@('LocalSubnet') + $networks | ForEach-Object { "          <fw:RemoteAddress Value=`"$_`" />" }) -join "`r`n"
$rules = foreach ($fwProfile in 'domain', 'private') {
@"
        <fw:FirewallException Id="VTalkUdp_$fwProfile" Name="V-Talk - Keşif (UDP 49050) - $fwProfile" Program="[#VTalkExe]"
                              Protocol="udp" Port="49050" Profile="$fwProfile">
$remote
        </fw:FirewallException>
        <fw:FirewallException Id="VTalkTcp_$fwProfile" Name="V-Talk - Mesajlaşma (TCP) - $fwProfile" Program="[#VTalkExe]"
                              Protocol="tcp" Profile="$fwProfile">
$remote
        </fw:FirewallException>
"@
}
$firewall = @"
<?xml version="1.0" encoding="utf-8"?>
<Wix xmlns="http://wixtoolset.org/schemas/v4/wxs" xmlns:fw="http://wixtoolset.org/schemas/v4/wxs/firewall">
  <Fragment>
    <ComponentGroup Id="FirewallRules" Directory="INSTALLFOLDER">
      <Component>
        <RegistryValue Root="HKLM" Key="Software\V-Talk" Name="FirewallRules" Type="integer" Value="1" KeyPath="yes" />
$($rules -join "`r`n")
      </Component>
    </ComponentGroup>
  </Fragment>
</Wix>
"@
$firewallWxs = Join-Path $work 'Firewall.wxs'
[IO.File]::WriteAllText($firewallWxs, $firewall, (New-Object System.Text.UTF8Encoding($false)))

# ------------------------------------------------------------------ MSI

$msi = Join-Path $out "VTalk-$version.msi"
Remove-Item $msi, ([IO.Path]::ChangeExtension($msi, '.wixpdb')) -ErrorAction SilentlyContinue
Push-Location $root
try {
    & $wix build (Join-Path $PSScriptRoot 'Package.wxs') $firewallWxs -arch x64 `
        -ext WixToolset.UI.wixext -ext WixToolset.Firewall.wixext -ext WixToolset.Util.wixext `
        -d "AppVersion=$version" -d "SourceDir=$dist" -d "OrgPub=$orgPub" -d "DeployJson=$deployJson" `
        -d "RegisterUpdateCmd=$(Join-Path $PSScriptRoot 'register-update.cmd')" `
        -d "IconPath=$(Join-Path $root 'src\VTalk\Assets\vtalk.ico')" -d "LicenseRtf=$(Join-Path $PSScriptRoot 'License.tr.rtf')" `
        -o $msi
    if ($LASTEXITCODE -ne 0) { throw "MSI derlemesi başarısız (wix çıkış kodu $LASTEXITCODE)." }
} finally { Pop-Location }
Invoke-Sign $msi

# ------------------------------------------------------------------ Kurulum programı (Setup.exe)

& (Join-Path $PSScriptRoot 'build-installer.ps1') -SkipPublish -AdminHost $adminHost -Networks ($networks -join ',')
$setup = Join-Path $out 'VTalk-Setup.exe'
Invoke-Sign $setup

Write-Host ""
Write-Host "Sürüm $version hazır (organizasyon parmak izi $fingerprint):" -ForegroundColor Green
Write-Host "  Otomatik güncelleme / GPO : $msi" -ForegroundColor Green
Write-Host "  Elle kurulum              : $setup" -ForegroundColor Green
Write-Host "Dağıtım: MSI'yı admin bilgisayarına kurun (yayın klasörüne kopyalanır), sonra Yönetim → Güncellemeyi gönder." -ForegroundColor Green
