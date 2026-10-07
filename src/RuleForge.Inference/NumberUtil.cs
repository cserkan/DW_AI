using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using RuleForge.Core.Expressions;

namespace RuleForge.Inference
{
    internal static class NumberUtil
    {
        private static readonly int[] Denominators = { 1, 2, 4, 5, 10, 3, 8, 20, 25, 50, 100, 1000 };

        public static bool Close(double a, double b, double tol) => Math.Abs(a - b) <= Math.Max(tol, 1e-6 * Math.Abs(b));

        /// <summary>Değeri tolerans içinde kalacak en "yuvarlak" sayıya çeker (0, 1, 2, 3 ondalık).</summary>
        public static double Nice(double v, double tol)
        {
            for (int digits = 0; digits <= 4; digits++)
            {
                var r = Math.Round(v, digits, MidpointRounding.AwayFromZero);
                if (Math.Abs(r - v) <= tol) return r;
            }
            return v;
        }

        /// <summary>Katsayı adayları: basit kesirler (1/2, 3/4, 0.1 ...).</summary>
        public static IEnumerable<double> CoefficientCandidates(double a)
        {
            var seen = new HashSet<double>();
            foreach (var d in Denominators)
            {
                var c = Math.Round(a * d) / d;
                if (seen.Add(c)) yield return c;
            }
            if (seen.Add(a)) yield return a;
        }

        public static string Fmt(double d) => Value.FormatNumber(d);

        /// <summary>a1*x1 + a2*x2 + b ifadesini okunur biçimde yazar.</summary>
        public static string Linear(IList<(double coef, string name)> terms, double constant)
        {
            var sb = new StringBuilder();
            foreach (var (coef, name) in terms)
            {
                if (Math.Abs(coef) < 1e-12) continue;
                var abs = Math.Abs(coef);
                var part = Math.Abs(abs - 1) < 1e-12 ? name : $"{Fmt(abs)} * {name}";
                if (sb.Length == 0) sb.Append(coef < 0 ? "-" + part : part);
                else sb.Append(coef < 0 ? " - " : " + ").Append(part);
            }
            if (Math.Abs(constant) > 1e-12 || sb.Length == 0)
            {
                if (sb.Length == 0) sb.Append(Fmt(constant));
                else sb.Append(constant < 0 ? " - " : " + ").Append(Fmt(Math.Abs(constant)));
            }
            return sb.ToString();
        }

        public static string Literal(Value v)
        {
            switch (v.Kind)
            {
                case ValueKind.Number: return Fmt(v.AsNumber());
                case ValueKind.Bool: return v.AsBool() ? "TRUE" : "FALSE";
                default: return "\"" + v.AsText().Replace("\"", "\"\"") + "\"";
            }
        }

        /// <summary>[lo, hi) aralığındaki en yuvarlak sayı (eşik seçimi için).</summary>
        public static double RoundestBetween(double lo, double hi)
        {
            double[] steps = { 10000, 5000, 1000, 500, 250, 100, 50, 25, 10, 5, 1, 0.5, 0.1, 0.01 };
            foreach (var s in steps)
            {
                var c = Math.Ceiling(lo / s - 1e-9) * s;
                if (c >= lo - 1e-9 && c < hi - 1e-9) return c;
            }
            return lo;
        }

        /// <summary>Destek (fazla örnek) arttıkça 1'e yaklaşan güven puanı.</summary>
        public static double Confidence(int samples, int parameters, int alternatives = 1)
        {
            int support = samples - parameters;
            if (support <= 0) return 0.2;
            var c = 1 - Math.Pow(0.5, support);
            if (alternatives > 1) c /= Math.Pow(alternatives, 0.25);
            return Math.Round(Math.Min(0.99, c), 2);
        }

        public static string Pct(double c) => c.ToString("P0", CultureInfo.InvariantCulture);

        /// <summary>3x3 doğrusal sistemi Gauss eliminasyonuyla çözer.</summary>
        public static double[]? Solve(double[,] a, double[] b)
        {
            int n = b.Length;
            var m = (double[,])a.Clone();
            var v = (double[])b.Clone();
            for (int col = 0; col < n; col++)
            {
                int pivot = Enumerable.Range(col, n - col).OrderByDescending(r => Math.Abs(m[r, col])).First();
                if (Math.Abs(m[pivot, col]) < 1e-12) return null;
                if (pivot != col)
                {
                    for (int k = 0; k < n; k++) { var t = m[col, k]; m[col, k] = m[pivot, k]; m[pivot, k] = t; }
                    var tv = v[col]; v[col] = v[pivot]; v[pivot] = tv;
                }
                for (int r = 0; r < n; r++)
                {
                    if (r == col) continue;
                    var f = m[r, col] / m[col, col];
                    for (int k = col; k < n; k++) m[r, k] -= f * m[col, k];
                    v[r] -= f * v[col];
                }
            }
            var x = new double[n];
            for (int i = 0; i < n; i++) x[i] = v[i] / m[i, i];
            return x;
        }
    }
}
