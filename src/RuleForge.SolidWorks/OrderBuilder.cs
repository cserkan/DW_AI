using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using RuleForge.Core.Engine;
using RuleForge.Core.Rules;

namespace RuleForge.SolidWorks
{
    /// <summary>Bir siparişin üretimi için gereken her şey (komut satırı ve arayüz ortak kullanır).</summary>
    public sealed class OrderRequest
    {
        public RuleSet Rules { get; set; } = new RuleSet();

        /// <summary>Kural motorunun sonucu (eylemler ve tablo satırları).</summary>
        public EvaluationResult Evaluation { get; set; } = new EvaluationResult();

        /// <summary>Master montaj; tekrarlanan modüllü üründe modülün kendisi.</summary>
        public string MasterAssembly { get; set; } = string.Empty;

        /// <summary>Tekrarlanan modüllü üründe kopyaları toplayan montaj (ör. "Conveyor Line.SLDASM").</summary>
        public string? RootAssembly { get; set; }

        /// <summary>Sipariş klasörü; adı siparişin adıdır.</summary>
        public string OutputFolder { get; set; } = string.Empty;

        /// <summary>Ortak parça kütüphanesi; null ise tüm dosyalar sipariş klasörüne master adlarıyla üretilir (eski davranış).</summary>
        public string? LibraryFolder { get; set; }

        public IList<string>? Slots { get; set; }
        public bool ExportPdf { get; set; }
        public bool ExportStep { get; set; }

        /// <summary>Aşama mesajları (arayüzde canlı günlük).</summary>
        public Action<string>? Progress { get; set; }

        /// <summary>Varsayılan kütüphane: sipariş klasörünün yanındaki "Kutuphane" klasörü.</summary>
        public static string DefaultLibrary(string outputFolder) =>
            Path.Combine(Path.GetDirectoryName(Path.GetFullPath(outputFolder).TrimEnd('\\', '/')) ?? ".", "Kutuphane");
    }

    public sealed class OrderBuilder
    {
        private readonly SwSession _session;

        public OrderBuilder(SwSession session)
        {
            _session = session;
        }

        /// <summary>Tekrarlanan modül varsa üretim için --root gerekir; yoksa null.</summary>
        public static string? Problem(OrderRequest request)
        {
            bool line = request.Evaluation.Actions.Any(a => a.Instance.HasValue);
            if (!line) return null;
            var lineTables = request.Rules.Tables.Where(t => string.IsNullOrEmpty(t.ModuleComponent)).ToList();
            if (lineTables.Count != 1 || request.Rules.Tables.Count != 1)
                return "Şimdilik yalnızca tek tablolu ve modülü master'ın kendisi olan kural setleri üretilebilir (ör. konveyör hattı).";
            if (string.IsNullOrEmpty(request.RootAssembly))
                return "Bu kural seti tekrarlanan modül içeriyor: kopyaları toplayan montaj (ör. \"Conveyor Line.SLDASM\") gerekli.";
            return null;
        }

        /// <summary>
        /// Siparişi üretir. Üretim boyunca master klasörlerindeki SolidWorks dosyaları salt-okunur yapılır (SolidWorks bir hata
        /// yüzünden master'a yazmaya kalksa bile yazamaz), sonunda eski hâllerine döner ve değişmedikleri doğrulanır.
        /// </summary>
        public GenerationResult Build(OrderRequest request)
        {
            var folders = new[] { request.MasterAssembly, request.RootAssembly }
                .Where(p => !string.IsNullOrEmpty(p)).Select(p => Path.GetDirectoryName(Path.GetFullPath(p!))!)
                .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var masters = folders.SelectMany(f => Directory.EnumerateFiles(f, "*.sld*", SearchOption.AllDirectories))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToDictionary(f => f, f => (attributes: File.GetAttributes(f), written: File.GetLastWriteTimeUtc(f)), StringComparer.OrdinalIgnoreCase);
            foreach (var kv in masters) File.SetAttributes(kv.Key, kv.Value.attributes | FileAttributes.ReadOnly);
            GenerationResult result;
            try
            {
                result = BuildUnprotected(request);
            }
            finally
            {
                foreach (var kv in masters)
                    if (File.Exists(kv.Key)) File.SetAttributes(kv.Key, kv.Value.attributes);
            }
            var changed = masters.Where(kv => File.Exists(kv.Key) && File.GetLastWriteTimeUtc(kv.Key) != kv.Value.written).Select(kv => kv.Key).ToList();
            if (changed.Count > 0)
                result.Errors.Add("UYARI: master dosyaları değişti (olmamalıydı): " + string.Join(", ", changed));
            return result;
        }

        private GenerationResult BuildUnprotected(OrderRequest request)
        {
            var problem = Problem(request);
            if (problem != null)
            {
                var r = new GenerationResult();
                r.Errors.Add(problem);
                return r;
            }

            var outDir = Path.GetFullPath(request.OutputFolder);
            bool useLibrary = !string.IsNullOrEmpty(request.LibraryFolder);
            if (useLibrary && Directory.Exists(outDir) && Directory.EnumerateFileSystemEntries(outDir).Any())
            {
                var r = new GenerationResult();
                r.Errors.Add($"Çıktı klasörü boş değil: {outDir}");
                return r;
            }
            // Kütüphaneli üretimde model önce geçici bir çalışma klasöründe kurulur, sonra kütüphaneye taşınır.
            // Not: %TEMP% altında kurulan kopyalarda SolidWorks sonekli alt montajları bulamayıp master dosyalarına bağlanıyordu;
            // çalışma klasörü sipariş klasörünün yanında.
            var workDir = useLibrary ? outDir.TrimEnd('\\', '/') + ".calisma" : outDir;
            if (useLibrary && Directory.Exists(workDir)) Directory.Delete(workDir, true);
            var generator = new ModelGenerator(_session) { Progress = request.Progress };
            var actions = request.Evaluation.Actions;
            GenerationResult gen;
            if (actions.Any(a => a.Instance.HasValue))
            {
                var table = request.Rules.Tables[0];
                var rowCount = request.Evaluation.Rows.TryGetValue(table.Name, out var rows) ? rows.Count : 0;
                gen = generator.GenerateLine(new LineGenerationRequest
                {
                    RootAssemblyPath = request.RootAssembly!,
                    ModuleAssemblyPath = request.MasterAssembly,
                    OutputFolder = workDir,
                    Rows = Enumerable.Range(1, rowCount).Select(i => (IList<ModelAction>)actions.Where(a => a.Instance == i).ToList()).ToList(),
                    RootActions = actions.Where(a => !a.Instance.HasValue).ToList(),
                    SharedDocuments = table.SharedDocuments,
                    Slots = request.Slots,
                    ExportPdf = request.ExportPdf && !useLibrary,
                    ExportStep = request.ExportStep && !useLibrary,
                });
            }
            else
            {
                gen = generator.Generate(new GenerationRequest
                {
                    MasterAssemblyPath = request.MasterAssembly,
                    OutputFolder = workDir,
                    Actions = actions,
                    ExportPdf = request.ExportPdf && !useLibrary,
                    ExportStep = request.ExportStep && !useLibrary,
                });
            }
            if (!useLibrary || !gen.Success) return gen;

            request.Progress?.Invoke("Kütüphaneye yayınlanıyor (daha önce üretilmiş parçalar aranıyor)…");
            var order = Path.GetFileName(outDir.TrimEnd('\\', '/'));
            var rootStem = Path.GetFileNameWithoutExtension(gen.AssemblyPath);
            new LibraryPublisher(_session).Publish(gen.AssemblyPath, outDir, request.LibraryFolder!, rootStem + " " + order, order, gen.Sources, gen);
            if (gen.Success && (request.ExportPdf || request.ExportStep))
                generator.ExportOutputs(gen.AssemblyPath, request.ExportPdf, request.ExportStep, gen);
            return gen;
        }
    }
}
