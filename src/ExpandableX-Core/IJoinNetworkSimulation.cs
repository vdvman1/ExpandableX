using System;
using System.Collections.Generic;
using Game.Core.Map.Simulation;

namespace ExpandableX.Core
{
    /// <summary>
    /// The single runtime simulation object for one logical network-model building — the node the
    /// game's simulation graph consumes (<see cref="IConnectableSimulation"/>) and the tile lookup
    /// answers with (<see cref="ILocalizedTileSimulation"/>). One per connected network of
    /// <c>JoinJunction</c>-joined pieces; produced by an author-supplied
    /// <see cref="IJoinNetworkSimulationFactory"/> (see CONTEXT.md "DynamicLayout", ADR-0012).
    ///
    /// The matcher (<see cref="JoinNetworkSystem"/>) treats this as opaque: it never inspects the
    /// gameplay logic. It hands the factory the network's occupied tiles at creation, so this answers
    /// <see cref="ILocalizedTileSimulation"/> from that footprint rather than recomputing it. On any
    /// change to the network's membership it disposes the old instance and
    /// asks the factory for a fresh one (rebuild-on-change — there is no incremental add/remove and
    /// no state carried between instances, so merge/split need no special handling). v1 networks are
    /// therefore stateless; a future stateful network would persist its state on its member
    /// buildings' own <c>State</c> containers and rehydrate it in
    /// <see cref="IJoinNetworkSimulationFactory.Create"/> (the only serialized slot — see ADR-0012's
    /// amendment).
    /// </summary>
    public interface IJoinNetworkSimulation : IConnectableSimulation, ILocalizedTileSimulation, IDisposable
    {
        /// <summary>The member buildings this network aggregates (introspection / debugging).</summary>
        IReadOnlyCollection<BuildingInstance> Members { get; }
    }
}
