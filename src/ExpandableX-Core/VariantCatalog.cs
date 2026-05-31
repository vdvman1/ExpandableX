using System.Collections.Generic;

namespace ExpandableX.Core
{
    /// <summary>
    /// The set of variants generated for one piece (one registration + layout + piece). Shared by
    /// every <see cref="VariantPlacement"/> of that piece so the UI can, from any placed variant,
    /// look up sibling variants without scanning the whole catalog.
    /// </summary>
    /// <param name="DefIdByComboKey">
    /// Maps a slot-role combination key (<see cref="VariantEncoder.ComboKey"/>) to the definition id
    /// realising it (synthesised id, the base def id for the base-matching combo, or an override
    /// target). Its keys are exactly the *valid* combinations — pruned ones were never generated, so
    /// membership doubles as the validity check for a candidate slot change.
    /// </param>
    public sealed record PieceVariantSet(
        Registration Registration,
        Layout Layout,
        PieceSpec Piece,
        string BaseDefinitionId,
        IReadOnlyList<ConnectorSlot> Slots,
        IReadOnlyDictionary<string, string> DefIdByComboKey);

    /// <summary>
    /// What a single placed definition id decodes to: the piece's variant set and the slot-role
    /// state this id encodes. Built per session by the rewirer into
    /// <see cref="ExpandableXRegistry.VariantsByDefId"/>, consumed by the slot UI / swap logic.
    /// </summary>
    public sealed record VariantPlacement(
        PieceVariantSet Set,
        IReadOnlyDictionary<string, SlotRole> SlotState);
}
