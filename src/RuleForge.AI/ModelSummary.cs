using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using RuleForge.Core.Expressions;
using RuleForge.Core.Model;

namespace RuleForge.AI
{
    /// <summary>
    /// Snapshot'ı yapay zekânın okuyabileceği kompakt metne çevirir.
    /// Kural hedefleri SADECE buradaki adlardan seçilebilir; YZ'nin ad uydurmasını engellemek için
    /// kesin adlar (dosya anahtarı, ölçü adı, bileşen yolu) birebir yazılır.
    /// </summary>
    public static class ModelSummary
    {
        private static readonly HashSet<string> NoisyFeatureTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "ProfileFeature", "3DProfileFeature", "OriginProfileFeature", "RefPlane", "RefAxis", "RefPoint",
            "MateGroup", "Attribute", "CoordSys",
        };

        public static string Build(ModelSnapshot snapshot, int maxDimensionsPerDocument = 400)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"Ana montaj: {snapshot.RootDocument}");
            if (!string.IsNullOrEmpty(snapshot.SolidWorksVersion)) sb.AppendLine($"SolidWorks: {snapshot.SolidWorksVersion}");
            sb.AppendLine("Birimler: uzunluk mm, açı derece. 'driven' = referans ölçü, değiştirilemez.");
            sb.AppendLine();

            var instanceCounts = snapshot.Components
                .GroupBy(c => c.DocumentKey, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);

            foreach (var doc in snapshot.Documents.OrderBy(d => d.Key == snapshot.RootDocument ? 0 : 1).ThenBy(d => d.Key))
            {
                instanceCounts.TryGetValue(doc.Key, out var count);
                sb.Append($"## {doc.Key} ({doc.Kind}");
                if (count > 0) sb.Append($", {count} örnek");
                sb.AppendLine(")");
                if (doc.Configurations.Count > 1)
                    sb.AppendLine($"  Konfigürasyonlar: {string.Join(", ", doc.Configurations)} (aktif: {doc.ActiveConfiguration})");

                var globals = doc.Equations.Where(e => e.IsGlobalVariable).ToList();
                if (globals.Count > 0)
                    sb.AppendLine("  Global değişkenler: " + string.Join("; ", globals.Select(g => $"\"{g.Name}\" = {Fmt(g.Value)}")));
                var eqs = doc.Equations.Where(e => !e.IsGlobalVariable).ToList();
                if (eqs.Count > 0)
                    sb.AppendLine("  Denklemler: " + string.Join("; ", eqs.Select(e => e.Text)));

                if (doc.Dimensions.Count > 0)
                {
                    sb.AppendLine("  Ölçüler:");
                    foreach (var d in doc.Dimensions.Take(maxDimensionsPerDocument))
                    {
                        var unit = d.Unit == DimensionUnit.Millimeter ? " mm" : d.Unit == DimensionUnit.Degree ? "°" : "";
                        var flags = d.IsDriven ? " [driven]" : d.DrivenByEquation != null ? $" [denklem: {d.DrivenByEquation}]" : "";
                        sb.AppendLine($"    {d.Name} = {Value.FormatNumber(d.Value)}{unit}{flags}");
                    }
                    if (doc.Dimensions.Count > maxDimensionsPerDocument)
                        sb.AppendLine($"    ... ve {doc.Dimensions.Count - maxDimensionsPerDocument} ölçü daha (kısaltıldı)");
                }

                var features = doc.Features.Where(f => !NoisyFeatureTypes.Contains(f.Type)).ToList();
                if (features.Count > 0)
                    sb.AppendLine("  Özellikler: " + string.Join(", ", features.Select(f => $"{f.Name} ({f.Type}{(f.Suppressed ? ", bastırılmış" : "")})")));

                if (doc.CustomProperties.Count > 0)
                    sb.AppendLine("  Özel özellikler: " + string.Join("; ", doc.CustomProperties.Select(kv => $"{kv.Key} = \"{kv.Value}\"")));
                if (doc.ConfigurationProperties.Count > 0)
                    sb.AppendLine("  Konfigürasyon özellikleri: " + string.Join("; ", doc.ConfigurationProperties.Select(kv => $"{kv.Key} = \"{kv.Value}\"")));
                sb.AppendLine();
            }

            if (snapshot.Components.Count > 0)
            {
                sb.AppendLine("## Bileşen ağacı (yol → dosya [konfigürasyon])");
                foreach (var c in snapshot.Components)
                {
                    var depth = c.Path.Count(ch => ch == '/');
                    sb.Append(new string(' ', 2 + depth * 2)).Append(c.Path).Append(" → ").Append(c.DocumentKey);
                    if (!string.IsNullOrEmpty(c.Configuration)) sb.Append($" [{c.Configuration}]");
                    if (c.Suppressed) sb.Append(" (bastırılmış)");
                    if (c.IsPatternInstance) sb.Append(" (desen örneği)");
                    sb.AppendLine();
                }
                sb.AppendLine();
            }

            if (snapshot.Mates.Count > 0)
            {
                sb.AppendLine("## Mate'ler");
                foreach (var m in snapshot.Mates)
                    sb.AppendLine($"  {m.Name} ({m.Type}{(m.Suppressed ? ", bastırılmış" : "")}): {string.Join(" ↔ ", m.Components)}");
            }
            return sb.ToString();
        }

        private static string Fmt(double? d) => d.HasValue ? Value.FormatNumber(d.Value) : "?";
    }
}
