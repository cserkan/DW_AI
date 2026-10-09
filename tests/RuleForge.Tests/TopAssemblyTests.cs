using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using RuleForge.Core.Files;
using Xunit;

namespace RuleForge.Tests
{
    public class TopAssemblyTests
    {
        private static readonly string Root = Path.Combine(Path.GetTempPath(), "rf-cikti");

        private static string P(params string[] parts) => Path.Combine(new[] { Root }.Concat(parts).ToArray());

        [Fact]
        public void FindsOneTopAssemblyPerVariantFolder()
        {
            var deps = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
            {
                // V1: üst montaj alt montajları ad ve eski (master) yol ile referanslıyor
                [P("V1", "Dolap_1001.SLDASM")] = new[] { "Kapak_1001.SLDASM", @"C:\Master\Govde_1001.SLDASM", "Raf.SLDPRT" },
                [P("V1", "Kapak_1001.SLDASM")] = new[] { "Mentese.SLDPRT" },
                [P("V1", "Govde_1001.SLDASM")] = new[] { "Yan.SLDPRT" },
                // V2: tam yol ile; alt montaj adı V1'dekiyle aynı olsa da kendi klasöründe değerlendirilir
                [P("V2", "Dolap_1002.SLDASM")] = new[] { P("V2", "Kapak_1001.SLDASM"), P("V2", "alt", "Cekmece_1002.SLDASM") },
                [P("V2", "Kapak_1001.SLDASM")] = new[] { "Mentese.SLDPRT" },
                [P("V2", "alt", "Cekmece_1002.SLDASM")] = new string[0],
                // V3: kullanılmayan artık bir montaj da var; büyük olan seçilmeli
                [P("V3", "Dolap_1003.SLDASM")] = new[] { "Kapak_1003.SLDASM" },
                [P("V3", "Kapak_1003.SLDASM")] = new string[0],
                [P("V3", "Eski.SLDASM")] = new string[0],
            };
            var total = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
            {
                [P("V3", "Dolap_1003.SLDASM")] = 25,
                [P("V3", "Eski.SLDASM")] = 3,
            };

            var result = TopAssemblyDetector.Detect(Root, deps.Keys.ToList(), a => deps[a],
                a => total.TryGetValue(a, out var n) ? n : 0);

            Assert.Equal(3, result.Count);
            Assert.Equal(new[] { "Dolap_1001.SLDASM", "Dolap_1002.SLDASM", "Dolap_1003.SLDASM" },
                result.Select(r => Path.GetFileName(r.Path)));
            Assert.Equal(new[] { 2, 2, 1 }, result.Select(r => r.SubAssemblies));
            Assert.Equal("V3__Dolap_1003", result[2].Label);
            Assert.Equal("Eski.SLDASM", Path.GetFileName(Assert.Single(result[2].OtherTopLevel)));
            Assert.Empty(result[0].OtherTopLevel);
        }

        [Fact]
        public void FlatFolderTreatsEveryTopAssemblyAsVariant()
        {
            var deps = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
            {
                [P("A.SLDASM")] = new[] { "AltA.SLDASM" },
                [P("AltA.SLDASM")] = new string[0],
                [P("B.SLDASM")] = new[] { "AltB.SLDASM" },
                [P("AltB.SLDASM")] = new string[0],
            };
            var result = TopAssemblyDetector.Detect(Root, deps.Keys.ToList(), a => deps[a]);
            Assert.Equal(new[] { "A", "B" }, result.Select(r => r.Label));
        }

        [Fact]
        public void RejoinsPathSplitByQuoteMistake()
        {
            const string real = @"C:\Users\pc\Downloads\CupboardSoloTemplate\SOLIDWORKS Files\Cupboard Assy.SLDASM";
            // PowerShell'de ""yol"" yazılınca yol boşluklarda bölünüyor
            var tokens = new[] { @"C:\Users\pc\Downloads\CupboardSoloTemplate\SOLIDWORKS", @"Files\Cupboard", "Assy.SLDASM\"" };
            var resolved = PathArguments.Resolve(tokens, p => p == real || p == @"D:\b.SLDASM");
            Assert.Equal(new[] { real }, resolved);

            var two = PathArguments.Resolve(new[] { real.Split(' ')[0], "Files\\Cupboard", "Assy.SLDASM", @"D:\b.SLDASM" },
                p => p == real || p == @"D:\b.SLDASM");
            Assert.Equal(new[] { real, @"D:\b.SLDASM" }, two);

            Assert.Equal(new[] { @"C:\yok.SLDASM" }, PathArguments.Resolve(new[] { "\"C:\\yok.SLDASM\"" }, _ => false));
        }
    }
}

namespace RuleForge.Tests
{
    public class DriveWorksNamingTests
    {
        [Theory]
        [InlineData("Cupboard RightDoor Framed Cupboard RightDoor Framed1.SLDPRT", "Cupboard RightDoor Framed.SLDPRT")]
        [InlineData("Cupboard Assy Cupboard Assy10.SLDASM", "Cupboard Assy.SLDASM")]
        [InlineData("Shaker Knob Large Steel.SLDPRT", null)]
        [InlineData("100234.SLDPRT", null)]
        public void DecodesDefaultDriveWorksFileNames(string file, string? master)
        {
            Assert.Equal(master, RuleForge.Inference.DriveWorksNaming.DecodeFile(file));
        }

        [Fact]
        public void DecodesComponentNames()
        {
            Assert.Equal("Cupboard RightDoor-1", RuleForge.Inference.DriveWorksNaming.DecodeComponentName("Cupboard RightDoor Cupboard RightDoor4-1"));
            Assert.Null(RuleForge.Inference.DriveWorksNaming.DecodeComponentName("Shelf Peg1-3"));
        }
    }
}
