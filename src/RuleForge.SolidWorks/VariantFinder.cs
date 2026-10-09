using System;
using System.Collections.Generic;
using System.Linq;

namespace RuleForge.SolidWorks
{
    /// <summary>
    /// Montaj dosyalarının referanslarını dosyayı açmadan okur (SolidWorks "Referansları Bul" ile aynı API).
    /// Üst montaj tespiti için <see cref="Core.Files.TopAssemblyDetector"/>'a veri sağlar.
    /// </summary>
    public sealed class VariantFinder
    {
        private readonly SwSession _session;

        public VariantFinder(SwSession session)
        {
            _session = session;
        }

        /// <summary>Doğrudan referanslar (sadece bir seviye).</summary>
        public IEnumerable<string> DirectDependencies(string path) => Read(path, traverse: false);

        /// <summary>Tüm seviyelerdeki referans sayısı (birden çok aday arasından ana montajı seçmek için).</summary>
        public int TotalDependencyCount(string path) =>
            Read(path, traverse: true).Count(p => p.EndsWith(".sldprt", StringComparison.OrdinalIgnoreCase) ||
                                                  p.EndsWith(".sldasm", StringComparison.OrdinalIgnoreCase));

        private List<string> Read(string path, bool traverse)
        {
            // Dönüş: [ad1, yol1, ad2, yol2, ...]. Searchflag = true: dosya konumu kurallarıyla çözülmüş güncel yollar.
            object raw = _session.App.GetDocumentDependencies2(path, traverse, true, false);
            var list = new List<string>();
            if (raw is object[] items)
                foreach (var item in items)
                    if (item is string s && s.Length > 0)
                        list.Add(s);
            return list;
        }
    }
}
