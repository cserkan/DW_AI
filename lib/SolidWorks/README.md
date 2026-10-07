# SolidWorks interop DLL'leri (isteğe bağlı)

Varsayılan olarak proje, NuGet'teki **SOLIDWORKS 2015** interop'una (23.5.0) karşı derlenir.
2015 API'sine karşı derlenen kod, SOLIDWORKS 2015 ve sonrasındaki **tüm sürümlerde** (2026 dahil) çalışır;
çünkü SolidWorks COM arayüzlerine yeni metotları sona ekler, eskileri kaldırmaz.

Kendi kurulumunuzun DLL'lerini kullanmak isterseniz şu iki dosyayı bu klasöre kopyalayın:

    C:\Program Files\SOLIDWORKS Corp\SOLIDWORKS\api\redist\SolidWorks.Interop.sldworks.dll
    C:\Program Files\SOLIDWORKS Corp\SOLIDWORKS\api\redist\SolidWorks.Interop.swconst.dll

Dikkat: Hangi sürümün DLL'ini koyarsanız program o sürüm ve sonrası ile sınırlanır
ve o sürümden yeni API'ler kullanılırsa eski sürümlerde çalışmaz. Geniş uyumluluk için
en eski desteklemek istediğiniz sürümün DLL'lerini kullanın.
