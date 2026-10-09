using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using RuleForge.Core.Expressions;
using RuleForge.Core.Model;
using RuleForge.Core.Rules;

namespace RuleForge.Core.Engine
{
    public enum IssueSeverity
    {
        Error,
        Warning,
    }

    public sealed class ValidationIssue
    {
        public ValidationIssue(IssueSeverity severity, string item, string message)
        {
            Severity = severity;
            Item = item;
            Message = message;
        }

        public IssueSeverity Severity { get; }

        /// <summary>Sorunlu öğe: girdi/değişken adı veya kural kimliği.</summary>
        public string Item { get; }

        public string Message { get; }

        public override string ToString() => $"[{(Severity == IssueSeverity.Error ? "HATA" : "UYARI")}] {Item}: {Message}";
    }

    /// <summary>
    /// Kural setini statik olarak (ve varsayılan/uç girdilerle deneme çalıştırarak) denetler.
    /// Yapay zekânın ürettiği her değişiklik buradan geçer; hatalar YZ'ye geri bildirilir.
    /// </summary>
    public static class RuleSetValidator
    {
        private static readonly Regex IdentifierPattern = new Regex(@"^[\p{L}_][\p{L}\p{Nd}_]*$", RegexOptions.Compiled);

        public static List<ValidationIssue> Validate(RuleSet ruleSet, ModelSnapshot? snapshot = null)
        {
            var issues = new List<ValidationIssue>();
            var symbols = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var input in ruleSet.Inputs)
            {
                CheckName(input.Name, "Girdi", symbols, issues);
                CheckInput(input, input.Name, issues);
            }

            // Tablolar: sütunlar sadece kapsamlı formüllerde, özetler (adet, ilk, toplam...) her yerde görünür.
            var tableNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var scoped = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (var table in ruleSet.Tables)
            {
                CheckName(table.Name, "Tablo", tableNames, issues);
                if (table.MinRows.HasValue && table.MaxRows.HasValue && table.MinRows > table.MaxRows)
                    issues.Add(Error(table.Name, "En az satır sayısı en fazladan büyük."));
                if (table.Columns.Count == 0)
                    issues.Add(Warning(table.Name, "Tablonun sütunu yok; sadece satır sayısı girilebilir."));
                var own = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var col in table.Columns)
                {
                    CheckName(col.Name, "Sütun", symbols, issues);
                    CheckInput(col, $"{table.Name}.{col.Name}", issues);
                    own.Add(col.Name);
                }
                own.Add(TableSymbols.Index(table));
                scoped[table.Name] = own;
            }
            foreach (var table in ruleSet.Tables)
                foreach (var name in TableSymbols.Aggregates(table).Concat(new[] { TableSymbols.Index(table) }))
                    if (!symbols.Add(name))
                        issues.Add(Error(name, $"'{name}' adı {table.Name} tablosunun özet adıyla çakışıyor; başka bir ad seçin."));
            // Satır no sadece kapsamlı formüllerde görünür; genel sembollerden çıkar.
            foreach (var table in ruleSet.Tables) symbols.Remove(TableSymbols.Index(table));

            foreach (var v in ruleSet.Variables)
            {
                if (!string.IsNullOrEmpty(v.Scope) && !scoped.ContainsKey(v.Scope!))
                    issues.Add(Error(v.Name, $"Kapsam '{v.Scope}' adında bir tablo yok."));
                CheckName(v.Name, "Değişken", symbols, issues);
            }
            foreach (var v in ruleSet.Variables.Where(v => !string.IsNullOrEmpty(v.Scope)))
                if (scoped.TryGetValue(v.Scope!, out var own)) own.Add(v.Name);
            // Tablo sütunları ve kapsamlı değişkenler sadece kendi tablolarının (satır) formüllerinde görünür.
            var global = new HashSet<string>(symbols, StringComparer.OrdinalIgnoreCase);
            foreach (var own in scoped.Values) global.ExceptWith(own);

            HashSet<string> Visible(string? scope)
            {
                if (string.IsNullOrEmpty(scope) || !scoped.TryGetValue(scope!, out var own)) return global;
                var set = new HashSet<string>(global, StringComparer.OrdinalIgnoreCase);
                set.UnionWith(own);
                return set;
            }

            foreach (var v in ruleSet.Variables)
                CheckExpression(v.Name, v.Expression, Visible(v.Scope), issues, "formül");

            var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var rule in ruleSet.Rules)
            {
                var id = string.IsNullOrWhiteSpace(rule.Id) ? "<kimliksiz kural>" : rule.Id;
                if (string.IsNullOrWhiteSpace(rule.Id)) issues.Add(Error(id, "Kural kimliği (id) boş."));
                else if (!ids.Add(rule.Id)) issues.Add(Error(id, "Aynı kimlikli birden fazla kural var."));

                var table = ruleSet.FindTable(rule.Scope);
                if (!string.IsNullOrEmpty(rule.Scope) && table == null)
                    issues.Add(Error(id, $"Kapsam '{rule.Scope}' adında bir tablo yok."));
                var visible = Visible(rule.Scope);
                CheckExpression(id, rule.Expression, visible, issues, "formül");
                if (!string.IsNullOrWhiteSpace(rule.Condition))
                    CheckExpression(id, rule.Condition!, visible, issues, "koşul");
                CheckTarget(id, rule.Target, snapshot, table, issues);
            }

            foreach (var group in ruleSet.Rules.Where(r => r.Status != RuleStatus.Rejected)
                         .GroupBy(r => (r.Scope ?? string.Empty).ToLowerInvariant() + "|" + r.Target.Key))
            {
                var list = group.ToList();
                if (list.Count > 1 && list.Count(r => string.IsNullOrWhiteSpace(r.Condition)) > 0)
                    issues.Add(Error(string.Join(", ", list.Select(r => r.Id)),
                        $"Aynı hedefe ({list[0].Target}) birden fazla kural yazıyor; ayırt edici 'condition' gerekli ya da tek kuralda IF kullanın."));
            }

            if (issues.All(i => i.Severity != IssueSeverity.Error))
                DryRun(ruleSet, issues);

            return issues;
        }

        private static void CheckInput(InputDefinition input, string item, List<ValidationIssue> issues)
        {
            if (input.Type == InputType.Choice && input.Options.Count == 0)
                issues.Add(Error(item, "Seçim tipindeki girdinin seçenekleri (options) boş."));
            if (input.Min.HasValue && input.Max.HasValue && input.Min > input.Max)
                issues.Add(Error(item, "Min, Max'tan büyük."));
            if (input.Default != null)
            {
                var err = RuleEngine.CoerceInput(input, Value.Parse(input.Default), out _);
                if (err != null) issues.Add(Error(item, "Varsayılan değer geçersiz: " + err));
            }
            else
            {
                issues.Add(Warning(item, "Varsayılan değer yok."));
            }
        }

        /// <summary>Varsayılanlarla ve her sayısal girdinin (ve tablo sütununun) min/max uçlarıyla deneme çalıştırması.</summary>
        private static void DryRun(RuleSet ruleSet, List<ValidationIssue> issues)
        {
            var options = new EvaluationOptions { IncludeProposed = true };
            var cases = new List<(string label, Dictionary<string, Value> inputs, Dictionary<string, List<Dictionary<string, Value>>> tables)>
            {
                ("varsayılanlar", new Dictionary<string, Value>(StringComparer.OrdinalIgnoreCase), Rows(ruleSet, null, Value.Null)),
            };
            void Add(string label, string name, Value v) =>
                cases.Add((label, One(name, v), Rows(ruleSet, null, Value.Null)));
            void AddColumn(string label, string column, Value v) =>
                cases.Add((label, new Dictionary<string, Value>(StringComparer.OrdinalIgnoreCase), Rows(ruleSet, column, v)));

            foreach (var input in ruleSet.Inputs)
            {
                if (input.Type == InputType.Number)
                {
                    if (input.Min.HasValue) Add($"{input.Name}={Value.FormatNumber(input.Min.Value)}", input.Name, Value.Number(input.Min.Value));
                    if (input.Max.HasValue) Add($"{input.Name}={Value.FormatNumber(input.Max.Value)}", input.Name, Value.Number(input.Max.Value));
                }
                else if (input.Type == InputType.Choice)
                {
                    foreach (var o in input.Options) Add($"{input.Name}={o}", input.Name, Value.Text(o));
                }
                else if (input.Type == InputType.Bool)
                {
                    Add($"{input.Name}=true", input.Name, Value.Bool(true));
                    Add($"{input.Name}=false", input.Name, Value.Bool(false));
                }
            }
            foreach (var table in ruleSet.Tables)
            foreach (var col in table.Columns)
            {
                var label = table.Name + "." + col.Name;
                if (col.Type == InputType.Number)
                {
                    if (col.Min.HasValue) AddColumn($"{label}={Value.FormatNumber(col.Min.Value)}", col.Name, Value.Number(col.Min.Value));
                    if (col.Max.HasValue) AddColumn($"{label}={Value.FormatNumber(col.Max.Value)}", col.Name, Value.Number(col.Max.Value));
                }
                else if (col.Type == InputType.Choice)
                {
                    foreach (var o in col.Options) AddColumn($"{label}={o}", col.Name, Value.Text(o));
                }
            }

            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var (label, inputs, tables) in cases)
            {
                var r = RuleEngine.Evaluate(ruleSet, inputs, options, tables);
                foreach (var e in r.Errors)
                    if (seen.Add(e))
                        issues.Add(Warning("deneme", $"[{label}] {e}"));
            }
        }

        /// <summary>Deneme için tablo satırları: en az satır sayısı kadar (en az 1), sütunlar varsayılan; biri verilen değerde.</summary>
        private static Dictionary<string, List<Dictionary<string, Value>>> Rows(RuleSet ruleSet, string? column, Value value)
        {
            var tables = new Dictionary<string, List<Dictionary<string, Value>>>(StringComparer.OrdinalIgnoreCase);
            foreach (var table in ruleSet.Tables)
            {
                var count = Math.Max(1, table.MinRows ?? 1);
                var rows = new List<Dictionary<string, Value>>();
                for (int i = 0; i < count; i++)
                {
                    var row = new Dictionary<string, Value>(StringComparer.OrdinalIgnoreCase);
                    if (column != null && table.FindColumn(column) != null) row[column] = value;
                    rows.Add(row);
                }
                tables[table.Name] = rows;
            }
            return tables;
        }

        private static Dictionary<string, Value> One(string name, Value v) =>
            new Dictionary<string, Value>(StringComparer.OrdinalIgnoreCase) { [name] = v };

        private static void CheckName(string name, string what, HashSet<string> symbols, List<ValidationIssue> issues)
        {
            if (string.IsNullOrWhiteSpace(name) || !IdentifierPattern.IsMatch(name))
                issues.Add(Error(name ?? "", $"{what} adı geçersiz. Harf ile başlamalı; sadece harf, rakam ve _ içermeli."));
            else if (Functions.TryGet(name, out _) || string.Equals(name, "TRUE", StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(name, "FALSE", StringComparison.OrdinalIgnoreCase))
                issues.Add(Error(name, $"{what} adı ayrılmış bir kelime."));
            else if (!symbols.Add(name))
                issues.Add(Error(name, $"'{name}' adı birden fazla kez tanımlı."));
        }

        private static void CheckExpression(string item, string text, HashSet<string> symbols,
            List<ValidationIssue> issues, string what)
        {
            if (!Expression.TryParse(text ?? string.Empty, out var expr, out var error))
            {
                issues.Add(Error(item, $"{what} hatalı: {error}  →  {text}"));
                return;
            }
            foreach (var id in expr!.GetIdentifiers())
                if (!symbols.Contains(id))
                    issues.Add(Error(item, $"{what} tanımsız ad kullanıyor: '{id}'. Önce girdi veya değişken olarak tanımlanmalı."));
        }

        private static void CheckTarget(string id, RuleTarget target, ModelSnapshot? snapshot, TableDefinition? table,
            List<ValidationIssue> issues)
        {
            switch (target.Kind)
            {
                case TargetKind.Dimension:
                case TargetKind.GlobalVariable:
                case TargetKind.FeatureSuppression:
                case TargetKind.CustomProperty:
                    if (string.IsNullOrWhiteSpace(target.Name))
                    {
                        issues.Add(Error(id, $"{target.Kind} hedefi için 'name' gerekli."));
                        return;
                    }
                    break;
                case TargetKind.ComponentSuppression:
                case TargetKind.ComponentReplace:
                    if (string.IsNullOrWhiteSpace(target.Component))
                    {
                        issues.Add(Error(id, $"{target.Kind} hedefi için 'component' gerekli."));
                        return;
                    }
                    break;
            }

            if (snapshot == null) return;

            DocumentInfo? doc = null;
            if (target.Kind != TargetKind.ComponentSuppression && target.Kind != TargetKind.ComponentReplace &&
                target.Kind != TargetKind.OutputFileName && !(target.Kind == TargetKind.Configuration && target.Component != null))
            {
                // Kapsamlı kuralda boş dosya = modülün kendisi.
                var key = !string.IsNullOrWhiteSpace(target.Document) ? target.Document!
                    : table != null && !string.IsNullOrEmpty(table.Module) ? table.Module : snapshot.RootDocument;
                doc = snapshot.FindDocument(key);
                if (doc == null)
                {
                    issues.Add(Error(id, $"Modelde '{key}' dosyası yok."));
                    return;
                }
            }

            switch (target.Kind)
            {
                case TargetKind.Dimension:
                {
                    var dim = doc!.FindDimension(target.Name!);
                    if (dim == null) issues.Add(Error(id, $"'{doc.Key}' içinde '{target.Name}' ölçüsü yok."));
                    else if (dim.IsDriven) issues.Add(Error(id, $"'{target.Name}' referans (driven) ölçü; değiştirilemez."));
                    else if (dim.DrivenByEquation != null)
                        issues.Add(Warning(id, $"'{target.Name}' modelde bir denklemle sürülüyor ({dim.DrivenByEquation}); kural denklemle çakışabilir."));
                    break;
                }
                case TargetKind.GlobalVariable:
                    if (doc!.FindGlobalVariable(target.Name!) == null)
                        issues.Add(Error(id, $"'{doc.Key}' içinde '{target.Name}' global değişkeni yok."));
                    break;
                case TargetKind.FeatureSuppression:
                    if (doc!.FindFeature(target.Name!) == null)
                        issues.Add(Error(id, $"'{doc.Key}' içinde '{target.Name}' özelliği yok."));
                    break;
                case TargetKind.ComponentSuppression:
                case TargetKind.ComponentReplace:
                    if (snapshot.FindComponent(InModule(table, target.Component!)) == null)
                        issues.Add(Error(id, $"Montajda '{target.Component}' bileşeni yok."));
                    break;
                case TargetKind.Configuration:
                    if (target.Component != null && snapshot.FindComponent(InModule(table, target.Component)) == null)
                        issues.Add(Error(id, $"Montajda '{target.Component}' bileşeni yok."));
                    break;
            }
        }

        /// <summary>Kapsamlı kuralın bileşen yolu modülün içindedir; master'daki tam yol modül bileşeninin altındadır.</summary>
        private static string InModule(TableDefinition? table, string path) =>
            table == null || string.IsNullOrEmpty(table.ModuleComponent) ? path : table.ModuleComponent + "/" + path;

        private static ValidationIssue Error(string item, string msg) => new ValidationIssue(IssueSeverity.Error, item, msg);

        private static ValidationIssue Warning(string item, string msg) => new ValidationIssue(IssueSeverity.Warning, item, msg);
    }
}
