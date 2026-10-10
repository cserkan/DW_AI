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

        /// <summary>Tekrarlanan modülün içindeyse modülün tablosu.</summary>
        public string? Scope { get; set; }

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

        /// <summary>Tüm varyantlarda aynı ama master'dan farklı olduğu için sabit kural verilen gözlem sayısı.</summary>
        public int ConstantRuleCount { get; set; }

        public List<InputDefinition> Inputs { get; set; } = new List<InputDefinition>();

        /// <summary>Tekrarlanan modüller: her satırı bir kopya olan tablo girdileri.</summary>
        public List<TableDefinition> Tables { get; set; } = new List<TableDefinition>();

        /// <summary>Önerilen varyantların satırlarının ait olduğu tablo (tekrarlanan modül varsa).</summary>
        public string? SuggestionTable { get; set; }

        /// <summary>Ortak eşikler ve ara değerler için çıkarılan değişkenler.</summary>
        public List<VariableDefinition> Variables { get; set; } = new List<VariableDefinition>();

        public List<Rule> Rules { get; set; } = new List<Rule>();
        public List<UnexplainedObservation> Unexplained { get; set; } = new List<UnexplainedObservation>();

        /// <summary>Kullanıcıya sorulması gereken belirsizlikler (YZ sohbetinde kullanılır).</summary>
        public List<string> Questions { get; set; } = new List<string>();

        /// <summary>Kullanıcının cevaplaması gereken sorular (cevapları kurallara uygulanabilir; bkz. <see cref="QuestionApplier"/>).</summary>
        public List<OpenQuestion> OpenQuestions { get; set; } = new List<OpenQuestion>();

        public List<string> Notes { get; set; } = new List<string>();

        /// <summary>Çıkarımın belirsiz kaldığı noktalar (hangi girdi değeri denenirse çözülür).</summary>
        public List<ProbeNeed> Needs { get; set; } = new List<ProbeNeed>();

        /// <summary>DriveWorks'te üretilmesi önerilen yeni varyantlar (girdi tablosu olarak).</summary>
        public List<SuggestedVariant> SuggestedVariants { get; set; } = new List<SuggestedVariant>();

        /// <summary>Kör testte (girdi tablosu yokken) modelden tahmin edilen girdiler.</summary>
        public List<DetectedDriver> Drivers { get; set; } = new List<DetectedDriver>();

        /// <summary>Varyantlar arasında değişen tüm değerlerin özeti ("ne değişti?").</summary>
        public List<ChangedValue> Changes { get; set; } = new List<ChangedValue>();

        /// <summary>Her varyantın girdi değerleri (kör testte modelden okunan, girdi tablosu verildiyse o tablo).</summary>
        public Dictionary<string, Dictionary<string, Value>> InputValues { get; set; } =
            new Dictionary<string, Dictionary<string, Value>>(StringComparer.OrdinalIgnoreCase);

        /// <summary>Tekrarlanan modül varsa her varyantın tablo satırları.</summary>
        public Dictionary<string, List<Dictionary<string, Value>>> RowValues { get; set; } =
            new Dictionary<string, List<Dictionary<string, Value>>>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Varyantların girdi tablosu (CSV, ayraç ';'). Bir varyantı programla yeniden üretmek için girdileri buradan alın;
        /// --inputs ile çıkarıma geri de verilebilir.
        /// </summary>
        public string ToInputCsv()
        {
            var table = Tables.FirstOrDefault();
            var rows = InputValues.Keys.Concat(RowValues.Keys).Distinct(StringComparer.OrdinalIgnoreCase).Select(name => new SuggestedVariant
            {
                Name = name,
                Inputs = InputValues.TryGetValue(name, out var inputs) ? inputs : new Dictionary<string, Value>(),
                Rows = RowValues.TryGetValue(name, out var r) ? r : new List<Dictionary<string, Value>>(),
            }).ToList();
            return VariantPlanner.ToCsv(Inputs, rows, table);
        }

        public RuleSet ToRuleSet(string name, string masterAssembly)
        {
            return new RuleSet
            {
                Name = name,
                MasterAssembly = masterAssembly,
                Description = $"{SampleCount} varyanttan otomatik çıkarıldı.",
                Inputs = Inputs,
                Tables = Tables,
                Variables = Variables,
                Rules = Rules,
            };
        }

        /// <summary>Önerilen varyantları DriveWorks'e aktarılabilecek girdi tablosu (CSV, ayraç ';') olarak verir.</summary>
        public string ToSuggestionCsv() =>
            VariantPlanner.ToCsv(Inputs, SuggestedVariants, Tables.FirstOrDefault(t => string.Equals(t.Name, SuggestionTable, StringComparison.OrdinalIgnoreCase)));

        public string ToText()
        {
            var sb = new StringBuilder();
            sb.AppendLine($"Varyant: {SampleCount}, gözlem: {ObservationCount}, sabit: {ConstantCount}, " +
                          $"kural önerisi: {Rules.Count}, açıklanamayan: {Unexplained.Count}" +
                          (ConstantRuleCount > 0 ? $" (bunlardan {ConstantRuleCount} kural: hiç değişmiyor ama master'dan farklı)" : string.Empty));
            if (Drivers.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("TAHMİNİ GİRDİLER (kör test: girdi tablosu verilmedi, modelden bulundu)");
                foreach (var d in Drivers)
                {
                    var table = Tables.FirstOrDefault(t => t.FindColumn(d.Name) != null);
                    var where = table != null ? $"   [satır girdisi: {table.Name} tablosunun sütunu]" : string.Empty;
                    sb.AppendLine($"  {d.Name}  ←  {d.Label}   ({d.Explains} değeri açıklıyor){where}");
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
            foreach (var t in Tables)
            {
                sb.AppendLine();
                sb.AppendLine($"TABLO {t.Name}  ({t.Label}; {t.MinRows}–{t.MaxRows} satır, her satır modülün bir kopyası)");
                sb.AppendLine($"  modül: {t.Module}" + (t.ModuleComponent != null ? $"  (master'da: {t.ModuleComponent})" : "  (master'ın kendisi; üretilen modelin kökü kopyaları toplayan ayrı bir montaj)"));
                foreach (var c in t.Columns)
                {
                    var range = c.Type == InputType.Number ? $" [{Fmt(c.Min)} … {Fmt(c.Max)}]" :
                        c.Type == InputType.Choice ? $" {{{string.Join(", ", c.Options)}}}" : string.Empty;
                    sb.AppendLine($"  sütun {c.Name} ({c.Type}){range}");
                }
            }
            if (Variables.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("DEĞİŞKENLER");
                foreach (var v in Variables)
                {
                    sb.AppendLine($"  {v.Name} = {v.Expression}" + (v.Scope != null ? $"   (her {v.Scope} satırı için)" : string.Empty));
                    if (!string.IsNullOrEmpty(v.Description)) sb.AppendLine($"         ({v.Description})");
                }
            }
            sb.AppendLine();
            sb.AppendLine("ÖNERİLEN KURALLAR");
            foreach (var r in Rules.OrderByDescending(r => r.Confidence))
            {
                sb.AppendLine($"  [{(r.Confidence ?? 0).ToString("P0", CultureInfo.InvariantCulture),4}] {r.Target}" +
                              (r.Scope != null ? $"   (her {r.Scope} satırı için)" : string.Empty));
                sb.AppendLine($"         = {r.Expression}");
                if (!string.IsNullOrEmpty(r.Evidence)) sb.AppendLine($"         ({r.Evidence})");
            }
            if (Unexplained.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("AÇIKLANAMAYAN DEĞİŞİMLER");
                foreach (var u in Unexplained)
                    sb.AppendLine($"  {u.Label}{(u.Scope != null ? $" [{u.Scope}]" : "")}: {string.Join(", ", u.Values.Take(16).Select(kv => kv.Key + "=" + kv.Value))}" +
                                  (u.Values.Count > 16 ? $" … (+{u.Values.Count - 16})" : string.Empty));
            }
            if (Questions.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("SORULAR");
                foreach (var q in Questions) sb.AppendLine("  - " + q);
            }
            if (SuggestedVariants.Count > 0 || Needs.Any(n => n.IsManual))
            {
                sb.AppendLine();
                sb.AppendLine("ÖNERİLEN YENİ VARYANTLAR (bunları DriveWorks'te üretip aynı komutu tekrar çalıştırın)");
                foreach (var v in SuggestedVariants)
                {
                    sb.AppendLine($"  {v.Name}:  " + string.Join(",  ", Inputs.Where(i => v.Inputs.ContainsKey(i.Name))
                        .Select(i => $"{i.Name} = {v.Inputs[i.Name].AsText()}")));
                    for (int ri = 0; ri < v.Rows.Count; ri++)
                        sb.AppendLine($"      {SuggestionTable} satır {ri + 1}: " + string.Join(",  ", v.Rows[ri].Select(kv => $"{kv.Key} = {kv.Value.AsText()}")));
                    foreach (var r in v.Reasons) sb.AppendLine($"      · {r}");
                }
                foreach (var m in Needs.Where(n => n.IsManual))
                    sb.AppendLine($"  Elle: {m.Reason}");
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

    /// <summary>Kural arama için gereken her şey: gözlemler, örnekler, girdi sütunları ve hedeflerin master adları.</summary>
    internal sealed class RuleContext
    {
        public List<Observation> Observations { get; set; } = new List<Observation>();

        /// <summary>Gözlem değerlerinin anahtarı olan örnekler (varyantlar ya da modül kopyaları).</summary>
        public IReadOnlyList<VariantSample> Samples { get; set; } = new List<VariantSample>();

        public List<InputColumn> Columns { get; set; } = new List<InputColumn>();

        /// <summary>Girdi olarak kullanılan gözlemler: kendileri için kural aranmaz.</summary>
        public HashSet<string> InputObservationKeys { get; set; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>Girdi adı → girdinin okunduğu gözlem (üretimde bu değer de yazılmalı).</summary>
        public Dictionary<string, string> InputSources { get; set; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>Gözlemin kural hedefi (master adlarıyla).</summary>
        public Func<Observation, RuleTarget> TargetOf { get; set; } = o => o.Target;

        /// <summary>Gözlemin kuralının kapsamı (tekrarlanan modülün tablosu) ya da null.</summary>
        public Func<Observation, string?> ScopeOf { get; set; } = _ => null;

        /// <summary>Bu aramada oluşturulan ara değişkenlerin kapsamı.</summary>
        public string? VariableScope { get; set; }

        public double Tolerance { get; set; }
        public HashSet<string> UsedIds { get; set; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>Kural → kaynak gözlemi ve veriye uyan diğer formüller (sorular için).</summary>
        public Dictionary<string, Observation> RuleObservations { get; } = new Dictionary<string, Observation>(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, List<string>> Alternatives { get; } = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

        /// <summary>Gözlemin her varyanttaki değeri ve o varyantın girdileri: kullanıcının yazdığı formülü sınamak için.</summary>
        public List<QuestionCheck> ChecksFor(Observation obs) =>
            Samples.Where(s => obs.Values.ContainsKey(s.Name)).Select(s => new QuestionCheck
            {
                Variant = s.Name.Split(new[] { "__" }, StringSplitOptions.None)[0] + (s.Name.Contains("#") ? " " + s.Name.Substring(s.Name.LastIndexOf('#')) : ""),
                Expected = obs.Values[s.Name].AsText(),
                Inputs = Columns.Where(c => c.Values.ContainsKey(s.Name))
                    .ToDictionary(c => c.Name, c => c.Values[s.Name].AsText(), StringComparer.OrdinalIgnoreCase),
            }).ToList();

        /// <summary>
        /// Master modeldeki değerler (gözlem anahtarıyla). Tüm varyantlarda aynı ama master'dan farklı olan değerler
        /// (ör. DriveWorks'ün her seferinde boşalttığı bir özellik) sabit kural olur.
        /// </summary>
        public Dictionary<string, Value>? MasterValues { get; set; }
    }

    /// <summary>Varyantlardan kural çıkarımı (deterministik). Çıktı her zaman "Proposed" durumundadır.</summary>
    public static partial class RuleInferencer
    {
        public static InferenceReport Infer(IReadOnlyList<VariantSample> samples, InferenceOptions? options = null,
            ModelSnapshot? master = null)
        {
            options = options ?? new InferenceOptions();
            if (samples.Count < 2)
            {
                var r = new InferenceReport { SampleCount = samples.Count };
                r.Notes.Add("Kural çıkarmak için en az 2 (tercihen 8+) varyant gerekir.");
                return r;
            }

            // Varyantlar master'ın birden çok kopyasını (ör. konveyör hattındaki bölümler) içeriyorsa iki katmanlı çıkarım.
            var split = options.DetectModules ? ModuleDetector.Detect(samples, master, options) : null;
            var report = split != null ? InferModules(split, options, master) : InferFlat(samples, options, master);
            ExtendSwitches(report);
            CoupledInputQuestions(report);
            TidyQuestions(report);
            return report;
        }

        /// <summary>
        /// Sonradan açıklanan değerlerin sorularını atar (başka bir değerden ya da aralık formülünden kural bulundu) ve
        /// varyantlarda aynı değerleri taşıyan açıklanamayan değerleri tek soruda birleştirir (ör. 8 parçadaki OrderNo).
        /// </summary>
        private static void TidyQuestions(InferenceReport report)
        {
            var stillUnexplained = new HashSet<string>(report.Unexplained.Select(u => "aciklanamayan:" + u.Key), StringComparer.OrdinalIgnoreCase);
            report.OpenQuestions.RemoveAll(q => q.Kind == QuestionKind.Unexplained && !stillUnexplained.Contains(q.Id));
            var merged = new List<OpenQuestion>();
            foreach (var group in report.OpenQuestions.Where(q => q.Kind == QuestionKind.Unexplained)
                         .GroupBy(q => string.Join("\u0001", q.Checks.Select(c => c.Variant + "=" + c.Expected))).Where(g => g.Count() > 1))
            {
                var first = group.First();
                foreach (var other in group.Skip(1))
                {
                    first.Targets.AddRange(other.Targets);
                    merged.Add(other);
                }
                first.Text = $"{first.Targets.Count} değer nasıl hesaplanıyor? ({string.Join(", ", first.Targets.Take(3).Select(t => t.Label))}" +
                             (first.Targets.Count > 3 ? " …)" : ")");
                first.Detail = first.Detail?.Replace("Bu değer varyantlarda", "Bu değerler varyantlarda hep aynı biçimde");
            }
            report.OpenQuestions.RemoveAll(merged.Contains);
        }

        /// <summary>
        /// Girdiyle hep birlikte değişen seçim değerleri (ör. malzeme ve kulp dosyası): aynı girdiden mi geliyor, yoksa formda
        /// ayrı bir seçim mi olmalı? Aynı formülü paylaşan kardeş kurallar (sol ve sağ kapağın kulpu) birlikte sorulur.
        /// </summary>
        private static void CoupledInputQuestions(InferenceReport report)
        {
            foreach (var d in report.Drivers.Where(d => d.Question != null && d.CoupledLabel != null))
            {
                var main = report.Rules.FirstOrDefault(r => string.Equals(r.Description, d.CoupledLabel, StringComparison.Ordinal));
                if (main == null) continue;
                var rules = report.Rules.Where(r => r == main || (r.Target.Kind == main.Target.Kind && r.Expression == main.Expression)).ToList();
                report.OpenQuestions.Add(new OpenQuestion
                {
                    Id = "ayri:" + d.Name + ":" + main.Id,
                    Kind = QuestionKind.SeparateInput,
                    Text = $"{main.Description}: her zaman {d.Name} seçimine göre mi belirleniyor?",
                    Detail = $"Varyantlarda bu seçim ile {d.Label} hep birlikte değişti ({main.Expression}). Siparişte ayrı seçilebiliyorsa formda ayrı bir alan olmalı.",
                    RuleIds = rules.Select(r => r.Id).ToList(),
                    Input = d.Name,
                    Label = main.Description,
                    Options =
                    {
                        new QuestionOption { Label = $"Evet, her zaman {d.Name} seçimine göre", Value = "ayni" },
                        new QuestionOption { Label = "Hayır, ayrı seçilebilmeli (formda ayrı alan olsun)", Value = "ayri" },
                    },
                });
            }
        }

        private static readonly System.Text.RegularExpressions.Regex SwitchCall = new System.Text.RegularExpressions.Regex(
            @"SWITCH\((?<in>[A-Za-z_][A-Za-z0-9_]*)(?<args>(?:\s*,\s*(?:-?\d+(?:\.\d+)?|""[^""]*""))+)\)");

        private static readonly System.Text.RegularExpressions.Regex SwitchLiteral =
            new System.Text.RegularExpressions.Regex(@"-?\d+(?:\.\d+)?|""[^""]*""");

        /// <summary>
        /// Aynı girdiye göre seçim yapan SWITCH'lerden biri diğerinin eksik hâliyse (ortak seçeneklerde aynı sonuç, ama bazı
        /// girdi değerleri hiç görülmemiş) eksik olan tamamlanır. Örnek: destek parça numarası sadece 40/50 çaplı varyantlarda
        /// görüldü ("Lİ", "ME"); makara parça numarası aynı kodlamayı 60 için de biliyor ("HE").
        /// </summary>
        public static void ExtendSwitches(InferenceReport report)
        {
            var calls = new List<(Rule rule, string text, string input, List<(string key, string value)> pairs)>();
            foreach (var rule in report.Rules)
                foreach (System.Text.RegularExpressions.Match m in SwitchCall.Matches(rule.Expression ?? string.Empty))
                {
                    var literals = SwitchLiteral.Matches(m.Groups["args"].Value).Cast<System.Text.RegularExpressions.Match>().Select(x => x.Value).ToList();
                    if (literals.Count < 2 || literals.Count % 2 != 0) continue;
                    var pairs = Enumerable.Range(0, literals.Count / 2).Select(i => (literals[2 * i], literals[2 * i + 1])).ToList();
                    if (pairs.Select(p => p.Item1).Distinct().Count() != pairs.Count) continue;
                    calls.Add((rule, m.Value, m.Groups["in"].Value, pairs));
                }

            foreach (var call in calls)
            {
                var keys = call.pairs.ToDictionary(p => p.key, p => p.value);
                var better = calls
                    .Where(o => o.input == call.input && o.pairs.Count > call.pairs.Count &&
                                call.pairs.All(p => o.pairs.Any(q => q.key == p.key && q.value == p.value)))
                    .OrderByDescending(o => o.pairs.Count).FirstOrDefault();
                if (better.rule == null) continue;
                var replacement = $"SWITCH({call.input}, {string.Join(", ", better.pairs.Select(p => p.key + ", " + p.value))})";
                call.rule.Expression = call.rule.Expression.Replace(call.text, replacement);
                var added = better.pairs.Where(p => !keys.ContainsKey(p.key)).Select(p => $"{p.key} → {p.value}");
                call.rule.Evidence += $" Varyantlarda görülmeyen seçenekler '{better.rule.Id}' kuralındaki aynı kodlamadan tamamlandı: {string.Join(", ", added)}.";
            }
        }

        private static InferenceReport InferFlat(IReadOnlyList<VariantSample> samples, InferenceOptions options, ModelSnapshot? master)
        {
            var report = new InferenceReport { SampleCount = samples.Count };
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
            var structure = options.MatchByStructure ?? (options.NamePattern == null && StructureMatcher.IsNeeded(snapshots));
            var masterValues = MasterValues(master, options, structure);
            if (extractor.UnstableFeatureFamilies.Count > 0)
                report.Notes.Add("Şu özelliklerin numaraları varyantlar arasında kayıyor (SolidWorks yeniden numaralıyor); adlarına güvenilemediği " +
                                 "için bastırma kuralı çıkarılmadı: " + string.Join(", ", extractor.UnstableFeatureFamilies) + ".");

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

            report.Changes = Changes(observations, samples.Count);

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

            var columns = BuildInputColumns(samples, report.Inputs, report.Notes);
            if (columns.Count == 0)
            {
                report.Notes.Add("Hiç girdi yok. CSV girdi tablosu verin veya --input Ad=gözlem ile modeldeki bir ölçüyü girdi seçin.");
                return report;
            }

            var ctx = new RuleContext
            {
                Observations = observations,
                Samples = samples,
                Columns = columns,
                InputObservationKeys = inputObsKeys,
                InputSources = inputSources,
                TargetOf = o => MapToMaster(o.Target, master, extractor),
                Tolerance = options.Tolerance,
                MasterValues = masterValues,
            };
            var needs = FindRules(report, ctx);
            foreach (var d in report.Drivers.Where(d => d.Question != null))
                needs.Add(new ProbeNeed { Priority = 2, Input = d.Name, Reason = d.Question! });
            DedupeNeeds(needs);
            report.Needs = needs;
            report.SuggestedVariants = VariantPlanner.Plan(report.Inputs, columns, needs, report.Notes);
            foreach (var s in samples)
                report.InputValues[s.Name] = columns.Where(c => c.Values.ContainsKey(s.Name))
                    .ToDictionary(c => c.Name, c => c.Values[s.Name], StringComparer.OrdinalIgnoreCase);

            if (samples.Count < 6)
                report.Notes.Add($"Sadece {samples.Count} varyant var; güven düşük. Girdi aralığının uçlarını kapsayan 8–15 varyant önerilir.");
            return report;
        }

        internal static List<ChangedValue> Changes(List<Observation> observations, int sampleCount) =>
            observations
                .Where(o => o.Values.Values.Distinct().Count() > 1)
                .Select(o => new ChangedValue { Key = o.Key, Label = o.Label, Summary = Summarize(o, sampleCount) })
                .ToList();

        /// <summary>
        /// Her değişen gözlem için en iyi formülü bulur; ortak eşikleri, türetilmiş değerleri ve eşit aralık formüllerini
        /// birleştirir. Dönüş: kesinleşmemiş noktaların çözülmesi için gereken yeni varyant ihtiyaçları.
        /// </summary>
        internal static List<ProbeNeed> FindRules(InferenceReport report, RuleContext ctx)
        {
            var observations = ctx.Observations;
            var samples = ctx.Samples;
            var columns = ctx.Columns;
            var usedIds = ctx.UsedIds;
            var finder = new RelationFinder(columns, ctx.Tolerance);
            var chosen = new List<(Observation obs, Rule rule, RelationCandidate candidate)>();
            foreach (var obs in observations)
            {
                if (ctx.InputObservationKeys.Contains(obs.Key)) continue;
                var distinct = obs.Values.Values.Distinct().Count();
                if (distinct <= 1 && obs.Values.Count == samples.Count)
                {
                    var constant = obs.Values.Values.First();
                    if (ctx.MasterValues != null && ctx.MasterValues.TryGetValue(obs.Key, out var masterValue) &&
                        !SameValue(masterValue, constant, ctx.Tolerance))
                    {
                        // Hiç değişmiyor ama master'dan farklı: üretim her seferinde bu değeri yazmalı.
                        var constTarget = ctx.TargetOf(obs);
                        report.Rules.Add(new Rule
                        {
                            Id = UniqueId(MakeId(constTarget), usedIds),
                            Description = obs.Label,
                            Target = constTarget,
                            Scope = ctx.ScopeOf(obs),
                            Expression = NumberUtil.Literal(constant),
                            Status = RuleStatus.Proposed,
                            Source = RuleSource.Inference,
                            Confidence = NumberUtil.Confidence(samples.Count, 1),
                            Evidence = $"{samples.Count} varyantın hepsinde \"{constant.AsText()}\"; master modelde \"{masterValue.AsText()}\". " +
                                       "Değişmiyor ama master'dan farklı, bu yüzden her üretimde yazılmalı.",
                        });
                        report.ConstantRuleCount++;
                        continue;
                    }
                    report.ConstantCount++;
                    continue;
                }
                var candidates = finder.Find(obs, obs.Target.ExpectedType);
                if (distinct <= 1)
                {
                    // Sadece bazı varyantlarda var ve değişmiyor (ör. sadece Oak siparişlerinde kullanılan Framed kapağın
                    // DWMaterial'ı). Kütüphaneden gelen dosyada değer boş olabilir: başka varyantlarda değişen bir girdiye
                    // birebir eşitse o girdiyi yaz, yoksa atla.
                    candidates = obs.Target.ExpectedType != ExpectedType.Text ? new List<RelationCandidate>()
                        : candidates.Where(c => IsVaryingInput(c.Expression, columns)).Take(1).ToList();
                    if (candidates.Count == 0) continue;
                }
                var best = candidates.FirstOrDefault();
                if (best == null)
                {
                    report.Unexplained.Add(new UnexplainedObservation
                    {
                        Key = obs.Key,
                        Label = obs.Label,
                        Target = obs.Target,
                        Scope = ctx.ScopeOf(obs),
                        Values = obs.Values.ToDictionary(kv => kv.Key, kv => kv.Value.AsText()),
                    });
                    var seen = obs.Values.Values.Select(v => v.AsText()).Distinct().Take(6).ToList();
                    report.OpenQuestions.Add(new OpenQuestion
                    {
                        Id = "aciklanamayan:" + obs.Key,
                        Kind = QuestionKind.Unexplained,
                        Text = $"{obs.Label} nasıl hesaplanıyor?",
                        Detail = $"Bu değer varyantlarda değişiyor ama hiçbir girdiyle açıklanamadı. Görülen değerler: {string.Join(", ", seen)}" +
                                 (obs.Values.Values.Distinct().Count() > seen.Count ? " …" : "") + ". Formülünü yazarsanız varyantlarla sınanır.",
                        Options = { new QuestionOption { Label = "Önemli değil (kural yazılmasın)", Value = "onemsiz" } },
                        AllowFormula = true,
                        Targets = { new NewRuleTarget { Target = ctx.TargetOf(obs), Scope = ctx.ScopeOf(obs), Label = obs.Label } },
                        Label = obs.Label,
                        Checks = ctx.ChecksFor(obs),
                    });
                    continue;
                }

                if (best.ThresholdInput != null)
                    best.Competitors = candidates.Skip(1)
                        .Where(c => c.ThresholdInput != null && c.ThresholdInput != best.ThresholdInput && c.Confidence >= 0.7)
                        .GroupBy(c => c.ThresholdInput).Select(g => g.First()).ToList();
                var alternatives = candidates.Skip(1).Where(c => c.Confidence >= 0.5).Take(2).Select(c => c.Expression).ToList();
                var target = ctx.TargetOf(obs);
                var rule = new Rule
                {
                    Id = UniqueId(MakeId(target), usedIds),
                    Description = obs.Label,
                    Target = target,
                    Scope = ctx.ScopeOf(obs),
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
                ctx.RuleObservations[rule.Id] = obs;
                ctx.Alternatives[rule.Id] = candidates.Skip(1).Where(c => c.Confidence >= 0.5).Select(c => c.Expression)
                    .Where(e => e != best.Expression).Distinct().Take(3).ToList();
            }

            // Girdi olarak seçilen model değerlerinin kendisi de üretimde yazılmalı (ör. kapak dosyası seçimi,
            // "Assembly Height" özelliği). Girdi tablosu verildiğinde bu değerler zaten ayrı gözlemdir.
            foreach (var kv in ctx.InputSources)
            {
                var obs = observations.FirstOrDefault(o => string.Equals(o.Key, kv.Value, StringComparison.OrdinalIgnoreCase));
                if (obs == null) continue;
                var target = ctx.TargetOf(obs);
                report.Rules.Add(new Rule
                {
                    Id = UniqueId(MakeId(target), usedIds),
                    Description = obs.Label,
                    Target = target,
                    Scope = ctx.ScopeOf(obs),
                    Expression = SanitizeName(kv.Key),
                    Status = RuleStatus.Proposed,
                    Source = RuleSource.Inference,
                    Confidence = 0.99,
                    Evidence = "Bu değer girdinin kendisi.",
                });
            }
            foreach (var d in report.Drivers.Where(d => d.Question != null && ctx.InputSources.ContainsKey(d.Name)))
                if (!report.Questions.Contains(d.Question!)) report.Questions.Add(d.Question!);

            var questionRules = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            var needs = new List<ProbeNeed>();
            var handled = ShareThresholds(chosen, report, ctx, needs);
            DeriveFromOtherValues(chosen, report, ctx, handled);
            DerivePitches(chosen, report, ctx, handled);
            CollectNeeds(chosen, columns, report, needs, handled, ctx);

            foreach (var (_, rule, candidate) in chosen)
            {
                if (candidate.Question == null || handled.Contains(rule.Id)) continue;
                if (!questionRules.TryGetValue(candidate.Question, out var ids)) questionRules[candidate.Question] = ids = new List<string>();
                ids.Add(rule.Id);
            }
            foreach (var q in questionRules)
            {
                report.Questions.Add(q.Value.Count <= 3
                    ? $"{string.Join(", ", q.Value)}: {q.Key}"
                    : $"{q.Value.Count} kural ({string.Join(", ", q.Value.Take(2))} …): {q.Key}");
                // Veriye birden çok formül uyan her kural için ayrı seçim sorusu; diğerleri (az veri) birlikte doğrulanır.
                var withAlternatives = q.Value.Where(id => ctx.Alternatives.TryGetValue(id, out var alt) && alt.Count > 0).ToList();
                foreach (var id in withAlternatives)
                {
                    var rule = report.Rules.First(r => r.Id == id);
                    var question = new OpenQuestion
                    {
                        Id = "formul:" + id,
                        Kind = QuestionKind.ChooseFormula,
                        Text = $"{rule.Description}: hangi formül doğru?",
                        Detail = q.Key,
                        RuleIds = { id },
                        Options = { new QuestionOption { Label = rule.Expression + "   (şu an kullanılan)", Value = rule.Expression } },
                        AllowFormula = true,
                        Checks = ctx.RuleObservations.TryGetValue(id, out var o) ? ctx.ChecksFor(o) : new List<QuestionCheck>(),
                    };
                    question.Options.AddRange(ctx.Alternatives[id].Select(e => new QuestionOption { Label = e, Value = e }));
                    report.OpenQuestions.Add(question);
                }
                var rest = q.Value.Except(withAlternatives).ToList();
                if (rest.Count == 0) continue;
                var first = report.Rules.First(r => r.Id == rest[0]);
                report.OpenQuestions.Add(new OpenQuestion
                {
                    Id = "dogrula:" + string.Join(",", rest.OrderBy(x => x, StringComparer.Ordinal)),
                    Kind = QuestionKind.Confirm,
                    Text = rest.Count == 1 ? $"{first.Description} = {first.Expression}: doğru mu?"
                                           : $"{rest.Count} kural doğru mu? (ör. {first.Description} = {first.Expression})",
                    Detail = q.Key,
                    RuleIds = rest,
                    Options = { new QuestionOption { Label = "Evet, doğru", Value = "dogru" } },
                    // Birden çok kural genelde simetrik kardeşlerdir (sol/sağ kapak): yazılan formül hepsine uygulanır, ilkiyle sınanır.
                    AllowFormula = true,
                    Checks = ctx.RuleObservations.TryGetValue(rest[0], out var ob) ? ctx.ChecksFor(ob) : new List<QuestionCheck>(),
                });
            }
            return needs;
        }

        /// <summary>İfade tek başına bir girdi adı mı ve o girdi varyantlar arasında değişiyor mu?</summary>
        private static bool IsVaryingInput(string expression, List<InputColumn> columns) =>
            columns.Any(c => string.Equals(c.Name, expression, StringComparison.OrdinalIgnoreCase) &&
                             c.Values.Values.Select(v => v.AsText()).Distinct(StringComparer.OrdinalIgnoreCase).Count() > 1);

        private static bool SameValue(Value a, Value b, double tolerance)
        {
            if (a.Kind == ValueKind.Number && b.Kind == ValueKind.Number) return Math.Abs(a.AsNumber() - b.AsNumber()) <= tolerance;
            return Value.LooseEquals(a, b);
        }

        /// <summary>Master modelin gözlem değerleri; anahtarlar varyant gözlemleriyle aynı (master kendine eşlenir).</summary>
        internal static Dictionary<string, Value>? MasterValues(ModelSnapshot? master, InferenceOptions options, bool structure)
        {
            if (master == null) return null;
            var sample = new VariantSample("\u0001master", master);
            var extractor = new ObservationExtractor(options);
            if (structure) extractor.UseStructure(master, new[] { sample });
            return extractor.Extract(new[] { sample })
                .Where(o => o.Values.ContainsKey(sample.Name))
                .ToDictionary(o => o.Key, o => o.Values[sample.Name], StringComparer.OrdinalIgnoreCase);
        }

        /// <summary>Aynı girdi + aynı değer için tekrar eden ihtiyaçları birleştirir (ilk gerekçe kalır).</summary>
        internal static void DedupeNeeds(List<ProbeNeed> needs)
        {
            var seen = new HashSet<string>();
            needs.RemoveAll(n => !n.IsManual && !seen.Add(n.Input + "|" + string.Join(",", n.Values.Select(v => v.AsText())) + "|" +
                string.Join(";", n.Combos.Select(c => string.Join(",", c.OrderBy(kv => kv.Key).Select(kv => kv.Key + "=" + kv.Value.AsText()))))));
        }

        /// <summary>
        /// Aynı girdiye ve örtüşen aralığa sahip eşikli kuralları tek bir eşik değişkenine bağlar
        /// (DriveWorks'teki ShelfQty değişkeni gibi). Kullanıcı eşiği tek yerden düzeltir.
        /// Dönüş: sorusu ortak soruya taşınan kural kimlikleri.
        /// </summary>
        private static HashSet<string> ShareThresholds(List<(Observation obs, Rule rule, RelationCandidate candidate)> chosen,
            InferenceReport report, RuleContext ctx, List<ProbeNeed> needs)
        {
            var usedIds = ctx.UsedIds;
            var columns = ctx.Columns;
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
                        Scope = ctx.VariableScope,
                        Expression = NumberUtil.Fmt(t),
                        Description = $"{byInput.Key} eşiği. Veriler {NumberUtil.Fmt(cluster.lo)} ile {NumberUtil.Fmt(cluster.hi)} " +
                                      $"arasındaki her değeri destekliyor; {cluster.items.Count} kural bu değişkeni kullanıyor.",
                    });
                    foreach (var item in cluster.items)
                    {
                        item.rule.Expression = item.candidate.Template!.Replace("{T}", name);
                        handled.Add(item.rule.Id);
                    }
                    var training = columns.First(c => c.Name == byInput.Key).Values.Values.Select(v => v.AsNumber()).ToList();
                    var need = VariantPlanner.ThresholdNeed(byInput.Key, cluster.lo, cluster.hi, training, $"{cluster.items.Count} kural: {name}");
                    needs.Add(need);
                    var examples = cluster.items.Take(3).Select(i => i.rule.Description).ToList();
                    // Tam sayı girdide (ör. konveyör adedi) arada başka değer yoksa eşik belirsiz değildir: sorulmaz.
                    bool integerGap = training.All(v => Math.Abs(v - Math.Round(v)) < 1e-9) && cluster.hi - cluster.lo <= 1 + 1e-9;
                    if (!integerGap) report.OpenQuestions.Add(new OpenQuestion
                    {
                        Id = "esik:" + name,
                        Kind = QuestionKind.Threshold,
                        Text = $"{byInput.Key} hangi değeri geçince değişiyor?",
                        Detail = $"{cluster.items.Count} kural {byInput.Key} küçükken bir, büyükken başka sonuç veriyor (ör. {string.Join("; ", examples)}). " +
                                 $"Varyantlarda {NumberUtil.Fmt(cluster.lo)} ile {NumberUtil.Fmt(cluster.hi)} farklı sonuç verdi; aradaki gerçek sınır nedir? " +
                                 $"Şimdilik {NumberUtil.Fmt(t)} kullanılıyor.",
                        RuleIds = cluster.items.Select(i => i.rule.Id).ToList(),
                        Variable = name,
                        Input = byInput.Key,
                        Min = cluster.lo,
                        Max = cluster.hi,
                        Current = t,
                        TryValues = need.Values.Select(v => v.AsText()).ToList(),
                    });
                    report.Questions.Add(
                        $"{byInput.Key} için {cluster.items.Count} kural aynı eşiği kullanıyor ({string.Join(", ", cluster.items.Take(4).Select(i => i.rule.Id))}" +
                        $"{(cluster.items.Count > 4 ? " …" : "")}). Veriler {NumberUtil.Fmt(cluster.lo)} ile {NumberUtil.Fmt(cluster.hi)} arasındaki her değeri " +
                        $"destekliyor; şimdilik {NumberUtil.Fmt(t)} seçildi. Gerçek eşik nedir? ('{name}' değişkenini değiştirmek hepsini düzeltir.)");
                }
            }
            return handled;
        }

        /// <summary>Kesinleşmemiş kuralların (tek başına eşik, belirsiz sabit, az veri) çözülmesi için gereken değerleri toplar.</summary>
        private static void CollectNeeds(List<(Observation obs, Rule rule, RelationCandidate candidate)> chosen,
            List<InputColumn> columns, InferenceReport report, List<ProbeNeed> needs, HashSet<string> handled, RuleContext ctx)
        {
            List<double> Training(string input) =>
                columns.First(c => c.Name == input).Values.Values.Select(v => v.AsNumber()).ToList();

            // Eşik hangi girdiye bağlı belirsizse (birden çok girdi veriyi ayırıyorsa) onları ayıracak varyantlar.
            var competing = new Dictionary<string, (string a, RelationCandidate ca, string b, RelationCandidate cb, List<string> rules)>();
            // Girdi → (kural → o girdiyle formül): kullanıcı girdiyi seçince kurallar ona göre yazılır.
            var choices = new Dictionary<string, Dictionary<string, Dictionary<string, string>>>();
            foreach (var (_, rule, c) in chosen.Where(x => x.candidate.ThresholdInput != null && x.candidate.Competitors.Count > 0))
            foreach (var other in c.Competitors)
            {
                var pair = new[] { c.ThresholdInput!, other.ThresholdInput! }.OrderBy(x => x, StringComparer.Ordinal).ToArray();
                var key = pair[0] + "|" + pair[1];
                if (!competing.TryGetValue(key, out var entry))
                {
                    competing[key] = entry = (c.ThresholdInput!, c, other.ThresholdInput!, other, new List<string>());
                    choices[key] = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
                }
                entry.rules.Add(rule.Id);
                Dictionary<string, string> For(string input) =>
                    choices[key].TryGetValue(input, out var m) ? m : choices[key][input] = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                For(c.ThresholdInput!)[rule.Id] = rule.Expression;
                For(other.ThresholdInput!)[rule.Id] = other.Expression;
            }
            foreach (var kv in competing)
            {
                var e = kv.Value;
                var colA = columns.First(x => x.Name == e.a);
                var colB = columns.First(x => x.Name == e.b);
                needs.Add(VariantPlanner.CompetingThresholdNeed(colA, e.ca.ThresholdLo, e.ca.ThresholdHi, colB, e.cb.ThresholdLo, e.cb.ThresholdHi,
                    e.rules.Count <= 2 ? string.Join(", ", e.rules) : $"{e.rules.Count} kural"));
                report.Questions.Add($"{e.rules.Count} kuralın eşiğini hangi girdi belirliyor: {e.a} / {e.b}? Mevcut varyantlarda ikisi de veriyi aynı şekilde ayırıyor.");
                var example = report.Rules.FirstOrDefault(r => r.Id == e.rules[0])?.Description;
                report.OpenQuestions.Add(new OpenQuestion
                {
                    Id = "girdi:" + kv.Key,
                    Kind = QuestionKind.ChooseInput,
                    Text = $"{e.rules.Count} kural {e.a} değerine mi, {e.b} değerine mi bağlı?",
                    Detail = $"Bu kurallar (ör. {example}) bir sınırda değişiyor. Mevcut varyantlarda {e.a} ve {e.b} aynı ayrımı yapıyor; " +
                             "hangisinin belirlediği veriden anlaşılamıyor.",
                    RuleIds = e.rules.Distinct().ToList(),
                    Options = { new QuestionOption { Label = e.a, Value = e.a }, new QuestionOption { Label = e.b, Value = e.b } },
                    Choices = choices[kv.Key],
                });
            }

            foreach (var (_, rule, c) in chosen.Where(x => !handled.Contains(x.rule.Id)))
            {
                if (c.ThresholdInput != null && c.ThresholdHi - c.ThresholdLo > 1e-9)
                    needs.Add(VariantPlanner.ThresholdNeed(c.ThresholdInput, c.ThresholdLo, c.ThresholdHi, Training(c.ThresholdInput), rule.Id));
                else if (c.OffsetInput != null && c.OffsetStep > 0)
                    needs.Add(VariantPlanner.OffsetNeed(c.OffsetInput, c.OffsetStep, c.OffsetLo, c.OffsetHi, Training(c.OffsetInput), rule.Id));
                else if (c.SupportInput != null && c.SupportValues.Count == 2)
                    needs.Add(VariantPlanner.SupportNeed(c.SupportInput, c.SupportValues, rule.Id));
            }

        }

        /// <summary>
        /// Girdilerle açıklanamayan (ya da zayıf açıklanan) bir değer, kuralı bilinen başka bir değerle doğrusal
        /// ilişkili olabilir (ör. ikinci pim deliği = 2 × birinci pim deliği − 35). Bu durumda bilinen değerin
        /// formülü bir ara değişken yapılır ve yeni kural onu kullanır.
        /// </summary>
        private static void DeriveFromOtherValues(List<(Observation obs, Rule rule, RelationCandidate candidate)> chosen,
            InferenceReport report, RuleContext ctx, HashSet<string> handled)
        {
            var observations = ctx.Observations;
            var samples = ctx.Samples;
            var tolerance = ctx.Tolerance;
            var usedIds = ctx.UsedIds;
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
                            Scope = ctx.VariableScope,
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
                        var target = ctx.TargetOf(obs);
                        report.Rules.Add(new Rule
                        {
                            Id = UniqueId(MakeId(target), usedIds),
                            Description = obs.Label,
                            Target = target,
                            Scope = ctx.ScopeOf(obs),
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
        private static void DerivePitches(List<(Observation obs, Rule rule, RelationCandidate candidate)> chosen,
            InferenceReport report, RuleContext ctx, HashSet<string> handled)
        {
            var observations = ctx.Observations;
            var columns = ctx.Columns;
            var usedIds = ctx.UsedIds;
            // Adet adayı: kuralı bulunmuş tam sayı değerler. Güveni düşük olabilir (ör. ofseti belirsiz basamak fonksiyonu);
            // aralık formülünün tam uyması zaten güçlü kanıttır ve ofseti de kesinleştirir.
            var counts = chosen
                .Where(c => c.candidate.Confidence >= 0.2 && c.obs.Values.Values.All(v => v.Kind == ValueKind.Number &&
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
                        // y yuvarlanmış olduğu için tolerans adet ile büyür (yukarı/aşağı yuvarlamada bir tam basamak).
                        double tol = Math.Pow(10, -digits) * zs.Max(Math.Abs) + 1e-6;
                        foreach (var x in numericInputs)
                        {
                            var xs = names.Select(n => x.Values[n].AsNumber()).ToArray();
                            var fit = RelationFinder.ExactLinear(xs, ks, tol);
                            if (fit == null || Math.Abs(fit.Value.a) < 1e-9) continue;
                            // Kontrol: formül her varyantta y'yi aynı basamağa yuvarlanmış olarak vermeli (ROUND, ROUNDUP ya da ROUNDDOWN).
                            string? rounding = null;
                            foreach (var mode in new[] { "ROUND", "ROUNDUP", "ROUNDDOWN" })
                            {
                                bool ok = true;
                                for (int i = 0; i < ys.Length && ok; i++)
                                    ok = Math.Abs(RoundAs(mode, (fit.Value.a * xs[i] + fit.Value.b) / zs[i], digits) - ys[i]) < 1e-6;
                                if (ok)
                                {
                                    rounding = mode;
                                    break;
                                }
                            }
                            if (rounding == null) continue;

                            // Çapraz kanıt: aralık formülündeki uzunluk (ör. Genişlik - 18) adet formülünde belirsiz kalan
                            // sabiti belirler, çünkü ikisi aynı uzunluğu böler.
                            var cand = count.candidate;
                            var newExpr = cand.ReformWithOffset != null && cand.StepInput == x.Name && Math.Abs(fit.Value.a - 1) < 1e-9
                                ? cand.ReformWithOffset(fit.Value.b)
                                : null;
                            if (newExpr != null)
                            {
                                var oldExpr = count.rule.Expression;
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
                                    Scope = ctx.VariableScope,
                                    Expression = count.rule.Expression,
                                    Description = $"Adet: {count.obs.Label} (aralık formülleri buna bağlı).",
                                });
                            }
                            var denominator = Math.Abs(c) < 1e-9 ? varName : $"({varName} {(c < 0 ? "-" : "+")} {NumberUtil.Fmt(Math.Abs(c))})";
                            var numerator = NumberUtil.Linear(new[] { (fit.Value.a, x.Name) }, fit.Value.b);
                            var target = ctx.TargetOf(obs);
                            report.Rules.Add(new Rule
                            {
                                Id = UniqueId(MakeId(target), usedIds),
                                Description = obs.Label,
                                Target = target,
                                Scope = ctx.ScopeOf(obs),
                                Expression = $"{rounding}(({numerator}) / {denominator}, {digits})",
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

        /// <summary>Kural motorundaki ROUND / ROUNDUP / ROUNDDOWN ile aynı yuvarlama.</summary>
        private static double RoundAs(string mode, double v, int digits)
        {
            double factor = Math.Pow(10, digits);
            double scaled = Math.Round(v * factor, 9);
            double r = mode == "ROUNDUP" ? Math.Sign(scaled) * Math.Ceiling(Math.Abs(scaled))
                : mode == "ROUNDDOWN" ? Math.Sign(scaled) * Math.Floor(Math.Abs(scaled))
                : Math.Round(scaled, MidpointRounding.AwayFromZero);
            return r / factor;
        }

        private static int Decimals(double v)
        {
            for (int d = 0; d <= 6; d++)
                if (Math.Abs(v - Math.Round(v, d)) < 1e-9) return d;
            return 6;
        }

        internal static string Summarize(Observation o, int sampleCount)
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

        internal static List<InputColumn> BuildInputColumns(IReadOnlyList<VariantSample> samples, List<InputDefinition> definitions,
            List<string> notes)
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
                    notes.Add($"'{name}' girdisi bazı varyantlarda eksik; o varyantlar bu girdiyle ilgili aramalarda yok sayılır.");
                columns.Add(ColumnFor(name, name, values, out var def));
                definitions.Add(def);
            }
            return columns;
        }

        /// <summary>Gözlenen değerlerden girdi sütunu ve tanımı (tip, aralık, seçenekler, varsayılan).</summary>
        internal static InputColumn ColumnFor(string name, string label, Dictionary<string, Value> values, out InputDefinition def)
        {
            var safeName = SanitizeName(name);
            var vals = values.Values.ToList();
            ColumnKind kind;
            if (vals.All(v => v.Kind == ValueKind.Number))
            {
                kind = ColumnKind.Number;
                var nums = vals.Select(v => v.AsNumber()).OrderBy(d => d).ToList();
                def = new InputDefinition
                {
                    Name = safeName, Label = label, Type = InputType.Number,
                    Min = nums.First(), Max = nums.Last(), Default = Value.FormatNumber(nums[nums.Count / 2]),
                    Description = $"Varyantlarda görülen aralık: {Value.FormatNumber(nums.First())} – {Value.FormatNumber(nums.Last())}",
                };
            }
            else if (vals.All(v => v.Kind == ValueKind.Bool))
            {
                kind = ColumnKind.Bool;
                def = new InputDefinition { Name = safeName, Label = label, Type = InputType.Bool, Default = "false" };
            }
            else
            {
                kind = ColumnKind.Category;
                var options = vals.Select(v => v.AsText()).Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(o => o, StringComparer.OrdinalIgnoreCase).ToList();
                def = new InputDefinition
                {
                    Name = safeName, Label = label,
                    Type = options.Count <= 30 ? InputType.Choice : InputType.Text,
                    Options = options.Count <= 30 ? options : new List<string>(),
                    Default = options.First(),
                };
            }
            return new InputColumn(safeName, kind, values);
        }

        internal static RuleTarget MapToMaster(RuleTarget target, ModelSnapshot? master, ObservationExtractor extractor)
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

        internal static string MakeId(RuleTarget t)
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

        internal static string UniqueId(string id, HashSet<string> used)
        {
            var candidate = id;
            for (int i = 2; !used.Add(candidate); i++) candidate = id + "_" + i;
            return candidate;
        }
    }
}
