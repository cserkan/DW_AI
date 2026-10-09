using System;
using System.Collections.Generic;
using System.Linq;
using RuleForge.Core.Model;

namespace RuleForge.Inference
{
    /// <summary>Bir varyantın dosya/bileşen adlarını referans modelin (master veya ilk varyant) adlarına çeviren eşleme.</summary>
    public sealed class NameMap
    {
        public Dictionary<string, string> Documents { get; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, string> Components { get; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Dosya adları her varyantta farklı kod olduğunda (ör. 100234.SLDPRT / 100871.SLDPRT) parçaları
    /// adlarından değil yapılarından eşleştirir: montaj ağacındaki yer, örnek numarası ve parçanın
    /// özellik/ölçü adları. DriveWorks her varyantı aynı master'dan kopyaladığı için bunlar korunur.
    /// </summary>
    public static class StructureMatcher
    {
        private const double MinScore = 0.35;

        /// <summary>İlk iki varyantın dosya adlarının çoğu farklıysa yapısal eşleme gerekir.</summary>
        public static bool IsNeeded(IReadOnlyList<ModelSnapshot> snapshots)
        {
            if (snapshots.Count < 2) return false;
            var a = new HashSet<string>(snapshots[0].Documents.Select(d => d.Key), StringComparer.OrdinalIgnoreCase);
            var b = snapshots[1].Documents.Select(d => d.Key).ToList();
            if (a.Count == 0 || b.Count == 0) return false;
            return b.Count(a.Contains) < 0.5 * Math.Min(a.Count, b.Count);
        }

        public static NameMap Map(ModelSnapshot reference, ModelSnapshot variant)
        {
            var map = new NameMap();
            map.Documents[variant.RootDocument] = reference.RootDocument;
            MatchChildren(reference, variant, null, null, map);
            return map;
        }

        private static void MatchChildren(ModelSnapshot reference, ModelSnapshot variant, string? refParent, string? varParent, NameMap map)
        {
            var refKids = reference.Components.Where(c => c.ParentPath == refParent && !c.IsPatternInstance).ToList();
            var varKids = variant.Components.Where(c => c.ParentPath == varParent && !c.IsPatternInstance).ToList();
            if (refKids.Count == 0 || varKids.Count == 0) return;

            var pairs = new List<(int r, int v, double score)>();
            for (int r = 0; r < refKids.Count; r++)
            for (int v = 0; v < varKids.Count; v++)
            {
                var score = Score(reference, refKids[r], r, refKids.Count, variant, varKids[v], v, varKids.Count, map);
                if (score >= MinScore) pairs.Add((r, v, score));
            }

            var usedRef = new HashSet<int>();
            var usedVar = new HashSet<int>();
            foreach (var p in pairs.OrderByDescending(p => p.score).ThenBy(p => Math.Abs(p.r - p.v)))
            {
                if (usedRef.Contains(p.r) || usedVar.Contains(p.v)) continue;
                usedRef.Add(p.r);
                usedVar.Add(p.v);
                var rc = refKids[p.r];
                var vc = varKids[p.v];
                map.Components[vc.Path] = rc.Path;
                // Aynı yerde yapısı tamamen farklı bir parça varsa bu bir "parça değişimi"dir (ör. farklı kulp):
                // dosyayı referanstakiyle eşleme, kendi adıyla kalsın ki değişim kural olarak görünsün.
                if (!map.Documents.ContainsKey(vc.DocumentKey) && !IsReplacement(reference, rc, variant, vc))
                    map.Documents[vc.DocumentKey] = rc.DocumentKey;
                MatchChildren(reference, variant, rc.Path, vc.Path, map);
            }
        }

        private static double Score(ModelSnapshot reference, ComponentInfo rc, int ri, int rn,
            ModelSnapshot variant, ComponentInfo vc, int vi, int vn, NameMap map)
        {
            var rdoc = reference.FindDocument(rc.DocumentKey);
            var vdoc = variant.FindDocument(vc.DocumentKey);
            if (rdoc != null && vdoc != null && rdoc.Kind != vdoc.Kind) return 0;

            double score = 0;

            // Bu dosya daha önce başka bir yerde eşlendiyse aynı eşlemeyi güçlü biçimde tercih et.
            if (map.Documents.TryGetValue(vc.DocumentKey, out var mapped))
                score += string.Equals(mapped, rc.DocumentKey, StringComparison.OrdinalIgnoreCase) ? 1.0 : -1.0;

            // Yapı benzerliği: özellik + ölçü + özel özellik adları (dosya adından bağımsız).
            if (rdoc != null && vdoc != null && (rdoc.Features.Count + rdoc.Dimensions.Count) > 0)
                score += Jaccard(Signature(rdoc), Signature(vdoc));
            else
                score += 0.3; // bastırılmış bileşen: belge yüklenmemiş, yapı bilinmiyor

            // Ağaçtaki sıra ve örnek numarası (Ayak-1, Ayak-2 ...).
            if (ri == vi) score += 0.3;
            else score += 0.3 * (1 - Math.Abs((double)ri / Math.Max(1, rn - 1) - (double)vi / Math.Max(1, vn - 1)));
            if (InstanceNumber(rc.Name) == InstanceNumber(vc.Name)) score += 0.2;

            // Dosya adı da aynıysa (kodlanmamış parçalar, ör. kütüphane parçaları) kesin eşleşme.
            if (string.Equals(rc.DocumentKey, vc.DocumentKey, StringComparison.OrdinalIgnoreCase)) score += 1.0;
            return score;
        }

        private static bool IsReplacement(ModelSnapshot reference, ComponentInfo rc, ModelSnapshot variant, ComponentInfo vc)
        {
            if (string.Equals(rc.DocumentKey, vc.DocumentKey, StringComparison.OrdinalIgnoreCase)) return false;
            var rdoc = reference.FindDocument(rc.DocumentKey);
            var vdoc = variant.FindDocument(vc.DocumentKey);
            if (rdoc == null || vdoc == null) return false; // bastırılmış: bilinmiyor
            return Jaccard(Signature(rdoc), Signature(vdoc)) < 0.5;
        }

        private static HashSet<string> Signature(DocumentInfo doc)
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var f in doc.Features) set.Add("f:" + f.Name + ":" + f.Type);
            foreach (var d in doc.Dimensions) set.Add("d:" + d.Name);
            foreach (var p in doc.CustomProperties.Keys) set.Add("p:" + p);
            foreach (var e in doc.Equations.Where(e => e.IsGlobalVariable)) set.Add("g:" + e.Name);
            return set;
        }

        private static double Jaccard(HashSet<string> a, HashSet<string> b)
        {
            if (a.Count == 0 && b.Count == 0) return 0.5;
            int inter = a.Count(b.Contains);
            return (double)inter / (a.Count + b.Count - inter);
        }

        private static string InstanceNumber(string name)
        {
            var dash = name.LastIndexOf('-');
            return dash >= 0 ? name.Substring(dash + 1) : string.Empty;
        }
    }
}
