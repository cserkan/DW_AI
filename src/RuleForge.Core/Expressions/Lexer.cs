using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace RuleForge.Core.Expressions
{
    internal enum TokenType
    {
        Number,
        String,
        Identifier,
        Operator,
        LParen,
        RParen,
        Comma,
        End,
    }

    internal readonly struct Token
    {
        public Token(TokenType type, string text, int position, double number = 0)
        {
            Type = type;
            Text = text;
            Position = position;
            Number = number;
        }

        public TokenType Type { get; }
        public string Text { get; }
        public int Position { get; }
        public double Number { get; }

        public override string ToString() => Type == TokenType.End ? "<son>" : Text;
    }

    internal static class Lexer
    {
        private static readonly string[] Operators =
        {
            "<=", ">=", "<>", "!=", "==", "&&", "||",
            "+", "-", "*", "/", "^", "%", "&", "=", "<", ">", "!",
        };

        public static List<Token> Tokenize(string text)
        {
            var tokens = new List<Token>();
            int i = 0;
            while (i < text.Length)
            {
                char c = text[i];
                if (char.IsWhiteSpace(c))
                {
                    i++;
                    continue;
                }

                if (char.IsDigit(c) || (c == '.' && i + 1 < text.Length && char.IsDigit(text[i + 1])))
                {
                    int start = i;
                    while (i < text.Length && (char.IsDigit(text[i]) || text[i] == '.')) i++;
                    if (i < text.Length && (text[i] == 'e' || text[i] == 'E'))
                    {
                        int save = i;
                        i++;
                        if (i < text.Length && (text[i] == '+' || text[i] == '-')) i++;
                        if (i < text.Length && char.IsDigit(text[i]))
                            while (i < text.Length && char.IsDigit(text[i])) i++;
                        else
                            i = save;
                    }
                    var s = text.Substring(start, i - start);
                    if (!double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var d))
                        throw new ExpressionException($"Geçersiz sayı: {s}", start);
                    tokens.Add(new Token(TokenType.Number, s, start, d));
                    continue;
                }

                if (c == '"' || c == '\'')
                {
                    int start = i;
                    char quote = c;
                    var sb = new StringBuilder();
                    i++;
                    while (true)
                    {
                        if (i >= text.Length) throw new ExpressionException("Kapanmamış metin (tırnak eksik).", start);
                        if (text[i] == quote)
                        {
                            // Excel gibi: "" -> "
                            if (i + 1 < text.Length && text[i + 1] == quote)
                            {
                                sb.Append(quote);
                                i += 2;
                                continue;
                            }
                            i++;
                            break;
                        }
                        sb.Append(text[i]);
                        i++;
                    }
                    tokens.Add(new Token(TokenType.String, sb.ToString(), start));
                    continue;
                }

                if (char.IsLetter(c) || c == '_')
                {
                    int start = i;
                    while (i < text.Length && (char.IsLetterOrDigit(text[i]) || text[i] == '_')) i++;
                    tokens.Add(new Token(TokenType.Identifier, text.Substring(start, i - start), start));
                    continue;
                }

                if (c == '(') { tokens.Add(new Token(TokenType.LParen, "(", i)); i++; continue; }
                if (c == ')') { tokens.Add(new Token(TokenType.RParen, ")", i)); i++; continue; }
                if (c == ',' || c == ';') { tokens.Add(new Token(TokenType.Comma, ",", i)); i++; continue; }

                string? op = null;
                foreach (var candidate in Operators)
                {
                    if (string.CompareOrdinal(text, i, candidate, 0, candidate.Length) == 0)
                    {
                        op = candidate;
                        break;
                    }
                }
                if (op == null) throw new ExpressionException($"Beklenmeyen karakter: '{c}'", i);
                tokens.Add(new Token(TokenType.Operator, op, i));
                i += op.Length;
            }
            tokens.Add(new Token(TokenType.End, string.Empty, text.Length));
            return tokens;
        }
    }
}
