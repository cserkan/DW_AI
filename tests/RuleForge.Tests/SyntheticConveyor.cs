using System;
using System.Collections.Generic;
using RuleForge.Core.Expressions;
using RuleForge.Core.Model;
using RuleForge.Inference;

namespace RuleForge.Tests
{
    /// <summary>
    /// "Gizli" kurallarla üretilmiş sahte konveyör varyantları. Çıkarım motoru bu kuralları
    /// sadece snapshot'lara bakarak geri bulabilmeli.
    ///   Govde D1@Boss         = Boy - 40
    ///   Bant  D1@Sketch1      = 2 * Boy + 300
    ///   Rulo  D2@Sketch1      = Genislik + 50
    ///   Ayak deseni adet      = CEILING(Boy / 1500) + 1
    ///   MotorSol-1 bastırılmış = Motor &lt;&gt; "Sol"
    ///   OrtaDestek-1 bastırılmış = Boy &lt;= 3000   (varyantlarda 2800 ile 3400 arasında)
    ///   Aciklama              = "KONVEYOR " &amp; Boy &amp; "x" &amp; Genislik
    ///   Profil D1@Sketch1     = RANGELOOKUP(Genislik, 500, 40, 60)
    ///   Kapak D1@Sketch1      = gürültü (açıklanamaz)
    /// </summary>
    internal static class SyntheticConveyor
    {
        public static readonly (double boy, double gen, string motor)[] Specs =
        {
            (1500, 400, "Sol"), (2200, 500, "Sag"), (2800, 650, "Sol"), (3400, 800, "Sag"),
            (4100, 400, "Sag"), (4500, 1000, "Sol"), (5200, 650, "Sol"), (6000, 500, "Sag"),
            (7400, 800, "Sol"), (2000, 1000, "Sag"),
        };

        public static List<VariantSample> Samples(string? suffixPattern = null)
        {
            var list = new List<VariantSample>();
            int i = 0;
            foreach (var (boy, gen, motor) in Specs)
            {
                i++;
                var name = $"SP{i:000}";
                var sfx = suffixPattern == null ? "" : "_" + name;
                var snap = Build(name, boy, gen, motor, sfx, noise: 100 + (i * 37) % 23);
                list.Add(new VariantSample(name, snap, new Dictionary<string, Value>
                {
                    ["Boy"] = Value.Number(boy),
                    ["Genislik"] = Value.Number(gen),
                    ["Motor"] = Value.Text(motor),
                }));
            }
            return list;
        }

        public static ModelSnapshot Build(string name, double boy, double gen, string motor, string sfx = "", double noise = 100)
        {
            string F(string stem, string ext) => stem + sfx + ext;
            var root = new DocumentInfo
            {
                Key = F("Konveyor", ".SLDASM"), Kind = DocumentKind.Assembly,
                Dimensions =
                {
                    new DimensionInfo { Name = "D1@LocalLPattern1", Value = Math.Ceiling(boy / 1500) + 1, Unit = DimensionUnit.None },
                },
                CustomProperties = { ["Aciklama"] = $"KONVEYOR {Value.FormatNumber(boy)}x{Value.FormatNumber(gen)}", ["Firma"] = "ACME" },
            };
            var govde = new DocumentInfo
            {
                Key = F("Govde", ".SLDPRT"), Kind = DocumentKind.Part,
                Dimensions =
                {
                    new DimensionInfo { Name = "D1@Boss", Value = boy - 40, Unit = DimensionUnit.Millimeter },
                    new DimensionInfo { Name = "D2@Boss", Value = 120, Unit = DimensionUnit.Millimeter },
                    new DimensionInfo { Name = "D3@Sketch2", Value = (boy - 40) / 2, Unit = DimensionUnit.Millimeter, IsDriven = true },
                },
                Features = { new FeatureInfo { Name = "Boss", Type = "Extrusion" } },
            };
            var bant = new DocumentInfo
            {
                Key = F("Bant", ".SLDPRT"), Kind = DocumentKind.Part,
                Dimensions = { new DimensionInfo { Name = "D1@Sketch1", Value = 2 * boy + 300, Unit = DimensionUnit.Millimeter } },
            };
            var rulo = new DocumentInfo
            {
                Key = F("Rulo", ".SLDPRT"), Kind = DocumentKind.Part,
                Dimensions = { new DimensionInfo { Name = "D2@Sketch1", Value = gen + 50, Unit = DimensionUnit.Millimeter } },
            };
            var profil = new DocumentInfo
            {
                Key = F("Profil", ".SLDPRT"), Kind = DocumentKind.Part,
                Dimensions = { new DimensionInfo { Name = "D1@Sketch1", Value = gen <= 500 ? 40 : 60, Unit = DimensionUnit.Millimeter } },
            };
            var kapak = new DocumentInfo
            {
                Key = F("Kapak", ".SLDPRT"), Kind = DocumentKind.Part,
                Dimensions = { new DimensionInfo { Name = "D1@Sketch1", Value = noise, Unit = DimensionUnit.Millimeter } },
            };

            return new ModelSnapshot
            {
                Name = name,
                RootDocument = root.Key,
                Documents = { root, govde, bant, rulo, profil, kapak },
                Components =
                {
                    Comp(F("Govde", "") + "-1", govde.Key),
                    Comp(F("Bant", "") + "-1", bant.Key),
                    Comp(F("Rulo", "") + "-1", rulo.Key),
                    Comp(F("Rulo", "") + "-2", rulo.Key),
                    Comp(F("Profil", "") + "-1", profil.Key),
                    Comp(F("Kapak", "") + "-1", kapak.Key),
                    Comp(F("MotorSol", "") + "-1", "MotorSol.SLDPRT", motor != "Sol"),
                    Comp(F("OrtaDestek", "") + "-1", "OrtaDestek.SLDPRT", boy <= 3000),
                },
            };
        }

        private static ComponentInfo Comp(string path, string doc, bool suppressed = false) => new ComponentInfo
        {
            Path = path, Name = path, DocumentKey = doc, Suppressed = suppressed, Configuration = suppressed ? "" : "Default",
        };
    }
}
