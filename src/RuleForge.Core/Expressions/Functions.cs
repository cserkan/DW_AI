using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace RuleForge.Core.Expressions
{
    public sealed class FunctionInfo
    {
        public FunctionInfo(string name, int minArgs, int maxArgs, string signature, string description,
            Func<Value[], Value>? impl, bool lazy = false)
        {
            Name = name;
            MinArgs = minArgs;
            MaxArgs = maxArgs;
            Signature = signature;
            Description = description;
            Implementation = impl;
            IsLazy = lazy;
        }

        public string Name { get; }
        public int MinArgs { get; }

        /// <summary>-1 = sınırsız.</summary>
        public int MaxArgs { get; }

        public string Signature { get; }
        public string Description { get; }

        /// <summary>Lazy fonksiyonlarda null'dır; Evaluator içinde özel ele alınır.</summary>
        public Func<Value[], Value>? Implementation { get; }

        public bool IsLazy { get; }
    }

    /// <summary>
    /// Kural dilindeki fonksiyonlar. Bu liste aynı zamanda yapay zekâya verilen dil
    /// dokümantasyonunun kaynağıdır, bu yüzden açıklamalar kısa ve net tutulmalı.
    /// Açılar DERECE cinsindendir (Excel'den farklı olarak).
    /// </summary>
    public static class Functions
    {
        private static readonly Dictionary<string, FunctionInfo> Registry =
            new Dictionary<string, FunctionInfo>(StringComparer.OrdinalIgnoreCase);

        static Functions()
        {
            // Mantık (lazy: sadece gereken argüman hesaplanır)
            Lazy("IF", 2, 3, "IF(koşul, doğruysa, [yanlışsa])", "Koşula göre değer seçer. Yanlış dalı verilmezse FALSE döner.");
            Lazy("IFS", 2, -1, "IFS(k1, d1, k2, d2, ...)", "İlk doğru koşulun değerini döner. Varsayılan için son koşulu TRUE yapın.");
            Lazy("SWITCH", 3, -1, "SWITCH(ifade, a1, d1, a2, d2, ..., [varsayılan])", "İfade a_i'ye eşitse d_i döner.");
            Lazy("AND", 1, -1, "AND(a, b, ...)", "Hepsi doğruysa TRUE.");
            Lazy("OR", 1, -1, "OR(a, b, ...)", "Biri doğruysa TRUE.");
            Lazy("RANGELOOKUP", 3, -1, "RANGELOOKUP(x, sınır1, d1, sınır2, d2, ..., [varsayılan])",
                "x <= sınır_i olan ilk aralığın değerini döner (sınırlar artan sırada). Örn. profil seçimi.");
            Add("NOT", 1, 1, "NOT(a)", "Mantıksal değil.", a => Value.Bool(!a[0].AsBool()));

            // Matematik
            Add("MIN", 1, -1, "MIN(a, b, ...)", "En küçük.", a => Value.Number(a.Min(v => v.AsNumber())));
            Add("MAX", 1, -1, "MAX(a, b, ...)", "En büyük.", a => Value.Number(a.Max(v => v.AsNumber())));
            Add("ABS", 1, 1, "ABS(x)", "Mutlak değer.", a => Value.Number(Math.Abs(a[0].AsNumber())));
            Add("SQRT", 1, 1, "SQRT(x)", "Karekök.", a => Value.Number(Sqrt(a[0].AsNumber())));
            Add("POWER", 2, 2, "POWER(x, y)", "x üssü y.", a => Value.Number(Math.Pow(a[0].AsNumber(), a[1].AsNumber())));
            Add("ROUND", 1, 2, "ROUND(x, [basamak])", "Yuvarla (0.5 yukarı).", a => Value.Number(Round(a[0].AsNumber(), Digits(a, 1), RoundMode.Nearest)));
            Add("ROUNDUP", 1, 2, "ROUNDUP(x, [basamak])", "Sıfırdan uzağa yuvarla.", a => Value.Number(Round(a[0].AsNumber(), Digits(a, 1), RoundMode.AwayFromZero)));
            Add("ROUNDDOWN", 1, 2, "ROUNDDOWN(x, [basamak])", "Sıfıra doğru yuvarla.", a => Value.Number(Round(a[0].AsNumber(), Digits(a, 1), RoundMode.TowardZero)));
            Add("CEILING", 1, 2, "CEILING(x, [adım])", "x'i adımın üst katına yuvarla. CEILING(1230, 100) = 1300.",
                a => Value.Number(ToMultiple(a[0].AsNumber(), Step(a, 1), Math.Ceiling)));
            Add("FLOOR", 1, 2, "FLOOR(x, [adım])", "x'i adımın alt katına yuvarla. FLOOR(1230, 100) = 1200.",
                a => Value.Number(ToMultiple(a[0].AsNumber(), Step(a, 1), Math.Floor)));
            Add("MROUND", 2, 2, "MROUND(x, adım)", "x'i adımın en yakın katına yuvarla.",
                a => Value.Number(ToMultiple(a[0].AsNumber(), Step(a, 1), v => Math.Round(v, MidpointRounding.AwayFromZero))));
            Add("INT", 1, 1, "INT(x)", "Aşağı tam sayı.", a => Value.Number(Math.Floor(a[0].AsNumber())));
            Add("MOD", 2, 2, "MOD(a, b)", "Bölümden kalan (b'nin işaretiyle).", a => Value.Number(Mod(a[0].AsNumber(), a[1].AsNumber())));
            Add("CLAMP", 3, 3, "CLAMP(x, min, max)", "x'i [min, max] aralığına sıkıştır.",
                a => Value.Number(Math.Min(Math.Max(a[0].AsNumber(), a[1].AsNumber()), a[2].AsNumber())));
            Add("PI", 0, 0, "PI()", "π sayısı.", a => Value.Number(Math.PI));
            Add("SIN", 1, 1, "SIN(derece)", "Sinüs (derece).", a => Value.Number(Math.Sin(ToRad(a[0].AsNumber()))));
            Add("COS", 1, 1, "COS(derece)", "Kosinüs (derece).", a => Value.Number(Math.Cos(ToRad(a[0].AsNumber()))));
            Add("TAN", 1, 1, "TAN(derece)", "Tanjant (derece).", a => Value.Number(Math.Tan(ToRad(a[0].AsNumber()))));
            Add("ASIN", 1, 1, "ASIN(x)", "Ark sinüs, derece döner.", a => Value.Number(ToDeg(Math.Asin(a[0].AsNumber()))));
            Add("ACOS", 1, 1, "ACOS(x)", "Ark kosinüs, derece döner.", a => Value.Number(ToDeg(Math.Acos(a[0].AsNumber()))));
            Add("ATAN", 1, 1, "ATAN(x)", "Ark tanjant, derece döner.", a => Value.Number(ToDeg(Math.Atan(a[0].AsNumber()))));
            Add("ATAN2", 2, 2, "ATAN2(y, x)", "İki argümanlı ark tanjant, derece döner.", a => Value.Number(ToDeg(Math.Atan2(a[0].AsNumber(), a[1].AsNumber()))));

            // Metin
            Add("CONCAT", 1, -1, "CONCAT(a, b, ...)", "Metinleri birleştir (& operatörü de olur).",
                a => Value.Text(string.Concat(a.Select(v => v.AsText()))));
            Add("TEXT", 2, 2, "TEXT(x, \"0.0\")", "Sayıyı biçimle (.NET/Excel biçim kodu).",
                a => Value.Text(a[0].AsNumber().ToString(a[1].AsText(), CultureInfo.InvariantCulture)));
            Add("UPPER", 1, 1, "UPPER(s)", "Büyük harf.", a => Value.Text(a[0].AsText().ToUpper(CultureInfo.GetCultureInfo("tr-TR"))));
            Add("LOWER", 1, 1, "LOWER(s)", "Küçük harf.", a => Value.Text(a[0].AsText().ToLower(CultureInfo.GetCultureInfo("tr-TR"))));
            Add("LEN", 1, 1, "LEN(s)", "Metin uzunluğu.", a => Value.Number(a[0].AsText().Length));
            Add("LEFT", 2, 2, "LEFT(s, n)", "Soldan n karakter.", a => Value.Text(Left(a[0].AsText(), (int)a[1].AsNumber())));
            Add("RIGHT", 2, 2, "RIGHT(s, n)", "Sağdan n karakter.", a => Value.Text(Right(a[0].AsText(), (int)a[1].AsNumber())));
            Add("CONTAINS", 2, 2, "CONTAINS(s, parça)", "s içinde parça var mı (harf duyarsız).",
                a => Value.Bool(a[0].AsText().IndexOf(a[1].AsText(), StringComparison.OrdinalIgnoreCase) >= 0));
            Add("NUMBER", 1, 1, "NUMBER(s)", "Metni sayıya çevir.", a => Value.Number(a[0].AsNumber()));
        }

        public static IEnumerable<FunctionInfo> All => Registry.Values.OrderBy(f => f.Name, StringComparer.Ordinal);

        public static bool TryGet(string name, out FunctionInfo info) => Registry.TryGetValue(name, out info!);

        private static void Add(string name, int min, int max, string sig, string desc, Func<Value[], Value> impl)
        {
            Registry[name] = new FunctionInfo(name, min, max, sig, desc, impl);
        }

        private static void Lazy(string name, int min, int max, string sig, string desc)
        {
            Registry[name] = new FunctionInfo(name, min, max, sig, desc, null, lazy: true);
        }

        private enum RoundMode
        {
            Nearest,
            AwayFromZero,
            TowardZero,
        }

        private static int Digits(Value[] a, int index) => a.Length > index ? (int)a[index].AsNumber() : 0;

        private static double Step(Value[] a, int index)
        {
            var s = a.Length > index ? a[index].AsNumber() : 1.0;
            if (Math.Abs(s) < Value.Epsilon) throw new ExpressionException("Adım (step) sıfır olamaz.");
            return Math.Abs(s);
        }

        private static double Round(double x, int digits, RoundMode mode)
        {
            double factor = Math.Pow(10, digits);
            // Kayan nokta hatasını (ör. 2.675*100 = 267.49999) temizle.
            double scaled = Math.Round(x * factor, 9);
            double r;
            switch (mode)
            {
                case RoundMode.AwayFromZero:
                    r = Math.Sign(scaled) * Math.Ceiling(Math.Abs(scaled));
                    break;
                case RoundMode.TowardZero:
                    r = Math.Sign(scaled) * Math.Floor(Math.Abs(scaled));
                    break;
                default:
                    r = Math.Round(scaled, MidpointRounding.AwayFromZero);
                    break;
            }
            return r / factor;
        }

        private static double ToMultiple(double x, double step, Func<double, double> op)
        {
            // 1200/100 = 11.999999 gibi durumlarda yanlış yuvarlamayı önle.
            return op(Math.Round(x / step, 9)) * step;
        }

        private static double Mod(double a, double b)
        {
            if (Math.Abs(b) < Value.Epsilon) throw new ExpressionException("MOD: sıfıra bölme.");
            return a - b * Math.Floor(a / b);
        }

        private static double Sqrt(double x)
        {
            if (x < 0) throw new ExpressionException("SQRT: negatif sayı.");
            return Math.Sqrt(x);
        }

        private static double ToRad(double deg) => deg * Math.PI / 180.0;

        private static double ToDeg(double rad) => rad * 180.0 / Math.PI;

        private static string Left(string s, int n) => n >= s.Length ? s : s.Substring(0, Math.Max(0, n));

        private static string Right(string s, int n) => n >= s.Length ? s : s.Substring(s.Length - Math.Max(0, n));
    }
}
