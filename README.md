# V-Talk — LAN Messenger

Sunucu gerektirmeyen, eşler arası (P2P), uçtan uca şifreli Windows yerel ağ mesajlaşma uygulaması.
MSN Messenger / LAN Messenger tarzı arayüz; .NET 8 + WPF. Güncel sürüm: **1.4.5**.

## Özellikler

### Mesajlaşma
- **Anında keşif:** Uygulamayı çalıştıran herkes, aynı ağdaki herkesin listesinde saniyeler içinde görünür (imzalı UDP duyuruları).
- **Çevrimiçi / çevrimdışı liste:** Daha önce görülen herkes listede kalır; çevrimdışılar soluk görünür. Durumlar: Çevrimiçi, Meşgul (sessiz), Dışarıda (boşta kalınca otomatik).
- **Birebir ve grup sohbeti:** Grup oluşturma, sohbete kişi davet etme, `@ad` ve `@all` ile etiketleme (grupta bildirim yalnızca etiketlenene gider). Üyeleri yalnızca grubu kuran çıkarabilir.
- **Teslim ve okundu tiki:** Giden mesajın sağ altında; herkese teslim edilince soluk ✓, herkes okuyunca mavi ✓. Grupta tikin üzerine gelince "Okuyan 2/4" gibi bilgi görünür.
- **Yazıyor göstergesi:** "X yazıyor…" (grupta birden çok kişi için de).
- **Yanıtla / alıntıla:** Mesaja sağ tık → **Yanıtla**. Yanıtın üstünde alıntı kutusu görünür; tıklayınca asıl mesaja gider.
- **Mesaj arama:** Sohbet içinde **Ctrl+F**, tüm sohbetlerde **Ctrl+Shift+F** (veya *Messenger → Mesajlarda ara…*). Türkçe harf duyarsız (İ/i, I/ı); dosya adlarında da arar. Sonuca çift tıklayınca mesaj açılır ve sözcük işaretlenir.
- **Tüm sohbet seçilebilir:** Ad, saat ve mesajlar birlikte sürükleyerek seçilir; **Ctrl+A** tüm sohbeti seçer, **Ctrl+C** satır düzeniyle kopyalar.
- **Çevrimdışı kuyruk:** Karşı taraf çevrimdışıysa 1:1 mesajlar kuyruğa alınır. Grupta ulaşılamayan üyeye, geri geldiğinde teslim almadığı mesajların hepsi sırayla gönderilir (son 14 gün, en fazla 300 mesaj).
- **Dosya gönderme:** Düğme, sürükle-bırak veya Ctrl+V. Kabul et / reddet / iptal, ilerleme çubuğu, SHA-256 bütünlük kontrolü.
- **Resimler sohbet içinde:** png, jpg, gif, bmp, tif doğrudan sohbet penceresinde görünür; panodan ekran görüntüsü yapıştırılabilir.
- **Titreşim (nudge):** Karşı tarafın penceresini sallar.
- **Bildirimler:** 12 hazır bildirim sesi (Ayarlar → Değiştir… ile dinleyerek seçilir) veya kendi `.wav` dosyanız; Windows bildirimi, görev çubuğunda yanıp sönme, tepsi simgesi.
- **Yerel geçmiş:** Konuşmalar yalnızca tarafların kendi bilgisayarında, şifreli olarak saklanır.

### Yönetim
- **Roller:** Admin, Birim Yöneticisi, Kullanıcı. Birim yöneticisi kendi biriminin yöneticisidir.
- **Birim grupları:** Kişiler çalıştıkları birime göre gruplanır. Dizinde olmayan (doğrulanmamış) kişiler gizlenir; admin bunları **Yönetim** menüsünden gösterip doğrulayabilir.
- **Admin paneli:** Kişi doğrulama, rol ve birim atama, sıkı mod (yalnızca dizindeki kişiler mesajlaşır), uzak ağlar (VPN/şubeler), org.pub ve admin anahtarı dışa aktarma.
- **Duyurular:** Admin tüm kullanıcılara, Birim Yöneticisi kendi birimine duyuru gönderir (açılır pencere + ses + bildirim).
- **Uzaktan güncelleme:** Admin bilgisayarından tüm istemcilere veya tek kişiye güncelleme gönderilir; istemcide uygulama kapanır, yeni sürüm kurulur ve kendiliğinden açılır. Sonuç admin panelinde görünür.

## Derleme ve test

Gereksinim: .NET 8 SDK.

```powershell
dotnet test                        # birim ve uçtan uca testler
.\publish.ps1                      # dist\VTalk.exe: .NET gerektirmeyen tek dosya (~65 MB), karartılmış ve öz sınamalı
.\publish.ps1 -FrameworkDependent  # küçük exe, hedefte .NET 8 Desktop Runtime gerekir
```

`publish.ps1` üretilen exe'yi `--selftest` ile ağa hiçbir şey yaymadan uçtan uca sınar; sınama başarısızsa paket üretilmiş sayılmaz.

## Sürüm paketi (imzalı MSI + kurulum programı)

**Admin bilgisayarında, admin Windows hesabıyla** tek komut:

```powershell
.\installer\build-release.ps1                 # derler, imzalar, MSI ve Setup üretir
.\installer\build-release.ps1 -SkipPublish    # dist\VTalk.exe zaten güncelse
```

Gereksinimler: [WiX Toolset](https://wixtoolset.org/) (`dotnet tool install --global wix`; UI, Firewall ve Util uzantıları), [Inno Setup 6](https://jrsoftware.org/isdl.php), Windows SDK (`signtool`) ve bu hesapta özel anahtarıyla birlikte kurulu kod imzalama sertifikası.

Üretilenler (`installer\out\`, depoya girmez):

| Dosya | Kullanım |
|------|----------|
| `VTalk-<sürüm>.msi` | Uzaktan güncelleme ve GPO ile toplu dağıtım |
| `VTalk-Setup.exe` | Elle kurulum (tüm kullanıcılar veya yalnızca geçerli kullanıcı) |
| `obfuscation-map-<sürüm>.txt` | Sahadaki günlüklerde karartılmış adları çözmek için eşleme |

Betik şunları yapar:
- Exe, MSI ve Setup'ı zaman damgalı olarak imzalar ve her imzayı uygulamanın güvendiği yayıncı parmak iziyle (`PackageTrust.cs`) doğrular.
- Organizasyon açık anahtarını bu bilgisayardaki admin anahtarından alıp pakete gömer. **Anahtar yoksa paket üretilmez:** anahtarsız istemci ağda ilk gördüğü dizine güvenir (TOFU) ve ağdaki bir saldırgan admin'den önce davranırsa organizasyonun admini olur.
- Admin bilgisayar adını ve kurulumdaki uzak ağları `installer\build-installer.local.json` dosyasından okur (örnek: `build-installer.local.example.json`; bu dosya depoya girmez). Bunlar exe yanına `deploy.json` olarak yazılır.

Yalnızca kurulum programı gerekiyorsa: `installer\build-installer.cmd` (imzalamaz).

> **Windows uyarıları:** Uzaktan güncelleme sessiz kurulduğu için uyarı göstermez. Elle kurulumda "Bilinmeyen yayımcı" uyarısını kaldırmak için imza sertifikasını veren iç CA sertifikası istemcilere Grup İlkesi ile güvenilir kök olarak dağıtılmalıdır.

## Dağıtım ve güncelleme

1. Yeni MSI'yı **admin bilgisayarına** kurun. Kurulum paketi güncelleme klasörüne de koyar (`C:\ProgramData\VTalk\update\published`).
2. Admin girişi yapın; önce admin panelinde bir kişinin satırındaki **Güncelle** ile tek istemcide deneyin.
3. Sorun yoksa ana menüden **İstemcileri güncelle** ile herkese gönderin.

İstemci paketi indirir, kullanıcıların yazamadığı bir hazırlık klasörüne kopyalar, imzasını ve sürümünü orada doğrular, ardından SYSTEM yetkili zamanlanmış görevle sessizce kurar ve uygulamayı yeniden açar.

- Sessiz kurulum (GPO / toplu dağıtım): `msiexec /i VTalk-<sürüm>.msi /qn` veya `VTalk-Setup.exe /VERYSILENT /ALLUSERS`
- Kaldırma güvenlik duvarı kurallarını da siler; kullanıcı verileri (`%LOCALAPPDATA%\VTalk`) korunur.
- **1.4.0** kurulu istemciler uzaktan güncellenemez (o sürümdeki bir hata nedeniyle); bunlara yeni sürüm bir kez elle kurulmalıdır. Admin paneli bunları ayrıca işaretler.

## Elle kurulum

`dist` klasörünü dağıtıp `kur.ps1`'i çalıştırın. Klasörde `org.pub` yoksa kurulum durur (bkz. *İlk admin kurulumu*, 4. adım).

- **Yönetici olarak:** `C:\Program Files\V-Talk` altına kurar, Başlat menüsü kısayolu ve güvenlik duvarı kurallarını ekler.
- **Normal kullanıcı olarak:** `%LOCALAPPDATA%\Programs\V-Talk` altına kurar; ilk açılışta Windows güvenlik duvarı izin isteyebilir.

## Ağ

> **Önemli:** Güvenlik duvarı gelen bağlantılara izin vermezse kullanıcı başkalarını göremez / mesaj alamaz. Kurulum kuralları kendisi ekler; toplu dağıtımda `firewall-kurallari.ps1` (yönetici) veya GPO ile eşdeğer kurallar kullanılabilir. Varsayılan kurallar yalnızca **yerel alt ağdan** ve tanımlı uzak ağlardan, **Etki Alanı/Özel** ağ profillerinde izin verir.

| Port | Protokol | Amaç |
|------|----------|------|
| 49050 | UDP | Keşif (yayın). 49152 altında; Windows geçici port rezervasyonu bu sınırı kapatamaz. |
| 49051 | TCP | Şifreli mesaj ve dosya aktarımı (doluysa otomatik başka port) |

### Farklı alt ağlar, VPN (OpenVPN / IPsec)

Yayın paketleri yönlendirici ve VPN tünellerini geçmez. Uzak ağlar **Admin paneli → Organizasyon ve Güvenlik → Uzak ağlar** bölümüne tek IP'ler veya CIDR alt ağları olarak yazılır (ör. `10.20.30.0/24, 10.20.40.15`) ve **Kaydet ve Yayınla** ile imzalı dizinle birlikte tüm istemcilere dağıtılır. Liste istemcilerde şifreli saklanır; yalnızca admin değiştirebilir. İstemcinin Ayarlar penceresinde etkin ağlar yalnızca bilgi olarak görünür.

- Kurulumda tanımlanan ağlar (`deploy.json`) ve admin'in yayınladığı ağlar birlikte kullanılır. Admin henüz liste yayınlamadıysa, eski sürümlerde kullanıcının kendi ayarına yazdığı ağlar geçerli kalır.
- Bu ağlar keşif hedefi olur ve genel (Public) ağ engellemesinden muaf tutulur.
- UDP duyurusu tek yönde engellense bile, TCP ile ulaşılabilen kişiler bilinen son adreslerinden kimlik doğrulamalı olarak yoklanır ve çevrimiçi görünür.
- Tanımlı ağların tüm adresleri 60 saniyede bir (ve açılışta, ağ/ayar değişikliğinde) taranır; çevrimiçi kişilere 5 saniyede bir doğrudan duyuru gider.
- **Messenger → Kullanıcıları yenile (F5)** tanımlı ağları hemen tarar ve kişileri hemen yoklar.
- Alt ağ başına en fazla 1024 adres taranır (ör. /22); daha büyük ağları küçük alt ağlara bölün.
- Çevrimiçi görünüp TCP ile ulaşılamayan kişiye (ör. yönlendirme eksik) yeniden denemeler 30 saniyeden 10 dakikaya kadar seyreltilir; günlüğe yalnızca ilk hata ve kurtulma yazılır.

## İlk admin kurulumu

1. Admin olacak kişi **Yönetim → Admin Girişi**'ni açar. Ağda henüz organizasyon yoksa "Organizasyonu oluştur" ekranı gelir.
2. Güçlü bir admin parolası belirlenir. Bu bilgisayarda organizasyon imzalama anahtarı üretilir ve kişi otomatik olarak Admin olur.
3. Admin panelinde birimler oluşturulur, kişiler dizine eklenir, rol ve birim atanır, gerekirse uzak ağlar girilir → **Kaydet ve Yayınla**.
4. **Elle dağıtımda zorunlu:** Admin panelindeki **org.pub dosyasını dışa aktar** ile alınan dosyayı `VTalk.exe`'nin yanına (veya `C:\ProgramData\VTalk\`) koyun. `publish.ps1`, proje kökünde `org.pub` varsa onu `dist`'e kopyalar. Sürüm paketi (`build-release.ps1`) bunu kendisi yapar.
5. Başka bir bilgisayardan da admin girişi gerekiyorsa **Admin anahtarını dışa aktar** (.vtkey, parolayla şifreli) ile alınan dosya o bilgisayarda Admin Girişi ekranından içe aktarılır. `.vtkey` dosyasını ve parolayı güvenli bir yoldan iletin; web sunucusu klasörlerinde veya paylaşılan klasörlerde tutmayın.

## Güvenlik modeli

| Konu | Uygulama |
|------|----------|
| Kimlik | Her kullanıcı için ECDSA P-256 anahtar çifti; kullanıcı kimliği (PeerId) açık anahtarın SHA-256 özetidir, taklit edilemez. Özel anahtar DPAPI ile korunur. |
| Keşif | UDP duyuruları imzalıdır; PeerId–anahtar eşleşmesi, tekrar oynatma koruması, 4 KB paket sınırı. Kaynak IP başına hız sınırı sahte kaynak IP'leriyle sıfırlanamaz; bilinmeyen kimlikler imza doğrulama bütçesinin yalnızca bir kısmını kullanabilir (sahte kimlik seli gerçek eşleri düşüremez). |
| Aktarım | Karşılıklı kimlik doğrulamalı el sıkışma + geçici ECDH (ileri gizlilik) → HKDF → yön başına AES-256-GCM. İstemci, keşifte gördüğü PeerId'yi doğrular (MITM engellenir). |
| Grup üyeliği | Üyelik değişiklikleri alıcıda denetlenir: bilinen grubu yalnızca mevcut üyeler değiştirebilir, başkalarını yalnızca grubu kuran çıkarabilir, kurucu sonradan değiştirilemez. Uzaktan çıkarılmada mesaj geçmişi silinmez. Okundu ve yazıyor bildirimleri de üyelik denetiminden geçer. |
| Güncelleme | SYSTEM görevi kullanıcıların yazabildiği klasörde hiçbir şey çalıştırmaz veya yazmaz (bağlantı noktası / sabit bağlantı saldırılarına karşı). Paket yöneticiye ait hazırlık klasörüne kopyalanır; Authenticode özeti, yayıncı parmak izi, ürün kodu ve sürüm orada doğrulanıp kopya kurulur. Değiştirilmiş paket reddedilir. |
| Yerel geçmiş | SQLite; mesaj içerikleri ve kişi bilgileri AES-256-GCM ile şifreli, anahtar Windows hesabına bağlı (DPAPI). Veri klasörü yalnızca kullanıcıya açık ACL ile korunur. Tüm SQL sorguları parametreli. |
| Yetkiler | Roller ve uzak ağlar organizasyon anahtarıyla **imzalı dizin** olarak dağıtılır. Güven kökü: kurulumla dağıtılan `org.pub` (sürüm paketi, kurulum programı ve `kur.ps1` bunu zorunlu tutar). Duyuru yetkisi alıcı tarafında gönderenin imzalı rolüne göre denetlenir. Farklı organizasyon anahtarı görülürse uyarı gösterilir. |
| Admin anahtarı | PBKDF2-SHA256 (600.000 tur) + AES-256-GCM ile parolalı, ayrıca DPAPI ile sarılı. Hatalı denemelerde artan bekleme; admin paneli 15 dk hareketsizlikte kapanır ve anahtar bellekten atılır. |
| Dosyalar | Gönderen yalnızca o kişiye teklif ettiği dosyayı sunar. Dosya adları temizlenir (yol gezinme, aygıt adları, ADS, RTL-override engellenir), `.vtpart` geçici dosya + SHA-256 doğrulaması, Windows "Mark of the Web" eklenir; çalıştırılabilir dosyalar açılmadan önce uyarı verir. Dizin varsa yalnızca doğrulanmış kişilerden gelen resimler otomatik indirilir. |
| Girdi denetimi | Ağdan gelen zaman damgaları (gelecek tarihli/bozuk değerler), sürüm metinleri, grup üye kimlikleri ve yanıt alıntıları doğrulanır ve sınırlanır. |
| Kötüye kullanım | Gelen bağlantılarda IP başına/toplam sınır, el sıkışma zaman aşımı, 1 MB çerçeve sınırı, kişi başına istek sınırı (10 sn'de 40), bilinmeyen kimlik sayısı üst sınırı. Günlük dosyasına mesaj içeriği yazılmaz. |

**Bilinen sınırlar:**
- Sunucu yoktur: mesajlar yalnızca iki taraf aynı anda çevrimiçi olduğunda iletilir; kuyruk göndericide bekler.
- Dizinde doğrulanmamış bir kişinin görünen adı kendi beyanıdır; arayüzde uyarıyla gösterilir.
- Eski sürümlerle karışık kullanımda: 1.4.3 öncesi okundu bilgisi göndermez (onlara giden mesajlarda yalnızca teslim tiki görünür); 1.4.5 öncesi yazıyor göstergesini göndermez ve yanıt alıntısını düz mesaj olarak gösterir.

## Sorun giderme

- **Günlük:** `%LOCALAPPDATA%\VTalk\vtalk.log` (mesaj içeriği yazılmaz).
- **Arayüz donması:** Günlükte donma süresi, o an çalışan arayüz işlemi, bellek ve çöp toplama bilgisi yazar. Sürüm paketindeki adlar karartılmış olduğundan `installer\out\obfuscation-map-<sürüm>.txt` ile çözülür.
- **Güncelleme:** İstemcide `C:\ProgramData\VTalk\update\pending\download.log`, kurulum adımları için `C:\Program Files\V-Talk\update-staging\apply.log`; admin bilgisayarında `C:\ProgramData\VTalk\update\register.log`.
- **Takılı istemci:** Yönetici CMD'de `installer\force-pending-install.cmd` bekleyen paketi doğrulamalı yoldan kurar.

## Veri konumları

- Kullanıcı verisi: `%LOCALAPPDATA%\VTalk\` (kimlik, şifreli geçmiş, ayarlar, günlük)
- Alınan dosyalar: `İndirilenler\V-Talk\<gönderen>\` (Ayarlar'dan değiştirilebilir)
- Test/ikinci örnek için ayrı profil: `VTALK_PROFILE=isim` ortam değişkeni → `%LOCALAPPDATA%\VTalk-isim\`

## Proje yapısı

```
src/VTalk/
  Core/       Kimlik, yerel şifreleme (DPAPI + AES-GCM), ayarlar, modeller, grup ve etiket kuralları
  Net/        UDP keşif ve hız sınırları, şifreli kanal (SecureChannel), RPC, bağlantı havuzu, sunucu
  Data/       SQLite geçmiş veritabanı, teslim/okundu kayıtları, arama
  Security/   İmzalı organizasyon dizini, admin anahtar deposu, paket imza doğrulaması
  Services/   ChatService (1:1, grup, teslim/okundu, yazıyor, erişilebilirlik), güncelleme (indirme/kurulum),
              dosya aktarımı, bildirim sesleri, tepsi/bildirim, arayüz donma izleyicisi
  UI/         WPF pencereleri (ana liste, sohbet, grup, arama, ses seçimi, admin girişi/paneli, ayarlar, duyuru)
tests/VTalk.Tests/  Birim ve uçtan uca (loopback eşli) testler
tools/        Elle kurulum ve güvenlik duvarı betikleri
installer/    WiX MSI, Inno Setup kurulum programı, imzalı sürüm betiği, güncelleme görevi kaydı
```
