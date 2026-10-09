using System;
using System.IO;
using RuleForge.Core.Files;
using RuleForge.Core.Model;
using Xunit;

namespace RuleForge.Tests
{
    public class PartLibraryTests
    {
        private static string NewFolder()
        {
            var dir = Path.Combine(Path.GetTempPath(), "rf-kutuphane-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(dir);
            return dir;
        }

        [Fact]
        public void NamesAreNumberedPerMasterAndReusedByFingerprint()
        {
            var dir = NewFolder();
            var lib = PartLibrary.Open(dir);
            var a = lib.Reserve("Frame.SLDPRT", "iz-a", "S1");
            File.WriteAllText(a, "x");
            var b = lib.Reserve("Frame.SLDPRT", "iz-b", "S1");
            File.WriteAllText(b, "x");
            var c = lib.Reserve("Roller.SLDPRT", "iz-c", "S1");
            Assert.Equal("Frame-0001.SLDPRT", Path.GetFileName(a));
            Assert.Equal("Frame-0002.SLDPRT", Path.GetFileName(b));
            Assert.Equal("Roller-0001.SLDPRT", Path.GetFileName(c));
            lib.Save();

            // Yeniden açınca liste korunur; aynı iz eski dosyayı bulur, silinmiş dosya bulunmaz.
            var again = PartLibrary.Open(dir);
            Assert.Equal(a, again.Find("iz-a", ".SLDPRT"));
            Assert.Null(again.Find("iz-a", ".SLDASM"));
            Assert.Null(again.Find("iz-c", ".SLDPRT")); // dosya hiç yazılmadı
            Assert.Equal("Frame-0003.SLDPRT", Path.GetFileName(again.Reserve("Frame.SLDPRT", "iz-d", "S2")));
        }

        [Fact]
        public void ContentIgnoresOrderAndFileNames()
        {
            DocumentInfo Doc(string key, double length, bool reverse)
            {
                var d = new DocumentInfo { Key = key, Kind = DocumentKind.Part, ActiveConfiguration = "Default" };
                var dims = new[] { new DimensionInfo { Name = "D1@Boy", Value = length }, new DimensionInfo { Name = "D1@En", Value = 50 } };
                if (reverse) Array.Reverse(dims);
                d.Dimensions.AddRange(dims);
                d.CustomProperties["PartNumber"] = "P-" + length;
                return d;
            }
            Assert.Equal(PartLibrary.DocumentContent(Doc("Frame-1.SLDPRT", 600, false)), PartLibrary.DocumentContent(Doc("Frame-3.SLDPRT", 600, true)));
            Assert.NotEqual(PartLibrary.DocumentContent(Doc("Frame-1.SLDPRT", 600, false)), PartLibrary.DocumentContent(Doc("Frame-1.SLDPRT", 601, false)));
        }
    }
}
