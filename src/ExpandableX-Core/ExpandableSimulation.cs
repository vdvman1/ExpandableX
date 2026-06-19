using System.Collections.Generic;
using System.Linq;
using Game.Core.Coordinates;
using Game.Core.Map.Simulation;
using Game.Core.Simulation;

namespace ExpandableX.Core
{
    /// <summary>
    /// Reusable <see cref="IExpandableSimulation"/> node: the connectable/localized boilerplate every
    /// expandable building's simulation needs, independent of gameplay. It presents a supplied inner
    /// <see cref="ISimulation"/> and a supplied connector set to the game's simulation graph, and
    /// answers the tile footprint (bounds + occupied chunks) from the matcher-supplied occupied tiles.
    ///
    /// This mirrors the game's own generic connectable wrappers (e.g. <c>ConnectableRailEndSimulation
    /// &lt;T&gt;</c>, <c>ConnectablePathSimulation&lt;...&gt;</c>): the only building-specific parts are
    /// which connectors and which inner simulation — both passed in — so a consumer, or a thin
    /// specialisation like <see cref="SignalExpandableSimulation{TSimulation}"/>, supplies those and
    /// reuses everything here. Rebuilt from scratch on every membership change; holds nothing
    /// persistent of its own.
    /// </summary>
    public class ExpandableSimulation : IExpandableSimulation
    {
        private readonly List<ISimulationConnector> _connectors;
        private readonly GlobalTileCoordinate[] _tiles;
        private readonly List<GlobalChunkCoordinate> _chunks;
        private readonly GlobalTileBounds _bounds;

        public ExpandableSimulation(
            ISimulation simulation,
            IReadOnlyCollection<BuildingInstance> members,
            IReadOnlyList<GlobalTileCoordinate> occupiedTiles,
            IReadOnlyList<ISimulationConnector> connectors)
        {
            Simulation = simulation;
            Members = members;
            _connectors = connectors as List<ISimulationConnector> ?? connectors.ToList();
            _tiles = occupiedTiles as GlobalTileCoordinate[] ?? occupiedTiles.ToArray();
            _bounds = GlobalTileBounds.From(_tiles);

            // Distinct occupied chunks, derived from the footprint tiles (chunk = 20×20 tiles).
            _chunks = new List<GlobalChunkCoordinate>();
            foreach (GlobalTileCoordinate tile in _tiles)
            {
                GlobalChunkCoordinate chunk = tile.ToChunkCoordinate();
                if (!_chunks.Contains(chunk))
                {
                    _chunks.Add(chunk);
                }
            }
        }

        public IReadOnlyCollection<BuildingInstance> Members { get; }
        public ISimulation Simulation { get; }

        public int NumConnectors => _connectors.Count;
        public ISimulationConnector GetConnector(int index) => _connectors[index];

        public GlobalTileBounds TileBounds => _bounds;
        public int NumOccupiedTiles => _tiles.Length;
        public GlobalTileCoordinate GetOccupiedTile(int index) => _tiles[index];

        public int NumOccupiedChunks => _chunks.Count;
        public GlobalChunkCoordinate GetOccupiedChunk(int index) => _chunks[index];

        public virtual void Dispose() => _connectors.Clear();
    }
}
