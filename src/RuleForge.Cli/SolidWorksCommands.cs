using System;
using System.IO;
using System.Linq;
using RuleForge.Core.Engine;
using RuleForge.Core.Files;
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
            // Tırnak hatasıyla boşluklarda bölünmüş yolları geri birleştir.
            var inputs = PathArguments.Resolve(args.NonAssignments, File.Exists);
            if (inputs.Count == 0) throw new UsageException("En az bir .SLDASM dosyası verin.");
            foreach (var missing in inputs.Where(i => !File.Exists(i)))
                throw new UsageException($"Dosya bulunamadı: {missing}\nYolu tek bir çift tırnak içinde yazın: \"C:\\...\\Montaj.SLDASM\"");
            var outDir = args.Get("out-dir");
            var single = args.Get("o", "output");
            if (inputs.Count > 1 && outDir == null) outDir = "snapshots";

            using (var session = SwSession.Connect(visible: args.Flag("visible")))
            {
                Console.WriteLine("Bağlandı: " + session.VersionLabel);
                var extractor = new SnapshotExtractor(session);
                int failed = 0;
                for (int i = 0; i < inputs.Count; i++)
                {
                    var asm = inputs[i];
                    // Her varyant ayrı klasördeyse dosya adları aynı olabilir; klasör adını öne ekle ki üst üste yazılmasın.
                    var stem = Path.GetFileNameWithoutExtension(asm);
                    var folder = Path.GetFileName(Path.GetDirectoryName(Path.GetFullPath(asm)) ?? string.Empty);
                    var label = inputs.Count > 1 && !string.IsNullOrEmpty(folder) ? folder + "__" + stem : stem;
                    var target = outDir != null
                        ? Path.Combine(outDir, SafeName(label) + ".json")
                        : single ?? Path.ChangeExtension(asm, ".snapshot.json");
                    if (!ExtractOne(extractor, asm, label, target, i + 1, inputs.Count)) failed++;
                }
                return failed == 0 ? 0 : 1;
            }
#else
            return NotAvailable();
#endif
        }

        /// <summary>
        /// Bir klasördeki varyantların üst montajlarını otomatik bulur ve okur. Alt montajlar atlanır:
        /// üst montaj = aynı varyant klasöründe başka hiçbir montajın kullanmadığı montaj.
        /// </summary>
        public static int ExtractVariants(Args args)
        {
#if SOLIDWORKS
            var folders = PathArguments.Resolve(args.NonAssignments, Directory.Exists);
            if (folders.Count != 1 || !Directory.Exists(folders[0]))
                throw new UsageException("Varyant klasörlerini içeren klasörü verin, ör: extract-variants \"C:\\DriveWorks\\Sonuclar\"" +
                                         (folders.Count > 0 ? $"\nBulunamadı: {folders[0]}" : string.Empty));
            var root = Path.GetFullPath(folders[0]);
            var outDir = args.Get("out-dir") ?? "varyantlar";

            var assemblies = TopAssemblyDetector.FindAssemblies(root);
            if (assemblies.Count == 0)
            {
                Console.Error.WriteLine("Bu klasörde (alt klasörler dahil) hiç .SLDASM yok: " + root);
                return 1;
            }

            using (var session = SwSession.Connect(visible: args.Flag("visible")))
            {
                Console.WriteLine("Bağlandı: " + session.VersionLabel);
                Console.WriteLine($"{assemblies.Count} montaj dosyası bulundu; hangisinin hangisini kullandığı okunuyor (dosyalar açılmadan)…");
                var finder = new VariantFinder(session);
                var variants = TopAssemblyDetector.Detect(root, assemblies, finder.DirectDependencies, finder.TotalDependencyCount);

                Console.WriteLine();
                Console.WriteLine($"{variants.Count} varyant (üst montaj) bulundu:");
                foreach (var v in variants)
                {
                    var where = v.Group.Length > 0 ? v.Group + "\\" : string.Empty;
                    Console.WriteLine($"  {where}{Path.GetFileName(v.Path)}" + (v.SubAssemblies > 0 ? $"   ({v.SubAssemblies} alt montaj atlandı)" : string.Empty));
                    foreach (var other in v.OtherTopLevel)
                        Console.WriteLine($"      Uyarı: bu klasörde kullanılmayan başka bir montaj da var, atlandı: {Path.GetFileName(other)}");
                }
                if (args.Flag("list"))
                {
                    Console.WriteLine();
                    Console.WriteLine("Sadece listelendi (--list). Okumak için aynı komutu --list olmadan çalıştırın.");
                    return 0;
                }

                Console.WriteLine();
                var extractor = new SnapshotExtractor(session);
                int failed = 0;
                for (int i = 0; i < variants.Count; i++)
                {
                    var v = variants[i];
                    var target = Path.Combine(outDir, SafeName(v.Label) + ".json");
                    if (!ExtractOne(extractor, v.Path, v.Label, target, i + 1, variants.Count)) failed++;
                }
                Console.WriteLine();
                Console.WriteLine(failed == 0
                    ? $"Tamamlandı: {variants.Count} varyant → {Path.GetFullPath(outDir)}"
                    : $"{variants.Count - failed} varyant okundu, {failed} varyant okunamadı (hatalar yukarıda).");
                return failed == 0 ? 0 : 1;
            }
#else
            return NotAvailable();
#endif
        }

#if SOLIDWORKS
        /// <summary>Tek montajı okur; hata olursa yazar ve false döner (diğer varyantlar devam eder).</summary>
        private static bool ExtractOne(SnapshotExtractor extractor, string asm, string label, string target, int index, int count)
        {
            Console.Write($"[{index}/{count}] Okunuyor: {asm} … ");
            var watch = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                var snap = extractor.Extract(asm, label);
                JsonStore.Save(snap, target);
                Console.WriteLine($"{snap.Documents.Count} belge, {snap.Components.Count} bileşen, " +
                                  $"{snap.Documents.Sum(d => d.Dimensions.Count)} ölçü → {target}  ({watch.Elapsed.TotalSeconds:0} sn)");
                return true;
            }
            catch (Exception ex)
            {
                Console.WriteLine();
                Console.WriteLine($"   HATA: {ex.Message}");
                return false;
            }
        }

        private static string SafeName(string name)
        {
            foreach (var c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
            return name;
        }
#endif

        public static int Generate(Args args)
        {
            var rules = JsonStore.Load<RuleSet>(args.Require("rules"));
            var inputs = RuleEngine.ParseAssignments(args.Assignments, out var tables);
            var result = RuleEngine.Evaluate(rules, inputs, new EvaluationOptions { IncludeProposed = args.Flag("include-proposed") }, tables);
            Program.Print(result);
            if (!result.Success) return 1;
            if (args.Flag("dry-run")) return 0;
            if (result.Actions.Any(a => a.Instance.HasValue))
            {
                Console.Error.WriteLine("Bu kural seti tekrarlanan bir modül (tablo) içeriyor; kopyalı modellerin üretimi henüz eklenmedi. " +
                                        "Değerleri görmek için --dry-run kullanın.");
                return 1;
            }

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
                if (gen.Skipped.Count > 0)
                {
                    Console.WriteLine($"{gen.Skipped.Count} eylem bu siparişte kullanılmayan dosyalar için atlandı (normal):");
                    foreach (var k in gen.Skipped.Take(10)) Console.WriteLine("  - " + k);
                    if (gen.Skipped.Count > 10) Console.WriteLine($"  … (+{gen.Skipped.Count - 10})");
                }
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
