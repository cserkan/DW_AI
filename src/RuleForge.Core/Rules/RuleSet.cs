using System;
using System.Collections.Generic;
using System.Linq;

namespace RuleForge.Core.Rules
{
    /// <summary>
    /// Bir ürünün tüm kuralları. Yapay zekâ bu yapıyı üretir/günceller; kural motoru
    /// bunu deterministik olarak çalıştırır. JSON olarak saklanır ve sürüm kontrolüne girer.
    /// </summary>
    public sealed class RuleSet
    {
        public int SchemaVersion { get; set; } = 1;
        public string Name { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;

        /// <summary>Ana (master) montaj dosyası, kural dosyasına göre göreli yol olabilir.</summary>
        public string MasterAssembly { get; set; } = string.Empty;

        public List<InputDefinition> Inputs { get; set; } = new List<InputDefinition>();
        public List<VariableDefinition> Variables { get; set; } = new List<VariableDefinition>();
        public List<Rule> Rules { get; set; } = new List<Rule>();

        public InputDefinition? FindInput(string name) =>
            Inputs.FirstOrDefault(i => string.Equals(i.Name, name, StringComparison.OrdinalIgnoreCase));

        public VariableDefinition? FindVariable(string name) =>
            Variables.FirstOrDefault(v => string.Equals(v.Name, name, StringComparison.OrdinalIgnoreCase));

        public Rule? FindRule(string id) =>
            Rules.FirstOrDefault(r => string.Equals(r.Id, id, StringComparison.OrdinalIgnoreCase));
    }

    public enum InputType
    {
        Number,
        Text,
        Bool,
        Choice,
    }

    public sealed class InputDefinition
    {
        /// <summary>Formüllerde kullanılan ad (boşluksuz), ör. "Genislik".</summary>
        public string Name { get; set; } = string.Empty;

        /// <summary>Formda görünen etiket, ör. "Genişlik (mm)".</summary>
        public string Label { get; set; } = string.Empty;

        public InputType Type { get; set; } = InputType.Number;
        public string? Unit { get; set; }
        public double? Min { get; set; }
        public double? Max { get; set; }
        public double? Step { get; set; }

        /// <summary>Varsayılan değer metin olarak ("1200", "true", "Galvaniz").</summary>
        public string? Default { get; set; }

        public List<string> Options { get; set; } = new List<string>();
        public string? Description { get; set; }
    }

    /// <summary>Ara hesap. Diğer formüller adıyla kullanabilir.</summary>
    public sealed class VariableDefinition
    {
        public string Name { get; set; } = string.Empty;
        public string Expression { get; set; } = string.Empty;
        public string? Description { get; set; }
    }

    public enum RuleStatus
    {
        /// <summary>Yapay zekâ veya çıkarım önerdi; henüz onaylanmadı.</summary>
        Proposed,
        Approved,
        Rejected,
    }

    public enum RuleSource
    {
        Manual,
        Chat,
        Inference,
        ModelEquation,
    }

    public sealed class Rule
    {
        public string Id { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public RuleTarget Target { get; set; } = new RuleTarget();

        /// <summary>Hedefe yazılacak değeri hesaplayan formül.</summary>
        public string Expression { get; set; } = string.Empty;

        /// <summary>İsteğe bağlı: yanlışsa kural uygulanmaz (model değeri olduğu gibi kalır).</summary>
        public string? Condition { get; set; }

        public RuleStatus Status { get; set; } = RuleStatus.Proposed;
        public RuleSource Source { get; set; } = RuleSource.Manual;

        /// <summary>0..1 arası güven (çıkarım/YZ için).</summary>
        public double? Confidence { get; set; }

        /// <summary>Kuralın dayanağı: "12/12 varyantta tam uyum", "Kullanıcı: kapak 10 mm büyük olsun" vb.</summary>
        public string? Evidence { get; set; }
    }

    public enum TargetKind
    {
        /// <summary>Ölçü değeri (mm/derece/adet). Document + Name ("D1@Sketch1").</summary>
        Dimension,

        /// <summary>Denklem yöneticisindeki global değişken. Document + Name.</summary>
        GlobalVariable,

        /// <summary>Özellik bastırma (true = bastırılmış). Document + Name (özellik adı).</summary>
        FeatureSuppression,

        /// <summary>Bileşen bastırma (true = bastırılmış). Component (ağaç yolu).</summary>
        ComponentSuppression,

        /// <summary>Bileşeni başka dosyayla değiştir. Component + değer = dosya yolu.</summary>
        ComponentReplace,

        /// <summary>Konfigürasyon seçimi. Component verilirse bileşenin referans konfigürasyonu,
        /// yoksa Document'ın aktif konfigürasyonu.</summary>
        Configuration,

        /// <summary>Özel özellik (metin). Document + Name. Document boşsa ana montaj.</summary>
        CustomProperty,

        /// <summary>Üretilen ana montajın dosya adı (uzantısız).</summary>
        OutputFileName,
    }

    public sealed class RuleTarget
    {
        public TargetKind Kind { get; set; }

        /// <summary>Dosya anahtarı (ör. "Govde.SLDPRT"). Boşsa ana montaj.</summary>
        public string? Document { get; set; }

        /// <summary>Ölçü/özellik/değişken/özel özellik adı.</summary>
        public string? Name { get; set; }

        /// <summary>Bileşen ağaç yolu, ör. "Ayak-3" veya "AltMontaj-1/Ayak-2".</summary>
        public string? Component { get; set; }

        public string Key
        {
            get
            {
                switch (Kind)
                {
                    case TargetKind.ComponentSuppression:
                    case TargetKind.ComponentReplace:
                        return $"{Kind}:{Component}".ToLowerInvariant();
                    case TargetKind.Configuration:
                        return $"{Kind}:{Component ?? Document}".ToLowerInvariant();
                    case TargetKind.OutputFileName:
                        return Kind.ToString().ToLowerInvariant();
                    default:
                        return $"{Kind}:{Document}:{Name}".ToLowerInvariant();
                }
            }
        }

        public override string ToString()
        {
            switch (Kind)
            {
                case TargetKind.ComponentSuppression:
                case TargetKind.ComponentReplace:
                    return $"{Kind} [{Component}]";
                case TargetKind.Configuration:
                    return Component != null ? $"{Kind} [{Component}]" : $"{Kind} [{Document}]";
                case TargetKind.OutputFileName:
                    return Kind.ToString();
                default:
                    return $"{Kind} [{(string.IsNullOrEmpty(Document) ? "<ana montaj>" : Document)} :: {Name}]";
            }
        }

        /// <summary>Hedefin beklediği değer tipi.</summary>
        public ExpectedType ExpectedType
        {
            get
            {
                switch (Kind)
                {
                    case TargetKind.Dimension:
                    case TargetKind.GlobalVariable:
                        return ExpectedType.Number;
                    case TargetKind.FeatureSuppression:
                    case TargetKind.ComponentSuppression:
                        return ExpectedType.Bool;
                    default:
                        return ExpectedType.Text;
                }
            }
        }
    }

    public enum ExpectedType
    {
        Number,
        Bool,
        Text,
    }
}
