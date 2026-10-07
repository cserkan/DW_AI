using System.Collections.Generic;
using RuleForge.Core.Expressions;
using Xunit;

namespace RuleForge.Tests
{
    public class ExpressionTests
    {
        private static Value Eval(string text, params (string, Value)[] vars)
        {
            var d = new Dictionary<string, Value>(System.StringComparer.OrdinalIgnoreCase);
            foreach (var (k, v) in vars) d[k] = v;
            return Expression.Evaluate(text, d);
        }

        [Theory]
        [InlineData("1 + 2 * 3", 7)]
        [InlineData("(1 + 2) * 3", 9)]
        [InlineData("2 ^ 3 ^ 2", 512)]
        [InlineData("-2 ^ 2", -4)]
        [InlineData("10 % 3", 1)]
        [InlineData("CEILING(1230, 100)", 1300)]
        [InlineData("FLOOR(1230, 100)", 1200)]
        [InlineData("CEILING(1200, 100)", 1200)]
        [InlineData("MROUND(1249, 100)", 1200)]
        [InlineData("ROUND(2.675, 2)", 2.68)]
        [InlineData("ROUNDUP(2.01, 0)", 3)]
        [InlineData("ROUNDDOWN(2.99, 0)", 2)]
        [InlineData("MIN(4, 2, 8)", 2)]
        [InlineData("MAX(4, 2, 8)", 8)]
        [InlineData("CLAMP(15, 0, 10)", 10)]
        [InlineData("IF(1 > 2, 10, 20)", 20)]
        [InlineData("IFS(FALSE, 1, TRUE, 2)", 2)]
        [InlineData("RANGELOOKUP(1500, 1000, 40, 2000, 60, 80)", 60)]
        [InlineData("RANGELOOKUP(2500, 1000, 40, 2000, 60, 80)", 80)]
        [InlineData("SIN(90)", 1)]
        public void Arithmetic(string text, double expected)
        {
            Assert.Equal(expected, Eval(text).AsNumber(), 6);
        }

        [Fact]
        public void VariablesAndText()
        {
            var r = Eval("\"KONV-\" & Boy & \"x\" & Genislik", ("Boy", Value.Number(3000)), ("Genislik", Value.Number(650.5)));
            Assert.Equal("KONV-3000x650.5", r.AsText());
        }

        [Fact]
        public void SwitchIsCaseInsensitive()
        {
            var r = Eval("SWITCH(Motor, \"sol\", 1, \"sag\", 2)", ("motor", Value.Text("SAG")));
            Assert.Equal(2, r.AsNumber());
        }

        [Fact]
        public void IfIsLazy()
        {
            // Yanlış dal sıfıra bölse bile hata vermemeli
            Assert.Equal(1, Eval("IF(TRUE, 1, 1/0)").AsNumber());
        }

        [Fact]
        public void LogicalOperators()
        {
            Assert.True(Eval("Boy > 3000 && Motor = \"Sol\"", ("Boy", Value.Number(3500)), ("Motor", Value.Text("Sol"))).AsBool());
            Assert.True(Eval("!(1 = 2) || FALSE").AsBool());
            Assert.True(Eval("AND(1 < 2, OR(FALSE, 3 <> 4))").AsBool());
        }

        [Fact]
        public void TurkishIdentifiers()
        {
            Assert.Equal(12, Eval("Genişlik + 2", ("Genişlik", Value.Number(10))).AsNumber());
        }

        [Theory]
        [InlineData("1 +", "satır bitti")]
        [InlineData("FOO(1)", "Bilinmeyen fonksiyon")]
        [InlineData("IF(1)", "argüman sayısı")]
        [InlineData("\"abc", "Kapanmamış")]
        public void ParseErrors(string text, string fragment)
        {
            Assert.False(Expression.TryParse(text, out _, out var error));
            Assert.Contains(fragment, error);
        }

        [Fact]
        public void UndefinedVariable()
        {
            var ex = Assert.Throws<ExpressionException>(() => Eval("Boy + 1"));
            Assert.Contains("Tanımsız", ex.Message);
        }

        [Fact]
        public void CollectsIdentifiers()
        {
            var ids = Expression.Parse("IF(Boy > 3000, Genislik * 2, MAX(Genislik, X))").GetIdentifiers();
            Assert.Equal(3, ids.Count);
            Assert.Contains("X", ids);
        }
    }
}
