using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using RuleForge.Core.Engine;
using RuleForge.Core.Expressions;
using RuleForge.Core.Files;
using RuleForge.Core.Json;
using RuleForge.Core.Model;
using RuleForge.Core.Rules;
using RuleForge.Inference;
#if SOLIDWORKS
using RuleForge.SolidWorks;
#endif

namespace RuleForge.Cli.Arayuz
{
    /// <summary>
    /// Tarayıcı arayüzü: bu bilgisayarda (127.0.0.1) küçük bir HTTP sunucusu ve gömülü tek sayfa. Her ürün aynı üç adımdan
    /// geçer: 1) master ve varyantları yükle, oku; 2) kuralları çıkar, onayla/reddet; 3) önerilen formla sipariş üret.
    /// </summary>
    internal sealed class ArayuzSunucu
    {
        private readonly ProjeDeposu _depo;
        private readonly ConcurrentDictionary<string, Is> _isler = new ConcurrentDictionary<string, Is>();
        private readonly object _isKilidi = new object();
        private Is? _calisan;

        private static readonly JsonSerializerOptions Json = JsonStore.Options;
        private static readonly string[] ModelUzantilari = { ".sldasm", ".sldprt", ".slddrw" };

        private ArayuzSunucu(ProjeDeposu depo)
        {
            _depo = depo;
        }

        public static int Calistir(Args args)
        {
            var depo = new ProjeDeposu(args.Get("projeler") ?? @"C:\RuleForge\Projeler");
            int port = int.TryParse(args.Get("port"), out var p) ? p : 5050;
            TcpListener? listener = null;
            for (int i = 0; i < 20 && listener == null; i++)
            {
                try
                {
                    listener = new TcpListener(IPAddress.Loopback, port + i);
                    listener.Start();
                    port += i;
                }
                catch (SocketException)
                {
                    listener = null;
                }
            }
            if (listener == null)
            {
                Console.Error.WriteLine("Boş port bulunamadı.");
                return 2;
            }
            var url = $"http://127.0.0.1:{port}/";
            Console.WriteLine($"RuleForge arayüzü: {url}  (projeler: {depo.Kok})");
            Console.WriteLine("Kapatmak için bu pencereyi kapatın ya da Ctrl+C.");
            if (!args.Flag("no-browser"))
            {
                try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
                catch (Exception ex) { Console.WriteLine("Tarayıcı açılamadı: " + ex.Message); }
            }
            new ArayuzSunucu(depo).Dinle(listener);
            return 0;
        }

        private void Dinle(TcpListener listener)
        {
            while (true)
            {
                var client = listener.AcceptTcpClient();
                ThreadPool.QueueUserWorkItem(_ =>
                {
                    try { Isle(client); }
                    catch (Exception ex) { Console.WriteLine("İstek hatası: " + ex.Message); }
                    finally { client.Close(); }
                });
            }
        }

        // ================================================================ HTTP

        private void Isle(TcpClient client)
        {
            client.ReceiveTimeout = 120000;
            var stream = client.GetStream();
            var header = new List<byte>();
            int b;
            while ((b = stream.ReadByte()) >= 0)
            {
                header.Add((byte)b);
                int n = header.Count;
                if (n >= 4 && header[n - 4] == '\r' && header[n - 3] == '\n' && header[n - 2] == '\r' && header[n - 1] == '\n') break;
                if (n > 64 * 1024) return;
            }
            var lines = Encoding.UTF8.GetString(header.ToArray()).Split(new[] { "\r\n" }, StringSplitOptions.None);
            var first = lines[0].Split(' ');
            if (first.Length < 2) return;
            var method = first[0];
            var target = first[1];
            long length = 0;
            foreach (var l in lines.Skip(1))
                if (l.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
                    long.TryParse(l.Substring(15).Trim(), out length);
            if (length > 500L * 1024 * 1024) return;
            var body = new byte[length];
            for (long read = 0; read < length;)
            {
                int r = stream.Read(body, (int)read, (int)(length - read));
                if (r <= 0) break;
                read += r;
            }
            var q = target.IndexOf('?');
            var path = Uri.UnescapeDataString(q >= 0 ? target.Substring(0, q) : target);
            var query = ParseQuery(q >= 0 ? target.Substring(q + 1) : string.Empty);

            int status = 200;
            string type = "application/json; charset=utf-8";
            byte[] content;
            try
            {
                if (method == "GET" && (path == "/" || path == "/index.html"))
                {
                    type = "text/html; charset=utf-8";
                    content = Sayfa();
                }
                else
                {
                    var result = Api(method, path, query, body);
                    content = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(result, Json));
                }
            }
            catch (KullaniciHatasi ex)
            {
                status = 400;
                content = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { hata = ex.Message }, Json));
            }
            catch (Exception ex)
            {
                status = 500;
                content = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { hata = ex.Message }, Json));
            }
            var head = $"HTTP/1.1 {status} {(status == 200 ? "OK" : "Error")}\r\nContent-Type: {type}\r\nContent-Length: {content.Length}\r\n" +
                       "Cache-Control: no-store\r\nConnection: close\r\n\r\n";
            var h = Encoding.ASCII.GetBytes(head);
            stream.Write(h, 0, h.Length);
            stream.Write(content, 0, content.Length);
        }

        private static Dictionary<string, string> ParseQuery(string q) =>
            q.Split('&').Where(p => p.Length > 0).Select(p => p.Split(new[] { '=' }, 2))
                .GroupBy(p => Uri.UnescapeDataString(p[0]), StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.Last().Length > 1 ? Uri.UnescapeDataString(g.Last()[1].Replace('+', ' ')) : string.Empty,
                    StringComparer.OrdinalIgnoreCase);

        private static byte[] Sayfa()
        {
            var asm = typeof(ArayuzSunucu).Assembly;
            var name = asm.GetManifestResourceNames().First(n => n.EndsWith("index.html", StringComparison.OrdinalIgnoreCase));
            using (var s = asm.GetManifestResourceStream(name)!)
            using (var ms = new MemoryStream())
            {
                s.CopyTo(ms);
                return ms.ToArray();
            }
        }

        private sealed class KullaniciHatasi : Exception
        {
            public KullaniciHatasi(string message) : base(message) { }
        }

        private static T Oku<T>(byte[] body) where T : new() =>
            body.Length == 0 ? new T() : JsonSerializer.Deserialize<T>(Encoding.UTF8.GetString(body), Json) ?? new T();

        // ================================================================ API

        private object Api(string method, string path, Dictionary<string, string> query, byte[] body)
        {
            string? Q(string k) => query.TryGetValue(k, out var v) ? v : null;
            switch (method + " " + path)
            {
                case "GET /api/projeler": return Projeler();
                case "POST /api/proje": return ProjeOlustur(Oku<AdIstegi>(body).Ad);
                case "GET /api/proje": return ProjeDurumu(ProjeBul(Q("id")));
                case "POST /api/yukle": return Yukle(ProjeBul(Q("id")), Q("hedef"), Q("yol"), body);
                case "POST /api/temizle": return Temizle(ProjeBul(Q("id")), Q("hedef"));
                case "POST /api/montaj-sec": return MontajSec(ProjeBul(Q("id")), Oku<MontajIstegi>(body));
                case "POST /api/oku": return IsBaslat(ProjeBul(Q("id")), "oku", true, VaryantlariOku);
                case "POST /api/cikar": return IsBaslat(ProjeBul(Q("id")), "cikar", false, KurallariCikar);
                case "GET /api/kurallar": return Kurallar(ProjeBul(Q("id")));
                case "POST /api/kural-durum": return KuralDurum(ProjeBul(Q("id")), Oku<DurumIstegi>(body));
                case "GET /api/sorular": return Sorular(ProjeBul(Q("id")));
                case "POST /api/cevap": return Cevapla(ProjeBul(Q("id")), Oku<CevapIstegi>(body));
                case "GET /api/form": return Form(ProjeBul(Q("id")));
                case "POST /api/form-etiket": return FormEtiket(ProjeBul(Q("id")), Oku<EtiketIstegi>(body));
                case "POST /api/hesapla": return Hesapla(ProjeBul(Q("id")), Oku<HesapIstegi>(body));
                case "POST /api/uret": return Uret(ProjeBul(Q("id")), Oku<HesapIstegi>(body));
                case "GET /api/is": return IsDurumu(Q("is"));
                case "GET /api/siparisler": return Siparisler(ProjeBul(Q("id")));
                case "POST /api/ac": return Ac(ProjeBul(Q("id")), Oku<AcIstegi>(body));
                default: throw new KullaniciHatasi("Bilinmeyen istek: " + method + " " + path);
            }
        }

        private sealed class AdIstegi { public string Ad { get; set; } = string.Empty; }
        private sealed class MontajIstegi { public string? Master { get; set; } public string? Kok { get; set; } }
        private sealed class DurumIstegi { public List<string> Kurallar { get; set; } = new List<string>(); public string Durum { get; set; } = "proposed"; }
        private sealed class EtiketIstegi { public string? Tablo { get; set; } public string Ad { get; set; } = string.Empty; public string? Etiket { get; set; } public string? Birim { get; set; } }
        private sealed class AcIstegi { public string Yol { get; set; } = string.Empty; public bool SolidWorks { get; set; } }

        private sealed class CevapIstegi
        {
            public string Soru { get; set; } = string.Empty;
            public string? Secim { get; set; }
            public double? Sayi { get; set; }
            public string? Formul { get; set; }
            public string? Not { get; set; }
        }

        private sealed class HesapIstegi
        {
            public Dictionary<string, string> Girdiler { get; set; } = new Dictionary<string, string>();
            public Dictionary<string, List<Dictionary<string, string>>> Tablolar { get; set; } = new Dictionary<string, List<Dictionary<string, string>>>();
            public bool Onerilen { get; set; } = true;
            public string? Siparis { get; set; }
            public bool Pdf { get; set; }
            public bool Step { get; set; }
        }

        private Proje ProjeBul(string? id) => _depo.Yukle(id) ?? throw new KullaniciHatasi("Ürün bulunamadı: " + id);

        // ---------------------------------------------------------------- Projeler

        private object Projeler() => _depo.Hepsi().Select(p => new { p.Id, p.Ad }).ToList();

        private object ProjeOlustur(string ad)
        {
            if (string.IsNullOrWhiteSpace(ad)) throw new KullaniciHatasi("Ürün için bir ad yazın.");
            var p = _depo.Olustur(ad);
            return new { p.Id, p.Ad };
        }

        private static List<string> Dosyalar(string dir, params string[] uzantilar) =>
            !Directory.Exists(dir) ? new List<string>()
                : Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories)
                    .Where(f => uzantilar.Length == 0 || uzantilar.Contains(Path.GetExtension(f).ToLowerInvariant()))
                    .ToList();

        private static string Goreli(string kok, string yol) =>
            yol.StartsWith(kok.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase) ? yol.Substring(kok.TrimEnd('\\').Length + 1) : yol;

        private object ProjeDurumu(Proje p)
        {
            var masterTam = p.Tam(p.Master);
            var montajlar = Dosyalar(p.MasterKlasoru, ".sldasm").Select(f => Goreli(p.Klasor, f)).OrderBy(f => f.Count(c => c == '\\')).ThenBy(f => f).ToList();
            var varyantDosyalari = Dosyalar(p.VaryantDosyalari, ModelUzantilari);
            var okunan = Directory.Exists(p.Varyantlar) ? Directory.GetFiles(p.Varyantlar, "*.json").Select(Path.GetFileNameWithoutExtension).OrderBy(n => n).ToList() : new List<string>();
            RuleSet? rules = File.Exists(p.Kurallar) ? JsonStore.Load<RuleSet>(p.Kurallar) : null;
            return new
            {
                p.Id,
                p.Ad,
                Master = p.Master,
                MasterVar = masterTam != null && File.Exists(masterTam),
                MasterYuklendi = montajlar.Count > 0,
                MasterDosyaSayisi = Dosyalar(p.MasterKlasoru, ModelUzantilari).Count,
                Montajlar = montajlar,
                p.Kok,
                KokGerekli = rules != null && rules.Tables.Any(t => string.IsNullOrEmpty(t.ModuleComponent)),
                MasterOkundu = File.Exists(p.MasterSnapshot),
                VaryantDosyaSayisi = varyantDosyalari.Count,
                VaryantKlasorSayisi = varyantDosyalari.Select(f => Path.GetDirectoryName(f)).Distinct(StringComparer.OrdinalIgnoreCase).Count(),
                OkunanVaryantlar = okunan,
                GirdiTablosu = File.Exists(p.GirdiTablosu),
                KuralSayisi = rules?.Rules.Count ?? 0,
                OnayliKural = rules?.Rules.Count(r => r.Status == RuleStatus.Approved) ?? 0,
                ReddedilenKural = rules?.Rules.Count(r => r.Status == RuleStatus.Rejected) ?? 0,
                AcikSoru = AcikSoruSayisi(p, rules),
                SiparisSayisi = Directory.Exists(p.Siparisler) ? Directory.GetDirectories(p.Siparisler).Count(d => !d.EndsWith(".calisma")) : 0,
                CalisanIs = _calisan != null && _calisan.Durum == "calisiyor" ? new { _calisan.Id, _calisan.Tur, _calisan.Proje } : null,
            };
        }

        // ---------------------------------------------------------------- 1) Yükleme ve okuma

        /// <summary>Tarayıcıdan tek dosya yükler; göreli yol (klasör yapısı) korunur. Sadece SolidWorks dosyaları ve girdi tablosu.</summary>
        private object Yukle(Proje p, string? hedef, string? yol, byte[] body)
        {
            if (hedef == "girdi")
            {
                File.WriteAllBytes(p.GirdiTablosu, body);
                return new { Tamam = true };
            }
            string kok = hedef == "master" ? p.MasterKlasoru : hedef == "varyant" ? p.VaryantDosyalari : throw new KullaniciHatasi("Geçersiz hedef.");
            var parcalar = (yol ?? string.Empty).Replace('/', '\\').Split('\\').Where(s => s.Length > 0).ToList();
            if (parcalar.Count == 0 || parcalar.Any(s => s == ".." || s == "." || s.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0))
                throw new KullaniciHatasi("Geçersiz dosya yolu: " + yol);
            if (!ModelUzantilari.Contains(Path.GetExtension(parcalar.Last()).ToLowerInvariant()) || parcalar.Last().StartsWith("~$"))
                return new { Atlandi = true };
            var dest = Path.Combine(new[] { kok }.Concat(parcalar).ToArray());
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            File.WriteAllBytes(dest, body);
            return new { Tamam = true };
        }

        private object Temizle(Proje p, string? hedef)
        {
            CalisanYok();
            switch (hedef)
            {
                case "master":
                    if (Directory.Exists(p.MasterKlasoru)) Directory.Delete(p.MasterKlasoru, true);
                    if (File.Exists(p.MasterSnapshot)) File.Delete(p.MasterSnapshot);
                    p.Master = null;
                    p.Kok = null;
                    p.Kaydet();
                    break;
                case "varyant":
                    if (Directory.Exists(p.VaryantDosyalari)) Directory.Delete(p.VaryantDosyalari, true);
                    if (Directory.Exists(p.Varyantlar)) Directory.Delete(p.Varyantlar, true);
                    break;
                case "girdi":
                    if (File.Exists(p.GirdiTablosu)) File.Delete(p.GirdiTablosu);
                    break;
                default: throw new KullaniciHatasi("Geçersiz hedef.");
            }
            return new { Tamam = true };
        }

        private object MontajSec(Proje p, MontajIstegi istek)
        {
            string? Kontrol(string? yol)
            {
                if (string.IsNullOrEmpty(yol)) return null;
                var tam = p.Tam(yol);
                if (tam == null || !File.Exists(tam)) throw new KullaniciHatasi("Montaj bulunamadı: " + yol);
                return yol;
            }
            if (istek.Master != null)
            {
                var yeni = Kontrol(istek.Master);
                if (!string.Equals(yeni, p.Master, StringComparison.OrdinalIgnoreCase) && File.Exists(p.MasterSnapshot)) File.Delete(p.MasterSnapshot);
                p.Master = yeni;
            }
            if (istek.Kok != null) p.Kok = Kontrol(istek.Kok);
            p.Kaydet();
            return new { Tamam = true };
        }

        private void VaryantlariOku(Is job, Proje p)
        {
#if SOLIDWORKS
            var master = p.Tam(p.Master);
            if (master == null || !File.Exists(master)) throw new KullaniciHatasi("Önce ana montajı seçin.");
            job.Yaz("SolidWorks'e bağlanılıyor…");
            using (var session = SwSession.Connect(visible: false))
            {
                var extractor = new SnapshotExtractor(session);
                job.Yaz("Master okunuyor: " + Path.GetFileName(master));
                JsonStore.Save(extractor.Extract(master), p.MasterSnapshot);

                // Sürüklenen klasörün kendisi (ör. "Results") tek alt klasör olarak gelir: varyantlar onun içindeki klasörlerdir.
                var root = p.VaryantDosyalari;
                while (Directory.Exists(root) && Directory.GetFiles(root, "*.sldasm").Length == 0 && Directory.GetDirectories(root).Length == 1)
                    root = Directory.GetDirectories(root)[0];
                var assemblies = Directory.Exists(root) ? TopAssemblyDetector.FindAssemblies(root) : new List<string>();
                if (assemblies.Count == 0)
                {
                    job.Yaz("Yüklenmiş varyant yok; sadece master okundu.");
                    return;
                }
                job.Yaz($"{assemblies.Count} montaj dosyası bulundu; varyantların üst montajları aranıyor…");
                var finder = new VariantFinder(session);
                var variants = TopAssemblyDetector.Detect(root, assemblies, finder.DirectDependencies, finder.TotalDependencyCount);
                job.Yaz($"{variants.Count} varyant bulundu.");
                if (Directory.Exists(p.Varyantlar)) Directory.Delete(p.Varyantlar, true);
                Directory.CreateDirectory(p.Varyantlar);
                int hata = 0;
                for (int i = 0; i < variants.Count; i++)
                {
                    var v = variants[i];
                    job.Yaz($"[{i + 1}/{variants.Count}] {v.Label}");
                    try
                    {
                        var snap = extractor.Extract(v.Path, v.Label);
                        var name = v.Label;
                        foreach (var c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
                        JsonStore.Save(snap, Path.Combine(p.Varyantlar, name + ".json"));
                    }
                    catch (Exception ex)
                    {
                        hata++;
                        job.Uyarilar.Add($"{v.Label} okunamadı: {ex.Message}");
                    }
                }
                job.Yaz(hata == 0 ? $"Tamamlandı: {variants.Count} varyant okundu." : $"{variants.Count - hata} varyant okundu, {hata} okunamadı.");
            }
#else
            throw new KullaniciHatasi("Varyantları okumak SolidWorks gerektirir (Windows, ruleforge.exe).");
#endif
        }

        // ---------------------------------------------------------------- 2) Kurallar

        private void KurallariCikar(Is job, Proje p)
        {
            var files = Directory.Exists(p.Varyantlar) ? Directory.GetFiles(p.Varyantlar, "*.json").OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToList() : new List<string>();
            if (files.Count < 2) throw new KullaniciHatasi("Kural çıkarmak için en az 2 okunmuş varyant gerekir (1. adım).");
            var table = File.Exists(p.GirdiTablosu) ? InputTable.Load(p.GirdiTablosu) : null;
            var samples = new List<VariantSample>();
            foreach (var f in files)
            {
                var snap = JsonStore.Load<ModelSnapshot>(f);
                var name = string.IsNullOrEmpty(snap.Name) ? Path.GetFileNameWithoutExtension(f) : snap.Name;
                Dictionary<string, Value>? inputs = null;
                if (table != null && (inputs = InferCommand.Match(table, name)) == null)
                {
                    job.Uyarilar.Add($"'{name}' girdi tablosunda yok, atlandı.");
                    continue;
                }
                samples.Add(new VariantSample(name, snap, inputs));
            }
            var master = File.Exists(p.MasterSnapshot) ? JsonStore.Load<ModelSnapshot>(p.MasterSnapshot) : null;
            if (master == null) job.Uyarilar.Add("Master okunmamış: adlar ilk varyanttan alınacak.");
            job.Yaz($"{samples.Count} varyanttan kurallar çıkarılıyor…");
            var report = RuleInferencer.Infer(samples, new InferenceOptions(), master);

            var rules = report.ToRuleSet(p.Ad, p.Tam(p.Master) ?? master?.SourcePath ?? samples[0].Snapshot.SourcePath);
            // Önceki onay/ret kararları korunur (aynı hedefli kural yeniden çıkarıldıysa).
            if (File.Exists(p.Kurallar))
            {
                var eski = JsonStore.Load<RuleSet>(p.Kurallar).Rules.Where(r => r.Status != RuleStatus.Proposed)
                    .GroupBy(r => r.Target.Key + "|" + r.Scope).ToDictionary(g => g.Key, g => g.First());
                foreach (var r in rules.Rules)
                    if (eski.TryGetValue(r.Target.Key + "|" + r.Scope, out var e))
                        r.Status = e.Status == RuleStatus.Approved && e.Expression != r.Expression ? RuleStatus.Proposed : e.Status;
            }
            // Daha önce verilen cevaplar yeni kurallara yeniden uygulanır (soru kimlikleri kararlı).
            var cevaplar = Cevaplar(p);
            int yeniden = 0;
            foreach (var q in report.OpenQuestions)
            {
                if (!cevaplar.TryGetValue(q.Id, out var kayit)) continue;
                var sonuc = QuestionApplier.Apply(rules, q, kayit.Cevap);
                kayit.Uygulandi = sonuc.Ok;
                kayit.Sonuc = sonuc.Ok ? sonuc.Message : "Yeni kurallara uygulanamadı: " + sonuc.Message;
                if (sonuc.Ok) yeniden++;
            }
            if (yeniden > 0) job.Yaz($"Önceki {yeniden} cevabınız yeni kurallara uygulandı.");
            JsonStore.Save(cevaplar, p.Cevaplar);
            JsonStore.Save(rules, p.Kurallar);
            File.WriteAllText(Path.ChangeExtension(p.Kurallar, ".cikarim.txt"), report.ToText());
            if (report.InputValues.Count > 0)
                File.WriteAllText(Path.ChangeExtension(p.Kurallar, ".girdiler.csv"), report.ToInputCsv(), new UTF8Encoding(true));
            JsonStore.Save(new CikarimNotlari
            {
                VaryantSayisi = samples.Count,
                Notlar = report.Notes,
                Sorular = report.Questions,
                Aciklanamayan = report.Unexplained.Select(u => u.Label).ToList(),
                OnerilenVaryant = report.SuggestedVariants.Count,
                AcikSorular = report.OpenQuestions,
            }, p.Notlar);
            job.Yaz($"{rules.Rules.Count} kural, {rules.Inputs.Count} girdi" + (rules.Tables.Count > 0 ? $", {rules.Tables.Count} tablo" : "") + " bulundu.");
        }

        private object Kurallar(Proje p)
        {
            if (!File.Exists(p.Kurallar)) return new { Var = false };
            var rules = JsonStore.Load<RuleSet>(p.Kurallar);
            var notlar = File.Exists(p.Notlar) ? JsonStore.Load<CikarimNotlari>(p.Notlar) : new CikarimNotlari();
            var form = FormAyari(p);
            return new
            {
                Var = true,
                notlar.Tarih,
                notlar.VaryantSayisi,
                notlar.Notlar,
                notlar.Sorular,
                notlar.Aciklanamayan,
                notlar.OnerilenVaryant,
                Girdiler = rules.Inputs.Select(i => new { i.Name, Etiket = Ayar(form.Girdiler, i.Name)?.Etiket ?? i.Label, i.Description }).ToList(),
                Tablolar = rules.Tables.Select(t => new { t.Name, Etiket = Ayar(form.Tablolar, t.Name)?.Etiket ?? t.Label, t.Description, Sutunlar = t.Columns.Select(c => c.Name).ToList() }).ToList(),
                Degiskenler = rules.Variables.Select(v => new { v.Name, Formul = v.Expression, v.Description }).ToList(),
                Kurallar = rules.Rules.Select(r => new
                {
                    r.Id,
                    Parca = Parca(r.Target),
                    Hedef = HedefAdi(r),
                    Tur = TurAdi(r.Target.Kind),
                    Formul = r.Expression,
                    Guven = Math.Round((r.Confidence ?? 0) * 100),
                    Durum = r.Status.ToString().ToLowerInvariant(),
                    Kanit = r.Evidence,
                    Satir = r.Scope != null,
                }).ToList(),
            };
        }

        /// <summary>Kuralın ait olduğu dosya (gruplama için): belge ya da bileşen yolunun ilk parçası.</summary>
        private static string Parca(RuleTarget t)
        {
            if (t.Document != null) return t.Document;
            if (t.Component == null) return "(ana montaj)";
            // Bileşen kuralları içinde bulundukları montajın altında: "Cupboard Left End Assy-1/Shelf Peg1-3" → "Cupboard Left End Assy".
            var parts = t.Component.Split('/');
            return parts.Length < 2 ? "(ana montaj)" : Regex.Replace(parts[parts.Length - 2], @"-\d+$", string.Empty);
        }

        private static string HedefAdi(Rule r)
        {
            if (r.Target.Component != null)
            {
                var name = r.Target.Component.Split('/').Last();
                switch (r.Target.Kind)
                {
                    case TargetKind.ComponentSuppression: return name + " bastırılmış mı";
                    case TargetKind.ComponentReplace: return name + " hangi dosya";
                    case TargetKind.Configuration: return name + " konfigürasyonu";
                    default: return name;
                }
            }
            var d = r.Description ?? r.Target.ToString();
            var i = d.IndexOf('›');
            return i >= 0 ? d.Substring(i + 1).Trim() : d;
        }

        private object KuralDurum(Proje p, DurumIstegi istek)
        {
            if (!File.Exists(p.Kurallar)) throw new KullaniciHatasi("Önce kuralları çıkarın.");
            if (!Enum.TryParse<RuleStatus>(istek.Durum, true, out var durum)) throw new KullaniciHatasi("Geçersiz durum.");
            var rules = JsonStore.Load<RuleSet>(p.Kurallar);
            var ids = new HashSet<string>(istek.Kurallar, StringComparer.OrdinalIgnoreCase);
            int n = 0;
            foreach (var r in rules.Rules.Where(r => ids.Contains(r.Id)))
            {
                r.Status = durum;
                n++;
            }
            JsonStore.Save(rules, p.Kurallar);
            return new { Degisen = n };
        }

        // ---------------------------------------------------------------- Sorular

        private static int AcikSoruSayisi(Proje p, RuleSet? rules)
        {
            if (rules == null || !File.Exists(p.Notlar)) return 0;
            var cevaplar = Cevaplar(p);
            return JsonStore.Load<CikarimNotlari>(p.Notlar).AcikSorular.Count(q =>
                !(cevaplar.TryGetValue(q.Id, out var c) && c.Uygulandi) &&
                !(q.Kind == QuestionKind.Threshold && q.Variable != null &&
                  !rules.Rules.Any(r => Regex.IsMatch(r.Expression ?? string.Empty, @"\b" + Regex.Escape(q.Variable) + @"\b"))));
        }

        private static Dictionary<string, KayitliCevap> Cevaplar(Proje p) =>
            File.Exists(p.Cevaplar) ? JsonStore.Load<Dictionary<string, KayitliCevap>>(p.Cevaplar) : new Dictionary<string, KayitliCevap>();

        /// <summary>Girdi adı → formdaki etiket (soru metinlerinde okunur ad göstermek için).</summary>
        private Dictionary<string, string> Etiketler(Proje p, RuleSet rules)
        {
            var form = FormAyari(p);
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var i in rules.Inputs) map[i.Name] = Ayar(form.Girdiler, i.Name)?.Etiket ?? (i.Label == i.Name ? Okunur(i.Name) : i.Label);
            foreach (var t in rules.Tables)
            {
                map[t.Name + "_Adet"] = (Ayar(form.Tablolar, t.Name)?.Etiket ?? Okunur(t.Name)) + " sayısı";
                foreach (var c in t.Columns) map[c.Name] = Ayar(Ayar(form.Tablolar, t.Name)?.Sutunlar, c.Name)?.Etiket ?? Okunur(c.Name);
            }
            return map;
        }

        private static string Okunurlastir(string text, Dictionary<string, string> etiketler) =>
            etiketler.OrderByDescending(kv => kv.Key.Length).Aggregate(text, (s, kv) =>
                Regex.Replace(s, @"(?<![A-Za-z0-9_])" + Regex.Escape(kv.Key) + @"(?![A-Za-z0-9_])", "«" + kv.Value + "»"));

        private object Sorular(Proje p)
        {
            if (!File.Exists(p.Kurallar) || !File.Exists(p.Notlar)) return new { Var = false };
            var rules = JsonStore.Load<RuleSet>(p.Kurallar);
            var sorular = JsonStore.Load<CikarimNotlari>(p.Notlar).AcikSorular;
            var cevaplar = Cevaplar(p);
            var etiketler = Etiketler(p, rules);
            return new
            {
                Var = true,
                Girdiler = etiketler.Select(kv => new { Ad = kv.Key, Etiket = kv.Value }).ToList(),
                // Sıra: önce başka soruları etkileyenler (hangi girdi, ayrı seçim), sonra eşikler ve formüller, en sonda açıklanamayanlar.
                Sorular = sorular.OrderBy(q => Array.IndexOf(new[] { QuestionKind.ChooseInput, QuestionKind.SeparateInput, QuestionKind.Threshold,
                    QuestionKind.ChooseFormula, QuestionKind.Confirm, QuestionKind.Unexplained }, q.Kind)).Select(q =>
                {
                    cevaplar.TryGetValue(q.Id, out var c);
                    // Eşik sorusu: kurallar artık o değişkeni kullanmıyorsa (ör. başka girdi seçildi) geçersizdir.
                    bool gecersiz = q.Kind == QuestionKind.Threshold && q.Variable != null &&
                                    !rules.Rules.Any(r => Regex.IsMatch(r.Expression ?? string.Empty, @"\b" + Regex.Escape(q.Variable) + @"\b"));
                    return new
                    {
                        q.Id,
                        Tur = q.Kind,
                        Metin = Okunurlastir(q.Text, etiketler),
                        Aciklama = q.Detail == null ? null : Okunurlastir(q.Detail, etiketler).Replace("«", "").Replace("»", ""),
                        KuralSayisi = q.RuleIds.Count + q.Targets.Count,
                        Kurallar = q.RuleIds.Select(id => rules.FindRule(id)).Where(r => r != null).Take(30)
                            .Select(r => new { r!.Id, Hedef = r.Description, Formul = r.Expression }).ToList(),
                        Secenekler = q.Options.Select(o => new { Etiket = q.Kind == QuestionKind.ChooseInput ? Okunurlastir(o.Label, etiketler).Trim('«', '»') : o.Label, Deger = o.Value }).ToList(),
                        FormulYazilabilir = q.AllowFormula,
                        q.Min,
                        q.Max,
                        Simdiki = q.Current,
                        Dene = q.TryValues,
                        GirdiEtiketi = q.Input != null && etiketler.TryGetValue(q.Input, out var ge) ? ge : q.Input,
                        VaryantSayisi = q.Checks.Count,
                        Durum = gecersiz ? "gecersiz" : c != null && c.Uygulandi ? "cevaplandi" : "acik",
                        Cevap = c?.Cevap,
                        Sonuc = c?.Sonuc,
                    };
                }).ToList(),
            };
        }

        private object Cevapla(Proje p, CevapIstegi istek)
        {
            if (!File.Exists(p.Kurallar) || !File.Exists(p.Notlar)) throw new KullaniciHatasi("Önce kuralları çıkarın.");
            var q = JsonStore.Load<CikarimNotlari>(p.Notlar).AcikSorular.FirstOrDefault(x => x.Id == istek.Soru)
                    ?? throw new KullaniciHatasi("Soru bulunamadı; kuralları yeniden çıkarın.");
            var rules = JsonStore.Load<RuleSet>(p.Kurallar);
            var cevap = new QuestionAnswer
            {
                Choice = istek.Secim,
                Number = istek.Sayi,
                Formula = string.IsNullOrWhiteSpace(istek.Formul) ? null : istek.Formul!.Trim(),
                Note = string.IsNullOrWhiteSpace(istek.Not) ? null : istek.Not!.Trim(),
                By = Environment.UserName,
            };
            var sonuc = QuestionApplier.Apply(rules, q, cevap);
            if (!sonuc.Ok) return new { Tamam = false, Mesaj = sonuc.Message };
            var errors = RuleSetValidator.Validate(rules).Where(i => i.Severity == IssueSeverity.Error).ToList();
            if (errors.Count > 0) return new { Tamam = false, Mesaj = "Kurallar bu cevapla geçersiz oluyor: " + string.Join("; ", errors.Take(3).Select(e => e.ToString())) };
            JsonStore.Save(rules, p.Kurallar);
            var cevaplar = Cevaplar(p);
            cevaplar[q.Id] = new KayitliCevap { Cevap = cevap, Sonuc = sonuc.Message, Uygulandi = true };
            JsonStore.Save(cevaplar, p.Cevaplar);
            return new { Tamam = true, Mesaj = sonuc.Message };
        }

        // ---------------------------------------------------------------- 3) Form ve üretim

        private static FormAyari FormAyari(Proje p) => File.Exists(p.FormDosyasi) ? JsonStore.Load<FormAyari>(p.FormDosyasi) : new FormAyari();

        private static T? Ayar<T>(Dictionary<string, T>? map, string key) where T : class =>
            map == null ? null : map.TryGetValue(key, out var v) ? v : map.FirstOrDefault(kv => string.Equals(kv.Key, key, StringComparison.OrdinalIgnoreCase)).Value;

        /// <summary>Girdi adını okunur bir etikete çevirir (etiket verilmemişse): "Assembly_Height" → "Assembly Height".</summary>
        private static string Okunur(string name) => Regex.Replace(name.Replace('_', ' '), @"\s+", " ").Trim();

        private static object Girdi(InputDefinition d, GirdiAyari? a)
        {
            var secenekler = a?.Secenekler ?? d.Options;
            return new
            {
                d.Name,
                Etiket = a?.Etiket ?? (d.Label == d.Name ? Okunur(d.Name) : d.Label),
                Tip = secenekler.Count > 0 ? "secim" : d.Type == InputType.Number ? "sayi" : d.Type == InputType.Bool ? "evet-hayir" : "metin",
                Birim = a?.Birim ?? d.Unit,
                d.Min,
                d.Max,
                Varsayilan = d.Default,
                Secenekler = secenekler.Select(s => new { Deger = s, Etiket = Ayar(a?.SecenekEtiketleri, s) ?? Path.GetFileNameWithoutExtension(s) }).ToList(),
            };
        }

        /// <summary>Üretimin neden yapılamayacağı (eksik master, kök montaj); sorun yoksa null.</summary>
        private static string? Sorun(Proje p, RuleSet rules)
        {
            var master = p.Tam(p.Master);
            if (master == null || !File.Exists(master)) return "Ana montaj seçilmemiş (1. adım).";
            if (rules.Tables.Any(t => string.IsNullOrEmpty(t.ModuleComponent)) && (p.Tam(p.Kok) is not string kok || !File.Exists(kok)))
                return "Bu ürün tekrarlanan modül içeriyor: kopyaları toplayan montajı seçin.";
            return null;
        }

        private object Form(Proje p)
        {
            if (!File.Exists(p.Kurallar)) return new { Var = false };
            var rules = JsonStore.Load<RuleSet>(p.Kurallar);
            var form = FormAyari(p);
            return new
            {
                Var = true,
                Sorun = Sorun(p, rules),
                KokGerekli = rules.Tables.Any(t => string.IsNullOrEmpty(t.ModuleComponent)),
                Montajlar = Dosyalar(p.MasterKlasoru, ".sldasm").Select(f => Goreli(p.Klasor, f)).OrderBy(f => f).ToList(),
                p.Kok,
                KuralSayisi = rules.Rules.Count,
                OnayliKural = rules.Rules.Count(r => r.Status == RuleStatus.Approved),
                Girdiler = rules.Inputs.Select(d => Girdi(d, Ayar(form.Girdiler, d.Name))).ToList(),
                Tablolar = rules.Tables.Select(t =>
                {
                    var ta = Ayar(form.Tablolar, t.Name);
                    return new
                    {
                        t.Name,
                        Etiket = ta?.Etiket ?? Okunur(t.Name),
                        SatirEtiketi = ta?.SatirEtiketi ?? "Satır",
                        MinSatir = t.MinRows ?? 1,
                        MaxSatir = t.MaxRows ?? 20,
                        Sutunlar = t.Columns.Select(c => Girdi(c, Ayar(ta?.Sutunlar, c.Name))).ToList(),
                    };
                }).ToList(),
                SiparisOnerisi = SiparisOnerisi(p),
            };
        }

        private object FormEtiket(Proje p, EtiketIstegi istek)
        {
            var form = FormAyari(p);
            GirdiAyari a;
            if (!string.IsNullOrEmpty(istek.Tablo))
            {
                var ta = Ayar(form.Tablolar, istek.Tablo!) ?? (form.Tablolar[istek.Tablo!] = new TabloAyari());
                if (string.IsNullOrEmpty(istek.Ad))
                {
                    if (istek.Etiket != null) ta.Etiket = istek.Etiket.Trim().Length > 0 ? istek.Etiket.Trim() : null;
                    JsonStore.Save(form, p.FormDosyasi);
                    return new { Tamam = true };
                }
                a = Ayar(ta.Sutunlar, istek.Ad) ?? (ta.Sutunlar[istek.Ad] = new GirdiAyari());
            }
            else
            {
                a = Ayar(form.Girdiler, istek.Ad) ?? (form.Girdiler[istek.Ad] = new GirdiAyari());
            }
            if (istek.Etiket != null) a.Etiket = istek.Etiket.Trim().Length > 0 ? istek.Etiket.Trim() : null;
            if (istek.Birim != null) a.Birim = istek.Birim.Trim().Length > 0 ? istek.Birim.Trim() : null;
            JsonStore.Save(form, p.FormDosyasi);
            return new { Tamam = true };
        }

        private static string SiparisOnerisi(Proje p)
        {
            int max = 0;
            if (Directory.Exists(p.Siparisler))
                foreach (var d in Directory.GetDirectories(p.Siparisler))
                {
                    var m = Regex.Match(Path.GetFileName(d), @"^S(\d+)$");
                    if (m.Success) max = Math.Max(max, int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture));
                }
            return "S" + (max + 1).ToString("D4", CultureInfo.InvariantCulture);
        }

        private static (RuleSet rules, EvaluationResult sonuc) Degerlendir(Proje p, HesapIstegi istek)
        {
            if (!File.Exists(p.Kurallar)) throw new KullaniciHatasi("Önce kuralları çıkarın (2. adım).");
            var rules = JsonStore.Load<RuleSet>(p.Kurallar);
            // Komut satırıyla aynı ayrıştırma: "Ad=Değer" ve "Tablo.Sütun=v1;v2".
            var pairs = istek.Girdiler.Where(kv => !string.IsNullOrWhiteSpace(kv.Value)).Select(kv => kv.Key + "=" + kv.Value.Trim()).ToList();
            foreach (var t in istek.Tablolar)
                foreach (var c in t.Value.SelectMany(r => r.Keys).Distinct())
                    pairs.Add($"{t.Key}.{c}=" + string.Join(";", t.Value.Select(r => r.TryGetValue(c, out var v) ? v.Trim() : string.Empty)));
            var inputs = RuleEngine.ParseAssignments(pairs, out var tables);
            return (rules, RuleEngine.Evaluate(rules, inputs, new EvaluationOptions { IncludeProposed = istek.Onerilen }, tables));
        }

        private object Hesapla(Proje p, HesapIstegi istek)
        {
            var (_, r) = Degerlendir(p, istek);
            return new
            {
                Basarili = r.Success,
                Hatalar = r.Errors,
                Uyarilar = r.Warnings,
                Eylemler = r.Actions.Select(a => new
                {
                    Satir = a.Instance,
                    Tur = TurAdi(a.Target.Kind),
                    Hedef = string.IsNullOrEmpty(a.Rule.Description) ? a.Target.ToString() : a.Rule.Description,
                    Deger = a.Value.AsText(),
                }).ToList(),
            };
        }

        private static string TurAdi(TargetKind kind)
        {
            switch (kind)
            {
                case TargetKind.Dimension: return "Ölçü";
                case TargetKind.GlobalVariable: return "Değişken";
                case TargetKind.FeatureSuppression: return "Özellik bastırma";
                case TargetKind.ComponentSuppression: return "Bileşen bastırma";
                case TargetKind.ComponentReplace: return "Parça değişimi";
                case TargetKind.Configuration: return "Konfigürasyon";
                case TargetKind.CustomProperty: return "Özel özellik";
                case TargetKind.OutputFileName: return "Dosya adı";
                default: return kind.ToString();
            }
        }

        private object Uret(Proje p, HesapIstegi istek)
        {
            var siparis = (istek.Siparis ?? string.Empty).Trim();
            if (siparis.Length == 0 || siparis.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || siparis.StartsWith("."))
                throw new KullaniciHatasi("Geçerli bir sipariş adı girin (ör. S0001).");
            var (rules, sonuc) = Degerlendir(p, istek);
            if (!sonuc.Success) throw new KullaniciHatasi("Kurallar hesaplanamadı: " + string.Join(" ", sonuc.Errors));
            if (Sorun(p, rules) is string sorun) throw new KullaniciHatasi(sorun);
            var outDir = Path.Combine(p.Siparisler, siparis);
            if (Directory.Exists(outDir) && Directory.EnumerateFileSystemEntries(outDir).Any())
                throw new KullaniciHatasi($"Bu sipariş zaten var: {siparis}. Başka bir ad verin.");
            return IsBaslat(p, "uret", true, (job, proje) =>
            {
                job.Siparis = siparis;
                job.Klasor = outDir;
                job.Yaz($"Sipariş {siparis}: {sonuc.Actions.Count} değişiklik hesaplandı.");
#if SOLIDWORKS
                job.Yaz("SolidWorks'e bağlanılıyor…");
                using (var session = SwSession.Connect(visible: false))
                {
                    var request = new OrderRequest
                    {
                        Rules = rules,
                        Evaluation = sonuc,
                        MasterAssembly = proje.Tam(proje.Master)!,
                        RootAssembly = proje.Tam(proje.Kok),
                        OutputFolder = outDir,
                        LibraryFolder = proje.Kutuphane,
                        ExportPdf = istek.Pdf,
                        ExportStep = istek.Step,
                        Progress = job.Yaz,
                    };
                    var gen = new OrderBuilder(session).Build(request);
                    foreach (var l in gen.Log.Where(l => l.StartsWith("Kütüphane") || l.StartsWith("Ortak parça"))) job.Yaz(l);
                    job.Uyarilar.AddRange(gen.Warnings);
                    foreach (var e in gen.Errors) job.Hatalar.Add(e);
                    foreach (var f in gen.ExportedFiles) job.Yaz("Dışa aktarıldı: " + f);
                    job.Ozet = gen.Log.LastOrDefault(l => l.StartsWith("Kütüphane"));
                    if (gen.Success) job.Montaj = gen.AssemblyPath;
                }
#else
                throw new KullaniciHatasi("Üretim SolidWorks gerektirir (Windows, ruleforge.exe).");
#endif
            });
        }

        private object Siparisler(Proje p)
        {
            if (!Directory.Exists(p.Siparisler)) return new object[0];
            return Directory.GetDirectories(p.Siparisler).Where(d => !d.EndsWith(".calisma", StringComparison.OrdinalIgnoreCase))
                .Select(d => new DirectoryInfo(d)).OrderByDescending(d => d.CreationTime).Take(50)
                .Select(d => new
                {
                    Ad = d.Name,
                    Klasor = d.FullName,
                    Tarih = d.CreationTime.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture),
                    Montaj = d.GetFiles("*.SLDASM").Select(f => f.FullName).FirstOrDefault(),
                }).ToList();
        }

        /// <summary>Proje içindeki bir klasörü Gezgin'de ya da bir montajı SolidWorks'te açar.</summary>
        private object Ac(Proje p, AcIstegi istek)
        {
            var full = Path.GetFullPath(istek.Yol);
            if (!full.StartsWith(p.Klasor.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase)) throw new KullaniciHatasi("Bu yol açılamaz.");
            if (!File.Exists(full) && !Directory.Exists(full)) throw new KullaniciHatasi("Bulunamadı: " + full);
            if (istek.SolidWorks && File.Exists(full))
            {
#if SOLIDWORKS
                CalisanYok();
                // Oturum kapatılmaz: SolidWorks'ü bu istek başlattıysa kapanmasın, model kullanıcıda açık kalsın.
                var session = SwSession.Connect(visible: true);
                session.App.Visible = true;
                int errors = 0, warnings = 0;
                session.App.OpenDoc6(full, (int)SwSession.DocumentTypeOf(full), 0, "", ref errors, ref warnings);
                return new { Tamam = true };
#else
                throw new KullaniciHatasi("SolidWorks gerekli.");
#endif
            }
            Process.Start(new ProcessStartInfo("explorer.exe", Directory.Exists(full) ? $"\"{full}\"" : $"/select,\"{full}\"") { UseShellExecute = true });
            return new { Tamam = true };
        }

        // ---------------------------------------------------------------- Arka plan işleri

        private sealed class Is
        {
            public string Id { get; set; } = string.Empty;
            public string Tur { get; set; } = string.Empty;
            public string Proje { get; set; } = string.Empty;
            public string Durum { get; set; } = "calisiyor"; // calisiyor | bitti | hata
            public DateTime Baslangic { get; } = DateTime.Now;
            public DateTime? Bitis { get; set; }
            public List<string> Gunluk { get; } = new List<string>();
            public List<string> Hatalar { get; } = new List<string>();
            public List<string> Uyarilar { get; } = new List<string>();
            public string? Siparis { get; set; }
            public string? Klasor { get; set; }
            public string? Montaj { get; set; }
            public string? Ozet { get; set; }

            public void Yaz(string s)
            {
                lock (Gunluk) Gunluk.Add(DateTime.Now.ToString("HH:mm:ss", CultureInfo.InvariantCulture) + "  " + s);
            }
        }

        private void CalisanYok()
        {
            var c = _calisan;
            if (c != null && c.Durum == "calisiyor") throw new KullaniciHatasi("Şu an başka bir iş sürüyor; bitmesini bekleyin.");
        }

        /// <summary>İşi arka planda (STA iş parçacığı) başlatır; SolidWorks bir seferde tek iş yapar.</summary>
        private object IsBaslat(Proje p, string tur, bool solidWorks, Action<Is, Proje> calis)
        {
            Is job;
            lock (_isKilidi)
            {
                CalisanYok();
                job = new Is { Id = Guid.NewGuid().ToString("N").Substring(0, 10), Tur = tur, Proje = p.Id };
                _isler[job.Id] = job;
                _calisan = job;
            }
            var thread = new Thread(() =>
            {
                try
                {
                    calis(job, p);
                    job.Durum = job.Hatalar.Count == 0 ? "bitti" : "hata";
                }
                catch (Exception ex)
                {
                    job.Hatalar.Add(ex.Message);
                    job.Durum = "hata";
                }
                finally
                {
                    job.Bitis = DateTime.Now;
                }
            }) { IsBackground = true, Name = tur + "-" + p.Id };
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            return new { Is = job.Id };
        }

        private object IsDurumu(string? id)
        {
            if (id == null || !_isler.TryGetValue(id, out var job)) throw new KullaniciHatasi("İş bulunamadı.");
            List<string> gunluk;
            lock (job.Gunluk) gunluk = job.Gunluk.ToList();
            return new
            {
                job.Id,
                job.Tur,
                job.Durum,
                Sure = (int)((job.Bitis ?? DateTime.Now) - job.Baslangic).TotalSeconds,
                Gunluk = gunluk,
                job.Hatalar,
                job.Uyarilar,
                job.Siparis,
                job.Klasor,
                job.Montaj,
                job.Ozet,
            };
        }
    }
}
