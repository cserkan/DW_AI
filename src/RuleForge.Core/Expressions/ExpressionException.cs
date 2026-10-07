using System;

namespace RuleForge.Core.Expressions
{
    public class ExpressionException : Exception
    {
        public ExpressionException(string message, int position = -1)
            : base(position >= 0 ? $"{message} (konum {position + 1})" : message)
        {
            Position = position;
        }

        public int Position { get; }
    }
}
