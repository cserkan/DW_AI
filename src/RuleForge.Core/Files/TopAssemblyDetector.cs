using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace RuleForge.Core.Files
{
    /// <summary>Bir varyantın üst (ana) montajı.</summary>
    public sealed class VariantAssembly
    {
        public string Path { get; set; } = string.Empty;

        /// <summary>Varyant klasörü: verilen kök klasörün ilk alt klasörü. Kökteki dosyalar için boş.</summary>
        public string Group { get; set; } = string.Empty;

        /// <summary>Bu varyant klasöründe atlanan alt montaj sayısı.</summary>
        public int SubAssemblies { get; set; }

        /// <summary>Aynı klasörde hiçbir montajın kullanmadığı ama seçilmeyen diğer montajlar.</summary>
        public List<string> OtherTopLevel { get; set; } = new List<string>();

        /// <summary>Snapshot adı: "VaryantKlasörü__MontajAdı" (klasörsüzse sadece montaj adı).</summary>
        public string Label
        {
            get
            {
                var stem = System.IO.Path.GetFileNameWithoutExtension(Path);
                return Group.Length == 0 ? stem : Group + "__" + stem;
            }
        }
    }

    /// <summary>
    /// Varyant klasörlerindeki üst montajları bulur. Üst montaj = aynı varyant klasöründeki başka hiçbir
    /// montajın referans vermediği montaj. Referanslar dışarıdan verilir (SolidWorks "Referansları Bul"),
    /// böylece bu mantık SolidWorks olmadan test edilebilir.
    /// </summary>
    public static class TopAssemblyDetector
    {
        public static List<string> FindAssemblies(string root)
        {
            return Directory.GetFiles(root, "*.sldasm", SearchOption.AllDirectories)
                .Where(f => !System.IO.Path.GetFileName(f).StartsWith("~$", StringComparison.Ordinal)) // SolidWorks geçici dosyaları
                .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        /// <param name="directDependencies">Montaj yolu → doğrudan kullandığı dosyalar (ad veya tam yol).</param>
        /// <param name="totalDependencies">Bir klasörde birden çok aday varsa en büyüğünü seçmek için toplam referans sayısı.</param>
        public static List<VariantAssembly> Detect(string root, IReadOnlyList<string> assemblies,
            Func<string, IEnumerable<string>> directDependencies, Func<string, int>? totalDependencies = null)
        {
            root = System.IO.Path.GetFullPath(root);
            var all = assemblies.Select(System.IO.Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var referenced = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var asm in all)
            {
                var group = GroupOf(root, asm);
                foreach (var dep in directDependencies(asm) ?? Enumerable.Empty<string>())
                {
                    if (string.IsNullOrWhiteSpace(dep) || !dep.EndsWith(".sldasm", StringComparison.OrdinalIgnoreCase)) continue;
                    var depFull = SafeFullPath(dep);
                    var depName = FileName(dep);
                    foreach (var other in all)
                    {
                        if (string.Equals(other, asm, StringComparison.OrdinalIgnoreCase)) continue;
                        // Tam yol eşleşmesi ya da (yol eski/taşınmışsa) aynı varyant klasöründe aynı dosya adı.
                        bool samePath = depFull != null && string.Equals(depFull, other, StringComparison.OrdinalIgnoreCase);
                        bool sameNameInGroup = string.Equals(FileName(other), depName, StringComparison.OrdinalIgnoreCase) &&
                                               GroupOf(root, other) == group;
                        if (samePath || sameNameInGroup) referenced.Add(other);
                    }
                }
            }

            var result = new List<VariantAssembly>();
            foreach (var group in all.GroupBy(a => GroupOf(root, a)).OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase))
            {
                var tops = group.Where(a => !referenced.Contains(a)).ToList();
                int subs = group.Count() - tops.Count;
                if (tops.Count == 0) continue;

                if (group.Key.Length == 0)
                {
                    // Kök klasörde düz liste: her üst montaj ayrı bir varyanttır.
                    result.AddRange(tops.Select(t => new VariantAssembly { Path = t, Group = string.Empty }));
                    continue;
                }

                // Her varyant klasöründe tek üst montaj beklenir; birden çoksa en çok parça kullananı seç.
                var ordered = tops
                    .OrderByDescending(t => totalDependencies?.Invoke(t) ?? 0)
                    .ThenByDescending(t => SafeLength(t))
                    .ToList();
                result.Add(new VariantAssembly
                {
                    Path = ordered[0],
                    Group = group.Key,
                    SubAssemblies = subs,
                    OtherTopLevel = ordered.Skip(1).ToList(),
                });
            }
            return result;
        }

        /// <summary>Kök klasöre göre ilk alt klasör adı ("" = doğrudan kökte).</summary>
        public static string GroupOf(string root, string file)
        {
            var rel = file.Length > root.Length ? file.Substring(root.Length).TrimStart('\\', '/') : string.Empty;
            var i = rel.IndexOfAny(new[] { '\\', '/' });
            return i < 0 ? string.Empty : rel.Substring(0, i);
        }

        // Windows yolları Linux'ta (testlerde) da doğru ayrışsın diye iki ayırıcı da desteklenir.
        private static string FileName(string path)
        {
            var i = path.LastIndexOfAny(new[] { '\\', '/' });
            return i < 0 ? path : path.Substring(i + 1);
        }

        private static string? SafeFullPath(string path)
        {
            try
            {
                return System.IO.Path.IsPathRooted(path) ? System.IO.Path.GetFullPath(path) : null;
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static long SafeLength(string path)
        {
            try
            {
                return File.Exists(path) ? new FileInfo(path).Length : 0;
            }
            catch (Exception)
            {
                return 0;
            }
        }
    }

    public static class PathArguments
    {
        /// <summary>
        /// Boşluk içeren bir yol tırnak hatası yüzünden birkaç parçaya bölünmüşse (PowerShell'de sık olur)
        /// parçaları, var olan bir dosya/klasör oluşana kadar birleştirir. Fazladan tırnakları temizler.
        /// </summary>
        public static List<string> Resolve(IEnumerable<string> tokens, Func<string, bool> exists)
        {
            var list = tokens.Select(t => t.Trim().Trim('"', '\'').Trim()).Where(t => t.Length > 0).ToList();
            var result = new List<string>();
            int i = 0;
            while (i < list.Count)
            {
                int end = -1;
                var acc = list[i];
                if (exists(acc))
                {
                    end = i;
                }
                else
                {
                    for (int j = i + 1; j < list.Count; j++)
                    {
                        acc += " " + list[j];
                        if (exists(acc))
                        {
                            end = j;
                            break;
                        }
                    }
                }

                if (end < 0)
                {
                    result.Add(list[i]);
                    i++;
                }
                else
                {
                    result.Add(string.Join(" ", list.Skip(i).Take(end - i + 1)));
                    i = end + 1;
                }
            }
            return result;
        }
    }
}
