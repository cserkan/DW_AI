using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using RuleForge.Core.Files;
using RuleForge.Core.Model;

namespace RuleForge.SolidWorks
{
    /// <summary>
    /// Bir çalışma klasöründe üretilmiş modeli ortak kütüphaneye taşır: her parça/alt montaj benzersiz adla kütüphaneye
    /// yazılır; aynı içerikte bir dosya daha önce üretildiyse yenisi yazılmaz, eskisi kullanılır. Sipariş klasörüne sadece
    /// ana montaj (ve teknik resmi) konur; ana montaj kütüphanedeki dosyalara bağlanır.
    /// </summary>
    public sealed class LibraryPublisher
    {
        private readonly SwSession _session;

        public LibraryPublisher(SwSession session)
        {
            _session = session;
        }

        /// <param name="workRoot">Çalışma klasöründeki ana montaj (kapalı olmalı).</param>
        /// <param name="sources">Çalışma kopyası → master dosyası (master'ın değişip değişmediği parmak izine girer).</param>
        /// <param name="rootName">Sipariş klasöründeki ana montajın adı (uzantısız).</param>
        public void Publish(string workRoot, string outDir, string libraryDir, string rootName, string order,
            IDictionary<string, string> sources, GenerationResult result)
        {
            workRoot = Path.GetFullPath(workRoot);
            var workDir = Path.GetDirectoryName(workRoot)!;
            outDir = Path.GetFullPath(outDir);
            Directory.CreateDirectory(outDir);
            var library = PartLibrary.Open(libraryDir);

            var snapshot = new SnapshotExtractor(_session).Extract(workRoot);
            var infoByPath = snapshot.Documents.Where(d => !string.IsNullOrEmpty(d.Path))
                .GroupBy(d => Path.GetFullPath(d.Path), StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

            // Dosyalar arası referanslar (açmadan), sadece çalışma klasörü içindekiler.
            var deps = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            List<string> Deps(string file)
            {
                if (deps.TryGetValue(file, out var list)) return list;
                list = new List<string>();
                if (_session.App.GetDocumentDependencies2(file, false, true, false) is object[] items)
                    foreach (var s in items.OfType<string>())
                        if (s.Length > 0 && Path.IsPathRooted(s) && File.Exists(s) &&
                            string.Equals(Path.GetDirectoryName(Path.GetFullPath(s)), workDir, StringComparison.OrdinalIgnoreCase) &&
                            !list.Contains(s, StringComparer.OrdinalIgnoreCase))
                            list.Add(Path.GetFullPath(s));
                deps[file] = list;
                return list;
            }

            // Alt parçalar önce (post-order).
            var order_ = new List<string>();
            var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            void Visit(string file)
            {
                if (!visited.Add(file)) return;
                foreach (var d in Deps(file)) Visit(d);
                order_.Add(file);
            }
            Visit(workRoot);

            var fingerprints = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var masterHashes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            string Fingerprint(string file)
            {
                if (fingerprints.TryGetValue(file, out var fp)) return fp;
                var source = sources.TryGetValue(file, out var s) ? s : file;
                if (!masterHashes.TryGetValue(source, out var masterHash))
                    masterHashes[source] = masterHash = File.Exists(source) ? PartLibrary.HashFile(source) : string.Empty;
                var lines = new List<string> { "v1", "master:" + Path.GetFileName(source) + ":" + masterHash };
                lines.Add(infoByPath.TryGetValue(file, out var info) ? PartLibrary.DocumentContent(info) : "yuklenmedi");

                var children = ChildrenOf(snapshot, file, workRoot);
                var covered = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var componentLines = new List<string>();
                foreach (var c in children)
                {
                    var childPath = Path.Combine(workDir, c.DocumentKey);
                    if (File.Exists(childPath)) covered.Add(childPath);
                    componentLines.Add(PartLibrary.ComponentLine(c, File.Exists(childPath) ? Fingerprint(childPath) : "dis:" + c.DocumentKey));
                }
                lines.AddRange(componentLines.OrderBy(l => l, StringComparer.Ordinal));
                // Bileşen listesinde görünmeyen referanslar (ör. bastırılmış alt montajın içi): yine de içeriğe girer.
                lines.AddRange(Deps(file).Where(d => !covered.Contains(d)).Select(d => "r:" + Fingerprint(d)).OrderBy(l => l, StringComparer.Ordinal));
                fp = PartLibrary.Hash(string.Join("\n", lines));
                fingerprints[file] = fp;
                return fp;
            }

            var parents = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (var file in order_)
                foreach (var d in Deps(file))
                {
                    if (!parents.TryGetValue(d, out var p)) parents[d] = p = new List<string>();
                    p.Add(file);
                }

            var published = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            int reused = 0, created = 0;
            foreach (var file in order_.Where(f => !string.Equals(f, workRoot, StringComparison.OrdinalIgnoreCase)))
            {
                var fp = Fingerprint(file);
                var master = sources.TryGetValue(file, out var src) ? Path.GetFileName(src) : Path.GetFileName(file);
                var existing = library.Find(fp, Path.GetExtension(file));
                if (existing != null)
                {
                    published[file] = existing;
                    reused++;
                }
                else
                {
                    var target = library.Reserve(master, fp, order, Summary(infoByPath.TryGetValue(file, out var info) ? info : null));
                    File.Copy(file, target);
                    File.SetAttributes(target, File.GetAttributes(target) & ~FileAttributes.ReadOnly);
                    published[file] = target;
                    created++;
                }
                if (parents.TryGetValue(file, out var ps))
                    foreach (var p in ps)
                        if (!_session.App.ReplaceReferencedDocument(p, file, published[file]))
                            result.Warnings.Add($"Kütüphane: {Path.GetFileName(p)} içindeki {Path.GetFileName(file)} referansı değiştirilemedi.");
            }

            var outRoot = Path.Combine(outDir, rootName + Path.GetExtension(workRoot));
            File.Copy(workRoot, outRoot, true);
            File.SetAttributes(outRoot, File.GetAttributes(outRoot) & ~FileAttributes.ReadOnly);
            published[workRoot] = outRoot;

            // Teknik resimler: modelle aynı adlı resim, modelin yeni adıyla yanına (kütüphanede zaten varsa atlanır).
            int drawings = 0;
            foreach (var drawing in Directory.GetFiles(workDir, "*.SLDDRW"))
            {
                var stem = Path.GetFileNameWithoutExtension(drawing);
                var model = order_.FirstOrDefault(f => string.Equals(Path.GetFileNameWithoutExtension(f), stem, StringComparison.OrdinalIgnoreCase));
                if (model == null) continue; // bu siparişte kullanılmayan modelin resmi
                var target = Path.ChangeExtension(published[model], ".SLDDRW");
                if (File.Exists(target) && !string.Equals(model, workRoot, StringComparison.OrdinalIgnoreCase)) continue;
                foreach (var d in Deps(drawing))
                    if (published.TryGetValue(d, out var newRef)) _session.App.ReplaceReferencedDocument(drawing, d, newRef);
                File.Copy(drawing, target, true);
                File.SetAttributes(target, File.GetAttributes(target) & ~FileAttributes.ReadOnly);
                drawings++;
            }

            library.Save();

            // Kontrol: sipariş montajı artık çalışma klasörüne hiç referans vermemeli.
            if (_session.App.GetDocumentDependencies2(outRoot, true, true, false) is object[] all)
            {
                var stale = all.OfType<string>().Where(s => s.StartsWith(workDir, StringComparison.OrdinalIgnoreCase)).Distinct().ToList();
                if (stale.Count > 0)
                    result.Errors.Add("Kütüphane: ana montaj hâlâ çalışma klasörüne bağlı: " + string.Join(", ", stale.Select(Path.GetFileName)));
            }

            result.AssemblyPath = outRoot;
            result.Log.Add($"Kütüphane ({library.Folder}): {created} yeni dosya, {reused} dosya daha önce üretilmişti (yeniden kullanıldı), {drawings} teknik resim.");
            if (result.Errors.Count == 0)
            {
                try { Directory.Delete(workDir, true); }
                catch (Exception ex) { result.Warnings.Add($"Çalışma klasörü silinemedi ({workDir}): {ex.Message}"); }
            }
        }

        /// <summary>Bir belgenin montajdaki doğrudan alt bileşenleri (ilk açık örneğine göre).</summary>
        private static List<ComponentInfo> ChildrenOf(ModelSnapshot snapshot, string file, string root)
        {
            if (string.Equals(file, root, StringComparison.OrdinalIgnoreCase))
                return snapshot.Components.Where(c => c.ParentPath == null).ToList();
            var key = Path.GetFileName(file);
            var instance = snapshot.Components.FirstOrDefault(c => !c.Suppressed && string.Equals(c.DocumentKey, key, StringComparison.OrdinalIgnoreCase));
            return instance == null ? new List<ComponentInfo>() : snapshot.Components.Where(c => c.ParentPath == instance.Path).ToList();
        }

        /// <summary>Kütüphane listesinde dosyayı tanıtan kısa metin: açıklama ya da parça no.</summary>
        private static string? Summary(DocumentInfo? info)
        {
            if (info == null) return null;
            foreach (var name in new[] { "Description", "PartNumber", "Açıklama", "Assembly Name" })
                if (info.CustomProperties.TryGetValue(name, out var v) && !string.IsNullOrWhiteSpace(v)) return v;
            return null;
        }
    }
}
