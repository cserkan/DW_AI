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

    public sealed class InferenceReport
    {
        public int SampleCount { get; set; }
        public int ObservationCount { get; set; }

        /// <summary>Tüm varyantlarda aynı kalan (kurala gerek olmayan) gözlem sayısı.</summary>
        public int ConstantCount { get; set; }

        public List<InputDefinition> Inputs { get; set; } = new List<InputDefinition>();
        public List<Rule> Rules { get; set; } = new List<Rule>();
        public List<UnexplainedObservation> Unexplained { get; set; } = new List<UnexplainedObservation>();

        /// <summary>Kullanıcıya sorulması gereken belirsizlikler (YZ sohbetinde kullanılır).</summary>
        public List<string> Questions { get; set; } = new List<string>();

        public List<string> Notes { get; set; } = new List<string>();

        public RuleSet ToRuleSet(string name, string masterAssembly)
        {
            return new RuleSet
            {
                Name = name,
                MasterAssembly = masterAssembly,
                Description = $"{SampleCount} varyanttan otomatik çıkarıldı.",
                Inputs = Inputs,
                Rules = Rules,
            };
        }

        public string ToText()
        {
            var sb = new StringBuilder();
            sb.AppendLine($"Varyant: {SampleCount}, gözlem: {ObservationCount}, sabit: {ConstantCount}, " +
                          $"kural önerisi: {Rules.Count}, açıklanamayan: {Unexplained.Count}");
            sb.AppendLine();
            sb.AppendLine("GİRDİLER");
            foreach (var i in Inputs)
            {
                var range = i.Type == InputType.Number ? $" [{Fmt(i.Min)} … {Fmt(i.Max)}]" :
                    i.Type == InputType.Choice ? $" {{{string.Join(", ", i.Options)}}}" : string.Empty;
                sb.AppendLine($"  {i.Name} ({i.Type}){range}");
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
            var observations = extractor.Extract(samples);
            report.ObservationCount = observations.Count;

            // Girdi tablosu yoksa modeldeki gözlemleri girdi olarak kullan.
            var inputObsKeys = new HashSet<string>(options.InputObservations.Values, StringComparer.OrdinalIgnoreCase);
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

            var columns = BuildInputColumns(samples, master, report);
            if (columns.Count == 0)
            {
                report.Notes.Add("Hiç girdi yok. CSV girdi tablosu verin veya --input Ad=gözlem ile modeldeki bir ölçüyü girdi seçin.");
                return report;
            }

            var finder = new RelationFinder(columns, options.Tolerance);
            var usedIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
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
                if (best.Question != null) report.Questions.Add($"{rule.Id}: {best.Question}");
            }

            if (samples.Count < 6)
                report.Notes.Add($"Sadece {samples.Count} varyant var; güven düşük. Girdi aralığının uçlarını kapsayan 8–15 varyant önerilir.");
            return report;
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
