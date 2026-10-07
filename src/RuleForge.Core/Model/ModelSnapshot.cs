using System;
using System.Collections.Generic;
using System.Linq;

namespace RuleForge.Core.Model
{
    public enum DocumentKind
    {
        Unknown,
        Part,
        Assembly,
        Drawing,
    }

    /// <summary>
    /// Bir SolidWorks montajının (ve alt parçalarının) kural çıkarımı için gereken
    /// "fotoğrafı". SolidWorks'e bağımlı değildir; JSON olarak saklanır ve
    /// Linux/CI ortamında da işlenebilir.
    /// Birimler: uzunluk mm, açı derece.
    /// </summary>
    public sealed class ModelSnapshot
    {
        public int SchemaVersion { get; set; } = 1;

        /// <summary>Varyant adı (ör. DriveWorks spesifikasyon adı). Çıkarımda girdi tablosuyla eşleştirilir.</summary>
        public string Name { get; set; } = string.Empty;

        public string SourcePath { get; set; } = string.Empty;
        public string RootDocument { get; set; } = string.Empty;
        public string? SolidWorksVersion { get; set; }
        public DateTime ExtractedAtUtc { get; set; } = DateTime.UtcNow;

        public List<DocumentInfo> Documents { get; set; } = new List<DocumentInfo>();
        public List<ComponentInfo> Components { get; set; } = new List<ComponentInfo>();
        public List<MateInfo> Mates { get; set; } = new List<MateInfo>();

        public DocumentInfo? FindDocument(string key)
        {
            return Documents.FirstOrDefault(d => string.Equals(d.Key, key, StringComparison.OrdinalIgnoreCase));
        }

        public ComponentInfo? FindComponent(string path)
        {
            return Components.FirstOrDefault(c => string.Equals(c.Path, path, StringComparison.OrdinalIgnoreCase));
        }

        public DocumentInfo? Root => FindDocument(RootDocument);
    }

    public sealed class DocumentInfo
    {
        /// <summary>Benzersiz anahtar: dosya adı (ör. "Govde.SLDPRT").</summary>
        public string Key { get; set; } = string.Empty;

        public string Path { get; set; } = string.Empty;
        public DocumentKind Kind { get; set; }
        public string ActiveConfiguration { get; set; } = string.Empty;
        public List<string> Configurations { get; set; } = new List<string>();
        public List<DimensionInfo> Dimensions { get; set; } = new List<DimensionInfo>();
        public List<FeatureInfo> Features { get; set; } = new List<FeatureInfo>();
        public List<EquationInfo> Equations { get; set; } = new List<EquationInfo>();

        /// <summary>Dosya düzeyindeki özel özellikler (çözümlenmiş değerler).</summary>
        public Dictionary<string, string> CustomProperties { get; set; } =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>Aktif konfigürasyona özel özellikler.</summary>
        public Dictionary<string, string> ConfigurationProperties { get; set; } =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        public DimensionInfo? FindDimension(string name)
        {
            return Dimensions.FirstOrDefault(d => string.Equals(d.Name, name, StringComparison.OrdinalIgnoreCase));
        }

        public FeatureInfo? FindFeature(string name)
        {
            return Features.FirstOrDefault(f => string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase));
        }

        public EquationInfo? FindGlobalVariable(string name)
        {
            return Equations.FirstOrDefault(e => e.IsGlobalVariable &&
                                                 string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase));
        }
    }

    public enum DimensionUnit
    {
        None,
        Millimeter,
        Degree,
    }

    public sealed class DimensionInfo
    {
        /// <summary>SolidWorks parametre adı, ör. "D1@Sketch1" veya "Boy@Taban".</summary>
        public string Name { get; set; } = string.Empty;

        public string Feature { get; set; } = string.Empty;
        public double Value { get; set; }
        public DimensionUnit Unit { get; set; }

        /// <summary>Referans (driven) ölçüler değiştirilemez, sadece okunur.</summary>
        public bool IsDriven { get; set; }

        /// <summary>Ölçü bir denklemle sürülüyorsa denklem metni.</summary>
        public string? DrivenByEquation { get; set; }
    }

    public sealed class FeatureInfo
    {
        public string Name { get; set; } = string.Empty;
        public string Type { get; set; } = string.Empty;
        public bool Suppressed { get; set; }
    }

    public sealed class EquationInfo
    {
        /// <summary>Tam denklem metni, ör. "\"Boy\" = \"Genislik\" * 2".</summary>
        public string Text { get; set; } = string.Empty;

        /// <summary>Sol taraf (global değişken veya ölçü adı, tırnaksız).</summary>
        public string Name { get; set; } = string.Empty;

        public bool IsGlobalVariable { get; set; }
        public double? Value { get; set; }
    }

    public sealed class ComponentInfo
    {
        /// <summary>Ağaçtaki tam yol, ör. "AltMontaj-1/Ayak-2".</summary>
        public string Path { get; set; } = string.Empty;

        public string Name { get; set; } = string.Empty;
        public string? ParentPath { get; set; }

        /// <summary>DocumentInfo.Key; bastırılmış bileşenlerde de dosya adı tutulur.</summary>
        public string DocumentKey { get; set; } = string.Empty;

        public string Configuration { get; set; } = string.Empty;
        public bool Suppressed { get; set; }
        public bool ExcludedFromBom { get; set; }
        public bool IsPatternInstance { get; set; }
    }

    public sealed class MateInfo
    {
        public string Name { get; set; } = string.Empty;
        public string Type { get; set; } = string.Empty;
        public bool Suppressed { get; set; }
        public List<string> Components { get; set; } = new List<string>();
    }
}
