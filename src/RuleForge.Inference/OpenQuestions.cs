using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using RuleForge.Core.Expressions;
using RuleForge.Core.Rules;

namespace RuleForge.Inference
{
    /// <summary>Soru türleri: cevap nasıl alınır ve kurallara nasıl uygulanır.</summary>
    public static class QuestionKind
    {
        /// <summary>Eşik değeri belirsiz (veri bir aralığı destekliyor): kullanıcı sayıyı söyler.</summary>
        public const string Threshold = "esik";

        /// <summary>Eşiği hangi girdinin belirlediği belirsiz: kullanıcı girdiyi seçer.</summary>
        public const string ChooseInput = "girdi-secimi";

        /// <summary>Veriye birden çok formül uyuyor: kullanıcı birini seçer ya da kendisi yazar.</summary>
        public const string ChooseFormula = "formul-secimi";

        /// <summary>Formül az veriye dayanıyor: kullanıcı doğrular ya da düzeltir.</summary>
        public const string Confirm = "dogrula";

        /// <summary>İki değer hep birlikte değişti: aynı girdiden mi, ayrı bir seçim mi?</summary>
        public const string SeparateInput = "ayri-girdi";

        /// <summary>Değer hiçbir girdiyle açıklanamadı: kullanıcı formülü yazar ya da önemsiz der.</summary>
        public const string Unexplained = "aciklanamayan";
    }

    public sealed class NewRuleTarget
    {
        public RuleTarget Target { get; set; } = new RuleTarget();
        public string? Scope { get; set; }
        public string Label { get; set; } = string.Empty;
    }

    public sealed class QuestionOption
    {
        public string Label { get; set; } = string.Empty;
        public string Value { get; set; } = string.Empty;
    }

    /// <summary>Bir varyantta formülün vermesi gereken değer ve o varyantın girdileri (cevabı veriyle sınamak için).</summary>
    public sealed class QuestionCheck
    {
        public string Variant { get; set; } = string.Empty;
        public string Expected { get; set; } = string.Empty;
        public Dictionary<string, string> Inputs { get; set; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Kullanıcının cevaplaması gereken, cevabı kurallara uygulanabilen soru.</summary>
    public sealed class OpenQuestion
    {
        /// <summary>Kararlı kimlik (yeniden çıkarımda aynı soru aynı kimliği alır; cevap yeniden uygulanır).</summary>
        public string Id { get; set; } = string.Empty;

        public string Kind { get; set; } = string.Empty;
        public string Text { get; set; } = string.Empty;

        /// <summary>Neden soruluyor (veri ne gösteriyor), kısa.</summary>
        public string? Detail { get; set; }

        public List<string> RuleIds { get; set; } = new List<string>();
        public List<QuestionOption> Options { get; set; } = new List<QuestionOption>();

        /// <summary>Kullanıcı kendi formülünü yazabilir.</summary>
        public bool AllowFormula { get; set; }

        public string? Variable { get; set; }
        public string? Input { get; set; }
        public double? Min { get; set; }
        public double? Max { get; set; }
        public double? Current { get; set; }

        /// <summary>Kullanıcı bilmiyorsa DriveWorks'te denenecek değerler (ör. eşiği daraltan girdiler).</summary>
        public List<string> TryValues { get; set; } = new List<string>();

        /// <summary>Girdi seçimi: girdi → (kural → o girdiyle formül).</summary>
        public Dictionary<string, Dictionary<string, string>> Choices { get; set; } = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);

        /// <summary>Açıklanamayan değer(ler) için yazılacak yeni kuralların hedefleri.</summary>
        public List<NewRuleTarget> Targets { get; set; } = new List<NewRuleTarget>();

        /// <summary>Ayrı girdi sorusunda yeni alanın etiketi.</summary>
        public string? Label { get; set; }

        public List<QuestionCheck> Checks { get; set; } = new List<QuestionCheck>();
    }

    /// <summary>Kullanıcının cevabı. Choice: seçeneğin değeri ya da "formul"; Number: eşik; Formula: elle yazılan formül.</summary>
    public sealed class QuestionAnswer
    {
        public string? Choice { get; set; }
        public double? Number { get; set; }
        public string? Formula { get; set; }

        /// <summary>Mühendisin açıklaması (şirket bilgisi): neden böyle.</summary>
        public string? Note { get; set; }

        public string? By { get; set; }
        public DateTime Date { get; set; } = DateTime.Now;
    }

    public sealed class AnswerResult
    {
        public bool Ok { get; set; }
        public string Message { get; set; } = string.Empty;
    }

    /// <summary>Cevapları kural setine uygular ve elle yazılan formülleri varyant verisiyle sınar.</summary>
    public static class QuestionApplier
    {
        public static AnswerResult Apply(RuleSet rules, OpenQuestion q, QuestionAnswer a)
        {
            Rule? R(string id) => rules.FindRule(id);
            void Approve(IEnumerable<string> ids)
            {
                foreach (var r in ids.Select(R).Where(r => r != null)) r!.Status = RuleStatus.Approved;
            }
            AnswerResult Fail(string m) => new AnswerResult { Ok = false, Message = m };

            switch (q.Kind)
            {
                case QuestionKind.Threshold:
                {
                    if (a.Number == null) return Fail("Bir sayı girin.");
                    var v = a.Number.Value;
                    if (q.Min != null && q.Max != null && (v <= q.Min || v > q.Max))
                        return Fail($"Varyantlarla çelişiyor: eşik {Fmt(q.Min.Value)}'den büyük ve en fazla {Fmt(q.Max.Value)} olmalı " +
                                    $"(varyantlarda {Fmt(q.Min.Value)} ve {Fmt(q.Max.Value)} farklı sonuç veriyor).");
                    var variable = rules.FindVariable(q.Variable ?? string.Empty);
                    if (variable == null) return Fail("Değişken bulunamadı: " + q.Variable);
                    variable.Expression = Fmt(v);
                    variable.Description = (variable.Description ?? string.Empty).Split(new[] { " Kullanıcı cevabı:" }, StringSplitOptions.None)[0] +
                                           $" Kullanıcı cevabı: {Fmt(v)}.";
                    Approve(q.RuleIds);
                    return new AnswerResult { Ok = true, Message = $"{q.Variable} = {Fmt(v)}; {q.RuleIds.Count} kural güncellendi ve onaylandı." };
                }
                case QuestionKind.ChooseInput:
                {
                    if (a.Choice == null || !q.Choices.TryGetValue(a.Choice, out var map)) return Fail("Bir girdi seçin.");
                    foreach (var kv in map)
                        if (R(kv.Key) is Rule r) r.Expression = kv.Value;
                    Approve(map.Keys);
                    return new AnswerResult { Ok = true, Message = $"{map.Count} kural {a.Choice} girdisine bağlandı ve onaylandı." };
                }
                case QuestionKind.ChooseFormula:
                case QuestionKind.Confirm:
                {
                    string? formula = a.Choice == "formul" ? a.Formula : a.Choice == "dogru" ? null : a.Choice;
                    if (a.Choice == null) return Fail("Bir seçenek işaretleyin.");
                    if (formula != null)
                    {
                        var check = Check(rules, q, formula);
                        if (!check.Ok) return check;
                        foreach (var r in q.RuleIds.Select(R).Where(r => r != null)) r!.Expression = formula.Trim();
                        Approve(q.RuleIds);
                        return new AnswerResult { Ok = true, Message = "Formül uygulandı ve onaylandı. " + check.Message };
                    }
                    Approve(q.RuleIds);
                    return new AnswerResult { Ok = true, Message = $"{q.RuleIds.Count} kural onaylandı." };
                }
                case QuestionKind.SeparateInput:
                {
                    if (a.Choice == "ayni")
                    {
                        Approve(q.RuleIds);
                        return new AnswerResult { Ok = true, Message = "Aynı girdiden geldiği kabul edildi; kurallar onaylandı." };
                    }
                    if (a.Choice != "ayri") return Fail("Bir seçenek işaretleyin.");
                    int n = 0;
                    // Aynı formülü paylaşan kurallar (sol ve sağ kapağın kulpu) tek bir seçim alanına bağlanır.
                    foreach (var group in q.RuleIds.Select(R).Where(r => r != null).GroupBy(r => r!.Expression))
                    {
                        var outputs = SwitchOutputs(group.Key);
                        if (outputs.Count < 2) continue;
                        var first = group.First()!;
                        var part = first.Target.Component != null
                            ? Regex.Replace(first.Target.Component.Split('/').Last(), @"-\d+$", string.Empty)
                            : q.Label ?? first.Id;
                        var name = UniqueInputName(rules, Ascii(part) + "_Secimi");
                        rules.Inputs.Add(new InputDefinition
                        {
                            Name = name,
                            Label = part + " seçimi",
                            Type = InputType.Choice,
                            Options = outputs,
                            Default = outputs[0],
                            Description = $"Ayrı seçim (kullanıcı cevabı); önceden {q.Input} girdisine bağlıydı.",
                        });
                        foreach (var r in group)
                        {
                            r!.Expression = name;
                            r.Status = RuleStatus.Approved;
                            n++;
                        }
                    }
                    return n == 0
                        ? Fail("Bu kural otomatik olarak ayrı bir seçime çevrilemedi.")
                        : new AnswerResult { Ok = true, Message = $"{n} kural için formda ayrı bir seçim alanı oluşturuldu." };
                }
                case QuestionKind.Unexplained:
                {
                    if (a.Choice == "onemsiz") return new AnswerResult { Ok = true, Message = "Önemsiz olarak işaretlendi; kural yazılmadı." };
                    if (string.IsNullOrWhiteSpace(a.Formula) || q.Targets.Count == 0) return Fail("Formülü yazın ya da \"önemsiz\" seçin.");
                    var check = Check(rules, q, a.Formula!);
                    if (!check.Ok) return check;
                    foreach (var nt in q.Targets)
                    {
                        var id = "elle_" + RuleInferencer.SanitizeName(nt.Label);
                        rules.Rules.RemoveAll(r => string.Equals(r.Id, id, StringComparison.OrdinalIgnoreCase));
                        rules.Rules.Add(new Rule
                        {
                            Id = id,
                            Description = nt.Label,
                            Target = nt.Target,
                            Scope = nt.Scope,
                            Expression = a.Formula!.Trim(),
                            Status = RuleStatus.Approved,
                            Source = RuleSource.Manual,
                            Confidence = 1,
                            Evidence = "Kullanıcı yazdı. " + check.Message,
                        });
                    }
                    return new AnswerResult { Ok = true, Message = (q.Targets.Count == 1 ? "Kural eklendi. " : $"{q.Targets.Count} kural eklendi. ") + check.Message };
                }
                default:
                    return Fail("Bilinmeyen soru türü.");
            }
        }

        /// <summary>
        /// Formülü her varyantın girdileriyle hesaplar, varyantta görülen değerle karşılaştırır. Formül hatalıysa ya da
        /// hiçbir varyantta tutmuyorsa reddedilir; bazılarında tutmuyorsa uyarıyla kabul edilir (veri eksik/gürültülü olabilir).
        /// </summary>
        public static AnswerResult Check(RuleSet rules, OpenQuestion q, string formula)
        {
            if (!Expression.TryParse(formula, out var expr, out var error)) return new AnswerResult { Ok = false, Message = "Formül okunamadı: " + error };
            if (q.Checks.Count == 0) return new AnswerResult { Ok = true, Message = "Doğrulanacak varyant verisi yok." };
            int ok = 0, total = 0;
            var wrong = new List<string>();
            foreach (var c in q.Checks)
            {
                var vars = new Dictionary<string, Value>(StringComparer.OrdinalIgnoreCase);
                foreach (var kv in c.Inputs) vars[kv.Key] = Value.Parse(kv.Value);
                foreach (var v in rules.Variables)
                {
                    try { vars[v.Name] = Expression.Evaluate(v.Expression, vars); }
                    catch (Exception) { /* değişken bu varyantta hesaplanamadı */ }
                }
                Value got;
                try { got = Expression.Evaluate(formula, vars); }
                catch (Exception ex) { return new AnswerResult { Ok = false, Message = $"Formül hesaplanamadı ({c.Variant}): {ex.Message}" }; }
                total++;
                var expected = Value.Parse(c.Expected);
                bool same = expected.Kind == ValueKind.Number && got.Kind == ValueKind.Number
                    ? Math.Abs(expected.AsNumber() - got.AsNumber()) <= 0.011
                    : Value.LooseEquals(expected, got);
                if (same) ok++;
                else if (wrong.Count < 3) wrong.Add($"{c.Variant}: beklenen {c.Expected}, formül {got.AsText()}");
            }
            if (ok == 0) return new AnswerResult { Ok = false, Message = $"Bu formül hiçbir varyantta tutmuyor ({string.Join("; ", wrong)})." };
            return new AnswerResult
            {
                Ok = true,
                Message = ok == total ? $"Formül {total} varyantın hepsinde tutuyor." : $"Formül {total} varyantın {ok}'inde tutuyor; tutmayanlar: {string.Join("; ", wrong)}.",
            };
        }


        private static readonly Regex SwitchCall = new Regex(@"^\s*SWITCH\(\s*[A-Za-z_][A-Za-z0-9_]*\s*,(?<args>.*)\)\s*$", RegexOptions.Singleline);
        private static readonly Regex Literal = new Regex(@"-?\d+(?:\.\d+)?|""[^""]*""");

        /// <summary>"SWITCH(Girdi, k1, "a", k2, "b")" → ["a", "b"] (sonuç değerleri, sırayla, tekrarsız).</summary>
        internal static List<string> SwitchOutputs(string expression)
        {
            var m = SwitchCall.Match(expression ?? string.Empty);
            if (!m.Success) return new List<string>();
            var lits = Literal.Matches(m.Groups["args"].Value).Cast<Match>().Select(x => x.Value.Trim('"')).ToList();
            var outputs = new List<string>();
            for (int i = 1; i < lits.Count; i += 2)
                if (!outputs.Contains(lits[i])) outputs.Add(lits[i]);
            return outputs;
        }

        /// <summary>Formüllerde kullanılacak ASCII ad: "Bedroom Knob Oak" → "Bedroom_Knob_Oak" (Türkçe harfler sadeleşir).</summary>
        private static string Ascii(string s)
        {
            var map = new Dictionary<char, char> { ['ç'] = 'c', ['ğ'] = 'g', ['ı'] = 'i', ['ö'] = 'o', ['ş'] = 's', ['ü'] = 'u', ['Ç'] = 'C', ['Ğ'] = 'G', ['İ'] = 'I', ['Ö'] = 'O', ['Ş'] = 'S', ['Ü'] = 'U' };
            var t = new string(s.Select(c => map.TryGetValue(c, out var r) ? r : c).ToArray());
            return Regex.Replace(Regex.Replace(t, @"[^A-Za-z0-9]+", "_"), "^_+|_+$", string.Empty);
        }

        private static string UniqueInputName(RuleSet rules, string name)
        {
            var n = name;
            for (int i = 2; rules.FindInput(n) != null; i++) n = name + "_" + i.ToString(CultureInfo.InvariantCulture);
            return n;
        }

        private static string Fmt(double v) => Value.FormatNumber(v);
    }
}
