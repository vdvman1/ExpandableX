using System.Collections.Generic;

namespace ExpandableX.Core
{
    /// <summary>Runtime state of one placed piece: its spec, expanded slots, and current roles.</summary>
    public sealed record PieceState(
        int PieceIndex,
        PieceSpec Spec,
        IReadOnlyList<ConnectorSlot> ExpandedSlots,
        IReadOnlyDictionary<string, SlotRole> SlotRoles)
    {
        /// <summary>The definition id this piece resolves to (override-aware — see ADR-0010).</summary>
        public string DefinitionId => VariantEncoder.ResolveId(Spec, ExpandedSlots, SlotRoles);

        public string DisplayLabel => Spec.Role switch
        {
            PieceRole.Singleton => "SINGLETON",
            PieceRole.Head => "HEAD",
            PieceRole.Body => $"BODY[{PieceIndex}]",
            PieceRole.Tail => "TAIL",
            _ => Spec.Role.ToString(),
        };
    }

    /// <summary>
    /// Runtime state of a placed (possibly multi-piece) building. <see cref="Axis"/> is the
    /// direction the head end faces; null for a singleton / static layout.
    /// </summary>
    public sealed record ChainState(
        Layout Layout,
        IReadOnlyList<PieceState> Pieces,
        TileDirection? Axis);
}
