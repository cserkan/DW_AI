using System;
using System.Collections.Generic;
using System.Linq;
using RuleForge.Core.Engine;
using RuleForge.Core.Expressions;
using RuleForge.Core.Model;
using RuleForge.Core.Rules;
using RuleForge.Inference;
using Xunit;

namespace RuleForge.Tests
{
    /// <summary>Tekrarlanan modüller (tablo girdileri): motor her satırı ayrı çalıştırır, çıkarım kopyaları ayrı örnek sayar.</summary>
    public class ModuleTests
    {
        private static RuleSet LineRules() => new RuleSet
        {
            Inputs = { new InputDefinition { Name = "Derinlik", Type = InputType.Number, Default = "400" } },
            Tables =
            {
                new TableDefinition
                {
                    Name = "Bolum", Module = "Raf.SLDASM", MinRows = 1, MaxRows = 6,
                    Columns = { new InputDefinition { Name = "Uzunluk", Type = InputType.Number, Min = 300, Max = 3000, Default = "1000" } },
                },
            },
            Variables = { new VariableDefinition { Name = "Delik", Scope = "Bolum", Expression = "FLOOR(Uzunluk / 300) + 1" } },
            Rules =
            {
                Rule("ust", TargetKind.Dimension, "Ust.SLDPRT", "D1@Derinlik", "Derinlik + 30", null),
                Rule("yan", TargetKind.Dimension, "Yan.SLDPRT", "D1@Boy", "Uzunluk - 20", "Bolum"),
                Rule("delik", TargetKind.Dimension, "Yan.SLDPRT", "D2@Delik", "Delik", "Bolum"),
                Rule("sira", TargetKind.CustomProperty, "Yan.SLDPRT", "Sira", "\"B\" & Bolum_Sira & \"/\" & Bolum_Adet", "Bolum"),
                Rule("aciklama", TargetKind.CustomProperty, null, "Aciklama", "\"HAT \" & Uzunluk_Toplam & \" ilk \" & Uzunluk_Ilk", null),
            },
        };

        private static Rule Rule(string id, TargetKind kind, string? doc, string name, string expr, string? scope) => new Rule
        {
            Id = id, Target = new RuleTarget { Kind = kind, Document = doc, Name = name }, Expression = expr, Scope = scope,
            Status = RuleStatus.Approved,
        };

        private static Dictionary<string, List<Dictionary<string, Value>>> Rows(params double[] lengths) =>
            new Dictionary<string, List<Dictionary<string, Value>>>(StringComparer.OrdinalIgnoreCase)
            {
                ["Bolum"] = lengths.Select(l => new Dictionary<string, Value>(StringComparer.OrdinalIgnoreCase) { ["Uzunluk"] = Value.Number(l) }).ToList(),
            };

        [Fact]
        public void ScopedRulesRunOncePerRow()
        {
            var result = RuleEngine.Evaluate(LineRules(), new Dictionary<string, Value> { ["Derinlik"] = Value.Number(500) }, null, Rows(1200, 650, 900));
            Assert.True(result.Success, string.Join("; ", result.Errors));

            Assert.Equal(530, result.Actions.Single(a => a.Rule.Id == "ust").Value.AsNumber());
            Assert.Null(result.Actions.Single(a => a.Rule.Id == "ust").Instance);

            var yan = result.Actions.Where(a => a.Rule.Id == "yan").OrderBy(a => a.Instance).Select(a => a.Value.AsNumber()).ToList();
            Assert.Equal(new[] { 1180.0, 630, 880 }, yan);
            var delik = result.Actions.Where(a => a.Rule.Id == "delik").OrderBy(a => a.Instance).Select(a => a.Value.AsNumber()).ToList();
            Assert.Equal(new[] { 5.0, 3, 4 }, delik);
            Assert.Equal("B2/3", result.Actions.Single(a => a.Rule.Id == "sira" && a.Instance == 2).Value.AsText());
            Assert.Equal("HAT 2750 ilk 1200", result.Actions.Single(a => a.Rule.Id == "aciklama").Value.AsText());
            Assert.Equal("customproperty:yan.sldprt:sira#2", result.Actions.Single(a => a.Rule.Id == "sira" && a.Instance == 2).Key);
        }

        [Fact]
        public void TableLimitsAndColumnsAreChecked()
        {
            var tooMany = RuleEngine.Evaluate(LineRules(), null, null, Rows(500, 500, 500, 500, 500, 500, 500));
            Assert.Contains(tooMany.Errors, e => e.Contains("en fazla 6"));

            var tooLong = RuleEngine.Evaluate(LineRules(), null, null, Rows(500, 5000));
            Assert.Contains(tooLong.Errors, e => e.Contains("satır 2") && e.Contains("3000"));

            var none = RuleEngine.Evaluate(LineRules(), null, null, null);
            Assert.Contains(none.Errors, e => e.Contains("en az 1"));
        }

        [Fact]
        public void ParsesTableAssignments()
        {
            var inputs = RuleEngine.ParseAssignments(new[] { "Derinlik=450", "Bolum.Uzunluk=1200;800;600" }, out var tables);
            Assert.Equal(450, inputs["Derinlik"].AsNumber());
            Assert.Equal(3, tables["Bolum"].Count);
            Assert.Equal(800, tables["Bolum"][1]["Uzunluk"].AsNumber());
            Assert.Throws<FormatException>(() => RuleEngine.ParseAssignments(new[] { "T.A=1;2", "T.B=1;2;3" }, out _));
        }

        [Fact]
        public void ValidatorKnowsWhichNamesEachScopeSees()
        {
            var ok = RuleSetValidator.Validate(LineRules());
            Assert.DoesNotContain(ok, i => i.Severity == IssueSeverity.Error);

            var bad = LineRules();
            bad.Rules.Add(Rule("kotu", TargetKind.Dimension, "Ust.SLDPRT", "D2@Kotu", "Uzunluk + 1", null)); // satır sütunu kapsamsız kuralda
            bad.Rules.Add(Rule("yok", TargetKind.Dimension, "Ust.SLDPRT", "D3@Yok", "1", "Olmayan"));
            var issues = RuleSetValidator.Validate(bad);
            Assert.Contains(issues, i => i.Item == "kotu" && i.Message.Contains("Uzunluk"));
            Assert.Contains(issues, i => i.Item == "yok" && i.Message.Contains("Olmayan"));
        }

        // ---------- çıkarım: hat = N adet "Raf" kopyası; uzunluk her kopyada farklı, derinlik hepsinde aynı ----------

        private static readonly (double derinlik, double[] uzunluklar)[] Lines =
        {
            (400, new double[] { 1200, 600, 900 }), (450, new double[] { 2100, 1500 }), (500, new double[] { 700, 1300, 2500, 800 }),
            (380, new double[] { 1000, 1000 }), (620, new double[] { 2900, 400, 1650 }), (550, new double[] { 1800, 2200 }),
            (420, new double[] { 500, 750, 1100, 1400, 2600 }), (600, new double[] { 950, 2050, 1250 }),
        };

        /// <summary>
        /// Gizli kurallar (kopya başına): Yan D1@Boy = Uzunluk, D2@Delik = FLOOR(Uzunluk / 300) + 1, D3@Orta = Uzunluk / 2;
        /// Ust D1@Derinlik = Derinlik, D2@Kenar = Derinlik + 30; Raf "Aciklama" = "RAF " &amp; Derinlik &amp; " / " &amp; ilk bölümün uzunluğu.
        /// </summary>
        internal static List<VariantSample> LineSamples()
        {
            var list = new List<VariantSample>();
            for (int v = 0; v < Lines.Length; v++)
            {
                var (derinlik, uzunluklar) = Lines[v];
                var spec = $"{v + 1:0000}";
                var snap = new ModelSnapshot { Name = "HAT " + spec, RootDocument = $"HAT {spec}.SLDASM" };
                snap.Documents.Add(new DocumentInfo { Key = snap.RootDocument, Kind = DocumentKind.Assembly, Features = { new FeatureInfo { Name = "Mates", Type = "MateGroup" } } });
                var ust = $"Ust UST-{spec}.SLDPRT"; // tüm bölümlerde ortak parça
                snap.Documents.Add(new DocumentInfo
                {
                    Key = ust, Kind = DocumentKind.Part,
                    Features = { new FeatureInfo { Name = "Plaka", Type = "Extrusion" } },
                    Dimensions =
                    {
                        new DimensionInfo { Name = "D1@Derinlik", Value = derinlik },
                        new DimensionInfo { Name = "D2@Kenar", Value = derinlik + 30 },
                    },
                });
                for (int k = 0; k < uzunluklar.Length; k++)
                {
                    var l = uzunluklar[k];
                    var raf = $"Raf -{k + 1}-{spec}.SLDASM";
                    var yan = $"Yan YAN-{k + 1}-{spec}.SLDPRT";
                    snap.Documents.Add(new DocumentInfo
                    {
                        Key = raf, Kind = DocumentKind.Assembly,
                        Features = { new FeatureInfo { Name = "Mates", Type = "MateGroup" } },
                        CustomProperties = { ["Aciklama"] = $"RAF {Value.FormatNumber(derinlik)} / {Value.FormatNumber(uzunluklar[0])}" },
                    });
                    snap.Documents.Add(new DocumentInfo
                    {
                        Key = yan, Kind = DocumentKind.Part,
                        Features = { new FeatureInfo { Name = "Govde", Type = "Extrusion" }, new FeatureInfo { Name = "Delik", Type = "LPattern" } },
                        Dimensions =
                        {
                            new DimensionInfo { Name = "D1@Boy", Value = l },
                            new DimensionInfo { Name = "D2@Delik", Value = Math.Floor(l / 300) + 1 },
                            new DimensionInfo { Name = "D3@Orta", Value = l / 2 },
                        },
                    });
                    // Ağaçta sıra karışık olsa da (DriveWorks böyle kaydedebiliyor) satır numarası dosya adından gelir.
                    var path = $"Raf -{k + 1}-{spec}-{k + 5}";
                    snap.Components.Insert(0, new ComponentInfo { Path = path, Name = path, DocumentKey = raf, Configuration = "Default" });
                    snap.Components.Add(new ComponentInfo { Path = path + "/Yan YAN-1", Name = "Yan YAN-1", ParentPath = path, DocumentKey = yan, Configuration = "Default" });
                    snap.Components.Add(new ComponentInfo { Path = path + "/Ust UST-1", Name = "Ust UST-1", ParentPath = path, DocumentKey = ust, Configuration = "Default" });
                }
                list.Add(new VariantSample(snap.Name, snap));
            }
            return list;
        }

        internal static ModelSnapshot LineMaster()
        {
            var m = new ModelSnapshot { Name = "Raf", RootDocument = "Raf.SLDASM" };
            m.Documents.Add(new DocumentInfo { Key = "Raf.SLDASM", Kind = DocumentKind.Assembly, Features = { new FeatureInfo { Name = "Mates", Type = "MateGroup" } }, CustomProperties = { ["Aciklama"] = "" } });
            m.Documents.Add(new DocumentInfo
            {
                Key = "Yan.SLDPRT", Kind = DocumentKind.Part,
                Features = { new FeatureInfo { Name = "Govde", Type = "Extrusion" }, new FeatureInfo { Name = "Delik", Type = "LPattern" } },
                Dimensions = { new DimensionInfo { Name = "D1@Boy", Value = 1000 }, new DimensionInfo { Name = "D2@Delik", Value = 4 }, new DimensionInfo { Name = "D3@Orta", Value = 500 } },
            });
            m.Documents.Add(new DocumentInfo
            {
                Key = "Ust.SLDPRT", Kind = DocumentKind.Part,
                Features = { new FeatureInfo { Name = "Plaka", Type = "Extrusion" } },
                Dimensions = { new DimensionInfo { Name = "D1@Derinlik", Value = 400 }, new DimensionInfo { Name = "D2@Kenar", Value = 430 } },
            });
            m.Components.Add(new ComponentInfo { Path = "Yan-1", Name = "Yan-1", DocumentKey = "Yan.SLDPRT", Configuration = "Default" });
            m.Components.Add(new ComponentInfo { Path = "Ust-1", Name = "Ust-1", DocumentKey = "Ust.SLDPRT", Configuration = "Default" });
            return m;
        }

        [Fact]
        public void DetectsRepeatedModuleAndOrdersCopiesByFileName()
        {
            var split = ModuleDetector.Detect(LineSamples(), LineMaster());
            Assert.NotNull(split);
            Assert.Equal("Raf.SLDASM", split!.Document);
            Assert.Null(split.ComponentPath); // master'ın kendisi; varyant kökü ayrı hat montajı
            Assert.Equal(Lines.Sum(l => l.uzunluklar.Length), split.Instances.Count);
            // Ağaçta ters sırada olsalar da satır 1 = "-1-" dosyası
            var first = split.InstancesOf("HAT 0001").First();
            Assert.Contains("-1-", first.DocumentKey);
            Assert.Null(ModuleDetector.Detect(SyntheticConveyor.Samples(), null)); // tekrarsız modelde modül yok
        }

        [Fact]
        public void InfersRowInputsGlobalInputsAndScopedRules()
        {
            var report = RuleInferencer.Infer(LineSamples(), new InferenceOptions(), LineMaster());
            var table = Assert.Single(report.Tables);
            Assert.Equal("Raf", table.Name);
            var column = Assert.Single(table.Columns);
            Assert.Equal(2, table.MinRows);
            Assert.Equal(5, table.MaxRows);

            Rule Find(string doc, string name) => report.Rules.Single(r => r.Target.Document == doc && r.Target.Name == name);
            Assert.Equal("Raf", Find("Yan.SLDPRT", "D2@Delik").Scope);
            Assert.Equal($"FLOOR({column.Name} / 300) + 1", Find("Yan.SLDPRT", "D2@Delik").Expression);
            Assert.Equal($"{column.Name} / 2", Find("Yan.SLDPRT", "D3@Orta").Expression);

            // Derinlik her bölümde aynı: genel girdi; ortak parça kuralı yine her kopya için yazılır.
            var depth = Assert.Single(report.Inputs);
            Assert.Equal($"{depth.Name} + 30", Find("Ust.SLDPRT", "D2@Kenar").Expression);

            // Açıklama ilk bölümün uzunluğunu kullanıyor: tablo özeti (_Ilk).
            var desc = report.Rules.Single(r => r.Target.Kind == TargetKind.CustomProperty && r.Target.Name == "Aciklama");
            Assert.Equal($"\"RAF \" & {depth.Name} & \" / \" & {column.Name}_Ilk", desc.Expression);
            Assert.Empty(report.Unexplained);

            // Kural seti doğrulanır ve yeni bir hat için çalışır.
            var set = report.ToRuleSet("hat", "");
            Assert.DoesNotContain(RuleSetValidator.Validate(set, LineMaster()), i => i.Severity == IssueSeverity.Error);
            var tables = new Dictionary<string, List<Dictionary<string, Value>>>
            {
                [table.Name] = new[] { 1600.0, 800 }.Select(l => new Dictionary<string, Value> { [column.Name] = Value.Number(l) }).ToList(),
            };
            var result = RuleEngine.Evaluate(set, new Dictionary<string, Value> { [depth.Name] = Value.Number(470) },
                new EvaluationOptions { IncludeProposed = true }, tables);
            Assert.True(result.Success, string.Join("; ", result.Errors));
            Assert.Equal(6, result.Actions.Single(a => a.Target.Name == "D2@Delik" && a.Instance == 1).Value.AsNumber());
            Assert.Equal(3, result.Actions.Single(a => a.Target.Name == "D2@Delik" && a.Instance == 2).Value.AsNumber());
            Assert.Equal("RAF 470 / 1600", result.Actions.First(a => a.Target.Name == "Aciklama").Value.AsText());
        }

        [Fact]
        public void CrossValidationHoldsOutWholeVariants()
        {
            var report = CrossValidator.Run(LineSamples(), new InferenceOptions(), LineMaster());
            Assert.Equal(Lines.Length, report.Folds.Count);
            Assert.Equal(0, report.Wrong);
            Assert.True(report.Accuracy > 0.95, report.ToText());
        }
    }
}

namespace RuleForge.Tests
{
    /// <summary>Üretilen model ile beklenen modelin (ör. DriveWorks varyantı) karşılaştırılması.</summary>
    public class SnapshotComparerTests
    {
        [Fact]
        public void IdenticalModelsHaveNoDifferences()
        {
            var a = SyntheticConveyor.Build("A", 3000, 600, "Sol");
            var report = SnapshotComparer.Compare(a, SyntheticConveyor.Build("B", 3000, 600, "Sol"));
            Assert.Equal(0, report.DifferenceCount);
            Assert.True(report.Same > 10);
        }

        [Fact]
        public void MatchesRenamedFilesAndReportsRealDifferences()
        {
            // Beklenen model DriveWorks gibi kodlu adlarla kaydedilmiş; değerlerden biri farklı.
            var actual = SyntheticConveyor.Build("Uretilen", 3000, 600, "Sol");
            var expected = SyntheticConveyor.Build("DW", 3000, 650, "Sol", sfx: "_SP0042");
            var report = SnapshotComparer.Compare(actual, expected);
            Assert.Empty(report.OnlyInActual);
            Assert.Empty(report.OnlyInExpected);
            Assert.Contains(report.Differences, d => d.Label.Contains("Rulo") && d.Actual == "650" && d.Expected == "700");
            Assert.DoesNotContain(report.Differences, d => d.Label.Contains("Govde"));
        }

        [Fact]
        public void ReplacedPartIsOneLineAndDeletedEqualsSuppressed()
        {
            var actual = SyntheticConveyor.Build("Uretilen", 3000, 600, "Sol");
            var expected = SyntheticConveyor.Build("DW", 3000, 600, "Sol");
            // Beklenende kapak yerine adı ve yapısı farklı bir parça var (ör. topuz → kulp); ayrıca bastırılmış bileşen
            // orada hiç yok (DriveWorks "Delete").
            var kapak = expected.Documents.First(d => d.Key.StartsWith("Kapak"));
            kapak.Key = "Tutamak.SLDPRT";
            kapak.Dimensions = new List<DimensionInfo> { new DimensionInfo { Name = "D1@Revolve1", Value = 360 } };
            kapak.Features.Add(new FeatureInfo { Name = "Revolve1", Type = "Revolution" });
            foreach (var c in expected.Components.Where(c => c.DocumentKey.StartsWith("Kapak"))) c.DocumentKey = kapak.Key;
            var suppressed = actual.Components.Where(c => c.Suppressed).Select(c => c.Path).ToList();
            expected.Components.RemoveAll(c => suppressed.Contains(c.Path));

            var report = SnapshotComparer.Compare(actual, expected);
            Assert.True(report.OnlyInActual.Contains("Kapak.SLDPRT"), report.ToText());
            Assert.Contains("Tutamak.SLDPRT", report.OnlyInExpected);
            Assert.Contains(report.Differences, d => d.Kind == TargetKind.ComponentReplace && d.Actual == "Kapak.SLDPRT" && d.Expected == "Tutamak.SLDPRT");
            Assert.DoesNotContain(report.Differences, d => d.Kind == TargetKind.ComponentSuppression);
        }
    }
}

namespace RuleForge.Tests
{
    /// <summary>Üretim gidiş-dönüş testinde bulunan genel durumlar.</summary>
    public class RoundTripLessonsTests
    {
        /// <summary>Gövdede "Delik" özelliği Boy 3000'den büyükse var, değilse silinmiş (DriveWorks "Delete").</summary>
        private static List<VariantSample> Samples(Action<ModelSnapshot, double, int>? edit = null)
        {
            var samples = SyntheticConveyor.Samples();
            for (int i = 0; i < samples.Count; i++)
            {
                var boy = SyntheticConveyor.Specs[i].boy;
                var govde = samples[i].Snapshot.Documents.First(d => d.Key.StartsWith("Govde"));
                if (boy > 3000) govde.Features.Add(new FeatureInfo { Name = "Delik", Type = "ICE" });
                samples[i].Snapshot.Documents.First(d => d.Key.StartsWith("Konveyor")).CustomProperties["Firma"] = "ACME";
                edit?.Invoke(samples[i].Snapshot, boy, i);
            }
            return samples;
        }

        private static ModelSnapshot Master()
        {
            var m = SyntheticConveyor.Build("master", 3000, 600, "Sol");
            m.Documents.First(d => d.Key.StartsWith("Govde")).Features.Add(new FeatureInfo { Name = "Delik", Type = "ICE" });
            m.Documents.First(d => d.Key.StartsWith("Konveyor")).CustomProperties["Firma"] = "XYZ";
            return m;
        }

        [Fact]
        public void DeletedFeatureIsLearnedAsSuppression()
        {
            var report = RuleInferencer.Infer(Samples(), new InferenceOptions(), Master());
            var rule = report.Rules.Single(r => r.Target.Kind == TargetKind.FeatureSuppression && r.Target.Name == "Delik");
            Assert.Contains("Boy", rule.Expression);
            Assert.Contains("<=", rule.Expression);
        }

        [Fact]
        public void ConstantValueDifferentFromMasterBecomesARule()
        {
            var report = RuleInferencer.Infer(Samples(), new InferenceOptions(), Master());
            var rule = report.Rules.Single(r => r.Target.Kind == TargetKind.CustomProperty && r.Target.Name == "Firma");
            Assert.Equal("\"ACME\"", rule.Expression);
            Assert.Equal(1, report.ConstantRuleCount);
            // Master verilmezse karşılaştıracak bir şey yok: sabit kalır.
            Assert.DoesNotContain(RuleInferencer.Infer(Samples(), new InferenceOptions()).Rules, r => r.Target.Name == "Firma");
        }

        [Fact]
        public void RenumberedFeaturesGiveNoRules()
        {
            // "Egri1..3": kısa konveyörlerde biri silinmiş ama hangisi olduğu varyanttan varyanta değişiyor (SolidWorks yeniden numaralıyor).
            var samples = Samples((snap, boy, i) =>
            {
                var govde = snap.Documents.First(d => d.Key.StartsWith("Govde"));
                for (int k = 1; k <= 3; k++)
                    if (boy > 3000 || k != 1 + i % 3)
                        govde.Features.Add(new FeatureInfo { Name = "Egri" + k, Type = "CompositeCurve" });
            });
            var report = RuleInferencer.Infer(samples, new InferenceOptions(), Master());
            Assert.DoesNotContain(report.Rules, r => r.Target.Name != null && r.Target.Name.StartsWith("Egri"));
            Assert.Contains(report.Notes, n => n.Contains("Egri"));
            Assert.Contains(report.Rules, r => r.Target.Name == "Delik"); // kararlı adlı silme yine öğrenilir
        }
    }
}
