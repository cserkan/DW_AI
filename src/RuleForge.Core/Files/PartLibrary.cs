using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using RuleForge.Core.Json;
using RuleForge.Core.Model;

namespace RuleForge.Core.Files
{
    /// <summary>Kütüphanedeki bir dosya: hangi master'dan, hangi içerikle (parmak izi) ve ilk hangi siparişte üretildiği.</summary>
    public sealed class LibraryEntry
    {
        /// <summary>Kütüphane klasöründeki dosya adı, ör. "Frame-0003.SLDPRT".</summary>
        public string File { get; set; } = string.Empty;

        /// <summary>Master dosya adı, ör. "Frame.SLDPRT".</summary>
        public string Master { get; set; } = string.Empty;

        public string Fingerprint { get; set; } = string.Empty;
        public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;

        /// <summary>Dosyayı ilk üreten sipariş (çıktı klasörünün adı).</summary>
        public string Order { get; set; } = string.Empty;

        /// <summary>İnsan için özet: dosyadaki değişen ölçü/özellik değerleri.</summary>
        public string? Summary { get; set; }
    }

    /// <summary>
    /// Üretilen parça ve montajların ortak kütüphanesi. Her dosya benzersiz bir ad alır (master adı + sıra no, ör.
    /// "Frame-0003.SLDPRT"); aynı içerikte bir dosya daha önce üretildiyse yenisi yazılmaz, eskisi kullanılır.
    /// İçerik, dosyanın son hâlinden hesaplanan parmak iziyle tanınır (ölçüler, özellik bastırmaları, özel özellikler,
    /// alt bileşenler ve master dosyanın kendisi). Liste klasördeki "kutuphane.json" dosyasında tutulur.
    /// </summary>
    public sealed class PartLibrary
    {
        public const string IndexFileName = "kutuphane.json";

        public string Folder { get; }
        public List<LibraryEntry> Entries { get; private set; } = new List<LibraryEntry>();

        private PartLibrary(string folder)
        {
            Folder = Path.GetFullPath(folder);
        }

        public static PartLibrary Open(string folder)
        {
            var lib = new PartLibrary(folder);
            Directory.CreateDirectory(lib.Folder);
            var index = Path.Combine(lib.Folder, IndexFileName);
            if (System.IO.File.Exists(index)) lib.Entries = JsonStore.Load<List<LibraryEntry>>(index);
            return lib;
        }

        public void Save() => JsonStore.Save(Entries, Path.Combine(Folder, IndexFileName));

        /// <summary>Aynı parmak izli, diskte hâlâ duran dosya; yoksa null.</summary>
        public string? Find(string fingerprint, string extension)
        {
            foreach (var e in Entries.Where(e => e.Fingerprint == fingerprint &&
                                                 string.Equals(Path.GetExtension(e.File), extension, StringComparison.OrdinalIgnoreCase)))
            {
                var path = Path.Combine(Folder, e.File);
                if (System.IO.File.Exists(path)) return path;
            }
            return null;
        }

        /// <summary>Yeni dosya için benzersiz ad ayırır ve listeye ekler (dosyayı yazmak çağıranın işidir).</summary>
        public string Reserve(string master, string fingerprint, string order, string? summary = null)
        {
            var stem = Path.GetFileNameWithoutExtension(master);
            var ext = Path.GetExtension(master);
            var pattern = new Regex("^" + Regex.Escape(stem) + @"-(\d+)$", RegexOptions.IgnoreCase);
            int max = 0;
            foreach (var name in Entries.Select(e => e.File)
                         .Concat(Directory.EnumerateFiles(Folder, stem + "-*" + ext).Select(Path.GetFileName)))
            {
                if (!string.Equals(Path.GetExtension(name), ext, StringComparison.OrdinalIgnoreCase)) continue;
                var m = pattern.Match(Path.GetFileNameWithoutExtension(name));
                if (m.Success) max = Math.Max(max, int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture));
            }
            var file = $"{stem}-{(max + 1).ToString("D4", CultureInfo.InvariantCulture)}{ext}";
            Entries.Add(new LibraryEntry { File = file, Master = master, Fingerprint = fingerprint, Order = order, Summary = summary });
            return Path.Combine(Folder, file);
        }

        public static string Hash(string text)
        {
            using (var sha = SHA256.Create())
                return string.Concat(sha.ComputeHash(Encoding.UTF8.GetBytes(text)).Select(b => b.ToString("x2"))).Substring(0, 32);
        }

        public static string HashFile(string path)
        {
            using (var sha = SHA256.Create())
            using (var stream = System.IO.File.OpenRead(path))
                return string.Concat(sha.ComputeHash(stream).Select(b => b.ToString("x2"))).Substring(0, 32);
        }

        /// <summary>
        /// Bir belgenin içeriğini dosya adından bağımsız metne çevirir (sıralı): ölçüler, özellikler ve bastırmaları,
        /// denklemler, özel özellikler, etkin konfigürasyon. Alt bileşenler ayrıca eklenir (<see cref="ComponentLine"/>).
        /// </summary>
        public static string DocumentContent(DocumentInfo doc)
        {
            var lines = new List<string> { "kind:" + doc.Kind, "config:" + doc.ActiveConfiguration };
            lines.AddRange(doc.Dimensions.Select(d => $"d:{d.Name}={Number(d.Value)}").OrderBy(s => s, StringComparer.Ordinal));
            lines.AddRange(doc.Features.Select(f => $"f:{f.Name}:{f.Type}:{f.Suppressed}").OrderBy(s => s, StringComparer.Ordinal));
            lines.AddRange(doc.Equations.Select(e => "e:" + e.Text).OrderBy(s => s, StringComparer.Ordinal));
            lines.AddRange(doc.CustomProperties.Select(p => $"p:{p.Key}={p.Value}").OrderBy(s => s, StringComparer.Ordinal));
            lines.AddRange(doc.ConfigurationProperties.Select(p => $"cp:{p.Key}={p.Value}").OrderBy(s => s, StringComparer.Ordinal));
            return string.Join("\n", lines);
        }

        /// <summary>Montajdaki bir alt bileşen: örnek numarası, durumu ve kullandığı dosyanın parmak izi.</summary>
        public static string ComponentLine(ComponentInfo c, string childFingerprint)
        {
            var dash = c.Name.LastIndexOf('-');
            var instance = dash >= 0 ? c.Name.Substring(dash + 1) : c.Name;
            return $"c:{instance}:{c.Configuration}:{c.Suppressed}:{c.ExcludedFromBom}:{childFingerprint}";
        }

        private static string Number(double v) => Math.Round(v, 6).ToString("R", CultureInfo.InvariantCulture);
    }
}
