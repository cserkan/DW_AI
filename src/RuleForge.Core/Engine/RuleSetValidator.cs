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
                if (input.Type == InputType.Choice && input.Options.Count == 0)
                    issues.Add(Error(input.Name, "Seçim tipindeki girdinin seçenekleri (options) boş."));
                if (input.Min.HasValue && input.Max.HasValue && input.Min > input.Max)
                    issues.Add(Error(input.Name, "Min, Max'tan büyük."));
                if (input.Default != null)
                {
                    var err = RuleEngine.CoerceInput(input, Value.Parse(input.Default), out _);
                    if (err != null) issues.Add(Error(input.Name, "Varsayılan değer geçersiz: " + err));
                }
                else
                {
                    issues.Add(Warning(input.Name, "Varsayılan değer yok."));
                }
            }

            foreach (var v in ruleSet.Variables)
            {
                CheckName(v.Name, "Değişken", symbols, issues);
            }

            foreach (var v in ruleSet.Variables)
                CheckExpression(v.Name, v.Expression, symbols, issues, "formül");

            var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var rule in ruleSet.Rules)
            {
                var id = string.IsNullOrWhiteSpace(rule.Id) ? "<kimliksiz kural>" : rule.Id;
                if (string.IsNullOrWhiteSpace(rule.Id)) issues.Add(Error(id, "Kural kimliği (id) boş."));
                else if (!ids.Add(rule.Id)) issues.Add(Error(id, "Aynı kimlikli birden fazla kural var."));

                CheckExpression(id, rule.Expression, symbols, issues, "formül");
                if (!string.IsNullOrWhiteSpace(rule.Condition))
                    CheckExpression(id, rule.Condition!, symbols, issues, "koşul");
                CheckTarget(id, rule.Target, snapshot, issues);
            }

            foreach (var group in ruleSet.Rules.Where(r => r.Status != RuleStatus.Rejected).GroupBy(r => r.Target.Key))
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

        /// <summary>Varsayılanlarla ve her sayısal girdinin min/max uçlarıyla deneme çalıştırması.</summary>
        private static void DryRun(RuleSet ruleSet, List<ValidationIssue> issues)
        {
            var options = new EvaluationOptions { IncludeProposed = true };
            var cases = new List<(string label, Dictionary<string, Value> inputs)>
            {
                ("varsayılanlar", new Dictionary<string, Value>(StringComparer.OrdinalIgnoreCase)),
            };
            foreach (var input in ruleSet.Inputs)
            {
                if (input.Type == InputType.Number)
                {
                    if (input.Min.HasValue)
                        cases.Add(($"{input.Name}={Value.FormatNumber(input.Min.Value)}", One(input.Name, Value.Number(input.Min.Value))));
                    if (input.Max.HasValue)
                        cases.Add(($"{input.Name}={Value.FormatNumber(input.Max.Value)}", One(input.Name, Value.Number(input.Max.Value))));
                }
                else if (input.Type == InputType.Choice)
                {
                    foreach (var o in input.Options)
                        cases.Add(($"{input.Name}={o}", One(input.Name, Value.Text(o))));
                }
                else if (input.Type == InputType.Bool)
                {
                    cases.Add(($"{input.Name}=true", One(input.Name, Value.Bool(true))));
                    cases.Add(($"{input.Name}=false", One(input.Name, Value.Bool(false))));
                }
            }

            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var (label, inputs) in cases)
            {
                var r = RuleEngine.Evaluate(ruleSet, inputs, options);
                foreach (var e in r.Errors)
                    if (seen.Add(e))
                        issues.Add(Warning("deneme", $"[{label}] {e}"));
            }
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

        private static void CheckTarget(string id, RuleTarget target, ModelSnapshot? snapshot, List<ValidationIssue> issues)
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
                var key = string.IsNullOrWhiteSpace(target.Document) ? snapshot.RootDocument : target.Document!;
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
                    if (snapshot.FindComponent(target.Component!) == null)
                        issues.Add(Error(id, $"Montajda '{target.Component}' bileşeni yok."));
                    break;
                case TargetKind.Configuration:
                    if (target.Component != null && snapshot.FindComponent(target.Component) == null)
                        issues.Add(Error(id, $"Montajda '{target.Component}' bileşeni yok."));
                    break;
            }
        }

        private static ValidationIssue Error(string item, string msg) => new ValidationIssue(IssueSeverity.Error, item, msg);

        private static ValidationIssue Warning(string item, string msg) => new ValidationIssue(IssueSeverity.Warning, item, msg);
    }
}
