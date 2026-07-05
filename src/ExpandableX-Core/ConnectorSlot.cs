using System.Collections.Generic;
using System.Linq;

namespace ExpandableX.Core
{
    /// <summary>
    /// A position on a building where a connector can live, with a player-configurable
    /// <see cref="SlotRole"/>. The role is not per-instance state — it is encoded in the
    /// definition id (see CONTEXT.md "Connector slot" and "Variant id").
    /// </summary>
    /// <param name="Label">
    /// Optional player-facing name for the slot (CONTEXT.md "Slot label", ADR-0015). Used only for
    /// <see cref="Layout.Static"/> slots, where geometry is arbitrary and the author names them; a
    /// <see cref="Layout.Dynamic"/> slot is labelled by its world-absolute compass direction instead and
    /// ignores this. Null falls back to <see cref="Id"/>. Purely presentational — <see cref="Id"/> remains
    /// the internal, unpersisted key and is absent from the variant id, so labels never touch blueprint
    /// identity.
    /// </param>
    public sealed record ConnectorSlot(
        string Id,
        IReadOnlyList<SlotRole> AllowedRoles,
        SlotRole DefaultRole,
        ConnectorReference Connector,
        string? Label = null)
    {
        public override string ToString() =>
            $"{Id}{(Label is null ? "" : $" (\"{Label}\")")} " +
            $"allowed={{{string.Join(",", AllowedRoles.Select(RoleAlphabet.Encode))}}} " +
            $"default={RoleAlphabet.Encode(DefaultRole)} <- {Connector}";
    }
}
