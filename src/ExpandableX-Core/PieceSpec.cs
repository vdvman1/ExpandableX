using System.Collections.Generic;

namespace ExpandableX.Core
{
    /// <summary>The role a generated piece plays. See CONTEXT.md "Piece role".</summary>
    public enum PieceRole { Singleton, Head, Body, Tail }

    /// <summary>
    /// One generated piece of a layout: its configurable base, its connector slots, the local
    /// rules pruning impossible combinations, and any per-variant overrides.
    /// </summary>
    /// <param name="BaseDefinitionId">
    /// The configurable base — the connector-superset definition the slots are declared against
    /// (ADR-0010). Often a new authored definition distinct from the default singleton.
    /// </param>
    /// <param name="VariantOverrides">
    /// Maps a slot-role combination key (see <see cref="VariantEncoder.ComboKey"/>) to a named
    /// pre-existing definition id, used instead of a synthesised variant (ADR-0010). Null = none.
    /// </param>
    public sealed record PieceSpec(
        string BaseDefinitionId,
        PieceRole Role,
        IReadOnlyList<ConnectorSlotSpec> SlotSpecs,
        IReadOnlyList<ISlotPredicate> LocalPredicates,
        IReadOnlyDictionary<string, string>? VariantOverrides = null)
    {
        private static readonly IReadOnlyDictionary<string, string> NoOverrides = new Dictionary<string, string>();

        /// <summary>Non-null view of <see cref="VariantOverrides"/>.</summary>
        public IReadOnlyDictionary<string, string> Overrides => VariantOverrides ?? NoOverrides;
    }
}
