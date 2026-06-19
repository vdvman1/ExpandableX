using System;
using System.Collections.Generic;
using System.Linq;

namespace ExpandableX.Core
{
    /// <summary>
    /// A per-piece rule over a single piece's slot roles. Used to prune impossible variants at
    /// generation time and to gate slot edits in the UI.
    /// </summary>
    public interface ISlotPredicate
    {
        bool IsValid(IReadOnlyDictionary<string, SlotRole> pieceState);
        string Describe();
    }

    public static class SlotPredicates
    {
        public static ISlotPredicate AtLeastN(int n, IEnumerable<SlotRole> inRoles) =>
            new AtLeastNAnywhereImpl(n, inRoles.ToHashSet());

        public static ISlotPredicate AtLeastN(int n, IEnumerable<string> slotIds, IEnumerable<SlotRole> inRoles) =>
            new AtLeastNAmongImpl(n, slotIds.ToList(), inRoles.ToHashSet());

        public static ISlotPredicate AtLeastOne(IEnumerable<SlotRole> inRoles) => AtLeastN(1, inRoles);

        public static ISlotPredicate AtLeastOne(IEnumerable<string> slotIds, IEnumerable<SlotRole> inRoles) =>
            AtLeastN(1, slotIds, inRoles);

        public static ISlotPredicate Custom(Func<IReadOnlyDictionary<string, SlotRole>, bool> predicate, string description) =>
            new CustomImpl(predicate, description);

        private static string FormatRoles(HashSet<SlotRole> roles) =>
            "{" + string.Join(",", roles.OrderBy(r => (int)r).Select(RoleAlphabet.Encode)) + "}";

        private sealed class AtLeastNAnywhereImpl : ISlotPredicate
        {
            private readonly int _n;
            private readonly HashSet<SlotRole> _roles;
            public AtLeastNAnywhereImpl(int n, HashSet<SlotRole> roles) { _n = n; _roles = roles; }
            public bool IsValid(IReadOnlyDictionary<string, SlotRole> state) => state.Values.Count(_roles.Contains) >= _n;
            public string Describe() => $"local: AtLeast {_n} in {FormatRoles(_roles)} (any slot)";
        }

        private sealed class AtLeastNAmongImpl : ISlotPredicate
        {
            private readonly int _n;
            private readonly IReadOnlyList<string> _ids;
            private readonly HashSet<SlotRole> _roles;
            public AtLeastNAmongImpl(int n, IReadOnlyList<string> ids, HashSet<SlotRole> roles) { _n = n; _ids = ids; _roles = roles; }
            public bool IsValid(IReadOnlyDictionary<string, SlotRole> state) =>
                _ids.Count(id => state.TryGetValue(id, out var r) && _roles.Contains(r)) >= _n;
            public string Describe() => $"local: AtLeast {_n} in {FormatRoles(_roles)} among {{{string.Join(",", _ids)}}}";
        }

        private sealed class CustomImpl : ISlotPredicate
        {
            private readonly Func<IReadOnlyDictionary<string, SlotRole>, bool> _predicate;
            private readonly string _description;
            public CustomImpl(Func<IReadOnlyDictionary<string, SlotRole>, bool> predicate, string description) { _predicate = predicate; _description = description; }
            public bool IsValid(IReadOnlyDictionary<string, SlotRole> state) => _predicate(state);
            public string Describe() => "local: " + _description;
        }
    }
}
