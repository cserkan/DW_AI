using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Anthropic;
using Anthropic.Models.Beta.Messages;
using RuleForge.Core.Engine;
using RuleForge.Core.Json;
using RuleForge.Core.Model;
using RuleForge.Core.Rules;

namespace RuleForge.AI
{
    public sealed class AssistantOptions
    {
        public string Model { get; set; } = "claude-opus-5-5";

        /// <summary>low / medium / high / xhigh / max. Kural yazımı dikkat isteyen bir iş: varsayılan high.</summary>
        public string Effort { get; set; } = "high";

        public int MaxTokens { get; set; } = 16000;

        /// <summary>Doğrulama hatalarında YZ'ye kaç kez otomatik düzeltme yaptırılacağı.</summary>
        public int MaxRepairRounds { get; set; } = 2;
    }

    public sealed class AssistantTurn
    {
        public string Reply { get; set; } = string.Empty;
        public List<string> Questions { get; set; } = new List<string>();
        public List<string> Changes { get; set; } = new List<string>();

        /// <summary>Düzeltme turlarından sonra hâlâ kalan sorunlar.</summary>
        public List<ValidationIssue> Issues { get; set; } = new List<ValidationIssue>();

        public bool Refused { get; set; }
    }

    /// <summary>
    /// Sohbetle kural yazma. Her turda: kullanıcı mesajı + güncel kural seti → Claude → yapılandırılmış
    /// değişiklikler → doğrulama → (hata varsa) otomatik düzeltme turu. Geçmiş sadece sona eklenerek büyür.
    /// </summary>
    public sealed class RuleAssistant
    {
        private static readonly JsonSerializerOptions Compact = new JsonSerializerOptions(JsonStore.Options) { WriteIndented = false };

        private readonly AnthropicClient _client;
        private readonly AssistantOptions _options;
        private readonly ModelSnapshot? _snapshot;
        private readonly string _systemPrompt;
        private readonly string _modelContext;
        private readonly List<BetaMessageParam> _history = new List<BetaMessageParam>();

        public RuleAssistant(AnthropicClient client, RuleSet ruleSet, ModelSnapshot? snapshot,
            string? inferenceReportText = null, AssistantOptions? options = null)
        {
            _client = client;
            _options = options ?? new AssistantOptions();
            _snapshot = snapshot;
            RuleSet = ruleSet;
            _systemPrompt = Prompts.System();
            _modelContext = Prompts.ModelContext(
                snapshot != null ? ModelSummary.Build(snapshot) : "(Model özeti yok — hedef adlarını kullanıcıya sor.)",
                inferenceReportText);
        }

        public RuleSet RuleSet { get; private set; }

        public async Task<AssistantTurn> SendAsync(string userMessage, CancellationToken cancellationToken = default)
        {
            var turn = new AssistantTurn();
            var text = $"<kural_seti>\n{JsonSerializer.Serialize(RuleSet, Compact)}\n</kural_seti>\n\n<mesaj>\n{userMessage}\n</mesaj>";

            for (int round = 0; ; round++)
            {
                _history.Add(new BetaMessageParam { Role = Role.User, Content = text });
                var response = await CreateAsync(cancellationToken).ConfigureAwait(false);

                if (response.StopReason == BetaStopReason.Refusal)
                {
                    // Geçmişi tutarlı tut: reddedilen kullanıcı mesajını geri al.
                    _history.RemoveAt(_history.Count - 1);
                    turn.Refused = true;
                    turn.Reply = "Model bu isteği yanıtlamayı reddetti. Lütfen isteği farklı ifade edin.";
                    return turn;
                }
                if (response.StopReason == BetaStopReason.MaxTokens)
                    throw new InvalidOperationException("Yanıt max_tokens sınırında kesildi. AssistantOptions.MaxTokens değerini artırın.");

                _history.Add(new BetaMessageParam { Role = Role.Assistant, Content = Echo(response) });

                var json = string.Concat(response.Content.Select(b => b.Value).OfType<BetaTextBlock>().Select(t => t.Text));
                var changes = RuleChanges.Parse(json);

                if (round == 0)
                {
                    turn.Reply = changes.Reply;
                    turn.Questions.AddRange(changes.Questions);
                }
                else if (!string.IsNullOrWhiteSpace(changes.Reply))
                {
                    turn.Reply += "\n" + changes.Reply;
                }

                // Klon üzerinde uygula; doğrulamadan geçemeyen bir değişiklik seti yarım kalmasın.
                var candidate = Clone(RuleSet);
                List<string> log;
                try
                {
                    log = changes.ApplyTo(candidate);
                }
                catch (FormatException ex)
                {
                    log = new List<string>();
                    turn.Issues = new List<ValidationIssue> { new ValidationIssue(IssueSeverity.Error, "yanıt", ex.Message) };
                    if (round >= _options.MaxRepairRounds) return turn;
                    text = RepairMessage(turn.Issues);
                    continue;
                }

                var issues = RuleSetValidator.Validate(candidate, _snapshot);
                var touched = Touched(changes);
                var relevant = issues.Where(i => i.Severity == IssueSeverity.Error ||
                                                 touched.Any(t => i.Item.IndexOf(t, StringComparison.OrdinalIgnoreCase) >= 0) ||
                                                 i.Item == "deneme").ToList();

                RuleSet = candidate;
                turn.Changes.AddRange(log);
                turn.Issues = relevant;

                var errors = relevant.Where(i => i.Severity == IssueSeverity.Error).ToList();
                if (errors.Count == 0 || round >= _options.MaxRepairRounds) return turn;
                text = RepairMessage(errors);
            }
        }

        private static string RepairMessage(IEnumerable<ValidationIssue> issues)
        {
            var sb = new StringBuilder();
            sb.AppendLine("<dogrulama_hatalari>");
            foreach (var i in issues) sb.AppendLine(i.ToString());
            sb.AppendLine("</dogrulama_hatalari>");
            sb.Append("Değişikliklerin kural setine uygulandı ama yukarıdaki hatalar var. Sadece bunları düzelt.");
            return sb.ToString();
        }

        private async Task<BetaMessage> CreateAsync(CancellationToken ct)
        {
            var parameters = new MessageCreateParams
            {
                Model = _options.Model,
                MaxTokens = _options.MaxTokens,
                Betas = ["server-side-fallback-2026-07-01"],
                // Güvenlik sınıflandırıcısı reddederse sunucu tarafında önerilen modele düşer.
                Fallbacks = new Default(),
                System = new List<BetaTextBlockParam>
                {
                    new BetaTextBlockParam { Text = _systemPrompt },
                    // Model özeti oturum boyunca sabit: önbelleğe alınır, sonraki turlar ucuzlar.
                    new BetaTextBlockParam { Text = _modelContext, CacheControl = new BetaCacheControlEphemeral() },
                },
                OutputConfig = new BetaOutputConfig
                {
                    Effort = _options.Effort,
                    Format = new BetaJsonOutputFormat { Schema = RuleChanges.Schema() },
                },
                Messages = _history.ToList(),
            };
            return await _client.Beta.Messages.Create(parameters, cancellationToken: ct).ConfigureAwait(false);
        }

        /// <summary>
        /// Yanıtı geçmişe eklenecek parametre bloklarına çevirir. Thinking blokları imzalarıyla
        /// aynen korunur. Sunucu tarafı fallback olduysa, son fallback bloğundan önceki
        /// thinking blokları atılır (API kuralı).
        /// </summary>
        private static List<BetaContentBlockParam> Echo(BetaMessage response)
        {
            var blocks = response.Content.ToList();
            int lastFallback = blocks.FindLastIndex(b => b.Value is BetaFallbackBlock);
            var result = new List<BetaContentBlockParam>();
            for (int i = 0; i < blocks.Count; i++)
            {
                var block = blocks[i];
                bool beforeFallback = i < lastFallback;
                if (block.TryPickText(out BetaTextBlock? text))
                    result.Add(new BetaTextBlockParam { Text = text.Text });
                else if (!beforeFallback && block.TryPickThinking(out BetaThinkingBlock? thinking))
                    result.Add(new BetaThinkingBlockParam { Thinking = thinking.Thinking, Signature = thinking.Signature });
                else if (!beforeFallback && block.TryPickRedactedThinking(out BetaRedactedThinkingBlock? redacted))
                    result.Add(new BetaRedactedThinkingBlockParam { Data = redacted.Data });
            }
            return result;
        }

        private static HashSet<string> Touched(RuleChanges c)
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var i in c.UpsertInputs) set.Add(i.Name);
            foreach (var v in c.UpsertVariables) set.Add(v.Name);
            foreach (var r in c.UpsertRules) set.Add(r.Id);
            return set;
        }

        private static RuleSet Clone(RuleSet set) => JsonStore.Deserialize<RuleSet>(JsonStore.Serialize(set));
    }
}
