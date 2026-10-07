using System.Linq;
using RuleForge.Core.Engine;
using RuleForge.Core.Expressions;
using RuleForge.Core.Json;
using RuleForge.Core.Rules;
using Xunit;

namespace RuleForge.Tests
{
    public class RuleEngineTests
    {
        internal static RuleSet Conveyor()
        {
            return new RuleSet
            {
                Name = "Konveyor",
                Inputs =
                {
                    new InputDefinition { Name = "Boy", Type = InputType.Number, Min = 1000, Max = 8000, Default = "3000" },
                    new InputDefinition { Name = "Genislik", Type = InputType.Number, Min = 300, Max = 1200, Default = "600" },
                    new InputDefinition { Name = "Motor", Type = InputType.Choice, Options = { "Sol", "Sag" }, Default = "Sol" },
                },
                Variables =
                {
                    new VariableDefinition { Name = "AyakAdedi", Expression = "CEILING(Boy / 1500) + 1" },
                    new VariableDefinition { Name = "AyakAraligi", Expression = "(Boy - 100) / (AyakAdedi - 1)" },
                },
                Rules =
                {
                    Approved("govde_boy", TargetKind.Dimension, "Govde.SLDPRT", "D1@Boss", "Boy - 40"),
                    Approved("ayak_adet", TargetKind.Dimension, "Konveyor.SLDASM", "D1@LocalLPattern1", "AyakAdedi"),
                    Approved("ayak_aralik", TargetKind.Dimension, "Konveyor.SLDASM", "D3@LocalLPattern1", "AyakAraligi"),
                    new Rule
                    {
                        Id = "motor_sol", Status = RuleStatus.Approved, Expression = "Motor <> \"Sol\"",
                        Target = new RuleTarget { Kind = TargetKind.ComponentSuppression, Component = "MotorSol-1" },
                    },
                    new Rule
                    {
                        Id = "aciklama", Status = RuleStatus.Approved, Expression = "\"KONV \" & Boy & \"x\" & Genislik",
                        Target = new RuleTarget { Kind = TargetKind.CustomProperty, Name = "Aciklama" },
                    },
                    new Rule
                    {
                        Id = "oneri", Status = RuleStatus.Proposed, Expression = "Genislik + 50",
                        Target = new RuleTarget { Kind = TargetKind.Dimension, Document = "Rulo.SLDPRT", Name = "D2@Sketch1" },
                    },
                },
            };
        }

        private static Rule Approved(string id, TargetKind kind, string doc, string name, string expr) => new Rule
        {
            Id = id, Status = RuleStatus.Approved, Expression = expr,
            Target = new RuleTarget { Kind = kind, Document = doc, Name = name },
        };

        [Fact]
        public void EvaluatesApprovedRulesWithDependencies()
        {
            var r = RuleEngine.Evaluate(Conveyor(), RuleEngine.ParseAssignments(new[] { "Boy=4500", "Motor=sag" }));
            Assert.True(r.Success, string.Join("\n", r.Errors));
            Assert.Equal(4, r.Values["AyakAdedi"].AsNumber());
            Assert.Equal(1466.666667, r.Values["AyakAraligi"].AsNumber(), 5);
            Assert.Equal(5, r.Actions.Count); // önerilen kural dahil değil
            Assert.True(r.Actions.Single(a => a.Rule.Id == "motor_sol").Value.AsBool());
            Assert.Equal("KONV 4500x600", r.Actions.Single(a => a.Rule.Id == "aciklama").Value.AsText());
            Assert.Equal("Sag", r.Values["Motor"].AsText()); // seçenek yazımı normalize edildi
        }

        [Fact]
        public void IncludeProposed()
        {
            var r = RuleEngine.Evaluate(Conveyor(), null, new EvaluationOptions { IncludeProposed = true });
            Assert.Equal(650, r.Actions.Single(a => a.Rule.Id == "oneri").Value.AsNumber());
        }

        [Fact]
        public void RejectsOutOfRangeAndBadChoice()
        {
            var r = RuleEngine.Evaluate(Conveyor(), RuleEngine.ParseAssignments(new[] { "Boy=9000", "Motor=Orta" }));
            Assert.Equal(2, r.Errors.Count);
        }

        [Fact]
        public void DetectsCycles()
        {
            var rs = Conveyor();
            rs.Variables.Add(new VariableDefinition { Name = "A", Expression = "B + 1" });
            rs.Variables.Add(new VariableDefinition { Name = "B", Expression = "A + 1" });
            var r = RuleEngine.Evaluate(rs, null);
            Assert.Contains(r.Errors, e => e.Contains("Döngüsel"));
        }

        [Fact]
        public void ConditionalRulesOnSameTarget()
        {
            var rs = Conveyor();
            rs.Rules.Add(new Rule
            {
                Id = "kisa", Status = RuleStatus.Approved, Condition = "Boy <= 3000", Expression = "40",
                Target = new RuleTarget { Kind = TargetKind.Dimension, Document = "Profil.SLDPRT", Name = "D1@Sketch1" },
            });
            rs.Rules.Add(new Rule
            {
                Id = "uzun", Status = RuleStatus.Approved, Condition = "Boy > 3000", Expression = "60",
                Target = new RuleTarget { Kind = TargetKind.Dimension, Document = "Profil.SLDPRT", Name = "D1@Sketch1" },
            });
            var r = RuleEngine.Evaluate(rs, RuleEngine.ParseAssignments(new[] { "Boy=5000" }));
            Assert.True(r.Success);
            Assert.Equal(60, r.Actions.Single(a => a.Target.Name == "D1@Sketch1").Value.AsNumber());
        }

        [Fact]
        public void ValidatorFindsProblems()
        {
            var rs = Conveyor();
            rs.Rules.Add(new Rule
            {
                Id = "kotu", Expression = "Yukseklik * 2",
                Target = new RuleTarget { Kind = TargetKind.Dimension, Document = "X.SLDPRT", Name = "D1@S" },
            });
            rs.Inputs.Add(new InputDefinition { Name = "2Hatali", Type = InputType.Number });
            var issues = RuleSetValidator.Validate(rs);
            Assert.Contains(issues, i => i.Item == "kotu" && i.Message.Contains("Yukseklik"));
            Assert.Contains(issues, i => i.Item == "2Hatali" && i.Severity == IssueSeverity.Error);
        }

        [Fact]
        public void ValidatorDryRunCatchesRuntimeErrors()
        {
            var rs = Conveyor();
            rs.Variables.Add(new VariableDefinition { Name = "Bolum", Expression = "1000 / (Boy - 1000)" });
            var issues = RuleSetValidator.Validate(rs);
            Assert.Contains(issues, i => i.Severity == IssueSeverity.Warning && i.Message.Contains("Boy=1000") && i.Message.Contains("Sıfıra"));
        }

        [Fact]
        public void JsonRoundTrip()
        {
            var json = JsonStore.Serialize(Conveyor());
            Assert.Contains("\"componentSuppression\"", json);
            Assert.Contains("Genislik", json);
            var back = JsonStore.Deserialize<RuleSet>(json);
            Assert.Equal(6, back.Rules.Count);
            Assert.Equal(TargetKind.ComponentSuppression, back.FindRule("motor_sol")!.Target.Kind);
            var r = RuleEngine.Evaluate(back, null);
            Assert.True(r.Success);
        }
    }
}
