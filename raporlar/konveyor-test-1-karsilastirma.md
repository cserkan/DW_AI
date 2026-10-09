# Kör test 2 — DriveWorks Konveyör Hattı (10 varyant) karşılaştırması

Cevap anahtarı: `konveyor/ConveryoSoloTemplate.driveprojx`. Kör sonuçlar (`konveyor-test-1.*`) cevap anahtarı açılmadan
önce üretildi ve ayrı bir commit'te kaydedildi. Formüller sadece 10 varyantta değil, formun izin verdiği tüm aralıkta
DriveWorks formülleriyle karşılaştırıldı.

## İlk deneme: tamamen başarısız

Mevcut program (dolap testinden kalan hâliyle) bu veri setini hiç okuyamadı: 774 anlamsız kural, 0 doğru kural.
Sebep yapısaldı. Her varyant, master'ın ("Conveyor Assembly") 2–5 kopyasını içeren ayrı bir "CONVEYOR LINE"
montajı. Program her varyantı master'ın tek bir kopyası sanıyordu. Bu konveyöre özel değil, genel bir eksikti.
Aynı durum çekmeceli dolap, raf sistemi ve çok bölmeli ürünlerde de olur.

## Eklenen genel yetenek: tekrarlanan modül (tablo)

- Bir master dosyasının bir varyantta birden çok farklı kopyası varsa (ör. "Conveyor Assembly -1-0007",
  "-2-0007"), bu bir **tekrarlanan modül** sayılır. Her kopya ayrı bir örnek olur: 10 varyant 32 bölüm örneği verir.
- Kopyadan kopyaya değişen değerler (bölüm uzunluğu, rulo adedi…) **tablo sütunları** olur ve kuralları her satır
  için ayrı çalışır.
- Bir varyantın tüm kopyalarında aynı olan değerler (yükseklik, genişlik, açıklamalar…) varyant düzeyinde çıkarılır.
  Bu düzeyde tablo özetleri kullanılabilir: ilk satır, toplam, satır sayısı…
- Kural motoru, doğrulayıcı, çapraz doğrulama ve `eval` komutu tabloları destekliyor:
  `"Conveyor_Assembly.Frame_D1_Overall_Length=2000;1200;600"` ile üç bölüm girilir.

## Özet (39 değişen model değeri)

| Sonuç | Adet | Açıklama |
|---|---|---|
| Doğru, formül DriveWorks ile birebir | 12 | Bölüm uzunluğuna bağlı değerler: rulo adedi `ROUNDUP((L-100)/90)` (×3), rulo aralığı `ROUNDUP((L-100)/(adet-1),2)` (×3), destek silme `L <= 1500` (×2, eşik değeri bile aynı), destek orta çizgisi `(L-76,2)/2`, boylar (×3) |
| Doğru, girdi eşlemesiyle aynı | 26 | Yüksekliğe, genişliğe, yük sınıfına ve ray yüksekliğine bağlı ölçüler, açıklamalar ve parça no'ları. Formüller DriveWorks'le aynı; girdiler modeldeki karşılıklarıyla ifade ediliyor (aşağıdaki girdi tablosu). |
| Kısmi | 1 | Destek parça no'su: "Heavy" + destekli bölüm hiçbir varyantta yoktu. Sistem bu durumda tahmin etmiyor, hata veriyor. |
| Yanlış | 0 | |
| Açıklanamayan | 0 | |

**Tam aralık testi:** Uzunluk 600–3000 aralığında her 1 mm, ayrıca 150 rastgele yükseklik/genişlik/yük/ray kombinasyonu
denendi. Sonuç: **105.241 / 105.339 değer (%99,9) DriveWorks ile aynı.** Farkın tamamı yukarıdaki "Heavy" destek parça
no'su; bu değerler için yanlış sonuç değil, açık bir hata üretiliyor.

**Çapraz doğrulama (cevap anahtarı olmadan):** %96,0. Hataların neredeyse tamamı, "Light" ve "Heavy" seçeneklerinin
birer varyantta görülmesinden kaynaklanıyor: o varyant çıkarılınca seçenek eğitimde hiç kalmıyor.
Öneri motoru da tam bunu istedi: biri Light, biri Heavy iki yeni varyant. Light olanda destek eşiğini daraltmak için
1530 / 1550 / 1580 mm'lik bölümler var.

## Girdiler

| DriveWorks | Kör test | Durum |
|---|---|---|
| NumberofConveyors (1–5) | Tablo satır sayısı (`Conveyor_Assembly_Adet`) | ✓ (veride 2–5 vardı; 1 hiç yoktu) |
| Conveyor1…5Length | Tablo sütunu `Frame_D1_Overall_Length` (her bölüm) | ✓ |
| ConveyorWidth | `Roller_D1_length` (= Genişlik − 90) | ✓ eşdeğer |
| ConveyorHeight | `Frame_D1_Floor` (= Yükseklik − çap/2 − 70) | ✓ eşdeğer; modelde yükseklik değil bu ölçü var |
| Duty (Light/Medium/Heavy) | `Roller_D1_Base_Extrude` (rulo çapı 40/50/60) | ✓ eşdeğer; metinlerde "Light/Medium/Heavy" ve "Lİ/ME/HE" çaptan seçiliyor |
| ElevatedSideRails (evet/hayır) | `LH_Rail_D1_bottom` (150/100) | ✓ eşdeğer |
| ConveyorType, ConveyorType2, C2/C4 Radius/Angle | — | Veride hiç eğri konveyör yoktu; öğrenilemez |
| IntelligentFileNaming, müşteri/teklif bilgileri | — | Kapsam dışı (dosya adı, teklif) |

Girdi adları modelden geliyor. "Frame_D1_Floor"un aslında "rulo üstü yüksekliği − çap/2 − 70" olduğunu sistem
bilemez, çünkü modelde yükseklik değeri hiç yok. Sohbet katmanında kullanıcı bunu söyleyince girdiler yeniden
adlandırılabilir.

## Kurallar (her bölüm için)

| DriveWorks | Kör test | Durum |
|---|---|---|
| RollerTop = çap/2 + 70 | `Roller_D1_Base_Extrude / 2 + 70` | ✓ birebir (güven %50; sadece 3 farklı çap vardı) |
| Rulo adedi = ROUNDUP((L-100)/90, 0) | `CEILING((L - 100) / 90)` | ✓ birebir |
| Rulo aralığı = ROUNDUP((L-100)/(adet-1), 2) | `ROUNDUP((L - 100) / (adet - 1), 2)` | ✓ birebir |
| Ray boyu, çerçeve boyu = L | `= L` | ✓ |
| Ray yüksekliği = IF(Elevated, 150, 100) | girdi | ✓ |
| Destek orta çizgisi = (L - 76,2) / 2 | `L / 2 - 38.1` | ✓ birebir |
| Destek: L > 1500 değilse sil | `L <= 1500` → bastır | ✓ birebir |
| Destek yüksekliği = Yükseklik − çap/2 − 70 − 76,2 | `Frame_D1_Floor - 76.2` | ✓ |
| Yarım genişlik = Genişlik / 2 | `Roller_D1_length / 2 + 45` | ✓ eşdeğer |
| Çerçeve, yatak, ray, rulo, destek açıklamaları | metin şablonları | ✓ |
| Parça no'ları ("CF-…", "LH-…", "R-…") | metin şablonları | ✓ |
| Destek parça no'su "CS-…-HE" | `SWITCH(çap, 40, "Lİ", 50, "ME")` | Kısmi: Heavy + destek hiç görülmedi |

## Test sırasında DriveWorks projesinde fark edilenler

1. **Tüm bölümlerin açıklaması 1. bölümün uzunluğunu yazıyor.** DriveWorks şablonunda her konveyörün açıklama ve parça
   no kuralı `Conveyor1Length` kullanıyor. Bu yüzden 1000 mm'lik 2. bölümün çerçevesinde "801 long" yazıyor.
   Sistem bunu varsaymadı, veriden buldu (`Frame_D1_Overall_Length_Ilk`). Muhtemelen şablondaki bir kopyala-yapıştır hatası.
2. **Türkçe Windows'ta UPPER().** DriveWorks `Upper(Left("Light",2))` için "LI" yerine "Lİ" üretiyor; dosya adları da
   "CONVEYOR LINE" yerine "CONVEYOR LİNE" oluyor. Gerçek üretimde parça no'larında "İ" istenmiyorsa bu düzeltilmeli.

## Testten sonra programa eklenenler (hepsi genel, konveyöre özel ayar yok)

1. Tekrarlanan modül tespiti, kopya sıralaması (dosya adındaki "-1-", "-2-"), ortak parçalar (destek, rulo)
2. İki katmanlı çıkarım: satır girdileri + genel girdiler + tablo özetleri (ilk, son, toplam, en büyük/küçük, satır sayısı)
3. Kural motorunda tablo: kapsamlı kurallar her satırda çalışır; doğrulayıcı hangi adın nerede kullanılabildiğini bilir
4. Modül için çapraz doğrulama: her varyant tüm bölümleriyle birlikte çıkarılır
5. Adet formüllerinde adım veriden de tahmin ediliyor (90 sabit listede yoktu); ofset için en "yuvarlak" değer seçiliyor
6. Aralık formülünde ROUNDUP / ROUNDDOWN; aralık formülündeki sabit, adet formülünün biçimini de seçebiliyor (çapraz kanıt)
7. Metin şablonlarında girdiye göre değişen kelimeler: "Medium/Light Duty", "ME/Lİ"
8. Her varyantta farklı olan metinler (açıklama, parça no) artık tek bir "girdi" sanılmıyor
9. Tek örneğe dayanan eşikler ve inişli-çıkışlı aralık tabloları düşük güvenle işaretleniyor (ezber önlemi)
10. Birkaç değer alan sayısal girdiler (çap 40/50/60) öneri planlamasında seçenek gibi ele alınıyor

Dolap testinin sonuçları bu değişikliklerden sonra birebir aynı kaldı (92 kural, aynı formüller).
