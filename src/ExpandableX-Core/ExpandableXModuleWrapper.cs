using System.Collections.Generic;
using Core.Localization;
using Game.Core.Coordinates;
using ILogger = Core.Logging.ILogger;

namespace ExpandableX.Core
{
    internal class ExpandableXModuleWrapper : IBuildingModules
    {
        private readonly IBuildingModules _inner;
        private readonly ExpandableXRegistry _registry;
        private readonly ILogger _logger;

        public ExpandableXModuleWrapper(IBuildingModules inner, ExpandableXRegistry registry, ILogger logger)
        {
            _inner = inner;
            _registry = registry;
            _logger = logger;
        }

        public IEnumerable<IHUDSidePanelModuleData> GetInfoModules(IMapModel map, BuildingModel building)
        {
            foreach (IHUDSidePanelModuleData module in _inner.GetInfoModules(map, building))
            {
                yield return module;
            }

            PlacementKeybindingHintData swapAction = new PlacementKeybindingHintData
            {
                OverrideTitle = new RawText("ExpandableX: Swap to configurable variant"),
                OverrideDescription = new RawText("Swap this building to the configurable variant so per-instance slot state can be set."),
                Icon = null,
                Handler = () => SwapToConfigurable(map, building),
            };

            yield return new HUDSidePanelModuleActionButtons.Data(new[] { swapAction });
        }

        public IEnumerable<IHUDSidePanelModuleData> GetInfoModules(IBuildingDefinition definition)
        {
            return _inner.GetInfoModules(definition);
        }

        private void SwapToConfigurable(IMapModel map, BuildingModel building)
        {
            string sourceName = building.Definition.Id.Name;
            string configurableName = sourceName + "_ExpandableXConfigurable";

#pragma warning disable CS0618
            BuildingDefinitionId configurableId = new BuildingDefinitionId(configurableName);
#pragma warning restore CS0618

            GameMode mode = _registry.CurrentMode;
            if (mode == null)
            {
                _logger.Info.Log("ExpandableX-Core: swap button: CurrentMode not captured yet, aborting");
                return;
            }

            IBuildingDefinition configurableDef;
            try
            {
                configurableDef = mode.Buildings.GetDefinition(configurableId);
            }
            catch
            {
                _logger.Info.Log($"ExpandableX-Core: swap button: no configurable variant '{configurableName}' registered for '{sourceName}', aborting");
                return;
            }

            GlobalTileTransform transform = building.Transform;
            BuildingId originalBuildingId = building.Id;
            IMapModel capturedMap = map;
            IBuildingDefinition capturedDef = configurableDef;

            _logger.Info.Log($"ExpandableX-Core: swap button: enqueuing swap of {sourceName} at {transform} to {configurableName}");

            _registry.EnqueueDeferred(() =>
            {
                IBuildingConfiguration newConfig = capturedDef.CreateConfiguration();
                capturedMap.DeleteBuilding(in originalBuildingId);
                capturedMap.CreateBuilding(capturedDef, in transform, newConfig);
                _logger.Info.Log($"ExpandableX-Core: swap (deferred): done, new instance has config: {newConfig != null}");
            });
        }
    }
}
