using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using RuleForge.Core.Expressions;
using RuleForge.Core.Rules;

namespace RuleForge.Inference
{
    /// <summary>Çıkarımın belirsiz kaldığı bir nokta ve onu çözecek girdi değerleri.</summary>
    public sealed class ProbeNeed
    {
        /// <summary>Küçük = önce. 1 eşik, 2 sabit/ofset, 3 az veri, 4 tek örnekli seçenek, 5 kapsam boşluğu.</summary>
        public int Priority { get; set; }

        public string Input { get; set; } = string.Empty;

        /// <summary>Denenmesi önerilen değerler (her biri ayrı bir varyantta). Boşsa elle yapılacak bir ihtiyaçtır.</summary>
        public List<Value> Values { get; set; } = new List<Value>();

        /// <summary>Birden çok girdinin AYNI varyantta birlikte ayarlanması gereken durumlar (ör. iki girdiyi ayırt etmek).</summary>
        public List<Dictionary<string, Value>> Combos { get; set; } = new List<Dictionary<string, Value>>();

        /// <summary>Her biri tek bir varyanta yerleştirilecek girdi atamaları.</summary>
        public IEnumerable<Dictionary<string, Value>> Assignments() => Combos.Count > 0
            ? Combos
            : Values.Select(v => new Dictionary<string, Value>(StringComparer.OrdinalIgnoreCase) { [Input] = v });

        public string Reason { get; set; } = string.Empty;

        /// <summary>Değer verilemeyen (ör. iki girdinin ayrıştırılması) ihtiyaçlar kullanıcıya metin olarak iletilir.</summary>
        public bool IsManual => Values.Count == 0 && Combos.Count == 0;
    }

    /// <summary>DriveWorks'te üretilmesi önerilen tam bir girdi kombinasyonu.</summary>
    public sealed class SuggestedVariant
    {
        public string Name { get; set; } = string.Empty;
        public Dictionary<string, Value> Inputs { get; set; } = new Dictionary<string, Value>(StringComparer.OrdinalIgnoreCase);

        /// <summary>Bu varyantın neden önerildiği (bir değer birden çok sorunu aynı anda çözebilir).</summary>
        public List<string> Reasons { get; set; } = new List<string>();
    }

    /// <summary>
    /// Bir sonraki varyant setini planlar: belirsizlikleri çözecek değerleri tek tek seçer, farklı girdilere ait
    /// ihtiyaçları aynı varyantta birleştirir, kalan girdileri düşük uyumsuzluklu dizilerle (Halton) doldurur ki
    /// girdiler birbirinden bağımsız değişsin. Her ürün için aynı şekilde çalışır.
    /// </summary>
    internal static class VariantPlanner
    {
        private const int MaxVariants = 12;
        private static readonly int[] Primes = { 2, 3, 5, 7, 11, 13, 17, 19, 23, 29, 31 };

        public static List<SuggestedVariant> Plan(IReadOnlyList<InputDefinition> inputs, List<InputColumn> columns,
            List<ProbeNeed> needs, List<string> notes)
        {
            AddCoverageNeeds(columns, needs);
            var manual = needs.Where(n => n.IsManual).ToList();
            var probes = needs.Where(n => !n.IsManual).OrderBy(n => n.Priority).ToList();
            if (probes.Count == 0) return new List<SuggestedVariant>();

            var variants = new List<SuggestedVariant>();
            foreach (var need in probes)
            {
                foreach (var assignment in need.Assignments())
                {
                    // Uyumlu ilk varyant: atamadaki hiçbir girdi orada farklı bir değerle dolu olmamalı.
                    var target = variants.FirstOrDefault(v => assignment.All(kv => !v.Inputs.ContainsKey(kv.Key)));
                    if (target == null)
                    {
                        if (variants.Count >= MaxVariants)
                        {
                            notes.Add($"Öneri sayısı {MaxVariants} ile sınırlandı; öncelik sırasına göre kalanlar atlandı: {need.Reason}");
                            break;
                        }
                        target = new SuggestedVariant { Name = $"ONERI{variants.Count + 1:00}" };
                        variants.Add(target);
                    }
                    foreach (var kv in assignment) target.Inputs[kv.Key] = kv.Value;
                    var reason = $"{string.Join(", ", assignment.Select(kv => $"{kv.Key} = {kv.Value.AsText()}"))}: {need.Reason}";
                    if (!target.Reasons.Contains(reason)) target.Reasons.Add(reason);
                }
            }

            // Kalan girdileri doldur: sayısal → Halton dizisi (her girdi farklı asal taban), seçimli → döngü.
            for (int vi = 0; vi < variants.Count; vi++)
            {
                for (int ci = 0; ci < columns.Count; ci++)
                {
                    var column = columns[ci];
                    if (variants[vi].Inputs.ContainsKey(column.Name)) continue;
                    variants[vi].Inputs[column.Name] = Fill(column, ci, vi);
                }
            }
            return variants;
        }

        private static Value Fill(InputColumn column, int columnIndex, int variantIndex)
        {
            var values = column.Values.Values.ToList();
            if (column.Kind == ColumnKind.Number)
            {
                var nums = values.Select(v => v.AsNumber()).ToList();
                double min = nums.Min(), max = nums.Max();
                if (max - min < 1e-9) return Value.Number(min);
                double h = Halton(variantIndex + 1 + columnIndex, Primes[columnIndex % Primes.Length]);
                return Value.Number(Nice(min + (max - min) * h, nums));
            }
            var options = values.Select(v => v.AsText()).Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(o => o, StringComparer.OrdinalIgnoreCase).ToList();
            var pick = options[(variantIndex + columnIndex) % options.Count];
            return values.First(v => string.Equals(v.AsText(), pick, StringComparison.OrdinalIgnoreCase));
        }

        private static double Halton(int index, int radix)
        {
            double f = 1, r = 0;
            for (int i = index; i > 0; i /= radix)
            {
                f /= radix;
                r += f * (i % radix);
            }
            return r;
        }

        /// <summary>İnsanların girdiği türden yuvarlak sayı: eğitim değerleri tam sayıysa tam sayıya, değilse 0,5'e yuvarlar.</summary>
        public static double Nice(double v, IEnumerable<double> training)
        {
            var list = training.ToList();
            double span = list.Max() - list.Min();
            bool integers = list.All(d => Math.Abs(d - Math.Round(d)) < 1e-9);
            double step = !integers ? 0.5 : span >= 500 ? 10 : span >= 50 ? 5 : 1;
            return Math.Round(v / step, MidpointRounding.AwayFromZero) * step;
        }

        // ---------- kapsam ihtiyaçları (belirli bir kuraldan bağımsız, her girdi için) ----------
        private static void AddCoverageNeeds(List<InputColumn> columns, List<ProbeNeed> needs)
        {
            foreach (var column in columns)
            {
                if (column.Kind == ColumnKind.Number)
                {
                    var xs = column.Values.Values.Select(v => v.AsNumber()).Distinct().OrderBy(d => d).ToList();
                    if (xs.Count < 2) continue;
                    double span = xs.Last() - xs.First();
                    double bestGap = 0;
                    int gapIndex = 0;
                    for (int i = 1; i < xs.Count; i++)
                        if (xs[i] - xs[i - 1] > bestGap) { bestGap = xs[i] - xs[i - 1]; gapIndex = i; }
                    if (bestGap > 0.4 * span || xs.Count < 4)
                    {
                        var mid = Nice((xs[gapIndex - 1] + xs[gapIndex]) / 2, xs);
                        if (mid > xs[gapIndex - 1] && mid < xs[gapIndex] && !needs.Any(n => n.Input == column.Name && n.Values.Any(v => Math.Abs(v.AsNumber() - mid) < 1e-9)))
                            needs.Add(new ProbeNeed
                            {
                                Priority = 5, Input = column.Name, Values = { Value.Number(mid) },
                                Reason = $"{column.Name} değerlerinde {Value.FormatNumber(xs[gapIndex - 1])} ile {Value.FormatNumber(xs[gapIndex])} arası boş; " +
                                         "bu aralıkta davranış bilinmiyor.",
                            });
                    }
                }
                else
                {
                    var groups = column.Values.GroupBy(kv => kv.Value.AsText(), StringComparer.OrdinalIgnoreCase).ToList();
                    foreach (var g in groups.Where(g => g.Count() == 1 && groups.Count > 1))
                        needs.Add(new ProbeNeed
                        {
                            Priority = 4, Input = column.Name, Values = { g.First().Value },
                            Reason = $"{column.Name} = {g.Key} sadece 1 varyantta var; bu seçeneğe bağlı kurallar tek örneğe dayanıyor.",
                        });
                }
            }

            // Birlikte değişen sayısal girdiler: Halton doldurma bunu yeni varyantlarda kırar; kullanıcıya haber ver.
            var numeric = columns.Where(c => c.Kind == ColumnKind.Number).ToList();
            for (int i = 0; i < numeric.Count; i++)
            for (int j = i + 1; j < numeric.Count; j++)
            {
                var common = numeric[i].Values.Keys.Intersect(numeric[j].Values.Keys, StringComparer.OrdinalIgnoreCase).ToList();
                if (common.Count < 4) continue;
                var r = Pearson(common.Select(k => numeric[i].Values[k].AsNumber()).ToArray(),
                    common.Select(k => numeric[j].Values[k].AsNumber()).ToArray());
                if (Math.Abs(r) > 0.85)
                    needs.Add(new ProbeNeed
                    {
                        Priority = 5, Input = numeric[i].Name,
                        Reason = $"{numeric[i].Name} ile {numeric[j].Name} mevcut varyantlarda birlikte değişiyor (r = {r:0.00}). " +
                                 "Hangisinin neyi etkilediği karışabilir; önerilen varyantlarda ikisi bağımsız seçildi.",
                    });
            }
        }

        private static double Pearson(double[] a, double[] b)
        {
            double ma = a.Average(), mb = b.Average(), sab = 0, saa = 0, sbb = 0;
            for (int i = 0; i < a.Length; i++)
            {
                sab += (a[i] - ma) * (b[i] - mb);
                saa += (a[i] - ma) * (a[i] - ma);
                sbb += (b[i] - mb) * (b[i] - mb);
            }
            return saa < 1e-12 || sbb < 1e-12 ? 0 : sab / Math.Sqrt(saa * sbb);
        }

        // ---------- ihtiyaç üreticileri (RuleInferencer çağırır) ----------

        /// <summary>Eşik [lo, hi) aralığındaysa, aralığı daraltacak değerler.</summary>
        public static ProbeNeed ThresholdNeed(string input, double lo, double hi, IEnumerable<double> training, string rules)
        {
            var points = new List<double>();
            if (hi - lo <= 25)
            {
                // Aralık zaten dar: sınırın dahil olup olmadığını görmek için aday eşik ve bir fazlası.
                var t = NumberUtil.RoundestBetween(lo, hi);
                points.Add(t);
                points.Add(t + 1);
            }
            else
            {
                foreach (var q in new[] { 0.25, 0.5, 0.75 }) points.Add(Nice(lo + (hi - lo) * q, training));
            }
            var values = points.Where(p => p > lo - 1e-9 && p <= hi + 1e-9).Distinct().OrderBy(p => p).Select(Value.Number).ToList();
            return new ProbeNeed
            {
                Priority = 1, Input = input, Values = values,
                Reason = hi - lo <= 25
                    ? $"{input} eşiği {Value.FormatNumber(lo)}–{Value.FormatNumber(hi)} arasında ve sınır değerin dahil olup olmadığı belli değil ({rules})."
                    : $"{input} eşiği {Value.FormatNumber(lo)}–{Value.FormatNumber(hi)} arasında; bu değerler aralığı daraltır ({rules}).",
            };
        }

        /// <summary>Adet formülündeki sabit [lo, hi] arasında belirsizse, adedin değiştiği sınırdaki değerler.</summary>
        public static ProbeNeed OffsetNeed(string input, double step, double lo, double hi, IEnumerable<double> training, string rule)
        {
            var xs = training.OrderBy(d => d).ToList();
            double median = xs[xs.Count / 2];
            var thresholds = new[] { 0.25, 0.5, 0.75 }
                .Select(q => Math.Round(lo + (hi - lo) * q, MidpointRounding.AwayFromZero)).Distinct().ToList();
            var values = new List<Value>();
            foreach (var t in thresholds)
            {
                double k = Math.Round((median + t) / step);
                values.Add(Value.Number(k * step - t));
            }
            return new ProbeNeed
            {
                Priority = 2, Input = input,
                Values = values.GroupBy(v => v.AsNumber()).Select(g => g.First()).ToList(),
                Reason = $"adet formülündeki sabit ({input} değerine eklenen) {Value.FormatNumber(lo)} ile {Value.FormatNumber(hi)} arasında herhangi biri olabilir; " +
                         $"bu değerler adedin değiştiği sınırlara denk geliyor ({rule}).",
            };
        }

        /// <summary>
        /// İki girdi de aynı eşik sonucunu veriyi mükemmel ayırıyorsa (eşik hangisine bağlı?), ikisinin anlaşmazlığa düştüğü
        /// iki varyant: eğitim verisinde birlikte (aynı yönde) değişiyorlarsa zıt uçlar, zıt yönde değişiyorlarsa aynı uçlar.
        /// </summary>
        public static ProbeNeed CompetingThresholdNeed(InputColumn a, double aLo, double aHi, InputColumn b, double bLo, double bHi, string rules)
        {
            var common = a.Values.Keys.Intersect(b.Values.Keys, StringComparer.OrdinalIgnoreCase).ToList();
            var xa = a.Values.Values.Select(v => v.AsNumber()).ToList();
            var xb = b.Values.Values.Select(v => v.AsNumber()).ToList();
            double r = common.Count >= 3
                ? Pearson(common.Select(k => a.Values[k].AsNumber()).ToArray(), common.Select(k => b.Values[k].AsNumber()).ToArray())
                : 1;
            Value Low(double min, double lo, List<double> t) => Value.Number(Nice((min + lo) / 2, t));
            Value High(double hi, double max, List<double> t) => Value.Number(Nice((hi + max) / 2, t));
            var aLow = Low(xa.Min(), aLo, xa);
            var aHigh = High(aHi, xa.Max(), xa);
            var bLow = Low(xb.Min(), bLo, xb);
            var bHigh = High(bHi, xb.Max(), xb);
            var ci = StringComparer.OrdinalIgnoreCase;
            var combos = r >= 0
                ? new List<Dictionary<string, Value>>
                {
                    new Dictionary<string, Value>(ci) { [a.Name] = aLow, [b.Name] = bHigh },
                    new Dictionary<string, Value>(ci) { [a.Name] = aHigh, [b.Name] = bLow },
                }
                : new List<Dictionary<string, Value>>
                {
                    new Dictionary<string, Value>(ci) { [a.Name] = aLow, [b.Name] = bLow },
                    new Dictionary<string, Value>(ci) { [a.Name] = aHigh, [b.Name] = bHigh },
                };
            return new ProbeNeed
            {
                Priority = 1, Input = a.Name, Combos = combos,
                Reason = $"eşiği belirleyen girdi hangisi: {a.Name} / {b.Name}? Mevcut varyantlarda ikisi de sonucu aynı şekilde ayırıyor ({rules}); " +
                         "bu iki varyantta ikisi birbirine zıt sonuç verir.",
            };
        }

        public static ProbeNeed SupportNeed(string input, IList<double> seen, string rule)
        {
            double mid = (seen.Min() + seen.Max()) / 2;
            return new ProbeNeed
            {
                Priority = 3, Input = input, Values = { Value.Number(Nice(mid, seen)) },
                Reason = $"{rule} sadece 2 farklı {input} değerine dayanıyor; ara değer formülün doğru çizgi olduğunu doğrular.",
            };
        }

        // ---------- çıktı ----------
        public static string ToCsv(IReadOnlyList<InputDefinition> inputs, IReadOnlyList<SuggestedVariant> variants)
        {
            var sb = new StringBuilder();
            sb.AppendLine("Varyant;" + string.Join(";", inputs.Select(i => i.Name)));
            foreach (var v in variants)
            {
                sb.Append(v.Name);
                foreach (var i in inputs)
                {
                    sb.Append(';');
                    if (v.Inputs.TryGetValue(i.Name, out var value))
                        sb.Append(value.Kind == ValueKind.Number
                            ? value.AsNumber().ToString("0.####", CultureInfo.InvariantCulture).Replace('.', ',')
                            : value.AsText());
                }
                sb.AppendLine();
            }
            return sb.ToString();
        }
    }
}
