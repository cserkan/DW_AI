using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using RuleForge.Core.Json;

namespace RuleForge.Cli.Arayuz
{
    /// <summary>
    /// Bir ürün projesi: kendi klasöründe yüklenen master ve varyant dosyaları, okunmuş modeller, kurallar, form ayarları,
    /// siparişler ve parça kütüphanesi. Klasör düzeni:
    /// <code>
    /// proje.json            bu dosya (ad, ana montaj, kök montaj)
    /// master\               yüklenen master dosyaları (üretim bunları kopyalar, asla değiştirmez)
    /// varyant-dosyalari\    yüklenen DriveWorks varyant klasörleri
    /// varyantlar\           okunmuş varyantlar (JSON)
    /// master.json           okunmuş master
    /// kurallar.json         çıkarılan kurallar (+ .cikarim.txt, .girdiler.csv, .notlar.json)
    /// form.json             formdaki etiketler/birimler
    /// siparisler\, kutuphane\
    /// </code>
    /// </summary>
    internal sealed class Proje
    {
        public string Id { get; set; } = string.Empty;
        public string Ad { get; set; } = string.Empty;
        public DateTime Olusturma { get; set; } = DateTime.Now;

        /// <summary>Ana (master) montaj: tam yol ya da proje klasörüne göre göreli yol.</summary>
        public string? Master { get; set; }

        /// <summary>Tekrarlanan modüllü üründe kopyaları toplayan montaj (ör. konveyör hattı).</summary>
        public string? Kok { get; set; }

        [System.Text.Json.Serialization.JsonIgnore]
        public string Klasor { get; set; } = string.Empty;

        public string Yol(params string[] parcalar) => Path.Combine(new[] { Klasor }.Concat(parcalar).ToArray());

        public string? Tam(string? yol) =>
            string.IsNullOrEmpty(yol) ? null : Path.GetFullPath(Path.IsPathRooted(yol) ? yol : Path.Combine(Klasor, yol));

        public string MasterKlasoru => Yol("master");
        public string VaryantDosyalari => Yol("varyant-dosyalari");
        public string Varyantlar => Yol("varyantlar");
        public string MasterSnapshot => Yol("master.json");
        public string Kurallar => Yol("kurallar.json");
        public string Notlar => Yol("kurallar.notlar.json");
        public string FormDosyasi => Yol("form.json");
        public string GirdiTablosu => Yol("girdi-tablosu.csv");
        public string Siparisler => Yol("siparisler");
        public string Kutuphane => Yol("kutuphane");

        public void Kaydet() => JsonStore.Save(this, Yol("proje.json"));
    }

    /// <summary>Çıkarımın kural dosyasına girmeyen sonuçları: notlar, sorular, önerilen yeni varyantlar.</summary>
    internal sealed class CikarimNotlari
    {
        public DateTime Tarih { get; set; } = DateTime.Now;
        public int VaryantSayisi { get; set; }
        public List<string> Notlar { get; set; } = new List<string>();
        public List<string> Sorular { get; set; } = new List<string>();
        public List<string> Aciklanamayan { get; set; } = new List<string>();
        public int OnerilenVaryant { get; set; }
    }

    /// <summary>Formun görünümü: girdi adı → etiket/birim/seçenek etiketleri (kural dosyasından bağımsız, kullanıcı düzenler).</summary>
    internal sealed class FormAyari
    {
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

    internal sealed class ProjeDeposu
    {
        public string Kok { get; }

        public ProjeDeposu(string kok)
        {
            Kok = Path.GetFullPath(kok);
            Directory.CreateDirectory(Kok);
        }

        public List<Proje> Hepsi() =>
            Directory.GetDirectories(Kok).Where(d => File.Exists(Path.Combine(d, "proje.json")))
                .Select(d => Yukle(Path.GetFileName(d))).Where(p => p != null).Select(p => p!)
                .OrderByDescending(p => p.Olusturma).ToList();

        public Proje? Yukle(string? id)
        {
            if (string.IsNullOrEmpty(id) || id!.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || id.Contains("..")) return null;
            var dir = Path.Combine(Kok, id);
            var file = Path.Combine(dir, "proje.json");
            if (!File.Exists(file)) return null;
            var p = JsonStore.Load<Proje>(file);
            p.Id = id;
            p.Klasor = dir;
            return p;
        }

        public Proje Olustur(string ad)
        {
            ad = ad.Trim();
            if (ad.Length == 0) throw new ArgumentException("Ürün adı boş olamaz.");
            var stem = Regex.Replace(Kucult(ad), @"[^a-z0-9]+", "-").Trim('-');
            if (stem.Length == 0) stem = "urun";
            var id = stem;
            for (int i = 2; Directory.Exists(Path.Combine(Kok, id)); i++) id = stem + "-" + i.ToString(CultureInfo.InvariantCulture);
            var p = new Proje { Id = id, Ad = ad, Klasor = Path.Combine(Kok, id) };
            Directory.CreateDirectory(p.Klasor);
            p.Kaydet();
            return p;
        }

        private static string Kucult(string s)
        {
            var map = new Dictionary<char, char>
            {
                ['ç'] = 'c', ['ğ'] = 'g', ['ı'] = 'i', ['ö'] = 'o', ['ş'] = 's', ['ü'] = 'u',
                ['Ç'] = 'c', ['Ğ'] = 'g', ['İ'] = 'i', ['Ö'] = 'o', ['Ş'] = 's', ['Ü'] = 'u', ['I'] = 'i',
            };
            var sb = new StringBuilder();
            foreach (var c in s) sb.Append(map.TryGetValue(c, out var r) ? r : char.ToLowerInvariant(c));
            return sb.ToString();
        }
    }
}
