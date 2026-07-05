using System.Collections.Generic;

namespace ExpandableX.Core
{
    /// <summary>
    /// One generated piece of a layout: its configurable base, its connector slots, the local
    /// rules pruning impossible combinations, and any per-variant overrides. (A piece has no fixed
    /// "kind" — for a <see cref="Layout.Dynamic"/> a single declared piece yields both the singleton
    /// and network variants, distinguished per-variant by join count; see CONTEXT.md "Piece role".)
    /// </summary>
    /// <param name="BaseDefinitionId">
    /// The configurable base — the connector-superset definition the slots are declared against
    /// (ADR-0010). Often a new authored definition distinct from the default singleton.
    /// </param>
    /// <param name="VariantOverrides">
    /// Maps a slot-role combination key (see <see cref="VariantEncoder.ComboKey"/>) to a named
    /// pre-existing definition id, used instead of a synthesised variant (ADR-0010). Null = none.
    /// </param>
    /// <param name="Models">
    /// Opt-in authored model for the piece's synthesised variants — a body piece plus per-connector
    /// bridge pieces the framework bakes into each variant's model (CONTEXT.md "Composed model",
    /// ADR-0016). Null keeps today's behaviour: each variant clones the base definition's model.
    /// </param>
    public sealed record PieceSpec(
        string BaseDefinitionId,
        IReadOnlyList<ConnectorSlotSpec> SlotSpecs,
        IReadOnlyList<ISlotPredicate> LocalPredicates,
        IReadOnlyDictionary<string, string>? VariantOverrides = null,
        ModelPieceSet? Models = null)
    {
        private static readonly IReadOnlyDictionary<string, string> NoOverrides = new Dictionary<string, string>();

        /// <summary>Non-null view of <see cref="VariantOverrides"/>.</summary>
        public IReadOnlyDictionary<string, string> Overrides => VariantOverrides ?? NoOverrides;
    }
}
