# Kör test 1 — DriveWorks Cupboard (9 varyant) karşılaştırması

Cevap anahtarı: `CupboardSoloTemplate.driveprojx`. Formüller sadece 9 varyantta değil, formun izin verdiği tüm aralıkta
(Yükseklik 400–1500, Genişlik 700–1500) DriveWorks formülleriyle karşılaştırıldı.

## Özet (82 model kuralı)

| Sonuç | Adet | Açıklama |
|---|---|---|
| Birebir aynı / eşdeğer | 66 | Ölçüler, çivi adet ve aralıkları, raf aralığı, pim delikleri, kapak değişimi, malzeme özellikleri |
| Formül doğru, eşik farklı | 6 | Raf sayısı ve ona bağlı kurallar: DriveWorks 600, ben 750. Verilerde 569–800 arası hiç yoktu. |
| Bulunamaz (doğru davranış) | 8 | OrderNo = "MF" & spesifikasyon no. Geometriden çıkarılamaz, "açıklanamayan" olarak listelendi. |
| Yanlış girdi | 2 | Kulp: DriveWorks'te ayrı "Handles" girdisi var; 9 varyantta malzemeyle hep birlikte değiştiği için ayırt edilemedi. Soru olarak işaretlendi. |

Kapsam dışı (henüz çıkarılmıyor): dosya adlandırma kuralları, teknik resim kuralları (tarih, kullanıcı, ölçek), fiyat/teklif değişkenleri.

## Girdiler

| DriveWorks | Kör test | Durum |
|---|---|---|
| Height, Width, Depth | Assembly_Height / Width / Depth | ✓ |
| Material | Material | ✓ |
| DoorStyle (Flat / Glass / Shaker) | Kapak dosyası seçimi (Cupboard LeftDoor / Framed / Shaker Style) | ✓ |
| Handles | Material ile birleşti | ✗ (soru soruldu) |
| QuoteNumber | — | ✗ (9 varyantta hep boştu) |
| IntelligentFileNaming | — | Sadece dosya adlarını etkiliyor (kapsam dışı) |

## Testten sonra programa eklenenler

1. Yuvarlamalı doğrusal formüller: `ROUND((H - 86) / 3)`
2. Eşiğe göre değişen formüller: `IF(H <= eşik, ..., ...)`
3. Ortak eşik değişkeni: 7 kural tek `Assembly_Height_Esigi` değişkenini kullanıyor (DriveWorks'teki ShelfQty gibi).
4. Ölçüden ölçüye ilişki: ikinci pim deliği = 2 × birinci − 35
5. Eşit aralık formülü: çivi aralığı = (Genişlik − 18) / (adet − 1)
6. Çapraz kanıt: aralık formülündeki "− 18", adet formülündeki belirsiz sabiti kesinleştirdi.
7. DriveWorks dosya adı çözümü ("<MASTER:NAME>" & ID) ve parça değişimi tespiti (kapak, kulp)
8. Ağırlık gibi SolidWorks'ün hesapladığı özellikler kural dışında bırakıldı.
9. Girdi olarak seçilen değerlerin kendisi de modele yazılıyor (kimlik kuralları).
