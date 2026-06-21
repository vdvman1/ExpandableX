using System.Collections.Generic;

namespace ExpandableX.Core
{
    /// <summary>Runtime state of one placed piece: its spec, expanded slots, and current per-face roles.</summary>
    public sealed record PieceState(
        PieceSpec Spec,
        IReadOnlyList<ConnectorSlot> ExpandedSlots,
        IReadOnlyDictionary<string, SlotRole> SlotRoles)
    {
        /// <summary>The definition id this piece resolves to (override-aware — see ADR-0010).</summary>
        public string DefinitionId => VariantEncoder.ResolveId(Spec, ExpandedSlots, SlotRoles);
    }

    /// <summary>
    /// Runtime snapshot of a placed (possibly multi-piece) building — the connected set of pieces
    /// that form one logical building — against which building-wide <see cref="INetworkPredicate"/>s
    /// are evaluated. A network-model building has no axis or head/tail ordering; it is just its
    /// pieces (CONTEXT.md "DynamicLayout", ADR-0012). A single-piece building is a one-element
    /// network.
    /// </summary>
    public sealed record NetworkState(
        Layout Layout,
        IReadOnlyList<PieceState> Pieces)
    {
        /// <summary>
        /// The <see cref="INetworkPredicate.Describe"/> text of the first of the layout's network
        /// predicates this state violates, or null if the building is valid (all hold, or there are none).
        /// The shared validity check for any modification that could leave a network invalid — grow
        /// re-validation and slot-config changes both gate on it (a non-null result is the blocked reason
        /// shown on the disabled button).
        /// </summary>
        public string? FirstPredicateViolation()
        {
            foreach (INetworkPredicate predicate in Layout.NetworkPredicatesOf())
            {
                if (!predicate.IsValid(this))
                {
                    return predicate.Describe();
                }
            }

            return null;
        }
    }
}
