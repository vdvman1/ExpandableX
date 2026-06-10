using Game.Core.Coordinates;
using Game.Core.Rendering.MeshGeneration;
using ShapezShifter.Flow.Atomic;
using ShapezShifter.Hijack;
using ILogger = Core.Logging.ILogger;

namespace ExpandableX
{
    /// <summary>
    /// Authors the configurable-base <c>MetaBuildingDefinition</c> for the network-model AND gate.
    /// It reuses the base-game AND gate's *visual* (the existing 3D model / draw data is cloned from
    /// its definition) but defines its own connector data: one signal connector on each of the four
    /// planar faces, so variant generation can place a join or a gameplay slot on any face (ADR-0010,
    /// ADR-0012).
    ///
    /// Runs as an <see cref="IBuildingsRewirer"/>, which fires while <c>GameBuildings</c> is built —
    /// strictly before the <c>ISimulationSystemsRewirer</c> variant generation that resolves this base
    /// by id, so the def is present in time. The base is added to <c>_DefinitionsById</c> and reuses
    /// the AND gate's group; it isn't surfaced as its own buildable entry (the player reaches its
    /// variants by swap, not by placing the maximal base directly).
    /// </summary>
    internal sealed class AndGateNetworkBaseRewirer : IBuildingsRewirer
    {
        /// <summary>The id of the authored configurable base, referenced by the AND-gate registration's piece.</summary>
        public const string BaseDefinitionId = "ExpandableXAndNetworkBase";

        private const string AndDefinitionId = "LogicGateAndInternalVariant";

        private readonly ILogger _logger;

        public AndGateNetworkBaseRewirer(ILogger logger) => _logger = logger;

        public GameBuildings ModifyGameBuildings(
            MetaGameModeBuildings meta,
            GameBuildings gameBuildings,
            IMeshCache meshCache,
            VisualThemeBaseResources theme)
        {
#pragma warning disable CS0618
            var andId = new BuildingDefinitionId(AndDefinitionId);
            var newId = new BuildingDefinitionId(BaseDefinitionId);
#pragma warning restore CS0618

            if (gameBuildings._DefinitionsById.ContainsKey(newId))
            {
                return gameBuildings; // already authored this session
            }

            if (!gameBuildings._DefinitionsById.TryGetValue(andId, out IBuildingDefinition andDef))
            {
                _logger.Info.Log($"ExpandableX: AND base '{AndDefinitionId}' not found; skipping network-AND base authoring");
                return gameBuildings;
            }

            // Aligned to the base-game AND gate (confirmed in-game: output East, inputs North + South)
            // with a third input added on the free West face — a 3-input AND. Variant generation flips
            // roles per slot and turns any face into a join, so this is just the native starting point.
            IBuildingConnectorData connectors = BuildingConnectors.SingleTile()
                .AddWireOutput(WireConnectorConfig.CustomOutput(TileDirection.East))
                .AddWireInput(WireConnectorConfig.CustomInput(TileDirection.North))
                .AddWireInput(WireConnectorConfig.CustomInput(TileDirection.South))
                .AddWireInput(WireConnectorConfig.CustomInput(TileDirection.West))
                .Build();

#pragma warning disable CS0618
            var def = new BuildingDefinition(newId, connectors);
#pragma warning restore CS0618

            // Reuse the AND gate's visual/group/etc. by cloning its CustomData, replacing only the
            // connector data with our four-face set (same dual-storage handling the Core rewirer uses
            // for synthesised variants).
            foreach (object item in andDef.CustomData.All)
            {
                if (item is IBuildingConnectorData)
                {
                    continue;
                }

                def.CustomData.Attach(item);
            }

            def.CustomData.Attach(connectors);

            gameBuildings._DefinitionsById.Add(newId, def);
            _logger.Info.Log($"ExpandableX: authored network-AND base '{BaseDefinitionId}' (4 signal faces, reusing the AND gate visual)");
            return gameBuildings;
        }
    }
}
