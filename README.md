# V-Talk — LAN Messenger

Sunucu gerektirmeyen, eşler arası (P2P), uçtan uca şifreli Windows yerel ağ mesajlaşma uygulaması.
MSN Messenger / LAN Messenger tarzı arayüz; .NET 8 + WPF.

## Özellikler

- **Anında keşif:** Uygulamayı çalıştıran herkes, aynı ağdaki herkesin listesinde saniyeler içinde görünür (UDP yayın).
- **Çevrimiçi / çevrimdışı liste:** Daha önce görülen herkes listede kalır; çevrimdışılar soluk görünür. Durumlar: Çevrimiçi, Meşgul (sessiz), Dışarıda (boşta kalınca otomatik).
- **Birim grupları:** Admin dizini varsa kişiler çalıştıkları birime göre gruplanır (boş birimler de görünür, kişinin kendi birimi en üstte). Dizinde olmayan (doğrulanmamış) kişiler listede gizlenir; admin bunları **Yönetim** menüsünden gösterip tek tek veya topluca doğrulayabilir.
- **Birebir sohbet:** MSN tarzı "... diyor ki:" görünümü, Enter ile gönder / Shift+Enter yeni satır.
- **Çevrimdışı kuyruk:** Karşı taraf çevrimdışıysa mesaj kuyruğa alınır, çevrimiçi olunca otomatik iletilir.
- **Dosya gönderme:** Düğme, sürükle-bırak veya Ctrl+V. Kabul et / reddet / iptal, ilerleme çubuğu, SHA-256 bütünlük kontrolü.
- **Resimler sohbet içinde:** Resimler (png, jpg, gif, bmp, tif) doğrudan sohbet penceresinde görünür; panodan ekran görüntüsü yapıştırılabilir.
- **Bildirimler:** MSN tarzı sentezlenmiş bildirim sesi (isteğe bağlı kendi .wav dosyanız), Windows'un sağ alt köşe bildirimi, görev çubuğunda yanıp sönme, tepsi simgesi.
- **Titreşim (nudge):** Karşı tarafın penceresini sallar.
- **Yerel geçmiş:** Konuşmalar yalnızca iki tarafın kendi bilgisayarında, şifreli olarak saklanır.
- **Yetkilendirme:** Admin, Birim Yöneticisi, Kullanıcı rolleri. Birim yöneticisi, çalıştığı birimin yöneticisidir. Admin; kişiye sağ tıklayarak (IP, bilgisayar, kullanıcı ve parmak izi görünür) doğrulama, çalıştığı birim ve rol seçebilir, kişiyi bir birim başlığına sürükleyerek birime alabilir veya **Yönetim → Admin Girişi** panelini kullanabilir.
- **Duyurular:** Admin tüm kullanıcılara, Birim Yöneticisi kendi birimine duyuru gönderebilir (açılır pencere + ses + bildirim).

## Derleme

Gereksinim: .NET 8 SDK.

```powershell
.\publish.ps1                      # dist\VTalk.exe — .NET gerektirmeyen tek dosya (~65 MB), karartılmış ve öz sınamalı
.\publish.ps1 -FrameworkDependent  # küçük exe, hedefte .NET 8 Desktop Runtime gerekir
dotnet test                        # birim + uçtan uca entegrasyon testleri
```

## Kurulum / Dağıtım

`dist` klasörünü kullanıcılara dağıtın ve `kur.ps1`'i çalıştırın:

- **Yönetici olarak:** `C:\Program Files\V-Talk` altına kurar, Başlat menüsü kısayolu ve **güvenlik duvarı kurallarını** ekler.
- **Normal kullanıcı olarak:** `%LOCALAPPDATA%\Programs\V-Talk` altına kurar; ilk açılışta Windows güvenlik duvarı izin isteyebilir.

> **Önemli:** Güvenlik duvarı gelen bağlantılara izin vermezse kullanıcı başkalarını göremez / mesaj alamaz.
> Toplu dağıtımda `firewall-kurallari.ps1`'i (yönetici) veya GPO ile eşdeğer kuralları kullanın.
> Varsayılan kurallar yalnızca **yerel alt ağdan** ve **Etki Alanı/Özel** ağ profillerinde izin verir.

| Port | Protokol | Amaç |
|------|----------|------|
| 50505 | UDP | Keşif (yayın) |
| 50506 | TCP | Şifreli mesaj ve dosya aktarımı (doluysa otomatik başka port) |

### Farklı alt ağlar, VPN (OpenVPN / IPsec)

Yayın paketleri yönlendirici ve VPN tünellerini geçmez. Uzak ağlar için **Ayarlar → Ağ (gelişmiş)** bölümüne tek IP'ler veya CIDR alt ağları yazılır (ör. `10.20.30.0/24, 10.20.40.15`). Bu ağlar genel (Public) ağ engellemesinden de muaf tutulur.

- Tanımın yalnızca bir tarafta yapılması yeterlidir: duyurusu alınan uzak eşlere düzenli tekil duyuru gönderildiği için iki taraf da birbirini çevrimiçi görür.
- Güvenlik duvarında uzak ağlara izin vermek için: `firewall-kurallari.ps1 -ExePath ... -RemoteSubnets 10.20.30.0/24,10.20.40.0/24`
- Alt ağ başına en fazla 1024 adres taranır (ör. /22); daha büyük ağlar için daha küçük alt ağlara bölün.

## İlk admin kurulumu

1. Admin olacak kişi **Yönetim → Admin Girişi**'ni açar. Ağda henüz organizasyon yoksa "Organizasyonu oluştur" ekranı gelir.
2. Güçlü bir admin parolası belirlenir. Bu bilgisayarda organizasyon imzalama anahtarı üretilir ve kişi otomatik olarak Admin olur.
3. Admin panelinde birimler oluşturulur, kişiler dizine eklenir, rol ve birim atanır → **Kaydet ve Yayınla**.
4. **Önerilen:** Admin panelindeki **org.pub dosyasını dışa aktar** ile alınan dosyayı `VTalk.exe`'nin yanına (veya `C:\ProgramData\VTalk\`) koyarak dağıtın. `publish.ps1`, proje kökünde `org.pub` varsa onu otomatik olarak `dist`'e kopyalar.
5. Başka bir bilgisayardan da admin girişi gerekiyorsa **Admin anahtarını dışa aktar** (.vtkey, parolayla şifreli) ile alınan dosya o bilgisayarda Admin Girişi ekranından içe aktarılır.

## Güvenlik modeli

| Konu | Uygulama |
|------|----------|
| Kimlik | Her kullanıcı için ECDSA P-256 anahtar çifti; kullanıcı kimliği (PeerId) açık anahtarın SHA-256 özetidir, taklit edilemez. Özel anahtar DPAPI ile korunur. |
| Keşif | UDP duyuruları imzalıdır; PeerId–anahtar eşleşmesi, tekrar oynatma (replay) koruması, kaynak IP başına hız sınırı, 4 KB paket sınırı. |
| Aktarım | Karşılıklı kimlik doğrulamalı el sıkışma + geçici ECDH (ileri gizlilik) → HKDF → yön başına AES-256-GCM. İstemci, keşifte gördüğü PeerId'yi doğrular (MITM engellenir). |
| Yerel geçmiş | SQLite; mesaj içerikleri ve kişi bilgileri AES-256-GCM ile şifreli, anahtar Windows hesabına bağlı (DPAPI). Veri klasörü yalnızca kullanıcıya açık ACL ile korunur. Tüm SQL sorguları parametreli. |
| Yetkiler | Roller, organizasyon anahtarıyla **imzalı dizin** olarak dağıtılır. Güven kökü: dağıtılmış `org.pub` (önerilen) veya ilk görülen dizin (TOFU, sonra sabitlenir). Duyuru yetkisi **alıcı tarafında** gönderenin imzalı rolüne göre denetlenir. Farklı organizasyon anahtarı görülürse uyarı gösterilir. |
| Admin anahtarı | PBKDF2-SHA256 (600.000 tur) + AES-256-GCM ile parolalı, ayrıca DPAPI ile sarılı. Hatalı denemelerde artan bekleme; admin paneli 15 dk hareketsizlikte kapanır ve kapanınca anahtar bellekten atılır. Ana penceredeki hızlı düzenlemeler (sağ tık / sürükle-bırak) için anahtar en fazla 10 dk hareketsizlik süresince açık tutulur. |
| Dosyalar | Gönderen yalnızca o kişiye teklif ettiği dosyayı sunar. Dosya adları temizlenir (yol gezinme, aygıt adları, ADS, RTL-override engellenir), `.vtpart` geçici dosya + SHA-256 doğrulaması, Windows "Mark of the Web" (Zone.Identifier) eklenir; çalıştırılabilir dosyalar açılmadan önce uyarı verilir. Resimler gerçekten çözülebiliyorsa önizlenir. Dizin varsa yalnızca doğrulanmış kişilerden gelen resimler otomatik indirilir. |
| Kötüye kullanım | Gelen bağlantılarda IP başına/toplam sınır, el sıkışma zaman aşımı, 1 MB çerçeve sınırı, kişi başına mesaj hız sınırı, bilinmeyen kimlik sayısı üst sınırı. Günlük dosyasına mesaj içeriği yazılmaz. |

**Bilinen sınırlar:** Mesajlar yalnızca iki taraf aynı anda çevrimiçi olduğunda iletilir (sunucu yok; kuyruk göndericide bekler). Dizinde doğrulanmamış bir kişinin görünen adı kendi beyanıdır; bu yüzden arayüzde uyarıyla gösterilir.

## Veri konumları

- Kullanıcı verisi: `%LOCALAPPDATA%\VTalk\` (kimlik, şifreli geçmiş, ayarlar, günlük)
- Alınan dosyalar: `İndirilenler\V-Talk\<gönderen>\` (Ayarlar'dan değiştirilebilir)
- Test/ikinci örnek için ayrı profil: `VTALK_PROFILE=isim` ortam değişkeni → `%LOCALAPPDATA%\VTalk-isim\`

## Proje yapısı

```
src/VTalk/
  Core/       Kimlik, yerel şifreleme (DPAPI + AES-GCM), ayarlar, modeller
  Net/        UDP keşif, şifreli kanal (SecureChannel), RPC, bağlantı havuzu, sunucu
  Data/       SQLite geçmiş veritabanı
  Security/   İmzalı organizasyon dizini, admin anahtar deposu
  Services/   ChatService (orkestrasyon), dosya aktarımı, ses, tepsi/bildirim
  UI/         WPF pencereleri (ana liste, sohbet, admin girişi/paneli, ayarlar, duyuru)
tests/VTalk.Tests/  Birim ve uçtan uca entegrasyon testleri
tools/        Kurulum ve güvenlik duvarı betikleri
```
