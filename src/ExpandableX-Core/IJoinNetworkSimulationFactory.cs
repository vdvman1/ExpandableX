using System.Collections.Generic;
using Game.Core.Coordinates;

namespace ExpandableX.Core
{
    /// <summary>
    /// Builds the per-network simulation for one network-model family. Author-supplied (consumer
    /// territory per ADR-0011 — Core never hardcodes the gameplay logic) and carried on the family's
    /// <see cref="Layout.Dynamic.SimulationFactory"/>; the rewirer hands it to the shared
    /// <see cref="JoinNetworkSystem"/> keyed by family.
    ///
    /// <see cref="Create"/> receives the full set of member buildings of a connected network — each
    /// <see cref="BuildingInstance"/> includes its <c>State</c> container — together with the
    /// network's <paramref name="occupiedTiles"/>, the union of every member's occupied tiles already
    /// computed by the matcher. The node answers <c>ILocalizedTileSimulation</c> from that footprint
    /// rather than recomputing it, so the network's geometry has a single source of truth. The
    /// matcher calls this afresh whenever a network's membership changes (rebuild-on-change), so an
    /// implementation must derive everything it needs from its arguments and must not assume it is the
    /// same instance as before.
    /// </summary>
    public interface IJoinNetworkSimulationFactory
    {
        IJoinNetworkSimulation Create(
            IReadOnlyCollection<BuildingInstance> members,
            IReadOnlyList<GlobalTileCoordinate> occupiedTiles);
    }
}
