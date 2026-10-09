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
                    if (t.Wrong > 0) parts.Add($"{t.Wrong} turda yanlış");
                    if (t.Missing > 0) parts.Add($"{t.Missing} turda kural yok");
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

        private static bool Same(Value predicted, Value actual)
        {
            if (predicted.Kind == ValueKind.Number && actual.Kind == ValueKind.Number)
                return Math.Abs(predicted.AsNumber() - actual.AsNumber()) <= 0.01;
            return Value.LooseEquals(predicted, actual);
        }
    }
}
