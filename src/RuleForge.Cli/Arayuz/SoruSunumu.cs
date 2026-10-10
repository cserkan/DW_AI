using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using RuleForge.Core.Expressions;
using RuleForge.Core.Rules;
using RuleForge.Inference;

namespace RuleForge.Cli.Arayuz
{
    /// <summary>Etkilenen bir parça ve içindeki unsurlar (soru kartının altında listelenir).</summary>
    public sealed class EtkilenenParca
    {
        public string Parca { get; set; } = string.Empty;
        public List<EtkilenenUnsur> Unsurlar { get; set; } = new List<EtkilenenUnsur>();
    }

    public sealed class EtkilenenUnsur
    {
        public string Ad { get; set; } = string.Empty;

        /// <summary>Ne değişiyor: "ölçü", "görünür / gizli", "montaj bağlantısı açık / kapalı" …</summary>
        public string Ne { get; set; } = string.Empty;

        /// <summary>Eşik sorularında: sınırın altında ve üstünde ne oluyor (ör. "gizli" → "görünür").</summary>
        public string? Altinda { get; set; }
        public string? Ustunde { get; set; }
    }

    /// <summary>
    /// Soruları mühendisin diliyle yazar: kural kimliği, formül değişkeni ya da "bastırılmış mı" gibi teknik ifadeler yerine
    /// "«Uzunluk» kaç olunca model değişiyor?" ve altında etkilenen parça/unsur listesi.
    /// </summary>
    internal static class SoruSunumu
    {
        private static readonly Regex MateAdi = new Regex(@"^(Coincident|Concentric|Distance|Parallel|Perpendicular|Tangent|Angle|Lock|Width|Symmetric|Gear|Path|Limit|Mate)\d*$",
            RegexOptions.IgnoreCase);

        public static (string Metin, string? Aciklama, string? Hesap) Metin(OpenQuestion q, RuleSet rules, Func<string, string> okunur)
        {
            string G(string? input) => input == null ? "?" : okunur(input).Trim();
            var ilk = q.RuleIds.Select(rules.FindRule).FirstOrDefault(r => r != null);
            switch (q.Kind)
            {
                case QuestionKind.Threshold:
                    return ($"{G(q.Input)} kaç olunca model değişiyor?",
                        $"{G(q.Input)} {Fmt(q.Min)} iken bir şekilde, {Fmt(q.Max)} iken başka şekilde oluyor. Sınır bu ikisinin arasında bir yerde. " +
                        $"Şimdilik {Fmt(q.Current)} kabul ettik. Aşağıdaki parçalar bu sınıra göre değişiyor.", null);
                case QuestionKind.ChooseInput:
                {
                    var ad = q.Options.Select(o => G(o.Value)).ToList();
                    return ($"Aşağıdaki değişiklikler hangisine bağlı: {ad[0]} veya {ad[1]}?",
                        $"Elimizdeki varyantlarda {ad[0]} ve {ad[1]} hep birlikte değiştiği için hangisi olduğunu ayırt edemedik.", null);
                }
                case QuestionKind.SeparateInput:
                {
                    var parca = ilk?.Target.Component != null ? KopyaNoSiz(ilk.Target.Component.Split('/').Last()) : q.Label ?? "Bu seçim";
                    return ($"«{parca}» her zaman {G(q.Input)} seçimine göre mi değişiyor?",
                        $"Varyantlarda {G(q.Input)} değiştikçe «{parca}» parçası da hep değişti. Müşteri bunu ayrıca seçebiliyorsa formda ayrı bir seçim alanı açarız.", null);
                }
                case QuestionKind.ChooseFormula:
                    return ($"«{UnsurAdi(ilk)}» {NeKisa(ilk)} nasıl hesaplanıyor?",
                        "Varyantlara birden fazla hesap uyuyor. Doğru olanı seçin ya da kendiniz yazın.", null);
                case QuestionKind.Confirm:
                {
                    // Kardeş kurallar (sol/sağ kapak) genelde aynı unsurdur: "«Bottom Plane» ölçüsü (2 parçada)".
                    var unsurlar = q.RuleIds.Select(rules.FindRule).Where(r => r != null).Select(UnsurAdi).Distinct().ToList();
                    var konu = unsurlar.Count == 1 ? $"«{unsurlar[0]}» {NeKisa(ilk)}" + (q.RuleIds.Count > 1 ? $" ({q.RuleIds.Count} parçada)" : "")
                                                   : $"Bu {q.RuleIds.Count} değer";
                    return ($"{konu} böyle mi hesaplanıyor?",
                        "Az sayıda varyanta dayandığı için emin olmak istiyoruz.", ilk == null ? null : okunur(ilk.Expression));
                }
                case QuestionKind.Unexplained:
                {
                    var adlar = q.Targets.Select(t => t.Target.Name ?? t.Label).Distinct().ToList();
                    var gorulen = q.Checks.Select(c => c.Expected).Distinct().Take(4).ToList();
                    return (adlar.Count == 1 ? $"«{adlar[0]}» değeri nereden geliyor?" : "Bu değerler nereden geliyor?",
                        "Varyantlarda değişiyor ama hiçbir girdiyle ilişkisini bulamadık." +
                        (gorulen.Count > 0 ? $" Görülen değerler: {string.Join(", ", gorulen)}" + (q.Checks.Select(c => c.Expected).Distinct().Count() > 4 ? " …" : "") + "." : ""), null);
                }
                default:
                    return (q.Text, q.Detail, null);
            }
        }

        /// <summary>Sorunun etkilediği parçalar, içlerindeki unsurlar ve (eşik sorularında) sınırın iki yanında ne olduğu.</summary>
        public static List<EtkilenenParca> Etkilenenler(OpenQuestion q, RuleSet rules)
        {
            var hedefler = q.RuleIds.Select(rules.FindRule).Where(r => r != null).Select(r => (r!.Target, (Rule?)r))
                .Concat(q.Targets.Select(t => (t.Target, (Rule?)null))).ToList();
            var sonuc = new List<EtkilenenParca>();
            foreach (var (t, rule) in hedefler)
            {
                var (parca, unsur, ne) = Tanim(t);
                var p = sonuc.FirstOrDefault(x => x.Parca == parca);
                if (p == null) sonuc.Add(p = new EtkilenenParca { Parca = parca });
                var u = new EtkilenenUnsur { Ad = unsur, Ne = ne };
                if (q.Kind == QuestionKind.Threshold && rule != null && q.Input != null && q.Min != null && q.Max != null)
                {
                    u.Altinda = Deger(rules, rule, q.Input, q.Min.Value);
                    u.Ustunde = Deger(rules, rule, q.Input, q.Max.Value);
                }
                if (p.Unsurlar.Any(x => x.Ad == u.Ad && x.Ne == u.Ne)) continue;
                p.Unsurlar.Add(u);
            }
            return sonuc;
        }

        /// <summary>Hedef → (parça, unsur, ne değişiyor).</summary>
        private static (string Parca, string Unsur, string Ne) Tanim(RuleTarget t)
        {
            string Belge(string? d) => d == null ? "Ana montaj" : Path.GetFileNameWithoutExtension(d);
            switch (t.Kind)
            {
                case TargetKind.Dimension:
                {
                    var parts = (t.Name ?? "").Split('@');
                    return (Belge(t.Document), parts.Length > 1 ? $"{parts[1]} ({parts[0]})" : t.Name ?? "", "ölçü");
                }
                case TargetKind.FeatureSuppression:
                    return MateAdi.IsMatch(t.Name ?? "")
                        ? (Belge(t.Document), t.Name ?? "", "montaj bağlantısı (mate) açık / kapalı")
                        : (Belge(t.Document), t.Name ?? "", "unsur açık / kapalı");
                case TargetKind.ComponentSuppression:
                    return (UstMontaj(t.Component), BilesenAdi(t.Component), "parça görünür / gizli");
                case TargetKind.ComponentReplace:
                    return (UstMontaj(t.Component), BilesenAdi(t.Component), "hangi dosya kullanılıyor");
                case TargetKind.Configuration:
                    return t.Component != null ? (UstMontaj(t.Component), BilesenAdi(t.Component), "konfigürasyon")
                                               : (Belge(t.Document), "Aktif konfigürasyon", "konfigürasyon");
                case TargetKind.CustomProperty:
                    return (Belge(t.Document), t.Name ?? "", "özel özellik");
                case TargetKind.GlobalVariable:
                    return (Belge(t.Document), t.Name ?? "", "denklem değişkeni");
                case TargetKind.OutputFileName:
                    return ("Ana montaj", "Dosya adı", "dosya adı");
                default:
                    return (Belge(t.Document), t.Name ?? t.Component ?? "", t.Kind.ToString());
            }
        }

        private static string UstMontaj(string? component)
        {
            var parts = (component ?? "").Split('/');
            return parts.Length < 2 ? "Ana montaj" : KopyaNoSiz(parts[parts.Length - 2]);
        }

        private static string BilesenAdi(string? component) => (component ?? "").Split('/').Last();

        private static string KopyaNoSiz(string s) => Regex.Replace(s, @"-\d+$", string.Empty);

        private static string UnsurAdi(Rule? r)
        {
            if (r == null) return "Bu değer";
            var (_, unsur, _) = Tanim(r.Target);
            return Regex.Replace(unsur, @"\s*\(.*\)$", string.Empty);
        }

        private static string NeKisa(Rule? r)
        {
            switch (r?.Target.Kind)
            {
                case TargetKind.Dimension: return "ölçüsü";
                case TargetKind.CustomProperty: return "özelliği";
                case TargetKind.GlobalVariable: return "değişkeni";
                default: return "değeri";
            }
        }

        /// <summary>Kuralı girdinin verilen değeriyle hesaplar ve okunur yazar (bastırma: gizli/görünür). Hesaplanamazsa null.</summary>
        private static string? Deger(RuleSet rules, Rule rule, string input, double x)
        {
            var vars = new Dictionary<string, Value>(StringComparer.OrdinalIgnoreCase) { [input] = Value.Parse(Fmt(x)) };
            foreach (var v in rules.Variables)
            {
                try { vars[v.Name] = Expression.Evaluate(v.Expression, vars); }
                catch (Exception) { /* bu değişken başka girdilere bağlı */ }
            }
            Value got;
            try { got = Expression.Evaluate(rule.Expression, vars); }
            catch (Exception) { return null; }
            switch (rule.Target.Kind)
            {
                case TargetKind.ComponentSuppression: return got.AsBool() ? "gizli" : "görünür";
                case TargetKind.FeatureSuppression: return got.AsBool() ? "kapalı" : "açık";
                case TargetKind.ComponentReplace: return Path.GetFileNameWithoutExtension(got.AsText());
                default: return got.AsText();
            }
        }

        private static string Fmt(double? v) => v == null ? "?" : Value.FormatNumber(v.Value);
    }
}
