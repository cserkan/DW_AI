using System;
using System.Collections.Generic;

namespace RuleForge.Core.Expressions
{
    public interface IVariableResolver
    {
        bool TryResolve(string name, out Value value);
    }

    public sealed class DictionaryResolver : IVariableResolver
    {
        private readonly IDictionary<string, Value> _values;

        public DictionaryResolver(IDictionary<string, Value> values)
        {
            _values = values;
        }

        public bool TryResolve(string name, out Value value) => _values.TryGetValue(name, out value);
    }

    /// <summary>Ayrıştırılmış (parse edilmiş) ve tekrar tekrar değerlendirilebilen ifade.</summary>
    public sealed class Expression
    {
        private Expression(string text, Node root)
        {
            Text = text;
            Root = root;
        }

        public string Text { get; }
        public Node Root { get; }

        public static Expression Parse(string text)
        {
            var root = Parser.Parse(text);
            ValidateCalls(root);
            return new Expression(text, root);
        }

        public static bool TryParse(string text, out Expression? expression, out string? error)
        {
            try
            {
                expression = Parse(text);
                error = null;
                return true;
            }
            catch (ExpressionException ex)
            {
                expression = null;
                error = ex.Message;
                return false;
            }
        }

        public static Value Evaluate(string text, IDictionary<string, Value> variables)
        {
            return Parse(text).Evaluate(new DictionaryResolver(variables));
        }

        public Value Evaluate(IVariableResolver resolver) => Evaluator.Evaluate(Root, resolver);

        /// <summary>İfadenin kullandığı değişken adları (fonksiyon adları hariç).</summary>
        public ISet<string> GetIdentifiers()
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            Collect(Root, set);
            return set;
        }

        private static void Collect(Node node, ISet<string> set)
        {
            switch (node)
            {
                case IdentifierNode id:
                    set.Add(id.Name);
                    break;
                case UnaryNode u:
                    Collect(u.Operand, set);
                    break;
                case BinaryNode b:
                    Collect(b.Left, set);
                    Collect(b.Right, set);
                    break;
                case CallNode c:
                    foreach (var a in c.Arguments) Collect(a, set);
                    break;
            }
        }

        private static void ValidateCalls(Node node)
        {
            switch (node)
            {
                case UnaryNode u:
                    ValidateCalls(u.Operand);
                    break;
                case BinaryNode b:
                    ValidateCalls(b.Left);
                    ValidateCalls(b.Right);
                    break;
                case CallNode c:
                    if (!Functions.TryGet(c.Name, out var info))
                        throw new ExpressionException($"Bilinmeyen fonksiyon: {c.Name}", c.Position);
                    if (c.Arguments.Count < info.MinArgs || (info.MaxArgs >= 0 && c.Arguments.Count > info.MaxArgs))
                        throw new ExpressionException($"{c.Name} için argüman sayısı hatalı. Kullanım: {info.Signature}", c.Position);
                    foreach (var a in c.Arguments) ValidateCalls(a);
                    break;
            }
        }

        public override string ToString() => Text;
    }
}
