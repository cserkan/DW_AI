using System;
using System.IO;
using System.Linq;
using RuleForge.Core.Engine;
using RuleForge.Core.Json;
using RuleForge.Core.Rules;
#if SOLIDWORKS
using RuleForge.SolidWorks;
#endif

namespace RuleForge.Cli
{
    internal static class SolidWorksCommands
    {
        public static int Extract(Args args)
        {
#if SOLIDWORKS
            var inputs = args.NonAssignments;
            if (inputs.Count == 0) throw new UsageException("En az bir .SLDASM dosyası verin.");
            var outDir = args.Get("out-dir");
            var single = args.Get("o", "output");
            if (inputs.Count > 1 && outDir == null) outDir = "snapshots";

            using (var session = SwSession.Connect(visible: args.Flag("visible")))
            {
                Console.WriteLine("Bağlandı: " + session.VersionLabel);
                var extractor = new SnapshotExtractor(session);
                foreach (var asm in inputs)
                {
                    Console.Write($"Okunuyor: {asm} … ");
                    var snap = extractor.Extract(asm);
                    var target = outDir != null
                        ? Path.Combine(outDir, Path.GetFileNameWithoutExtension(asm) + ".json")
                        : single ?? Path.ChangeExtension(asm, ".snapshot.json");
                    JsonStore.Save(snap, target);
                    Console.WriteLine($"{snap.Documents.Count} belge, {snap.Components.Count} bileşen, " +
                                      $"{snap.Documents.Sum(d => d.Dimensions.Count)} ölçü → {target}");
                }
            }
            return 0;
#else
            return NotAvailable();
#endif
        }

        public static int Generate(Args args)
        {
            var rules = JsonStore.Load<RuleSet>(args.Require("rules"));
            var result = RuleEngine.Evaluate(rules, RuleEngine.ParseAssignments(args.Assignments),
                new EvaluationOptions { IncludeProposed = args.Flag("include-proposed") });
            Program.Print(result);
            if (!result.Success) return 1;
            if (args.Flag("dry-run")) return 0;

#if SOLIDWORKS
            var master = args.Get("master") ?? rules.MasterAssembly;
            if (string.IsNullOrEmpty(master)) throw new UsageException("--master gerekli.");
            var outDir = args.Require("out");
            using (var session = SwSession.Connect(visible: args.Flag("visible")))
            {
                Console.WriteLine("Bağlandı: " + session.VersionLabel);
                var gen = new ModelGenerator(session).Generate(new GenerationRequest
                {
                    MasterAssemblyPath = master,
                    OutputFolder = outDir,
                    Actions = result.Actions,
                    ExportPdf = args.Flag("pdf"),
                    ExportStep = args.Flag("step"),
                });
                foreach (var l in gen.Log) Console.WriteLine("  " + l);
                foreach (var w in gen.Warnings) Console.WriteLine("Uyarı: " + w);
                foreach (var e in gen.Errors) Console.WriteLine("HATA: " + e);
                foreach (var f in gen.ExportedFiles) Console.WriteLine("Dışa aktarıldı: " + f);
                Console.WriteLine(gen.Success ? "Tamamlandı: " + gen.AssemblyPath : "Hatalarla tamamlandı.");
                return gen.Success ? 0 : 1;
            }
#else
            return NotAvailable();
#endif
        }

#if !SOLIDWORKS
        private static int NotAvailable()
        {
            Console.Error.WriteLine("Bu komut SolidWorks gerektirir: Windows'ta net48 derlemesini (ruleforge.exe) kullanın.");
            return 3;
        }
#endif
    }
}
