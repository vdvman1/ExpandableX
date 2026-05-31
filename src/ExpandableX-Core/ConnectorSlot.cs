using System.Collections.Generic;
using System.Linq;

namespace ExpandableX.Core
{
    /// <summary>
    /// A position on a building where a connector can live, with a player-configurable
    /// <see cref="SlotRole"/>. The role is not per-instance state — it is encoded in the
    /// definition id (see CONTEXT.md "Connector slot" and "Variant id").
    /// </summary>
    public sealed record ConnectorSlot(
        string Id,
        IReadOnlyList<SlotRole> AllowedRoles,
        SlotRole DefaultRole,
        ConnectorReference Connector)
    {
        public override string ToString() =>
            $"{Id} allowed={{{string.Join(",", AllowedRoles.Select(RoleAlphabet.Encode))}}} " +
            $"default={RoleAlphabet.Encode(DefaultRole)} <- {Connector}";
    }
}
