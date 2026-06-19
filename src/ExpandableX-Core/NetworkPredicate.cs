using System;
using System.Collections.Generic;
using System.Linq;

namespace ExpandableX.Core
{
    /// <summary>
    /// A rule spanning a whole logical building (all pieces of a network). Used at runtime to gate
    /// grow/shrink and refuse an action that would leave the building invalid, <b>and</b> at generation
    /// to prune impossible <i>singleton</i> (0-join) variants — a singleton is a complete one-piece
    /// building, so it must satisfy the same building-wide rules (one declaration covers both). Network
    /// <i>pieces</i> (>=1 join) are partial and are <b>not</b> pruned by these (another piece may supply
    /// a missing role; a piece with every face disabled is a valid spacer) — they are validated only at
    /// runtime. See CONTEXT.md "Expansion" (the Network kind) and ADR-0012.
    /// </summary>
    public interface INetworkPredicate
    {
        bool IsValid(NetworkState network);
        string Describe();
    }

    public static class NetworkPredicates
    {
        /// <summary>At least <paramref name="n"/> faces across the whole building are in one of <paramref name="inRoles"/>.</summary>
        public static INetworkPredicate AtLeastN(int n, IEnumerable<SlotRole> inRoles) =>
            new AtLeastNImpl(n, inRoles.ToHashSet());

        public static INetworkPredicate AtLeastOne(IEnumerable<SlotRole> inRoles) => AtLeastN(1, inRoles);

        /// <summary>An author predicate over the whole building (may read live game state).</summary>
        public static INetworkPredicate Custom(Func<NetworkState, bool> predicate, string description) =>
            new CustomImpl(predicate, description);

        private static string FormatRoles(HashSet<SlotRole> roles) =>
            "{" + string.Join(",", roles.OrderBy(r => (int)r).Select(RoleAlphabet.Encode)) + "}";

        private sealed class AtLeastNImpl : INetworkPredicate
        {
            private readonly int _n;
            private readonly HashSet<SlotRole> _roles;
            public AtLeastNImpl(int n, HashSet<SlotRole> roles) { _n = n; _roles = roles; }
            public bool IsValid(NetworkState network) => network.Pieces.Sum(p => p.SlotRoles.Values.Count(_roles.Contains)) >= _n;
            public string Describe() => $"network: AtLeast {_n} in {FormatRoles(_roles)} across the whole building";
        }

        private sealed class CustomImpl : INetworkPredicate
        {
            private readonly Func<NetworkState, bool> _predicate;
            private readonly string _description;
            public CustomImpl(Func<NetworkState, bool> predicate, string description) { _predicate = predicate; _description = description; }
            public bool IsValid(NetworkState network) => _predicate(network);
            public string Describe() => "network: " + _description;
        }
    }
}
