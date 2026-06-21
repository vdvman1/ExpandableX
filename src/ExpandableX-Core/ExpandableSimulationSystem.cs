using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using Core.Events;
using Core.Events.Logging;
using Game.Core.Coordinates;
using Game.Core.Map.Simulation;
using ILogger = Core.Logging.ILogger;

namespace ExpandableX.Core
{
    /// <summary>
    /// The single, shared simulation system for every expandable family that has a simulation: it
    /// groups a family's placed pieces into connected components — joined through their
    /// <see cref="JoinJunction"/> connectors — and surfaces one runtime simulation per component
    /// (ADR-0012). A configurable singleton or any 0-join piece forms its own one-member component, so
    /// the same system (and the same author-supplied factory) serves the standalone building and the
    /// multi-piece network alike. It is added to the live simulation systems by
    /// <see cref="ExpandableXSimulationSystemsRewirer"/>; no game patching is involved.
    ///
    /// Selection is by catalog membership: a building participates iff its family (resolved from the
    /// definition id via the registry decode catalog) has a registered
    /// <see cref="ExpandableSimulationFactory"/>. Adjacency is geometric — a join connector at world
    /// pivot <c>P</c> meets another piece's join connector at <c>P</c>'s counterpart — and additionally
    /// same-family, so different families never fuse (the matcher is the second line of defence behind
    /// border-closing geometry). A member with no join connectors never fuses with anything.
    ///
    /// Network state is derived entirely from placement geometry + definition data; nothing
    /// per-building is stored, keeping saves/blueprints exact. Any change to a network's membership
    /// disposes its old simulation and asks the factory for a fresh one (rebuild-on-change), so merge
    /// and split need no special handling — they fall out of the connected-components recompute in
    /// <see cref="JoinNetworkGraph{A,B,C}"/>. Mass edits (placement, paste, save-load) are batched via
    /// the bunch-edit protocol so re-networking happens once at the end.
    ///
    /// Isolation from the vanilla fluid/signal networks is structural: membership and matching key
    /// only on <see cref="JoinJunction"/> (our own connector type) and all grouping state lives here.
    /// </summary>
    internal sealed class ExpandableSimulationSystem
        : IBuildingObserverSimulationSystem, ITileSimulationSystem, IBunchEditSystem, ISimulationSystem, IDisposable
    {
        private readonly record struct MemberRecord(string Family, GlobalTileCoordinate[] Tiles);

        private readonly record struct PendingMember(
            JoinMember<BuildingInstance, GlobalTilePivot, string> Member,
            GlobalTileCoordinate[] Tiles);

        private readonly ILogger _logger;
        private readonly ExpandableXRegistry _registry;
        private readonly IReadOnlyDictionary<string, ExpandableSimulationFactory> _factories;

        private readonly JoinNetworkGraph<BuildingInstance, GlobalTilePivot, string> _graph = new();

        private readonly MultiRegisterEvent<IConnectableSimulation> _onSimulationCreated = new();
        private readonly MultiRegisterEvent<IConnectableSimulation> _onBeforeSimulationDestroyed = new();
        private readonly HashSet<IConnectableSimulation> _simulations = new();

        // Committed graph members → the data we need to maintain the tile index and rebuild nodes.
        private readonly Dictionary<BuildingInstance, MemberRecord> _committed = new();
        // Every occupied tile → the member occupying it (the membership lookup, maintained for every
        // tracked family — including network families without a simulation factory). Independent of the
        // node index below: a member's tiles don't change as its network re-forms, so this is touched only
        // when a member is added/removed, not on every re-networking.
        private readonly Dictionary<GlobalTileCoordinate, BuildingInstance> _memberByTile = new();
        // Every occupied tile of every *simulated* network → that network's node (the tile-simulation
        // lookup). Only populated for families that supply a factory.
        private readonly Dictionary<GlobalTileCoordinate, IExpandableSimulation> _byTile = new();
        // Member → its network's node.
        private readonly Dictionary<BuildingInstance, IExpandableSimulation> _nodeByMember = new();

        // Bunch-edit accumulation: re-network once at FinishBunchEdit instead of per building.
        private bool _inBunch;
        private readonly Dictionary<BuildingInstance, PendingMember> _pendingAdds = new();
        private readonly HashSet<BuildingInstance> _pendingRemoves = new();

        public ExpandableSimulationSystem(
            ExpandableXRegistry registry,
            IReadOnlyDictionary<string, ExpandableSimulationFactory> factories,
            ILogger logger)
        {
            _registry = registry;
            _factories = factories;
            _logger = logger;
        }

        IEvent<IConnectableSimulation> ISimulationSystem.OnSimulationCreated => _onSimulationCreated;
        IEvent<IConnectableSimulation> ISimulationSystem.OnBeforeSimulationDestroyed => _onBeforeSimulationDestroyed;
        IEnumerable<IConnectableSimulation> ISimulationSystem.ConnectableSimulations => _simulations;

        public void BuildingWasAdded(in BuildingInstance building, IReadOnlyMapLayout layout)
        {
            if (!TryResolveMember(building, out PendingMember member))
            {
                return;
            }

            if (_inBunch)
            {
                _pendingAdds[building] = member;
                return;
            }

            ApplyAndProcess(new[] { member }, Array.Empty<BuildingInstance>());
        }

        public void BuildingWillBeRemoved(in BuildingInstance building, IReadOnlyMapLayout layout)
        {
            if (_inBunch)
            {
                // Defer; reconciled against pending adds at FinishBunchEdit.
                _pendingRemoves.Add(building);
                return;
            }

            if (!_committed.ContainsKey(building))
            {
                return;
            }

            ApplyAndProcess(Array.Empty<PendingMember>(), new[] { building });
        }

        public bool TryGetTileSimulation(in GlobalTileCoordinate position, out ILocalizedTileSimulation tileSimulation)
        {
            if (_byTile.TryGetValue(position, out IExpandableSimulation? node))
            {
                tileSimulation = node;
                return true;
            }

            tileSimulation = null!;
            return false;
        }

        /// <summary>
        /// The member buildings of the network occupying <paramref name="position"/>, if one is tracked
        /// here. This is the authoritative, already-computed membership (the connected component
        /// <see cref="JoinNetworkGraph{A,B,C}"/> maintains) — grow/shrink re-validation reads it instead of
        /// re-deriving the network, so the join-adjacency logic lives in exactly one place. Available for
        /// every tracked <see cref="Layout.Dynamic"/> family, including those without a simulation factory
        /// (membership is tracked even when no node is built).
        /// </summary>
        public bool TryGetNetworkMembers(in GlobalTileCoordinate position, [NotNullWhen(true)] out IReadOnlyCollection<BuildingInstance>? members)
        {
            if (_memberByTile.TryGetValue(position, out BuildingInstance member)
                && _graph.TryGetNetwork(member, out members))
            {
                return true;
            }

            members = null;
            return false;
        }

        public void StartBunchEdit()
        {
            _inBunch = true;
            _pendingAdds.Clear();
            _pendingRemoves.Clear();
        }

        public void FinishBunchEdit()
        {
            _inBunch = false;

            // A building added and removed within the same bunch is a net no-op; a building removed
            // that was never committed (e.g. removed-then-this-add cancelled it) is ignored.
            var adds = new List<PendingMember>(_pendingAdds.Count);
            foreach (KeyValuePair<BuildingInstance, PendingMember> pending in _pendingAdds)
            {
                if (!_pendingRemoves.Contains(pending.Key))
                {
                    adds.Add(pending.Value);
                }
            }

            var removes = new List<BuildingInstance>(_pendingRemoves.Count);
            foreach (BuildingInstance building in _pendingRemoves)
            {
                if (_committed.ContainsKey(building))
                {
                    removes.Add(building);
                }
            }

            _pendingAdds.Clear();
            _pendingRemoves.Clear();

            if (adds.Count > 0 || removes.Count > 0)
            {
                ApplyAndProcess(adds, removes);
            }
        }

        public void Dispose()
        {
            _onSimulationCreated.Dispose();
            _onBeforeSimulationDestroyed.Dispose();
        }

        /// <summary>Resolve whether a placed building belongs to a simulated family, computing its join faces (if any) and occupied tiles.</summary>
        private bool TryResolveMember(in BuildingInstance building, out PendingMember member)
        {
            member = default;

            // Read the connector data the game places by (CustomData), not the obsolete
            // Definition.ConnectorData — for a synthesised variant the former carries the variant's
            // actual connectors (including the join junctions emitted onto network pieces). This runs
            // for every placed building, so tolerate one without connector data rather than throwing.
            if (!building.Definition.CustomData.TryGet<IBuildingConnectorData>(out IBuildingConnectorData connectorData))
            {
                return false;
            }

            // Membership is by catalog + network-model layout, NOT by the presence of a join connector: a
            // 0-join building (a configurable singleton of a network family) is still a member — it forms
            // its own one-member component (JoinNetworkGraph handles a face-less member as a singleton
            // component). This is what lets one author-supplied factory serve the standalone building and
            // the multi-piece network through the same system (ADR-0012).
            //
            // We track every Layout.Dynamic family's graph, whether or not it supplies a simulation
            // factory: membership is what grow/shrink re-validation reads (issue #7), so a factory-less
            // network must still be grouped. The factory only gates whether Build() additionally creates a
            // runtime simulation node. Static families are simulated per-definition elsewhere and are not
            // tracked here.
            if (!_registry.VariantsByDefId.TryGetValue(building.Definition.Id.Name, out VariantPlacement? placement)
                || placement.Set.Layout is not Layout.Dynamic)
            {
                return false;
            }

            string family = placement.Set.Layout.LayoutId;

            // Don't double-simulate a def the game already simulates: an override target reuses a
            // pre-existing, separately-simulated def. (Synthesised variants and an author's own base are
            // ours to simulate.)
            if (placement.Set.Piece.VariantOverrides is { } overrides
                && overrides.Values.Contains(building.Definition.Id.Name))
            {
                return false;
            }

            // Join faces drive adjacency; a 0-join member produces an empty face list and never fuses.
            IReadOnlyList<JoinJunction> joins = connectorData.BuildingConnectorsOfType<JoinJunction>();
            var faces = new List<GlobalTilePivot>(joins.Count);
            foreach (JoinJunction join in joins)
            {
                faces.Add(CanonicalFace(join.Pivot(building.Transform)));
            }

            TileVector[] tiles = connectorData.Tiles;
            var global = new GlobalTileCoordinate[tiles.Length];
            for (int i = 0; i < tiles.Length; i++)
            {
                global[i] = tiles[i].ToGlobal(in building.Transform);
            }

            member = new PendingMember(
                new JoinMember<BuildingInstance, GlobalTilePivot, string>(building, family, faces),
                global);
            return true;
        }

        /// <summary>
        /// The shared identity of a join face: both pieces meeting at a face must produce the same
        /// key. A pivot and its counterpart name the same face from opposite sides, so we canonicalise
        /// to whichever of the two has a "positive" axis direction.
        /// </summary>
        private static GlobalTilePivot CanonicalFace(GlobalTilePivot pivot) =>
            IsCanonicalDirection(pivot.Direction) ? pivot : pivot.CounterpartConnector();

        private static bool IsCanonicalDirection(TileDirection direction) =>
            direction == TileDirection.East || direction == TileDirection.North || direction == TileDirection.Up;

        private void ApplyAndProcess(IReadOnlyList<PendingMember> adds, IReadOnlyList<BuildingInstance> removes)
        {
            List<JoinMember<BuildingInstance, GlobalTilePivot, string>> added = adds.Select(a => a.Member).ToList();
            NetworkDelta<BuildingInstance> delta = _graph.Apply(added, removes);

            // 1. Tear down the simulations of every network whose membership changed, clearing their
            //    tile-index entries (using the still-present committed records).
            foreach (IReadOnlyCollection<BuildingInstance> dissolved in delta.Dissolved)
            {
                TearDown(dissolved);
            }

            // 2. Reconcile committed members: drop removed (node tiles already cleared above), add new.
            //    The member→tile index tracks every member regardless of simulation, so maintain it here.
            foreach (BuildingInstance building in removes)
            {
                if (_committed.TryGetValue(building, out MemberRecord record))
                {
                    foreach (GlobalTileCoordinate tile in record.Tiles)
                    {
                        _memberByTile.Remove(tile);
                    }
                }
                _committed.Remove(building);
            }
            foreach (PendingMember add in adds)
            {
                _committed[add.Member.Id] = new MemberRecord(add.Member.Family, add.Tiles);
                foreach (GlobalTileCoordinate tile in add.Tiles)
                {
                    _memberByTile[tile] = add.Member.Id;
                }
            }

            // 3. Build a fresh simulation for each network that (re)formed.
            foreach (IReadOnlyCollection<BuildingInstance> formed in delta.Formed)
            {
                Build(formed);
            }
        }

        private void TearDown(IReadOnlyCollection<BuildingInstance> network)
        {
            BuildingInstance any = network.First();
            if (!_nodeByMember.TryGetValue(any, out IExpandableSimulation? node))
            {
                return;
            }

            _onBeforeSimulationDestroyed.InvokeSafe(node, _logger);
            _simulations.Remove(node);

            foreach (BuildingInstance member in network)
            {
                _nodeByMember.Remove(member);
                if (_committed.TryGetValue(member, out MemberRecord record))
                {
                    foreach (GlobalTileCoordinate tile in record.Tiles)
                    {
                        _byTile.Remove(tile);
                    }
                }
            }

            node.Dispose();
        }

        private void Build(IReadOnlyCollection<BuildingInstance> network)
        {
            BuildingInstance first = network.First();
            MemberRecord seed = _committed[first];

            // A network family without a simulation factory is tracked for membership only (its graph is
            // maintained above; grow/shrink re-validation still reads it). There is no node to build.
            if (!_factories.TryGetValue(seed.Family, out ExpandableSimulationFactory? createSimulation))
            {
                return;
            }

            // Compute the network footprint once: distinct buildings occupy distinct tiles, so the
            // union is a plain concatenation. This single set feeds both the node (its
            // ILocalizedTileSimulation geometry) and our tile→node index — no second computation.
            var footprint = new List<GlobalTileCoordinate>();
            foreach (BuildingInstance member in network)
            {
                footprint.AddRange(_committed[member].Tiles);
            }

            IExpandableSimulation node = createSimulation(network, footprint);
            _simulations.Add(node);

            foreach (BuildingInstance member in network)
            {
                _nodeByMember[member] = node;
            }
            foreach (GlobalTileCoordinate tile in footprint)
            {
                _byTile[tile] = node;
            }

            _onSimulationCreated.InvokeSafe(node, _logger);
        }
    }
}
