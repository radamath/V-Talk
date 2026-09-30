# V-Talk'u tek dosyalık Windows uygulaması (VTalk.exe) olarak "dist" klasörüne derler.
#   .\publish.ps1                      -> .NET kurulu olmayan bilgisayarlarda da çalışan tek exe (~70 MB)
#   .\publish.ps1 -FrameworkDependent  -> küçük exe (~5 MB), hedefte .NET 8 Desktop Runtime gerekir
#   .\publish.ps1 -NoObfuscate         -> karartmasız (yalnızca sorun ayıklama için; dağıtmayın)
# Varsayılan olarak kod Obfuscar ile karartılır ve üretilen exe "--selftest" ile uçtan uca sınanır;
# sınama başarısız olursa paket üretilmiş sayılmaz.
param([switch]$FrameworkDependent, [switch]$NoObfuscate)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$project = Join-Path $root 'src\VTalk'
$dist = Join-Path $root 'dist'

# Dağıtılmış güven kökü (org.pub) varsa paket yenilenirken kaybolmasın.
$orgPubBackup = $null
$existingOrgPub = Join-Path $dist 'org.pub'
if (Test-Path $existingOrgPub) {
    $orgPubBackup = Join-Path $env:TEMP ('vtalk-org-' + [guid]::NewGuid().ToString('N') + '.pub')
    Copy-Item $existingOrgPub $orgPubBackup -Force
}
if (Test-Path $dist) { Remove-Item $dist -Recurse -Force }

# Eski (karartılmamış ya da farklı ayarlı) ara çıktılar yeniden kullanılmasın.
foreach ($dir in 'obj\Release', 'bin\Release') {
    $path = Join-Path $project $dir
    if (Test-Path $path) { Remove-Item $path -Recurse -Force }
}

Push-Location $root
try {
    & dotnet tool restore
    if ($LASTEXITCODE -ne 0) { throw "dotnet araçları geri yüklenemedi (Obfuscar)." }
} finally { Pop-Location }

$dotnetArgs = @(
    'publish', (Join-Path $project 'VTalk.csproj'),
    '-c', 'Release', '-r', 'win-x64', '-o', $dist,
    '-p:PublishSingleFile=true',
    '-p:IncludeNativeLibrariesForSelfExtract=true',
    '-p:DebugType=none',
    "-p:Obfuscate=$(if ($NoObfuscate) { 'false' } else { 'true' })"
)
if ($FrameworkDependent) {
    $dotnetArgs += '--self-contained', 'false'
} else {
    $dotnetArgs += '--self-contained', 'true', '-p:EnableCompressionInSingleFile=true'
}

& dotnet @dotnetArgs
if ($LASTEXITCODE -ne 0) { throw "Yayın derlemesi başarısız oldu." }

$exe = Join-Path $dist 'VTalk.exe'
if (-not (Test-Path $exe)) { throw "VTalk.exe üretilemedi." }

# Tek dosyaya gömülmeyen ve dağıtıma girmemesi gereken hata ayıklama / eşleme artıkları temizlenir.
Get-ChildItem $dist -Include '*.pdb', '*.xml', 'Mapping.txt' -Recurse -ErrorAction SilentlyContinue |
    Remove-Item -Force -ErrorAction SilentlyContinue

# Kurulum ve güvenlik duvarı betikleri pakete eklenir.
Copy-Item (Join-Path $root 'tools\kur.ps1') $dist -Force
Copy-Item (Join-Path $root 'tools\firewall-kurallari.ps1') $dist -Force

# Güven kökü: önce kök dizindeki org.pub, yoksa önceki paketten korunan org.pub.
$rootOrgPub = Join-Path $root 'org.pub'
if (Test-Path $rootOrgPub) {
    Copy-Item $rootOrgPub $dist -Force
    Write-Host "Organizasyon güven kökü (org.pub) pakete eklendi." -ForegroundColor Cyan
} elseif ($orgPubBackup) {
    Copy-Item $orgPubBackup (Join-Path $dist 'org.pub') -Force
    Write-Host "Önceki paketteki org.pub korundu." -ForegroundColor Cyan
}
if ($orgPubBackup) { Remove-Item $orgPubBackup -Force -ErrorAction SilentlyContinue }

# Üretilen exe'yi ağa hiçbir şey yaymadan, geçici profilde uçtan uca sına.
Write-Host "Üretilen exe --selftest ile sınanıyor..." -ForegroundColor Cyan
$report = Join-Path $env:TEMP ('vtalk-selftest-' + [guid]::NewGuid().ToString('N') + '.txt')
$proc = Start-Process $exe -ArgumentList "--selftest `"$report`"" -Wait -PassThru
if (Test-Path $report) {
    Get-Content $report -Encoding UTF8 | Write-Host
    Remove-Item $report -Force -ErrorAction SilentlyContinue
}
if ($proc.ExitCode -ne 0) {
    throw "Öz sınama başarısız oldu (çıkış kodu $($proc.ExitCode)); paket güvenilir değil."
}

$size = [math]::Round((Get-Item $exe).Length / 1MB, 1)
Write-Host ""
Write-Host "Paket hazır: $dist  (VTalk.exe ~$size MB)" -ForegroundColor Green
if (-not $NoObfuscate) { Write-Host "Kod karartıldı (Obfuscar) ve öz sınamadan geçti." -ForegroundColor Green }
