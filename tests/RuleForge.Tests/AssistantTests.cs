using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Anthropic;
using RuleForge.AI;
using RuleForge.Core.Rules;
using Xunit;

namespace RuleForge.Tests
{
    /// <summary>API'ye gitmeden RuleAssistant akışını test eder: istekler kaydedilir, yanıtlar sıradan verilir.</summary>
    public class AssistantTests
    {
        private sealed class FakeHandler : HttpMessageHandler
        {
            private readonly Queue<string> _replies;

            public FakeHandler(params string[] replies)
            {
                _replies = new Queue<string>(replies);
            }

            public List<JsonDocument> Requests { get; } = new List<JsonDocument>();

            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            {
                Requests.Add(JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct)));
                var body = new
                {
                    id = "msg_" + Requests.Count,
                    type = "message",
                    role = "assistant",
                    model = "claude-opus-5-5",
                    content = new object[]
                    {
                        new { type = "thinking", thinking = "", signature = "sig" + Requests.Count },
                        new { type = "text", text = _replies.Dequeue() },
                    },
                    stop_reason = "end_turn",
                    stop_sequence = (string?)null,
                    usage = new { input_tokens = 100, output_tokens = 50 },
                };
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"),
                };
            }
        }

        private static string Reply(string reply, object[]? inputs = null, object[]? variables = null, object[]? rules = null)
        {
            return JsonSerializer.Serialize(new Dictionary<string, object>
            {
                ["reply"] = reply,
                ["questions"] = new[] { "Ayak aralığı en fazla kaç mm?" },
                ["upsert_inputs"] = inputs ?? Array.Empty<object>(),
                ["upsert_variables"] = variables ?? Array.Empty<object>(),
                ["upsert_rules"] = rules ?? Array.Empty<object>(),
                ["remove"] = Array.Empty<object>(),
            });
        }

        private static object Rule(string id, string kind, string? doc, string? name, string expr, string? component = null) => new
        {
            id, description = (string?)null, expression = expr, condition = (string?)null, evidence = "kullanıcı",
            target = new { kind, document = doc, name, component },
        };

        private static object Input(string name, double min, double max, string def) => new
        {
            name, label = name, type = "number", unit = "mm", min, max, step = (double?)null, @default = def,
            options = Array.Empty<string>(), description = (string?)null,
        };

        [Fact]
        public async Task AppliesChangesAndRepairsValidationErrors()
        {
            var handler = new FakeHandler(
                // 1. tur: tanımsız 'Genislik' kullanıyor ve olmayan bir ölçüyü hedefliyor
                Reply("Gövde ve rulo kurallarını ekledim.",
                    inputs: new[] { Input("Boy", 1000, 8000, "3000") },
                    rules: new[]
                    {
                        Rule("govde_boy", "dimension", "Govde.SLDPRT", "D1@Boss", "Boy - 40"),
                        Rule("rulo_boy", "dimension", "Rulo.SLDPRT", "D9@Yok", "Genislik + 50"),
                    }),
                // 2. tur (düzeltme): girdiyi ekliyor, ölçü adını düzeltiyor
                Reply("Hataları düzelttim.",
                    inputs: new[] { Input("Genislik", 300, 1200, "600") },
                    rules: new[] { Rule("rulo_boy", "dimension", "Rulo.SLDPRT", "D2@Sketch1", "Genislik + 50") }));

            var client = new AnthropicClient { ApiKey = "test", HttpClient = new HttpClient(handler), MaxRetries = 0 };
            var snapshot = SyntheticConveyor.Build("master", 3000, 600, "Sol");
            var assistant = new RuleAssistant(client, new RuleSet { Name = "Konveyor" }, snapshot);

            var turn = await assistant.SendAsync("Gövde boyu konveyör boyundan 40 eksik olsun, rulo genişlikten 50 fazla.");

            Assert.Equal(2, handler.Requests.Count);
            Assert.DoesNotContain(turn.Issues, i => i.Severity == RuleForge.Core.Engine.IssueSeverity.Error);
            Assert.Equal("D2@Sketch1", assistant.RuleSet.FindRule("rulo_boy")!.Target.Name);
            Assert.Equal(2, assistant.RuleSet.Inputs.Count);
            Assert.All(assistant.RuleSet.Rules, r => Assert.Equal(RuleStatus.Proposed, r.Status));
            Assert.Contains("Ayak aralığı en fazla kaç mm?", turn.Questions);

            // İstek gövdesi: model, fallback, yapılandırılmış çıktı, önbellek
            var first = handler.Requests[0].RootElement;
            Assert.Equal("claude-opus-5-5", first.GetProperty("model").GetString());
            Assert.Equal("default", first.GetProperty("fallbacks").GetString());
            Assert.Equal("high", first.GetProperty("output_config").GetProperty("effort").GetString());
            Assert.Equal("json_schema", first.GetProperty("output_config").GetProperty("format").GetProperty("type").GetString());
            var system = first.GetProperty("system").EnumerateArray().ToList();
            Assert.Contains("Govde.SLDPRT", system[1].GetProperty("text").GetString());
            Assert.Equal("ephemeral", system[1].GetProperty("cache_control").GetProperty("type").GetString());

            // İkinci istek: geçmiş sona eklenerek büyür, thinking imzası korunur, hata mesajı gönderilir
            var messages = handler.Requests[1].RootElement.GetProperty("messages").EnumerateArray().ToList();
            Assert.Equal(3, messages.Count);
            Assert.Equal("sig1", messages[1].GetProperty("content")[0].GetProperty("signature").GetString());
            var repair = messages[2].GetProperty("content").ToString();
            Assert.Contains("dogrulama_hatalari", repair);
            Assert.Contains("Genislik", repair);
            Assert.Contains("D9@Yok", repair);
        }

        [Fact]
        public void SchemaRequiresEveryProperty()
        {
            void Check(JsonElement e)
            {
                if (e.ValueKind != JsonValueKind.Object) return;
                if (e.TryGetProperty("properties", out var props))
                {
                    var required = e.GetProperty("required").EnumerateArray().Select(x => x.GetString()).ToHashSet();
                    foreach (var p in props.EnumerateObject())
                    {
                        Assert.Contains(p.Name, required);
                        Check(p.Value);
                    }
                    Assert.False(e.GetProperty("additionalProperties").GetBoolean());
                }
                if (e.TryGetProperty("items", out var items)) Check(items);
            }

            var schema = JsonSerializer.SerializeToElement(RuleChanges.Schema());
            Check(schema);
        }

        [Fact]
        public void ModelSummaryListsExactNames()
        {
            var text = ModelSummary.Build(SyntheticConveyor.Build("m", 3000, 600, "Sol"));
            Assert.Contains("D1@Boss = 2960 mm", text);
            Assert.Contains("D3@Sketch2 = 1480 mm [driven]", text);
            Assert.Contains("MotorSol-1 → MotorSol.SLDPRT", text);
            Assert.Contains("OrtaDestek-1 → OrtaDestek.SLDPRT (bastırılmış)", text);
        }
    }
}
