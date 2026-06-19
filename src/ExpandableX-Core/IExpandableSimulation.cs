using System;
using System.Collections.Generic;
using Game.Core.Map.Simulation;

namespace ExpandableX.Core
{
    /// <summary>
    /// The single runtime simulation object for one logical expandable building — the node the game's
    /// simulation graph consumes (<see cref="IConnectableSimulation"/>) and the tile lookup answers with
    /// (<see cref="ILocalizedTileSimulation"/>). One per connected component of <c>JoinJunction</c>-joined
    /// pieces (a standalone building is a one-member component); produced by an author-supplied
    /// <see cref="ExpandableSimulationFactory"/> (see CONTEXT.md "DynamicLayout", ADR-0012). The reusable
    /// <see cref="ExpandableSimulation"/> implements this; most consumers never implement it directly.
    ///
    /// The matcher (<see cref="ExpandableSimulationSystem"/>) treats this as opaque: it never inspects the
    /// gameplay logic. It hands the factory the network's occupied tiles at creation, so this answers
    /// <see cref="ILocalizedTileSimulation"/> from that footprint rather than recomputing it. On any
    /// change to the network's membership it disposes the old instance and
    /// asks the factory for a fresh one (rebuild-on-change — there is no incremental add/remove and
    /// no state carried between instances, so merge/split need no special handling). v1 networks are
    /// therefore stateless; a future stateful network would persist its state on its member
    /// buildings' own <c>State</c> containers and rehydrate it in the
    /// <see cref="ExpandableSimulationFactory"/> (the only serialized slot — see ADR-0012's amendment).
    /// </summary>
    public interface IExpandableSimulation : IConnectableSimulation, ILocalizedTileSimulation, IDisposable
    {
        /// <summary>The member buildings this network aggregates (introspection / debugging).</summary>
        IReadOnlyCollection<BuildingInstance> Members { get; }
    }
}
