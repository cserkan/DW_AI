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

`generate` master dosyaları asla değiştirmez. Adımlar sırasıyla:
1. Pack and Go ile master'ı (teknik resimler dahil) sipariş klasörüne kopyalar.
2. Kopyayı açar.
3. Konfigürasyon, bastırma ve değiştirme işlemlerini, ardından ölçüleri ve özellikleri uygular.
4. Rebuild eder ve kaydeder.
5. İstenirse PDF ve STEP olarak dışa aktarır.

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

Kopyalı modellerin SolidWorks'te üretimi (kopyaları çoğaltıp hat montajına yerleştirme) henüz yok; `generate`
bu kural setleri için sadece `--dry-run` ile çalışır.

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
- **Dosya adlandırma:** Şu an her sipariş kendi klasörüne, master ile aynı dosya adlarıyla üretilir.
  PDM için benzersiz ad stratejisi (sipariş no eki) eklenecek.
- **Teknik resimler:** Görünüşler ve ölçüler rebuild ile güncellenir. Antet, not ve tablo kuralları henüz yok.
- **Arayüz:** Şimdilik komut satırı. Sırada SolidWorks Task Pane eklentisi veya WPF formu var; form, girdi
  tanımlarından otomatik oluşturulacak.
- **Gerçek SolidWorks testi:** SolidWorks katmanı 2015 interop'una karşı derleniyor ve imzalar doğrulandı.
  Ancak gerçek SolidWorks'te çalıştırma testleri Windows makinede yapılmalı.
