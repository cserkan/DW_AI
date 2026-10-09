using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using RuleForge.Core.Engine;
using RuleForge.Core.Rules;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace RuleForge.SolidWorks
{
    public sealed class GenerationRequest
    {
        public string MasterAssemblyPath { get; set; } = string.Empty;

        /// <summary>Her sipariş/spesifikasyon kendi klasörüne üretilir; dosya adları master ile aynı kalır.</summary>
        public string OutputFolder { get; set; } = string.Empty;

        public IList<ModelAction> Actions { get; set; } = new List<ModelAction>();
        public bool ExportPdf { get; set; }
        public bool ExportStep { get; set; }

        /// <summary>Master kullanıcı tarafından açıksa kapatmaya izin ver (aynı adlı kopya açılamaz).</summary>
        public bool AllowClosingMaster { get; set; } = true;

        /// <summary>
        /// Kopyalanan tüm dosya adlarına eklenir (ör. "-2": "Frame.SLDPRT" → "Frame-2.SLDPRT"). Aynı modülün birden çok
        /// kopyası aynı sipariş klasörüne üretilirken kullanılır; kurallar yine master adlarıyla yazılır.
        /// </summary>
        public string? FileSuffix { get; set; }

        /// <summary>Çıktı klasöründe başka dosyalar olabilir (ör. hattın diğer kopyaları). Varsayılan: klasör boş olmalı.</summary>
        public bool AllowNonEmptyOutput { get; set; }
    }

    /// <summary>
    /// Tekrarlanan modül içeren ürün (ör. 2–5 konveyörlük hat): modül master'ı her satır için ayrı dosya adlarıyla üretilir,
    /// sonra kopyaları toplayan montajın (hat master'ı) yer tutucu bileşenleri bu kopyalarla değiştirilir, artanlar silinir.
    /// DriveWorks projesindeki "&lt;Replace&gt;" / "DELETE" yaklaşımının aynısı.
    /// </summary>
    public sealed class LineGenerationRequest
    {
        /// <summary>Kopyaları toplayan montaj, ör. "Conveyor Line.SLDASM".</summary>
        public string RootAssemblyPath { get; set; } = string.Empty;

        /// <summary>Tekrarlanan modülün master'ı, ör. "Conveyor Assembly.SLDASM".</summary>
        public string ModuleAssemblyPath { get; set; } = string.Empty;

        public string OutputFolder { get; set; } = string.Empty;

        /// <summary>Satır başına eylemler (satır 1 ilk eleman).</summary>
        public IList<IList<ModelAction>> Rows { get; set; } = new List<IList<ModelAction>>();

        /// <summary>Satıra bağlı olmayan eylemler: hat montajının kendisine uygulanır (ör. kopyalar arası ilişkiler).</summary>
        public IList<ModelAction> RootActions { get; set; } = new List<ModelAction>();

        /// <summary>
        /// Satırların yerleşeceği yer tutucu bileşenler sırayla (ör. "Conveyor Dummy 1-5"). Boşsa hat master'ının üst
        /// seviye bileşenleri örnek numarasına göre sıralanır.
        /// </summary>
        public IList<string>? Slots { get; set; }

        /// <summary>Tüm satırlarda aynı dosya olacak modül parçaları (ör. "Roller.SLDPRT"): satır 1'in kopyası kullanılır.</summary>
        public IList<string> SharedDocuments { get; set; } = new List<string>();

        public bool ExportPdf { get; set; }
        public bool ExportStep { get; set; }
    }

    public sealed class GenerationResult
    {
        public string AssemblyPath { get; set; } = string.Empty;
        public List<string> Log { get; } = new List<string>();
        public List<string> Errors { get; } = new List<string>();
        public List<string> Warnings { get; } = new List<string>();
        public List<string> ExportedFiles { get; } = new List<string>();

        /// <summary>Bu siparişte kullanılmayan dosyalara ait olduğu için uygulanmayan eylemler (hata değildir).</summary>
        public List<string> Skipped { get; } = new List<string>();

        /// <summary>Çıktı klasöründeki her kopya → kopyalandığı master (ya da kütüphane) dosyası.</summary>
        public Dictionary<string, string> Sources { get; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>Kuralların değer yazdığı belgelerin yolları.</summary>
        public HashSet<string> ModifiedDocuments { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        public bool Success => Errors.Count == 0;
    }

    /// <summary>Eylemin hedefi bu siparişte yok (ör. başka kapak tipinin dosyası): eylem atlanır, hata sayılmaz.</summary>
    internal sealed class SkippedActionException : Exception
    {
        public SkippedActionException(string message) : base(message) { }
    }

    /// <summary>
    /// Kural motorunun ürettiği eylemleri SolidWorks'e uygular:
    /// master'ı Pack and Go ile kopyala → kopyayı aç → eylemleri uygula → rebuild → kaydet → dışa aktar.
    /// Master dosyalar asla değiştirilmez.
    /// </summary>
    public sealed class ModelGenerator
    {
        private readonly SwSession _session;

        public ModelGenerator(SwSession session)
        {
            _session = session;
        }

        public GenerationResult Generate(GenerationRequest request)
        {
            var result = new GenerationResult();
            var master = Path.GetFullPath(request.MasterAssemblyPath);
            var outDir = Path.GetFullPath(request.OutputFolder);
            Directory.CreateDirectory(outDir);
            if (!request.AllowNonEmptyOutput && Directory.EnumerateFileSystemEntries(outDir).Any())
            {
                result.Errors.Add($"Çıktı klasörü boş değil: {outDir}");
                return result;
            }

            var outputName = request.Actions.FirstOrDefault(a => a.Target.Kind == TargetKind.OutputFileName)?.Value.AsText();
            var copiedRoot = CopyMaster(master, outDir, outputName, request.AllowClosingMaster, result, out var masterStates, request.FileSuffix);
            if (copiedRoot == null) return result;
            result.AssemblyPath = copiedRoot;
            // Kurallar gereği bastırılacak bileşenler (ve altındakiler); bunların dışında bastırılan bileşen beklenmedik demektir.
            var allowedSuppressed = new HashSet<string>(request.Actions
                .Where(a => a.Target.Kind == TargetKind.ComponentSuppression && a.Value.AsBool())
                .Select(a => a.Target.Component!), StringComparer.OrdinalIgnoreCase);

            var app = _session.App;
            var root = _session.Open(copiedRoot);
            if (!IsInside(root.GetPathName(), outDir))
            {
                // SolidWorks bellekte aynı adlı bir belge (ör. gizli kalmış master) varsa kopya yerine onu döndürür.
                result.Errors.Add($"Kopya yerine başka bir belge açıldı: {root.GetPathName()}. SolidWorks'te aynı adlı bir belge açık kalmış; " +
                                  "SolidWorks'ü kapatıp yeniden açın. Hiçbir şey değiştirilmedi.");
                return result;
            }
            app.CommandInProgress = true;
            try
            {
                if (root is AssemblyDoc asm) asm.ResolveAllLightWeightComponents(false);
                var context = new ApplyContext(_session, root, Path.GetFileName(master), outDir, Path.GetDirectoryName(master)!, result,
                    request.FileSuffix);

                // Açılış kontrolü: master'da açık olan bileşen kopyada bastırılmış geldiyse ya da çıktı klasörü dışındaki bir
                // dosyayı kullanıyorsa (ör. aynı adlı dosya başka klasörden bellekte kalmış) üretim yanlış olur.
                context.CheckLoaded(masterStates);
                if (result.Errors.Count > 0)
                {
                    // Kopya başka dosyalara (ör. master'a) bağlıysa kural uygulamak ve kaydetmek o dosyaları değiştirirdi.
                    result.Errors.Add("Açılış kontrolü başarısız: hiçbir kural uygulanmadı, hiçbir dosya kaydedilmedi.");
                    return result;
                }

                // Sıra önemli: önce yapı (konfigürasyon, bastırma, değiştirme), sonra ölçüler, en son özellikler.
                foreach (var action in request.Actions.OrderBy(a => Order(a.Target.Kind)))
                {
                    try
                    {
                        context.Apply(action);
                    }
                    catch (SkippedActionException ex)
                    {
                        result.Skipped.Add($"{action.Rule.Id}: {ex.Message}");
                    }
                    catch (Exception ex)
                    {
                        result.Errors.Add($"{action.Rule.Id} ({action.Target}): {ex.Message}");
                    }
                    // Yapısal eylemlerden sonra: kural istemediği hâlde bastırılan bileşen var mı? Varsa hangi eylemden sonra olduğunu yaz, geri aç.
                    if (action.Target.Kind == TargetKind.FeatureSuppression || action.Target.Kind == TargetKind.ComponentReplace ||
                        action.Target.Kind == TargetKind.Configuration || action.Target.Kind == TargetKind.ComponentSuppression)
                        context.RestoreUnexpectedSuppression(masterStates, allowedSuppressed, $"'{action.Rule.Id}' uygulandıktan sonra");
                }

                context.RebuildModifiedParts();
                root.ForceRebuild3(false);
                context.RestoreUnexpectedSuppression(masterStates, allowedSuppressed, "rebuild sonrasında");
                int whatsWrong = root.Extension.GetWhatsWrongCount();
                if (whatsWrong > 0)
                    result.Warnings.Add($"Rebuild sonrası {whatsWrong} hata/uyarı var: {WhatsWrong(root)}");

                if (SaveInside(root, outDir, result)) result.Log.Add("Kaydedildi: " + copiedRoot);

                if (request.ExportStep) Export(root, Path.ChangeExtension(copiedRoot, ".step"), null, result);
            }
            finally
            {
                app.CommandInProgress = false;
                _session.Close(copiedRoot);
            }

            if (request.ExportPdf) ExportDrawings(outDir, result);
            return result;
        }

        /// <summary>
        /// Tekrarlanan modüllü ürün: önce her satırın modül kopyası ("-1", "-2"… ekli dosyalarla) üretilir, sonra hat master'ı
        /// kopyalanır, yer tutucular sırayla bu kopyalarla değiştirilir, artan yer tutucular (ve ilişkileri) silinir.
        /// </summary>
        public GenerationResult GenerateLine(LineGenerationRequest request)
        {
            var result = new GenerationResult();
            var rootMaster = Path.GetFullPath(request.RootAssemblyPath);
            var outDir = Path.GetFullPath(request.OutputFolder);
            Directory.CreateDirectory(outDir);
            if (Directory.EnumerateFileSystemEntries(outDir).Any())
            {
                result.Errors.Add($"Çıktı klasörü boş değil: {outDir}");
                return result;
            }
            if (request.Rows.Count == 0)
            {
                result.Errors.Add("Hiç satır yok: en az bir modül kopyası gerekir.");
                return result;
            }

            // 1) Satırlar: her biri modül master'ının kendi sonekli kopyası.
            var rowAssemblies = new List<string>();
            var rowResults = new List<GenerationResult>();
            for (int i = 0; i < request.Rows.Count; i++)
            {
                var row = Generate(new GenerationRequest
                {
                    MasterAssemblyPath = request.ModuleAssemblyPath,
                    OutputFolder = outDir,
                    Actions = request.Rows[i],
                    FileSuffix = "-" + (i + 1),
                    AllowNonEmptyOutput = true,
                });
                var tag = $"[satır {i + 1}] ";
                result.Log.AddRange(row.Log.Select(l => tag + l));
                result.Warnings.AddRange(row.Warnings.Select(l => tag + l));
                result.Errors.AddRange(row.Errors.Select(l => tag + l));
                result.Skipped.AddRange(row.Skipped.Select(l => tag + l));
                foreach (var kv in row.Sources) result.Sources[kv.Key] = kv.Value;
                result.ModifiedDocuments.UnionWith(row.ModifiedDocuments);
                if (!row.Success) return result;
                rowAssemblies.Add(row.AssemblyPath);
                rowResults.Add(row);
            }
            ShareDocuments(request.SharedDocuments, rowResults, outDir, result);

            // 2) Hat master'ı.
            var copiedRoot = CopyMaster(rootMaster, outDir, null, true, result, out var rootStates);
            if (copiedRoot == null) return result;
            result.AssemblyPath = copiedRoot;

            var app = _session.App;
            var root = _session.Open(copiedRoot);
            if (!IsInside(root.GetPathName(), outDir))
            {
                // SolidWorks bellekte aynı adlı bir belge (ör. gizli kalmış master) varsa kopya yerine onu döndürür.
                result.Errors.Add($"Kopya yerine başka bir belge açıldı: {root.GetPathName()}. SolidWorks'te aynı adlı bir belge açık kalmış; " +
                                  "SolidWorks'ü kapatıp yeniden açın. Hiçbir şey değiştirilmedi.");
                return result;
            }
            app.CommandInProgress = true;
            try
            {
                var asm = (AssemblyDoc)root;
                asm.ResolveAllLightWeightComponents(false);
                var slots = request.Slots?.ToList() ?? TopLevel(asm)
                    .OrderBy(c => InstanceNumber(c.Name2)).Select(c => c.Name2).ToList();
                if (slots.Count < request.Rows.Count)
                {
                    result.Errors.Add($"Hat master'ında {slots.Count} yer tutucu var ama {request.Rows.Count} satır istendi: " + string.Join(", ", slots));
                    return result;
                }
                var featuresBefore = AllFeatureNames(root);

                for (int i = 0; i < slots.Count; i++)
                {
                    var comp = TopLevel(asm).FirstOrDefault(c => string.Equals(c.Name2, slots[i], StringComparison.OrdinalIgnoreCase));
                    if (comp == null)
                    {
                        result.Errors.Add($"Yer tutucu bulunamadı: {slots[i]}. Mevcut: " + string.Join(", ", TopLevel(asm).Select(c => c.Name2)));
                        return result;
                    }
                    root.ClearSelection2(true);
                    comp.Select4(false, null, false);
                    if (i < rowAssemblies.Count)
                    {
                        if (!asm.ReplaceComponents(rowAssemblies[i], "", false, true))
                        {
                            result.Errors.Add($"{slots[i]} → {Path.GetFileName(rowAssemblies[i])} değiştirilemedi.");
                            return result;
                        }
                        result.Log.Add($"{slots[i]} → {Path.GetFileName(rowAssemblies[i])}");
                    }
                    else
                    {
                        if (!root.Extension.DeleteSelection2((int)swDeleteSelectionOptions_e.swDelete_Absorbed) &&
                            TopLevel(asm).Any(c => string.Equals(c.Name2, slots[i], StringComparison.OrdinalIgnoreCase)))
                        {
                            result.Errors.Add($"{slots[i]} silinemedi.");
                            return result;
                        }
                        result.Log.Add($"{slots[i]} silindi (kullanılmayan yer tutucu)");
                    }
                }
                root.ClearSelection2(true);

                var context = new ApplyContext(_session, root, Path.GetFileName(rootMaster), outDir, Path.GetDirectoryName(rootMaster)!, result);
                context.DeletedFeatures.UnionWith(featuresBefore.Except(AllFeatureNames(root), StringComparer.OrdinalIgnoreCase));
                // Kurallar hat montajını DriveWorks'ün verdiği adla (ör. "CONVEYOR LİNE 0001.SLDASM") anabilir.
                foreach (var a in request.RootActions)
                    if (a.Target.Document != null && !File.Exists(Path.Combine(Path.GetDirectoryName(rootMaster)!, a.Target.Document)))
                        context.RootAliases.Add(a.Target.Document);
                foreach (var action in request.RootActions.OrderBy(a => Order(a.Target.Kind)))
                {
                    try
                    {
                        context.Apply(action);
                    }
                    catch (SkippedActionException ex)
                    {
                        result.Skipped.Add($"{action.Rule.Id}: {ex.Message}");
                    }
                    catch (Exception ex)
                    {
                        result.Errors.Add($"{action.Rule.Id} ({action.Target}): {ex.Message}");
                    }
                }

                root.ForceRebuild3(false);
                int whatsWrong = root.Extension.GetWhatsWrongCount();
                if (whatsWrong > 0)
                    result.Warnings.Add($"Hat montajında rebuild sonrası {whatsWrong} hata/uyarı var: {WhatsWrong(root)}");
                if (SaveInside(root, outDir, result)) result.Log.Add("Kaydedildi: " + copiedRoot);
                if (request.ExportStep) Export(root, Path.ChangeExtension(copiedRoot, ".step"), null, result);
            }
            finally
            {
                app.CommandInProgress = false;
                _session.Close(copiedRoot);
            }

            if (request.ExportPdf) ExportDrawings(outDir, result);
            return result;
        }

        /// <summary>
        /// Ortak parçalar: tüm satırların dosyaları, kuralların değer yazdığı ilk satırın kopyasını kullanacak şekilde (dosyalar
        /// kapalıyken) yeniden bağlanır; artık kullanılmayan kopyalar ve teknik resimleri silinir. Hiçbir satırda değer
        /// yazılmadıysa (ör. tüm desteklar bastırılmış) satır 1'in kopyası kullanılır.
        /// </summary>
        private void ShareDocuments(IList<string> shared, IList<GenerationResult> rows, string outDir, GenerationResult result)
        {
            foreach (var key in shared)
            {
                string Copy(int row) => Path.Combine(outDir, Path.GetFileNameWithoutExtension(key) + "-" + row + Path.GetExtension(key));
                var canonicalRow = Enumerable.Range(1, rows.Count).FirstOrDefault(r => rows[r - 1].ModifiedDocuments.Contains(Copy(r)));
                if (canonicalRow == 0) canonicalRow = 1;
                var first = Copy(canonicalRow);
                if (!File.Exists(first))
                {
                    result.Warnings.Add($"Ortak parça {key}: satır {canonicalRow} kopyası bulunamadı ({Path.GetFileName(first)}); satırlar ayrı kopya kullanıyor.");
                    continue;
                }
                for (int row = 1; row <= rows.Count; row++)
                {
                    if (row == canonicalRow) continue;
                    var own = Copy(row);
                    if (!File.Exists(own)) continue;
                    if (_session.App.GetOpenDocumentByName(own) != null) _session.ForceClose(own);
                    int relinked = 0;
                    var suffix = "-" + row;
                    foreach (var file in Directory.GetFiles(outDir).Where(f =>
                                 Path.GetFileNameWithoutExtension(f).EndsWith(suffix, StringComparison.OrdinalIgnoreCase) &&
                                 !string.Equals(f, own, StringComparison.OrdinalIgnoreCase) &&
                                 (f.EndsWith(".SLDASM", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".SLDDRW", StringComparison.OrdinalIgnoreCase))))
                        if (_session.App.ReplaceReferencedDocument(file, own, first)) relinked++;
                    if (relinked == 0)
                    {
                        result.Warnings.Add($"Ortak parça {key}: satır {row} dosyalarında {Path.GetFileName(own)} referansı bulunamadı; ayrı kopya kaldı.");
                        continue;
                    }
                    File.Delete(own);
                    var drawing = Path.ChangeExtension(own, ".SLDDRW");
                    if (File.Exists(drawing)) File.Delete(drawing);
                    result.Log.Add($"Ortak parça: satır {row} {Path.GetFileName(own)} yerine {Path.GetFileName(first)} kullanıyor ({relinked} dosya).");
                }
            }
        }

        private static bool IsInside(string? path, string folder) =>
            !string.IsNullOrEmpty(path) &&
            string.Equals(Path.GetDirectoryName(Path.GetFullPath(path)), Path.GetFullPath(folder).TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// Montajı ve değişmiş alt belgelerini tek tek kaydeder; sadece çıktı klasöründekileri. Klasör dışında değişmiş bir belge
        /// varsa (ör. kopya yanlışlıkla master'a bağlanmış) hiçbir şey kaydedilmez: master dosyaları asla değişmemeli.
        /// </summary>
        private static bool SaveInside(ModelDoc2 root, string outDir, GenerationResult result)
        {
            var docs = new Dictionary<string, ModelDoc2>(StringComparer.OrdinalIgnoreCase) { [root.GetPathName()] = root };
            if (root is AssemblyDoc asm && asm.GetComponents(false) is object[] all)
                foreach (var c in all.OfType<Component2>())
                    if (c.GetModelDoc2() is ModelDoc2 d && !string.IsNullOrEmpty(d.GetPathName()) && !docs.ContainsKey(d.GetPathName()))
                        docs[d.GetPathName()] = d;
            var outside = docs.Where(kv => !IsInside(kv.Key, outDir)).ToList();
            var dirtyOutside = outside.Where(kv => kv.Value.GetSaveFlag()).Select(kv => kv.Key).ToList();
            if (dirtyOutside.Count > 0)
            {
                result.Errors.Add("Çıktı klasörü dışındaki dosyalar değişmiş; hiçbir dosya kaydedilmedi (master'lar korunuyor): " +
                                  string.Join(", ", dirtyOutside));
                return false;
            }
            if (outside.Count > 0)
                result.Warnings.Add("Montaj çıktı klasörü dışındaki dosyalar kullanıyor (değişmedikleri için dokunulmadı): " +
                                    string.Join(", ", outside.Select(kv => Path.GetFileName(kv.Key))));
            bool ok = true;
            foreach (var kv in docs.Where(kv => !ReferenceEquals(kv.Value, root)).Concat(new[] { new KeyValuePair<string, ModelDoc2>(root.GetPathName(), root) }))
            {
                if (!ReferenceEquals(kv.Value, root) && !kv.Value.GetSaveFlag()) continue;
                int errors = 0, warnings = 0;
                if (!kv.Value.Save3((int)swSaveAsOptions_e.swSaveAsOptions_Silent, ref errors, ref warnings))
                {
                    result.Errors.Add($"Kaydedilemedi: {Path.GetFileName(kv.Key)} (swFileSaveError_e = {errors}).");
                    ok = false;
                }
            }
            return ok;
        }

        private static IEnumerable<Component2> TopLevel(AssemblyDoc asm) =>
            (asm.GetComponents(true) as object[] ?? new object[0]).OfType<Component2>();

        /// <summary>"Conveyor Dummy 1-8" → 8 (sıralama için; numara yoksa en sona).</summary>
        internal static int InstanceNumber(string name)
        {
            var dash = name.LastIndexOf('-');
            return dash >= 0 && int.TryParse(name.Substring(dash + 1), out var n) ? n : int.MaxValue;
        }

        /// <summary>Belgedeki tüm özellik adları, alt özellikler (ör. ilişkiler klasöründeki mate'ler) dahil.</summary>
        private static HashSet<string> AllFeatureNames(ModelDoc2 doc)
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            void Walk(Feature f)
            {
                names.Add(f.Name);
                for (var sub = f.GetFirstSubFeature() as Feature; sub != null; sub = sub.GetNextSubFeature() as Feature) Walk(sub);
            }
            for (var f = doc.FirstFeature() as Feature; f != null; f = f.GetNextFeature() as Feature) Walk(f);
            return names;
        }

        /// <summary>Bileşen adı ("Alt-1/Parca-2") → bastırılmış mı.</summary>
        internal static Dictionary<string, bool> ComponentStates(ModelDoc2 doc)
        {
            var states = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            if (doc is AssemblyDoc asm && asm.GetComponents(false) is object[] all)
                foreach (var o in all)
                    if (o is Component2 c) states[c.Name2] = c.IsSuppressed();
            return states;
        }

        /// <summary>SolidWorks'ün "What's Wrong" listesindeki özellikler ve hata kodları (swFeatureError_e).</summary>
        private static string WhatsWrong(ModelDoc2 doc)
        {
            try
            {
                if (!doc.Extension.GetWhatsWrong(out object featuresObj, out object codesObj, out object warningsObj))
                    return "liste alınamadı (SolidWorks 'What's Wrong' penceresine bakın).";
                var features = featuresObj as object[] ?? new object[0];
                var codes = codesObj as int[] ?? new int[0];
                var warnings = warningsObj as bool[] ?? new bool[0];
                var items = new List<string>();
                for (int i = 0; i < features.Length; i++)
                {
                    var name = (features[i] as Feature)?.Name ?? "?";
                    var code = i < codes.Length ? codes[i].ToString() : "?";
                    var kind = i < warnings.Length && warnings[i] ? "uyarı" : "hata";
                    items.Add($"{name} ({kind}, kod {code})");
                }
                return items.Count > 0 ? string.Join(", ", items) : "ayrıntı yok.";
            }
            catch (Exception ex)
            {
                return "liste alınamadı: " + ex.Message;
            }
        }

        private static int Order(TargetKind kind)
        {
            switch (kind)
            {
                case TargetKind.Configuration: return 0;
                case TargetKind.ComponentSuppression: return 1;
                case TargetKind.ComponentReplace: return 2;
                case TargetKind.FeatureSuppression: return 3;
                case TargetKind.GlobalVariable: return 4;
                case TargetKind.Dimension: return 5;
                default: return 6;
            }
        }

        /// <summary>Pack and Go: montaj + parçalar + aynı adlı teknik resimler tek klasöre kopyalanır.</summary>
        private string? CopyMaster(string master, string outDir, string? outputName, bool allowClose, GenerationResult result,
            out Dictionary<string, bool> masterStates, string? suffix = null)
        {
            var app = _session.App;
            bool wasOpen = app.GetOpenDocumentByName(master) != null;
            var doc = _session.Open(master, readOnly: true);
            masterStates = ComponentStates(doc);

            var pack = doc.Extension.GetPackAndGo();
            pack.IncludeDrawings = true;
            pack.IncludeSimulationResults = false;
            pack.IncludeToolboxComponents = false;
            pack.FlattenToSingleFolder = true;
            pack.SetSaveToName(true, outDir);
            if (!string.IsNullOrEmpty(suffix)) pack.AddSuffix = suffix;

            string newRoot = Path.Combine(outDir, Path.GetFileNameWithoutExtension(master) + suffix + Path.GetExtension(master));
            if (!string.IsNullOrWhiteSpace(outputName))
            {
                pack.GetDocumentSaveToNames(out object namesObj, out object _);
                if (namesObj is object[] names)
                {
                    var renamed = names.Cast<string>().ToArray();
                    for (int i = 0; i < renamed.Length; i++)
                    {
                        if (string.Equals(Path.GetFileName(renamed[i]), Path.GetFileName(master), StringComparison.OrdinalIgnoreCase))
                        {
                            newRoot = Path.Combine(outDir, SafeFileName(outputName!) + Path.GetExtension(master));
                            renamed[i] = newRoot;
                        }
                    }
                    pack.SetDocumentSaveToNames(renamed);
                }
            }

            var statuses = doc.Extension.SavePackAndGo(pack) as int[];
            if (statuses != null && statuses.Any(s => s != (int)swPackAndGoSaveStatus_e.swPackAndGoSaveStatus_Succeed))
                result.Warnings.Add("Pack and Go bazı dosyaları kopyalayamadı: durum kodları " + string.Join(",", statuses));
            // Kopya → master eşlemesi (kütüphane parmak izi master dosyasının kendisini de içerir). Pack and Go'nun kendi listesi
            // kullanılmıyor: dolu dönmüyor ve kayıttan önce istenince sonek ayarını bozuyor.
            result.Sources[newRoot] = master;
            if (app.GetDocumentDependencies2(master, true, true, false) is object[] deps)
                foreach (var source in deps.OfType<string>().Where(s => s.Length > 0 && Path.IsPathRooted(s) && File.Exists(s)))
                    result.Sources[Path.Combine(outDir, Path.GetFileNameWithoutExtension(source) + suffix + Path.GetExtension(source))] = source;

            // SolidWorks aynı adlı iki belgeyi aynı anda açamaz: kopyayı açmadan önce master kapanmalı.
            if (wasOpen)
            {
                if (!allowClose)
                {
                    result.Errors.Add("Master montaj SolidWorks'te açık. Kapatıp tekrar deneyin.");
                    return null;
                }
                result.Warnings.Add("Master montaj açıktı; kopyayı açabilmek için kapatıldı.");
                _session.ForceClose(master);
            }
            else
            {
                _session.Close(master);
            }

            if (!File.Exists(newRoot))
            {
                result.Errors.Add("Pack and Go sonrası montaj bulunamadı: " + newRoot);
                return null;
            }

            // Aynı adlı bir belge başka klasörden açıksa SolidWorks kopya yerine onu kullanır: önce gizli olanları kapat, kalırsa dur.
            var conflicts = NameConflicts(outDir);
            foreach (var c in conflicts.Where(c => !c.visible)) app.CloseDoc(c.path);
            conflicts = NameConflicts(outDir);
            if (conflicts.Count > 0)
            {
                // Gizli belge bazen CloseDoc ile kapanmıyor (önceki bir üretimden bellekte kalmış): tek çare SolidWorks'ü yeniden başlatmak.
                result.Errors.Add("SolidWorks'te çıktı dosyalarıyla aynı adlı belgeler açık; kopya montaj bunları kullanırdı. " +
                                  "Görünür olanları kapatın; gizli olanlar (pencerede görünmez) için SolidWorks'ü kapatıp yeniden açın. " +
                                  "Belgeler: " + string.Join(", ", conflicts.Select(c => c.path + (c.visible ? "" : " (gizli)"))));
                return null;
            }
            result.Log.Add($"Master kopyalandı → {outDir}");
            return newRoot;
        }

        private List<(string path, bool visible)> NameConflicts(string outDir)
        {
            var names = new HashSet<string>(Directory.GetFiles(outDir).Select(Path.GetFileName), StringComparer.OrdinalIgnoreCase);
            var list = new List<(string, bool)>();
            if (_session.App.GetDocuments() is object[] docs)
                foreach (var o in docs)
                {
                    if (!(o is ModelDoc2 d)) continue;
                    var p = d.GetPathName();
                    if (string.IsNullOrEmpty(p) || !names.Contains(Path.GetFileName(p))) continue;
                    if (string.Equals(Path.GetDirectoryName(Path.GetFullPath(p)), outDir.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase)) continue;
                    list.Add((p, d.Visible));
                }
            return list;
        }

        /// <summary>Kütüphaneye yayınladıktan sonra: sipariş klasöründeki ana montajı STEP, teknik resimleri PDF olarak dışa aktarır.</summary>
        public void ExportOutputs(string assemblyPath, bool pdf, bool step, GenerationResult result)
        {
            if (step)
            {
                var doc = _session.Open(assemblyPath);
                try { Export(doc, Path.ChangeExtension(assemblyPath, ".step"), null, result); }
                finally { _session.Close(assemblyPath); }
            }
            if (pdf) ExportDrawings(Path.GetDirectoryName(Path.GetFullPath(assemblyPath))!, result);
        }

        private void ExportDrawings(string outDir, GenerationResult result)
        {
            foreach (var drawing in Directory.GetFiles(outDir, "*.SLDDRW"))
            {
                ModelDoc2? doc = null;
                try
                {
                    doc = _session.Open(drawing);
                    doc.ForceRebuild3(false);
                    var data = _session.App.GetExportFileData((int)swExportDataFileType_e.swExportPdfData) as ExportPdfData;
                    data?.SetSheets((int)swExportDataSheetsToExport_e.swExportData_ExportAllSheets, null);
                    Export(doc, Path.ChangeExtension(drawing, ".pdf"), data, result);
                    int e = 0, w = 0;
                    doc.Save3((int)swSaveAsOptions_e.swSaveAsOptions_Silent, ref e, ref w);
                }
                catch (Exception ex)
                {
                    result.Warnings.Add($"Teknik resim işlenemedi ({Path.GetFileName(drawing)}): {ex.Message}");
                }
                finally
                {
                    if (doc != null) _session.Close(drawing);
                }
            }
        }

        private static void Export(ModelDoc2 doc, string path, object? exportData, GenerationResult result)
        {
            int errors = 0, warnings = 0;
            bool ok = doc.Extension.SaveAs(path, (int)swSaveAsVersion_e.swSaveAsCurrentVersion,
                (int)swSaveAsOptions_e.swSaveAsOptions_Silent, exportData, ref errors, ref warnings);
            if (ok) result.ExportedFiles.Add(path);
            else result.Warnings.Add($"Dışa aktarılamadı: {Path.GetFileName(path)} (hata {errors})");
        }

        private static string SafeFileName(string name)
        {
            foreach (var c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
            return name.Trim();
        }

        /// <summary>Tek bir üretim sırasında belge ve bileşen aramalarını önbelleğe alır.</summary>
        private sealed class ApplyContext
        {
            private readonly SwSession _session;
            private readonly ModelDoc2 _root;
            private readonly string _masterRootKey;
            private readonly string _outDir;
            private readonly string _masterDir;
            private readonly GenerationResult _result;
            private readonly HashSet<string> _modified = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            private Dictionary<string, Component2>? _components;
            private readonly string _suffix;

            /// <summary>Kökü gösteren diğer belge adları (ör. kurallarda DriveWorks'ün verdiği hat montajı adı).</summary>
            public HashSet<string> RootAliases { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            /// <summary>Kopyayla birlikte silinen özellikler (ör. silinen konveyörün ilişkileri): bunlara ait eylemler atlanır.</summary>
            public HashSet<string> DeletedFeatures { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            public ApplyContext(SwSession session, ModelDoc2 root, string masterRootKey, string outDir, string masterDir,
                GenerationResult result, string? suffix = null)
            {
                _session = session;
                _root = root;
                _masterRootKey = masterRootKey;
                _outDir = outDir;
                _masterDir = masterDir;
                _result = result;
                _suffix = suffix ?? string.Empty;
            }

            /// <summary>
            /// Kopyadaki bileşen adını master adına çevirir: dosya adına sonek eklenince SolidWorks bileşen adını da değiştirir
            /// ("Conveyor Frame-2-1/Support-2-3" → "Conveyor Frame-1/Support-3").
            /// </summary>
            private string MasterName(string name)
            {
                if (_suffix.Length == 0) return name;
                return string.Join("/", name.Split('/').Select(segment =>
                {
                    var dash = segment.LastIndexOf('-');
                    if (dash <= 0) return segment;
                    var stem = segment.Substring(0, dash);
                    return stem.EndsWith(_suffix, StringComparison.OrdinalIgnoreCase)
                        ? stem.Substring(0, stem.Length - _suffix.Length) + segment.Substring(dash)
                        : segment;
                }));
            }

            public void Apply(ModelAction action)
            {
                var t = action.Target;
                var v = action.Value;
                switch (t.Kind)
                {
                    case TargetKind.Dimension:
                    {
                        var doc = Document(t.Document);
                        var dim = FindDimension(doc, t.Name!, out var available);
                        if (dim == null)
                            throw new InvalidOperationException($"Ölçü bulunamadı: {t.Name}" +
                                                                (available.Length > 0 ? $". Bu özellikteki ölçüler: {available}" : ". Özellik bulunamadı."));
                        double system;
                        switch ((swDimensionParamType_e)dim.GetType())
                        {
                            case swDimensionParamType_e.swDimensionParamTypeDoubleAngular: system = v.AsNumber() * Math.PI / 180.0; break;
                            case swDimensionParamType_e.swDimensionParamTypeInteger: system = Math.Round(v.AsNumber()); break;
                            default: system = v.AsNumber() / 1000.0; break;
                        }
                        int status = dim.SetSystemValue3(system, (int)swSetValueInConfiguration_e.swSetValue_InThisConfiguration, null);
                        if (status != (int)swSetValueReturnStatus_e.swSetValue_Successful)
                            throw new InvalidOperationException($"Ölçü yazılamadı (swSetValueReturnStatus_e = {status}).");
                        Touch(doc);
                        Log(action);
                        break;
                    }
                    case TargetKind.GlobalVariable:
                    {
                        var doc = Document(t.Document);
                        var mgr = doc.GetEquationMgr();
                        int index = -1;
                        for (int i = 0; i < mgr.GetCount(); i++)
                        {
                            var eq = mgr.get_Equation(i) ?? string.Empty;
                            if (eq.TrimStart().StartsWith("\"" + t.Name + "\"", StringComparison.OrdinalIgnoreCase)) { index = i; break; }
                        }
                        if (index < 0) throw new InvalidOperationException($"Global değişken bulunamadı: {t.Name}");
                        mgr.set_Equation(index, $"\"{t.Name}\" = {Core.Expressions.Value.FormatNumber(v.AsNumber())}");
                        mgr.EvaluateAll();
                        Touch(doc);
                        Log(action);
                        break;
                    }
                    case TargetKind.FeatureSuppression:
                    {
                        var doc = Document(t.Document);
                        object? found = doc is PartDoc part ? part.FeatureByName(t.Name)
                            : doc is AssemblyDoc asm ? asm.FeatureByName(t.Name) : null;
                        if (!(found is Feature) && ReferenceEquals(doc, _root) && DeletedFeatures.Contains(t.Name!))
                        {
                            if (!v.AsBool())
                                _result.Warnings.Add($"{t}: kural açık olmasını istiyor ama silinen kopyayla birlikte silindi.");
                            throw new SkippedActionException($"{t.Name} silinen kopyayla birlikte silindi.");
                        }
                        if (!(found is Feature feature)) throw new InvalidOperationException($"Özellik bulunamadı: {t.Name}");
                        var state = v.AsBool() ? swFeatureSuppressionAction_e.swSuppressFeature : swFeatureSuppressionAction_e.swUnSuppressFeature;
                        if (!feature.SetSuppression2((int)state, (int)swInConfigurationOpts_e.swThisConfiguration, null) &&
                            IsSuppressed(feature) != v.AsBool())
                            throw new InvalidOperationException("Bastırma durumu değiştirilemedi.");
                        Touch(doc);
                        Log(action);
                        break;
                    }
                    case TargetKind.ComponentSuppression:
                    {
                        var comp = Component(t.Component!);
                        var state = v.AsBool() ? swComponentSuppressionState_e.swComponentSuppressed : swComponentSuppressionState_e.swComponentFullyResolved;
                        int status = comp.SetSuppression2((int)state);
                        if (status != (int)swSuppressionError_e.swSuppressionChangeOk)
                            throw new InvalidOperationException($"Bileşen bastırma değiştirilemedi (swSuppressionError_e = {status}).");
                        Log(action);
                        break;
                    }
                    case TargetKind.ComponentReplace:
                    {
                        var comp = Component(t.Component!);
                        var wanted = v.AsText();
                        var name = Path.GetFileName(wanted);
                        var current = Path.GetFileName(comp.GetPathName() ?? string.Empty);
                        if (string.Equals(current, name, StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(current, Path.GetFileNameWithoutExtension(name) + _suffix + Path.GetExtension(name), StringComparison.OrdinalIgnoreCase))
                        {
                            _result.Log.Add($"{t} = {name} (zaten bu dosya, değişiklik yok)");
                            break;
                        }
                        // Yeni dosya master klasöründen (ya da alt klasörlerinden) sipariş klasörüne kopyalanır: sipariş kendi
                        // kendine yeter, sonraki ölçü kuralları kopyayı değiştirir, master asla değişmez.
                        var source = Path.IsPathRooted(wanted) ? wanted : FindLibraryFile(name);
                        if (source == null || !File.Exists(source))
                            throw new FileNotFoundException($"Yeni bileşen dosyası master klasöründe bulunamadı: {name} (aranan: {_masterDir})");
                        var dest = Path.Combine(_outDir, Path.GetFileNameWithoutExtension(name) + _suffix + Path.GetExtension(name));
                        _result.Sources[dest] = source;
                        if (!File.Exists(dest))
                        {
                            File.Copy(source, dest);
                            File.SetAttributes(dest, File.GetAttributes(dest) & ~FileAttributes.ReadOnly);
                            if (string.Equals(Path.GetExtension(dest), ".SLDASM", StringComparison.OrdinalIgnoreCase))
                                _result.Warnings.Add($"{name} bir montaj; alt bileşenleri hâlâ master klasöründen referanslanıyor.");
                        }
                        _root.ClearSelection2(true);
                        comp.Select4(false, null, false);
                        if (!((AssemblyDoc)_root).ReplaceComponents(dest, "", false, true))
                            throw new InvalidOperationException("Bileşen değiştirilemedi.");
                        _components = null; // ağaç değişti
                        Log(action);
                        break;
                    }
                    case TargetKind.Configuration:
                    {
                        if (t.Component != null)
                        {
                            Component(t.Component).ReferencedConfiguration = v.AsText();
                        }
                        else if (!Document(t.Document).ShowConfiguration2(v.AsText()))
                        {
                            throw new InvalidOperationException($"Konfigürasyon bulunamadı: {v.AsText()}");
                        }
                        Log(action);
                        break;
                    }
                    case TargetKind.CustomProperty:
                    {
                        var doc = Document(t.Document);
                        var mgr = doc.Extension.get_CustomPropertyManager("");
                        mgr.Add3(t.Name, (int)swCustomInfoType_e.swCustomInfoText, v.AsText(),
                            (int)swCustomPropertyAddOption_e.swCustomPropertyReplaceValue);
                        Touch(doc);
                        Log(action);
                        break;
                    }
                    case TargetKind.OutputFileName:
                        break; // Pack and Go sırasında uygulandı
                }
            }

            public void RebuildModifiedParts()
            {
                foreach (var path in _modified)
                    if (_session.App.GetOpenDocumentByName(path) is ModelDoc2 doc && !ReferenceEquals(doc, _root))
                        doc.EditRebuild3();
            }

            /// <summary>
            /// "D1@Sketch1" ölçüsünü snapshot okuyucusunun bulduğu yoldan bulur: özelliği adıyla bul, ölçülerinde ilk "D1"i al.
            /// Böylece her zaman okunan ölçünün kendisi değişir. (ModelDoc2.Parameter bazı çizim/özellik ölçülerini bu adla
            /// bulamıyor; sadece yedek olarak kullanılır.)
            /// </summary>
            private static Dimension? FindDimension(ModelDoc2 doc, string name, out string available)
            {
                available = string.Empty;
                var at = name.IndexOf('@');
                if (at > 0)
                {
                    var dimName = name.Substring(0, at);
                    var featureName = name.Substring(at + 1);
                    var feature = FindFeature(doc, featureName);
                    if (feature != null)
                    {
                        var seen = new List<string>();
                        for (var display = feature.GetFirstDisplayDimension() as DisplayDimension; display != null;
                             display = feature.GetNextDisplayDimension(display) as DisplayDimension)
                        {
                            var dim = display.GetDimension2(0);
                            if (dim == null) continue;
                            if (string.Equals(dim.Name, dimName, StringComparison.OrdinalIgnoreCase)) return dim;
                            seen.Add($"{dim.Name} ({dim.FullName})");
                        }
                        available = string.Join(", ", seen);
                    }
                }
                return doc.Parameter(name) as Dimension;
            }

            private static Feature? FindFeature(ModelDoc2 doc, string name)
            {
                object? found = doc is PartDoc part ? part.FeatureByName(name) : doc is AssemblyDoc asm ? asm.FeatureByName(name) : null;
                if (found is Feature direct) return direct;
                // Yedek: tüm ağaç (alt özellikler dahil) adla taranır.
                for (var f = doc.FirstFeature() as Feature; f != null; f = f.GetNextFeature() as Feature)
                {
                    var hit = FindIn(f, name);
                    if (hit != null) return hit;
                }
                return null;
            }

            private static Feature? FindIn(Feature feature, string name)
            {
                if (string.Equals(feature.Name, name, StringComparison.OrdinalIgnoreCase)) return feature;
                for (var sub = feature.GetFirstSubFeature() as Feature; sub != null; sub = sub.GetNextSubFeature() as Feature)
                {
                    var hit = FindIn(sub, name);
                    if (hit != null) return hit;
                }
                return null;
            }

            /// <summary>Kopya açıldıktan sonra: master'da açık bileşen bastırılmış geldiyse yeniden yüklemeyi dener; dışarıdaki dosyayı kullananları bildirir.</summary>
            public void CheckLoaded(Dictionary<string, bool> masterStates)
            {
                foreach (var c in AllComponents())
                {
                    if (masterStates.TryGetValue(MasterName(c.Name2), out var wasSuppressed) && !wasSuppressed && c.IsSuppressed())
                    {
                        c.SetSuppression2((int)swComponentSuppressionState_e.swComponentFullyResolved);
                        if (c.IsSuppressed())
                            _result.Errors.Add($"'{c.Name2}' kopya açılırken yüklenemedi (master'da açık). SolidWorks'te aynı adlı bir dosya başka " +
                                               "klasörden açık kalmış olabilir: SolidWorks'ü tamamen kapatıp tekrar deneyin.");
                        else
                            _result.Warnings.Add($"'{c.Name2}' kopya açılırken bastırılmış geldi (master'da açık); yeniden yüklendi.");
                    }
                    if (c.IsSuppressed()) continue;
                    var path = c.GetPathName();
                    if (!string.IsNullOrEmpty(path) && !Path.GetFullPath(path).StartsWith(_outDir, StringComparison.OrdinalIgnoreCase))
                        _result.Errors.Add($"'{c.Name2}' çıktı klasöründeki kopya yerine başka bir dosyayı kullanıyor: {path}. " +
                                           "SolidWorks'te aynı adlı bir dosya açık kalmış olabilir: SolidWorks'ü tamamen kapatıp tekrar deneyin.");
                }
            }

            /// <summary>Kural istemediği hâlde (master'da açıkken) bastırılmış bileşenleri bildirir ve geri açar.</summary>
            public void RestoreUnexpectedSuppression(Dictionary<string, bool> masterStates, HashSet<string> allowed, string when)
            {
                foreach (var c in AllComponents())
                {
                    var name = MasterName(c.Name2);
                    if (!masterStates.TryGetValue(name, out var wasSuppressed) || wasSuppressed || !c.IsSuppressed()) continue;
                    if (allowed.Any(a => string.Equals(a, name, StringComparison.OrdinalIgnoreCase) ||
                                         name.StartsWith(a + "/", StringComparison.OrdinalIgnoreCase)))
                        continue;
                    int status = c.SetSuppression2((int)swComponentSuppressionState_e.swComponentFullyResolved);
                    _result.Warnings.Add($"'{name}' {when} bastırılmış hâle geldi; hiçbir kural bunu istemiyordu" +
                                         (status == (int)swSuppressionError_e.swSuppressionChangeOk ? ", geri açıldı." : $", geri açılamadı (kod {status})."));
                    _components = null;
                }
            }

            private IEnumerable<Component2> AllComponents()
            {
                if (((AssemblyDoc)_root).GetComponents(false) is object[] all)
                    foreach (var o in all)
                        if (o is Component2 c) yield return c;
            }

            private static bool IsSuppressed(Feature feature)
            {
                var states = feature.IsSuppressed2((int)swInConfigurationOpts_e.swThisConfiguration, null) as bool[];
                return states != null && states.Length > 0 && states[0];
            }

            private void Touch(ModelDoc2 doc)
            {
                _modified.Add(doc.GetPathName());
                _result.ModifiedDocuments.Add(doc.GetPathName());
            }

            private void Log(ModelAction action) => _result.Log.Add($"{action.Target} = {action.Value}");

            /// <summary>
            /// Master'daki dosya anahtarı → kopyadaki açık belge. Belge bu siparişte kullanılmıyorsa (ör. başka kapak tipinin
            /// dosyası) ya da sadece bastırılmış bileşenlerde geçiyorsa yüklenmemiştir: eylem atlanır.
            /// </summary>
            private ModelDoc2 Document(string? key)
            {
                if (string.IsNullOrWhiteSpace(key) || string.Equals(key, _masterRootKey, StringComparison.OrdinalIgnoreCase) ||
                    RootAliases.Contains(key!))
                    return _root;
                var path = Path.Combine(_outDir, Path.GetFileNameWithoutExtension(key) + _suffix + Path.GetExtension(key));
                if (_session.App.GetOpenDocumentByName(path) is ModelDoc2 open) return open;
                throw new SkippedActionException(File.Exists(path)
                    ? $"{key} bu siparişte sadece bastırılmış bileşenlerde ya da hiç kullanılmıyor."
                    : $"{key} bu siparişte kullanılmıyor.");
            }

            /// <summary>Değiştirme dosyasını master klasöründe, sonra alt klasörlerinde, sonra bir üst klasörde arar.</summary>
            private string? FindLibraryFile(string name)
            {
                var direct = Path.Combine(_masterDir, name);
                if (File.Exists(direct)) return direct;
                foreach (var root in new[] { _masterDir, Path.GetDirectoryName(_masterDir) })
                {
                    if (string.IsNullOrEmpty(root) || !Directory.Exists(root)) continue;
                    try
                    {
                        var found = Directory.EnumerateFiles(root!, name, SearchOption.AllDirectories)
                            .FirstOrDefault(f => !f.StartsWith(_outDir, StringComparison.OrdinalIgnoreCase));
                        if (found != null) return found;
                    }
                    catch (UnauthorizedAccessException)
                    {
                        // erişilemeyen klasörleri atla
                    }
                }
                return null;
            }

            private Component2 Component(string path)
            {
                if (_components == null)
                {
                    _components = new Dictionary<string, Component2>(StringComparer.OrdinalIgnoreCase);
                    if (((AssemblyDoc)_root).GetComponents(false) is object[] all)
                        foreach (Component2 c in all)
                        {
                            _components[c.Name2] = c;
                            var masterName = MasterName(c.Name2);
                            if (!_components.ContainsKey(masterName)) _components[masterName] = c;
                        }
                }
                if (_components.TryGetValue(path, out var comp)) return comp;
                throw new InvalidOperationException($"Bileşen bulunamadı: {path}");
            }
        }
    }
}
