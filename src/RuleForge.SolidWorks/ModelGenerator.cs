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
            if (Directory.EnumerateFileSystemEntries(outDir).Any())
            {
                result.Errors.Add($"Çıktı klasörü boş değil: {outDir}");
                return result;
            }

            var outputName = request.Actions.FirstOrDefault(a => a.Target.Kind == TargetKind.OutputFileName)?.Value.AsText();
            var copiedRoot = CopyMaster(master, outDir, outputName, request.AllowClosingMaster, result);
            if (copiedRoot == null) return result;
            result.AssemblyPath = copiedRoot;

            var app = _session.App;
            var root = _session.Open(copiedRoot);
            app.CommandInProgress = true;
            try
            {
                if (root is AssemblyDoc asm) asm.ResolveAllLightWeightComponents(false);
                var context = new ApplyContext(_session, root, Path.GetFileName(master), outDir, Path.GetDirectoryName(master)!, result);

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
                }

                context.RebuildModifiedParts();
                root.ForceRebuild3(false);
                int whatsWrong = root.Extension.GetWhatsWrongCount();
                if (whatsWrong > 0)
                    result.Warnings.Add($"Rebuild sonrası {whatsWrong} hata/uyarı var (SolidWorks 'What's Wrong' listesine bakın).");

                int errors = 0, warnings = 0;
                var saveOptions = (int)(swSaveAsOptions_e.swSaveAsOptions_Silent | swSaveAsOptions_e.swSaveAsOptions_SaveReferenced);
                if (!root.Save3(saveOptions, ref errors, ref warnings))
                    result.Errors.Add($"Kaydedilemedi (swFileSaveError_e = {errors}).");
                else
                    result.Log.Add("Kaydedildi: " + copiedRoot);

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
        private string? CopyMaster(string master, string outDir, string? outputName, bool allowClose, GenerationResult result)
        {
            var app = _session.App;
            bool wasOpen = app.GetOpenDocumentByName(master) != null;
            var doc = _session.Open(master, readOnly: true);

            var pack = doc.Extension.GetPackAndGo();
            pack.IncludeDrawings = true;
            pack.IncludeSimulationResults = false;
            pack.IncludeToolboxComponents = false;
            pack.FlattenToSingleFolder = true;
            pack.SetSaveToName(true, outDir);

            string newRoot = Path.Combine(outDir, Path.GetFileName(master));
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
                result.Errors.Add("SolidWorks'te çıktı dosyalarıyla aynı adlı belgeler açık; kopya montaj bunları kullanırdı. " +
                                  "Bu belgeleri kapatıp tekrar deneyin: " + string.Join(", ", conflicts.Select(c => c.path)));
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

            public ApplyContext(SwSession session, ModelDoc2 root, string masterRootKey, string outDir, string masterDir,
                GenerationResult result)
            {
                _session = session;
                _root = root;
                _masterRootKey = masterRootKey;
                _outDir = outDir;
                _masterDir = masterDir;
                _result = result;
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
                        if (!(doc.Parameter(t.Name) is Dimension dim))
                            throw new InvalidOperationException($"Ölçü bulunamadı: {t.Name}");
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
                        if (!(found is Feature feature)) throw new InvalidOperationException($"Özellik bulunamadı: {t.Name}");
                        var state = v.AsBool() ? swFeatureSuppressionAction_e.swSuppressFeature : swFeatureSuppressionAction_e.swUnSuppressFeature;
                        if (!feature.SetSuppression2((int)state, (int)swInConfigurationOpts_e.swThisConfiguration, null))
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
                        if (string.Equals(Path.GetFileName(comp.GetPathName() ?? string.Empty), name, StringComparison.OrdinalIgnoreCase))
                        {
                            _result.Log.Add($"{t} = {name} (zaten bu dosya, değişiklik yok)");
                            break;
                        }
                        // Yeni dosya master klasöründen (ya da alt klasörlerinden) sipariş klasörüne kopyalanır: sipariş kendi
                        // kendine yeter, sonraki ölçü kuralları kopyayı değiştirir, master asla değişmez.
                        var source = Path.IsPathRooted(wanted) ? wanted : FindLibraryFile(name);
                        if (source == null || !File.Exists(source))
                            throw new FileNotFoundException($"Yeni bileşen dosyası master klasöründe bulunamadı: {name} (aranan: {_masterDir})");
                        var dest = Path.Combine(_outDir, name);
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

            private void Touch(ModelDoc2 doc) => _modified.Add(doc.GetPathName());

            private void Log(ModelAction action) => _result.Log.Add($"{action.Target} = {action.Value}");

            /// <summary>
            /// Master'daki dosya anahtarı → kopyadaki açık belge. Belge bu siparişte kullanılmıyorsa (ör. başka kapak tipinin
            /// dosyası) ya da sadece bastırılmış bileşenlerde geçiyorsa yüklenmemiştir: eylem atlanır.
            /// </summary>
            private ModelDoc2 Document(string? key)
            {
                if (string.IsNullOrWhiteSpace(key) || string.Equals(key, _masterRootKey, StringComparison.OrdinalIgnoreCase))
                    return _root;
                var path = Path.Combine(_outDir, key);
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
                            _components[c.Name2] = c;
                }
                if (_components.TryGetValue(path, out var comp)) return comp;
                throw new InvalidOperationException($"Bileşen bulunamadı: {path}");
            }
        }
    }
}
