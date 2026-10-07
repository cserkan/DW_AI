using System.Collections.Generic;

namespace RuleForge.Core.Expressions
{
    public abstract class Node
    {
        protected Node(int position)
        {
            Position = position;
        }

        public int Position { get; }
    }

    public sealed class LiteralNode : Node
    {
        public LiteralNode(Value value, int position) : base(position)
        {
            Value = value;
        }

        public Value Value { get; }
    }

    public sealed class IdentifierNode : Node
    {
        public IdentifierNode(string name, int position) : base(position)
        {
            Name = name;
        }

        public string Name { get; }
    }

    public sealed class UnaryNode : Node
    {
        public UnaryNode(string op, Node operand, int position) : base(position)
        {
            Operator = op;
            Operand = operand;
        }

        public string Operator { get; }
        public Node Operand { get; }
    }

    public sealed class BinaryNode : Node
    {
        public BinaryNode(string op, Node left, Node right, int position) : base(position)
        {
            Operator = op;
            Left = left;
            Right = right;
        }

        public string Operator { get; }
        public Node Left { get; }
        public Node Right { get; }
    }

    public sealed class CallNode : Node
    {
        public CallNode(string name, IReadOnlyList<Node> args, int position) : base(position)
        {
            Name = name;
            Arguments = args;
        }

        public string Name { get; }
        public IReadOnlyList<Node> Arguments { get; }
    }
}
