using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using RuleForge.Core.Engine;
using RuleForge.Core.Json;
using RuleForge.Core.Rules;

namespace RuleForge.Cli
{
    internal static class Program
    {
        private const string Usage = @"RuleForge — yapay zekâ destekli SolidWorks konfigüratörü

Kullanım: ruleforge <komut> [seçenekler]

  extract   <montaj.SLDASM...> [-o snapshot.json | --out-dir klasör] [--visible]
            Montajı okuyup model snapshot'ı (JSON) üretir.                          [Windows + SolidWorks]
  extract-variants <klasör> [--out-dir varyantlar] [--list]
            Klasördeki varyantların ÜST montajlarını otomatik bulur (alt montajları atlar) ve okur.
            --list: sadece bulunan üst montajları listeler, okumaz.                [Windows + SolidWorks]
  infer     <snapshot.json... | klasör> [--inputs girdiler.csv] [--master master.json]
            [--name-pattern REGEX] [--input Ad=gözlem_anahtarı] [-o kurallar.json]
            Varyantlardan kural önerileri çıkarır (deterministik).
  crossval  <snapshot.json... | klasör> [infer ile aynı seçenekler] [-o rapor.txt]
            Her varyantı sırayla çıkarıp kuralları kalanlardan öğrenir, çıkarılanı tahmin eder.
            Cevap anahtarı olmadan kuralların yeni girdilerde ne kadar güvenilir olduğunu ölçer.
  compare   <uretilen.json> <beklenen.json> [-o fark.txt]
            İki modeli karşılaştırır (ör. RuleForge'un ürettiği ile DriveWorks'ün ürettiği varyant).
            Dosya adları farklı olabilir; parçalar yapılarına göre eşleştirilir.
  chat      --rules kurallar.json [--snapshot master.json] [--report cikarim.txt] [--effort high]
            Claude ile sohbet ederek kural yazar/düzeltir. ANTHROPIC_API_KEY gerekir.
  validate  --rules kurallar.json [--snapshot master.json]
  approve   --rules kurallar.json (<kural_id>... | --all [--min-confidence 0.9])
  eval      --rules kurallar.json [--include-proposed] [Ad=Değer ...] [""Tablo.Sütun=v1;v2;v3"" ...]
            Girdilerle kuralları çalıştırır ve modele uygulanacak eylemleri listeler.
            Tekrarlanan modül (tablo) satırları: ""Tablo.Sütun=1200;800"" iki satır (çift tırnak içinde).
  generate  --rules kurallar.json --master master.SLDASM --out klasör [Ad=Değer ...]
            [--pdf] [--step] [--include-proposed] [--visible] [--dry-run]
            [--library klasör | --no-library] [--root kök.SLDASM] [--slots ""Ad-5;Ad-8""]
            Yeni sipariş modelini üretir. Parçalar kütüphaneye benzersiz adlarla yazılır,
            aynısı daha önce üretildiyse yeniden kullanılır.                       [Windows + SolidWorks]
  arayuz    [--projeler C:\RuleForge\Projeler] [--port 5050] [--no-browser]
            Tarayıcı arayüzü: varyantları yükle, kuralları çıkar ve onayla, formla üret. [Windows + SolidWorks]
";

        private static async Task<int> Main(string[] argv)
        {
            Console.OutputEncoding = Encoding.UTF8;
            try
            {
                Console.InputEncoding = Encoding.UTF8;
            }
            catch (IOException)
            {
                // Girdi yönlendirilmişse (pipe) desteklenmeyebilir.
            }

            if (argv.Length == 0 || argv[0] == "-h" || argv[0] == "--help" || argv[0] == "help")
            {
                Console.WriteLine(Usage);
                return 0;
            }

            var command = argv[0].ToLowerInvariant();
            var rest = argv.Skip(1).ToArray();
            try
            {
                switch (command)
                {
                    case "extract": return SolidWorksCommands.Extract(new Args(rest, "visible"));
                    case "extract-variants": return SolidWorksCommands.ExtractVariants(new Args(rest, "visible", "list"));
                    case "generate": return SolidWorksCommands.Generate(new Args(rest, "pdf", "step", "include-proposed", "visible", "dry-run", "no-library"));
                    case "arayuz":
                    case "ui": return Arayuz.ArayuzSunucu.Calistir(new Args(rest, "no-browser"));
                    case "infer": return InferCommand.Run(new Args(rest));
                    case "crossval": return InferCommand.CrossValidate(new Args(rest));
                    case "compare": return InferCommand.Compare(new Args(rest));
                    case "chat": return await ChatCommand.RunAsync(new Args(rest)).ConfigureAwait(false);
                    case "validate": return Validate(new Args(rest));
                    case "approve": return Approve(new Args(rest, "all"));
                    case "eval": return Eval(new Args(rest, "include-proposed"));
                    default:
                        Console.Error.WriteLine($"Bilinmeyen komut: {command}\n");
                        Console.WriteLine(Usage);
                        return 2;
                }
            }
            catch (UsageException ex)
            {
                Console.Error.WriteLine("Hata: " + ex.Message);
                return 2;
            }
            catch (Exception ex) when (!(ex is OutOfMemoryException))
            {
                Console.Error.WriteLine("Hata: " + ex.Message);
                if (Environment.GetEnvironmentVariable("RULEFORGE_DEBUG") == "1") Console.Error.WriteLine(ex);
                return 1;
            }
        }

        private static int Validate(Args args)
        {
            var rules = JsonStore.Load<RuleSet>(args.Require("rules"));
            var snapshotPath = args.Get("snapshot");
            var snapshot = snapshotPath != null ? JsonStore.Load<Core.Model.ModelSnapshot>(snapshotPath) : null;
            var issues = RuleSetValidator.Validate(rules, snapshot);
            foreach (var i in issues) Console.WriteLine(i);
            var errors = issues.Count(i => i.Severity == IssueSeverity.Error);
            Console.WriteLine(errors == 0 ? $"Geçerli ({issues.Count} uyarı)." : $"{errors} hata.");
            return errors == 0 ? 0 : 1;
        }

        private static int Approve(Args args)
        {
            var path = args.Require("rules");
            var rules = JsonStore.Load<RuleSet>(path);
            var ids = args.Positional;
            double? min = double.TryParse(args.Get("min-confidence"), System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var m) ? m : (double?)null;
            int count = 0;
            foreach (var rule in rules.Rules.Where(r => r.Status == RuleStatus.Proposed))
            {
                bool selected = args.Flag("all")
                    ? (!min.HasValue || (rule.Confidence ?? 1) >= min.Value)
                    : ids.Any(id => string.Equals(id, rule.Id, StringComparison.OrdinalIgnoreCase));
                if (!selected) continue;
                rule.Status = RuleStatus.Approved;
                count++;
                Console.WriteLine($"✔ {rule.Id}: {rule.Target} = {rule.Expression}");
            }
            JsonStore.Save(rules, path);
            Console.WriteLine($"{count} kural onaylandı.");
            return 0;
        }

        private static int Eval(Args args)
        {
            var rules = JsonStore.Load<RuleSet>(args.Require("rules"));
            var inputs = RuleEngine.ParseAssignments(args.Assignments, out var tables);
            var result = RuleEngine.Evaluate(rules, inputs, new EvaluationOptions { IncludeProposed = args.Flag("include-proposed") }, tables);
            Print(result);
            return result.Success ? 0 : 1;
        }

        internal static void Print(EvaluationResult result)
        {
            if (result.Values.Count > 0)
            {
                Console.WriteLine("Değerler:");
                foreach (var kv in result.Values) Console.WriteLine($"  {kv.Key} = {kv.Value}");
            }
            foreach (var table in result.Rows)
                for (int i = 0; i < table.Value.Count; i++)
                    Console.WriteLine($"  {table.Key} satır {i + 1}: " + string.Join(", ", table.Value[i].Select(kv => $"{kv.Key} = {kv.Value}")));
            if (result.Actions.Count > 0)
            {
                Console.WriteLine("Modele uygulanacak:");
                foreach (var a in result.Actions)
                    Console.WriteLine($"  {a}{(a.Rule.Status == RuleStatus.Proposed ? "   (önerilen)" : "")}");
            }
            foreach (var r in result.SkippedRules) Console.WriteLine($"  (koşul sağlanmadı: {r.Id})");
            foreach (var w in result.Warnings) Console.WriteLine("Uyarı: " + w);
            foreach (var e in result.Errors) Console.WriteLine("HATA: " + e);
        }
    }
}
