# Devam notu (kullanıcının bilgisayarında çalışacak oturum için)

Bu dosya, çalışmaya kullanıcının kendi bilgisayarında (Windows, SolidWorks 2026, PowerShell) devam edecek
Claude oturumu içindir. Önceki oturum bulutta çalıştığı için SolidWorks denemelerini yapamıyordu.

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
(`extract`) ve iki modeli karşılaştırır (`compare`). Testler: `dotnet test tests\RuleForge.Tests` (78 test).

Önemli dosyalar:
- `master.json`, `varyantlar\`: dolap (DriveWorks Cupboard) master'ı ve 9 varyantı
- `konveyor\`: konveyör hattı master'ı, 10 varyantı ve DriveWorks projesi
- `kurallar\dolap.json`: dolap için güncel kurallar (`infer` çıktısı)
- `raporlar\`: kör testler ve DriveWorks karşılaştırmaları
- `uretim\`: üretim denemelerinin okunan modelleri ve fark raporları

## Şu anki adım: dolap üretim testi (gidiş-dönüş)

Amaç: DriveWorks'ün ürettiği 0009 numaralı dolabı (yükseklik 569, genişlik 759, derinlik 759, Shaker kapak, Maple)
RuleForge'a aynı girdilerle ürettirmek ve karşılaştırmada **sadece 7 OrderNo farkı** kalana kadar düzeltmek.
OrderNo = "MF" & DriveWorks spesifikasyon no; modelden çıkarılamaz, fark olarak kalması normal.

Depo klasöründe PowerShell komutları:

```powershell
dotnet build -c Release
$rf = ".\src\RuleForge.Cli\bin\Release\net48\ruleforge.exe"
& $rf generate --rules kurallar\dolap.json --master "C:\Users\pc\Downloads\CupboardSoloTemplate\SOLIDWORKS Files\Cupboard Assy.SLDASM" --out C:\RuleForge\deneme4 --include-proposed Assembly_Height=569 Assembly_Width=759 Assembly_Depth=759 "Cupboard_LeftDoor_Shaker_Style_1_Dosyasi=Cupboard LeftDoor Shaker Style.SLDPRT" Material=Maple
& $rf extract "C:\RuleForge\deneme4\Cupboard Assy.SLDASM" -o uretim\deneme4.json
& $rf compare uretim\deneme4.json "varyantlar\Cupboard Assy 0009__Cupboard Assy Cupboard Assy9.json" -o uretim\deneme4-fark.txt
```

- Çıktı klasörü boş olmalı: her denemede yeni ad kullanın (deneme5, deneme6…).
- Kurallar değişirse: `& $rf infer varyantlar --master master.json -o kurallar\dolap.json`
  (her varyantın girdileri `kurallar\dolap.girdiler.csv` dosyasına yazılır).
- Kod değişikliğinden sonra `dotnet test tests\RuleForge.Tests` ve dolap/konveyör `infer` sonuçlarının
  bozulmadığını kontrol edin. Değişiklikleri commit edip push edin.

## Bilinen durum (deneme 3)

- 1096 / 1106 değer DriveWorks ile aynı. Kalan farklar: 7 × OrderNo (beklenen) ve
  **"Cupboard Base Assy-1/Cupboard Base-1" bastırılmış çıkıyor**. Hiçbir kural bunu istemiyor; deneme 2'de yoktu.
  Olası neden: aynı adlı "Cupboard Base.SLDPRT" başka klasörden (önceki deneme ya da master) SolidWorks belleğinde
  kalmış ve kopya yüklenememiş.
- Bunun için üretime iki koruma eklendi (`src\RuleForge.SolidWorks\ModelGenerator.cs`):
  `CheckLoaded` (açılışta master'da açık olup kopyada bastırılmış gelen bileşeni yeniden yükler, çıktı klasörü dışındaki
  dosyayı kullanan bileşeni bildirir) ve `RestoreUnexpectedSuppression` (kural istemeden bastırılan bileşeni, hangi
  eylemden sonra olduğuyla bildirir ve geri açar). Deneme 4'ün uyarıları nedeni gösterecek.
- Önceki denemelerin dersleri (koda işlendi): ölçüler özellik ağacından bulunuyor (`FindDimension`); değiştirilen parça
  dosyası master klasöründen sipariş klasörüne kopyalanıyor; bu siparişte kullanılmayan dosyaların kuralları atlanıyor;
  silinmiş özellik = bastırılmış; tüm varyantlarda sabit ama master'dan farklı değer = sabit kural ("Pos No");
  SolidWorks'ün yeniden numaraladığı özellik aileleri (CompCurve) için kural çıkarılmıyor.

## Sonraki adımlar

1. Dolap testi temiz çıkınca başka bir varyantla tekrarlayın (ör. 0001: Framed kapak, Oak, yükseklik 800;
   girdiler `kurallar\dolap.girdiler.csv`).
2. Konveyör hattı üretimi: tablo (tekrarlanan modül) içeren kural setleri için `generate` henüz yok. DriveWorks projesi
   hat montajında 5 hazır konveyör tutup kullanılmayanları siliyor (`konveyor\ConveryoSoloTemplate.driveprojx`);
   benzer bir yaklaşım uygun olabilir.
