using System;
using System.Globalization;

namespace RuleForge.Core.Expressions
{
    public enum ValueKind
    {
        Null,
        Number,
        Text,
        Bool,
    }

    /// <summary>
    /// Kural dilindeki tek değer tipi: sayı, metin veya mantıksal.
    /// Sayılar her zaman mm / derece / adet gibi "kullanıcı birimi" ile tutulur.
    /// </summary>
    public readonly struct Value : IEquatable<Value>
    {
        public const double Epsilon = 1e-9;

        private readonly double _number;
        private readonly string? _text;
        private readonly bool _bool;

        private Value(ValueKind kind, double number, string? text, bool b)
        {
            Kind = kind;
            _number = number;
            _text = text;
            _bool = b;
        }

        public ValueKind Kind { get; }

        public static readonly Value Null = default;

        public static Value Number(double d) => new Value(ValueKind.Number, d, null, false);

        public static Value Text(string s) => new Value(ValueKind.Text, 0, s ?? string.Empty, false);

        public static Value Bool(bool b) => new Value(ValueKind.Bool, 0, null, b);

        public bool IsNull => Kind == ValueKind.Null;

        public double AsNumber()
        {
            switch (Kind)
            {
                case ValueKind.Number: return _number;
                case ValueKind.Bool: return _bool ? 1 : 0;
                case ValueKind.Text:
                    if (double.TryParse(_text, NumberStyles.Float, CultureInfo.InvariantCulture, out var d))
                        return d;
                    throw new ExpressionException($"\"{_text}\" metni sayıya çevrilemiyor.");
                default:
                    throw new ExpressionException("Boş değer sayı olarak kullanılamaz.");
            }
        }

        public bool AsBool()
        {
            switch (Kind)
            {
                case ValueKind.Bool: return _bool;
                case ValueKind.Number: return Math.Abs(_number) > Epsilon;
                case ValueKind.Text:
                    if (string.Equals(_text, "true", StringComparison.OrdinalIgnoreCase)) return true;
                    if (string.Equals(_text, "false", StringComparison.OrdinalIgnoreCase)) return false;
                    throw new ExpressionException($"\"{_text}\" metni mantıksal değere çevrilemiyor.");
                default:
                    throw new ExpressionException("Boş değer mantıksal olarak kullanılamaz.");
            }
        }

        public string AsText()
        {
            switch (Kind)
            {
                case ValueKind.Text: return _text!;
                case ValueKind.Number: return FormatNumber(_number);
                case ValueKind.Bool: return _bool ? "TRUE" : "FALSE";
                default: return string.Empty;
            }
        }

        public static string FormatNumber(double d)
        {
            if (Math.Abs(d - Math.Round(d)) < Epsilon && Math.Abs(d) < 1e15)
                return Math.Round(d).ToString("0", CultureInfo.InvariantCulture);
            return d.ToString("0.##########", CultureInfo.InvariantCulture);
        }

        /// <summary>Metin girdilerini (komut satırı, CSV) en uygun tipe çevirir.</summary>
        public static Value Parse(string? raw)
        {
            if (raw == null) return Null;
            var s = raw.Trim();
            if (s.Length == 0) return Text(string.Empty);
            if (double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var d)) return Number(d);
            if (string.Equals(s, "true", StringComparison.OrdinalIgnoreCase)) return Bool(true);
            if (string.Equals(s, "false", StringComparison.OrdinalIgnoreCase)) return Bool(false);
            return Text(raw);
        }

        public bool Equals(Value other) => LooseEquals(this, other);

        public override bool Equals(object? obj) => obj is Value v && Equals(v);

        public override int GetHashCode()
        {
            switch (Kind)
            {
                case ValueKind.Number: return Math.Round(_number, 6).GetHashCode();
                case ValueKind.Text: return StringComparer.OrdinalIgnoreCase.GetHashCode(_text!);
                case ValueKind.Bool: return _bool.GetHashCode();
                default: return 0;
            }
        }

        /// <summary>Excel'e benzer eşitlik: metinler büyük/küçük harf duyarsız, sayılar toleranslı.</summary>
        public static bool LooseEquals(Value a, Value b)
        {
            if (a.Kind == ValueKind.Null || b.Kind == ValueKind.Null) return a.Kind == b.Kind;
            if (a.Kind == ValueKind.Text || b.Kind == ValueKind.Text)
            {
                if (a.Kind == ValueKind.Text && b.Kind == ValueKind.Text)
                    return string.Equals(a._text, b._text, StringComparison.OrdinalIgnoreCase);
                return string.Equals(a.AsText(), b.AsText(), StringComparison.OrdinalIgnoreCase);
            }
            if (a.Kind == ValueKind.Bool && b.Kind == ValueKind.Bool) return a._bool == b._bool;
            return Math.Abs(a.AsNumber() - b.AsNumber()) <= Epsilon * Math.Max(1, Math.Abs(a.AsNumber()));
        }

        public static int Compare(Value a, Value b)
        {
            if (a.Kind == ValueKind.Text || b.Kind == ValueKind.Text)
                return string.Compare(a.AsText(), b.AsText(), StringComparison.OrdinalIgnoreCase);
            if (LooseEquals(a, b)) return 0;
            return a.AsNumber().CompareTo(b.AsNumber());
        }

        public override string ToString()
        {
            return Kind == ValueKind.Text ? "\"" + _text + "\"" : AsText();
        }
    }
}
