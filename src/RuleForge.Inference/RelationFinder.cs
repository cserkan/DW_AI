using System;
using System.Collections.Generic;
using System.Linq;
using RuleForge.Core.Expressions;
using RuleForge.Core.Rules;

namespace RuleForge.Inference
{
    internal enum ColumnKind
    {
        Number,
        Bool,
        Category,
    }

    internal sealed class InputColumn
    {
        public InputColumn(string name, ColumnKind kind, Dictionary<string, Value> values)
        {
            Name = name;
            Kind = kind;
            Values = values;
        }

        public string Name { get; }
        public ColumnKind Kind { get; }
        public Dictionary<string, Value> Values { get; }
    }

    /// <summary>Bir gözlem için bulunan aday ilişki.</summary>
    public sealed class RelationCandidate
    {
        public string Expression { get; set; } = string.Empty;
        public double Confidence { get; set; }
        public string Evidence { get; set; } = string.Empty;

        /// <summary>Küçük = daha basit. Aynı güvende basit olan seçilir (Occam).</summary>
        public int Complexity { get; set; }

        /// <summary>Kullanıcıya sorulması gereken belirsizlik (ör. eşik aralığı).</summary>
        public string? Question { get; set; }
    }

    /// <summary>
    /// Deterministik ilişki arama: doğrusal, kategorik eşleme, basamak (adet) fonksiyonu,
    /// eşik, aralık tablosu, metin şablonu. Yapay zekâ kullanmaz; YZ bu sonuçları yorumlar.
    /// </summary>
    internal sealed class RelationFinder
    {
        private static readonly double[] StepCandidates =
            { 1, 2, 5, 10, 20, 25, 50, 100, 125, 150, 200, 250, 300, 400, 500, 600, 750, 800, 1000, 1200, 1250, 1500, 2000, 2500, 3000, 5000 };

        private readonly IReadOnlyList<InputColumn> _inputs;
        private readonly double _tol;

        public RelationFinder(IReadOnlyList<InputColumn> inputs, double tolerance)
        {
            _inputs = inputs;
            _tol = tolerance;
        }

        public List<RelationCandidate> Find(Observation obs, ExpectedType expected)
        {
            var samples = obs.Values.Keys.Where(s => _inputs.All(i => i.Values.ContainsKey(s))).ToList();
            var y = samples.ToDictionary(s => s, s => obs.Values[s], StringComparer.OrdinalIgnoreCase);
            var found = new List<RelationCandidate>();
            if (samples.Count < 2) return found;

            bool allNumber = y.Values.All(v => v.Kind == ValueKind.Number);
            bool allBool = y.Values.All(v => v.Kind == ValueKind.Bool);

            if (allNumber)
            {
                var yn = y.ToDictionary(kv => kv.Key, kv => kv.Value.AsNumber(), StringComparer.OrdinalIgnoreCase);
                Add(found, LinearOne(samples, yn));
                Add(found, LinearTwo(samples, yn));
                Add(found, StepFunction(samples, yn));
                Add(found, PerCategoryLinear(samples, yn));
                Add(found, RangeLookup(samples, y));
            }
            if (allBool)
            {
                Add(found, BoolEqualsInput(samples, y));
                Add(found, Threshold(samples, y));
            }
            if (!allNumber && !allBool && expected == ExpectedType.Text)
            {
                Add(found, TextEqualsInput(samples, y));
                Add(found, TextTemplate(samples, y));
            }
            Add(found, CategoryMapping(samples, y));

            return found
                .OrderByDescending(c => c.Confidence >= 0.7)
                .ThenBy(c => c.Complexity)
                .ThenByDescending(c => c.Confidence)
                .ToList();
        }

        private static void Add(List<RelationCandidate> list, IEnumerable<RelationCandidate> items) => list.AddRange(items);

        private static void Add(List<RelationCandidate> list, RelationCandidate? item)
        {
            if (item != null) list.Add(item);
        }

        private IEnumerable<InputColumn> Numeric => _inputs.Where(i => i.Kind == ColumnKind.Number);

        private IEnumerable<InputColumn> Categorical => _inputs.Where(i => i.Kind != ColumnKind.Number);

        // ---------- y = a*x + b ----------
        private IEnumerable<RelationCandidate> LinearOne(List<string> samples, Dictionary<string, double> y)
        {
            foreach (var x in Numeric)
            {
                var xs = samples.Select(s => x.Values[s].AsNumber()).ToArray();
                var ys = samples.Select(s => y[s]).ToArray();
                int distinct = xs.Distinct().Count();
                if (distinct < 2) continue;

                var fit = FitLine(xs, ys);
                if (fit == null) continue;
                var (a, b) = fit.Value;
                if (Math.Abs(a) < 1e-9) continue; // sabit; girdiye bağlı değil
                var snapped = SnapLine(xs, ys, a, b);
                if (snapped == null) continue;
                var (sa, sb) = snapped.Value;

                var conf = NumberUtil.Confidence(distinct, 2);
                yield return new RelationCandidate
                {
                    Expression = NumberUtil.Linear(new[] { (sa, x.Name) }, sb),
                    Confidence = conf,
                    Complexity = Math.Abs(sa - 1) < 1e-12 ? 1 : 2,
                    Evidence = $"{samples.Count} varyantta tam uyum ({distinct} farklı {x.Name} değeri).",
                    Question = distinct == 2 ? $"Sadece 2 farklı {x.Name} değeri var; iki noktadan her zaman bir doğru geçer, daha fazla varyantla doğrulanmalı." : null,
                };
            }
        }

        private (double a, double b)? FitLine(double[] xs, double[] ys)
        {
            int n = xs.Length;
            double mx = xs.Average(), my = ys.Average();
            double sxx = 0, sxy = 0;
            for (int i = 0; i < n; i++)
            {
                sxx += (xs[i] - mx) * (xs[i] - mx);
                sxy += (xs[i] - mx) * (ys[i] - my);
            }
            if (sxx < 1e-12) return null;
            var a = sxy / sxx;
            return (a, my - a * mx);
        }

        private (double a, double b)? SnapLine(double[] xs, double[] ys, double a, double b)
        {
            foreach (var ca in NumberUtil.CoefficientCandidates(a))
            {
                var cb = NumberUtil.Nice(Enumerable.Range(0, xs.Length).Average(i => ys[i] - ca * xs[i]), _tol);
                bool ok = true;
                for (int i = 0; i < xs.Length && ok; i++)
                    ok = NumberUtil.Close(ca * xs[i] + cb, ys[i], _tol);
                if (ok) return (ca, cb);
            }
            return null;
        }

        // ---------- y = a*x1 + b*x2 + c ----------
        private IEnumerable<RelationCandidate> LinearTwo(List<string> samples, Dictionary<string, double> y)
        {
            var nums = Numeric.ToList();
            if (samples.Count < 4) yield break;
            for (int i = 0; i < nums.Count; i++)
            for (int j = i + 1; j < nums.Count; j++)
            {
                var x1 = samples.Select(s => nums[i].Values[s].AsNumber()).ToArray();
                var x2 = samples.Select(s => nums[j].Values[s].AsNumber()).ToArray();
                var ys = samples.Select(s => y[s]).ToArray();
                int n = ys.Length;

                // Normal denklemler
                var m = new double[3, 3];
                var v = new double[3];
                for (int k = 0; k < n; k++)
                {
                    var row = new[] { x1[k], x2[k], 1.0 };
                    for (int r = 0; r < 3; r++)
                    {
                        for (int c = 0; c < 3; c++) m[r, c] += row[r] * row[c];
                        v[r] += row[r] * ys[k];
                    }
                }
                var sol = NumberUtil.Solve(m, v);
                if (sol == null) continue;
                if (Math.Abs(sol[0]) < 1e-9 || Math.Abs(sol[1]) < 1e-9) continue; // tek değişkenli zaten aranıyor

                foreach (var ca in NumberUtil.CoefficientCandidates(sol[0]))
                foreach (var cb in NumberUtil.CoefficientCandidates(sol[1]))
                {
                    var cc = NumberUtil.Nice(Enumerable.Range(0, n).Average(k => ys[k] - ca * x1[k] - cb * x2[k]), _tol);
                    bool ok = true;
                    for (int k = 0; k < n && ok; k++)
                        ok = NumberUtil.Close(ca * x1[k] + cb * x2[k] + cc, ys[k], _tol);
                    if (!ok) continue;

                    yield return new RelationCandidate
                    {
                        Expression = NumberUtil.Linear(new[] { (ca, nums[i].Name), (cb, nums[j].Name) }, cc),
                        Confidence = NumberUtil.Confidence(n, 3),
                        Complexity = 3,
                        Evidence = $"{n} varyantta iki girdili doğrusal ilişki tam uyuyor.",
                    };
                    goto nextPair;
                }
                nextPair: ;
            }
        }

        // ---------- y = CEILING/FLOOR/ROUND((x + off) / step) + c  (adet, ayak sayısı vb.) ----------
        private IEnumerable<RelationCandidate> StepFunction(List<string> samples, Dictionary<string, double> y)
        {
            var ys = samples.Select(s => y[s]).ToArray();
            if (ys.Any(v => Math.Abs(v - Math.Round(v)) > 1e-6)) yield break;
            if (ys.Distinct().Count() < 2) yield break;
            // Basamak fonksiyonu ancak y, x'ten daha az farklı değer alıyorsa anlamlıdır (adet gibi).
            if (Numeric.All(x => samples.Select(s => x.Values[s].AsNumber()).Distinct().Count() <= ys.Distinct().Count())) yield break;

            var forms = new (string name, Func<double, double> f)[]
            {
                ("CEILING", Math.Ceiling),
                ("FLOOR", Math.Floor),
                ("ROUND", v => Math.Round(v, MidpointRounding.AwayFromZero)),
            };

            foreach (var x in Numeric)
            {
                var xs = samples.Select(s => x.Values[s].AsNumber()).ToArray();
                if (xs.Distinct().Count() <= ys.Distinct().Count()) continue;
                var fits = new List<(string expr, int score)>();
                foreach (var step in StepCandidates)
                {
                    double grid = Math.Max(1, step / 20);
                    for (int k = -20; k <= 20; k++)
                    {
                        double off = k * grid;
                        foreach (var (name, f) in forms)
                        {
                            double c = ys[0] - f(Math.Round((xs[0] + off) / step, 9));
                            bool ok = true;
                            for (int i = 1; i < xs.Length && ok; i++)
                                ok = Math.Abs(f(Math.Round((xs[i] + off) / step, 9)) + c - ys[i]) < 1e-6;
                            if (!ok) continue;

                            var inner = Math.Abs(off) < 1e-9
                                ? $"{x.Name} / {NumberUtil.Fmt(step)}"
                                : $"({NumberUtil.Linear(new[] { (1.0, x.Name) }, off)}) / {NumberUtil.Fmt(step)}";
                            var expr = $"{name}({inner})";
                            if (Math.Abs(c) > 1e-9) expr += c > 0 ? $" + {NumberUtil.Fmt(c)}" : $" - {NumberUtil.Fmt(-c)}";
                            // Basitlik puanı: ofset yok, sabit yok, yuvarlak adım tercih edilir.
                            int score = (Math.Abs(off) < 1e-9 ? 0 : 2) + (Math.Abs(c) < 1e-9 ? 0 : 1) + (step % 100 == 0 ? 0 : 1);
                            fits.Add((expr, score));
                        }
                    }
                }
                if (fits.Count == 0) continue;

                var best = fits.OrderBy(f => f.score).First();
                int alternatives = fits.Select(f => f.expr).Distinct().Count();
                int distinct = xs.Distinct().Count();
                yield return new RelationCandidate
                {
                    Expression = best.expr,
                    Confidence = NumberUtil.Confidence(distinct, 2, Math.Min(alternatives, 25)),
                    Complexity = 4,
                    Evidence = $"{samples.Count} varyantta basamak (adet) fonksiyonu uyuyor; {alternatives} alternatif formül de veriyle uyumlu.",
                    Question = alternatives > 1
                        ? $"Adet kuralı için birden fazla formül veriyle uyumlu (ör. {best.expr}). Gerçek kural nedir? (ör. 'her 1000 mm'de bir ayak')"
                        : null,
                };
            }
        }

        // ---------- kategoriye göre farklı doğrular: SWITCH(Malzeme, "A", x+10, "B", x+12) ----------
        private IEnumerable<RelationCandidate> PerCategoryLinear(List<string> samples, Dictionary<string, double> y)
        {
            foreach (var cat in Categorical)
            foreach (var x in Numeric)
            {
                var groups = samples.GroupBy(s => cat.Values[s]).ToList();
                if (groups.Count < 2) continue;
                var parts = new List<string>();
                bool ok = true;
                int distinctTotal = 0;
                foreach (var g in groups.OrderBy(g => g.Key.AsText(), StringComparer.OrdinalIgnoreCase))
                {
                    var xs = g.Select(s => x.Values[s].AsNumber()).ToArray();
                    var ys = g.Select(s => y[s]).ToArray();
                    string expr;
                    if (ys.All(v => NumberUtil.Close(v, ys[0], _tol)))
                    {
                        expr = NumberUtil.Fmt(NumberUtil.Nice(ys[0], _tol));
                    }
                    else
                    {
                        if (xs.Distinct().Count() < 2) { ok = false; break; }
                        var fit = FitLine(xs, ys);
                        var snapped = fit == null ? null : SnapLine(xs, ys, fit.Value.a, fit.Value.b);
                        if (snapped == null) { ok = false; break; }
                        expr = NumberUtil.Linear(new[] { (snapped.Value.a, x.Name) }, snapped.Value.b);
                    }
                    distinctTotal += xs.Distinct().Count();
                    parts.Add(NumberUtil.Literal(g.Key));
                    parts.Add(expr);
                }
                if (!ok) continue;
                // Tüm dallar aynıysa kategori gereksiz; tek girdili doğrusal ilişki zaten bulunur.
                if (Enumerable.Range(0, parts.Count / 2).Select(k => parts[2 * k + 1]).Distinct().Count() < 2) continue;
                yield return new RelationCandidate
                {
                    Expression = cat.Kind == ColumnKind.Bool && groups.Count == 2
                        ? $"IF({cat.Name}, {parts[parts.IndexOf("TRUE") + 1]}, {parts[parts.IndexOf("FALSE") + 1]})"
                        : $"SWITCH({cat.Name}, {string.Join(", ", parts)})",
                    Confidence = NumberUtil.Confidence(distinctTotal, 2 * groups.Count),
                    Complexity = 5,
                    Evidence = $"{cat.Name} değerine göre farklı doğrusal ilişkiler ({groups.Count} grup).",
                };
            }
        }

        // ---------- aralık tablosu: RANGELOOKUP(x, 1000, 40, 2000, 60, 80) ----------
        private IEnumerable<RelationCandidate> RangeLookup(List<string> samples, Dictionary<string, Value> y)
        {
            foreach (var x in Numeric)
            {
                var pts = samples.Select(s => (x: x.Values[s].AsNumber(), y: y[s])).OrderBy(p => p.x).ToList();
                // Aynı x → aynı y olmalı
                if (pts.GroupBy(p => p.x).Any(g => g.Select(p => p.y).Distinct().Count() > 1)) continue;
                var runs = new List<(double lo, double hi, Value v, int count)>();
                foreach (var p in pts)
                {
                    if (runs.Count > 0 && Value.LooseEquals(runs[runs.Count - 1].v, p.y))
                    {
                        var r = runs[runs.Count - 1];
                        runs[runs.Count - 1] = (r.lo, p.x, r.v, r.count + 1);
                    }
                    else runs.Add((p.x, p.x, p.y, 1));
                }
                if (runs.Count < 2 || runs.Count > 8) continue;
                // Aynı değer iki ayrı aralıkta tekrar ediyorsa tablo büyür; yine de izin ver ama güveni düşür.
                var parts = new List<string>();
                var questions = new List<string>();
                for (int i = 0; i < runs.Count - 1; i++)
                {
                    var limit = NumberUtil.RoundestBetween(runs[i].hi, runs[i + 1].lo);
                    parts.Add(NumberUtil.Fmt(limit));
                    parts.Add(NumberUtil.Literal(runs[i].v));
                    questions.Add($"{NumberUtil.Fmt(runs[i].hi)}–{NumberUtil.Fmt(runs[i + 1].lo)}");
                }
                parts.Add(NumberUtil.Literal(runs[runs.Count - 1].v));
                yield return new RelationCandidate
                {
                    Expression = $"RANGELOOKUP({x.Name}, {string.Join(", ", parts)})",
                    Confidence = NumberUtil.Confidence(pts.Count, 2 * runs.Count - 1),
                    Complexity = 6,
                    Evidence = $"{x.Name} aralıklarına göre {runs.Count} farklı değer.",
                    Question = $"{x.Name} için geçiş sınırları şu aralıklarda bir yerde: {string.Join(", ", questions)}. Kesin sınırlar nedir?",
                };
            }
        }

        // ---------- mantıksal = girdi ----------
        private IEnumerable<RelationCandidate> BoolEqualsInput(List<string> samples, Dictionary<string, Value> y)
        {
            foreach (var x in _inputs.Where(i => i.Kind == ColumnKind.Bool))
            {
                if (samples.All(s => x.Values[s].AsBool() == y[s].AsBool()))
                    yield return new RelationCandidate
                    {
                        Expression = x.Name, Complexity = 1, Confidence = NumberUtil.Confidence(samples.Count, 1),
                        Evidence = $"{x.Name} ile birebir aynı.",
                    };
                else if (samples.All(s => x.Values[s].AsBool() != y[s].AsBool()))
                    yield return new RelationCandidate
                    {
                        Expression = $"NOT({x.Name})", Complexity = 1, Confidence = NumberUtil.Confidence(samples.Count, 1),
                        Evidence = $"{x.Name} ile her zaman zıt.",
                    };
            }
        }

        // ---------- eşik: Boy > 3000 ----------
        private IEnumerable<RelationCandidate> Threshold(List<string> samples, Dictionary<string, Value> y)
        {
            foreach (var x in Numeric)
            {
                var trues = samples.Where(s => y[s].AsBool()).Select(s => x.Values[s].AsNumber()).ToList();
                var falses = samples.Where(s => !y[s].AsBool()).Select(s => x.Values[s].AsNumber()).ToList();
                if (trues.Count == 0 || falses.Count == 0) continue;

                if (trues.Min() > falses.Max())
                {
                    var t = NumberUtil.RoundestBetween(falses.Max(), trues.Min());
                    yield return ThresholdCandidate($"{x.Name} > {NumberUtil.Fmt(t)}", x.Name, falses.Max(), trues.Min(), samples.Count);
                }
                else if (trues.Max() < falses.Min())
                {
                    var t = NumberUtil.RoundestBetween(trues.Max(), falses.Min());
                    yield return ThresholdCandidate($"{x.Name} <= {NumberUtil.Fmt(t)}", x.Name, trues.Max(), falses.Min(), samples.Count);
                }
            }
        }

        private static RelationCandidate ThresholdCandidate(string expr, string name, double lo, double hi, int n)
        {
            return new RelationCandidate
            {
                Expression = expr,
                Complexity = 2,
                Confidence = NumberUtil.Confidence(n, 2),
                Evidence = $"{name} {NumberUtil.Fmt(lo)} ile {NumberUtil.Fmt(hi)} arasında bir eşikte değişiyor.",
                Question = $"{name} için eşik {NumberUtil.Fmt(lo)} ile {NumberUtil.Fmt(hi)} arasında. Kesin değer ve sınır dahil mi?",
            };
        }

        // ---------- kategori → değer: SWITCH(Tip, "A", 40, "B", 60) ----------
        private IEnumerable<RelationCandidate> CategoryMapping(List<string> samples, Dictionary<string, Value> y)
        {
            foreach (var cat in Categorical)
            {
                var groups = samples.GroupBy(s => cat.Values[s]).ToList();
                if (groups.Count < 2) continue;
                if (groups.Any(g => g.Select(s => y[s]).Distinct().Count() > 1)) continue;
                var mapping = groups.Select(g => (key: g.Key, value: y[g.First()])).OrderBy(m => m.key.AsText(), StringComparer.OrdinalIgnoreCase).ToList();
                if (mapping.Select(m => m.value).Distinct().Count() < 2) continue;

                string expr;
                if (y.Values.All(v => v.Kind == ValueKind.Bool))
                {
                    var trueKeys = mapping.Where(m => m.value.AsBool()).Select(m => m.key).ToList();
                    expr = trueKeys.Count == 1
                        ? $"{cat.Name} = {NumberUtil.Literal(trueKeys[0])}"
                        : $"OR({string.Join(", ", trueKeys.Select(k => $"{cat.Name} = {NumberUtil.Literal(k)}"))})";
                    if (cat.Kind == ColumnKind.Bool) continue; // BoolEqualsInput kapsıyor
                }
                else if (cat.Kind == ColumnKind.Bool)
                {
                    var t = mapping.First(m => m.key.AsBool()).value;
                    var f = mapping.First(m => !m.key.AsBool()).value;
                    expr = $"IF({cat.Name}, {NumberUtil.Literal(t)}, {NumberUtil.Literal(f)})";
                }
                else
                {
                    expr = $"SWITCH({cat.Name}, {string.Join(", ", mapping.Select(m => $"{NumberUtil.Literal(m.key)}, {NumberUtil.Literal(m.value)}"))})";
                }

                yield return new RelationCandidate
                {
                    Expression = expr,
                    Complexity = 2 + groups.Count / 3,
                    Confidence = NumberUtil.Confidence(samples.Count, groups.Count),
                    Evidence = $"{cat.Name} değerine göre belirleniyor ({groups.Count} seçenek, {samples.Count} varyant).",
                };
            }
        }

        // ---------- metin = girdi ----------
        private IEnumerable<RelationCandidate> TextEqualsInput(List<string> samples, Dictionary<string, Value> y)
        {
            foreach (var x in _inputs)
                if (samples.All(s => string.Equals(x.Values[s].AsText(), y[s].AsText(), StringComparison.OrdinalIgnoreCase)))
                    yield return new RelationCandidate
                    {
                        Expression = x.Name, Complexity = 1, Confidence = NumberUtil.Confidence(samples.Count, 1),
                        Evidence = $"{x.Name} girdisiyle aynı metin.",
                    };
        }

        // ---------- metin şablonu: "KONV-" & Boy & "x" & Genislik ----------
        private RelationCandidate? TextTemplate(List<string> samples, Dictionary<string, Value> y)
        {
            if (y.Values.Select(v => v.AsText()).Distinct(StringComparer.OrdinalIgnoreCase).Count() < 2) return null;
            string? template = null;
            var ordered = _inputs.OrderByDescending(i => samples.Max(s => i.Values[s].AsText().Length)).ToList();
            foreach (var s in samples)
            {
                var text = y[s].AsText();
                foreach (var input in ordered)
                {
                    var v = input.Values[s].AsText();
                    if (v.Length == 0) continue;
                    text = text.Replace(v, "\u0001" + input.Name + "\u0002");
                }
                if (template == null) template = text;
                else if (!string.Equals(template, text, StringComparison.Ordinal)) return null;
            }
            if (template == null || template.IndexOf('\u0001') < 0) return null;

            var parts = new List<string>();
            int pos = 0;
            while (pos < template.Length)
            {
                int open = template.IndexOf('\u0001', pos);
                if (open < 0) { parts.Add(NumberUtil.Literal(Value.Text(template.Substring(pos)))); break; }
                if (open > pos) parts.Add(NumberUtil.Literal(Value.Text(template.Substring(pos, open - pos))));
                int close = template.IndexOf('\u0002', open);
                parts.Add(template.Substring(open + 1, close - open - 1));
                pos = close + 1;
            }
            return new RelationCandidate
            {
                Expression = string.Join(" & ", parts),
                Complexity = 3,
                Confidence = NumberUtil.Confidence(samples.Count, 1),
                Evidence = $"{samples.Count} varyantta aynı metin şablonu.",
            };
        }
    }
}
