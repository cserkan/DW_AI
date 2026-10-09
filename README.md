# RuleForge

Yapay zekâ destekli SolidWorks konfigüratörü. DriveWorks'e benzer, tek farkla: **kuralları siz yazmazsınız.**
Kurallar iki kaynaktan çıkar:

1. **Mevcut montajlar / varyantlar:** Aynı ürünün farklı varyantları (ör. DriveWorks ile üretilmiş) incelenir,
   ölçüler, bastırmalar ve özellikler arasındaki ilişkiler otomatik bulunur.
2. **Sohbet:** Mühendis ürünü kendi cümleleriyle anlatır ("Boy 3 metreyi geçince orta destek eklensin"),
   Claude bunu kesin kurallara çevirir, belirsiz yerleri sorar.

Temel ilke: **Yapay zekâ kural önerir, insan onaylar, kuralları deterministik motor çalıştırır.**
Üretim sırasında yapay zekâ devrede değildir; aynı girdiler her zaman aynı modeli üretir.

```
 ┌──────────────────┐        ┌──────────────────┐
 │ Master montaj +  │        │  Sohbet (Claude)  │
 │ varyantlar       │        │  "Boy > 3000 ise  │
 │ (SolidWorks API) │        │   orta destek…"   │
 └────────┬─────────┘        └─────────┬────────┘
   extract│ snapshot.json              │ yapılandırılmış değişiklik
          ▼                            ▼
 ┌──────────────────┐        ┌──────────────────┐
 │ infer: ilişki    │──────▶│ kurallar.json    │◀── doğrulayıcı (her değişiklikte,
 │ arama (doğrusal, │ öneri  │ (önerilen →      │     hata varsa YZ'ye geri döner)
 │ adet, eşik, …)   │        │  onaylı)         │
 └──────────────────┘        └─────────┬────────┘
                                       │ approve (insan)
                                       ▼
                             ┌──────────────────┐   ┌───────────────────────────┐
             sipariş girdileri ─▶│ Kural motoru     │──▶│ generate: Pack and Go →  │
             (Boy=5000 …)    │ (deterministik)  │   │ ölçü/bastırma/özellik →   │
                             └──────────────────┘   │ rebuild → kaydet → PDF/STEP│
                                                    └───────────────────────────┘
```

## Proje yapısı

| Proje | Hedef | Görev |
|---|---|---|
| `RuleForge.Core` | netstandard2.0 | Snapshot ve kural veri modeli, Excel benzeri formül dili, kural motoru, doğrulayıcı |
| `RuleForge.Inference` | netstandard2.0 | Varyantlardan kural çıkarımı (yapay zekâsız, deterministik) |
| `RuleForge.AI` | netstandard2.0 + net8.0 | Claude ile sohbetle kural yazma (structured output + otomatik düzeltme döngüsü) |
| `RuleForge.SolidWorks` | net48 | SolidWorks bağlantısı: snapshot okuma, model üretme |
| `RuleForge.Cli` | net48 + net8.0 | `ruleforge` komut satırı aracı |
| `tests/RuleForge.Tests` | net8.0 | Birim testleri (SolidWorks ve API anahtarı gerektirmez) |

## SolidWorks sürüm uyumluluğu

Kod **SOLIDWORKS 2015 API**'sine karşı derlenir ve interop tipleri programa gömülür (`EmbedInteropTypes`).
SolidWorks COM arayüzlerine yeni metotları sona eklediği için 2015 API'siyle yazılmış kod **2015–2026 ve sonrası**
tüm sürümlerde çalışır. 2015'ten sonra eklenmiş API'ler (ör. `EquationMgr.GlobalVariable`, `ReplaceComponents2`)
bilerek kullanılmıyor. Ayrıntı: [`lib/SolidWorks/README.md`](lib/SolidWorks/README.md).

## Kurulum (Windows)

Gerekenler: SOLIDWORKS (2015+), [.NET 8 SDK](https://dotnet.microsoft.com/download) (net48 derlemesini de yapar),
.NET Framework 4.8 (Windows'ta zaten var), Claude API anahtarı (sadece `chat` için).

```powershell
git clone <repo> RuleForge
cd RuleForge
dotnet build -c Release
dotnet test
$env:ANTHROPIC_API_KEY = "sk-ant-..."
$rf = ".\src\RuleForge.Cli\bin\Release\net48\ruleforge.exe"
```

## İş akışı

### 1. Master montajı ve varyantları oku

```powershell
& $rf extract "C:\Master\Konveyor\Konveyor.SLDASM" -o master.json
& $rf extract-variants "C:\DriveWorks\Sonuclar" --list     # önce sadece listele
& $rf extract-variants "C:\DriveWorks\Sonuclar"            # varyantlar\ klasörüne okur
```

`extract-variants` verilen klasörü (alt klasörler dahil) tarar. Her varyant klasöründe **üst montajı otomatik
bulur**: aynı klasördeki başka hiçbir montajın kullanmadığı montaj. Alt montajlar atlanır. Referanslar SolidWorks'ün
"Referansları Bul" API'siyle dosyalar açılmadan okunur.

Snapshot; tüm belgelerin ölçülerini (mm/derece/adet), özelliklerini ve bastırma durumlarını, denklemleri ve global
değişkenleri, özel özellikleri, bileşen ağacını ve mate'leri içeren bir JSON dosyasıdır. SolidWorks'ten bağımsızdır,
her yerde işlenebilir.

### 2. Varyantlardan kural çıkar

DriveWorks'ten varyant girdilerini CSV olarak verin. İlk sütun varyant adıdır, diğerleri girdilerdir.
Türkçe Excel'in `;` ayracı ve `1200,5` ondalık virgülü desteklenir:

```
Varyant;Boy;Genislik;Motor
SP001;1500;400;Sol
SP002;2200;500;Sag
```

```powershell
& $rf infer varyantlar --inputs girdiler.csv --master master.json --name-pattern "^(.*?)_SP\d+$" -o kurallar.json
```

`--name-pattern`, DriveWorks'ün dosya adlarına eklediği ekleri temizler (`Govde_SP001.SLDPRT` → `Govde.SLDPRT`).
Dosya adları her varyantta tamamen farklı kodlarsa (ör. `100234.SLDPRT`, `100871.SLDPRT`) buna gerek yoktur:
program bunu fark eder ve parçaları **montaj ağacındaki yerleri ve özellik/ölçü adlarıyla** eşleştirir.
Bu durumda `--master master.json` verin ki kurallar kodlar yerine master'daki okunur adlarla yazılsın.
Girdi tablosu yoksa modeldeki bir ölçüyü girdi yapabilirsiniz: `--input Boy=dim:Govde.SLDPRT:D1@Boss`.

**Kör test (girdi tablosu olmadan):** `--inputs` vermezseniz program DriveWorks girdilerini kendisi tahmin eder.
Varyantlar arasında **ne değiştiğini** listeler, diğer değerlerin çoğunu açıklayan değerleri "tahmini girdi" seçer ve
kalan her şeyi bu girdilerle ifade eder. Sonuçları DriveWorks'teki gerçek kurallarla karşılaştırarak sistemi test edebilirsiniz:

```powershell
& $rf infer varyantlar -o kor-test.json
```

Bulunan ilişki türleri:

| Tür | Örnek |
|---|---|
| Doğrusal (1–2 girdi) | `2 * Boy + 300`, `Boy - 0.5 * Genislik + 15` |
| Adet / basamak | `CEILING(Boy / 1500) + 1` |
| Eşik (bastırma) | `Boy <= 3000` |
| Seçime göre | `SWITCH(Malzeme, "Paslanmaz", 2, "Galvaniz", 1.5)`, `Motor = "Sag"` |
| Seçime göre farklı doğrular | `SWITCH(Tip, "A", Boy + 10, "B", Boy + 25)` |
| Aralık tablosu | `RANGELOOKUP(Genislik, 500, 40, 60)` |
| Metin şablonu | `"KONVEYOR " & Boy & "x" & Genislik` |
| Sayılı metin | `"KONVEYOR " & ((Bant - 300) / 2) & "x" & Genislik` |

Çıkan her kural **önerilen** durumundadır ve güven puanı, dayanak bilgisi taşır. Veriyle açıklanamayan değişimler
ve belirsizlikler raporda soru olarak listelenir. Örneğin: "eşik 2800 ile 3400 arasında, kesin değer nedir?".

**Daha iyi sonuç için:** 8–15 varyant üretin. Girdilerin uç değerlerini (en küçük/en büyük boy) kapsayın.
Girdileri birbirinden bağımsız değiştirin; her zaman birlikte artan Boy ve Genişlik ilişkileri karıştırır.

### 3. Sohbetle tamamla ve düzelt

```powershell
& $rf chat --rules kurallar.json --snapshot master.json --report kurallar.cikarim.txt
```

```
> Orta destek boy 3000'i GEÇİNCE gelsin, 3000 dahil değil. Ayaklar arası en fazla 1500 mm olsun.
> Kapak ölçüsü boydan 120 eksik olsun ama 2500'ü geçmesin.
> /dene Boy=3200 Genislik=650 Motor=Sol
> /onayla hepsi
```

Claude yalnızca model özetindeki gerçek ölçü, bileşen ve özellik adlarını kullanabilir. Her yanıt doğrulayıcıdan
geçer: tanımsız ad, olmayan ölçü, sıfıra bölme gibi hatalar Claude'a otomatik geri gönderilir ve düzeltilir.
Kuralları onaylama yetkisi sadece kullanıcıdadır.

### 4. Doğrula, onayla, üret

```powershell
& $rf validate --rules kurallar.json --snapshot master.json
& $rf approve  --rules kurallar.json --all --min-confidence 0.9
& $rf eval     --rules kurallar.json Boy=5000 Genislik=700 Motor=Sol
& $rf generate --rules kurallar.json --master C:\Master\Konveyor\Konveyor.SLDASM --out C:\Siparisler\S1234 Boy=5000 Genislik=700 Motor=Sol --pdf --step
```

**Benzersiz dosya adları ve kütüphane.** Parçalar ve alt montajlar ortak bir kütüphane klasörüne benzersiz adlarla
yazılır (`Frame-0003.SLDPRT`); sipariş klasörüne sadece ana montaj ve teknik resmi konur (`Cupboard Assy S1234.SLDASM`).
Aynı içerikte bir dosya daha önce üretildiyse yenisi yazılmaz, kütüphanedeki kullanılır. İçerik, dosyanın son hâlinden
hesaplanan parmak iziyle tanınır: ölçüler, özellik bastırmaları, özel özellikler, alt bileşenler ve master dosyanın kendisi
(master değişirse eski kopyalar kullanılmaz). Liste `Kutuphane\kutuphane.json` dosyasındadır (dosya, master, ilk sipariş,
açıklama). Varsayılan kütüphane sipariş klasörünün yanındaki `Kutuphane` klasörüdür; `--library <klasör>` ile değiştirilir,
`--no-library` eski davranıştır (her şey sipariş klasörüne master adlarıyla).

`generate` master dosyaları asla değiştirmez: üretim boyunca master klasöründeki SolidWorks dosyaları salt-okunur yapılır,
sadece çıktı klasöründeki belgeler tek tek kaydedilir ve kopya başka bir dosyaya bağlı açılırsa hiçbir şey uygulanmaz.
Adımlar sırasıyla:
1. Pack and Go ile master'ı (teknik resimler dahil) sipariş klasörüne kopyalar.
2. Kopyayı açar.
3. Konfigürasyon, bastırma ve değiştirme işlemlerini, ardından ölçüleri ve özellikleri uygular.
   Değiştirilen parçanın yeni dosyası (ör. başka bir kulp) master klasöründe aranır ve sipariş klasörüne kopyalanır.
   Bu siparişte kullanılmayan dosyalara ait kurallar (ör. başka kapak tipinin ölçüleri) atlanır; bunlar hata sayılmaz.
4. Rebuild eder ve kaydeder.
5. İstenirse PDF ve STEP olarak dışa aktarır.

### 5. Üretilen modeli kontrol et (gidiş-dönüş testi)

Bir DriveWorks varyantının girdileriyle (`infer` her varyantın girdilerini `kurallar.girdiler.csv` dosyasına yazar)
model üretin, üretilen modeli okuyun ve DriveWorks'ün ürettiğiyle karşılaştırın:

```powershell
& $rf extract "C:\Siparisler\deneme1\Montaj.SLDASM" -o uretim\deneme1.json
& $rf compare uretim\deneme1.json "varyantlar\<aynı girdili DriveWorks varyantı>.json" -o uretim\deneme1-fark.txt
```

Dosya adları farklı olsa da parçalar yapılarına göre eşleştirilir. Silinmiş bileşen, bastırılmış bileşenle aynı sayılır.
Değiştirilen parçalar tek satırda "sadece bir modelde" olarak gösterilir.

## Her montaj setinde çalışması için: güvenilirlik ve bir sonraki varyantlar

Program varyantlardan sadece **veriyle desteklenenleri** öğrenebilir. Bu yüzden iki araç, bilmediğini bilir ve çözümünü söyler.

**1. Yeni varyant önerisi (`infer` kendiliğinden yapar).** Belirsiz kalan her nokta için hangi girdi değerlerinin DriveWorks'te
üretilmesi gerektiğini söyler ve bunları girdi tablosu olarak `kurallar.oneriler.csv` dosyasına yazar:

| Belirsizlik | Önerilen varyant |
|---|---|
| Eşik iki değer arasında (569–800) | Aralığı daraltan değerler (625, 685, 740); aralık daralınca sınırın dahil olup olmadığını gösteren tam eşik ve +1 |
| Eşiği iki girdiden hangisi belirliyor? | İkisi birbirine zıt sonuç verecek iki varyant (biri düşük, diğeri yüksek) |
| Adet formülündeki sabit belirsiz (`Boy − 68` mi `− 66` mı) | Adedin değiştiği sınırdaki girdi değerleri |
| Formül sadece 2 noktaya dayanıyor | Ara bir değer |
| Bir seçenek sadece 1 varyantta var | O seçenekle bir varyant daha |
| Girdi aralığında büyük boşluk | Boşluğun ortası |
| İki girdi hep birlikte değişiyor | Önerilen varyantlarda girdiler birbirinden bağımsız seçilir (Halton dizisi) |

Önerilen varyantları DriveWorks'te üretip aynı komutu tekrar çalıştırın; belirsizlikler her turda daralır.

**2. Çapraz doğrulama (`crossval`).** Cevap anahtarı olmadan kuralların yeni girdilerde ne kadar işe yaradığını ölçer:
her varyant sırayla çıkarılır, kurallar kalanlardan öğrenilir, çıkarılan varyantın değerleri tahmin edilip gerçekle karşılaştırılır.
Çıktı: genel doğruluk, her tur için sonuç ve **riskli kurallar** (yanlış çıkan, kararsız kalan).

```powershell
& $rf crossval varyantlar --master master.json -o capraz-dogrulama.txt
```

## Tekrarlanan modüller (tablo girdileri)

Bazı ürünlerde master'ın ya da bir alt montajının birden çok kopyası bulunur. Örnek: 2–5 bölümlük bir konveyör hattı,
her bölüm farklı uzunlukta. `infer` bunu kendiliğinden tanır: aynı master dosyasının bir varyantta birden çok farklı
kopyası varsa her kopya ayrı bir örnek sayılır.

- Kopyadan kopyaya değişen değerler (bölüm uzunluğu…) bir **tablonun sütunları** olur. Bu değerlere bağlı kurallar
  her satır için ayrı çalışır (`"scope": "<tablo>"`).
- Tüm kopyalarda aynı olan değerler (yükseklik, genişlik…) genel girdilerdir.
- Her formülde kullanılabilen tablo özetleri: `<Tablo>_Adet` (satır sayısı), `<sütun>_Ilk`, `_Son`, `_Toplam`,
  `_EnBuyuk`, `_EnKucuk`. Satır kurallarında ayrıca `<Tablo>_Sira` (satır numarası) kullanılabilir.

Satırlar komut satırında `Tablo.Sütun=değer1;değer2;...` biçiminde, çift tırnak içinde verilir:

```powershell
& $rf eval --rules kurallar.json --include-proposed Frame_D1_Floor=750 "Conveyor_Assembly.Frame_D1_Overall_Length=2000;1200;600"
```

Kopyalı modeller de üretilir. `--master` modülün kendisi, `--root` kopyaları toplayan montajdır (DriveWorks
projesindeki hat montajı; içinde yer tutucu bileşenler vardır):

```powershell
$t = "C:\...\ConveryoSoloTemplate\SOLIDWORKS Files"
& $rf generate --rules konveyor\kurallar.json --master "$t\Straight Conveyor\Conveyor Assembly.SLDASM" `
  --root "$t\Conveyor Line.SLDASM" --out C:\Siparisler\H1 --include-proposed `
  LH_Rail_D1_bottom=150 Roller_D1_length=761 Roller_D1_Base_Extrude=50 Frame_D1_Floor=556 `
  "Conveyor_Assembly.Frame_D1_Overall_Length=801;1000;600"
```

1. Her satır için modül ayrı dosya adlarıyla üretilir (`Conveyor Assembly-1.SLDASM`, `Frame-1.SLDPRT`, `-2`, `-3`…).
2. Tüm satırlarda aynı dosyayı kullanan parçalar (`infer` bunları `sharedDocuments` olarak yazar; konveyörde makara
   ve destek) tek dosyaya indirilir: kuralların değer yazdığı ilk satırın kopyası kullanılır.
3. Kök montaj kopyalanır; yer tutucular örnek numarası sırasıyla (ör. `Conveyor Dummy 1-5`, `-8`, `-9`, `-10`, `-11`)
   satırların montajlarıyla değiştirilir, artanlar ve onlara bağlı ilişkiler silinir. Sıra farklıysa:
   `--slots "Ad-5;Ad-8;..."`.
4. Kök montajın kuralları (ör. kopyalar arası ilişkiler) uygulanır, kaydedilir.

## Kural dili

Excel'e benzer. Adlar büyük/küçük harf duyarsızdır. Açılar **derece**, uzunluklar **mm** cinsindendir.

- Operatörler: `+ - * / ^ %`, `= <> < <= > >=`, `&& || !`, `&` (metin birleştirme)
- Mantık: `IF`, `IFS`, `SWITCH`, `AND`, `OR`, `NOT`, `RANGELOOKUP`
- Matematik: `MIN`, `MAX`, `ABS`, `SQRT`, `POWER`, `ROUND`, `ROUNDUP`, `ROUNDDOWN`, `CEILING`, `FLOOR`, `MROUND`,
  `INT`, `MOD`, `CLAMP`, `PI`, `SIN/COS/TAN/ASIN/ACOS/ATAN/ATAN2` (derece)
- Metin: `CONCAT`, `TEXT`, `UPPER`, `LOWER`, `LEN`, `LEFT`, `RIGHT`, `CONTAINS`, `NUMBER`

Kural hedefleri: `dimension`, `globalVariable`, `featureSuppression`, `componentSuppression`, `componentReplace`,
`configuration`, `customProperty`, `outputFileName`.

Örnek `kurallar.json` parçası:

```json
{
  "inputs": [ { "name": "Boy", "type": "number", "min": 1000, "max": 8000, "default": "3000", "unit": "mm" } ],
  "variables": [ { "name": "AyakAdedi", "expression": "CEILING(Boy / 1500) + 1" } ],
  "rules": [
    {
      "id": "orta_destek",
      "target": { "kind": "componentSuppression", "component": "OrtaDestek-1" },
      "expression": "Boy <= 3000",
      "status": "approved",
      "source": "chat",
      "evidence": "Kullanıcı: orta destek boy 3000'i geçince gelsin"
    }
  ]
}
```

## SolidWorks olmadan deneme (her işletim sistemi)

`samples/konveyor-demo` klasöründe 10 sentetik konveyör varyantı vardır. Bunlar gizli kurallarla üretildi;
çıkarım motoru bu kuralları geri bulmalıdır.

```bash
cd samples/konveyor-demo
RF="dotnet ../../src/RuleForge.Cli/bin/Debug/net8.0/ruleforge.dll"
$RF infer varyantlar --inputs girdiler.csv --master master.json --name-pattern '^(.*?)_SP\d+$' -o kurallar.json
$RF approve --rules kurallar.json --all --min-confidence 0.9
$RF eval --rules kurallar.json Boy=5000 Genislik=700 Motor=Sol
$RF chat --rules kurallar.json --snapshot master.json --report kurallar.cikarim.txt   # ANTHROPIC_API_KEY gerekir
```

## Bilinen sınırlamalar / yol haritası

- **Aynı parçanın farklı boyutlu örnekleri:** Bir dosyadaki ölçü değişince o dosyanın tüm örnekleri değişir.
  Farklı boyutlu örnekler için parçanın ayrı kopyası gerekir (DriveWorks'teki "farklı ad ile kaydet" kuralı). Planlı.
- **Dosya adlandırma:** Kütüphane adları master adı + sıra no (`Frame-0003`). Parametreli okunur adlar (DriveWorks
  "intelligent file naming" gibi) için bir adlandırma kuralı henüz yok.
- **Teknik resimler:** Görünüşler ve ölçüler rebuild ile güncellenir. Antet, not ve tablo kuralları henüz yok.
- **Arayüz:** Yerel tarayıcı arayüzü var (aşağıda). Kural düzenleme, onay ve "şirket kuralları" bölümü henüz yok.

## Arayüz

```powershell
& $rf arayuz            # ya da depo klasöründeki "RuleForge Arayuz.cmd" dosyasına çift tıklayın
```

Bilgisayarda küçük bir sunucu başlar (sadece bu bilgisayardan erişilir, `http://127.0.0.1:5050/`) ve tarayıcı açılır.
Ürünler `urunler.json` dosyasındadır: kural dosyası, master montaj, kopyaları toplayan montaj (kök) ve formdaki Türkçe
etiketler, birimler, seçenek listeleri. Sayfada:

- Ürün seçilir; form kural dosyasının girdilerinden oluşur. Tekrarlanan modüllerde (konveyörler) satır eklenip silinir.
  Varyantlarda görülen aralığın dışındaki değerler işaretlenir.
- Değer değiştikçe kurallar anında hesaplanır; modele uygulanacak tüm değişiklikler aranabilir bir listede görünür.
- **Üret:** SolidWorks'te arka planda üretir, günlüğü canlı gösterir; sonunda "SolidWorks'te aç" ve "Klasörü aç".
  Siparişler `SiparisKlasoru\<ürün>\<sipariş>`, parçalar `Kutuphane\<ürün>` altına yazılır.
- **Siparişler** ve **Kütüphane** sekmeleri önceki siparişleri ve kütüphanedeki dosyaları (açıklama, ilk sipariş) listeler.
