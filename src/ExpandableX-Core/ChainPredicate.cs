using System;
using System.Collections.Generic;
using System.Linq;

namespace ExpandableX.Core
{
    /// <summary>
    /// A rule spanning a whole chain (all pieces). Evaluated at runtime only — chain predicates do
    /// not prune variants (a piece with every slot disabled is a valid spacer).
    /// </summary>
    public interface IChainPredicate
    {
        bool IsValid(ChainState chain);
        string Describe();
    }

    public static class ChainPredicates
    {
        public static IChainPredicate AtLeastN(int n, IEnumerable<SlotRole> inRoles) =>
            new AtLeastNChainImpl(n, inRoles.ToHashSet());

        public static IChainPredicate AtLeastOne(IEnumerable<SlotRole> inRoles) => AtLeastN(1, inRoles);

        public static IChainPredicate Custom(Func<ChainState, bool> predicate, string description) =>
            new CustomChainImpl(predicate, description);

        private static string FormatRoles(HashSet<SlotRole> roles) =>
            "{" + string.Join(",", roles.OrderBy(r => (int)r).Select(RoleAlphabet.Encode)) + "}";

        private sealed class AtLeastNChainImpl : IChainPredicate
        {
            private readonly int _n;
            private readonly HashSet<SlotRole> _roles;
            public AtLeastNChainImpl(int n, HashSet<SlotRole> roles) { _n = n; _roles = roles; }
            public bool IsValid(ChainState chain) => chain.Pieces.Sum(p => p.SlotRoles.Values.Count(_roles.Contains)) >= _n;
            public string Describe() => $"chain: AtLeast {_n} in {FormatRoles(_roles)} across whole chain";
        }

        private sealed class CustomChainImpl : IChainPredicate
        {
            private readonly Func<ChainState, bool> _predicate;
            private readonly string _description;
            public CustomChainImpl(Func<ChainState, bool> predicate, string description) { _predicate = predicate; _description = description; }
            public bool IsValid(ChainState chain) => _predicate(chain);
            public string Describe() => "chain: " + _description;
        }
    }
}
