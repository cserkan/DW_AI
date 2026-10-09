using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Anthropic;
using RuleForge.AI;
using RuleForge.Core.Engine;
using RuleForge.Core.Json;
using RuleForge.Core.Model;
using RuleForge.Core.Rules;

namespace RuleForge.Cli
{
    internal static class ChatCommand
    {
        private const string Help = @"Komutlar:
  /kurallar               kural listesini göster
  /onayla <id...|hepsi>   kural(lar)ı onayla
  /reddet <id...>         kural(lar)ı reddet
  /dene Ad=Değer ...      kuralları (önerilenler dahil) bu girdilerle çalıştır
  /dogrula                kural setini doğrula
  /cikis                  çık (kural seti her turda otomatik kaydedilir)
Diğer her şey yapay zekâya gider. Ör: ""Boy 3000'i geçince orta destek eklensin.""";

        public static async Task<int> RunAsync(Args args)
        {
            var rulesPath = args.Require("rules");
            var ruleSet = File.Exists(rulesPath) ? JsonStore.Load<RuleSet>(rulesPath) : new RuleSet { Name = Path.GetFileNameWithoutExtension(rulesPath) };
            var snapshot = args.Get("snapshot") is string sp ? JsonStore.Load<ModelSnapshot>(sp) : null;
            var report = args.Get("report") is string rp ? File.ReadAllText(rp) : null;
            if (snapshot == null)
                Console.WriteLine("Uyarı: --snapshot verilmedi; yapay zekâ model ölçü/bileşen adlarını bilemez.");

            var options = new AssistantOptions();
            if (args.Get("effort") is string effort) options.Effort = effort;
            if (args.Get("model") is string model) options.Model = model;

            var client = new AnthropicClient(); // ANTHROPIC_API_KEY ortam değişkeninden
            var assistant = new RuleAssistant(client, ruleSet, snapshot, report, options);

            Console.WriteLine($"RuleForge sohbet — {ruleSet.Rules.Count} kural, {ruleSet.Inputs.Count} girdi yüklendi.");
            Console.WriteLine(Help);
            while (true)
            {
                Console.Write("\n> ");
                var line = Console.ReadLine();
                if (line == null) break;
                line = line.Trim();
                if (line.Length == 0) continue;

                if (line.StartsWith("/"))
                {
                    if (!HandleCommand(line, assistant.RuleSet, snapshot)) break;
                    JsonStore.Save(assistant.RuleSet, rulesPath);
                    continue;
                }

                Console.WriteLine("…düşünüyor");
                AssistantTurn turn;
                try
                {
                    turn = await assistant.SendAsync(line).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    Console.WriteLine("API hatası: " + ex.Message);
                    continue;
                }

                Console.WriteLine();
                Console.WriteLine(turn.Reply);
                foreach (var c in turn.Changes) Console.WriteLine("  " + c);
                foreach (var i in turn.Issues) Console.WriteLine("  " + i);
                if (turn.Questions.Count > 0)
                {
                    Console.WriteLine("Sorular:");
                    foreach (var q in turn.Questions) Console.WriteLine("  ? " + q);
                }
                JsonStore.Save(assistant.RuleSet, rulesPath);
            }
            JsonStore.Save(assistant.RuleSet, rulesPath);
            Console.WriteLine("Kaydedildi: " + rulesPath);
            return 0;
        }

        /// <summary>false dönerse çıkılır. Onay/ret yapay zekâdan değil, sadece buradan yapılır.</summary>
        private static bool HandleCommand(string line, RuleSet set, ModelSnapshot? snapshot)
        {
            var parts = line.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            switch (parts[0].ToLowerInvariant())
            {
                case "/cikis":
                case "/çıkış":
                case "/exit":
                    return false;

                case "/kurallar":
                    foreach (var i in set.Inputs)
                        Console.WriteLine($"  girdi  {i.Name} ({i.Type}) varsayılan={i.Default}");
                    foreach (var v in set.Variables)
                        Console.WriteLine($"  değişken  {v.Name} = {v.Expression}");
                    foreach (var r in set.Rules)
                    {
                        var mark = r.Status == RuleStatus.Approved ? "✔" : r.Status == RuleStatus.Rejected ? "✘" : "?";
                        Console.WriteLine($"  {mark} {r.Id}: {r.Target} = {r.Expression}{(r.Condition != null ? "  EĞER " + r.Condition : "")}");
                    }
                    return true;

                case "/onayla":
                case "/reddet":
                {
                    var status = parts[0] == "/onayla" ? RuleStatus.Approved : RuleStatus.Rejected;
                    bool all = parts.Skip(1).Any(p => p == "hepsi");
                    int n = 0;
                    foreach (var r in set.Rules)
                    {
                        if (all ? r.Status == RuleStatus.Proposed : parts.Skip(1).Any(id => string.Equals(id, r.Id, StringComparison.OrdinalIgnoreCase)))
                        {
                            r.Status = status;
                            n++;
                        }
                    }
                    Console.WriteLine($"{n} kural {(status == RuleStatus.Approved ? "onaylandı" : "reddedildi")}.");
                    return true;
                }

                case "/dene":
                {
                    var inputs = RuleEngine.ParseAssignments(parts.Skip(1), out var tables);
                    var result = RuleEngine.Evaluate(set, inputs, new EvaluationOptions { IncludeProposed = true }, tables);
                    Program.Print(result);
                    return true;
                }

                case "/dogrula":
                case "/doğrula":
                    foreach (var i in RuleSetValidator.Validate(set, snapshot)) Console.WriteLine("  " + i);
                    return true;

                default:
                    Console.WriteLine(Help);
                    return true;
            }
        }
    }
}
