using System.IO;
using System.Text.RegularExpressions;

namespace RuleForge.Inference
{
    /// <summary>
    /// DriveWorks'ün varsayılan dosya adlandırmasını çözer: "&lt;MASTER:NAME&gt;" &amp; DWSpecificationID kuralı
    /// "Cupboard RightDoor Framed Cupboard RightDoor Framed1.SLDPRT" gibi adlar üretir. Buradan master parça
    /// adı ("Cupboard RightDoor Framed.SLDPRT") geri çıkarılır; böylece varyantlar arasında aynı parça tanınır
    /// ve bir parçanın başka bir master parçayla değiştirildiği (ör. kapak tipi) anlaşılır.
    /// </summary>
    public static class DriveWorksNaming
    {
        private static readonly Regex Doubled = new Regex(@"^(?<m>.+?) \k<m>\d+$", RegexOptions.Compiled);

        /// <summary>"Govde Govde12.SLDPRT" → "Govde.SLDPRT"; DriveWorks biçiminde değilse null.</summary>
        public static string? DecodeFile(string fileName)
        {
            var stem = Path.GetFileNameWithoutExtension(fileName);
            var m = Doubled.Match(stem);
            return m.Success ? m.Groups["m"].Value + Path.GetExtension(fileName) : null;
        }

        /// <summary>Bileşen adı: "Govde Govde12-1" → "Govde-1"; DriveWorks biçiminde değilse null.</summary>
        public static string? DecodeComponentName(string name)
        {
            var dash = name.LastIndexOf('-');
            if (dash <= 0) return null;
            var m = Doubled.Match(name.Substring(0, dash));
            return m.Success ? m.Groups["m"].Value + name.Substring(dash) : null;
        }
    }
}
