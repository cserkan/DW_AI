using System;

namespace RuleForge.Core.Expressions
{
    internal static class Evaluator
    {
        public static Value Evaluate(Node node, IVariableResolver resolver)
        {
            switch (node)
            {
                case LiteralNode lit:
                    return lit.Value;

                case IdentifierNode id:
                    if (resolver.TryResolve(id.Name, out var v)) return v;
                    throw new ExpressionException($"Tanımsız değişken: {id.Name}", id.Position);

                case UnaryNode u:
                {
                    var operand = Evaluate(u.Operand, resolver);
                    switch (u.Operator)
                    {
                        case "-": return Value.Number(-operand.AsNumber());
                        case "+": return Value.Number(operand.AsNumber());
                        case "!": return Value.Bool(!operand.AsBool());
                    }
                    throw new ExpressionException($"Bilinmeyen operatör {u.Operator}", u.Position);
                }

                case BinaryNode b:
                    return EvaluateBinary(b, resolver);

                case CallNode c:
                    return EvaluateCall(c, resolver);
            }
            throw new ExpressionException("Bilinmeyen düğüm tipi.");
        }

        private static Value EvaluateBinary(BinaryNode b, IVariableResolver resolver)
        {
            // Kısa devre
            if (b.Operator == "&&")
                return Value.Bool(Evaluate(b.Left, resolver).AsBool() && Evaluate(b.Right, resolver).AsBool());
            if (b.Operator == "||")
                return Value.Bool(Evaluate(b.Left, resolver).AsBool() || Evaluate(b.Right, resolver).AsBool());

            var l = Evaluate(b.Left, resolver);
            var r = Evaluate(b.Right, resolver);
            try
            {
                switch (b.Operator)
                {
                    case "+": return Value.Number(l.AsNumber() + r.AsNumber());
                    case "-": return Value.Number(l.AsNumber() - r.AsNumber());
                    case "*": return Value.Number(l.AsNumber() * r.AsNumber());
                    case "/":
                    {
                        var d = r.AsNumber();
                        if (Math.Abs(d) < Value.Epsilon) throw new ExpressionException("Sıfıra bölme.", b.Position);
                        return Value.Number(l.AsNumber() / d);
                    }
                    case "%":
                    {
                        var d = r.AsNumber();
                        if (Math.Abs(d) < Value.Epsilon) throw new ExpressionException("Sıfıra bölme (%).", b.Position);
                        var a = l.AsNumber();
                        return Value.Number(a - d * Math.Floor(a / d));
                    }
                    case "^": return Value.Number(Math.Pow(l.AsNumber(), r.AsNumber()));
                    case "&": return Value.Text(l.AsText() + r.AsText());
                    case "=": return Value.Bool(Value.LooseEquals(l, r));
                    case "<>": return Value.Bool(!Value.LooseEquals(l, r));
                    case "<": return Value.Bool(Value.Compare(l, r) < 0);
                    case "<=": return Value.Bool(Value.Compare(l, r) <= 0);
                    case ">": return Value.Bool(Value.Compare(l, r) > 0);
                    case ">=": return Value.Bool(Value.Compare(l, r) >= 0);
                }
            }
            catch (ExpressionException ex) when (ex.Position < 0)
            {
                throw new ExpressionException(ex.Message, b.Position);
            }
            throw new ExpressionException($"Bilinmeyen operatör {b.Operator}", b.Position);
        }

        private static Value EvaluateCall(CallNode c, IVariableResolver resolver)
        {
            if (!Functions.TryGet(c.Name, out var info))
                throw new ExpressionException($"Bilinmeyen fonksiyon: {c.Name}", c.Position);

            var args = c.Arguments;
            try
            {
                if (info.IsLazy)
                {
                    switch (c.Name)
                    {
                        case "IF":
                            if (Evaluate(args[0], resolver).AsBool()) return Evaluate(args[1], resolver);
                            return args.Count > 2 ? Evaluate(args[2], resolver) : Value.Bool(false);

                        case "IFS":
                            if (args.Count % 2 != 0)
                                throw new ExpressionException("IFS çift sayıda argüman ister (koşul, değer, ...).", c.Position);
                            for (int i = 0; i < args.Count; i += 2)
                                if (Evaluate(args[i], resolver).AsBool()) return Evaluate(args[i + 1], resolver);
                            throw new ExpressionException("IFS: hiçbir koşul sağlanmadı.", c.Position);

                        case "SWITCH":
                        {
                            var key = Evaluate(args[0], resolver);
                            int pairs = (args.Count - 1) / 2;
                            for (int i = 0; i < pairs; i++)
                                if (Value.LooseEquals(key, Evaluate(args[1 + 2 * i], resolver)))
                                    return Evaluate(args[2 + 2 * i], resolver);
                            if ((args.Count - 1) % 2 == 1) return Evaluate(args[args.Count - 1], resolver);
                            throw new ExpressionException($"SWITCH: {key} için eşleşme yok.", c.Position);
                        }

                        case "RANGELOOKUP":
                        {
                            var x = Evaluate(args[0], resolver).AsNumber();
                            int pairs = (args.Count - 1) / 2;
                            for (int i = 0; i < pairs; i++)
                                if (x <= Evaluate(args[1 + 2 * i], resolver).AsNumber() + Value.Epsilon)
                                    return Evaluate(args[2 + 2 * i], resolver);
                            if ((args.Count - 1) % 2 == 1) return Evaluate(args[args.Count - 1], resolver);
                            throw new ExpressionException($"RANGELOOKUP: {Value.FormatNumber(x)} tüm sınırların üstünde.", c.Position);
                        }

                        case "AND":
                            foreach (var a in args)
                                if (!Evaluate(a, resolver).AsBool()) return Value.Bool(false);
                            return Value.Bool(true);

                        case "OR":
                            foreach (var a in args)
                                if (Evaluate(a, resolver).AsBool()) return Value.Bool(true);
                            return Value.Bool(false);
                    }
                    throw new ExpressionException($"Lazy fonksiyon uygulanmamış: {c.Name}", c.Position);
                }

                var values = new Value[args.Count];
                for (int i = 0; i < args.Count; i++) values[i] = Evaluate(args[i], resolver);
                return info.Implementation!(values);
            }
            catch (ExpressionException ex) when (ex.Position < 0)
            {
                throw new ExpressionException($"{c.Name}: {ex.Message}", c.Position);
            }
        }
    }
}
