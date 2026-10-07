using System;
using System.Collections.Generic;
using RuleForge.Core.Expressions;
using RuleForge.Core.Model;

namespace RuleForge.Inference
{
    /// <summary>Bir varyant: girdi değerleri (ör. DriveWorks spesifikasyonu) + üretilmiş modelin snapshot'ı.</summary>
    public sealed class VariantSample
    {
        public VariantSample(string name, ModelSnapshot snapshot, IDictionary<string, Value>? inputs = null)
        {
            Name = name;
            Snapshot = snapshot;
            Inputs = new Dictionary<string, Value>(StringComparer.OrdinalIgnoreCase);
            if (inputs != null)
                foreach (var kv in inputs)
                    Inputs[kv.Key] = kv.Value;
        }

        public string Name { get; }
        public ModelSnapshot Snapshot { get; }
        public Dictionary<string, Value> Inputs { get; }
    }

    public sealed class InferenceOptions
    {
        /// <summary>
        /// Dosya adlarını varyantlar arasında eşleştirmek için düzenli ifade. İlk grup "temel ad" olur.
        /// Ör. DriveWorks "Govde_SP0012.SLDPRT" üretiyorsa: <c>^(.*?)_SP\d+$</c>
        /// </summary>
        public string? NamePattern { get; set; }

        /// <summary>Sayısal eşleşme toleransı (mm).</summary>
        public double Tolerance { get; set; } = 0.005;

        /// <summary>Girdi tablosu yoksa: modeldeki bir gözlemi girdi olarak kullan. Girdi adı → gözlem anahtarı.</summary>
        public Dictionary<string, string> InputObservations { get; set; } =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        public bool IncludeCustomProperties { get; set; } = true;
        public bool IncludeFeatureSuppression { get; set; } = true;
    }
}
