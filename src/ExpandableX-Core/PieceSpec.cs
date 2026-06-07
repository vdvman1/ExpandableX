using System.Collections.Generic;

namespace ExpandableX.Core
{
    /// <summary>
    /// The kind of piece a spec describes. See CONTEXT.md "Piece role". A <see cref="Static"/>
    /// layout's lone piece is a <see cref="Singleton"/>; a network-model <see cref="Layout.Dynamic"/>
    /// piece is a <see cref="NetworkPiece"/> (the join-face set distinguishes its variants — there is
    /// no head/body/tail).
    /// </summary>
    public enum PieceRole { Singleton, NetworkPiece }

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
