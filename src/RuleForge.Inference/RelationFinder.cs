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

        /// <summary>Eşikli formüllerde eşiğin bağlı olduğu girdi (ör. raf sayısı → Yükseklik).</summary>
        public string? ThresholdInput { get; set; }

        /// <summary>Veriyle uyumlu eşik aralığı: [ThresholdLo, ThresholdHi).</summary>
        public double ThresholdLo { get; set; }

        public double ThresholdHi { get; set; }

        /// <summary>Eşik yerine "{T}" yazılmış ifade; ortak eşik değişkeni oluşturulurken kullanılır.</summary>
        public string? Template { get; set; }

        /// <summary>Aynı sonucu veriyi mükemmel ayıran başka girdiler (eşik hangi girdiye bağlı belirsiz).</summary>
        public List<RelationCandidate> Competitors { get; set; } = new List<RelationCandidate>();

        /// <summary>Adet formüllerinde: parantez içindeki sabit veriyle tam belirlenemediyse uyumlu aralık.</summary>
        public string? OffsetInput { get; set; }

        public double OffsetLo { get; set; }
        public double OffsetHi { get; set; }

        /// <summary>Verilen sabitle aynı formülü yeniden yazar (başka bir kuraldan kanıt gelince).</summary>
        public Func<double, string>? WithOffset { get; set; }

        /// <summary>Adet formülündeki adım (ör. 50): sabit belirsizse sınırdaki değerleri hesaplamak için.</summary>
        public double OffsetStep { get; set; }

        /// <summary>Sadece 2 farklı girdi değerine dayanan doğrusal formüllerde o girdi ve gördüğü değerler.</summary>
        public string? SupportInput { get; set; }

        public List<double> SupportValues { get; set; } = new List<double>();
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

        /// <param name="fast">Pahalı aramaları (iki girdili doğrusal, adet, kategoriye göre doğrular) atlar. Girdi tahmininde binlerce kez çağrılır.</param>
        public List<RelationCandidate> Find(Observation obs, ExpectedType expected, bool fast = false)
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
                Add(found, RangeLookup(samples, y));
                if (!fast)
                {
                    Add(found, LinearTwo(samples, yn));
                    Add(found, StepFunction(samples, yn));
                    Add(found, PerCategoryLinear(samples, yn));
                    if (!found.Any(c => c.Confidence >= 0.7)) Add(found, Piecewise(samples, yn));
                }
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
                Add(found, NumericTextTemplate(samples, y));
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
                if (snapped == null)
                {
                    var rounded = RoundedLinear(x.Name, xs, ys, a, b);
                    if (rounded != null) yield return rounded;
                    continue;
                }
                var (sa, sb) = snapped.Value;

                var conf = NumberUtil.Confidence(distinct, 2);
                yield return new RelationCandidate
                {
                    Expression = NumberUtil.Linear(new[] { (sa, x.Name) }, sb),
                    Confidence = conf,
                    Complexity = Math.Abs(sa - 1) < 1e-12 ? 1 : 2,
                    Evidence = $"{samples.Count} varyantta tam uyum ({distinct} farklı {x.Name} değeri).",
                    Question = distinct == 2 ? $"Sadece 2 farklı {x.Name} değeri var; iki noktadan her zaman bir doğru geçer, daha fazla varyantla doğrulanmalı." : null,
                    SupportInput = distinct == 2 ? x.Name : null,
                    SupportValues = distinct == 2 ? xs.Distinct().ToList() : new List<double>(),
                };
            }
        }

        /// <summary>
        /// y = ROUND((x + c) / d) gibi tam sayıya yuvarlanmış doğrusal ilişkiler (ör. raf aralığı = ROUND((H - 86) / 3)).
        /// </summary>
        private RelationCandidate? RoundedLinear(string name, double[] xs, double[] ys, double a, double b)
        {
            if (ys.Any(v => Math.Abs(v - Math.Round(v)) > 1e-6)) return null;
            if (xs.Distinct().Count() < 4) return null; // yuvarlama çok şeyi örter; az veriyle güvenilmez
            if (a <= 0 || a > 1) return null;          // bölme biçimi: x / d (d >= 1)

            var forms = new (string fn, Func<double, double> f)[]
            {
                ("ROUND", v => Math.Round(v, MidpointRounding.AwayFromZero)),
                ("FLOOR", Math.Floor),
                ("CEILING", Math.Ceiling),
            };
            int d0 = (int)Math.Round(1 / a);
            if (d0 > 12) return null; // büyük adımlar (her 1500 mm'de bir ayak gibi) basamak fonksiyonunun işi
            var fits = new List<(string expr, int score)>();
            for (int d = Math.Max(1, d0 - 1); d <= d0 + 1; d++)
            {
                // y ≈ (x + c) / d  →  c ≈ y*d - x
                double c0 = Math.Round(ys.Select((v, i) => v * d - xs[i]).Average());
                for (int dc = -d - 1; dc <= d + 1; dc++)
                {
                    double c = c0 + dc;
                    foreach (var (fn, f) in forms)
                    {
                        bool ok = true;
                        for (int i = 0; i < xs.Length && ok; i++)
                            ok = Math.Abs(f(Math.Round((xs[i] + c) / d, 9)) - ys[i]) < 1e-6;
                        if (!ok) continue;
                        var inner = Math.Abs(c) < 1e-9 ? name : $"({NumberUtil.Linear(new[] { (1.0, name) }, c)})";
                        var expr = d == 1 ? $"{fn}({inner})" : $"{fn}({inner} / {d})";
                        int score = (fn == "ROUND" ? 0 : 1) + (Math.Abs(c % 10) < 1e-9 ? 0 : Math.Abs(c % 2) < 1e-9 ? 1 : 2);
                        fits.Add((expr, score));
                    }
                }
            }
            if (fits.Count == 0) return null;
            var best = fits.OrderBy(f => f.score).First();
            int alternatives = fits.Select(f => f.expr).Distinct().Count();
            return new RelationCandidate
            {
                Expression = best.expr,
                Confidence = NumberUtil.Confidence(xs.Distinct().Count(), 3, Math.Min(alternatives, 25)),
                Complexity = 3,
                Evidence = $"{xs.Length} varyantta tam sayıya yuvarlanmış doğrusal ilişki uyuyor" +
                           (alternatives > 1 ? $"; {alternatives} benzer formül de uyumlu." : "."),
                Question = alternatives > 1 ? $"Yuvarlama formülü kesin mi? Veriyle uyumlu diğerleri de var (ör. {best.expr})." : null,
            };
        }

        // ---------- eşiğe göre iki formül: IF(Yukseklik <= 750, Yukseklik / 2 - 25, ROUND((Yukseklik + 19) / 3)) ----------
        private IEnumerable<RelationCandidate> Piecewise(List<string> samples, Dictionary<string, double> y)
        {
            foreach (var x in Numeric)
            {
                var pts = samples.Select(s => (x: x.Values[s].AsNumber(), y: y[s])).OrderBy(p => p.x).ToList();
                if (pts.Select(p => p.x).Distinct().Count() < 5) continue;

                RelationCandidate? best = null;
                int bestBalance = -1;
                for (int k = 2; k <= pts.Count - 2; k++)
                {
                    if (Math.Abs(pts[k - 1].x - pts[k].x) < 1e-9) continue;
                    var left = SegmentExpression(x.Name, pts.Take(k).ToList());
                    var right = SegmentExpression(x.Name, pts.Skip(k).ToList());
                    if (left == null || right == null || left == right) continue;

                    int balance = Math.Min(k, pts.Count - k);
                    if (balance <= bestBalance) continue;
                    bestBalance = balance;
                    var lo = pts[k - 1].x;
                    var hi = pts[k].x;
                    var t = NumberUtil.RoundestBetween(lo, hi);
                    best = new RelationCandidate
                    {
                        Expression = $"IF({x.Name} <= {NumberUtil.Fmt(t)}, {left}, {right})",
                        Template = $"IF({x.Name} <= {{T}}, {left}, {right})",
                        ThresholdInput = x.Name,
                        ThresholdLo = lo,
                        ThresholdHi = hi,
                        Complexity = 6,
                        Confidence = NumberUtil.Confidence(pts.Count, 5),
                        Evidence = $"{x.Name} {NumberUtil.Fmt(lo)} ile {NumberUtil.Fmt(hi)} arasındaki bir eşikte formül değişiyor " +
                                   $"(altında {k}, üstünde {pts.Count - k} varyant).",
                        Question = $"{x.Name} için formülün değiştiği eşik {NumberUtil.Fmt(lo)} ile {NumberUtil.Fmt(hi)} arasında. Kesin değer nedir?" +
                                   (k < 3 || pts.Count - k < 3 ? " Bir tarafta sadece 2 varyant var; o taraftaki formül daha fazla varyantla doğrulanmalı." : string.Empty),
                    };
                }
                if (best != null) yield return best;
            }
        }

        /// <summary>Bir veri parçası için sabit, doğrusal veya yuvarlanmış doğrusal ifade; uymazsa null.</summary>
        private string? SegmentExpression(string name, List<(double x, double y)> seg)
        {
            var xs = seg.Select(p => p.x).ToArray();
            var ys = seg.Select(p => p.y).ToArray();
            if (ys.All(v => NumberUtil.Close(v, ys[0], _tol))) return NumberUtil.Fmt(NumberUtil.Nice(ys[0], _tol));
            if (xs.Distinct().Count() < 2) return null;
            var fit = FitLine(xs, ys);
            if (fit == null) return null;
            var snapped = SnapLine(xs, ys, fit.Value.a, fit.Value.b);
            if (snapped != null) return NumberUtil.Linear(new[] { (snapped.Value.a, name) }, snapped.Value.b);
            return RoundedLinear(name, xs, ys, fit.Value.a, fit.Value.b)?.Expression;
        }

        /// <summary>ys = a·xs + b tam uyumu (yuvarlak katsayılarla); yoksa null.</summary>
        internal static (double a, double b)? ExactLinear(double[] xs, double[] ys, double tolerance)
        {
            var finder = new RelationFinder(new List<InputColumn>(), tolerance);
            var fit = finder.FitLine(xs, ys);
            return fit == null ? null : finder.SnapLine(xs, ys, fit.Value.a, fit.Value.b);
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
            // İki noktadan her doğru geçer: az veride sadece "yuvarlak" katsayılar (1, 1/2, 1/4 …) kabul edilir.
            bool fewPoints = xs.Distinct().Count() < 3;
            foreach (var ca in NumberUtil.CoefficientCandidates(a))
            {
                if (fewPoints && !NumberUtil.IsNiceCoefficient(ca)) continue;
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

                // (biçim, adım, sabit) → veriyle uyumlu ofsetler
                var groups = new Dictionary<(string form, double step, double c), List<double>>();
                foreach (var step in StepCandidates)
                {
                    // Küçük adımlarda (çivi aralığı gibi) tam sayı ofsetler, ±2 adım; büyüklerde kaba ızgara.
                    var offsets = step <= 200
                        ? Enumerable.Range((int)(-2 * step), (int)(4 * step) + 1).Select(o => (double)o)
                        : Enumerable.Range(-20, 41).Select(k => k * Math.Max(1, step / 20));
                    foreach (var off in offsets)
                    {
                        foreach (var (name, f) in forms)
                        {
                            double c = ys[0] - f(Math.Round((xs[0] + off) / step, 9));
                            bool ok = true;
                            for (int i = 1; i < xs.Length && ok; i++)
                                ok = Math.Abs(f(Math.Round((xs[i] + off) / step, 9)) + c - ys[i]) < 1e-6;
                            if (!ok) continue;
                            var key = (name, step, c);
                            if (!groups.TryGetValue(key, out var list)) groups[key] = list = new List<double>();
                            list.Add(off);
                        }
                    }
                }
                if (groups.Count == 0) continue;

                // Her grup için veriyle uyumlu ofset aralığının ortasını seç (gerçek değere beklenen en yakın tahmin).
                var scored = groups.Select(g =>
                {
                    var lo = g.Value.Min();
                    var hi = g.Value.Max();
                    // Ofsetsiz formül uyuyorsa en basiti odur; uymuyorsa uyumlu aralığın ortası en iyi tahmindir.
                    var off = g.Value.Contains(0) ? 0 : Math.Round((lo + hi) / 2, MidpointRounding.AwayFromZero);
                    if (!g.Value.Contains(off)) off = g.Value.OrderBy(o => Math.Abs(o - (lo + hi) / 2)).First();
                    bool fencepost = g.Key.form == "FLOOR" && Math.Abs(g.Key.c - 1) < 1e-9; // ROUNDDOWN(L / adım) + 1
                    int score = (Math.Abs(off) < 1e-9 ? 0 : 2)
                                + (Math.Abs(g.Key.c) < 1e-9 || fencepost ? 0 : 1)
                                + (g.Key.step % 50 == 0 ? 0 : 1)
                                + (g.Key.form == "ROUND" ? 1 : 0);
                    return (g.Key.form, g.Key.step, g.Key.c, off, lo, hi, score, fencepost);
                })
                .OrderBy(g => g.score).ThenByDescending(g => g.fencepost).ThenBy(g => Math.Abs(g.off))
                .ToList();

                var best = scored[0];
                var inputName = x.Name;
                Func<double, string> format = off =>
                {
                    var inner = Math.Abs(off) < 1e-9
                        ? $"{inputName} / {NumberUtil.Fmt(best.step)}"
                        : $"({NumberUtil.Linear(new[] { (1.0, inputName) }, off)}) / {NumberUtil.Fmt(best.step)}";
                    var e = $"{best.form}({inner})";
                    if (Math.Abs(best.c) > 1e-9) e += best.c > 0 ? $" + {NumberUtil.Fmt(best.c)}" : $" - {NumberUtil.Fmt(-best.c)}";
                    return e;
                };
                var expr = format(best.off);

                int alternatives = scored.Count;
                int distinct = xs.Distinct().Count();
                string? question = null;
                if (best.hi - best.lo > 1e-9)
                    question = $"{expr}: parantez içindeki sabit veriyle ayırt edilemiyor; " +
                               $"{NumberUtil.Linear(new[] { (1.0, x.Name) }, best.lo)} ile {NumberUtil.Linear(new[] { (1.0, x.Name) }, best.hi)} arasındaki her değer 9 varyantla da uyumlu. Gerçek değer nedir?"
                               .Replace("9 varyant", $"{samples.Count} varyant");
                else if (alternatives > 1)
                    question = $"Adet kuralı için başka formüller de veriyle uyumlu (ör. {expr}). Gerçek kural nedir?";

                yield return new RelationCandidate
                {
                    Expression = expr,
                    OffsetInput = best.hi - best.lo > 1e-9 ? x.Name : null,
                    OffsetStep = best.step,
                    OffsetLo = best.lo,
                    OffsetHi = best.hi,
                    WithOffset = format,
                    Confidence = NumberUtil.Confidence(distinct, 2, Math.Min(alternatives, 25)),
                    Complexity = 4,
                    Evidence = $"{samples.Count} varyantta basamak (adet) fonksiyonu uyuyor; {alternatives} farklı biçim de veriyle uyumlu.",
                    Question = question,
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
                if (groups.Any(g => g.Count() < 2)) continue; // tek varyantlık grup hiçbir şey kanıtlamaz
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
                // Veri sayısı kadar girişli tablo bir kural değil ezberdir: 3'ten fazla aralık için her aralıkta ortalama 2 örnek ister.
                if (runs.Count > 3 && pts.Count < 2 * runs.Count) continue;
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
                    Template = runs.Count == 2 ? $"RANGELOOKUP({x.Name}, {{T}}, {parts[1]}, {parts[2]})" : null,
                    ThresholdInput = runs.Count == 2 ? x.Name : null,
                    ThresholdLo = runs[0].hi,
                    ThresholdHi = runs[runs.Count - 1].lo,
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
                    yield return ThresholdCandidate($"{x.Name} > {{T}}", x.Name, falses.Max(), trues.Min(), samples.Count);
                else if (trues.Max() < falses.Min())
                    yield return ThresholdCandidate($"{x.Name} <= {{T}}", x.Name, trues.Max(), falses.Min(), samples.Count);
            }
        }

        private static RelationCandidate ThresholdCandidate(string template, string name, double lo, double hi, int n)
        {
            var t = NumberUtil.RoundestBetween(lo, hi);
            return new RelationCandidate
            {
                Expression = template.Replace("{T}", NumberUtil.Fmt(t)),
                Template = template,
                ThresholdInput = name,
                ThresholdLo = lo,
                ThresholdHi = hi,
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

        // ---------- sayılı metin: "KONVEYOR 1500x400" → "KONVEYOR " & ((Bant - 300) / 2) & "x" & (Rulo - 50) ----------
        private static readonly System.Text.RegularExpressions.Regex NumberToken =
            new System.Text.RegularExpressions.Regex(@"\d+(?:[.,]\d+)?", System.Text.RegularExpressions.RegexOptions.Compiled);

        private RelationCandidate? NumericTextTemplate(List<string> samples, Dictionary<string, Value> y)
        {
            var texts = samples.Select(s => y[s].AsText()).ToList();
            if (texts.Distinct(StringComparer.OrdinalIgnoreCase).Count() < 2) return null;

            // Tüm varyantlarda sayılar dışındaki iskelet aynı olmalı.
            var skeletons = texts.Select(t => NumberToken.Replace(t, "\u0001")).Distinct(StringComparer.Ordinal).ToList();
            if (skeletons.Count != 1) return null;
            var literals = skeletons[0].Split('\u0001');
            int slots = literals.Length - 1;
            if (slots == 0 || slots > 6) return null;

            var numbers = texts.Select(t => NumberToken.Matches(t).Cast<System.Text.RegularExpressions.Match>()
                .Select(m => double.Parse(m.Value.Replace(',', '.'), System.Globalization.CultureInfo.InvariantCulture)).ToArray()).ToList();

            var parts = new List<string>();
            bool anyInput = false;
            for (int k = 0; k < slots; k++)
            {
                if (literals[k].Length > 0) parts.Add(NumberUtil.Literal(Value.Text(literals[k])));
                var ys = numbers.Select(n => n[k]).ToArray();
                if (ys.All(v => Math.Abs(v - ys[0]) < 1e-9))
                {
                    parts.Add(NumberUtil.Literal(Value.Text(NumberUtil.Fmt(ys[0]))));
                    continue;
                }
                string? expr = null;
                foreach (var x in Numeric)
                {
                    var xs = samples.Select(s => x.Values[s].AsNumber()).ToArray();
                    var fit = FitLine(xs, ys);
                    var snapped = fit == null ? null : SnapLine(xs, ys, fit.Value.a, fit.Value.b);
                    if (snapped == null) continue;
                    var linear = NumberUtil.Linear(new[] { (snapped.Value.a, x.Name) }, snapped.Value.b);
                    expr = linear == x.Name ? x.Name : "(" + linear + ")";
                    break;
                }
                if (expr == null) return null;
                anyInput = true;
                parts.Add(expr);
            }
            if (!anyInput) return null;
            if (literals[slots].Length > 0) parts.Add(NumberUtil.Literal(Value.Text(literals[slots])));

            return new RelationCandidate
            {
                Expression = string.Join(" & ", parts),
                Complexity = 4,
                Confidence = NumberUtil.Confidence(samples.Count, 2 * slots),
                Evidence = $"{samples.Count} varyantta metin iskeleti aynı, içindeki sayılar girdilerle doğrusal ilişkili.",
            };
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
