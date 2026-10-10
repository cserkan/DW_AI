using System.Collections.Generic;
using System.Linq;
using RuleForge.Core.Rules;
using RuleForge.Inference;
using Xunit;

namespace RuleForge.Tests
{
    /// <summary>"Sizin cevaplamanız gerekenler": cevaplar kurallara doğru uygulanıyor, veriyle çelişen cevap reddediliyor.</summary>
    public class QuestionTests
    {
        private static RuleSet Rules() => new RuleSet
        {
            Inputs = { new InputDefinition { Name = "Boy" }, new InputDefinition { Name = "Malzeme", Type = InputType.Choice } },
            Variables = { new VariableDefinition { Name = "Boy_Esigi", Expression = "750" } },
            Rules =
            {
                new Rule { Id = "raf", Expression = "Boy <= Boy_Esigi" },
                new Rule { Id = "kapak", Expression = "Boy - 54" },
                new Rule { Id = "kulpSol", Target = new RuleTarget { Kind = TargetKind.ComponentReplace, Component = "Kapak-1/Kulp Oak-6" },
                           Expression = "SWITCH(Malzeme, \"Oak\", \"Kulp A.SLDPRT\", \"Maple\", \"Kulp B.SLDPRT\")" },
                new Rule { Id = "kulpSag", Target = new RuleTarget { Kind = TargetKind.ComponentReplace, Component = "Kapak-2/Kulp Oak-1" },
                           Expression = "SWITCH(Malzeme, \"Oak\", \"Kulp A.SLDPRT\", \"Maple\", \"Kulp B.SLDPRT\")" },
            },
        };

        private static List<QuestionCheck> Checks() => new[] { (600, 546), (800, 746), (900, 846) }
            .Select(x => new QuestionCheck { Variant = "V" + x.Item1, Expected = x.Item2.ToString(), Inputs = { ["Boy"] = x.Item1.ToString() } }).ToList();

        [Fact]
        public void ThresholdOutsideTheDataIsRejected()
        {
            var rules = Rules();
            var q = new OpenQuestion { Kind = QuestionKind.Threshold, Variable = "Boy_Esigi", Min = 569, Max = 800, RuleIds = { "raf" } };
            Assert.False(QuestionApplier.Apply(rules, q, new QuestionAnswer { Number = 900 }).Ok);
            Assert.Equal("750", rules.FindVariable("Boy_Esigi")!.Expression);
            Assert.True(QuestionApplier.Apply(rules, q, new QuestionAnswer { Number = 700 }).Ok);
            Assert.Equal("700", rules.FindVariable("Boy_Esigi")!.Expression);
            Assert.Equal(RuleStatus.Approved, rules.FindRule("raf")!.Status);
        }

        [Fact]
        public void WrittenFormulaIsCheckedAgainstVariants()
        {
            var rules = Rules();
            var q = new OpenQuestion { Kind = QuestionKind.Confirm, RuleIds = { "kapak" }, AllowFormula = true, Checks = Checks() };
            var wrong = QuestionApplier.Apply(rules, q, new QuestionAnswer { Choice = "formul", Formula = "Boy / 2" });
            Assert.False(wrong.Ok);
            Assert.Contains("hiçbir varyantta", wrong.Message);
            Assert.False(QuestionApplier.Apply(rules, q, new QuestionAnswer { Choice = "formul", Formula = "(Boy - 54" }).Ok);
            var right = QuestionApplier.Apply(rules, q, new QuestionAnswer { Choice = "formul", Formula = "Boy - 54" });
            Assert.True(right.Ok);
            Assert.Contains("3 varyantın hepsinde", right.Message);
        }

        [Fact]
        public void SeparateChoiceCreatesOneSharedInput()
        {
            var rules = Rules();
            var q = new OpenQuestion { Kind = QuestionKind.SeparateInput, Input = "Malzeme", RuleIds = { "kulpSol", "kulpSag" } };
            Assert.True(QuestionApplier.Apply(rules, q, new QuestionAnswer { Choice = "ayri" }).Ok);
            var input = Assert.Single(rules.Inputs, i => i.Name.EndsWith("_Secimi"));
            Assert.Equal("Kulp_Oak_Secimi", input.Name);
            Assert.Equal(new[] { "Kulp A.SLDPRT", "Kulp B.SLDPRT" }, input.Options);
            Assert.All(new[] { "kulpSol", "kulpSag" }, id => Assert.Equal(input.Name, rules.FindRule(id)!.Expression));
        }

        [Fact]
        public void UnexplainedValueGetsAManualRule()
        {
            var rules = Rules();
            var q = new OpenQuestion
            {
                Kind = QuestionKind.Unexplained, AllowFormula = true, Checks = Checks(),
                Targets = { new NewRuleTarget { Label = "Arka panel", Target = new RuleTarget { Kind = TargetKind.Dimension, Document = "Arka.SLDPRT", Name = "D1@Sketch1" } } },
            };
            Assert.True(QuestionApplier.Apply(rules, q, new QuestionAnswer { Choice = "onemsiz" }).Ok);
            Assert.Equal(4, rules.Rules.Count);
            Assert.True(QuestionApplier.Apply(rules, q, new QuestionAnswer { Choice = "formul", Formula = "Boy - 54" }).Ok);
            var added = rules.Rules.Single(r => r.Id.StartsWith("elle_"));
            Assert.Equal("Boy - 54", added.Expression);
            Assert.Equal(RuleStatus.Approved, added.Status);
        }
    }
}
