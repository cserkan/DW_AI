using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using RuleForge.Core.Json;

namespace RuleForge.Cli.Arayuz
{
    /// <summary>Arayüzün ürün listesi ("urunler.json"): kural dosyası, master yolları ve formdaki Türkçe etiketler.</summary>
    internal sealed class Ayarlar
    {
        public string SiparisKlasoru { get; set; } = @"C:\RuleForge\Siparisler";
        public string Kutuphane { get; set; } = @"C:\RuleForge\Kutuphane";
        public List<UrunAyari> Urunler { get; set; } = new List<UrunAyari>();

        /// <summary>Ayar dosyasının klasörü: göreli yollar buna göre çözülür.</summary>
        [System.Text.Json.Serialization.JsonIgnore]
        public string Klasor { get; set; } = string.Empty;

        public string Yol(string path) => Path.GetFullPath(Path.IsPathRooted(path) ? path : Path.Combine(Klasor, path));

        public UrunAyari? Bul(string? id) =>
            Urunler.FirstOrDefault(u => string.Equals(u.Id, id, StringComparison.OrdinalIgnoreCase));

        public static Ayarlar Yukle(string path)
        {
            var a = JsonStore.Load<Ayarlar>(path);
            a.Klasor = Path.GetDirectoryName(Path.GetFullPath(path))!;
            return a;
        }

        /// <summary>Verilen yol, yoksa çalışma klasörü ve programın klasöründen yukarı doğru "urunler.json".</summary>
        public static string? Ara(string? verilen)
        {
            if (!string.IsNullOrEmpty(verilen)) return File.Exists(verilen) ? Path.GetFullPath(verilen) : null;
            foreach (var start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
                for (var dir = new DirectoryInfo(start); dir != null; dir = dir.Parent)
                {
                    var f = Path.Combine(dir.FullName, "urunler.json");
                    if (File.Exists(f)) return f;
                }
            return null;
        }
    }

    internal sealed class UrunAyari
    {
        public string Id { get; set; } = string.Empty;
        public string Ad { get; set; } = string.Empty;
        public string? Aciklama { get; set; }
        public string Kurallar { get; set; } = string.Empty;

        /// <summary>Master montaj; tekrarlanan modüllü üründe modülün kendisi.</summary>
        public string? Master { get; set; }

        /// <summary>Tekrarlanan modüllü üründe kopyaları toplayan montaj.</summary>
        public string? Kok { get; set; }

        public Dictionary<string, GirdiAyari> Girdiler { get; set; } = new Dictionary<string, GirdiAyari>(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, TabloAyari> Tablolar { get; set; } = new Dictionary<string, TabloAyari>(StringComparer.OrdinalIgnoreCase);
    }

    internal sealed class GirdiAyari
    {
        public string? Etiket { get; set; }
        public string? Birim { get; set; }

        /// <summary>Kural dosyasındaki seçenekler yerine kullanılacak liste (ör. sadece 40/50/60 olabilen sayı).</summary>
        public List<string>? Secenekler { get; set; }

        public Dictionary<string, string>? SecenekEtiketleri { get; set; }
    }

    internal sealed class TabloAyari
    {
        public string? Etiket { get; set; }
        public string? SatirEtiketi { get; set; }
        public Dictionary<string, GirdiAyari> Sutunlar { get; set; } = new Dictionary<string, GirdiAyari>(StringComparer.OrdinalIgnoreCase);
    }
}
