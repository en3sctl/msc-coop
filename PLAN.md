# MSC Co-op: Mimari ve Uygulama Planı

Tarih: 2026-10-13. Hedef oyun sürümü: v.250908-04 (Unity 5.0.0f4, x64, Mono .NET 3.5, PlayMaker 1.7.7.f6).

## 1. Hedef ve "senkron sorunu yok" tanımı

2-4 oyunculu, Steam üzerinden arkadaşla oynanan co-op. "Senkron sorunu yok" ölçülebilir hedeflere bağlanır:

| Kriter | Hedef |
|---|---|
| Sürücünün kendi arabasındaki gecikme | 0 ms. Arabayı kendi makinesi simüle eder |
| Kalıcı durum farkı (kapı, cıvata, parça, para, FSM state) | 150 ms ping ve %3 kayıpla 2 saatlik oturumda sıfır kalıcı fark. Fark oluşursa 2 sn içinde kendiliğinden düzelir |
| Uzaktaki hareketli obje hatası | Ortalama < 10 cm, 1 m üstü hata 1 sn sürerse snap |
| Steady-state GC allocation | Karede 0 byte (ağ kodu) |
| Bant genişliği | Client başına < 128 kbps, 4 oyunculu host upload < 400 kbps |
| Paket boyutu | Unreliable ≤ 1200 byte (Steam sınırı), hedef ≤ 1024 |

Dürüst not: MSC'de binlerce FSM var. Hiçbir mimari "hiç desync olmaz" garantisi veremez. Bu plan desync'i **tespit edilebilir ve kendini onaran** hale getirir. Kalıcı durum host'ta versiyonlanır, periyodik özetler karşılaştırılır, fark varsa host düzeltme gönderir (bkz. 6.4).

## 2. Önceki projelerden çıkan dersler

| Proje | Hata | Bizim çözüm |
|---|---|---|
| MSCMP (MIT, terk) | ID'ler keşif sırasıyla atanıyor, sürekli durum reliable gidiyor, gelen değer yumuşatılmadan basılıyor, sahiplik yarışları | Kararlı hash ID, unreliable snapshot ve interpolasyon, host hakemliği |
| WreckMP (kapalı) | Araba gövdesi host'ta, client sürücü RTT kadar rubber-band yaşıyor. Rigidbody ve oyuncu her FixedUpdate'te reliable. Sahiplik devri hakemsiz ve bir modda unreliable. Yükleme sırasında gelen paketler atılıyor | Sürücü otoritesi, sahiplik daima reliable ve hakemli, yüklenirken gelen paketler buffer'da tutulur |
| WreckMP ExtendedSync | Mağaza/posta spawn'ları çiftleniyor, para mutlak değerle eziliyor, isim/pozisyonla eşleştirme | Spawn'ı sadece host yapar ve ID'yi host verir. Para delta istekleriyle değişir. Eşleştirme yalnızca ID ile |
| MWCMP | FSM event spam döngüleri | Event yerine versiyonlu state gönderilir, echo bastırılır |

Kopyalanacak fikirler:
- Sadece host kaydeder ve kaydı client'a aktarır.
- ID önce `Use` FSM'deki `ID` değerinden türetilir.
- FSM state'i `MP_*` global transition ile zorlanır.
- Spawn/destroy yakalamak için `ActionTypeLookup` değiştirilir.
- Kargo, araç sahibine devredilir.
- Paket kayıt/replay ve aynı PC'de iki oyun açma.

Lisans: sadece MSCMP-OLD (MIT) kodundan alıntı yapılabilir, telif notu korunarak. WreckMP/BeerMP decompile'ları sadece okunur, kod kopyalanmaz.

## 3. Teknoloji seçimleri (hepsi native)

| Katman | Seçim |
|---|---|
| Yükleyici | MSCLoader 1.4.2 (Doorstop 4.5). Mod, `Mod` sınıfı ve `SetupFunction` API'siyle yazılır |
| Dil/hedef | C#, `net35`, SDK-style csproj, `LangVersion latest`, `Microsoft.NETFramework.ReferenceAssemblies` |
| Patch | MSCLoader'ın getirdiği Harmony 1.2 (`HarmonyInstance`). Harmony 2 eklenmez |
| Taşıma | Oyunun Steamworks.NET 9.1.0 (SDK 1.39) legacy `SteamNetworking` P2P |
| Lobi | `SteamMatchmaking` FriendsOnly lobi, overlay daveti, `+connect_lobby` |
| Geliştirme taşıması | `System.Net.Sockets`: unreliable için UDP, reliable için TCP. Sadece localhost/LAN testi için |
| UI | Oyunun `UnityEngine.UI` (uGUI) kütüphanesi. Debug overlay için IMGUI |
| Araçlar (oyun dışı) | Rust CLI: FSM dump analizi, desync log karşılaştırma |

Oyunun `steam_api64.dll` ve `CSteamworks.dll` dosyalarına dokunulmaz.

## 4. Repo yapısı

```
msc-coop/
  src/
    MSCCoop.Net/          saf C#, Unity bağımlılığı yok: BitWriter/Reader, quantization,
                          mesaj tanımları, reliability, clock sync, interpolasyon matematiği
    MSCCoop/              MSCLoader modu: transport'lar, oyun entegrasyonu, UI
    MSCCoop.Recon/        sadece geliştirici modu: sahne ve FSM dökümü
  tests/MSCCoop.Net.Tests/  net8.0 xUnit, MSCCoop.Net'i test eder (net35;net8.0 multi-target)
  tools/coop-tools/       Rust: fsm-dump analizi, desync log diff
```

CI (GitHub Actions) sadece `MSCCoop.Net` ve testleri derler. Oyun DLL'leri repoya konmaz, telif nedeniyle dağıtılamaz.

## 5. Ağ mimarisi

### 5.1 Topoloji
Yıldız topolojisi kullanılır. Host hem oyuncu hem hakemdir. Client'lar sadece host'la konuşur, host gerekenleri diğerlerine iletir. Full mesh yok.

### 5.2 Kanallar (Steam P2P)

| Kanal | Gönderim tipi | İçerik |
|---|---|---|
| 0 | Reliable | Kontrol: handshake, sahiplik, FSM state, spawn/destroy, para, chat |
| 1 | Unreliable | Snapshot: oyuncu, araç ve hareketli obje durumu, ping |
| 2 | Reliable | Toplu veri: kayıt dosyası ve tam dünya snapshot'ı, 16-32 KB parçalar halinde |

`ReliableWithBuffering` (Nagle) asla kullanılmaz. Her kanal her karede `IsP2PPacketAvailable` ile boşaltılır. Okuma tek bir önceden ayrılmış buffer'la yapılır.

### 5.3 Paket formatı
- Başlık: `protocolId(u16) | protocolVersion(u8) | packetSeq(u16) | sendTime(u16, ms)`.
- Gövde: `[msgType(u8) | payload]*`. Bir pakette birden çok mesaj olabilir.
- Bit düzeyinde yazılır: `BitWriter`, havuzlanmış `byte[]`.
- Gönderimde `GetBuffer()` değil, yazılan uzunluk geçilir (MSCMP'deki çöp padding hatası).

### 5.4 Quantization

| Veri | Kodlama | Boyut |
|---|---|---|
| Pozisyon | Hücre (i16 x/z) ve hücre içi 1/512 m | ~56 bit |
| Rotasyon | Smallest-three: obje için 3×10 bit, araç için 3×11 bit | 32-35 bit |
| Doğrusal hız | ±64 m/s, 3×12 bit, artı "duruyor" biti | ≤ 37 bit |
| Açısal hız | ±16 rad/s, 3×10 bit | 30 bit |
| Obje ID | Registry indeksi (u16), handshake'te eşleştirilir | 16 bit |

Hareketli bir rigidbody yaklaşık 22 byte eder. 1024 byte'lık pakete yaklaşık 40 obje sığar. Quantize edilen değer gönderen tarafta da uygulanır, böylece iki taraf aynı sayıyı görür.

### 5.5 Gönderim hızları ve öncelik

| Veri | Hız |
|---|---|
| Oyuncu avatarı | 20 Hz |
| Sürülen araç durumu | 30 Hz |
| Araç girdileri ve drivetrain | 20 Hz |
| Hareketli objeler | Priority accumulator ile 10-20 Hz, paket bütçesine sığdırılır |
| Duran objeler | 0 Hz. Durduğu an son durum reliable gönderilir |
| Saat ve hava | 1 Hz, client arada kendisi ilerletir |
| Ping/clock sync | 2 Hz |

Öncelik `taban × mesafe faktörü` olarak hesaplanır: tutulan obje en yüksek, sürülen araç ve kargosu yüksek, uzaktaki düşüktür. Sadece gönderilen objelerin accumulator'ı sıfırlanır.

### 5.6 Zaman senkronu ve interpolasyon
- Ping-pong ile `rtt` ve `offset` ölçülür. En düşük RTT'li örneklerin EMA'sı alınır. 1 sn üstü sapmada snap edilir.
- Uzaktaki objeler için snapshot buffer: 32 slot.
  - Adaptif gecikme: son 20 varış aralığının en kötüsü × 2, 50-250 ms arasına sıkıştırılır (ilk değer ~100 ms).
  - Hermite interpolasyon (pozisyon ve hız). Rotasyon için slerp.
  - Buffer boşalırsa en fazla 250 ms hızla extrapolasyon yapılır.
  - Hafif zaman ölçeklemesiyle (+2% / −4%) buffer hedef derinlikte tutulur.

### 5.7 Oturum
- Lobi verisinde: mod sürümü, `SteamApps.GetAppBuildId()`, protokol sürümü, kayıt hash'i. Uyuşmazsa katılım reddedilir ve nedeni gösterilir.
- Handshake mesajları: `Hello{sürümler, mod listesi + hash}` → `Welcome{slot, registryHash}` veya `Reject{neden}`.
- `P2PSessionRequest_t` sadece lobi üyesi için kabul edilir.
- Heartbeat ping ile birlikte gider. Zaman aşımı 15 sn, yükleme sırasında 90 sn.
- Host ayrılırsa oturum kapanır ve client kendi kaydına geri döner. Host migration yok.

## 6. Oyun durumu senkronu

### 6.1 Kararlı obje kimliği (NetId)

Recon ölçümleri (2026-10-13, 36.693 obje, 6.617 FSM):
- `OnLevelWasLoaded` anında (bütün Awake'ler bitmiş, hiçbir Start çalışmamış) sahne yazıldığı gibi duruyor. Parçalar oyunun ID'leriyle adlandırılmış (`battery0`, `wheel_steel1`). FSM'ler sonra bunları yeniden adlandırıp taşıyor.
- Awake → PreLoad arasında 292, PreLoad → PostLoad arasında 322 yol değişiyor.
- İki ayrı oturumun PostLoad yolları arasında bile 4 fark var: radyo kanalları zamanlamaya göre farklı hoparlöre taşınıyor. Geç okunan yol deterministik değil.
- Awake yolları 2 oturum ve 3 yüklemede, aradaki kayıt değişmiş olmasına rağmen birebir aynı çıktı (6.054 / 6.054, sıfır fark). PreLoad'da da fark yok. PostLoad'da yüklemeden yüklemeye 8 fark var.
- Oyunun kayıt anahtarları (`LoadTransform.uniqueTag`) PreLoad'da 270 objeyi kapsıyor. Ama oyunda bir çakışma hatası var: Satsuma ve Ferndale'in kırık camı ikisi de `WindshieldBrokenPos` kullanıyor.

Öncelik sırası:
1. Sahne objeleri: `OnLevelWasLoaded` anında alınan hiyerarşi yolu. Aynı isimli kardeşler sıra indeksiyle ayrılır (`name[1]`). ID objeye bir kez sabitlenir, bir daha yoldan hesaplanmaz.
2. Kayıttan sonradan spawn olan eşyalar: `Use` FSM'deki `ID` değeri (`beercase0`). Oyun bunu kayda yazar ve benzersiz tutar.
3. Oyun sırasında spawn olanlar: ID'yi host verir.

Bu anahtar FNV-1a 64 ile hash'lenir. `GetHashCode` kullanılmaz.
- Çakışma olursa yükleme hata verir ve dump dosyası yazar.
- Handshake'te kategori bazında registry hash karşılaştırılır. Fark varsa hangi objeler farklı, listelenir.
- Çalışma zamanında spawn olan objelere ID'yi sadece host verir (üst bit 1). Client spawn'ı mesajla yapar.
- Keşif sırasına bağlı sayaç kullanılmaz.

### 6.2 Sahiplik
- Her senkron obje şunları tutar: `owner(slot)`, `ownerSeq(u16)`, `lockedUntil`.
- Değişiklik akışı:
  1. Client iyimser davranır, yerelde simülasyona başlar.
  2. Reliable `OwnReq{id, seenSeq}` gönderir.
  3. Host ya `OwnGrant{id, owner, seq+1}` yayınlar ya da `OwnDeny` gönderir. Deny gelirse client host durumuna döner.
- Tutulan obje ve sürülen araç özel sahipliktir, çalınamaz. Devirden sonra 250 ms kilit uygulanır.
- Bırakılan obje hareket ettiği sürece sahibinde kalır. 16 fizik adımı durduktan sonra son durum reliable gönderilir ve sahiplik host'a döner.
- Çarpışmayla, sahibi olmayan bir objeye otorite talep edilebilir. Daha yüksek seq kazanır, eşitlikte host kazanır.
- Sürülen aracın içindeki kargo hacmindeki eşyalar ile takılı parçalar aracın sahibine bağlanır (ayrı gövde olarak gönderilmez).
- Ayrılan oyuncunun bütün objeleri host'a geçer.

Sahip olmayan tarafta obje `isKinematic` olur ve `MovePosition`/`MoveRotation` ile interpolasyonla sürülür. Gelen değer düz basılmaz.

### 6.3 Araçlar
- Sürücü arabayı tam simüle eder: UnityCar, `CarDynamics`, `Drivetrain`, raycast `Wheel`.
- Diğer makinelerde:
  - Gövde kinematik proxy olur ve interpolasyonla hareket eder.
  - Görsel tekerlekler gelen veriden sürülür: direksiyon açısı, tekerlek dönüşü, süspansiyon sıkışması.
  - `MPCarController : AxisCarController` uzak girdileri besler. Direksiyon simidi, fren lambası ve ses doğru çalışır.
  - Drivetrain `rpm`, `gear` ve `throttle` değerleri ses ve gösterge FSM'leri için yazılır. Hangi FSM'lerin bu değerleri okuduğu Faz 0'da çıkarılacak.
- Sürücü değişimi: koltuk tetikleyicisi (`DriveTrigger`/`PlayerTrigger` FSM) sahiplik ister ve onay gelince sürüş başlar.
- Panel ve motor durumu (kontak, marş, farlar, silecek, sinyal, el freni, yakıt musluğu) reliable versiyonlu state olarak, sadece değiştiğinde gider.
- Yolcu koltuğu: oyuncu araca bağlanır ve sürücünün simülasyonunu izler. Kamera yerelde kalır.
- Römork ve çekme (Kekmet römorku, halat): bağlantı olayı reliable gider. Bağlı römork aracın sahibine geçer.
- Aşama 2 (ölçümden sonra): uzak araç kinematik yerine BeamMP tarzı dinamik gövde ve PD düzeltme kuvvetiyle sürülebilir. Böylece çarpışmalar gerçekçi olur. Önce kinematik versiyon ölçülecek.

### 6.4 PlayMaker durum senkronu
- Temel kural: kalıcı şeyler için **state**, geçici şeyler (ses, parçacık) için **event** gönderilir.
- `FsmSync` adaptörü:
  - Hedef FSM'deki ilgili state'lerin başına `FsmStateAction` eklenir. Bu aksiyon değişimi algılar.
  - Her state için `MP_<state>` event'i ve global transition eklenir.
  - Uzak taraf `SendEvent("MP_<state>")` ile state'i zorlar. PlayMaker 1.7.7'de public `SetState` yok, bu yüzden bu yöntem gerekiyor.
  - Echo bastırma iki katmanlıdır: `applying` bayrağı ve `LastTransition` kontrolü.
  - Mesaj: `FsmState{netId, version(u16), stateIndex, vars[]}`. Uygulama idempotent'tir, eski versiyon atılır.
  - Değişikliği client değil host onaylar. Client istek gönderir, host versiyonu artırıp yayınlar. Aynı obje sahibindeyse (ör. kendi arabası) sahip yayınlar.
- Kendini onarma:
  - Host her saniye kategori bazında `(netId, version)` özet hash'ini gönderir.
  - Uyuşmayan kategori için client tam liste ister ve farkları alır.
- Kapsam kataloğu Faz 0 dökümüne göre çıkarılır. Öncelik sırası:
  1. Kapılar
  2. Işıklar ve anahtarlar
  3. Musluk, sauna, telefon
  4. Araç paneli
  5. Parça takma/sökme (`Assembly`, `Removal`)
  6. Cıvatalar (`Screw`, `BoltCheck`)
  7. Sıvılar (yağ, su, yakıt)
- Cıvata senkronu: cıvata başına sadece son state ve sıkılık gider. Anahtar animasyonu gönderilmez.
- Spawn/destroy: `ActionTypeLookup` içinde `CreateObject`, `DestroyObject`, `DestroySelf` ve `ActivateGameObject` alt sınıflarla değiştirilir. Bu, FSM'ler deserialize olmadan önce yapılır (menüde).
  - Host'ta spawn olursa ID verilip yayınlanır.
  - Client'ta oyun mantığı spawn denerse engellenir ve host'tan beklenir.

### 6.5 Dünya sistemleri (host otoritesi)
- **Zaman:** `SUN` "Color" FSM ve global saat değişkenleri, 1 Hz. Client'ın kayıttan yüklediği zaman ezilir.
- **Hava:** `CloudSystem` FSM değişkenleri.
- **Trafik ve NPC'ler:** sadece host simüle eder. Client'ta kinematik proxy olurlar, sadece oyunculara yakın olanlar gönderilir.
- **Para:** ortak cüzdan. Client'lar `MoneyDelta{amount, reason, reqId}` gönderir, host uygular ve mutlak değeri yayınlar. Aynı anda iki harcama birbirini ezmez.
- **Mağaza ve posta siparişi:** satın alma host'ta gerçekleşir. Spawn'ı host yapar ve ID'yi o verir. Tekilleştirme `reqId` ile yapılır.
- **Oyuncu ihtiyaçları** (açlık, susuzluk, yorgunluk, sarhoşluk, idrar): kişiseldir ve yerelde kalır. Diğer oyuncular sadece görsel durumu (sarhoşluk sallantısı, uyuma) görür.
- **Ölüm:** kişiseldir. Ölen oyuncu yeniden doğar ve host'un kaydı silinmez.

### 6.6 Kayıt ve katılma
- Sadece host kaydeder. Client'ta ES2 Save/Load PlayMaker aksiyonları `ActionTypeLookup` ile no-op sürümlerle değiştirilir. Bu sayede client'ın kaydı hiç bozulmaz.
- Katılma akışı:
  1. Client ana menüdeyken lobiye katılır.
  2. Host `defaultES2File.txt` ve `items.txt` dosyalarını kanal 2'den, parçalar halinde ve hash'leriyle gönderir.
  3. Client bunları izole bir kayıt klasörüne yazar. ES2 yol yönlendirmesi Faz 0'da araştırılacak. Gerekirse yedekle, yaz ve çıkışta geri yükle.
  4. Client GAME sahnesini yükler. Bu sırada gelen paketler buffer'da tutulur, atılmaz.
  5. Client `Ready` gönderir. Host tam canlı snapshot'ı gönderir: zaman, sahiplik tablosu, FSM versiyonları ve hareketli objeler.
  6. Buffer'daki güncellemeler versiyonla uygulanır.
- Oyun sırasında katılma da aynı akışla yapılır. Host durmaz.
- Steam Cloud'un client'ta host kaydını buluta yüklememesi için izole klasör şart.

### 6.7 Mod uyumluluğu
- Handshake'te iki taraftaki mod listesi karşılaştırılır. Uyuşmazlıkta uyarı veya ret.
- **MOP:** objeleri yerel oyuncuya mesafeye göre kapattığı için uzak oyuncuyu bozar. İlk sürümde algılanıp uyarı gösterilir. Sonra MOP'un kural dosyalarıyla entegrasyon denenir.
- Dışa açık küçük bir API: `RegisterRigidbody`, `RegisterFsm`, `OnPlayerJoined`. Erken sürümde değil, çekirdek oturduktan sonra.

## 7. Performans kuralları
- Sıcak yolda allocation yok: `foreach` (koleksiyonlarda), LINQ, string birleştirme, lambda capture, boxing kullanılmaz. Mesajlar struct'tır, buffer'lar havuzlanır.
- `GameObject.Find`, `GetComponent` ve MSCLoader'ın `GetVariable`/`GetState` çağrıları sadece yüklemede yapılır ve sonuç cache'lenir. Bu çağrılar her seferinde `InitData` çalıştırıyor.
- `Resources.FindObjectsOfTypeAll` sadece yüklemede kullanılır.
- Boş `SetupFunction` kaydedilmez. Fizik okuma ve yazma `FixedUpdate`'te, gönderim fizik adımı sonrasında yapılır.
- Ölçüm:
  - Debug overlay'de `GC.CollectionCount` takibi, kare süresi ve mesaj tipi başına bant genişliği.
  - Kabul kriteri: 10 dakikalık oturumda ağ kodundan kaynaklı GC tetiklenmemesi.

## 8. Test ve hata ayıklama altyapısı
- **Aynı PC'de iki oyun:**
  - MSCLoader'ın `-mscloader-sipatch` parametresi kullanılır.
  - İkinci instance `-coop-dev-slot 2` ile LAN transport'u kullanır.
  - Steam başlatma iki instance'ta da gerekirse Harmony ile atlanır (MWCMP yöntemi).
- **Ağ simülatörü:** transport'a gömülür. Gecikme, jitter, kayıp, çiftleme ve sıra bozma ayarlanabilir. Kayıp sadece unreliable kanala uygulanır.
- Standart senaryolar:
  - 0 ms
  - 100 ms, %1 kayıp
  - 150 ms, ±30 ms jitter, %3 kayıp
  - 300 ms, %5 kayıp
  - 500 ms stres testi
- **Desync dedektörü:** 1 Hz kategori hash'i: FSM state'leri, quantize değişkenler, parça/cıvata durumu, sahiplik tablosu. Fark olunca iki tarafın okunabilir dump'ı yazılır. Rust aracıyla karşılaştırılır.
- **Fizik sapması:** sahip ile kopya arasındaki fark 0.5 m'yi 2 sn aşarsa loglanır.
- **Paket kayıt/replay:** tek başına test için.
- **Debug overlay (F-tuşu):** ping, offset, buffer derinliği, kbps; sahipliğe göre obje renklendirme; hedef ve gerçek pozisyon gizmo'su.
- **Birim testleri (`MSCCoop.Net.Tests`):** BitWriter/Reader, quantization hata sınırları, seq wrap karşılaştırma, interpolasyon, sahiplik hakemi durum makinesi, reliability ve parçalama.

## 9. Fazlar

Her faz bir öncekinin kabul kriteri sağlanmadan başlamaz.

### Faz 0: Kurulum ve keşif
1. MSCLoader 1.4.2'yi kur. Git repo'yu, lisansı ve `.gitignore`'u oluştur. Solution iskeletini ve csproj'ları kur (oyun yolu `Directory.Build.props` içinde, kullanıcıya özel).
2. `MSCCoop.Recon` modunu yaz. GAME yüklendikten sonra şunları JSON'a döker:
   - Tüm GameObject hiyerarşisi: yol, aktiflik, layer, tag, component tipleri, Rigidbody ayarları.
   - Tüm PlayMakerFSM'ler: state'ler, transition'lar, global transition'lar, aksiyon tipleri ve alanları, değişkenler.
   - Global değişkenler ve event'ler.
3. ID kararlılık testi: iki farklı yüklemede, yeni oyun ve eski kayıtta, kayıt-yükle sonrasında ID'ler dökülüp karşılaştırılır. Çakışma ve kayma ölçülür.
4. Bu dökümden çıkarılacaklar:
   - Oyuncuya doğrudan referans veren FSM'ler.
   - Araç ses ve gösterge FSM'lerinin `Drivetrain`'den ne okuduğu.
   - Eşya alma FSM'si (`PickUp`).
   - Satın alma ve posta akışı.
   - Kayıt (`Save game`) akışı.
   - ES2 yol yönlendirme imkanı.
5. Rust `coop-tools fsm`: dökümü sorgulama (ör. "PLAYER'a referans veren aksiyonlar", "Screw FSM'leri listesi").

**Kabul:** döküm tamam, ID şeması %100 kararlı ve çakışmasız, senkron edilecek FSM kataloğunun ilk sürümü yazılmış.

### Faz 1: Ağ çekirdeği
- `MSCCoop.Net`: BitWriter/Reader, quantization, mesaj çerçeveleme, seq, clock sync, snapshot buffer ve interpolasyon, ağ simülatörü. Hepsi birim testli.
- Transport'lar: `SteamP2PTransport` (3 kanal) ve `LanTransport` (UDP+TCP).
- Lobi ve davet, handshake, sürüm kontrolü, heartbeat, temiz kopma.
- uGUI ekranları: Host Et, Davet Et, Lobiden Ayrıl, oyuncu listesi, bağlantı durumu.

**Kabul:** iki makine Steam üzerinden ve aynı PC'de LAN üzerinden bağlanıyor. Ping ve kbps overlay'de görünüyor. Testler yeşil.

### Faz 2: Kayıt aktarımı ve katılma
- Host-only kayıt, client'ta ES2 engelleme, izole kayıt klasörü.
- Kayıt dosyası aktarımı, GAME yükleme, yükleme sırasında buffer, Ready ve tam snapshot.

**Kabul:** client host'un dünyasında doğuyor. Client'ın kendi kaydı hiçbir koşulda bozulmuyor; çökme, kopma ve alt-F4 test edilecek.

### Faz 3: Oyuncular
- Avatar: önce sahnedeki bir NPC modeli klonlanır (harici asset gerekmez). Gerekirse Unity 5.0.0f4 ile AssetBundle hazırlanır.
- Transform ve animasyon durumu senkronu (yürüme, koşma, çömelme, oturma, sürme), isim etiketi.

**Kabul:** 150 ms ve %3 kayıpta akıcı hareket, titreme yok.

### Faz 4: Obje registry ve sahiplik
- NetId registry, handshake registry hash karşılaştırması.
- Sahiplik hakemi, tutma/bırakma/fırlatma, dinlenme algılama, priority accumulator.

**Kabul:** iki oyuncu aynı kasayı aynı anda kapınca deterministik sonuç çıkıyor. 100 eşyalı yığın devrilince iki tarafta da aynı son konum (±5 cm).

### Faz 5: Araçlar
- Sürücü sahipliği, uzak proxy, girdi ve drivetrain senkronu, panel FSM'leri, yolcu, kargo devri, römork.
- Önce Satsuma ve Hayosiko, sonra diğer araçlar.

**Kabul:**
- Sürücü sıfır gecikme hissediyor.
- Yolcu titreme görmüyor.
- 100 km/s'de takip eden araç hatası < 30 cm.
- Kargo arabada düşmüyor veya içinden geçmiyor.

### Faz 6: PlayMaker senkron çerçevesi ve katalog
- `FsmSync` adaptörü, versiyonlama, özet hash ile kendini onarma.
- Katalog sırasıyla: kapılar, anahtarlar, ev eşyaları, parça takma/sökme, cıvatalar, sıvılar.
- `ActionTypeLookup` ile spawn/destroy.

**Kabul:** 2 saatlik montaj oturumunda (Satsuma'yı sıfırdan toplama) desync dedektörü sıfır kalıcı fark raporluyor.

### Faz 7: Dünya sistemleri
- Saat ve hava, ortak cüzdan, mağaza, posta, işler (odun, septik, taksi vb.), NPC'ler, trafik, polis.

**Kabul:** her sistemin iki taraflı testi yapılmış. Çift spawn veya kayıp spawn yok.

### Faz 8: Sertleştirme
- 4 oyunculu testler, uzun soak testleri, tüm ağ senaryoları, bant genişliği ve GC profili, MOP ve popüler modlarla uyumluluk.

### Faz 9: Yayın
- GitHub repo, README (kurulum ve bilinen kısıtlar), sürüm etiketleri, CI.
- Hata raporu için log paketi toplama komutu.

## 10. Açık kararlar (önerilerle)

| Karar | Öneri |
|---|---|
| Lisans | Karar verildi: CC BY-NC-SA 4.0. Atıf zorunlu, ticari kullanım yasak, türevler aynı lisansla açık kalmak zorunda. MSCMP'den alınan kodda MIT telif notu korunur |
| Maksimum oyuncu | Mimari 4'e göre. İlk sürüm 2 oyuncuyla test edilir |
| Para | Ortak cüzdan (co-op'a uygun). Alternatif: kişisel cüzdan |
| Kayıt | Sadece host |
| Uzak araç fiziği | Önce kinematik proxy, ölçümden sonra dinamik ve PD kuvveti değerlendirilir |
