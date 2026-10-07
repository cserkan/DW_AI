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
            if (!string.IsNullOrWhiteSpace(options.NamePattern))
                _namePattern = new Regex(options.NamePattern!, RegexOptions.IgnoreCase);
        }

        /// <summary>"Govde_SP0012.SLDPRT" → "Govde.SLDPRT" (NamePattern varsa).</summary>
        public string NormalizeDocumentKey(string key)
        {
            if (_namePattern == null) return key;
            var ext = Path.GetExtension(key);
            var stem = Path.GetFileNameWithoutExtension(key);
            var m = _namePattern.Match(stem);
            return m.Success && m.Groups.Count > 1 ? m.Groups[1].Value + ext : key;
        }

        /// <summary>"Alt_SP12-1/Ayak_SP12-3" → "Alt-1/Ayak-3".</summary>
        public string NormalizeComponentPath(string path)
        {
            if (_namePattern == null) return path;
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
                var rootKey = NormalizeDocumentKey(snap.RootDocument);
                foreach (var doc in snap.Documents)
                {
                    var docKey = NormalizeDocumentKey(doc.Key);
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

                foreach (var comp in snap.Components)
                {
                    var path = NormalizeComponentPath(comp.Path);
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
