using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using RuleForge.Core.Expressions;

namespace RuleForge.Inference
{
    /// <summary>
    /// Varyant girdi tablosu (CSV). İlk sütun varyant adı, diğer sütunlar girdiler.
    /// DriveWorks'ten dışa aktarılan spesifikasyon listesi bu formata kolayca getirilebilir.
    /// Ayraç otomatik algılanır (; , veya sekme). Türkçe Excel'in "1200,5" ondalık virgülü desteklenir.
    /// </summary>
    public static class InputTable
    {
        private static readonly Regex DecimalComma = new Regex(@"^-?\d+,\d+$", RegexOptions.Compiled);

        public static Dictionary<string, Dictionary<string, Value>> Load(string path)
        {
            return Parse(File.ReadAllText(path, Encoding.UTF8));
        }

        public static Dictionary<string, Dictionary<string, Value>> Parse(string csv)
        {
            var lines = csv.Replace("\r\n", "\n").Split('\n').Where(l => l.Trim().Length > 0).ToList();
            var result = new Dictionary<string, Dictionary<string, Value>>(StringComparer.OrdinalIgnoreCase);
            if (lines.Count < 2) return result;

            char sep = DetectSeparator(lines[0]);
            var header = SplitLine(lines[0], sep).Select(h => h.Trim().TrimStart('﻿')).ToList();
            for (int i = 1; i < lines.Count; i++)
            {
                var cells = SplitLine(lines[i], sep);
                if (cells.Count == 0 || string.IsNullOrWhiteSpace(cells[0])) continue;
                var row = new Dictionary<string, Value>(StringComparer.OrdinalIgnoreCase);
                for (int c = 1; c < header.Count && c < cells.Count; c++)
                {
                    var raw = cells[c].Trim();
                    if (sep == ';' && DecimalComma.IsMatch(raw)) raw = raw.Replace(',', '.');
                    row[header[c]] = Value.Parse(raw);
                }
                result[cells[0].Trim()] = row;
            }
            return result;
        }

        private static char DetectSeparator(string headerLine)
        {
            var candidates = new[] { ';', '\t', ',' };
            return candidates.OrderByDescending(c => headerLine.Count(ch => ch == c)).First();
        }

        private static List<string> SplitLine(string line, char sep)
        {
            var cells = new List<string>();
            var sb = new StringBuilder();
            bool quoted = false;
            for (int i = 0; i < line.Length; i++)
            {
                char ch = line[i];
                if (quoted)
                {
                    if (ch == '"' && i + 1 < line.Length && line[i + 1] == '"') { sb.Append('"'); i++; }
                    else if (ch == '"') quoted = false;
                    else sb.Append(ch);
                }
                else if (ch == '"') quoted = true;
                else if (ch == sep) { cells.Add(sb.ToString()); sb.Clear(); }
                else sb.Append(ch);
            }
            cells.Add(sb.ToString());
            return cells;
        }
    }
}
