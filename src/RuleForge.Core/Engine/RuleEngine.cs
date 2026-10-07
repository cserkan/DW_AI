using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using RuleForge.Core.Expressions;
using RuleForge.Core.Rules;

namespace RuleForge.Core.Engine
{
    public sealed class EvaluationOptions
    {
        /// <summary>Onaylanmamış (önerilen) kurallar da çalıştırılsın mı? Önizleme/test için.</summary>
        public bool IncludeProposed { get; set; }
    }

    /// <summary>Modele uygulanacak tek bir değişiklik.</summary>
    public sealed class ModelAction
    {
        public ModelAction(Rule rule, Value value)
        {
            Rule = rule;
            Value = value;
        }

        public Rule Rule { get; }
        public RuleTarget Target => Rule.Target;
        public Value Value { get; }

        public override string ToString() => $"{Target} = {Value}";
    }

    public sealed class EvaluationResult
    {
        public Dictionary<string, Value> Values { get; } = new Dictionary<string, Value>(StringComparer.OrdinalIgnoreCase);
        public List<ModelAction> Actions { get; } = new List<ModelAction>();
        public List<string> Errors { get; } = new List<string>();
        public List<string> Warnings { get; } = new List<string>();

        /// <summary>Koşulu yanlış çıktığı için atlanan kurallar.</summary>
        public List<Rule> SkippedRules { get; } = new List<Rule>();

        public bool Success => Errors.Count == 0;
    }

    /// <summary>
    /// Deterministik kural motoru: aynı girdiler her zaman aynı eylemleri üretir.
    /// Yapay zekâ burada YOKTUR.
    /// </summary>
    public static class RuleEngine
    {
        public static EvaluationResult Evaluate(RuleSet ruleSet, IDictionary<string, Value>? inputs,
            EvaluationOptions? options = null)
        {
            options = options ?? new EvaluationOptions();
            var result = new EvaluationResult();
            inputs = inputs ?? new Dictionary<string, Value>();

            ResolveInputs(ruleSet, inputs, result);
            // Girdiler geçersizse devam etmek sadece zincirleme hata üretir.
            if (!result.Success) return result;
            EvaluateVariables(ruleSet, result);
            EvaluateRules(ruleSet, options, result);
            return result;
        }

        /// <summary>Girdi değerini tanımın tipine çevirir ve doğrular. Hata varsa mesaj döner.</summary>
        public static string? CoerceInput(InputDefinition def, Value raw, out Value value)
        {
            value = raw;
            try
            {
                switch (def.Type)
                {
                    case InputType.Number:
                    {
                        var d = raw.AsNumber();
                        if (def.Min.HasValue && d < def.Min.Value - Value.Epsilon)
                            return $"{def.Name} = {Value.FormatNumber(d)} en az {Value.FormatNumber(def.Min.Value)} olmalı.";
                        if (def.Max.HasValue && d > def.Max.Value + Value.Epsilon)
                            return $"{def.Name} = {Value.FormatNumber(d)} en fazla {Value.FormatNumber(def.Max.Value)} olmalı.";
                        value = Value.Number(d);
                        return null;
                    }
                    case InputType.Bool:
                        value = Value.Bool(raw.AsBool());
                        return null;
                    case InputType.Choice:
                    {
                        var s = raw.AsText();
                        var match = def.Options.FirstOrDefault(o => string.Equals(o, s, StringComparison.OrdinalIgnoreCase));
                        if (match == null)
                            return $"{def.Name} = \"{s}\" geçersiz. Seçenekler: {string.Join(", ", def.Options)}";
                        value = Value.Text(match);
                        return null;
                    }
                    default:
                        value = Value.Text(raw.AsText());
                        return null;
                }
            }
            catch (ExpressionException ex)
            {
                return $"{def.Name}: {ex.Message}";
            }
        }

        private static void ResolveInputs(RuleSet ruleSet, IDictionary<string, Value> inputs, EvaluationResult result)
        {
            foreach (var def in ruleSet.Inputs)
            {
                Value raw;
                if (!inputs.TryGetValue(def.Name, out raw))
                {
                    // Sözlük büyük/küçük harf duyarlıysa ikinci deneme
                    var key = inputs.Keys.FirstOrDefault(k => string.Equals(k, def.Name, StringComparison.OrdinalIgnoreCase));
                    if (key != null)
                    {
                        raw = inputs[key];
                    }
                    else if (def.Default != null)
                    {
                        raw = Value.Parse(def.Default);
                    }
                    else
                    {
                        result.Errors.Add($"Girdi eksik: {def.Name}");
                        continue;
                    }
                }

                var error = CoerceInput(def, raw, out var value);
                if (error != null)
                {
                    result.Errors.Add(error);
                    continue;
                }
                result.Values[def.Name] = value;
            }

            foreach (var name in inputs.Keys)
                if (ruleSet.FindInput(name) == null)
                    result.Warnings.Add($"Tanımsız girdi yok sayıldı: {name}");
        }

        private static void EvaluateVariables(RuleSet ruleSet, EvaluationResult result)
        {
            var parsed = new Dictionary<string, Expression>(StringComparer.OrdinalIgnoreCase);
            foreach (var v in ruleSet.Variables)
            {
                if (ruleSet.FindInput(v.Name) != null)
                {
                    result.Errors.Add($"'{v.Name}' hem girdi hem değişken olarak tanımlı.");
                    continue;
                }
                if (!Expression.TryParse(v.Expression, out var expr, out var error))
                {
                    result.Errors.Add($"Değişken {v.Name}: {error}");
                    continue;
                }
                parsed[v.Name] = expr!;
            }

            var order = TopologicalOrder(parsed, out var cycle);
            if (cycle != null)
            {
                result.Errors.Add("Döngüsel bağımlılık: " + string.Join(" → ", cycle));
                return;
            }

            var resolver = new DictionaryResolver(result.Values);
            foreach (var name in order)
            {
                try
                {
                    result.Values[name] = parsed[name].Evaluate(resolver);
                }
                catch (ExpressionException ex)
                {
                    result.Errors.Add($"Değişken {name}: {ex.Message}");
                }
            }
        }

        private static void EvaluateRules(RuleSet ruleSet, EvaluationOptions options, EvaluationResult result)
        {
            var resolver = new DictionaryResolver(result.Values);
            var active = ruleSet.Rules
                .Where(r => r.Status == RuleStatus.Approved || (options.IncludeProposed && r.Status == RuleStatus.Proposed))
                .ToList();

            // Aynı hedefe birden fazla kural: koşullarından en fazla biri doğru olabilir.
            foreach (var group in active.GroupBy(r => r.Target.Key))
            {
                ModelAction? chosen = null;
                foreach (var rule in group)
                {
                    try
                    {
                        if (!string.IsNullOrWhiteSpace(rule.Condition) &&
                            !Expression.Parse(rule.Condition!).Evaluate(resolver).AsBool())
                        {
                            result.SkippedRules.Add(rule);
                            continue;
                        }

                        var value = Coerce(rule.Target.ExpectedType, Expression.Parse(rule.Expression).Evaluate(resolver));
                        if (chosen != null)
                        {
                            result.Errors.Add($"Çakışma: '{chosen.Rule.Id}' ve '{rule.Id}' aynı hedefe ({rule.Target}) değer yazıyor.");
                            continue;
                        }
                        chosen = new ModelAction(rule, value);
                    }
                    catch (ExpressionException ex)
                    {
                        result.Errors.Add($"Kural {rule.Id}: {ex.Message}");
                    }
                }
                if (chosen != null) result.Actions.Add(chosen);
            }
        }

        public static Value Coerce(ExpectedType type, Value value)
        {
            switch (type)
            {
                case ExpectedType.Number:
                    if (value.Kind == ValueKind.Text)
                        throw new ExpressionException($"Sayı bekleniyordu, metin geldi: {value}");
                    var d = value.AsNumber();
                    if (double.IsNaN(d) || double.IsInfinity(d))
                        throw new ExpressionException("Sonuç geçerli bir sayı değil.");
                    return Value.Number(d);
                case ExpectedType.Bool:
                    return Value.Bool(value.AsBool());
                default:
                    return Value.Text(value.AsText());
            }
        }

        /// <summary>Değişkenleri bağımlılık sırasına dizer. Döngü varsa cycle dolar.</summary>
        internal static List<string> TopologicalOrder(Dictionary<string, Expression> parsed, out List<string>? cycle)
        {
            List<string>? found = null;
            var order = new List<string>();
            var state = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase); // 1 = ziyarette, 2 = bitti
            var stack = new List<string>();

            bool Visit(string name)
            {
                if (state.TryGetValue(name, out var s))
                {
                    if (s == 2) return true;
                    var start = stack.FindIndex(x => string.Equals(x, name, StringComparison.OrdinalIgnoreCase));
                    var loop = stack.Skip(start).ToList();
                    loop.Add(name);
                    found = loop;
                    return false;
                }
                state[name] = 1;
                stack.Add(name);
                foreach (var dep in parsed[name].GetIdentifiers().OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
                    if (parsed.ContainsKey(dep) && !Visit(dep))
                        return false;
                stack.RemoveAt(stack.Count - 1);
                state[name] = 2;
                order.Add(name);
                return true;
            }

            foreach (var name in parsed.Keys.ToList())
                if (!Visit(name))
                    break;
            cycle = found;
            return order;
        }

        /// <summary>Komut satırı/CSV'den gelen "Ad=Değer" çiftlerini ayrıştırır.</summary>
        public static Dictionary<string, Value> ParseAssignments(IEnumerable<string> pairs)
        {
            var dict = new Dictionary<string, Value>(StringComparer.OrdinalIgnoreCase);
            foreach (var p in pairs)
            {
                var idx = p.IndexOf('=');
                if (idx <= 0) throw new FormatException($"'Ad=Değer' bekleniyordu: {p}");
                dict[p.Substring(0, idx).Trim()] = Value.Parse(p.Substring(idx + 1));
            }
            return dict;
        }

        internal static string Format(double d) => d.ToString(CultureInfo.InvariantCulture);
    }
}
