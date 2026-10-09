using System;
using System.Collections.Generic;
using System.Linq;
using RuleForge.Core.Expressions;
using RuleForge.Core.Rules;

namespace RuleForge.Inference
{
    /// <summary>Kör testte bulunan, diğer değerleri süren (tahmini) girdi.</summary>
    public sealed class DetectedDriver
    {
        /// <summary>Formüllerde kullanılacak ad, ör. "Govde_D1_Boss".</summary>
        public string Name { get; set; } = string.Empty;

        public string ObservationKey { get; set; } = string.Empty;
        public string Label { get; set; } = string.Empty;

        /// <summary>Bu girdiyle açıklanan diğer değer sayısı.</summary>
        public int Explains { get; set; }

        /// <summary>Tüm varyantlarda birebir aynı değeri taşıyan diğer gözlemler (hangisi "asıl" girdi, veriden ayırt edilemez).</summary>
        public List<string> Equivalents { get; set; } = new List<string>();
    }

    /// <summary>
    /// Girdi tablosu olmadan (kör test) DriveWorks girdilerini tahmin eder: her değişen değeri aday girdi
    /// sayar ve diğer değerlerin en çoğunu açıklayan adayları açgözlü (greedy) biçimde seçer.
    /// </summary>
    internal static class DriverDetector
    {
        private const int MaxDrivers = 10;

        public static List<DetectedDriver> Detect(List<Observation> observations, IReadOnlyList<VariantSample> samples,
            double tolerance)
        {
            var names = samples.Select(s => s.Name).ToList();

            // Tüm varyantlarda bulunan ve değişen gözlemler; aynı değer dizisine sahip olanlar tek grupta.
            var groups = observations
                .Where(o => o.Values.Count == samples.Count && o.Values.Values.Distinct().Count() > 1)
                .GroupBy(o => string.Join("\u0001", names.Select(n => o.Values[n].Kind + ":" + o.Values[n].AsText())))
                .Select(g => g.OrderBy(Priority).ThenBy(o => o.Key, StringComparer.OrdinalIgnoreCase).ToList())
                .ToList();

            var unexplained = new HashSet<int>(Enumerable.Range(0, groups.Count));
            var chosen = new List<(int group, InputColumn column)>();

            while (chosen.Count < MaxDrivers && unexplained.Count > 1)
            {
                (int group, InputColumn column, List<int> explained)? best = null;
                foreach (var g in unexplained)
                {
                    var rep = groups[g][0];
                    var column = ToColumn(DriverName(rep), rep);
                    if (column == null) continue;

                    var finder = new RelationFinder(chosen.Select(c => c.column).Concat(new[] { column }).ToList(), tolerance);
                    var explained = unexplained
                        .Where(o => o != g && finder.Find(groups[o][0], groups[o][0].Target.ExpectedType, fast: true)
                            .Any(c => c.Confidence >= 0.7 && c.Expression.IndexOf(column.Name, StringComparison.OrdinalIgnoreCase) >= 0))
                        .ToList();

                    if (best == null || Better(explained.Count, rep, best.Value.explained.Count, groups[best.Value.group][0]))
                        best = (g, column, explained);
                }

                // Hiçbir şeyi açıklamayan değer girdi değildir; bağımsız/gürültü olarak açıklanamayanlarda kalır.
                if (best == null || best.Value.explained.Count == 0) break;

                chosen.Add((best.Value.group, best.Value.column));
                unexplained.Remove(best.Value.group);
                foreach (var e in best.Value.explained) unexplained.Remove(e);
            }

            // Hiçbir şeyi açıklamayan ama az seçenekli (evet/hayır, birkaç seçenek) değerler büyük ihtimalle
            // tek etkili bir seçim girdisidir (ör. "motor sağda mı"). Sayısal olanlar gürültü olabilir; onlara dokunma.
            foreach (var g in unexplained.OrderBy(g => g).ToList())
            {
                if (chosen.Count >= MaxDrivers) break;
                var rep = groups[g][0];
                if (rep.Values.Values.All(v => v.Kind == ValueKind.Number)) continue;
                var column = ToColumn(DriverName(rep), rep);
                if (column != null) chosen.Add((g, column));
            }

            return chosen.Select(c =>
            {
                var group = groups[c.group];
                var finder = new RelationFinder(new List<InputColumn> { c.column }, tolerance);
                return new DetectedDriver
                {
                    Name = c.column.Name,
                    ObservationKey = group[0].Key,
                    Label = group[0].Label,
                    Explains = groups.Count(g => g != group &&
                                                 finder.Find(g[0], g[0].Target.ExpectedType, fast: true).Any(x => x.Confidence >= 0.7)),
                    Equivalents = group.Skip(1).Select(o => o.Label).ToList(),
                };
            }).ToList();
        }

        /// <summary>Daha çok açıklayan kazanır; eşitlikte "yuvarlak" değerli olan (DriveWorks girdileri genelde yuvarlaktır).</summary>
        private static bool Better(int count, Observation candidate, int bestCount, Observation best)
        {
            if (count != bestCount) return count > bestCount;
            var r1 = Roundness(candidate);
            var r2 = Roundness(best);
            if (Math.Abs(r1 - r2) > 1e-9) return r1 > r2;
            return Priority(candidate) < Priority(best);
        }

        private static double Roundness(Observation o)
        {
            var nums = o.Values.Values.Where(v => v.Kind == ValueKind.Number).Select(v => v.AsNumber()).ToList();
            if (nums.Count == 0) return 0;
            double score = 0;
            foreach (var d in nums)
            {
                if (Math.Abs(d % 100) < 1e-6) score += 3;
                else if (Math.Abs(d % 10) < 1e-6) score += 2;
                else if (Math.Abs(d % 1) < 1e-6) score += 1;
            }
            return score / nums.Count;
        }

        /// <summary>Küçük = girdi olmaya daha yatkın.</summary>
        private static int Priority(Observation o)
        {
            switch (o.Target.Kind)
            {
                case TargetKind.GlobalVariable: return 0;
                case TargetKind.CustomProperty: return 1;
                case TargetKind.Dimension: return 2;
                case TargetKind.Configuration: return 3;
                case TargetKind.ComponentSuppression: return 4;
                default: return 5;
            }
        }

        private static InputColumn? ToColumn(string name, Observation o)
        {
            var values = new Dictionary<string, Value>(o.Values, StringComparer.OrdinalIgnoreCase);
            if (values.Values.All(v => v.Kind == ValueKind.Number))
            {
                // DriveWorks'e girilen ölçüler pratikte tam sayıdır; 21.62 gibi değerler (ağırlık, kütle) hesaplanmış sonuçtur.
                int integers = values.Values.Count(v => Math.Abs(v.AsNumber() - Math.Round(v.AsNumber())) < 1e-6);
                if (integers < 0.8 * values.Count) return null;
                return new InputColumn(name, ColumnKind.Number, values);
            }
            if (values.Values.All(v => v.Kind == ValueKind.Bool)) return new InputColumn(name, ColumnKind.Bool, values);
            // Neredeyse her varyantta farklı olan metin (ör. açıklama, parça no) seçim girdisi değil, sonuçtur.
            var distinct = values.Values.Select(v => v.AsText()).Distinct(StringComparer.OrdinalIgnoreCase).Count();
            if (distinct <= Math.Min(12, values.Count / 2))
                return new InputColumn(name, ColumnKind.Category, values);
            return null;
        }

        public static string DriverName(Observation o)
        {
            var t = o.Target;
            string raw;
            switch (t.Kind)
            {
                case TargetKind.GlobalVariable:
                case TargetKind.CustomProperty:
                    raw = t.Name!;
                    break;
                case TargetKind.Dimension:
                case TargetKind.FeatureSuppression:
                    raw = System.IO.Path.GetFileNameWithoutExtension(t.Document) + "_" + t.Name;
                    break;
                case TargetKind.ComponentSuppression:
                    raw = t.Component + "_Bastirilmis";
                    break;
                case TargetKind.Configuration:
                    raw = (t.Component ?? t.Document) + "_Konfig";
                    break;
                default:
                    raw = o.Key;
                    break;
            }
            return RuleInferencer.SanitizeName(raw);
        }
    }
}
