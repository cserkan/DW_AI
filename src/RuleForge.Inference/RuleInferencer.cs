using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using RuleForge.Core.Expressions;
using RuleForge.Core.Model;
using RuleForge.Core.Rules;

namespace RuleForge.Inference
{
    public sealed class UnexplainedObservation
    {
        public string Key { get; set; } = string.Empty;
        public string Label { get; set; } = string.Empty;
        public RuleTarget Target { get; set; } = new RuleTarget();

        /// <summary>Varyant adı → değer (metin).</summary>
        public Dictionary<string, string> Values { get; set; } = new Dictionary<string, string>();
    }

    public sealed class ChangedValue
    {
        public string Key { get; set; } = string.Empty;
        public string Label { get; set; } = string.Empty;
        public string Summary { get; set; } = string.Empty;
    }

    public sealed class InferenceReport
    {
        public int SampleCount { get; set; }
        public int ObservationCount { get; set; }

        /// <summary>Tüm varyantlarda aynı kalan (kurala gerek olmayan) gözlem sayısı.</summary>
        public int ConstantCount { get; set; }

        public List<InputDefinition> Inputs { get; set; } = new List<InputDefinition>();

        /// <summary>Ortak eşikler ve ara değerler için çıkarılan değişkenler.</summary>
        public List<VariableDefinition> Variables { get; set; } = new List<VariableDefinition>();

        public List<Rule> Rules { get; set; } = new List<Rule>();
        public List<UnexplainedObservation> Unexplained { get; set; } = new List<UnexplainedObservation>();

        /// <summary>Kullanıcıya sorulması gereken belirsizlikler (YZ sohbetinde kullanılır).</summary>
        public List<string> Questions { get; set; } = new List<string>();

        public List<string> Notes { get; set; } = new List<string>();

        /// <summary>Kör testte (girdi tablosu yokken) modelden tahmin edilen girdiler.</summary>
        public List<DetectedDriver> Drivers { get; set; } = new List<DetectedDriver>();

        /// <summary>Varyantlar arasında değişen tüm değerlerin özeti ("ne değişti?").</summary>
        public List<ChangedValue> Changes { get; set; } = new List<ChangedValue>();

        public RuleSet ToRuleSet(string name, string masterAssembly)
        {
            return new RuleSet
            {
                Name = name,
                MasterAssembly = masterAssembly,
                Description = $"{SampleCount} varyanttan otomatik çıkarıldı.",
                Inputs = Inputs,
                Variables = Variables,
                Rules = Rules,
            };
        }

        public string ToText()
        {
            var sb = new StringBuilder();
            sb.AppendLine($"Varyant: {SampleCount}, gözlem: {ObservationCount}, sabit: {ConstantCount}, " +
                          $"kural önerisi: {Rules.Count}, açıklanamayan: {Unexplained.Count}");
            if (Drivers.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("TAHMİNİ GİRDİLER (kör test: girdi tablosu verilmedi, modelden bulundu)");
                foreach (var d in Drivers)
                {
                    sb.AppendLine($"  {d.Name}  ←  {d.Label}   ({d.Explains} değeri açıklıyor)");
                    if (d.Equivalents.Count > 0)
                        sb.AppendLine($"      birebir aynı değişenler: {string.Join(", ", d.Equivalents.Take(5))}{(d.Equivalents.Count > 5 ? $" … (+{d.Equivalents.Count - 5})" : "")}");
                }
            }
            sb.AppendLine();
            sb.AppendLine("GİRDİLER");
            foreach (var i in Inputs)
            {
                var range = i.Type == InputType.Number ? $" [{Fmt(i.Min)} … {Fmt(i.Max)}]" :
                    i.Type == InputType.Choice ? $" {{{string.Join(", ", i.Options)}}}" : string.Empty;
                sb.AppendLine($"  {i.Name} ({i.Type}){range}");
            }
            if (Variables.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("DEĞİŞKENLER");
                foreach (var v in Variables)
                {
                    sb.AppendLine($"  {v.Name} = {v.Expression}");
                    if (!string.IsNullOrEmpty(v.Description)) sb.AppendLine($"         ({v.Description})");
                }
            }
            sb.AppendLine();
            sb.AppendLine("ÖNERİLEN KURALLAR");
            foreach (var r in Rules.OrderByDescending(r => r.Confidence))
            {
                sb.AppendLine($"  [{(r.Confidence ?? 0).ToString("P0", CultureInfo.InvariantCulture),4}] {r.Target}");
                sb.AppendLine($"         = {r.Expression}");
                if (!string.IsNullOrEmpty(r.Evidence)) sb.AppendLine($"         ({r.Evidence})");
            }
            if (Unexplained.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("AÇIKLANAMAYAN DEĞİŞİMLER");
                foreach (var u in Unexplained)
                    sb.AppendLine($"  {u.Label}: {string.Join(", ", u.Values.Select(kv => kv.Key + "=" + kv.Value))}");
            }
            if (Questions.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("SORULAR");
                foreach (var q in Questions) sb.AppendLine("  - " + q);
            }
            if (Changes.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine($"DEĞİŞEN DEĞERLER ({Changes.Count})");
                foreach (var c in Changes) sb.AppendLine($"  {c.Label}: {c.Summary}");
            }
            foreach (var n in Notes) sb.AppendLine("Not: " + n);
            return sb.ToString();
        }

        private static string Fmt(double? d) => d.HasValue ? Value.FormatNumber(d.Value) : "?";
    }

    /// <summary>Varyantlardan kural çıkarımı (deterministik). Çıktı her zaman "Proposed" durumundadır.</summary>
    public static class RuleInferencer
    {
        public static InferenceReport Infer(IReadOnlyList<VariantSample> samples, InferenceOptions? options = null,
            ModelSnapshot? master = null)
        {
            options = options ?? new InferenceOptions();
            var report = new InferenceReport { SampleCount = samples.Count };
            if (samples.Count < 2)
            {
                report.Notes.Add("Kural çıkarmak için en az 2 (tercihen 8+) varyant gerekir.");
                return report;
            }

            var extractor = new ObservationExtractor(options);
            var snapshots = samples.Select(s => s.Snapshot).ToList();
            if (options.MatchByStructure ?? (options.NamePattern == null && StructureMatcher.IsNeeded(snapshots)))
            {
                var unmatched = extractor.UseStructure(master ?? snapshots[0], samples);
                report.Notes.Add("Dosya adları varyantlar arasında farklı; parçalar montaj yapısına göre eşleştirildi. Adlar " +
                                 (master != null ? "master modelden" : $"ilk varyanttan ({samples[0].Name})") + " alındı." +
                                 (unmatched > 0 ? $" {unmatched} bileşen eşlenemedi." : string.Empty));
            }
            var observations = extractor.Extract(samples);
            report.ObservationCount = observations.Count;
            if (extractor.SkippedCalculated.Count > 0)
                report.Notes.Add($"SolidWorks'ün hesapladığı özellikler kural dışı bırakıldı: {string.Join(", ", extractor.SkippedCalculated)}.");

            // Girdi tablosu yoksa modeldeki gözlemleri girdi olarak kullan.
            var inputObsKeys = new HashSet<string>(options.InputObservations.Values, StringComparer.OrdinalIgnoreCase);
            var inputSources = new Dictionary<string, string>(options.InputObservations, StringComparer.OrdinalIgnoreCase);
            foreach (var kv in options.InputObservations)
            {
                var obs = observations.FirstOrDefault(o => string.Equals(o.Key, kv.Value, StringComparison.OrdinalIgnoreCase));
                if (obs == null)
                {
                    report.Notes.Add($"Girdi gözlemi bulunamadı: {kv.Value}");
                    continue;
                }
                foreach (var s in samples)
                    if (obs.Values.TryGetValue(s.Name, out var v))
                        s.Inputs[kv.Key] = v;
            }

            report.Changes = observations
                .Where(o => o.Values.Values.Distinct().Count() > 1)
                .Select(o => new ChangedValue { Key = o.Key, Label = o.Label, Summary = Summarize(o, samples.Count) })
                .ToList();

            // Kör test: hiç girdi verilmediyse, değerleri en iyi açıklayan gözlemleri girdi say.
            if (options.InputObservations.Count == 0 && samples.All(s => s.Inputs.Count == 0))
            {
                report.Drivers = DriverDetector.Detect(observations, samples, options.Tolerance);
                foreach (var d in report.Drivers)
                {
                    inputObsKeys.Add(d.ObservationKey);
                    inputSources[d.Name] = d.ObservationKey;
                    var obs = observations.First(o => o.Key == d.ObservationKey);
                    foreach (var s in samples) s.Inputs[d.Name] = obs.Values[s.Name];
                }
                if (report.Drivers.Count > 0)
                    report.Notes.Add("Girdi tablosu verilmediği için girdiler modelden tahmin edildi. Adlar modeldeki ölçü/özellik adlarıdır; " +
                                     "DriveWorks'teki gerçek girdilerle karşılaştırın.");
            }

            var columns = BuildInputColumns(samples, master, report);
            if (columns.Count == 0)
            {
                report.Notes.Add("Hiç girdi yok. CSV girdi tablosu verin veya --input Ad=gözlem ile modeldeki bir ölçüyü girdi seçin.");
                return report;
            }

            var finder = new RelationFinder(columns, options.Tolerance);
            var usedIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var chosen = new List<(Observation obs, Rule rule, RelationCandidate candidate)>();
            foreach (var obs in observations)
            {
                if (inputObsKeys.Contains(obs.Key)) continue;
                var distinct = obs.Values.Values.Distinct().Count();
                if (distinct <= 1 && obs.Values.Count == samples.Count)
                {
                    report.ConstantCount++;
                    continue;
                }
                if (distinct <= 1) continue; // sadece bazı varyantlarda var ve değişmiyor

                var candidates = finder.Find(obs, obs.Target.ExpectedType);
                var best = candidates.FirstOrDefault();
                if (best == null)
                {
                    report.Unexplained.Add(new UnexplainedObservation
                    {
                        Key = obs.Key,
                        Label = obs.Label,
                        Target = obs.Target,
                        Values = obs.Values.ToDictionary(kv => kv.Key, kv => kv.Value.AsText()),
                    });
                    continue;
                }

                var alternatives = candidates.Skip(1).Where(c => c.Confidence >= 0.5).Take(2).Select(c => c.Expression).ToList();
                var target = MapToMaster(obs.Target, master, extractor);
                var rule = new Rule
                {
                    Id = UniqueId(MakeId(target), usedIds),
                    Description = obs.Label,
                    Target = target,
                    Expression = best.Expression,
                    Status = RuleStatus.Proposed,
                    Source = RuleSource.Inference,
                    Confidence = best.Confidence,
                    Evidence = best.Evidence + (alternatives.Count > 0
                        ? $" Diğer aday: {string.Join(" | ", alternatives)}"
                        : string.Empty),
                };
                report.Rules.Add(rule);
                chosen.Add((obs, rule, best));
            }

            // Girdi olarak seçilen model değerlerinin kendisi de üretimde yazılmalı (ör. kapak dosyası seçimi,
            // "Assembly Height" özelliği). Girdi tablosu verildiğinde bu değerler zaten ayrı gözlemdir.
            foreach (var kv in inputSources)
            {
                var obs = observations.FirstOrDefault(o => string.Equals(o.Key, kv.Value, StringComparison.OrdinalIgnoreCase));
                if (obs == null) continue;
                var target = MapToMaster(obs.Target, master, extractor);
                report.Rules.Add(new Rule
                {
                    Id = UniqueId(MakeId(target), usedIds),
                    Description = obs.Label,
                    Target = target,
                    Expression = SanitizeName(kv.Key),
                    Status = RuleStatus.Proposed,
                    Source = RuleSource.Inference,
                    Confidence = 0.99,
                    Evidence = "Bu değer girdinin kendisi.",
                });
            }
            foreach (var d in report.Drivers.Where(d => d.Question != null))
                report.Questions.Add(d.Question!);

            var questionRules = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            var handled = ShareThresholds(chosen, report, usedIds);
            DeriveFromOtherValues(observations, chosen, report, samples, options.Tolerance, master, extractor, usedIds, handled);
            DerivePitches(observations, chosen, report, columns, master, extractor, usedIds, handled);

            foreach (var (_, rule, candidate) in chosen)
            {
                if (candidate.Question == null || handled.Contains(rule.Id)) continue;
                if (!questionRules.TryGetValue(candidate.Question, out var ids)) questionRules[candidate.Question] = ids = new List<string>();
                ids.Add(rule.Id);
            }
            foreach (var q in questionRules)
                report.Questions.Add(q.Value.Count <= 3
                    ? $"{string.Join(", ", q.Value)}: {q.Key}"
                    : $"{q.Value.Count} kural ({string.Join(", ", q.Value.Take(2))} …): {q.Key}");

            if (samples.Count < 6)
                report.Notes.Add($"Sadece {samples.Count} varyant var; güven düşük. Girdi aralığının uçlarını kapsayan 8–15 varyant önerilir.");
            return report;
        }

        /// <summary>
        /// Aynı girdiye ve örtüşen aralığa sahip eşikli kuralları tek bir eşik değişkenine bağlar
        /// (DriveWorks'teki ShelfQty değişkeni gibi). Kullanıcı eşiği tek yerden düzeltir.
        /// Dönüş: sorusu ortak soruya taşınan kural kimlikleri.
        /// </summary>
        private static HashSet<string> ShareThresholds(List<(Observation obs, Rule rule, RelationCandidate candidate)> chosen,
            InferenceReport report, HashSet<string> usedIds)
        {
            var handled = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var withThreshold = chosen.Where(c => c.candidate.ThresholdInput != null && c.candidate.Template != null).ToList();
            foreach (var byInput in withThreshold.GroupBy(c => c.candidate.ThresholdInput!))
            {
                // Örtüşen aralıkları kümele
                var clusters = new List<(double lo, double hi, List<(Observation obs, Rule rule, RelationCandidate candidate)> items)>();
                foreach (var item in byInput.OrderBy(c => c.candidate.ThresholdLo))
                {
                    var c = item.candidate;
                    int idx = clusters.FindIndex(k => c.ThresholdLo < k.hi && c.ThresholdHi > k.lo);
                    if (idx < 0) clusters.Add((c.ThresholdLo, c.ThresholdHi, new List<(Observation, Rule, RelationCandidate)> { item }));
                    else
                    {
                        var k = clusters[idx];
                        k.items.Add(item);
                        clusters[idx] = (Math.Max(k.lo, c.ThresholdLo), Math.Min(k.hi, c.ThresholdHi), k.items);
                    }
                }

                foreach (var cluster in clusters.Where(k => k.items.Count >= 2))
                {
                    var t = NumberUtil.RoundestBetween(cluster.lo, cluster.hi);
                    var name = UniqueId(SanitizeName(byInput.Key + "_Esigi"), usedIds);
                    report.Variables.Add(new VariableDefinition
                    {
                        Name = name,
                        Expression = NumberUtil.Fmt(t),
                        Description = $"{byInput.Key} eşiği. Veriler {NumberUtil.Fmt(cluster.lo)} ile {NumberUtil.Fmt(cluster.hi)} " +
                                      $"arasındaki her değeri destekliyor; {cluster.items.Count} kural bu değişkeni kullanıyor.",
                    });
                    foreach (var item in cluster.items)
                    {
                        item.rule.Expression = item.candidate.Template!.Replace("{T}", name);
                        handled.Add(item.rule.Id);
                    }
                    report.Questions.Add(
                        $"{byInput.Key} için {cluster.items.Count} kural aynı eşiği kullanıyor ({string.Join(", ", cluster.items.Take(4).Select(i => i.rule.Id))}" +
                        $"{(cluster.items.Count > 4 ? " …" : "")}). Veriler {NumberUtil.Fmt(cluster.lo)} ile {NumberUtil.Fmt(cluster.hi)} arasındaki her değeri " +
                        $"destekliyor; şimdilik {NumberUtil.Fmt(t)} seçildi. Gerçek eşik nedir? ('{name}' değişkenini değiştirmek hepsini düzeltir.)");
                }
            }
            return handled;
        }

        /// <summary>
        /// Girdilerle açıklanamayan (ya da zayıf açıklanan) bir değer, kuralı bilinen başka bir değerle doğrusal
        /// ilişkili olabilir (ör. ikinci pim deliği = 2 × birinci pim deliği − 35). Bu durumda bilinen değerin
        /// formülü bir ara değişken yapılır ve yeni kural onu kullanır.
        /// </summary>
        private static void DeriveFromOtherValues(List<Observation> observations,
            List<(Observation obs, Rule rule, RelationCandidate candidate)> chosen, InferenceReport report,
            IReadOnlyList<VariantSample> samples, double tolerance, ModelSnapshot? master, ObservationExtractor extractor,
            HashSet<string> usedIds, HashSet<string> handled)
        {
            var anchors = chosen
                .Where(c => c.candidate.Confidence >= 0.9 && c.obs.Values.Values.All(v => v.Kind == ValueKind.Number) &&
                            c.obs.Values.Count == samples.Count && c.rule.Expression.IndexOf('(') >= 0) // basit "Boy - 40" zaten girdiye bağlı
                .ToList();
            if (anchors.Count == 0) return;

            var weak = new List<(Observation obs, int ruleIndex)>();
            foreach (var u in report.Unexplained.ToList())
            {
                var obs = observations.FirstOrDefault(o => o.Key == u.Key);
                if (obs != null) weak.Add((obs, -1));
            }
            for (int i = 0; i < chosen.Count; i++)
                if (chosen[i].candidate.Confidence < 0.5) weak.Add((chosen[i].obs, i));

            var variableFor = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (obs, ruleIndex) in weak)
            {
                if (!obs.Values.Values.All(v => v.Kind == ValueKind.Number)) continue;
                var names = obs.Values.Keys.ToList();
                var ys = names.Select(n => obs.Values[n].AsNumber()).ToArray();
                foreach (var anchor in anchors)
                {
                    if (anchor.obs.Key == obs.Key) continue;
                    var xs = names.Select(n => anchor.obs.Values[n].AsNumber()).ToArray();
                    if (xs.Distinct().Count() < 4) continue;
                    var fit = RelationFinder.ExactLinear(xs, ys, tolerance);
                    if (fit == null || Math.Abs(fit.Value.a) < 1e-9) continue;

                    if (!variableFor.TryGetValue(anchor.rule.Id, out var varName))
                    {
                        varName = UniqueId(DriverDetector.DriverName(anchor.obs), usedIds);
                        variableFor[anchor.rule.Id] = varName;
                        report.Variables.Add(new VariableDefinition
                        {
                            Name = varName,
                            Expression = anchor.rule.Expression,
                            Description = $"Ara değer: {anchor.obs.Label} (başka bir değer buna bağlı).",
                        });
                    }

                    var expression = NumberUtil.Linear(new[] { (fit.Value.a, varName) }, fit.Value.b);
                    var evidence = $"{xs.Length} varyantta {anchor.obs.Label} değeriyle doğrusal ilişki tam uyuyor.";
                    if (ruleIndex >= 0)
                    {
                        var rule = chosen[ruleIndex].rule;
                        rule.Expression = expression;
                        rule.Confidence = NumberUtil.Confidence(xs.Distinct().Count(), 2);
                        rule.Evidence = evidence;
                        handled.Add(rule.Id);
                    }
                    else
                    {
                        var target = MapToMaster(obs.Target, master, extractor);
                        report.Rules.Add(new Rule
                        {
                            Id = UniqueId(MakeId(target), usedIds),
                            Description = obs.Label,
                            Target = target,
                            Expression = expression,
                            Status = RuleStatus.Proposed,
                            Source = RuleSource.Inference,
                            Confidence = NumberUtil.Confidence(xs.Distinct().Count(), 2),
                            Evidence = evidence,
                        });
                        report.Unexplained.RemoveAll(u => u.Key == obs.Key);
                    }
                    break;
                }
            }
        }

        /// <summary>
        /// Eşit aralık formülü: aralık = ROUND((uzunluk + b) / (adet + c), basamak). Desen/delik aralıklarında çok yaygındır
        /// (ör. çivi aralığı = (Genişlik − 18) / (çivi adedi − 1)). Adet, kuralı bulunmuş başka bir değerdir.
        /// </summary>
        private static void DerivePitches(List<Observation> observations,
            List<(Observation obs, Rule rule, RelationCandidate candidate)> chosen, InferenceReport report,
            List<InputColumn> columns, ModelSnapshot? master, ObservationExtractor extractor, HashSet<string> usedIds,
            HashSet<string> handled)
        {
            var counts = chosen
                .Where(c => c.candidate.Confidence >= 0.5 && c.obs.Values.Values.All(v => v.Kind == ValueKind.Number &&
                            Math.Abs(v.AsNumber() - Math.Round(v.AsNumber())) < 1e-9))
                .ToList();
            var numericInputs = columns.Where(c => c.Kind == ColumnKind.Number).ToList();
            if (counts.Count == 0 || numericInputs.Count == 0) return;

            foreach (var u in report.Unexplained.ToList())
            {
                var obs = observations.FirstOrDefault(o => o.Key == u.Key);
                if (obs == null || !obs.Values.Values.All(v => v.Kind == ValueKind.Number)) continue;
                bool done = false;
                foreach (var count in counts)
                {
                    if (done) break;
                    // Her adet adayı için ortak varyantlar ayrı hesaplanır (bazı değerler sadece bazı varyantlarda var).
                    var names = obs.Values.Keys.Where(n => count.obs.Values.ContainsKey(n) && numericInputs.All(x => x.Values.ContainsKey(n))).ToList();
                    var ys = names.Select(n => obs.Values[n].AsNumber()).ToArray();
                    if (ys.Distinct().Count() < 4) continue;
                    int digits = ys.Max(v => Decimals(v));
                    foreach (var c in new[] { -1.0, 0.0, 1.0 })
                    {
                        if (done) break;
                        var zs = names.Select(n => count.obs.Values[n].AsNumber() + c).ToArray();
                        if (zs.Any(z => Math.Abs(z) < 1e-9)) continue;
                        var ks = ys.Select((v, i) => v * zs[i]).ToArray();
                        // y yuvarlanmış olduğu için tolerans adet ile büyür
                        double tol = 0.5 * Math.Pow(10, -digits) * zs.Max(Math.Abs) + 1e-6;
                        foreach (var x in numericInputs)
                        {
                            var xs = names.Select(n => x.Values[n].AsNumber()).ToArray();
                            var fit = RelationFinder.ExactLinear(xs, ks, tol);
                            if (fit == null || Math.Abs(fit.Value.a) < 1e-9) continue;
                            // Kontrol: formül her varyantta y'yi aynı basamağa yuvarlanmış olarak vermeli.
                            bool ok = true;
                            for (int i = 0; i < ys.Length && ok; i++)
                                ok = Math.Abs(Math.Round((fit.Value.a * xs[i] + fit.Value.b) / zs[i], digits, MidpointRounding.AwayFromZero) - ys[i]) < 1e-6;
                            if (!ok) continue;

                            // Çapraz kanıt: aralık formülündeki uzunluk (ör. Genişlik - 18) adet formülünde belirsiz kalan
                            // sabiti belirler, çünkü ikisi aynı uzunluğu böler.
                            var cand = count.candidate;
                            if (cand.WithOffset != null && cand.OffsetInput == x.Name && Math.Abs(fit.Value.a - 1) < 1e-9 &&
                                fit.Value.b >= cand.OffsetLo - 1e-9 && fit.Value.b <= cand.OffsetHi + 1e-9)
                            {
                                var oldExpr = count.rule.Expression;
                                var newExpr = cand.WithOffset(fit.Value.b);
                                foreach (var other in chosen.Where(o => o.rule.Expression == oldExpr))
                                {
                                    other.rule.Expression = newExpr;
                                    other.rule.Evidence += $" Sabit, aynı uzunluğu bölen aralık formülüyle kesinleşti ({NumberUtil.Linear(new[] { (1.0, x.Name) }, fit.Value.b)}).";
                                    other.rule.Confidence = Math.Max(other.rule.Confidence ?? 0, 0.9);
                                    handled.Add(other.rule.Id);
                                }
                                foreach (var v in report.Variables.Where(v => v.Expression == oldExpr)) v.Expression = newExpr;
                            }

                            var varName = report.Variables.FirstOrDefault(v => v.Expression == count.rule.Expression)?.Name;
                            if (varName == null)
                            {
                                varName = UniqueId(DriverDetector.DriverName(count.obs), usedIds);
                                report.Variables.Add(new VariableDefinition
                                {
                                    Name = varName,
                                    Expression = count.rule.Expression,
                                    Description = $"Adet: {count.obs.Label} (aralık formülleri buna bağlı).",
                                });
                            }
                            var denominator = Math.Abs(c) < 1e-9 ? varName : $"({varName} {(c < 0 ? "-" : "+")} {NumberUtil.Fmt(Math.Abs(c))})";
                            var numerator = NumberUtil.Linear(new[] { (fit.Value.a, x.Name) }, fit.Value.b);
                            var target = MapToMaster(obs.Target, master, extractor);
                            report.Rules.Add(new Rule
                            {
                                Id = UniqueId(MakeId(target), usedIds),
                                Description = obs.Label,
                                Target = target,
                                Expression = $"ROUND(({numerator}) / {denominator}, {digits})",
                                Status = RuleStatus.Proposed,
                                Source = RuleSource.Inference,
                                Confidence = NumberUtil.Confidence(xs.Distinct().Count(), 3),
                                Evidence = $"{ys.Length} varyantta eşit aralık formülü tam uyuyor: aralık = ({numerator}) / ({count.obs.Label} {(c < 0 ? "-" : "+")} {NumberUtil.Fmt(Math.Abs(c))}).",
                            });
                            report.Unexplained.RemoveAll(e => e.Key == obs.Key);
                            done = true;
                            break;
                        }
                    }
                }
            }
        }

        private static int Decimals(double v)
        {
            for (int d = 0; d <= 6; d++)
                if (Math.Abs(v - Math.Round(v, d)) < 1e-9) return d;
            return 6;
        }

        private static string Summarize(Observation o, int sampleCount)
        {
            var vals = o.Values.Values.ToList();
            var missing = sampleCount - vals.Count;
            var tail = missing > 0 ? $" ({missing} varyantta yok)" : string.Empty;
            if (vals.All(v => v.Kind == ValueKind.Number))
            {
                var nums = vals.Select(v => v.AsNumber()).ToList();
                return $"{Value.FormatNumber(nums.Min())} … {Value.FormatNumber(nums.Max())} ({nums.Distinct().Count()} farklı değer){tail}";
            }
            if (vals.All(v => v.Kind == ValueKind.Bool))
                return $"{vals.Count(v => v.AsBool())} varyantta evet, {vals.Count(v => !v.AsBool())} varyantta hayır{tail}";
            var distinct = vals.Select(v => v.AsText()).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            return distinct.Count <= 6
                ? string.Join(" / ", distinct.Select(d => "\"" + d + "\"")) + tail
                : $"{distinct.Count} farklı metin, ör. \"{distinct[0]}\"{tail}";
        }

        private static List<InputColumn> BuildInputColumns(IReadOnlyList<VariantSample> samples, ModelSnapshot? master,
            InferenceReport report)
        {
            var names = samples.SelectMany(s => s.Inputs.Keys).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var columns = new List<InputColumn>();
            foreach (var name in names)
            {
                var values = new Dictionary<string, Value>(StringComparer.OrdinalIgnoreCase);
                foreach (var s in samples)
                    if (s.Inputs.TryGetValue(name, out var v) && !v.IsNull)
                        values[s.Name] = v;
                if (values.Count < samples.Count)
                    report.Notes.Add($"'{name}' girdisi bazı varyantlarda eksik; o varyantlar bu girdiyle ilgili aramalarda yok sayılır.");

                var safeName = SanitizeName(name);
                var vals = values.Values.ToList();
                InputDefinition def;
                ColumnKind kind;
                if (vals.All(v => v.Kind == ValueKind.Number))
                {
                    kind = ColumnKind.Number;
                    var nums = vals.Select(v => v.AsNumber()).OrderBy(d => d).ToList();
                    def = new InputDefinition
                    {
                        Name = safeName, Label = name, Type = InputType.Number,
                        Min = nums.First(), Max = nums.Last(), Default = Value.FormatNumber(nums[nums.Count / 2]),
                        Description = $"Varyantlarda görülen aralık: {Value.FormatNumber(nums.First())} – {Value.FormatNumber(nums.Last())}",
                    };
                }
                else if (vals.All(v => v.Kind == ValueKind.Bool))
                {
                    kind = ColumnKind.Bool;
                    def = new InputDefinition { Name = safeName, Label = name, Type = InputType.Bool, Default = "false" };
                }
                else
                {
                    kind = ColumnKind.Category;
                    var options = vals.Select(v => v.AsText()).Distinct(StringComparer.OrdinalIgnoreCase)
                        .OrderBy(o => o, StringComparer.OrdinalIgnoreCase).ToList();
                    def = new InputDefinition
                    {
                        Name = safeName, Label = name,
                        Type = options.Count <= 30 ? InputType.Choice : InputType.Text,
                        Options = options.Count <= 30 ? options : new List<string>(),
                        Default = options.First(),
                    };
                }
                report.Inputs.Add(def);
                columns.Add(new InputColumn(safeName, kind, values));
            }
            return columns;
        }

        private static RuleTarget MapToMaster(RuleTarget target, ModelSnapshot? master, ObservationExtractor extractor)
        {
            if (master == null) return target;
            var t = new RuleTarget { Kind = target.Kind, Document = target.Document, Name = target.Name, Component = target.Component };
            if (t.Document != null)
            {
                var doc = master.Documents.FirstOrDefault(d =>
                    string.Equals(extractor.NormalizeDocumentKey(d.Key), t.Document, StringComparison.OrdinalIgnoreCase));
                if (doc != null) t.Document = doc.Key;
            }
            if (t.Component != null)
            {
                var comp = master.Components.FirstOrDefault(c =>
                    string.Equals(extractor.NormalizeComponentPath(c.Path), t.Component, StringComparison.OrdinalIgnoreCase));
                if (comp != null) t.Component = comp.Path;
            }
            return t;
        }

        private static readonly Regex NonIdentifier = new Regex(@"[^\p{L}\p{Nd}_]+", RegexOptions.Compiled);

        public static string SanitizeName(string name)
        {
            var s = NonIdentifier.Replace(name.Trim(), "_").Trim('_');
            if (s.Length == 0) s = "Girdi";
            if (char.IsDigit(s[0])) s = "_" + s;
            return s;
        }

        private static string MakeId(RuleTarget t)
        {
            string body;
            switch (t.Kind)
            {
                case TargetKind.ComponentSuppression: body = "sup_" + t.Component; break;
                case TargetKind.ComponentReplace: body = "rep_" + t.Component; break;
                case TargetKind.Configuration: body = "cfg_" + (t.Component ?? t.Document); break;
                case TargetKind.Dimension: body = "dim_" + StripExt(t.Document) + "_" + t.Name; break;
                case TargetKind.GlobalVariable: body = "gv_" + StripExt(t.Document) + "_" + t.Name; break;
                case TargetKind.FeatureSuppression: body = "feat_" + StripExt(t.Document) + "_" + t.Name; break;
                case TargetKind.CustomProperty: body = "prop_" + (t.Document == null ? "" : StripExt(t.Document) + "_") + t.Name; break;
                default: body = t.Kind.ToString(); break;
            }
            return SanitizeName(body);
        }

        private static string StripExt(string? s) => s == null ? "" : System.IO.Path.GetFileNameWithoutExtension(s);

        private static string UniqueId(string id, HashSet<string> used)
        {
            var candidate = id;
            for (int i = 2; !used.Add(candidate); i++) candidate = id + "_" + i;
            return candidate;
        }
    }
}
