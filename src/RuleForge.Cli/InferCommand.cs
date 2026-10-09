using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using RuleForge.Core.Expressions;
using RuleForge.Core.Json;
using RuleForge.Core.Model;
using RuleForge.Inference;

namespace RuleForge.Cli
{
    internal static class InferCommand
    {
        /// <summary>infer ve crossval için ortak: snapshot'ları, girdi tablosunu ve seçenekleri yükler.</summary>
        private static (List<VariantSample> samples, InferenceOptions options, ModelSnapshot? master) Load(Args args)
        {
            var files = new List<string>();
            foreach (var p in args.NonAssignments)
            {
                if (Directory.Exists(p)) files.AddRange(Directory.GetFiles(p, "*.json").OrderBy(f => f, StringComparer.OrdinalIgnoreCase));
                else if (File.Exists(p)) files.Add(p);
                else throw new UsageException("Bulunamadı: " + p);
            }
            var masterPath = args.Get("master");
            if (masterPath != null)
                files.RemoveAll(f => string.Equals(Path.GetFullPath(f), Path.GetFullPath(masterPath), StringComparison.OrdinalIgnoreCase));
            if (files.Count < 2) throw new UsageException("En az 2 varyant snapshot'ı gerekli.");

            var table = args.Get("inputs") is string csv ? InputTable.Load(csv) : null;
            var samples = new List<VariantSample>();
            foreach (var f in files)
            {
                var snap = JsonStore.Load<ModelSnapshot>(f);
                var name = string.IsNullOrEmpty(snap.Name) ? Path.GetFileNameWithoutExtension(f) : snap.Name;
                Dictionary<string, Value>? inputs = null;
                if (table != null)
                {
                    inputs = Match(table, name);
                    if (inputs == null)
                    {
                        Console.WriteLine($"Uyarı: '{name}' girdi tablosunda yok, atlandı.");
                        continue;
                    }
                }
                samples.Add(new VariantSample(name, snap, inputs));
            }

            var options = new InferenceOptions { NamePattern = args.Get("name-pattern") };
            foreach (var a in args.All("input"))
            {
                var idx = a.IndexOf('=');
                if (idx <= 0) throw new UsageException("--input Ad=gözlem_anahtarı biçiminde olmalı (ör. Boy=dim:Govde.SLDPRT:D1@Boss).");
                options.InputObservations[a.Substring(0, idx)] = a.Substring(idx + 1);
            }
            if (args.Get("tolerance") is string tol)
                options.Tolerance = double.Parse(tol, System.Globalization.CultureInfo.InvariantCulture);

            var master = masterPath != null ? JsonStore.Load<ModelSnapshot>(masterPath) : null;
            return (samples, options, master);
        }

        public static int Run(Args args)
        {
            var (samples, options, master) = Load(args);
            var report = RuleInferencer.Infer(samples, options, master);
            var text = report.ToText();
            Console.WriteLine(text);

            var output = args.Get("o", "output") ?? "kurallar.json";
            var masterAssembly = master?.SourcePath ?? samples[0].Snapshot.SourcePath;
            var name2 = Path.GetFileNameWithoutExtension(master?.RootDocument ?? samples[0].Snapshot.RootDocument);
            JsonStore.Save(report.ToRuleSet(name2, masterAssembly), output);
            var reportPath = Path.ChangeExtension(output, ".cikarim.txt");
            File.WriteAllText(reportPath, text);
            Console.WriteLine($"Kurallar: {output}  (hepsi 'önerilen' durumda)");
            Console.WriteLine($"Rapor:    {reportPath}  (chat komutuna --report ile verin)");
            if (report.SuggestedVariants.Count > 0)
            {
                var csvPath = Path.ChangeExtension(output, ".oneriler.csv");
                File.WriteAllText(csvPath, report.ToSuggestionCsv(), new System.Text.UTF8Encoding(true));
                Console.WriteLine($"Öneri:    {csvPath}  ({report.SuggestedVariants.Count} yeni varyant; DriveWorks'te üretip tekrar çalıştırın)");
            }
            return 0;
        }

        /// <summary>Leave-one-out çapraz doğrulama: cevap anahtarı olmadan kuralların güvenilirliğini ölçer.</summary>
        public static int CrossValidate(Args args)
        {
            var (samples, options, master) = Load(args);
            Console.WriteLine($"{samples.Count} varyant; her biri sırayla çıkarılıp kurallar kalanlardan öğreniliyor…");
            var report = CrossValidator.Run(samples, options, master,
                (done, total) => Console.Write($"\r  tur {done}/{total}   "));
            Console.WriteLine();
            var text = report.ToText();
            Console.WriteLine(text);
            var output = args.Get("o", "output") ?? "capraz-dogrulama.txt";
            File.WriteAllText(output, text);
            Console.WriteLine($"Rapor: {output}");
            return 0;
        }

        /// <summary>Snapshot adını girdi tablosundaki satırla eşleştirir: tam eşleşme, sonra içerme.</summary>
        private static Dictionary<string, Value>? Match(Dictionary<string, Dictionary<string, Value>> table, string name)
        {
            if (table.TryGetValue(name, out var row)) return row;
            var candidates = table.Where(kv => name.IndexOf(kv.Key, StringComparison.OrdinalIgnoreCase) >= 0 ||
                                               kv.Key.IndexOf(name, StringComparison.OrdinalIgnoreCase) >= 0).ToList();
            return candidates.Count == 1 ? candidates[0].Value : null;
        }
    }
}
