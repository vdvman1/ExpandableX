using System.Collections.Generic;
using System.Linq;

namespace ExpandableX.Core
{
    /// <summary>
    /// Reads visible connectors off a live configurable-base definition's <c>ConnectorData</c>,
    /// applying <see cref="ConnectorReference.AutoSkipInternal"/> — internal connectors
    /// (<c>BuildingFluidIOType.None</c>, e.g. the painter's 4th junction) are skipped so a slot's
    /// visible index matches the connectors the player can see. Constructed per base definition by
    /// the framework (the rewirer), which is the only place a live <c>ConnectorData</c> is available.
    /// </summary>
    internal sealed class ConnectorDataResolver : IConnectorCountResolver
    {
        private readonly IBuildingConnectorData _data;

        public ConnectorDataResolver(IBuildingConnectorData data)
        {
            _data = data;
        }

        public int CountVisible(ConnectorReference reference) => VisibleConnectors(reference).Count;

        /// <summary>The visible connectors of the reference's type, in connector order, internal ones skipped if requested.</summary>
        public IReadOnlyList<IBuildingIO> VisibleConnectors(ConnectorReference reference)
        {
            IReadOnlyList<IBuildingIO> all = reference.Accessor(_data);
            if (!reference.AutoSkipInternal)
            {
                return all;
            }

            return all.Where(io => !IsInternal(io)).ToList();
        }

        /// <summary>The concrete connector a reference resolves to (for its pivot), or null if its index is out of range.</summary>
        public IBuildingIO? ResolveVisible(ConnectorReference reference)
        {
            IReadOnlyList<IBuildingIO> visible = VisibleConnectors(reference);
            return reference.VisibleIndex >= 0 && reference.VisibleIndex < visible.Count
                ? visible[reference.VisibleIndex]
                : null;
        }

        /// <summary>
        /// An internal connector has no external presence — its medium-specific IO type is
        /// <c>None</c>. All three connector media (item incl. belt ports, fluid, signal) carry a
        /// parallel <c>*IOType</c> with a <c>None</c> member.
        /// </summary>
        private static bool IsInternal(IBuildingIO io) => io switch
        {
            BuildingItemIO item => item.IOType == BuildingItemIOType.None,
            BuildingFluidIO fluid => fluid.IOType == BuildingFluidIOType.None,
            BuildingSignalIO signal => signal.IOType == BuildingSignalIOType.None,
            _ => false,
        };
    }
}
