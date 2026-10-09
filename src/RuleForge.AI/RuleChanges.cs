using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using RuleForge.Core.Rules;

namespace RuleForge.AI
{
    /// <summary>
    /// Yapay zekânın her turda döndürdüğü yapılandırılmış yanıt. JSON şeması
    /// <see cref="Schema"/> ile API'ye verilir (structured outputs), böylece çıktı her zaman ayrıştırılabilir.
    /// </summary>
    public sealed class RuleChanges
    {
        [JsonPropertyName("reply")] public string Reply { get; set; } = string.Empty;
        [JsonPropertyName("questions")] public List<string> Questions { get; set; } = new List<string>();
        [JsonPropertyName("upsert_inputs")] public List<InputChange> UpsertInputs { get; set; } = new List<InputChange>();
        [JsonPropertyName("upsert_variables")] public List<VariableChange> UpsertVariables { get; set; } = new List<VariableChange>();
        [JsonPropertyName("upsert_rules")] public List<RuleChange> UpsertRules { get; set; } = new List<RuleChange>();
        [JsonPropertyName("remove")] public List<RemoveChange> Remove { get; set; } = new List<RemoveChange>();

        public bool IsEmpty => UpsertInputs.Count == 0 && UpsertVariables.Count == 0 && UpsertRules.Count == 0 && Remove.Count == 0;

        public static RuleChanges Parse(string json)
        {
            return JsonSerializer.Deserialize<RuleChanges>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                   ?? throw new JsonException("Boş yanıt.");
        }

        /// <summary>
        /// Değişiklikleri kural setine uygular. Yeni/değişen kurallar her zaman "Proposed" olur:
        /// onay sadece kullanıcıdan gelir. Dönüş: insan okunur değişiklik listesi.
        /// </summary>
        public List<string> ApplyTo(RuleSet set)
        {
            var log = new List<string>();
            foreach (var r in Remove)
            {
                bool removed;
                switch (r.Kind)
                {
                    case "input": removed = set.Inputs.RemoveAll(i => Same(i.Name, r.Name)) > 0; break;
                    case "variable": removed = set.Variables.RemoveAll(v => Same(v.Name, r.Name)) > 0; break;
                    default: removed = set.Rules.RemoveAll(x => Same(x.Id, r.Name)) > 0; break;
                }
                if (removed) log.Add($"- {r.Kind} silindi: {r.Name}");
            }

            foreach (var c in UpsertInputs)
            {
                var def = new InputDefinition
                {
                    Name = c.Name, Label = string.IsNullOrWhiteSpace(c.Label) ? c.Name : c.Label!, Type = ParseType(c.Type),
                    Unit = c.Unit, Min = c.Min, Max = c.Max, Step = c.Step, Default = c.Default,
                    Options = c.Options ?? new List<string>(), Description = c.Description,
                };
                var idx = set.Inputs.FindIndex(i => Same(i.Name, c.Name));
                if (idx >= 0) { set.Inputs[idx] = def; log.Add($"~ girdi güncellendi: {c.Name}"); }
                else { set.Inputs.Add(def); log.Add($"+ girdi eklendi: {c.Name}"); }
            }

            foreach (var c in UpsertVariables)
            {
                var idx = set.Variables.FindIndex(v => Same(v.Name, c.Name));
                // scope null = mevcut kapsam korunur (yeni değişkende kapsamsız); "" = kapsamı kaldır.
                var scope = c.Scope == null ? (idx >= 0 ? set.Variables[idx].Scope : null) : Blank(c.Scope);
                var def = new VariableDefinition { Name = c.Name, Expression = c.Expression, Description = c.Description, Scope = scope };
                if (idx >= 0) { set.Variables[idx] = def; log.Add($"~ değişken güncellendi: {c.Name} = {c.Expression}"); }
                else { set.Variables.Add(def); log.Add($"+ değişken eklendi: {c.Name} = {c.Expression}"); }
            }

            foreach (var c in UpsertRules)
            {
                var existing = set.FindRule(c.Id);
                var rule = new Rule
                {
                    Id = c.Id,
                    Description = c.Description ?? string.Empty,
                    Target = new RuleTarget
                    {
                        Kind = ParseKind(c.Target.Kind),
                        Document = Blank(c.Target.Document),
                        Name = Blank(c.Target.Name),
                        Component = Blank(c.Target.Component),
                    },
                    Expression = c.Expression,
                    Condition = Blank(c.Condition),
                    Scope = c.Scope == null ? existing?.Scope : Blank(c.Scope),
                    Status = RuleStatus.Proposed,
                    Source = existing?.Source == RuleSource.Inference ? RuleSource.Inference : RuleSource.Chat,
                    Confidence = existing?.Confidence,
                    Evidence = c.Evidence ?? existing?.Evidence,
                };
                if (existing != null)
                {
                    set.Rules[set.Rules.IndexOf(existing)] = rule;
                    log.Add($"~ kural güncellendi: {c.Id}: {rule.Target} = {c.Expression}");
                }
                else
                {
                    set.Rules.Add(rule);
                    log.Add($"+ kural eklendi: {c.Id}: {rule.Target} = {c.Expression}");
                }
            }
            return log;
        }

        private static bool Same(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

        private static string? Blank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s;

        private static InputType ParseType(string s)
        {
            switch ((s ?? "").ToLowerInvariant())
            {
                case "text": return InputType.Text;
                case "bool": return InputType.Bool;
                case "choice": return InputType.Choice;
                default: return InputType.Number;
            }
        }

        internal static readonly Dictionary<string, TargetKind> KindNames = new Dictionary<string, TargetKind>(StringComparer.OrdinalIgnoreCase)
        {
            ["dimension"] = TargetKind.Dimension,
            ["globalVariable"] = TargetKind.GlobalVariable,
            ["featureSuppression"] = TargetKind.FeatureSuppression,
            ["componentSuppression"] = TargetKind.ComponentSuppression,
            ["componentReplace"] = TargetKind.ComponentReplace,
            ["configuration"] = TargetKind.Configuration,
            ["customProperty"] = TargetKind.CustomProperty,
            ["outputFileName"] = TargetKind.OutputFileName,
        };

        private static TargetKind ParseKind(string s) =>
            KindNames.TryGetValue(s ?? "", out var k) ? k : throw new FormatException($"Bilinmeyen hedef türü: {s}");

        /// <summary>Structured outputs için JSON şeması (tüm alanlar zorunlu, boş değerler null).</summary>
        public static Dictionary<string, JsonElement> Schema()
        {
            object NullableString() => new { type = new[] { "string", "null" } };
            object NullableNumber() => new { type = new[] { "number", "null" } };
            object StringArray() => new { type = "array", items = new { type = "string" } };

            var input = new
            {
                type = "object",
                additionalProperties = false,
                required = new[] { "name", "label", "type", "unit", "min", "max", "step", "default", "options", "description" },
                properties = new Dictionary<string, object>
                {
                    ["name"] = new { type = "string", description = "Formüllerde kullanılan ad: harfle başlar, boşluksuz." },
                    ["label"] = new { type = "string" },
                    ["type"] = new { type = "string", @enum = new[] { "number", "text", "bool", "choice" } },
                    ["unit"] = NullableString(),
                    ["min"] = NullableNumber(),
                    ["max"] = NullableNumber(),
                    ["step"] = NullableNumber(),
                    ["default"] = new { type = new[] { "string", "null" }, description = "Metin olarak: \"1200\", \"true\", \"Sol\"" },
                    ["options"] = StringArray(),
                    ["description"] = NullableString(),
                },
            };
            var variable = new
            {
                type = "object",
                additionalProperties = false,
                required = new[] { "name", "expression", "description", "scope" },
                properties = new Dictionary<string, object>
                {
                    ["name"] = new { type = "string" },
                    ["expression"] = new { type = "string" },
                    ["description"] = NullableString(),
                    ["scope"] = new { type = new[] { "string", "null" }, description = "Tablo adı: her satır için ayrı hesaplanır. null = mevcut kapsam korunur." },
                },
            };
            var rule = new
            {
                type = "object",
                additionalProperties = false,
                required = new[] { "id", "description", "target", "expression", "condition", "scope", "evidence" },
                properties = new Dictionary<string, object>
                {
                    ["id"] = new { type = "string", description = "Kararlı kimlik; mevcut kuralı güncellemek için aynı id." },
                    ["description"] = NullableString(),
                    ["target"] = new
                    {
                        type = "object",
                        additionalProperties = false,
                        required = new[] { "kind", "document", "name", "component" },
                        properties = new Dictionary<string, object>
                        {
                            ["kind"] = new { type = "string", @enum = KindNames.Keys.ToArray() },
                            ["document"] = NullableString(),
                            ["name"] = NullableString(),
                            ["component"] = NullableString(),
                        },
                    },
                    ["expression"] = new { type = "string" },
                    ["condition"] = NullableString(),
                    ["scope"] = new { type = new[] { "string", "null" }, description = "Tablo adı: kural tekrarlanan modülün her kopyası (satır) için çalışır. null = mevcut kapsam korunur; \"\" = kapsamsız." },
                    ["evidence"] = new { type = new[] { "string", "null" }, description = "Kuralın dayanağı: kullanıcının sözü, model verisi vb." },
                },
            };
            var remove = new
            {
                type = "object",
                additionalProperties = false,
                required = new[] { "kind", "name" },
                properties = new Dictionary<string, object>
                {
                    ["kind"] = new { type = "string", @enum = new[] { "input", "variable", "rule" } },
                    ["name"] = new { type = "string", description = "Girdi/değişken adı veya kural id" },
                },
            };

            var root = new Dictionary<string, object>
            {
                ["type"] = "object",
                ["additionalProperties"] = false,
                ["required"] = new[] { "reply", "questions", "upsert_inputs", "upsert_variables", "upsert_rules", "remove" },
                ["properties"] = new Dictionary<string, object>
                {
                    ["reply"] = new { type = "string", description = "Kullanıcıya Türkçe, kısa yanıt." },
                    ["questions"] = StringArray(),
                    ["upsert_inputs"] = new { type = "array", items = input },
                    ["upsert_variables"] = new { type = "array", items = variable },
                    ["upsert_rules"] = new { type = "array", items = rule },
                    ["remove"] = new { type = "array", items = remove },
                },
            };
            return root.ToDictionary(kv => kv.Key, kv => JsonSerializer.SerializeToElement(kv.Value));
        }
    }

    public sealed class InputChange
    {
        [JsonPropertyName("name")] public string Name { get; set; } = string.Empty;
        [JsonPropertyName("label")] public string? Label { get; set; }
        [JsonPropertyName("type")] public string Type { get; set; } = "number";
        [JsonPropertyName("unit")] public string? Unit { get; set; }
        [JsonPropertyName("min")] public double? Min { get; set; }
        [JsonPropertyName("max")] public double? Max { get; set; }
        [JsonPropertyName("step")] public double? Step { get; set; }
        [JsonPropertyName("default")] public string? Default { get; set; }
        [JsonPropertyName("options")] public List<string>? Options { get; set; }
        [JsonPropertyName("description")] public string? Description { get; set; }
    }

    public sealed class VariableChange
    {
        [JsonPropertyName("name")] public string Name { get; set; } = string.Empty;
        [JsonPropertyName("expression")] public string Expression { get; set; } = string.Empty;
        [JsonPropertyName("description")] public string? Description { get; set; }
        [JsonPropertyName("scope")] public string? Scope { get; set; }
    }

    public sealed class RuleChange
    {
        [JsonPropertyName("id")] public string Id { get; set; } = string.Empty;
        [JsonPropertyName("description")] public string? Description { get; set; }
        [JsonPropertyName("target")] public TargetChange Target { get; set; } = new TargetChange();
        [JsonPropertyName("expression")] public string Expression { get; set; } = string.Empty;
        [JsonPropertyName("condition")] public string? Condition { get; set; }
        [JsonPropertyName("scope")] public string? Scope { get; set; }
        [JsonPropertyName("evidence")] public string? Evidence { get; set; }
    }

    public sealed class TargetChange
    {
        [JsonPropertyName("kind")] public string Kind { get; set; } = "dimension";
        [JsonPropertyName("document")] public string? Document { get; set; }
        [JsonPropertyName("name")] public string? Name { get; set; }
        [JsonPropertyName("component")] public string? Component { get; set; }
    }

    public sealed class RemoveChange
    {
        [JsonPropertyName("kind")] public string Kind { get; set; } = "rule";
        [JsonPropertyName("name")] public string Name { get; set; } = string.Empty;
    }
}
