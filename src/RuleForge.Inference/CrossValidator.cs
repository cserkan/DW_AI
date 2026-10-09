using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using RuleForge.Core.Engine;
using RuleForge.Core.Expressions;
using RuleForge.Core.Model;
using RuleForge.Core.Rules;

namespace RuleForge.Inference
{
    public sealed class FoldResult
    {
        public string Variant { get; set; } = string.Empty;
        public int Correct { get; set; }
        public int Wrong { get; set; }

        /// <summary>Bu varyantta değişen ama kalan varyantlardan öğrenilmiş bir kuralı olmayan değer sayısı.</summary>
        public int Missing { get; set; }

        public List<string> Errors { get; set; } = new List<string>();
        public int Total => Correct + Wrong + Missing;
    }

    public sealed class TargetStat
    {
        public string Key { get; set; } = string.Empty;
        public string Label { get; set; } = string.Empty;
        public int Correct { get; set; }
        public int Wrong { get; set; }
        public int Missing { get; set; }
        public List<string> Failures { get; set; } = new List<string>();

        /// <summary>Turlar arasında kuralın farklı yazıldığı ifadeler (kural kararsızsa birden fazla).</summary>
        public HashSet<string> Expressions { get; set; } = new HashSet<string>(StringComparer.Ordinal);

        public bool Reliable => Wrong == 0 && Missing == 0 && Expressions.Count <= 1;
    }

    public sealed class CrossValidationReport
    {
        public List<FoldResult> Folds { get; set; } = new List<FoldResult>();
        public List<TargetStat> Targets { get; set; } = new List<TargetStat>();
        public List<string> Notes { get; set; } = new List<string>();

        /// <summary>Hiçbir turda kuralı çıkarılamayan değerler (ör. sipariş no): doğruluk oranına katılmaz.</summary>
        public List<string> Unexplainable { get; set; } = new List<string>();

        public int Correct => Folds.Sum(f => f.Correct);
        public int Wrong => Folds.Sum(f => f.Wrong);
        public int Missing => Folds.Sum(f => f.Missing);
        public double Accuracy => Correct + Wrong + Missing == 0 ? 0 : (double)Correct / (Correct + Wrong + Missing);

        public string ToText()
        {
            var sb = new StringBuilder();
            sb.AppendLine("ÇAPRAZ DOĞRULAMA — her varyant sırayla çıkarıldı, kurallar kalan varyantlardan öğrenildi,");
            sb.AppendLine("sonra çıkarılan varyantın değerleri tahmin edildi ve gerçek değerlerle karşılaştırıldı.");
            sb.AppendLine("Cevap anahtarı gerekmez; her montaj setinde çalışır.");
            sb.AppendLine();
            sb.AppendLine($"{"Varyant",-44} {"doğru",6} {"yanlış",7} {"kuralsız",9}");
            foreach (var f in Folds)
                sb.AppendLine($"{Trim(f.Variant, 44),-44} {f.Correct,6} {f.Wrong,7} {f.Missing,9}");
            sb.AppendLine();
            sb.AppendLine($"GENEL: {Correct}/{Correct + Wrong + Missing} değer doğru tahmin edildi (%{Accuracy * 100:0.0}); " +
                          $"{Wrong} yanlış, {Missing} değer için kural çıkarılamamıştı.");
            if (Unexplainable.Count > 0)
                sb.AppendLine($"Bu oran dışında: {Unexplainable.Count} değerin kuralı hiçbir turda çıkarılamadı (girdilerden hesaplanamıyor olabilir: " +
                              $"{string.Join(", ", Unexplainable.Take(4))}{(Unexplainable.Count > 4 ? " …" : "")}).");

            var reliable = Targets.Count(t => t.Reliable);
            sb.AppendLine($"Her turda doğru ve kararlı kural: {reliable}/{Targets.Count}");

            var risky = Targets.Where(t => !t.Reliable).OrderByDescending(t => t.Wrong).ThenByDescending(t => t.Missing).ToList();
            if (risky.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine($"RİSKLİ DEĞERLER ({risky.Count}) — bu kurallar yeni bir girdide yanlış çıkabilir:");
                foreach (var t in risky)
                {
                    var parts = new List<string>();
                    if (t.Wrong > 0) parts.Add($"{t.Wrong} tahminde yanlış");
                    if (t.Missing > 0) parts.Add($"{t.Missing} tahminde kural yok");
                    if (t.Expressions.Count > 1) parts.Add($"{t.Expressions.Count} farklı formül");
                    sb.AppendLine($"  {t.Label}: {string.Join(", ", parts)}");
                    foreach (var fail in t.Failures.Take(2)) sb.AppendLine($"      {fail}");
                }
            }
            foreach (var f in Folds.Where(f => f.Errors.Count > 0))
                foreach (var e in f.Errors.Take(3)) sb.AppendLine($"Not ({f.Variant}): {e}");
            foreach (var n in Notes) sb.AppendLine("Not: " + n);
            return sb.ToString();
        }

        private static string Trim(string s, int n) => s.Length <= n ? s : s.Substring(0, n - 1) + "…";
    }

    /// <summary>
    /// Leave-one-out çapraz doğrulama: kuralların yeni (görülmemiş) girdilerde ne kadar işe yaradığını
    /// DriveWorks projesi ya da cevap anahtarı olmadan ölçer.
    /// </summary>
    public static class CrossValidator
    {
        public static CrossValidationReport Run(IReadOnlyList<VariantSample> samples, InferenceOptions? options = null,
            ModelSnapshot? master = null, Action<int, int>? progress = null)
        {
            options = options ?? new InferenceOptions();
            var report = new CrossValidationReport();
            if (samples.Count < 4)
            {
                report.Notes.Add("Çapraz doğrulama için en az 4 varyant gerekir.");
                return report;
            }

            // Varyantlar tekrarlanan bir modülün kopyalarını içeriyorsa: her varyant (tüm kopyalarıyla) sırayla çıkarılır.
            var split = options.DetectModules ? ModuleDetector.Detect(samples, master, options) : null;
            if (split != null) return RunModules(samples, split, options, master, progress, report);

            var snapshots = samples.Select(s => s.Snapshot).ToList();
            bool structure = options.MatchByStructure ?? (options.NamePattern == null && StructureMatcher.IsNeeded(snapshots));
            var reference = master ?? snapshots[0];
            var foldOptions = options.Clone();
            foldOptions.MatchByStructure = structure;

            // Tüm varyantların gözlemleri, her turda aynı anahtarlarla karşılaştırılabilsin diye bir kez çıkarılır.
            var extractor = new ObservationExtractor(foldOptions);
            if (structure) extractor.UseStructure(reference, samples);
            var observations = extractor.Extract(samples);
            var byKey = observations.ToDictionary(o => o.Key, StringComparer.OrdinalIgnoreCase);
            var targetKey = observations.ToDictionary(o => o.Key,
                o => RuleInferencer.MapToMaster(o.Target, reference, extractor).Key, StringComparer.OrdinalIgnoreCase);
            var varying = observations.Where(o => o.Values.Values.Distinct().Count() > 1).ToList();

            var log = new List<(FoldResult fold, string key, int status)>(); // 0 doğru, 1 yanlış, 2 kuralsız
            var originalInputs = samples.ToDictionary(s => s.Name, s => new Dictionary<string, Value>(s.Inputs, StringComparer.OrdinalIgnoreCase));
            var stats = new Dictionary<string, TargetStat>(StringComparer.OrdinalIgnoreCase);

            for (int i = 0; i < samples.Count; i++)
            {
                progress?.Invoke(i + 1, samples.Count);
                var held = samples[i];
                var train = samples.Where((_, k) => k != i)
                    .Select(s => new VariantSample(s.Name, s.Snapshot, originalInputs[s.Name])).ToList();
                var rep = RuleInferencer.Infer(train, foldOptions.Clone(), reference);
                var fold = new FoldResult { Variant = held.Name };
                report.Folds.Add(fold);

                // Çıkarılan varyantın girdileri
                var heldInputs = new Dictionary<string, Value>(StringComparer.OrdinalIgnoreCase);
                foreach (var d in rep.Drivers)
                    if (byKey.TryGetValue(d.ObservationKey, out var dobs) && dobs.Values.TryGetValue(held.Name, out var dv))
                        heldInputs[d.Name] = dv;
                foreach (var kv in options.InputObservations)
                    if (byKey.TryGetValue(kv.Value, out var iobs) && iobs.Values.TryGetValue(held.Name, out var iv))
                        heldInputs[RuleInferencer.SanitizeName(kv.Key)] = iv;
                foreach (var kv in originalInputs[held.Name])
                    heldInputs[RuleInferencer.SanitizeName(kv.Key)] = kv.Value;

                var ruleSet = rep.ToRuleSet("cv", string.Empty);
                foreach (var input in ruleSet.Inputs)
                {
                    // Çıkarılan varyant eğitim aralığının dışında olabilir (ekstrapolasyon): sınırları kaldır.
                    input.Min = null;
                    input.Max = null;
                    if (input.Type == InputType.Choice && heldInputs.TryGetValue(input.Name, out var hv) &&
                        !input.Options.Contains(hv.AsText(), StringComparer.OrdinalIgnoreCase))
                        input.Options.Add(hv.AsText());
                }
                var result = RuleEngine.Evaluate(ruleSet, heldInputs, new EvaluationOptions { IncludeProposed = true });
                foreach (var e in result.Errors) fold.Errors.Add(e);

                var predicted = new Dictionary<string, ModelAction>(StringComparer.OrdinalIgnoreCase);
                foreach (var a in result.Actions)
                    if (!predicted.ContainsKey(a.Target.Key)) predicted[a.Target.Key] = a;

                var driverKeys = new HashSet<string>(rep.Drivers.Select(d => d.ObservationKey).Concat(options.InputObservations.Values),
                    StringComparer.OrdinalIgnoreCase);
                var inputNames = new HashSet<string>(originalInputs[held.Name].Keys, StringComparer.OrdinalIgnoreCase);

                foreach (var obs in varying)
                {
                    if (driverKeys.Contains(obs.Key) || !obs.Values.TryGetValue(held.Name, out var actual)) continue;
                    var key = targetKey[obs.Key];
                    if (!stats.TryGetValue(key, out var stat))
                        stats[key] = stat = new TargetStat { Key = key, Label = obs.Label };

                    if (!predicted.TryGetValue(key, out var action))
                    {
                        log.Add((fold, key, 2));
                        stat.Missing++;
                        stat.Failures.Add($"{held.Name}: kural çıkarılamamıştı (gerçek değer {actual.AsText()})");
                        continue;
                    }
                    var rule = rep.Rules.FirstOrDefault(r => r.Target.Key == key);
                    if (rule != null) stat.Expressions.Add(rule.Expression);

                    if (Same(action.Value, actual))
                    {
                        log.Add((fold, key, 0));
                        stat.Correct++;
                    }
                    else
                    {
                        log.Add((fold, key, 1));
                        stat.Wrong++;
                        stat.Failures.Add($"{held.Name}: tahmin {action.Value.AsText()}, gerçek {actual.AsText()}  (formül: {rule?.Expression})");
                    }
                }
            }

            var unexplainable = new HashSet<string>(stats.Values.Where(t => t.Correct + t.Wrong == 0).Select(t => t.Key), StringComparer.OrdinalIgnoreCase);
            foreach (var (fold, key, status) in log.Where(l => !unexplainable.Contains(l.key)))
            {
                if (status == 0) fold.Correct++;
                else if (status == 1) fold.Wrong++;
                else fold.Missing++;
            }
            report.Unexplainable = stats.Values.Where(t => unexplainable.Contains(t.Key)).Select(t => t.Label).ToList();
            report.Targets = stats.Values.Where(t => !unexplainable.Contains(t.Key)).OrderBy(t => t.Label, StringComparer.OrdinalIgnoreCase).ToList();
            return report;
        }

        private static CrossValidationReport RunModules(IReadOnlyList<VariantSample> samples, ModuleSplit split, InferenceOptions options,
            ModelSnapshot? master, Action<int, int>? progress, CrossValidationReport report)
        {
            var mo = RuleInferencer.ExtractModuleObservations(split, options, new List<string>());
            var instTarget = mo.Instance.ToDictionary(o => o.Key,
                o => RuleInferencer.MapToMaster(o.Target, split.InstanceReference, mo.InstanceExtractor).Key, StringComparer.OrdinalIgnoreCase);
            var outerTarget = mo.Outer.ToDictionary(o => o.Key,
                o => RuleInferencer.OuterTarget(o, split, mo.OuterExtractor).Key, StringComparer.OrdinalIgnoreCase);
            var instByKey = mo.Instance.ToDictionary(o => o.Key, StringComparer.OrdinalIgnoreCase);
            var outerByKey = mo.Outer.ToDictionary(o => o.Key, StringComparer.OrdinalIgnoreCase);
            var varyingInst = mo.Instance.Where(o => o.Values.Values.Distinct().Count() > 1).ToList();
            var varyingOuter = mo.Outer.Where(o => o.Values.Values.Distinct().Count() > 1).ToList();

            var originalInputs = samples.ToDictionary(s => s.Name, s => new Dictionary<string, Value>(s.Inputs, StringComparer.OrdinalIgnoreCase));
            var log = new List<(FoldResult fold, string key, int status)>(); // 0 doğru, 1 yanlış, 2 kuralsız
            var stats = new Dictionary<string, TargetStat>(StringComparer.OrdinalIgnoreCase);
            var foldOptions = options.Clone();

            for (int i = 0; i < samples.Count; i++)
            {
                progress?.Invoke(i + 1, samples.Count);
                var held = samples[i];
                var train = samples.Where((_, k) => k != i)
                    .Select(s => new VariantSample(s.Name, s.Snapshot, originalInputs[s.Name])).ToList();
                var rep = RuleInferencer.Infer(train, foldOptions.Clone(), master);
                var fold = new FoldResult { Variant = held.Name };
                report.Folds.Add(fold);
                var rows = split.InstancesOf(held.Name);

                // Çıkarılan varyantın girdileri: genel girdiler varyant düzeyindeki değerlerden, tablo sütunları her kopyadan.
                Value? VariantValue(string key)
                {
                    if (key.StartsWith(RuleInferencer.OuterPrefix, StringComparison.Ordinal))
                        return outerByKey.TryGetValue(key.Substring(RuleInferencer.OuterPrefix.Length), out var oo) &&
                               oo.Values.TryGetValue(held.Name, out var ov) ? ov : (Value?)null;
                    if (!instByKey.TryGetValue(key, out var io)) return null;
                    foreach (var r in rows)
                        if (io.Values.TryGetValue(r.Name, out var iv)) return iv;
                    return null;
                }

                var ruleSet = rep.ToRuleSet("cv", string.Empty);
                var columns = new HashSet<string>(ruleSet.Tables.SelectMany(t => t.Columns.Select(c => c.Name)), StringComparer.OrdinalIgnoreCase);
                var heldInputs = new Dictionary<string, Value>(StringComparer.OrdinalIgnoreCase);
                foreach (var d in rep.Drivers.Where(d => !columns.Contains(d.Name)))
                    if (VariantValue(d.ObservationKey) is Value dv) heldInputs[d.Name] = dv;
                foreach (var kv in originalInputs[held.Name]) heldInputs[RuleInferencer.SanitizeName(kv.Key)] = kv.Value;

                var tables = new Dictionary<string, List<Dictionary<string, Value>>>(StringComparer.OrdinalIgnoreCase);
                foreach (var t in ruleSet.Tables)
                {
                    var tableRows = rows.Select(_ => new Dictionary<string, Value>(StringComparer.OrdinalIgnoreCase)).ToList();
                    foreach (var c in t.Columns)
                    {
                        var d = rep.Drivers.FirstOrDefault(x => string.Equals(x.Name, c.Name, StringComparison.OrdinalIgnoreCase));
                        if (d == null || !instByKey.TryGetValue(d.ObservationKey, out var co)) continue;
                        for (int r = 0; r < rows.Count; r++)
                            if (co.Values.TryGetValue(rows[r].Name, out var cv)) tableRows[r][c.Name] = cv;
                    }
                    tables[t.Name] = tableRows;
                    // Çıkarılan varyant eğitim aralığının dışında olabilir: sınırları kaldır.
                    t.MinRows = null;
                    t.MaxRows = null;
                    foreach (var c in t.Columns) Relax(c, tableRows.Where(r => r.ContainsKey(c.Name)).Select(r => r[c.Name]));
                }
                foreach (var input in ruleSet.Inputs)
                    Relax(input, heldInputs.TryGetValue(input.Name, out var hv) ? new[] { hv } : new Value[0]);

                var result = RuleEngine.Evaluate(ruleSet, heldInputs, new EvaluationOptions { IncludeProposed = true }, tables);
                foreach (var e in result.Errors) fold.Errors.Add(e);
                var predicted = new Dictionary<string, ModelAction>(StringComparer.OrdinalIgnoreCase);
                foreach (var a in result.Actions)
                    if (!predicted.ContainsKey(a.Key)) predicted[a.Key] = a;

                var driverKeys = new HashSet<string>(rep.Drivers.Select(d => d.ObservationKey), StringComparer.OrdinalIgnoreCase);

                void Compare(Observation obs, string baseKey, string actionKey, Value actual, string where)
                {
                    if (!stats.TryGetValue(baseKey, out var stat))
                        stats[baseKey] = stat = new TargetStat { Key = baseKey, Label = obs.Label };
                    if (!predicted.TryGetValue(actionKey, out var action))
                    {
                        log.Add((fold, baseKey, 2));
                        stat.Missing++;
                        stat.Failures.Add($"{where}: kural çıkarılamamıştı (gerçek değer {actual.AsText()})");
                        return;
                    }
                    stat.Expressions.Add(action.Rule.Expression);
                    if (Same(action.Value, actual))
                    {
                        log.Add((fold, baseKey, 0));
                        stat.Correct++;
                    }
                    else
                    {
                        log.Add((fold, baseKey, 1));
                        stat.Wrong++;
                        stat.Failures.Add($"{where}: tahmin {action.Value.AsText()}, gerçek {actual.AsText()}  (formül: {action.Rule.Expression})");
                    }
                }

                foreach (var obs in varyingInst)
                {
                    if (driverKeys.Contains(obs.Key)) continue;
                    var baseKey = instTarget[obs.Key];
                    for (int r = 0; r < rows.Count; r++)
                        if (obs.Values.TryGetValue(rows[r].Name, out var actual))
                            Compare(obs, baseKey, baseKey + "#" + (r + 1), actual, $"{held.Name} satır {r + 1}");
                }
                foreach (var obs in varyingOuter)
                {
                    if (driverKeys.Contains(RuleInferencer.OuterPrefix + obs.Key) || !obs.Values.TryGetValue(held.Name, out var actual)) continue;
                    var baseKey = outerTarget[obs.Key];
                    Compare(obs, baseKey, baseKey, actual, held.Name);
                }
            }

            var unexplainable = new HashSet<string>(stats.Values.Where(t => t.Correct + t.Wrong == 0).Select(t => t.Key), StringComparer.OrdinalIgnoreCase);
            foreach (var (fold, key, status) in log.Where(l => !unexplainable.Contains(l.key)))
            {
                if (status == 0) fold.Correct++;
                else if (status == 1) fold.Wrong++;
                else fold.Missing++;
            }
            report.Unexplainable = stats.Values.Where(t => unexplainable.Contains(t.Key)).Select(t => t.Label).ToList();
            report.Targets = stats.Values.Where(t => !unexplainable.Contains(t.Key)).OrderBy(t => t.Label, StringComparer.OrdinalIgnoreCase).ToList();
            report.Notes.Add($"Tekrarlanan modül ('{split.Document}'): her varyant tüm kopyalarıyla birlikte çıkarıldı; " +
                             "değerler kopya (satır) bazında karşılaştırıldı.");
            return report;
        }

        /// <summary>Çapraz doğrulamada girdinin aralık/seçenek sınırlarını kaldırır (çıkarılan varyant eğitim aralığının dışında olabilir).</summary>
        private static void Relax(InputDefinition input, IEnumerable<Value> seen)
        {
            input.Min = null;
            input.Max = null;
            if (input.Type != InputType.Choice) return;
            foreach (var v in seen)
                if (!input.Options.Contains(v.AsText(), StringComparer.OrdinalIgnoreCase))
                    input.Options.Add(v.AsText());
        }

        private static bool Same(Value predicted, Value actual)
        {
            if (predicted.Kind == ValueKind.Number && actual.Kind == ValueKind.Number)
                return Math.Abs(predicted.AsNumber() - actual.AsNumber()) <= 0.01;
            return Value.LooseEquals(predicted, actual);
        }
    }
}
