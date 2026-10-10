# Devam notu (kullanıcının bilgisayarında çalışacak oturum için)

Bu dosya, çalışmaya kullanıcının kendi bilgisayarında (Windows, SolidWorks 2026, PowerShell) devam edecek
Claude oturumu içindir.

## Kullanıcı

- **Her şeyi Türkçe yazın**, aradaki kısa durum notları dahil. Kullanıcı yazılımcı değil; sade anlatın.
- Kullanıcının talimatı: *"Denemeleri sen kendin benim bilgisayarımda yapabilirsin, beni uğraştırma. Bundan sonra
  hatalar çıkarsa yine kendin denemeler yap, en doğru sonuca ulaşana kadar."* Üretim → okuma → karşılaştırma
  döngüsünü kendiniz çalıştırın; sonuçları ve yaptığınız düzeltmeleri kısaca bildirin.
- SolidWorks ve PowerShell açık. RuleForge çalışan SolidWorks'e bağlanır (`SwSession.Connect`).
- Master dosyaları asla değiştirilmez; üretim her zaman yeni bir çıktı klasörüne yapılır.

## Proje

RuleForge: yapay zekâ destekli, DriveWorks benzeri SolidWorks konfigüratörü (C#). Genel bilgi `README.md`'de.
Varyantlardan kural çıkarır (`infer`), çapraz doğrulama yapar (`crossval`), modeli üretir (`generate`), okur
(`extract`) ve iki modeli karşılaştırır (`compare`). Testler: `dotnet test tests\RuleForge.Tests` (83 test;
`RealDataTests` depodaki gerçek varyantları ve `uretim\` dosyalarını kullanır).

Önemli dosyalar:
- `master.json`, `varyantlar\`: dolap (DriveWorks Cupboard) master'ı ve 9 varyantı
- `kurallar\dolap.json`: dolap kuralları (`infer` çıktısı; girdiler `kurallar\dolap.girdiler.csv`)
- `konveyor\`: konveyör modülü master'ı (`master.json`), hat master'ı (`hat-master.json`), 10 varyant, DriveWorks projesi
- `konveyor\kurallar.json`: konveyör kuralları (girdiler `konveyor\kurallar.girdiler.csv`, her satır bir konveyör)
- `raporlar\`: kör testler ve DriveWorks karşılaştırmaları
- `uretim\`: üretim denemelerinin okunan modelleri ve fark raporları

## Komutlar (depo klasöründe PowerShell)

```powershell
dotnet build -c Release
$rf = ".\src\RuleForge.Cli\bin\Release\net48\ruleforge.exe"

# Dolap (0009)
& $rf generate --rules kurallar\dolap.json --master "C:\Users\pc\Downloads\CupboardSoloTemplate\SOLIDWORKS Files\Cupboard Assy.SLDASM" --out C:\RuleForge\denemeX --include-proposed Assembly_Height=569 Assembly_Width=759 Assembly_Depth=759 "Cupboard_LeftDoor_Shaker_Style_1_Dosyasi=Cupboard LeftDoor Shaker Style.SLDPRT" Material=Maple
& $rf extract "C:\RuleForge\denemeX\Cupboard Assy.SLDASM" -o uretim\denemeX.json
& $rf compare uretim\denemeX.json "varyantlar\Cupboard Assy 0009__Cupboard Assy Cupboard Assy9.json" -o uretim\denemeX-fark.txt

# Konveyör hattı (0001: 3 konveyör)
$t = "C:\Users\pc\Downloads\ConveryoSoloTemplate\SOLIDWORKS Files"
& $rf generate --rules konveyor\kurallar.json --master "$t\Straight Conveyor\Conveyor Assembly.SLDASM" --root "$t\Conveyor Line.SLDASM" --out C:\RuleForge\hatX --include-proposed LH_Rail_D1_bottom=150 Roller_D1_length=761 Roller_D1_Base_Extrude=50 Frame_D1_Floor=556 "Conveyor_Assembly.Frame_D1_Overall_Length=801;1000;600"
& $rf extract "C:\RuleForge\hatX\Conveyor Line.SLDASM" -o uretim\hatX.json
& $rf compare uretim\hatX.json "konveyor\varyantlar\Conveyor assembly 0001__CONVEYOR LİNE 0001.json" -o uretim\hatX-fark.txt
```

- Çıktı klasörü boş olmalı: her denemede yeni ad kullanın.
- Kurallar değişirse: `& $rf infer varyantlar --master master.json -o kurallar\dolap.json` ve
  `& $rf infer konveyor\varyantlar --master konveyor\master.json -o konveyor\kurallar.json`.
- Kod değişikliğinden sonra testleri ve iki `infer` raporunun (`*.cikarim.txt`) beklenmedik biçimde değişmediğini
  kontrol edin. Değişiklikleri commit edip push edin.
- PowerShell 5.1 BOM'suz `.ps1` betiklerini yanlış kodlamayla okur (Türkçe karakterler bozulur): betikleri BOM'lu UTF-8
  kaydedin. Kaynak dosyalarda ise BOM yok; `Set-Content -Encoding utf8` BOM ekler, dikkat.

## Arayüz (10 Ekim 2026)

- Kullanıcı geri bildirimi: ilk arayüz "çok karmaşık" ve ürüne özeldi (Dolap/Konveyör sekmeleri). İstenen: **genel ürün
  arayüzü**, üç adım: 1) "Varyantlarınızı buraya yükleyin", 2) Kurallar (çıkarılan kurallar), 3) önerilen form + üret.
  Böyle yeniden yapıldı; sade tutun, ürüne özel kod/ayar eklemeyin.
- `ruleforge arayuz` (ya da `RuleForge Arayuz.cmd`): `src\RuleForge.Cli\Arayuz\` (`ArayuzSunucu.cs` TcpListener tabanlı
  HTTP sunucusu, `Projeler.cs` ürün klasörleri, `index.html` gömülü tek sayfa). Ürünler `C:\RuleForge\Projeler\<id>`.
  Örnek ürünler: `dolap`, `konveyor-hatti` (master yolları Downloads'u gösterir, okunmuş varyantlar kopyalandı).
  Üretim/okuma STA iş parçacığında; aynı anda tek SolidWorks işi.
- Uçtan uca denendi (yalnızca API ile, tarayıcının yaptığı istekler): yeni ürün → özgün dolap şablonu (45 dosya) ve
  DriveWorks Results klasörü (169 dosya) yüklendi → 9 varyant okundu → 109 kural → S0001 üretildi → DriveWorks 0009 ile
  1152/1160 aynı (sadece OrderNo). Ekran görüntüleri görünmez (headless) Chrome ile alındı.
- **Kullanıcının ekranına fare tıklaması göndermeyin**: bir kez tıklama, kullanıcının önde açık Gezgin penceresine gitti.
  Görüntü için headless Chrome kullanın (`chrome --headless=new --screenshot ... http://127.0.0.1:5050/#<ürün>/<adım>`).
- Sıradaki (kullanıcı onaylı): **şirket bilgisi** özelliği (aşağıda "Sıradaki özellikler"); arayüzde "Şirket kuralları"
  bölümü olacak. Ayrıca arayüzde kural onaylama/reddetme henüz yok.

## Kütüphane ve master koruması (10 Ekim 2026)

- **Benzersiz adlar + yeniden kullanım** (kullanıcı isteği): `generate` varsayılan olarak parçaları sipariş klasörünün
  yanındaki `Kutuphane` klasörüne `Master-NNNN` adlarıyla yazar; aynı içerik (parmak izi) daha önce üretildiyse onu kullanır
  (`LibraryPublisher`, `PartLibrary`). Model önce `<sipariş>.calisma` klasöründe kurulur, sonra taşınır.
  Doğrulama (`C:\RuleForge\test1`): dolap 0009/0009 tekrar/0001/0008 ve hat 0001/0001 tekrar/0010/0008, hepsi DriveWorks ile
  aynı (dolapta sadece OrderNo); aynı sipariş tekrarında 0 yeni dosya; 0010'daki dört özdeş konveyör tek dosya takımı.
- **Olay:** denemeler sırasında master dosyaları değişti (konveyör modülü, `Conveyor Line`, `Cupboard Assy`). Nedenler:
  kopya master'a bağlı açılınca üretim durmuyordu ve `SaveReferenced` master'ları kaydediyordu; bellekte gizli kalan aynı adlı
  master, kopya yerine açılabiliyordu. Özgün dosyalar `Downloads\*-DriveWorksSolo-V1.zip`'ten geri yüklendi ve `master.json`,
  `konveyor\master.json`, `konveyor\hat-master.json` ile birebir aynı oldukları doğrulandı; bozulmuş hâller
  `C:\RuleForge\master-yedek-bozulmus-20261010` klasöründe. Korumalar: üretim boyunca master dosyaları salt-okunur, açılış
  kontrolü başarısızsa hiçbir şey uygulanmaz, açılan belge kopya değilse durur, sadece çıktı klasöründeki belgeler tek tek
  kaydedilir, sonunda master tarihleri kontrol edilir. **Her denemeden sonra master tarihlerini kontrol etmeye devam edin.**
- `%TEMP%` altında kurulan kopyalarda SolidWorks sonekli alt montajları bulamayıp master'lara bağlanıyor: çalışma klasörünü
  orada açmayın.
- Dolap klasöründe 9 Ekim 14:28–16:09 tarihli değişmiş dosyalar bu oturumdan önce; dokunulmadı.

## Bilinen durum (10 Ekim 2026)

- **Dolap gidiş-dönüş testi 4 varyantta temiz:** 0009, 0001, 0008, 0010; her birinde kalan 8 fark yalnızca OrderNo
  (7 parça + ana montaj). OrderNo = "MF" & DriveWorks spesifikasyon no; modelden çıkarılamaz.
- **Konveyör hattı gidiş-dönüş testi 10 varyantın 10'unda birebir aynı** (`uretim\hat000N-son-fark.txt`, 0 fark;
  2–5 konveyörlü hatlar). Hat üretimi (`ModelGenerator.GenerateLine`):
  1. Her satır için modül master'ı `-1`, `-2`… ekli dosyalarla üretilir (mevcut `Generate`, `FileSuffix` ile).
  2. Ortak parçalar (`sharedDocuments`: Roller, Support) tek dosyaya indirilir: değer yazılan ilk satırın kopyası;
     diğer satırların montajları `ReplaceReferencedDocument` ile (dosyalar kapalıyken) ona bağlanır.
  3. `Conveyor Line.SLDASM` kopyalanır; 5 yer tutucu (`Conveyor Dummy 1-5, -8, -9, -10, -11`) örnek numarası sırasıyla
     satırlarla değiştirilir, artanlar silinir (ilişkileri de silinir; bunlara ait kurallar atlanır).
- Konveyör dersleri (koda işlendi):
  - Karşılaştırmada bileşenler ağaç sırasından çok örnek numarasıyla eşlenir (DriveWorks ağacı 9, 8, 5 sırasında).
  - Ad karşılaştırmasında sondaki kopya/sipariş numaraları atılır ("RH Rail Assembly-2" ↔ "RH Rail Assembly RH RAİL ASSEMBLY-2-0001").
  - Aynı master adını taşıyan dosya içeriği farklı görünse de "parça değişimi" sayılmaz (DriveWorks kısa konveyörde
    destekleri ve ilişkilerini siliyor).
  - Bir SWITCH, aynı girdideki daha tam bir SWITCH'in alt kümesiyse tamamlanır (destek parça numarası 60 → "HE"
    seçeneğini makara parça numarasından aldı; yoksa 0010 üretilemiyordu).
- SolidWorks notları:
  - Önceki bir üretimden bellekte **gizli** belge kalırsa `CloseDoc` kapatmıyor; üretim bunu algılayıp durur.
    Çare: SolidWorks'ü kapatıp açmak. Gizli belgeyi `Visible = true` yapmak SolidWorks'ü çökertti, denemeyin.
  - SolidWorks'ü yeniden başlatmanız gerekirse Gezgin üzerinden başlatın:
    `explorer.exe "C:\Program Files\SOLIDWORKS Corp\SOLIDWORKS (4)\SLDWORKS.exe"`. `Start-Process` ile Claude'un kabuğundan
    başlatılana bağlanılıyor ama ilk uzun çağrıda takılıyor (RPC_E_SYS_CALL_FAILED).
  - Bir kez okuma sırasında SolidWorks kendiliğinden kapandı; RuleForge kapalıysa kendisi açıyor (o zaman dışarıdan
    `GetActiveObject` ile bulunamayabilir).
- Önceki dersler (koda işlendi): ölçüler özellik ağacından bulunuyor; değiştirilen parça dosyası master klasöründen
  kopyalanıyor; kullanılmayan dosyaların kuralları atlanıyor; silinmiş özellik = bastırılmış; sabit ama master'dan
  farklı değer = sabit kural; yeniden numaralanan özellik aileleri için kural yok; sadece bazı varyantlarda görülen sabit
  metin, değişen bir girdiye eşitse o girdi olur (Framed kapak `DWMaterial = Material`); JSON sayıları en kısa biçimde.

## Sonraki adımlar (kullanıcıyla konuşun)

1. Kavisli konveyör (`Curved Conveyor Assembly`): DriveWorks projesi 2. ve 4. konveyörde düz/kavisli seçimi yapıyor
   ama elimizdeki 10 varyantın hepsi düz. Kavisli varyantlar üretilirse çıkarım ve üretim genişletilmeli.
2. Konveyör girdileri modelden tahmin edilmiş adlar taşıyor (`Frame_D1_Floor` vb.); DriveWorks'teki gerçek girdi
   adlarıyla (Duty, Height, Width…) bir girdi tablosu verilirse kurallar daha okunur olur.
3. Teknik resim/PDF ve PDM için benzersiz dosya adı stratejisi (README "Bilinen sınırlamalar").

## Sıradaki özellikler (kullanıcı onaylı)

**Sıra (kullanıcının talimatı):** önce devam eden iş bitecek (dolap üretim testi, ardından arayüz çalışması).
Şirket bilgisi özelliği **arayüzden sonra** eklenecek; arayüz bitmeden başlanmayacak. Kullanıcı uyuyor, soru
sormadan bu sırayla ilerleyin. Bulut oturumu ve yerel oturum aynı depoda aynı anda çalışmasın; bu özelliği
yerel oturum yapar.

### Şirket bilgisi (müşteriye özel öğrenme)
Amaç: mühendislerle yapılan konuşmalardan öğrenilen, kural olmayan bilgiler şirkete özel kalıcı olarak birikir ve
yapay zekâ her konuşmanın başında bunu okur. Kural setleri zamanla büyür.

- İki ayrı kayıt: **kurallar** (JSON; çalıştırılır, onaylı/önerilmiş/reddedilmiş) ve **şirket bilgisi** (açıklamalar,
  istisnalar, alışkanlıklar, cevaplanmamış sorular, reddedilen öneriler ve nedenleri).
- Kullanıcıya markdown olarak gösterilmez. Arayüzde "Şirket kuralları" adlı bir bölüm olur; her kayıt başlık, metin,
  kimin ne zaman söylediği ve durumu olan bir kart ya da form. Arka planda yapay zekâ için markdown'a dönüştürülür.
- Depolama: şirket (tenant) başına sunucuda, kullanıcının bilgisayarında değil. Şirketler birbirinden ayrı;
  Claude API anahtarı sunucuda tutulur, tarayıcıya gitmez.
- Yapay zekâ kuralı ya da notu kendi başına değiştirmez; öneri yazar, onay mühendisten gelir. Geçmiş (kim, ne zaman)
  tutulur; çelişen bilgi üzerine yazılmaz, birlikte gösterilir.
- Reddedilen kural sonraki çıkarımda yeniden önerilmemeli (çıkarım şu an `Rejected` durumuna bakmıyor).
- Notlar büyüyünce konu başlıklarıyla özetlenir.
