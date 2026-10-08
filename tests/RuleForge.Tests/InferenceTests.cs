using System.Linq;
using RuleForge.Core.Engine;
using RuleForge.Core.Expressions;
using RuleForge.Core.Rules;
using RuleForge.Inference;
using Xunit;
using Xunit.Abstractions;

namespace RuleForge.Tests
{
    public class InferenceTests
    {
        private readonly ITestOutputHelper _out;

        public InferenceTests(ITestOutputHelper output)
        {
            _out = output;
        }

        private static Rule RuleFor(InferenceReport r, string fragment) =>
            r.Rules.Single(x => x.Target.ToString().Contains(fragment));

        [Fact]
        public void RecoversHiddenConveyorRules()
        {
            var report = RuleInferencer.Infer(SyntheticConveyor.Samples());
            _out.WriteLine(report.ToText());

            Assert.Equal("Boy - 40", RuleFor(report, "Govde.SLDPRT :: D1@Boss").Expression);
            Assert.Equal("2 * Boy + 300", RuleFor(report, "Bant.SLDPRT").Expression);
            Assert.Equal("Genislik + 50", RuleFor(report, "Rulo.SLDPRT").Expression);
            Assert.Equal("CEILING(Boy / 1500) + 1", RuleFor(report, "LocalLPattern1").Expression);
            Assert.Equal("Motor = \"Sag\"", RuleFor(report, "MotorSol-1").Expression);
            Assert.Equal("Boy <= 3000", RuleFor(report, "OrtaDestek-1").Expression);
            Assert.Equal("\"KONVEYOR \" & Boy & \"x\" & Genislik", RuleFor(report, "Aciklama").Expression);
            Assert.StartsWith("RANGELOOKUP(Genislik, 500, 40, 60)", RuleFor(report, "Profil.SLDPRT").Expression);

            // Gürültü açıklanamamalı, sabitler ve referans ölçüler kural olmamalı
            Assert.Contains(report.Unexplained, u => u.Label.Contains("Kapak"));
            Assert.DoesNotContain(report.Rules, r => r.Target.Name == "D2@Boss" || r.Target.Name == "D3@Sketch2");
            Assert.DoesNotContain(report.Rules, r => r.Target.Name == "Firma");
            Assert.All(report.Rules, r => Assert.Equal(RuleStatus.Proposed, r.Status));

            // Eşik belirsizliği kullanıcıya sorulmalı
            Assert.Contains(report.Questions, q => q.Contains("2800") && q.Contains("3400"));

            // Girdiler veriden doğru tanımlanmalı
            var motor = report.Inputs.Single(i => i.Name == "Motor");
            Assert.Equal(InputType.Choice, motor.Type);
            Assert.Equal(new[] { "Sag", "Sol" }, motor.Options);
            Assert.Equal(1500, report.Inputs.Single(i => i.Name == "Boy").Min);
        }

        [Fact]
        public void InferredRuleSetReproducesEveryVariant()
        {
            var samples = SyntheticConveyor.Samples();
            var ruleSet = RuleInferencer.Infer(samples).ToRuleSet("Konveyor", "Konveyor.SLDASM");
            Assert.DoesNotContain(RuleSetValidator.Validate(ruleSet, samples[0].Snapshot), i => i.Severity == IssueSeverity.Error);

            foreach (var s in samples)
            {
                var result = RuleEngine.Evaluate(ruleSet, s.Inputs, new EvaluationOptions { IncludeProposed = true });
                Assert.True(result.Success, string.Join("\n", result.Errors));
                foreach (var a in result.Actions.Where(a => a.Target.Kind == TargetKind.Dimension))
                {
                    var doc = s.Snapshot.FindDocument(a.Target.Document!)!;
                    Assert.Equal(doc.FindDimension(a.Target.Name!)!.Value, a.Value.AsNumber(), 6);
                }
            }
        }

        [Fact]
        public void NormalizesDriveWorksStyleFileNames()
        {
            var samples = SyntheticConveyor.Samples(suffixPattern: "yes");
            var master = SyntheticConveyor.Build("master", 3000, 600, "Sol");
            var report = RuleInferencer.Infer(samples, new InferenceOptions { NamePattern = @"^(.*?)_SP\d+$" }, master);
            _out.WriteLine(report.ToText());

            Assert.Equal("Boy - 40", RuleFor(report, "Govde.SLDPRT :: D1@Boss").Expression);
            Assert.Equal("Motor = \"Sag\"", RuleFor(report, "[MotorSol-1]").Expression);
        }

        [Fact]
        public void UsesModelObservationAsInputWhenNoTable()
        {
            var samples = SyntheticConveyor.Samples().Select(s => new VariantSample(s.Name, s.Snapshot)).ToList();
            var options = new InferenceOptions();
            options.InputObservations["Boy"] = "dim:Govde.SLDPRT:D1@Boss";
            var report = RuleInferencer.Infer(samples, options);
            _out.WriteLine(report.ToText());
            Assert.Equal("2 * Boy + 380", RuleFor(report, "Bant.SLDPRT").Expression);
        }

        [Fact]
        public void CsvInputTableWithTurkishExcelFormat()
        {
            var table = InputTable.Parse("Varyant;Boy;Genislik;Motor;Kapakli\nSP001;1500;400,5;Sol;true\nSP002;2200;500;Sag;false\n");
            Assert.Equal(2, table.Count);
            Assert.Equal(400.5, table["SP001"]["Genislik"].AsNumber());
            Assert.Equal(ValueKind.Bool, table["SP002"]["Kapakli"].Kind);
        }
    }
}

namespace RuleForge.Tests
{
    public class SampleDataWriter
    {
        /// <summary>RULEFORGE_WRITE_SAMPLES=klasör ile çalıştırılırsa demo veri setini yazar.</summary>
        [Fact]
        public void WriteDemoSamples()
        {
            var dir = System.Environment.GetEnvironmentVariable("RULEFORGE_WRITE_SAMPLES");
            if (string.IsNullOrEmpty(dir)) return;
            var csv = new System.Text.StringBuilder("Varyant;Boy;Genislik;Motor\n");
            foreach (var s in SyntheticConveyor.Samples(suffixPattern: "yes"))
            {
                s.Snapshot.SourcePath = $@"C:\DriveWorks\Cikti\{s.Name}\Konveyor_{s.Name}.SLDASM";
                RuleForge.Core.Json.JsonStore.Save(s.Snapshot, System.IO.Path.Combine(dir, "varyantlar", s.Name + ".json"));
                csv.Append($"{s.Name};{s.Inputs["Boy"].AsText()};{s.Inputs["Genislik"].AsText()};{s.Inputs["Motor"].AsText()}\n");
            }
            System.IO.File.WriteAllText(System.IO.Path.Combine(dir, "girdiler.csv"), csv.ToString());
            var master = SyntheticConveyor.Build("Konveyor", 3000, 600, "Sol");
            master.SourcePath = @"C:\Master\Konveyor\Konveyor.SLDASM";
            RuleForge.Core.Json.JsonStore.Save(master, System.IO.Path.Combine(dir, "master.json"));
        }
    }
}

namespace RuleForge.Tests
{
    public class BlindInferenceTests
    {
        private readonly Xunit.Abstractions.ITestOutputHelper _out;

        public BlindInferenceTests(Xunit.Abstractions.ITestOutputHelper output)
        {
            _out = output;
        }

        /// <summary>Girdi tablosu olmadan: değişen değerler listelenmeli, girdiler modelden tahmin edilmeli.</summary>
        [Fact]
        public void DetectsDriversWithoutInputTable()
        {
            var samples = System.Linq.Enumerable.ToList(System.Linq.Enumerable.Select(SyntheticConveyor.Samples(),
                s => new RuleForge.Inference.VariantSample(s.Name, s.Snapshot)));
            var report = RuleForge.Inference.RuleInferencer.Infer(samples);
            _out.WriteLine(report.ToText());

            var drivers = System.Linq.Enumerable.ToList(System.Linq.Enumerable.Select(report.Drivers, d => d.ObservationKey));
            // Boy ailesinden biri, Genişlik ailesinden biri ve motor seçimi bulunmalı
            Assert.Contains(drivers, k => k.Contains("Bant") || k.Contains("Govde"));
            Assert.Contains(drivers, k => k.Contains("Rulo") || k.Contains("Profil"));
            Assert.Contains(drivers, k => k.Contains("MotorSol"));
            Assert.Equal(3, report.Drivers.Count);

            // Boy'a bağlı diğer değerler tahmini girdiyle ifade edilmeli
            Assert.Contains(report.Rules, r => r.Target.Name == "D1@LocalLPattern1");
            Assert.Contains(report.Rules, r => r.Target.Component == "OrtaDestek-1");
            Assert.Contains(report.Rules, r => r.Target.Name == "Aciklama");
            // Gürültü açıklanamaz kalmalı, değişenler listesinde görünmeli
            Assert.Contains(report.Unexplained, u => u.Label.Contains("Kapak"));
            Assert.Contains(report.Changes, c => c.Label.Contains("Kapak"));
            Assert.DoesNotContain(report.Changes, c => c.Label.Contains("Firma"));
        }
    }
}
