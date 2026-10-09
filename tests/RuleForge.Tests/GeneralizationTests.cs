using System;
using System.Collections.Generic;
using System.Linq;
using RuleForge.Core.Expressions;
using RuleForge.Core.Model;
using RuleForge.Inference;
using Xunit;
using Xunit.Abstractions;

namespace RuleForge.Tests
{
    /// <summary>Çıkarımın belirsizliğini bilmesi, ezberlememesi ve bir sonraki varyantları önermesi (her ürün için geçerli davranışlar).</summary>
    public class GeneralizationTests
    {
        private readonly ITestOutputHelper _out;

        public GeneralizationTests(ITestOutputHelper output)
        {
            _out = output;
        }

        [Fact]
        public void CrossValidationMeasuresReliabilityWithoutAnswerKey()
        {
            var samples = SyntheticConveyor.Samples().Select(s => new VariantSample(s.Name, s.Snapshot)).ToList();
            var cv = CrossValidator.Run(samples);
            _out.WriteLine(cv.ToText());

            Assert.Equal(10, cv.Folds.Count);
            Assert.True(cv.Accuracy > 0.8, $"doğruluk {cv.Accuracy:P0}");
            // Düz doğrusal kural her turda doğru ve kararlı olmalı
            Assert.Contains(cv.Targets, t => t.Label.Contains("Govde") && t.Label.Contains("D1@Boss") && t.Reliable);
            // Gürültü (Kapak) hiçbir turda öğrenilemez: doğruluk oranına katılmaz, ayrı listelenir
            Assert.Contains(cv.Unexplainable, l => l.Contains("Kapak"));
            // Eşik sadece iki örnekle sınırlıdır; kararsız/riskli olarak işaretlenmeli
            Assert.Contains(cv.Targets, t => t.Label.Contains("OrtaDestek") && !t.Reliable);
        }

        [Fact]
        public void CrossValidationNeedsEnoughVariants()
        {
            var samples = SyntheticConveyor.Samples().Take(3).ToList();
            Assert.Contains("en az 4", string.Join(" ", CrossValidator.Run(samples).Notes));
        }

        [Fact]
        public void SuggestsVariantsThatNarrowTheThreshold()
        {
            var samples = SyntheticConveyor.Samples();
            var report = RuleInferencer.Infer(samples);
            _out.WriteLine(report.ToText());

            Assert.NotEmpty(report.SuggestedVariants);
            Assert.All(report.SuggestedVariants, v => Assert.Equal(report.Inputs.Count, v.Inputs.Count));

            // OrtaDestek eşiği 2800–3400 arasında bilinmiyor: önerilen girdi değerleri bu aralığı daraltmalı
            var thresholdInput = report.Needs.First(n => n.Priority == 1).Input;
            var probes = report.SuggestedVariants.Select(v => v.Inputs[thresholdInput].AsNumber()).Where(x => x > 2800 && x < 3400).ToList();
            Assert.True(probes.Count >= 2, "eşik aralığında en az 2 deneme değeri önerilmeli");

            var csv = report.ToSuggestionCsv();
            Assert.StartsWith("Varyant;", csv.TrimStart('﻿'));
            Assert.Equal(report.SuggestedVariants.Count + 1, csv.Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries).Length);
        }

        [Fact]
        public void SuggestedVariantsKeepInputsIndependent()
        {
            var report = RuleInferencer.Infer(SyntheticConveyor.Samples());
            var boy = report.SuggestedVariants.Select(v => v.Inputs["Boy"].AsNumber()).ToArray();
            var gen = report.SuggestedVariants.Select(v => v.Inputs["Genislik"].AsNumber()).ToArray();
            Assert.True(boy.Distinct().Count() > 1);
            Assert.True(gen.Distinct().Count() > 1);
            // Önerilen girdiler eğitim aralığının dışına taşmamalı (bilinmeyen izin verilen aralığı aşmamak için)
            Assert.All(boy, b => Assert.InRange(b, 1500, 7400));
            Assert.All(gen, g => Assert.InRange(g, 400, 1000));
        }

        [Fact]
        public void DoesNotMemorizeLookupTables()
        {
            // 8 varyantta her Boy için farklı bir değer: 8 girişli tablo bir kural değildir.
            var rnd = new Random(7);
            var samples = new List<VariantSample>();
            var noise = Enumerable.Range(0, 8).Select(_ => rnd.Next(100, 999)).ToArray();
            for (int i = 0; i < 8; i++)
            {
                var snap = SyntheticConveyor.Build("v" + i, 1500 + 700 * i, 600, "Sol", noise: noise[i]);
                samples.Add(new VariantSample("v" + i, snap, new Dictionary<string, Value>
                {
                    ["Boy"] = Value.Number(1500 + 700 * i), ["Genislik"] = Value.Number(600), ["Motor"] = Value.Text("Sol"),
                }));
            }
            var report = RuleInferencer.Infer(samples);
            Assert.DoesNotContain(report.Rules, r => r.Target.Document == "Kapak.SLDPRT" && r.Expression.Contains("RANGELOOKUP"));
            Assert.Contains(report.Unexplained, u => u.Label.Contains("Kapak"));
        }

        [Fact]
        public void DetectsThatTwoInputsBothExplainTheSameThreshold()
        {
            // Boy ve Genişlik birlikte artıyor; OrtaDestek hem "Boy <= 3000" hem "Genislik <= 650" ile açıklanabilir.
            var specs = new[] { (1500.0, 400.0), (2200, 500), (2800, 650), (3400, 800), (4100, 900), (5200, 1000), (6000, 1100) };
            var samples = specs.Select((s, i) => new VariantSample("v" + i, SyntheticConveyor.Build("v" + i, s.Item1, s.Item2, "Sol"),
                new Dictionary<string, Value> { ["Boy"] = Value.Number(s.Item1), ["Genislik"] = Value.Number(s.Item2), ["Motor"] = Value.Text("Sol") })).ToList();
            var report = RuleInferencer.Infer(samples);
            _out.WriteLine(report.ToText());

            var combo = report.Needs.FirstOrDefault(n => n.Combos.Count == 2 && n.Combos[0].ContainsKey("Boy") && n.Combos[0].ContainsKey("Genislik"));
            Assert.NotNull(combo);
            // Pozitif korelasyon: iki girdi zıt uçlarda olmalı (biri düşük, diğeri yüksek)
            var first = combo!.Combos[0];
            Assert.NotEqual(first["Boy"].AsNumber() < 3000, first["Genislik"].AsNumber() < 650);
            Assert.Contains(report.SuggestedVariants, v => v.Reasons.Any(r => r.Contains("belirleyen girdi hangisi")));
        }
    }
}
