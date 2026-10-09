using System;
using System.IO;
using System.Linq;
using RuleForge.Core.Json;
using RuleForge.Core.Model;
using RuleForge.Core.Rules;
using RuleForge.Inference;
using Xunit;

namespace RuleForge.Tests
{
    /// <summary>
    /// Depodaki gerçek DriveWorks varyantları ve RuleForge'un SolidWorks'te ürettiği modellerin okunmuş hâlleri
    /// (uretim\). Gidiş-dönüş testinde bulunan eşleştirme dersleri bozulmasın diye.
    /// </summary>
    public class RealDataTests
    {
        private static readonly string? Root = FindRoot();

        private static string? FindRoot()
        {
            for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
                if (File.Exists(Path.Combine(dir.FullName, "RuleForge.sln"))) return dir.FullName;
            return null;
        }

        private static ModelSnapshot Load(params string[] parts) => JsonStore.Load<ModelSnapshot>(Path.Combine(new[] { Root! }.Concat(parts).ToArray()));

        private static string Variant(string folder, string number) =>
            Directory.GetFiles(Path.Combine(Root!, folder), $"*{number}__*.json").Single();

        [Fact]
        public void GeneratedConveyorLineMatchesDriveWorks()
        {
            if (Root == null) return;
            // RuleForge: "Conveyor Assembly-1-5", "RH Rail Assembly-1"…; DriveWorks: "Conveyor Assembly -1-0001-5",
            // "RH Rail Assembly RH RAİL ASSEMBLY-1-0001"… ve ağaçta ters sırada (9, 8, 5). Ortak makara tek dosya.
            var actual = Load("uretim", "hat0001-deneme2.json");
            var expected = JsonStore.Load<ModelSnapshot>(Variant(Path.Combine("konveyor", "varyantlar"), "0001"));
            var report = SnapshotComparer.Compare(actual, expected);
            Assert.True(report.DifferenceCount == 0, report.ToText());
        }

        [Fact]
        public void GeneratedCupboardDiffersOnlyInOrderNo()
        {
            if (Root == null) return;
            var actual = Load("uretim", "v0001-deneme2.json");
            var expected = JsonStore.Load<ModelSnapshot>(Variant("varyantlar", "0001"));
            var report = SnapshotComparer.Compare(actual, expected);
            Assert.True(report.OnlyInActual.Count + report.OnlyInExpected.Count == 0, report.ToText());
            Assert.All(report.Differences, d => Assert.Equal("OrderNo", d.Label.Split('\'')[1]));
        }

        [Fact]
        public void ConveyorRollerAndSupportAreSharedAcrossRows()
        {
            if (Root == null) return;
            var dir = Path.Combine(Root, "konveyor", "varyantlar");
            var samples = Directory.GetFiles(dir, "*.json").OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                .Select(f => JsonStore.Load<ModelSnapshot>(f)).Select(s => new VariantSample(s.Name, s)).ToList();
            var report = RuleInferencer.Infer(samples, new InferenceOptions(), Load("konveyor", "master.json"));
            var table = Assert.Single(report.Tables);
            Assert.Equal(new[] { "Roller.SLDPRT", "Support.SLDPRT" }, table.SharedDocuments);
            Assert.True(string.IsNullOrEmpty(table.ModuleComponent));
        }
    }
}
