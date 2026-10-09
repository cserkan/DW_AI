using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using RuleForge.Core.Model;

namespace RuleForge.Inference
{
    /// <summary>Bir varyanttaki tekrarlanan modülün bir kopyası (ör. konveyör hattının 2. bölümü).</summary>
    public sealed class ModuleInstance
    {
        public string Variant { get; set; } = string.Empty;

        /// <summary>Kopyanın örnek adı: "&lt;varyant&gt;#&lt;satır&gt;".</summary>
        public string Name { get; set; } = string.Empty;

        /// <summary>Satır numarası (1'den başlar), dosya adındaki sıra numarasına göre.</summary>
        public int Row { get; set; }

        /// <summary>Varyant montajındaki bileşen yolu; boşsa varyantın kökü modülün kendisidir.</summary>
        public string ComponentPath { get; set; } = string.Empty;

        public string DocumentKey { get; set; } = string.Empty;
    }

    /// <summary>
    /// Varyantların "modül kopyaları" ve "dış kısım" olarak bölünmüş hâli. Her kopya kendi başına bir örnektir;
    /// böylece mevcut çıkarım her bölüm için ayrı çalışır (10 varyant × 3 bölüm = 30 örnek).
    /// </summary>
    public sealed class ModuleSplit
    {
        /// <summary>Tekrarlanan modülün referans (master) dosyası.</summary>
        public string Document { get; set; } = string.Empty;

        /// <summary>Modülün referanstaki bileşen yolu; null = modül referansın kendisi (varyant kökü ayrı bir hat montajı).</summary>
        public string? ComponentPath { get; set; }

        public string TableName { get; set; } = string.Empty;

        /// <summary>Kopyaların eşleştirildiği referans: modülün master'daki hâli.</summary>
        public ModelSnapshot InstanceReference { get; set; } = new ModelSnapshot();

        public List<VariantSample> Instances { get; set; } = new List<VariantSample>();
        public List<ModuleInstance> InstanceInfo { get; set; } = new List<ModuleInstance>();

        /// <summary>Varyant düzeyi: kopyalar çıkarılmış montaj (hat montajı, ortak parçalar...).</summary>
        public List<VariantSample> Outer { get; set; } = new List<VariantSample>();

        /// <summary>Dış kısmın referansı; null ise ilk varyantın dış kısmı kullanılır (master'da karşılığı yok).</summary>
        public ModelSnapshot? OuterReference { get; set; }

        /// <summary>Örnek adı (kopya ya da varyant) → varyant dosyası → referans dosyası eşlemesi (adlardan çözülen).</summary>
        public Dictionary<string, Dictionary<string, string>> DocumentMaps { get; set; } =
            new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);

        public List<string> Notes { get; set; } = new List<string>();

        /// <summary>Modülün içinde olup her varyantta tüm kopyaların aynı dosyayı kullandığı referans dosyaları (ör. makara).</summary>
        public List<string> SharedDocuments { get; set; } = new List<string>();

        public string VariantOf(string instanceName) =>
            InstanceInfo.First(i => string.Equals(i.Name, instanceName, StringComparison.OrdinalIgnoreCase)).Variant;

        public List<ModuleInstance> InstancesOf(string variant) =>
            InstanceInfo.Where(i => string.Equals(i.Variant, variant, StringComparison.OrdinalIgnoreCase)).OrderBy(i => i.Row).ToList();
    }

    /// <summary>
    /// Tekrarlanan modülleri bulur: master'daki bir dosyanın bir varyantta birden çok farklı kopyası varsa
    /// (ör. "Conveyor Assembly -1-0007", "Conveyor Assembly -2-0007") bu bir modüldür ve her kopya ayrı parametrelerle
    /// üretilmiştir. DriveWorks bunu tablo/alt spesifikasyonlarla yapar; kural çıkarımı da her kopyayı ayrı örnek sayar.
    /// </summary>
    public static class ModuleDetector
    {
        public static ModuleSplit? Detect(IReadOnlyList<VariantSample> samples, ModelSnapshot? master, InferenceOptions? options = null)
        {
            if (samples.Count < 2) return null;
            var reference = master ?? samples[0].Snapshot;
            var maps = samples.ToDictionary(s => s.Name, s => DocumentMatcher.Map(reference, s.Snapshot), StringComparer.OrdinalIgnoreCase);

            // Bir varyantta aynı master dosyasının birden çok farklı kopyası bileşen olarak kullanılıyorsa: tekrarlanan.
            var repeated = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var s in samples)
            {
                var used = new HashSet<string>(s.Snapshot.Components.Select(c => c.DocumentKey), StringComparer.OrdinalIgnoreCase);
                foreach (var g in maps[s.Name].Where(kv => used.Contains(kv.Key)).GroupBy(kv => kv.Value, StringComparer.OrdinalIgnoreCase))
                {
                    var n = g.Count();
                    if (n >= 2) repeated[g.Key] = Math.Max(n, repeated.TryGetValue(g.Key, out var m) ? m : 0);
                }
            }
            if (repeated.Count == 0) return null;

            // En üstteki tekrarlanan dosya modüldür; altındakiler (bölümün çerçevesi, rayı...) zaten kopyanın içindedir.
            int Depth(string doc)
            {
                if (string.Equals(doc, reference.RootDocument, StringComparison.OrdinalIgnoreCase)) return -1;
                var comps = reference.Components.Where(c => string.Equals(c.DocumentKey, doc, StringComparison.OrdinalIgnoreCase)).ToList();
                return comps.Count == 0 ? int.MaxValue : comps.Min(c => c.Path.Count(ch => ch == '/'));
            }
            var ranked = repeated.Keys.Select(d => (doc: d, depth: Depth(d))).Where(x => x.depth != int.MaxValue)
                .OrderBy(x => x.depth).ThenByDescending(x => repeated[x.doc]).ThenBy(x => x.doc, StringComparer.OrdinalIgnoreCase).ToList();
            if (ranked.Count == 0) return null;
            var module = ranked[0].doc;
            var wrapper = ranked[0].depth == -1;
            var moduleComponent = wrapper ? null : reference.Components
                .Where(c => string.Equals(c.DocumentKey, module, StringComparison.OrdinalIgnoreCase))
                .OrderBy(c => c.Path.Count(ch => ch == '/')).ThenBy(c => c.Path, StringComparer.Ordinal).First();

            var split = new ModuleSplit
            {
                Document = module,
                ComponentPath = moduleComponent?.Path,
                TableName = RuleInferencer.SanitizeName(Path.GetFileNameWithoutExtension(module)),
                InstanceReference = wrapper ? reference : SubSnapshot(reference, moduleComponent!, reference.Name),
                OuterReference = wrapper ? null : OuterSnapshot(reference, new[] { moduleComponent!.Path }, reference.Name),
            };

            foreach (var other in ranked.Skip(1).Where(x => !IsInside(reference, x.doc, module)))
                split.Notes.Add($"'{other.doc}' de varyantlarda birden çok kopya hâlinde; şimdilik sadece '{module}' modül olarak ele alındı.");

            foreach (var s in samples)
            {
                var map = maps[s.Name];
                var snap = s.Snapshot;
                bool IsModule(string? key) => key != null && map.TryGetValue(key, out var r) && string.Equals(r, module, StringComparison.OrdinalIgnoreCase);

                var instances = new List<(ComponentInfo? comp, string doc)>();
                if (IsModule(snap.RootDocument)) instances.Add((null, snap.RootDocument));
                else
                {
                    var byPath = snap.Components.ToDictionary(c => c.Path, StringComparer.Ordinal);
                    foreach (var c in snap.Components.Where(c => IsModule(c.DocumentKey)))
                    {
                        bool nested = false;
                        for (var p = c.ParentPath; p != null && !nested; p = byPath.TryGetValue(p, out var pc) ? pc.ParentPath : null)
                            nested = byPath.TryGetValue(p, out var parent) && IsModule(parent.DocumentKey);
                        if (!nested) instances.Add((c, c.DocumentKey));
                    }
                }

                var ordered = Order(instances, module, snap);
                for (int i = 0; i < ordered.Count; i++)
                {
                    var (comp, doc) = ordered[i];
                    var name = $"{s.Name}#{i + 1}";
                    var sub = comp == null ? Clone(snap, name) : SubSnapshot(snap, comp, name);
                    split.Instances.Add(new VariantSample(name, sub, s.Inputs));
                    split.InstanceInfo.Add(new ModuleInstance { Variant = s.Name, Name = name, Row = i + 1, ComponentPath = comp?.Path ?? string.Empty, DocumentKey = doc });
                    var subDocs = new HashSet<string>(sub.Documents.Select(d => d.Key), StringComparer.OrdinalIgnoreCase);
                    split.DocumentMaps[name] = map.Where(kv => subDocs.Contains(kv.Key)).ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.OrdinalIgnoreCase);
                }

                var outer = ordered.Any(o => o.comp == null)
                    ? new ModelSnapshot { Name = s.Name, SourcePath = snap.SourcePath, RootDocument = snap.RootDocument, SolidWorksVersion = snap.SolidWorksVersion }
                    : OuterSnapshot(snap, ordered.Select(o => o.comp!.Path), s.Name);
                split.Outer.Add(new VariantSample(s.Name, outer, s.Inputs));
                split.DocumentMaps[s.Name] = map;
            }

            split.SharedDocuments = SharedDocuments(split, module);

            var counts = samples.Select(s => split.InstanceInfo.Count(i => i.Variant == s.Name)).ToList();
            split.Notes.Add($"Tekrarlanan modül bulundu: '{module}'. Varyantlarda {counts.Min()}–{counts.Max()} kopya " +
                            $"(toplam {split.Instances.Count}); her kopya ayrı örnek olarak incelendi." +
                            (wrapper ? " Varyantların kökü kopyaları bir araya getiren ayrı bir montaj; master'da karşılığı yok." : string.Empty));
            return split;
        }

        /// <summary>
        /// Kopyalar arasında ortak dosyalar: bir varyantta en az iki kopyada bulunup hepsinde aynı varyant dosyası olan, hiçbir
        /// varyantta kopyadan kopyaya farklı dosya olmayan referans dosyaları. Kopyada hiç bulunmaması sorun değildir
        /// (ör. kısa konveyörde DriveWorks desteği silmiş).
        /// </summary>
        private static List<string> SharedDocuments(ModuleSplit split, string module)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var differs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var variant in split.InstanceInfo.Select(i => i.Variant).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                // referans dosyası → kopyalarda kullanılan varyant dosyaları
                var files = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
                foreach (var instance in split.InstancesOf(variant))
                foreach (var g in split.DocumentMaps[instance.Name].GroupBy(kv => kv.Value, StringComparer.OrdinalIgnoreCase))
                {
                    if (!files.TryGetValue(g.Key, out var list)) files[g.Key] = list = new List<string>();
                    list.AddRange(g.Select(kv => kv.Key));
                    if (g.Count() > 1) differs.Add(g.Key); // bir kopyanın içinde bile iki farklı dosya
                }
                foreach (var kv in files.Where(kv => !string.Equals(kv.Key, module, StringComparison.OrdinalIgnoreCase)))
                {
                    if (kv.Value.Distinct(StringComparer.OrdinalIgnoreCase).Count() > 1) differs.Add(kv.Key);
                    else if (kv.Value.Count >= 2) seen.Add(kv.Key);
                }
            }
            return seen.Except(differs, StringComparer.OrdinalIgnoreCase).OrderBy(d => d, StringComparer.OrdinalIgnoreCase).ToList();
        }

        private static bool IsInside(ModelSnapshot reference, string doc, string module)
        {
            if (string.Equals(module, reference.RootDocument, StringComparison.OrdinalIgnoreCase)) return true;
            var moduleComps = reference.Components.Where(c => string.Equals(c.DocumentKey, module, StringComparison.OrdinalIgnoreCase)).Select(c => c.Path + "/").ToList();
            return reference.Components.Any(c => string.Equals(c.DocumentKey, doc, StringComparison.OrdinalIgnoreCase) &&
                                                 moduleComps.Any(p => c.Path.StartsWith(p, StringComparison.Ordinal)));
        }

        /// <summary>
        /// Kopyaların sırası: dosya adlarında kopyadan kopyaya değişen sayı (DriveWorks "-1-", "-2-"), yoksa bileşen
        /// örnek numarası, o da yoksa ağaçtaki sıra.
        /// </summary>
        private static List<(ComponentInfo? comp, string doc)> Order(List<(ComponentInfo? comp, string doc)> instances, string module, ModelSnapshot snap)
        {
            if (instances.Count <= 1) return instances;
            var masterStem = DocumentMatcher.Normalize(Path.GetFileNameWithoutExtension(module));
            var tokens = instances.Select(x =>
            {
                var stem = DocumentMatcher.Normalize(Path.GetFileNameWithoutExtension(x.doc));
                var rest = stem.StartsWith(masterStem, StringComparison.Ordinal) ? stem.Substring(masterStem.Length) : stem;
                return Regex.Matches(rest, @"\d+").Cast<Match>().Select(m => long.Parse(m.Value)).ToList();
            }).ToList();
            var tree = instances.Select(x => x.comp == null ? -1 : snap.Components.IndexOf(x.comp)).ToList();

            if (tokens.All(t => t.Count == tokens[0].Count && t.Count > 0))
            {
                for (int p = 0; p < tokens[0].Count; p++)
                {
                    if (tokens.Select(t => t[p]).Distinct().Count() < 2) continue;
                    return instances.Select((x, i) => (x, i)).OrderBy(t => tokens[t.i][p]).ThenBy(t => tree[t.i]).Select(t => t.x).ToList();
                }
            }
            return instances.Select((x, i) => (x, i))
                .OrderBy(t => InstanceNumber(t.x.comp?.Name)).ThenBy(t => tree[t.i]).Select(t => t.x).ToList();
        }

        private static long InstanceNumber(string? name)
        {
            if (name == null) return 0;
            var dash = name.LastIndexOf('-');
            return dash >= 0 && long.TryParse(name.Substring(dash + 1), out var n) ? n : long.MaxValue;
        }

        private static ModelSnapshot Clone(ModelSnapshot s, string name) => new ModelSnapshot
        {
            Name = name,
            SourcePath = s.SourcePath,
            RootDocument = s.RootDocument,
            SolidWorksVersion = s.SolidWorksVersion,
            Documents = s.Documents,
            Components = s.Components,
            Mates = s.Mates,
        };

        /// <summary>Bir bileşenin alt ağacını kendi başına bir montajmış gibi çıkarır (yollar bileşene göre).</summary>
        internal static ModelSnapshot SubSnapshot(ModelSnapshot s, ComponentInfo instance, string name)
        {
            var prefix = instance.Path + "/";
            var comps = s.Components.Where(c => c.Path.StartsWith(prefix, StringComparison.Ordinal)).Select(c => new ComponentInfo
            {
                Path = c.Path.Substring(prefix.Length),
                Name = c.Name,
                ParentPath = c.ParentPath == null || c.ParentPath == instance.Path ? null : c.ParentPath.Substring(prefix.Length),
                DocumentKey = c.DocumentKey,
                Configuration = c.Configuration,
                Suppressed = c.Suppressed,
                ExcludedFromBom = c.ExcludedFromBom,
                IsPatternInstance = c.IsPatternInstance,
            }).ToList();
            var docs = new HashSet<string>(comps.Select(c => c.DocumentKey), StringComparer.OrdinalIgnoreCase) { instance.DocumentKey };
            return new ModelSnapshot
            {
                Name = name,
                SourcePath = s.SourcePath,
                RootDocument = instance.DocumentKey,
                SolidWorksVersion = s.SolidWorksVersion,
                Documents = s.Documents.Where(d => docs.Contains(d.Key)).ToList(),
                Components = comps,
            };
        }

        /// <summary>Kopyalar (ve altları) çıkarılmış montaj: hat montajı ve kopyaların dışındaki her şey.</summary>
        internal static ModelSnapshot OuterSnapshot(ModelSnapshot s, IEnumerable<string> instancePaths, string name)
        {
            var paths = instancePaths.ToList();
            var comps = s.Components.Where(c => !paths.Any(p => c.Path == p || c.Path.StartsWith(p + "/", StringComparison.Ordinal))).ToList();
            var docs = new HashSet<string>(comps.Select(c => c.DocumentKey), StringComparer.OrdinalIgnoreCase) { s.RootDocument };
            return new ModelSnapshot
            {
                Name = name,
                SourcePath = s.SourcePath,
                RootDocument = s.RootDocument,
                SolidWorksVersion = s.SolidWorksVersion,
                Documents = s.Documents.Where(d => docs.Contains(d.Key)).ToList(),
                Components = comps,
                Mates = s.Mates,
            };
        }
    }

    /// <summary>
    /// Varyant dosyalarını adlarından master dosyalarına çözer: DriveWorks varsayılan adı ("X X12"), master adıyla
    /// başlayan adlar ("Frame FRAME-1-0007" → "Frame") ya da kodlu adlarda yapı imzası (özellik/ölçü adları).
    /// </summary>
    internal static class DocumentMatcher
    {
        public static Dictionary<string, string> Map(ModelSnapshot reference, ModelSnapshot variant)
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var d in variant.Documents)
            {
                var m = Match(reference, d);
                if (m != null) result[d.Key] = m;
            }
            // Bileşen olarak kullanılıp belgesi okunamamış (bastırılmış) dosyalar: sadece adla.
            foreach (var key in variant.Components.Select(c => c.DocumentKey).Where(k => !string.IsNullOrEmpty(k) && !result.ContainsKey(k)))
            {
                var m = Match(reference, new DocumentInfo { Key = key });
                if (m != null) result[key] = m;
            }
            return result;
        }

        public static string Normalize(string s) => s.Replace('İ', 'I').Replace('ı', 'i').ToLowerInvariant();

        private static string? Match(ModelSnapshot reference, DocumentInfo doc)
        {
            var exact = reference.FindDocument(doc.Key);
            if (exact != null) return exact.Key;
            // Bastırılmış bileşenlerin belgeleri okunmamış olabilir; bileşenlerin kullandığı dosya adı da birebir eşleşmedir
            // ("Shelf Peg1" okunmadı diye "Shelf Peg" ile eşlenmesin).
            var referenced = reference.Components.Select(c => c.DocumentKey)
                .FirstOrDefault(k => string.Equals(k, doc.Key, StringComparison.OrdinalIgnoreCase));
            if (referenced != null) return referenced;

            var decoded = DriveWorksNaming.DecodeFile(doc.Key);
            if (decoded != null && reference.FindDocument(decoded) is DocumentInfo dw) return dw.Key;

            // En uzun master adı öneki: "Roller Assembly ROLLER ASSEMBLY-1-0007" → "Roller Assembly" ("Roller" değil).
            var ext = Path.GetExtension(doc.Key);
            var stem = Normalize(Path.GetFileNameWithoutExtension(doc.Key));
            DocumentInfo? best = null;
            int bestLength = 0;
            foreach (var r in reference.Documents)
            {
                if (!string.Equals(Path.GetExtension(r.Key), ext, StringComparison.OrdinalIgnoreCase)) continue;
                var rs = Normalize(Path.GetFileNameWithoutExtension(r.Key));
                if (rs.Length == 0 || rs.Length > stem.Length || !stem.StartsWith(rs, StringComparison.Ordinal)) continue;
                if (rs.Length < stem.Length && char.IsLetter(stem[rs.Length])) continue; // "Frame" ≠ "Framework"
                if (rs.Length > bestLength && SameStructure(r, doc) != false)
                {
                    best = r;
                    bestLength = rs.Length;
                }
            }
            if (best != null) return best.Key;

            // Kodlu adlar: yapı imzası açıkça tek bir master dosyasına uyuyorsa.
            if (doc.Features.Count + doc.Dimensions.Count == 0) return null;
            var scored = reference.Documents.Where(r => r.Kind == doc.Kind && r.Features.Count + r.Dimensions.Count > 0)
                .Select(r => (r, score: Jaccard(Signature(r), Signature(doc)))).OrderByDescending(x => x.score).ToList();
            if (scored.Count == 0 || scored[0].score < 0.8) return null;
            if (scored.Count > 1 && scored[1].score > scored[0].score - 0.05) return null;
            return scored[0].r.Key;
        }

        /// <summary>İkisinin de yapısı biliniyorsa benzer mi? Bilinmiyorsa null.</summary>
        private static bool? SameStructure(DocumentInfo a, DocumentInfo b)
        {
            if (a.Features.Count + a.Dimensions.Count == 0 || b.Features.Count + b.Dimensions.Count == 0) return null;
            return a.Kind == b.Kind && Jaccard(Signature(a), Signature(b)) >= 0.5;
        }

        internal static HashSet<string> Signature(DocumentInfo doc)
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var f in doc.Features) set.Add("f:" + f.Name + ":" + f.Type);
            foreach (var d in doc.Dimensions) set.Add("d:" + d.Name);
            foreach (var p in doc.CustomProperties.Keys) set.Add("p:" + p);
            foreach (var e in doc.Equations.Where(e => e.IsGlobalVariable)) set.Add("g:" + e.Name);
            return set;
        }

        internal static double Jaccard(HashSet<string> a, HashSet<string> b)
        {
            if (a.Count == 0 && b.Count == 0) return 0.5;
            int inter = a.Count(b.Contains);
            return (double)inter / (a.Count + b.Count - inter);
        }
    }
}
