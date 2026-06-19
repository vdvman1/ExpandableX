using System.Collections.Generic;
using Game.Core.Coordinates;

namespace ExpandableX.Core
{
    /// <summary>
    /// Builds the runtime simulation for one connected set of member buildings of an expandable family
    /// — one member for a standalone building, N for a multi-piece network. Author-supplied (consumer
    /// territory per ADR-0011 — Core never hardcodes the gameplay logic) and carried on a layout's
    /// <see cref="Layout.SimulationFactory"/>; the shared <see cref="ExpandableSimulationSystem"/> keys
    /// it by family and invokes it afresh whenever a network's membership changes (rebuild-on-change),
    /// passing the members (each <see cref="BuildingInstance"/> includes its <c>State</c>) and the
    /// network's <paramref name="occupiedTiles"/> — the union of every member's tiles, already computed.
    ///
    /// This is a delegate, not an interface, because the common case is stateless: a factory that just
    /// constructs a node. A factory that needs collaborators (config, registries, a shape registry)
    /// closes over them in the lambda — the same reach the game's <c>IFactory&lt;,&gt;</c> classes get
    /// from instance fields, but without a dedicated type per family.
    /// </summary>
    public delegate IExpandableSimulation ExpandableSimulationFactory(
        IReadOnlyCollection<BuildingInstance> members,
        IReadOnlyList<GlobalTileCoordinate> occupiedTiles);
}
