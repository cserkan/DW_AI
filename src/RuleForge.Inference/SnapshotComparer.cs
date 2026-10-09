using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using RuleForge.Core.Expressions;
using RuleForge.Core.Model;
using RuleForge.Core.Rules;

namespace RuleForge.Inference
{
    public sealed class SnapshotDifference
    {
        public string Key { get; set; } = string.Empty;
        public string Label { get; set; } = string.Empty;
        public TargetKind Kind { get; set; }

        /// <summary>Üretilen modeldeki değer; yoksa null.</summary>
        public string? Actual { get; set; }

        /// <summary>Beklenen (ör. DriveWorks'ün ürettiği) modeldeki değer; yoksa null.</summary>
        public string? Expected { get; set; }
    }

    public sealed class ComparisonReport
    {
        public int Same { get; set; }
        public List<SnapshotDifference> Differences { get; set; } = new List<SnapshotDifference>();

        /// <summary>Sadece bir modelde bulunan dosyalar (ör. değiştirilmiş parça): tüm değerleri tek satırda özetlenir.</summary>
        public List<string> OnlyInActual { get; set; } = new List<string>();

        public List<string> OnlyInExpected { get; set; } = new List<string>();
        public List<string> Notes { get; set; } = new List<string>();

        public int DifferenceCount => Differences.Count + OnlyInActual.Count + OnlyInExpected.Count;

        public string ToText(int maxPerKind = 40)
        {
            var sb = new StringBuilder();
            int total = Same + DifferenceCount;
            sb.AppendLine($"KARŞILAŞTIRMA: {Same}/{total} değer aynı, {DifferenceCount} fark.");
            if (OnlyInActual.Count + OnlyInExpected.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("SADECE BİR MODELDE OLAN DOSYALAR");
                foreach (var d in OnlyInActual) sb.AppendLine($"  {d}: sadece üretilen modelde");
                foreach (var d in OnlyInExpected) sb.AppendLine($"  {d}: sadece beklenen modelde");
            }
            foreach (var group in Differences.GroupBy(d => d.Kind).OrderBy(g => g.Key))
            {
                sb.AppendLine();
                sb.AppendLine($"{KindName(group.Key)} ({group.Count()})");
                foreach (var d in group.Take(maxPerKind))
                    sb.AppendLine($"  {d.Label}: üretilen {d.Actual ?? "(yok)"}, beklenen {d.Expected ?? "(yok)"}");
                if (group.Count() > maxPerKind) sb.AppendLine($"  … (+{group.Count() - maxPerKind})");
            }
            foreach (var n in Notes) sb.AppendLine("Not: " + n);
            return sb.ToString();
        }

        private static string KindName(TargetKind kind)
        {
            switch (kind)
            {
                case TargetKind.Dimension: return "ÖLÇÜLER";
                case TargetKind.GlobalVariable: return "GLOBAL DEĞİŞKENLER";
                case TargetKind.FeatureSuppression: return "ÖZELLİK BASTIRMA";
                case TargetKind.ComponentSuppression: return "BİLEŞEN BASTIRMA / SİLME";
                case TargetKind.ComponentReplace: return "KULLANILAN DOSYALAR (parça değişimi)";
                case TargetKind.Configuration: return "KONFİGÜRASYONLAR";
                case TargetKind.CustomProperty: return "ÖZEL ÖZELLİKLER";
                default: return kind.ToString().ToUpperInvariant();
            }
        }
    }

    /// <summary>
    /// İki modeli (ör. RuleForge'un ürettiği ile DriveWorks'ün ürettiği) karşılaştırır. Dosya adları farklı olabilir:
    /// parçalar çıkarımdaki gibi yapılarına ve adlarından çözülen master adlarına göre eşleştirilir.
    /// Silinmiş bileşen ile bastırılmış bileşen aynı sayılır.
    /// </summary>
    public static class SnapshotComparer
    {
        private const string ActualName = "uretilen";
        private const string ExpectedName = "beklenen";

        public static ComparisonReport Compare(ModelSnapshot actual, ModelSnapshot expected, InferenceOptions? options = null)
        {
            options = options ?? new InferenceOptions();
            var report = new ComparisonReport();
            var samples = new List<VariantSample> { new VariantSample(ActualName, actual), new VariantSample(ExpectedName, expected) };

            var extractor = new ObservationExtractor(options);
            var known = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase)
            {
                [ExpectedName] = DocumentMatcher.Map(actual, expected),
            };
            var unmatched = extractor.UseStructure(actual, samples, known);
            if (unmatched > 0) report.Notes.Add($"{unmatched} bileşen iki model arasında eşleştirilemedi; farklar listesinde 'yok' olarak görünür.");
            if (extractor.SkippedCalculated.Count > 0)
                report.Notes.Add($"SolidWorks'ün hesapladığı özellikler karşılaştırılmadı: {string.Join(", ", extractor.SkippedCalculated)}.");

            var observations = extractor.Extract(samples);
            var byKey = observations.ToDictionary(o => o.Key, StringComparer.OrdinalIgnoreCase);

            // Bir tarafta bastırılmış, diğer tarafta silinmiş bileşenleri olan montaj belgeleri: DriveWorks bileşeni silince
            // ona bağlı ilişkileri ve eğrileri de siler; bastırılmış tarafta bunlar durur ama etkisizdir.
            var deletedIn = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase) // taraf → montaj belgeleri
            {
                [ActualName] = new HashSet<string>(StringComparer.OrdinalIgnoreCase),
                [ExpectedName] = new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            };
            foreach (var comp in observations.Where(o => o.Target.Kind == TargetKind.ComponentSuppression))
            {
                var path = comp.Target.Component ?? string.Empty;
                var slash = path.LastIndexOf('/');
                foreach (var side in new[] { ActualName, ExpectedName })
                {
                    var other = side == ActualName ? ExpectedName : ActualName;
                    // Bu tarafta var ve bastırılmış, diğer tarafta hiç yok (dosya bilgisi yok) → silinmiş.
                    bool here = byKey.TryGetValue("file:" + path, out var file) && file.Values.ContainsKey(side);
                    bool there = file != null && file.Values.ContainsKey(other);
                    if (!here || there || !comp.Values.TryGetValue(side, out var sup) || !sup.AsBool()) continue;
                    var parentDoc = slash < 0 ? actual.RootDocument
                        : byKey.TryGetValue("file:" + path.Substring(0, slash), out var parent) && parent.Values.TryGetValue(side, out var pd) ? pd.AsText() : null;
                    if (parentDoc != null) deletedIn[side].Add(parentDoc);
                }
            }
            // Bir dosyanın hiçbir değeri karşı modelde yoksa dosya orada yok demektir (ör. değiştirilmiş parça): tek satır.
            var oneSided = observations.Where(o => o.Target.Document != null && o.Values.Count == 1)
                .GroupBy(o => o.Target.Document!, StringComparer.OrdinalIgnoreCase)
                .Where(g => observations.Where(o => string.Equals(o.Target.Document, g.Key, StringComparison.OrdinalIgnoreCase)).All(o => o.Values.Count == 1) &&
                            g.Select(o => o.Values.Keys.First()).Distinct(StringComparer.OrdinalIgnoreCase).Count() == 1)
                .ToDictionary(g => g.Key, g => g.First().Values.Keys.First(), StringComparer.OrdinalIgnoreCase);
            foreach (var kv in oneSided.OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase))
                (kv.Value == ActualName ? report.OnlyInActual : report.OnlyInExpected).Add(kv.Key);

            foreach (var o in observations)
            {
                if (o.Target.Document != null && oneSided.ContainsKey(o.Target.Document)) continue;
                o.Values.TryGetValue(ActualName, out var a);
                o.Values.TryGetValue(ExpectedName, out var e);
                bool hasA = o.Values.ContainsKey(ActualName), hasE = o.Values.ContainsKey(ExpectedName);
                if (hasA && hasE && Same(a, e))
                {
                    report.Same++;
                    continue;
                }
                // Bir tarafta bastırılmış, diğer tarafta silinmiş bileşen: hangi dosyayı kullandığı önemli değil.
                if (hasA != hasE && o.Target.Kind == TargetKind.ComponentReplace &&
                    byKey.TryGetValue("comp:" + o.Target.Component, out var comp) &&
                    comp.Values.TryGetValue(hasA ? ActualName : ExpectedName, out var suppressed) && suppressed.AsBool())
                {
                    report.Same++;
                    continue;
                }
                // Silinmiş bileşenlerin montajında sadece bastırılmış tarafta kalan ilişki/eğri ve ölçüleri.
                bool inA = hasA && !extractor.DeletedFeatureValues.Contains(o.Key + "\u0001" + ActualName);
                bool inE = hasE && !extractor.DeletedFeatureValues.Contains(o.Key + "\u0001" + ExpectedName);
                if (inA != inE && (o.Target.Kind == TargetKind.FeatureSuppression || o.Target.Kind == TargetKind.Dimension) &&
                    o.Target.Document != null && deletedIn[inA ? ActualName : ExpectedName].Contains(o.Target.Document))
                {
                    report.Same++;
                    continue;
                }
                // Silinmiş özellik (ör. silinen bileşenin montaj ilişkisi) bastırılmış özellikle aynı sonucu verir;
                // bastırılmış özelliğin ölçüsü de karşı tarafta hiç olmayabilir.
                if (hasA != hasE && (o.Target.Kind == TargetKind.FeatureSuppression ? (hasA ? a : e).AsBool()
                        : o.Target.Kind == TargetKind.Dimension && FeatureSuppressed(byKey, o, hasA ? ActualName : ExpectedName)))
                {
                    report.Same++;
                    continue;
                }
                report.Differences.Add(new SnapshotDifference
                {
                    Key = o.Key,
                    Label = o.Label,
                    Kind = o.Target.Kind,
                    Actual = hasA ? a.AsText() : null,
                    Expected = hasE ? e.AsText() : null,
                });
            }
            report.Differences = report.Differences.OrderBy(d => d.Kind).ThenBy(d => d.Label, StringComparer.OrdinalIgnoreCase).ToList();
            if (report.DifferenceCount == 0)
                report.Notes.Add("İki model karşılaştırılan tüm değerlerde aynı.");
            return report;
        }

        /// <summary>"D1@Distance4" ölçüsünün özelliği (Distance4) o modelde bastırılmış mı?</summary>
        private static bool FeatureSuppressed(Dictionary<string, Observation> byKey, Observation dim, string side)
        {
            var name = dim.Target.Name ?? string.Empty;
            var at = name.IndexOf('@');
            if (at < 0) return false;
            return byKey.TryGetValue($"feat:{dim.Target.Document}:{name.Substring(at + 1)}", out var feature) &&
                   feature.Values.TryGetValue(side, out var v) && v.AsBool();
        }

        private static bool Same(Value a, Value b)
        {
            if (a.Kind == ValueKind.Number && b.Kind == ValueKind.Number)
                return Math.Abs(a.AsNumber() - b.AsNumber()) <= 1e-4 * Math.Max(1, Math.Abs(b.AsNumber()));
            return Value.LooseEquals(a, b);
        }
    }
}
