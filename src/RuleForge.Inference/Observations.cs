using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using RuleForge.Core.Expressions;
using RuleForge.Core.Model;
using RuleForge.Core.Rules;

namespace RuleForge.Inference
{
    /// <summary>Varyantlar arasında karşılaştırılabilen tek bir model değeri (ölçü, bastırma durumu, özellik...).</summary>
    public sealed class Observation
    {
        public Observation(string key, RuleTarget target, string label)
        {
            Key = key;
            Target = target;
            Label = label;
        }

        /// <summary>Kararlı anahtar, ör. "dim:Govde.SLDPRT:D1@Sketch1".</summary>
        public string Key { get; }

        public RuleTarget Target { get; }
        public string Label { get; }

        /// <summary>Varyant adı → değer. Varyantta yoksa anahtar bulunmaz.</summary>
        public Dictionary<string, Value> Values { get; } = new Dictionary<string, Value>(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Snapshot'lardan gözlem tablosu oluşturur ve dosya adlarını varyantlar arasında eşleştirir.</summary>
    public sealed class ObservationExtractor
    {
        private readonly Regex? _namePattern;
        private readonly InferenceOptions _options;

        public ObservationExtractor(InferenceOptions options)
        {
            _options = options;
            _calculated = new Regex(options.CalculatedPropertyPattern ?? "^$", RegexOptions.IgnoreCase);
            if (!string.IsNullOrWhiteSpace(options.NamePattern))
                _namePattern = new Regex(options.NamePattern!, RegexOptions.IgnoreCase);
        }

        /// <summary>"Govde_SP0012.SLDPRT" → "Govde.SLDPRT" (NamePattern varsa).</summary>
        public string NormalizeDocumentKey(string key)
        {
            if (_namePattern == null) return DriveWorksNaming.DecodeFile(key) ?? key;
            var ext = Path.GetExtension(key);
            var stem = Path.GetFileNameWithoutExtension(key);
            var m = _namePattern.Match(stem);
            return m.Success && m.Groups.Count > 1 ? m.Groups[1].Value + ext : key;
        }

        /// <summary>"Alt_SP12-1/Ayak_SP12-3" → "Alt-1/Ayak-3".</summary>
        public string NormalizeComponentPath(string path)
        {
            if (_namePattern == null)
                return string.Join("/", path.Split('/').Select(seg => DriveWorksNaming.DecodeComponentName(seg) ?? seg));
            var parts = path.Split('/');
            for (int i = 0; i < parts.Length; i++)
            {
                var p = parts[i];
                var dash = p.LastIndexOf('-');
                var stem = dash > 0 ? p.Substring(0, dash) : p;
                var suffix = dash > 0 ? p.Substring(dash) : string.Empty;
                var m = _namePattern.Match(stem);
                parts[i] = (m.Success && m.Groups.Count > 1 ? m.Groups[1].Value : stem) + suffix;
            }
            return string.Join("/", parts);
        }

        private Dictionary<string, NameMap>? _maps;
        private readonly Regex _calculated;

        /// <summary>SolidWorks'ün hesapladığı için atlanan özellik adları (ör. Weight).</summary>
        public HashSet<string> SkippedCalculated { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>Yapısal eşlemeyi aç. Dönüş: referansla eşlenemeyen bileşen sayısı (tüm varyantlarda toplam).</summary>
        public int UseStructure(ModelSnapshot reference, IReadOnlyList<VariantSample> samples)
        {
            _maps = new Dictionary<string, NameMap>(StringComparer.OrdinalIgnoreCase);
            int unmatched = 0;
            foreach (var s in samples)
            {
                var map = StructureMatcher.Map(reference, s.Snapshot);
                _maps[s.Name] = map;
                unmatched += s.Snapshot.Components.Count(c => !c.IsPatternInstance && !map.Components.ContainsKey(c.Path));
            }
            return unmatched;
        }

        private string DocKey(VariantSample sample, string key)
        {
            if (_maps != null && _maps[sample.Name].Documents.TryGetValue(key, out var m)) key = m;
            return NormalizeDocumentKey(key);
        }

        private string ComponentPath(VariantSample sample, string path)
        {
            if (_maps != null && _maps[sample.Name].Components.TryGetValue(path, out var m)) path = m;
            return NormalizeComponentPath(path);
        }

        public List<Observation> Extract(IReadOnlyList<VariantSample> samples)
        {
            var map = new Dictionary<string, Observation>(StringComparer.OrdinalIgnoreCase);

            Observation Get(string key, Func<RuleTarget> target, string label)
            {
                if (!map.TryGetValue(key, out var o))
                {
                    o = new Observation(key, target(), label);
                    map[key] = o;
                }
                return o;
            }

            foreach (var sample in samples)
            {
                var snap = sample.Snapshot;
                var rootKey = DocKey(sample, snap.RootDocument);
                foreach (var doc in snap.Documents)
                {
                    var docKey = DocKey(sample, doc.Key);
                    foreach (var dim in doc.Dimensions)
                    {
                        // Referans ölçüler ve denklemle sürülen ölçüler kural hedefi olamaz.
                        if (dim.IsDriven || dim.DrivenByEquation != null) continue;
                        Get($"dim:{docKey}:{dim.Name}",
                                () => new RuleTarget { Kind = TargetKind.Dimension, Document = docKey, Name = dim.Name },
                                $"{docKey} › {dim.Name}")
                            .Values[sample.Name] = Value.Number(dim.Value);
                    }

                    foreach (var eq in doc.Equations.Where(e => e.IsGlobalVariable && e.Value.HasValue))
                    {
                        Get($"gv:{docKey}:{eq.Name}",
                                () => new RuleTarget { Kind = TargetKind.GlobalVariable, Document = docKey, Name = eq.Name },
                                $"{docKey} › \"{eq.Name}\"")
                            .Values[sample.Name] = Value.Number(eq.Value!.Value);
                    }

                    if (_options.IncludeFeatureSuppression)
                    {
                        foreach (var f in doc.Features)
                        {
                            Get($"feat:{docKey}:{f.Name}",
                                    () => new RuleTarget { Kind = TargetKind.FeatureSuppression, Document = docKey, Name = f.Name },
                                    $"{docKey} › {f.Name} (bastırılmış mı)")
                                .Values[sample.Name] = Value.Bool(f.Suppressed);
                        }
                    }

                    if (_options.IncludeCustomProperties)
                    {
                        foreach (var kv in doc.CustomProperties)
                        {
                            if (_calculated.IsMatch(kv.Key.Trim()))
                            {
                                SkippedCalculated.Add(kv.Key);
                                continue;
                            }
                            Get($"prop:{docKey}:{kv.Key}",
                                    () => new RuleTarget
                                    {
                                        Kind = TargetKind.CustomProperty,
                                        Document = string.Equals(docKey, rootKey, StringComparison.OrdinalIgnoreCase) ? null : docKey,
                                        Name = kv.Key,
                                    },
                                    $"{docKey} › özellik '{kv.Key}'")
                                .Values[sample.Name] = Value.Parse(kv.Value);
                        }
                    }
                }

                // Desen örneklerinin sayısı desen ölçüsünden gelir; tek tek örnekler kural hedefi değildir.
                foreach (var comp in snap.Components.Where(c => !c.IsPatternInstance))
                {
                    var path = ComponentPath(sample, comp.Path);

                    // Bu yerde hangi dosya kullanılıyor? Değişiyorsa "parça değiştir" kuralı olur (ör. malzemeye göre kulp).
                    if (!string.IsNullOrEmpty(comp.DocumentKey))
                    {
                        Get($"file:{path}",
                                () => new RuleTarget { Kind = TargetKind.ComponentReplace, Component = path },
                                $"{path} (kullanılan dosya)")
                            .Values[sample.Name] = Value.Text(DocKey(sample, comp.DocumentKey));
                    }

                    Get($"comp:{path}",
                            () => new RuleTarget { Kind = TargetKind.ComponentSuppression, Component = path },
                            $"{path} (bastırılmış mı)")
                        .Values[sample.Name] = Value.Bool(comp.Suppressed);

                    if (!comp.Suppressed && !string.IsNullOrEmpty(comp.Configuration))
                    {
                        Get($"cfg:{path}",
                                () => new RuleTarget { Kind = TargetKind.Configuration, Component = path },
                                $"{path} (konfigürasyon)")
                            .Values[sample.Name] = Value.Text(comp.Configuration);
                    }
                }
            }

            // Bazı varyantlarda hiç olmayan bileşen = silinmiş/bastırılmış kabul edilir.
            foreach (var o in map.Values.Where(o => o.Target.Kind == TargetKind.ComponentSuppression))
                foreach (var s in samples)
                    if (!o.Values.ContainsKey(s.Name))
                        o.Values[s.Name] = Value.Bool(true);

            return map.Values.OrderBy(o => o.Key, StringComparer.OrdinalIgnoreCase).ToList();
        }
    }
}
