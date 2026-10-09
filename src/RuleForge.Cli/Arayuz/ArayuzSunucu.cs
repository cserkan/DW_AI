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
using RuleForge.Core.Files;
using RuleForge.Core.Json;
using RuleForge.Core.Rules;
#if SOLIDWORKS
using RuleForge.SolidWorks;
#endif

namespace RuleForge.Cli.Arayuz
{
    /// <summary>
    /// Tarayıcı arayüzü: bu bilgisayarda (127.0.0.1) küçük bir HTTP sunucusu. Sayfa programın içinde gömülü; formlar kural
    /// dosyalarından oluşur, hesaplama kural motoruyla anında yapılır, üretim SolidWorks'te arka planda çalışır.
    /// </summary>
    internal sealed class ArayuzSunucu
    {
        private readonly Ayarlar _ayarlar;
        private readonly ConcurrentDictionary<string, Is> _isler = new ConcurrentDictionary<string, Is>();
        private volatile Is? _calisan;

        private static readonly JsonSerializerOptions Json = JsonStore.Options;

        public ArayuzSunucu(Ayarlar ayarlar)
        {
            _ayarlar = ayarlar;
        }

        public static int Calistir(Args args)
        {
            var path = Ayarlar.Ara(args.Get("config"));
            if (path == null)
            {
                Console.Error.WriteLine("urunler.json bulunamadı. --config <dosya> ile verin ya da depo klasöründe çalıştırın.");
                return 2;
            }
            var ayarlar = Ayarlar.Yukle(path);
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
            Console.WriteLine($"RuleForge arayüzü: {url}  (ayarlar: {path})");
            Console.WriteLine("Kapatmak için bu pencerede Ctrl+C.");
            if (!args.Flag("no-browser"))
            {
                try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
                catch (Exception ex) { Console.WriteLine("Tarayıcı açılamadı: " + ex.Message); }
            }
            new ArayuzSunucu(ayarlar).Dinle(listener);
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

        // ---------------------------------------------------------------- HTTP

        private void Isle(TcpClient client)
        {
            client.ReceiveTimeout = 30000;
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
            int length = 0;
            foreach (var l in lines.Skip(1))
                if (l.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
                    int.TryParse(l.Substring(15).Trim(), out length);
            var body = new byte[length];
            for (int read = 0; read < length;)
            {
                int r = stream.Read(body, read, length - read);
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
                    var result = Api(method, path, query, Encoding.UTF8.GetString(body));
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
                .ToDictionary(p => Uri.UnescapeDataString(p[0]), p => p.Length > 1 ? Uri.UnescapeDataString(p[1].Replace('+', ' ')) : string.Empty,
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

        // ---------------------------------------------------------------- API

        private object Api(string method, string path, Dictionary<string, string> query, string body)
        {
            switch (method + " " + path)
            {
                case "GET /api/urunler": return Urunler();
                case "GET /api/urun": return Urun(query.TryGetValue("id", out var id) ? id : null);
                case "POST /api/hesapla": return Hesapla(Istek(body));
                case "POST /api/uret": return Uret(Istek(body));
                case "GET /api/is": return IsDurumu(query.TryGetValue("id", out var isId) ? isId : null);
                case "GET /api/siparisler": return Siparisler(query.TryGetValue("id", out var u) ? u : null);
                case "GET /api/kutuphane": return KutuphaneListesi(query.TryGetValue("id", out var k) ? k : null);
                case "POST /api/ac": return Ac(JsonSerializer.Deserialize<AcIstegi>(body, Json) ?? new AcIstegi());
                default: throw new KullaniciHatasi("Bilinmeyen istek: " + method + " " + path);
            }
        }

        private object Urunler() => _ayarlar.Urunler.Select(u => new
        {
            u.Id,
            u.Ad,
            u.Aciklama,
            Sorun = Sorun(u),
        }).ToList();

        /// <summary>Ürünün üretilememe nedeni (kural dosyası ya da master yok); sorun yoksa null.</summary>
        private string? Sorun(UrunAyari u)
        {
            if (!File.Exists(_ayarlar.Yol(u.Kurallar))) return "Kural dosyası bulunamadı: " + u.Kurallar;
            var rules = KuralSeti(u);
            var master = string.IsNullOrEmpty(u.Master) ? rules.MasterAssembly : u.Master!;
            if (string.IsNullOrEmpty(master) || !File.Exists(_ayarlar.Yol(master))) return "Master montaj bulunamadı: " + master;
            if (rules.Tables.Count > 0 && (string.IsNullOrEmpty(u.Kok) || !File.Exists(_ayarlar.Yol(u.Kok!))))
                return "Kopyaları toplayan montaj (kok) bulunamadı: " + u.Kok;
            return null;
        }

        private RuleSet KuralSeti(UrunAyari u) => JsonStore.Load<RuleSet>(_ayarlar.Yol(u.Kurallar));

        private UrunAyari UrunBul(string? id) => _ayarlar.Bul(id) ?? throw new KullaniciHatasi("Ürün bulunamadı: " + id);

        private static T? Ayar<T>(Dictionary<string, T>? map, string key) where T : class =>
            map == null ? null : map.TryGetValue(key, out var v) ? v : map.FirstOrDefault(kv => string.Equals(kv.Key, key, StringComparison.OrdinalIgnoreCase)).Value;

        private static object Girdi(InputDefinition d, GirdiAyari? a)
        {
            var secenekler = a?.Secenekler ?? d.Options;
            return new
            {
                d.Name,
                Etiket = a?.Etiket ?? d.Label,
                Tip = secenekler.Count > 0 ? "secim" : d.Type == InputType.Number ? "sayi" : d.Type == InputType.Bool ? "evet-hayir" : "metin",
                Birim = a?.Birim ?? d.Unit,
                d.Min,
                d.Max,
                Varsayilan = d.Default,
                Secenekler = secenekler.Select(s => new { Deger = s, Etiket = Ayar(a?.SecenekEtiketleri, s) ?? s }).ToList(),
                Aciklama = d.Description,
            };
        }

        private object Urun(string? id)
        {
            var u = UrunBul(id);
            var rules = KuralSeti(u);
            return new
            {
                u.Id,
                u.Ad,
                u.Aciklama,
                Sorun = Sorun(u),
                KuralSayisi = rules.Rules.Count,
                OnayliKural = rules.Rules.Count(r => r.Status == RuleStatus.Approved),
                Girdiler = rules.Inputs.Select(d => Girdi(d, Ayar(u.Girdiler, d.Name))).ToList(),
                Tablolar = rules.Tables.Select(t =>
                {
                    var ta = Ayar(u.Tablolar, t.Name);
                    return new
                    {
                        t.Name,
                        Etiket = ta?.Etiket ?? t.Label,
                        SatirEtiketi = ta?.SatirEtiketi ?? "Satır",
                        MinSatir = t.MinRows ?? 1,
                        MaxSatir = t.MaxRows ?? 20,
                        Sutunlar = t.Columns.Select(c => Girdi(c, Ayar(ta?.Sutunlar, c.Name))).ToList(),
                    };
                }).ToList(),
                SiparisOnerisi = SiparisOnerisi(u),
            };
        }

        private string SiparisKlasoru(UrunAyari u) => Path.Combine(_ayarlar.Yol(_ayarlar.SiparisKlasoru), u.Id);
        private string KutuphaneKlasoru(UrunAyari u) => Path.Combine(_ayarlar.Yol(_ayarlar.Kutuphane), u.Id);

        private string SiparisOnerisi(UrunAyari u)
        {
            var dir = SiparisKlasoru(u);
            int max = 0;
            if (Directory.Exists(dir))
                foreach (var d in Directory.GetDirectories(dir))
                {
                    var m = Regex.Match(Path.GetFileName(d), @"^S(\d+)$");
                    if (m.Success) max = Math.Max(max, int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture));
                }
            return "S" + (max + 1).ToString("D4", CultureInfo.InvariantCulture);
        }

        private sealed class HesapIstegi
        {
            public string Id { get; set; } = string.Empty;
            public Dictionary<string, string> Girdiler { get; set; } = new Dictionary<string, string>();
            public Dictionary<string, List<Dictionary<string, string>>> Tablolar { get; set; } = new Dictionary<string, List<Dictionary<string, string>>>();
            public bool Onerilen { get; set; } = true;
            public string? Siparis { get; set; }
            public bool Pdf { get; set; }
            public bool Step { get; set; }
        }

        private sealed class AcIstegi
        {
            public string Yol { get; set; } = string.Empty;
            public bool SolidWorks { get; set; }
        }

        private static HesapIstegi Istek(string body) =>
            JsonSerializer.Deserialize<HesapIstegi>(body, Json) ?? throw new KullaniciHatasi("Boş istek.");

        private (UrunAyari urun, RuleSet rules, EvaluationResult sonuc) Degerlendir(HesapIstegi istek)
        {
            var u = UrunBul(istek.Id);
            var rules = KuralSeti(u);
            // Komut satırıyla aynı ayrıştırma: "Ad=Değer" ve "Tablo.Sütun=v1;v2".
            var pairs = istek.Girdiler.Where(kv => !string.IsNullOrWhiteSpace(kv.Value)).Select(kv => kv.Key + "=" + kv.Value.Trim()).ToList();
            foreach (var t in istek.Tablolar)
            {
                var columns = t.Value.SelectMany(r => r.Keys).Distinct().ToList();
                foreach (var c in columns)
                    pairs.Add($"{t.Key}.{c}=" + string.Join(";", t.Value.Select(r => r.TryGetValue(c, out var v) ? v.Trim() : string.Empty)));
            }
            var inputs = RuleEngine.ParseAssignments(pairs, out var tables);
            var sonuc = RuleEngine.Evaluate(rules, inputs, new EvaluationOptions { IncludeProposed = istek.Onerilen }, tables);
            return (u, rules, sonuc);
        }

        private object Hesapla(HesapIstegi istek)
        {
            var (_, rules, r) = Degerlendir(istek);
            var inputNames = new HashSet<string>(rules.Inputs.Select(i => i.Name), StringComparer.OrdinalIgnoreCase);
            return new
            {
                Basarili = r.Success,
                Hatalar = r.Errors,
                Uyarilar = r.Warnings,
                // Girdilerin dışında hesaplanan ara değerler (eşikler, adetler…).
                Degerler = r.Values.Where(kv => !inputNames.Contains(kv.Key)).Select(kv => new { Ad = kv.Key, Deger = kv.Value.AsText() }).ToList(),
                Eylemler = r.Actions.Select(a => new
                {
                    Satir = a.Instance,
                    Tur = TurAdi(a.Target.Kind),
                    Hedef = string.IsNullOrEmpty(a.Rule.Description) ? a.Target.ToString() : a.Rule.Description,
                    Deger = a.Value.AsText(),
                    Onerilen = a.Rule.Status == RuleStatus.Proposed,
                }).ToList(),
                AtlananKural = r.SkippedRules.Count,
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

        // ---------------------------------------------------------------- Üretim

        private sealed class Is
        {
            public string Id { get; set; } = string.Empty;
            public string Urun { get; set; } = string.Empty;
            public string Siparis { get; set; } = string.Empty;
            public string Durum { get; set; } = "calisiyor"; // calisiyor | bitti | hata
            public DateTime Baslangic { get; set; } = DateTime.Now;
            public DateTime? Bitis { get; set; }
            public List<string> Gunluk { get; } = new List<string>();
            public string? Montaj { get; set; }
            public string? Klasor { get; set; }
            public List<string> Hatalar { get; } = new List<string>();
            public List<string> Uyarilar { get; } = new List<string>();
            public string? Kutuphane { get; set; }

            public void Yaz(string s)
            {
                lock (Gunluk) Gunluk.Add(DateTime.Now.ToString("HH:mm:ss", CultureInfo.InvariantCulture) + "  " + s);
            }
        }

        private object Uret(HesapIstegi istek)
        {
            var siparis = (istek.Siparis ?? string.Empty).Trim();
            if (siparis.Length == 0 || siparis.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || siparis.StartsWith("."))
                throw new KullaniciHatasi("Geçerli bir sipariş adı girin (ör. S0001).");
            var (u, rules, sonuc) = Degerlendir(istek);
            if (!sonuc.Success) throw new KullaniciHatasi("Kurallar hesaplanamadı: " + string.Join(" ", sonuc.Errors));
            if (Sorun(u) is string sorun) throw new KullaniciHatasi(sorun);
            var outDir = Path.Combine(SiparisKlasoru(u), siparis);
            if (Directory.Exists(outDir) && Directory.EnumerateFileSystemEntries(outDir).Any())
                throw new KullaniciHatasi($"Bu sipariş zaten var: {outDir}. Başka bir ad verin.");
            var calisan = _calisan;
            if (calisan != null && calisan.Durum == "calisiyor")
                throw new KullaniciHatasi($"Şu an başka bir üretim sürüyor ({calisan.Siparis}). Bitmesini bekleyin.");

            var job = new Is { Id = Guid.NewGuid().ToString("N").Substring(0, 10), Urun = u.Id, Siparis = siparis, Klasor = outDir };
            _isler[job.Id] = job;
            _calisan = job;
            job.Yaz($"{u.Ad} — sipariş {siparis}: {sonuc.Actions.Count} eylem hesaplandı.");
            var thread = new Thread(() => UretimCalistir(job, u, rules, sonuc, outDir, istek)) { IsBackground = true, Name = "uretim-" + siparis };
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            return new { Is = job.Id };
        }

        private void UretimCalistir(Is job, UrunAyari u, RuleSet rules, EvaluationResult sonuc, string outDir, HesapIstegi istek)
        {
            try
            {
#if SOLIDWORKS
                job.Yaz("SolidWorks'e bağlanılıyor…");
                using (var session = SwSession.Connect(visible: false))
                {
                    job.Yaz("Bağlandı: " + session.VersionLabel + ". Model üretiliyor (birkaç dakika sürebilir)…");
                    var request = new OrderRequest
                    {
                        Rules = rules,
                        Evaluation = sonuc,
                        MasterAssembly = _ayarlar.Yol(string.IsNullOrEmpty(u.Master) ? rules.MasterAssembly : u.Master!),
                        RootAssembly = string.IsNullOrEmpty(u.Kok) ? null : _ayarlar.Yol(u.Kok!),
                        OutputFolder = outDir,
                        LibraryFolder = KutuphaneKlasoru(u),
                        ExportPdf = istek.Pdf,
                        ExportStep = istek.Step,
                        Progress = job.Yaz,
                    };
                    var gen = new OrderBuilder(session).Build(request);
                    foreach (var l in gen.Log.Where(l => !l.Contains(" = ") && !l.Contains("Master kopyalandı") && !l.StartsWith("[satır") && !l.StartsWith("Kaydedildi")))
                        job.Yaz(l);
                    if (gen.Skipped.Count > 0) job.Yaz($"{gen.Skipped.Count} eylem bu siparişte kullanılmayan dosyalar için atlandı (normal).");
                    job.Uyarilar.AddRange(gen.Warnings);
                    job.Hatalar.AddRange(gen.Errors);
                    foreach (var f in gen.ExportedFiles) job.Yaz("Dışa aktarıldı: " + f);
                    job.Kutuphane = gen.Log.LastOrDefault(l => l.StartsWith("Kütüphane"));
                    if (gen.Success)
                    {
                        job.Montaj = gen.AssemblyPath;
                        job.Yaz("Tamamlandı: " + gen.AssemblyPath);
                    }
                    job.Durum = gen.Success ? "bitti" : "hata";
                }
#else
                job.Hatalar.Add("Üretim SolidWorks gerektirir: Windows'ta ruleforge.exe (net48) ile çalıştırın.");
                job.Durum = "hata";
#endif
            }
            catch (Exception ex)
            {
                job.Hatalar.Add(ex.Message);
                job.Durum = "hata";
            }
            finally
            {
                job.Bitis = DateTime.Now;
                if (job.Durum == "hata") job.Yaz("Üretim hatalarla bitti.");
            }
        }

        private object IsDurumu(string? id)
        {
            if (id == null || !_isler.TryGetValue(id, out var job)) throw new KullaniciHatasi("İş bulunamadı.");
            List<string> gunluk;
            lock (job.Gunluk) gunluk = job.Gunluk.ToList();
            return new
            {
                job.Id,
                job.Durum,
                job.Siparis,
                Sure = (int)((job.Bitis ?? DateTime.Now) - job.Baslangic).TotalSeconds,
                Gunluk = gunluk,
                job.Montaj,
                job.Klasor,
                job.Hatalar,
                job.Uyarilar,
                job.Kutuphane,
            };
        }

        private object Siparisler(string? id)
        {
            var u = UrunBul(id);
            var dir = SiparisKlasoru(u);
            if (!Directory.Exists(dir)) return new object[0];
            return Directory.GetDirectories(dir).Where(d => !d.EndsWith(".calisma", StringComparison.OrdinalIgnoreCase))
                .Select(d => new DirectoryInfo(d)).OrderByDescending(d => d.CreationTime).Take(50)
                .Select(d => new
                {
                    Ad = d.Name,
                    Klasor = d.FullName,
                    Tarih = d.CreationTime.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture),
                    Montaj = d.GetFiles("*.SLDASM").Select(f => f.FullName).FirstOrDefault(),
                }).ToList();
        }

        private object KutuphaneListesi(string? id)
        {
            var u = UrunBul(id);
            var dir = KutuphaneKlasoru(u);
            if (!File.Exists(Path.Combine(dir, PartLibrary.IndexFileName))) return new { Klasor = dir, Dosyalar = new object[0] };
            var lib = PartLibrary.Open(dir);
            return new
            {
                Klasor = dir,
                Dosyalar = lib.Entries.OrderByDescending(e => e.CreatedUtc).Take(300).Select(e => new
                {
                    e.File,
                    e.Master,
                    e.Order,
                    e.Summary,
                    Tarih = e.CreatedUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture),
                }).ToList(),
            };
        }

        /// <summary>Sipariş ya da kütüphane klasörünü Gezgin'de, ya da sipariş montajını SolidWorks'te açar (sadece bu klasörlerin içi).</summary>
        private object Ac(AcIstegi istek)
        {
            var full = Path.GetFullPath(istek.Yol);
            var izinli = new[] { _ayarlar.Yol(_ayarlar.SiparisKlasoru), _ayarlar.Yol(_ayarlar.Kutuphane) };
            if (!izinli.Any(root => full.StartsWith(root.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase)))
                throw new KullaniciHatasi("Bu klasör açılamaz.");
            if (!File.Exists(full) && !Directory.Exists(full)) throw new KullaniciHatasi("Bulunamadı: " + full);
            if (istek.SolidWorks && File.Exists(full))
            {
#if SOLIDWORKS
                var calisan = _calisan;
                if (calisan != null && calisan.Durum == "calisiyor") throw new KullaniciHatasi("Üretim sürerken açılamaz.");
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
    }
}
