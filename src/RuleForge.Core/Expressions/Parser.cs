using System;
using System.Collections.Generic;

namespace RuleForge.Core.Expressions
{
    /// <summary>
    /// Öncelik (düşükten yükseğe):
    ///   ||  →  &amp;&amp;  →  = == != &lt;&gt; &lt; &lt;= &gt; &gt;=  →  &amp; (metin birleştirme)
    ///   →  + -  →  * / %  →  tekli - + !  →  ^ (sağdan birleşir)
    /// </summary>
    internal sealed class Parser
    {
        private readonly List<Token> _tokens;
        private int _pos;

        private Parser(List<Token> tokens)
        {
            _tokens = tokens;
        }

        public static Node Parse(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) throw new ExpressionException("İfade boş.");
            var parser = new Parser(Lexer.Tokenize(text));
            var node = parser.ParseOr();
            if (parser.Current.Type != TokenType.End)
                throw new ExpressionException($"Beklenmeyen ifade: '{parser.Current}'", parser.Current.Position);
            return node;
        }

        private Token Current => _tokens[_pos];

        private Token Next() => _tokens[_pos++];

        private bool IsOperator(params string[] ops)
        {
            if (Current.Type != TokenType.Operator) return false;
            foreach (var op in ops)
                if (Current.Text == op) return true;
            return false;
        }

        private Node ParseOr()
        {
            var left = ParseAnd();
            while (IsOperator("||"))
            {
                var t = Next();
                left = new BinaryNode("||", left, ParseAnd(), t.Position);
            }
            return left;
        }

        private Node ParseAnd()
        {
            var left = ParseComparison();
            while (IsOperator("&&"))
            {
                var t = Next();
                left = new BinaryNode("&&", left, ParseComparison(), t.Position);
            }
            return left;
        }

        private Node ParseComparison()
        {
            var left = ParseConcat();
            while (IsOperator("=", "==", "!=", "<>", "<", "<=", ">", ">="))
            {
                var t = Next();
                var op = t.Text == "==" ? "=" : t.Text == "!=" ? "<>" : t.Text;
                left = new BinaryNode(op, left, ParseConcat(), t.Position);
            }
            return left;
        }

        private Node ParseConcat()
        {
            var left = ParseAdditive();
            while (IsOperator("&"))
            {
                var t = Next();
                left = new BinaryNode("&", left, ParseAdditive(), t.Position);
            }
            return left;
        }

        private Node ParseAdditive()
        {
            var left = ParseMultiplicative();
            while (IsOperator("+", "-"))
            {
                var t = Next();
                left = new BinaryNode(t.Text, left, ParseMultiplicative(), t.Position);
            }
            return left;
        }

        private Node ParseMultiplicative()
        {
            var left = ParseUnary();
            while (IsOperator("*", "/", "%"))
            {
                var t = Next();
                left = new BinaryNode(t.Text, left, ParseUnary(), t.Position);
            }
            return left;
        }

        private Node ParseUnary()
        {
            if (IsOperator("-", "+", "!"))
            {
                var t = Next();
                return new UnaryNode(t.Text, ParseUnary(), t.Position);
            }
            return ParsePower();
        }

        private Node ParsePower()
        {
            var left = ParsePrimary();
            if (IsOperator("^"))
            {
                var t = Next();
                // Sağdan birleşir: 2^3^2 = 2^(3^2). Üs tarafında tekli eksiye izin ver.
                return new BinaryNode("^", left, ParseUnary(), t.Position);
            }
            return left;
        }

        private Node ParsePrimary()
        {
            var t = Current;
            switch (t.Type)
            {
                case TokenType.Number:
                    Next();
                    return new LiteralNode(Value.Number(t.Number), t.Position);
                case TokenType.String:
                    Next();
                    return new LiteralNode(Value.Text(t.Text), t.Position);
                case TokenType.LParen:
                {
                    Next();
                    var inner = ParseOr();
                    Expect(TokenType.RParen, ")");
                    return inner;
                }
                case TokenType.Identifier:
                {
                    Next();
                    if (Current.Type == TokenType.LParen)
                    {
                        Next();
                        var args = new List<Node>();
                        if (Current.Type != TokenType.RParen)
                        {
                            args.Add(ParseOr());
                            while (Current.Type == TokenType.Comma)
                            {
                                Next();
                                args.Add(ParseOr());
                            }
                        }
                        Expect(TokenType.RParen, ")");
                        return new CallNode(t.Text.ToUpperInvariant(), args, t.Position);
                    }
                    if (string.Equals(t.Text, "TRUE", StringComparison.OrdinalIgnoreCase))
                        return new LiteralNode(Value.Bool(true), t.Position);
                    if (string.Equals(t.Text, "FALSE", StringComparison.OrdinalIgnoreCase))
                        return new LiteralNode(Value.Bool(false), t.Position);
                    return new IdentifierNode(t.Text, t.Position);
                }
                case TokenType.End:
                    throw new ExpressionException("İfade beklenirken satır bitti.", t.Position);
                default:
                    throw new ExpressionException($"Beklenmeyen '{t}'", t.Position);
            }
        }

        private void Expect(TokenType type, string text)
        {
            if (Current.Type != type)
                throw new ExpressionException($"'{text}' bekleniyordu, '{Current}' bulundu.", Current.Position);
            Next();
        }
    }
}
