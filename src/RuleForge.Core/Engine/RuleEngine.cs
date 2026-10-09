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
        public ModelAction(Rule rule, Value value, int? instance = null)
        {
            Rule = rule;
            Value = value;
            Instance = instance;
        }

        public Rule Rule { get; }
        public RuleTarget Target => Rule.Target;
        public Value Value { get; }

        /// <summary>Tablo kapsamlı kuralda satır (modül kopyası) numarası, 1'den başlar; kapsamsız kuralda null.</summary>
        public int? Instance { get; }

        /// <summary>Kuralın tablosu (kapsamı); kapsamsız kuralda null.</summary>
        public string? Scope => Rule.Scope;

        /// <summary>Hedef + satır: aynı hedefin farklı kopyalardaki değerlerini ayırır.</summary>
        public string Key => Instance.HasValue ? Target.Key + "#" + Instance.Value.ToString(CultureInfo.InvariantCulture) : Target.Key;

        public override string ToString() => Instance.HasValue ? $"[{Scope} {Instance}] {Target} = {Value}" : $"{Target} = {Value}";
    }

    public sealed class EvaluationResult
    {
        public Dictionary<string, Value> Values { get; } = new Dictionary<string, Value>(StringComparer.OrdinalIgnoreCase);

        /// <summary>Tablo adı → her satırın kendi değerleri (sütunlar, satır no, kapsamlı değişkenler).</summary>
        public Dictionary<string, List<Dictionary<string, Value>>> Rows { get; } =
            new Dictionary<string, List<Dictionary<string, Value>>>(StringComparer.OrdinalIgnoreCase);

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
        /// <param name="tables">Tablo adı → satırlar (sütun adı → değer). Tablosu olmayan kural setlerinde boş bırakılır.</param>
        public static EvaluationResult Evaluate(RuleSet ruleSet, IDictionary<string, Value>? inputs,
            EvaluationOptions? options = null, IDictionary<string, List<Dictionary<string, Value>>>? tables = null)
        {
            options = options ?? new EvaluationOptions();
            var result = new EvaluationResult();
            inputs = inputs ?? new Dictionary<string, Value>();

            ResolveInputs(ruleSet, inputs, result);
            ResolveTables(ruleSet, tables, result);
            // Girdiler geçersizse devam etmek sadece zincirleme hata üretir.
            if (!result.Success) return result;

            foreach (var v in ruleSet.Variables.Where(v => !string.IsNullOrEmpty(v.Scope) && ruleSet.FindTable(v.Scope) == null))
                result.Errors.Add($"Değişken {v.Name}: '{v.Scope}' adında bir tablo yok.");
            var active = ruleSet.Rules
                .Where(r => r.Status == RuleStatus.Approved || (options.IncludeProposed && r.Status == RuleStatus.Proposed))
                .ToList();
            foreach (var r in active.Where(r => !string.IsNullOrEmpty(r.Scope) && ruleSet.FindTable(r.Scope) == null))
                result.Errors.Add($"Kural {r.Id}: '{r.Scope}' adında bir tablo yok.");

            EvaluateVariables(ruleSet, ruleSet.Variables.Where(v => string.IsNullOrEmpty(v.Scope)), result.Values, result.Errors, string.Empty);
            EvaluateRules(active.Where(r => string.IsNullOrEmpty(r.Scope)), result.Values, null, result, string.Empty);

            // Tekrarlanan modül: kapsamlı değişken ve kurallar her satır için ayrı çalışır.
            foreach (var table in ruleSet.Tables)
            {
                var vars = ruleSet.Variables.Where(v => Same(v.Scope, table.Name)).ToList();
                var rules = active.Where(r => Same(r.Scope, table.Name)).ToList();
                var rows = result.Rows[table.Name];
                for (int i = 0; i < rows.Count; i++)
                {
                    var values = new Dictionary<string, Value>(result.Values, StringComparer.OrdinalIgnoreCase);
                    foreach (var kv in rows[i]) values[kv.Key] = kv.Value;
                    values[TableSymbols.Index(table)] = Value.Number(i + 1);
                    var prefix = $"[{table.Name} {i + 1}] ";
                    EvaluateVariables(ruleSet, vars, values, result.Errors, prefix);
                    EvaluateRules(rules, values, i + 1, result, prefix);
                    rows[i][TableSymbols.Index(table)] = Value.Number(i + 1);
                    foreach (var v in vars)
                        if (values.TryGetValue(v.Name, out var value)) rows[i][v.Name] = value;
                }
            }
            return result;
        }

        private static bool Same(string? a, string? b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

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

        /// <summary>Tablo satırlarını doğrular ve tüm formüllerin görebileceği özet değerleri (adet, ilk, toplam...) hesaplar.</summary>
        private static void ResolveTables(RuleSet ruleSet, IDictionary<string, List<Dictionary<string, Value>>>? tables,
            EvaluationResult result)
        {
            foreach (var table in ruleSet.Tables)
            {
                List<Dictionary<string, Value>>? raw = null;
                if (tables != null)
                {
                    var key = tables.Keys.FirstOrDefault(k => Same(k, table.Name));
                    if (key != null) raw = tables[key];
                }
                raw = raw ?? new List<Dictionary<string, Value>>();
                if (table.MinRows.HasValue && raw.Count < table.MinRows.Value)
                    result.Errors.Add($"Tablo {table.Name}: en az {table.MinRows} satır gerekli, {raw.Count} verildi.");
                if (table.MaxRows.HasValue && raw.Count > table.MaxRows.Value)
                    result.Errors.Add($"Tablo {table.Name}: en fazla {table.MaxRows} satır olabilir, {raw.Count} verildi.");

                var rows = new List<Dictionary<string, Value>>();
                for (int i = 0; i < raw.Count; i++)
                {
                    var row = new Dictionary<string, Value>(StringComparer.OrdinalIgnoreCase);
                    foreach (var col in table.Columns)
                    {
                        var key = raw[i].Keys.FirstOrDefault(k => Same(k, col.Name));
                        Value value;
                        if (key != null) value = raw[i][key];
                        else if (col.Default != null) value = Value.Parse(col.Default);
                        else
                        {
                            result.Errors.Add($"Tablo {table.Name} satır {i + 1}: {col.Name} eksik.");
                            continue;
                        }
                        var error = CoerceInput(col, value, out var coerced);
                        if (error != null) result.Errors.Add($"Tablo {table.Name} satır {i + 1}: {error}");
                        else row[col.Name] = coerced;
                    }
                    foreach (var k in raw[i].Keys.Where(k => table.FindColumn(k) == null))
                        result.Warnings.Add($"Tablo {table.Name}: tanımsız sütun yok sayıldı: {k}");
                    rows.Add(row);
                }
                result.Rows[table.Name] = rows;

                result.Values[TableSymbols.Count(table)] = Value.Number(rows.Count);
                foreach (var col in table.Columns)
                {
                    var vals = rows.Where(r => r.ContainsKey(col.Name)).Select(r => r[col.Name]).ToList();
                    result.Values[TableSymbols.First(col.Name)] = vals.Count > 0 ? vals[0] : Value.Null;
                    result.Values[TableSymbols.Last(col.Name)] = vals.Count > 0 ? vals[vals.Count - 1] : Value.Null;
                    if (col.Type != InputType.Number) continue;
                    var nums = vals.Select(v => v.AsNumber()).ToList();
                    result.Values[TableSymbols.Sum(col.Name)] = Value.Number(nums.Sum());
                    result.Values[TableSymbols.Max(col.Name)] = nums.Count > 0 ? Value.Number(nums.Max()) : Value.Null;
                    result.Values[TableSymbols.Min(col.Name)] = nums.Count > 0 ? Value.Number(nums.Min()) : Value.Null;
                }
            }

            if (tables != null)
                foreach (var name in tables.Keys.Where(k => ruleSet.FindTable(k) == null))
                    result.Warnings.Add($"Tanımsız tablo yok sayıldı: {name}");
        }

        private static void EvaluateVariables(RuleSet ruleSet, IEnumerable<VariableDefinition> variables,
            Dictionary<string, Value> values, List<string> errors, string prefix)
        {
            var parsed = new Dictionary<string, Expression>(StringComparer.OrdinalIgnoreCase);
            foreach (var v in variables)
            {
                if (ruleSet.FindInput(v.Name) != null)
                {
                    errors.Add($"{prefix}'{v.Name}' hem girdi hem değişken olarak tanımlı.");
                    continue;
                }
                if (!Expression.TryParse(v.Expression, out var expr, out var error))
                {
                    errors.Add($"{prefix}Değişken {v.Name}: {error}");
                    continue;
                }
                parsed[v.Name] = expr!;
            }

            var order = TopologicalOrder(parsed, out var cycle);
            if (cycle != null)
            {
                errors.Add(prefix + "Döngüsel bağımlılık: " + string.Join(" → ", cycle));
                return;
            }

            var resolver = new DictionaryResolver(values);
            foreach (var name in order)
            {
                try
                {
                    values[name] = parsed[name].Evaluate(resolver);
                }
                catch (ExpressionException ex)
                {
                    errors.Add($"{prefix}Değişken {name}: {ex.Message}");
                }
            }
        }

        private static void EvaluateRules(IEnumerable<Rule> active, Dictionary<string, Value> values, int? instance,
            EvaluationResult result, string prefix)
        {
            var resolver = new DictionaryResolver(values);

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
                            result.Errors.Add($"{prefix}Çakışma: '{chosen.Rule.Id}' ve '{rule.Id}' aynı hedefe ({rule.Target}) değer yazıyor.");
                            continue;
                        }
                        chosen = new ModelAction(rule, value, instance);
                    }
                    catch (ExpressionException ex)
                    {
                        result.Errors.Add($"{prefix}Kural {rule.Id}: {ex.Message}");
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

        /// <summary>
        /// "Ad=Değer" çiftlerini ayrıştırır; "Tablo.Sütun=v1;v2;v3" biçimindekiler tablo satırlarıdır
        /// (ör. "Bolumler.Uzunluk=1200;800;600" üç bölüm). Aynı tablonun sütunları aynı sayıda değer içermeli.
        /// </summary>
        public static Dictionary<string, Value> ParseAssignments(IEnumerable<string> pairs,
            out Dictionary<string, List<Dictionary<string, Value>>> tables)
        {
            var scalars = new List<string>();
            tables = new Dictionary<string, List<Dictionary<string, Value>>>(StringComparer.OrdinalIgnoreCase);
            foreach (var p in pairs)
            {
                var idx = p.IndexOf('=');
                var dot = idx > 0 ? p.LastIndexOf('.', idx) : -1;
                if (dot <= 0)
                {
                    scalars.Add(p);
                    continue;
                }
                var table = p.Substring(0, dot).Trim();
                var column = p.Substring(dot + 1, idx - dot - 1).Trim();
                var cells = p.Substring(idx + 1).Split(';');
                if (!tables.TryGetValue(table, out var rows)) tables[table] = rows = new List<Dictionary<string, Value>>();
                if (rows.Count > 0 && rows.Count != cells.Length)
                    throw new FormatException($"{table} tablosunun sütunları farklı sayıda değer içeriyor ({rows.Count} / {cells.Length}): {p}");
                while (rows.Count < cells.Length) rows.Add(new Dictionary<string, Value>(StringComparer.OrdinalIgnoreCase));
                for (int i = 0; i < cells.Length; i++) rows[i][column] = Value.Parse(cells[i]);
            }
            return ParseAssignments(scalars);
        }

        internal static string Format(double d) => d.ToString(CultureInfo.InvariantCulture);
    }
}
