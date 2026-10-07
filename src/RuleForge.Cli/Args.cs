using System;
using System.Collections.Generic;
using System.Linq;

namespace RuleForge.Cli
{
    /// <summary>Basit argüman ayrıştırıcı: --ad değer, --bayrak, ve konumsal argümanlar.</summary>
    internal sealed class Args
    {
        private readonly Dictionary<string, List<string>> _options = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _flags = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        public Args(IEnumerable<string> args, params string[] flagNames)
        {
            var flags = new HashSet<string>(flagNames, StringComparer.OrdinalIgnoreCase);
            var list = args.ToList();
            for (int i = 0; i < list.Count; i++)
            {
                var a = list[i];
                if (a.StartsWith("--") || (a.StartsWith("-") && a.Length == 2 && !char.IsDigit(a[1])))
                {
                    var name = a.TrimStart('-');
                    if (flags.Contains(name) || i + 1 >= list.Count)
                    {
                        _flags.Add(name);
                        continue;
                    }
                    if (!_options.TryGetValue(name, out var values)) _options[name] = values = new List<string>();
                    values.Add(list[++i]);
                }
                else
                {
                    Positional.Add(a);
                }
            }
        }

        public List<string> Positional { get; } = new List<string>();

        public bool Flag(string name) => _flags.Contains(name);

        public string? Get(string name, string? alias = null)
        {
            if (_options.TryGetValue(name, out var v)) return v.Last();
            if (alias != null && _options.TryGetValue(alias, out v)) return v.Last();
            return null;
        }

        public string Require(string name, string? alias = null) =>
            Get(name, alias) ?? throw new UsageException($"--{name} gerekli.");

        public List<string> All(string name) => _options.TryGetValue(name, out var v) ? v : new List<string>();

        /// <summary>Konumsal argümanlardan "Ad=Değer" olanlar.</summary>
        public List<string> Assignments => Positional.Where(p => p.Contains("=")).ToList();

        public List<string> NonAssignments => Positional.Where(p => !p.Contains("=")).ToList();
    }

    internal sealed class UsageException : Exception
    {
        public UsageException(string message) : base(message)
        {
        }
    }
}
