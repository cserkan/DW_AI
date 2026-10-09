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

## Bilinen durum (9 Ekim 2026)

- **Dolap gidiş-dönüş testi 4 varyantta temiz** (her birinde kalan 8 fark yalnızca OrderNo: 7 parça + ana montaj;
  deneme 3'te Cupboard Base bastırıldığı için 7 görünüyordu):
  0009 Shaker/Maple (`uretim\deneme6-fark.txt`, 1152/1160), 0001 Framed/Oak (`uretim\v0001-deneme2-fark.txt`, 1179/1187),
  0008 düz kapak/Oak, yükseklik 458 eşik altı (`uretim\v0008-deneme1-fark.txt`, 1130/1138),
  0010 düz kapak/Mahogany (`uretim\v0010-deneme1-fark.txt`, 1181/1189).
- Bir kez 0008 okunurken SolidWorks kendiliğinden kapandı; tekrar okuyunca sorun çıkmadı. RuleForge SolidWorks kapalıysa
  kendisi açıyor (o zaman SolidWorks görünmez çalışabilir ve dışarıdan `GetActiveObject` ile bulunamaz).
- Deneme 3'teki bastırılmış Cupboard Base'in nedeni: deneme 3'ün `Cupboard Base.SLDPRT` dosyası SolidWorks belleğinde
  **gizli** olarak açık kalmıştı. `CloseDoc` onu kapatmıyor; üretim bunu algılayıp duruyor. Çare: SolidWorks'ü kapatıp açmak.
  Gizli belgeyi `Visible = true` yapmak SolidWorks'ü çökertti, denemeyin.
- **SolidWorks'ü yeniden başlatmanız gerekirse** Gezgin üzerinden başlatın:
  `explorer.exe "C:\Program Files\SOLIDWORKS Corp\SOLIDWORKS (4)\SLDWORKS.exe"`. `Start-Process` ile Claude'un kabuğundan
  başlatılan SolidWorks'e bağlanılıyor ama ilk uzun çağrıda takılıyor (RPC_E_SYS_CALL_FAILED).
- Yeni ders (koda işlendi): değer sadece bazı varyantlarda var ve hep aynıysa (Framed kapağın `DWMaterial`'ı, Framed'ı
  kullanan 3 varyantın hepsi Oak), değişen bir girdiye birebir eşitse o girdi kural olur (`= Material`).
- JSON'da sayılar artık en kısa biçimde yazılıyor (Windows'taki .NET Framework 0.94'ü 0.93999999999999995 yazıyordu).
- Önceki denemelerin dersleri (koda işlendi): ölçüler özellik ağacından bulunuyor (`FindDimension`); değiştirilen parça
  dosyası master klasöründen sipariş klasörüne kopyalanıyor; bu siparişte kullanılmayan dosyaların kuralları atlanıyor;
  silinmiş özellik = bastırılmış; tüm varyantlarda sabit ama master'dan farklı değer = sabit kural ("Pos No");
  SolidWorks'ün yeniden numaraladığı özellik aileleri (CompCurve) için kural çıkarılmıyor; üretimde `CheckLoaded` ve
  `RestoreUnexpectedSuppression` korumaları var.

## Sonraki adımlar

1. Konveyör hattı üretimi: tablo (tekrarlanan modül) içeren kural setleri için `generate` henüz yok. DriveWorks projesi
   hat montajında 5 hazır konveyör tutup kullanılmayanları siliyor (`konveyor\ConveryoSoloTemplate.driveprojx`);
   benzer bir yaklaşım uygun olabilir.
