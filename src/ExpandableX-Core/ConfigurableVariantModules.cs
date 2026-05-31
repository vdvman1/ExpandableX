using System.Collections.Generic;
using Core.Localization;
using Game.Core.Coordinates;
using ILogger = Core.Logging.ILogger;

namespace ExpandableX.Core
{
    /// <summary>
    /// Wraps a building's existing module provider and appends one dropdown per connector slot.
    /// Selecting a role swaps the building to the variant whose id encodes the new slot state
    /// (id-as-truth, ADR-0008) via a deferred Delete+Create. Only reachable roles are offered —
    /// a role is reachable iff the combination it produces exists in the piece's combo table
    /// (pruned combinations were never generated), so membership is the validity check.
    /// </summary>
    internal class ConfigurableVariantModules : IBuildingModules
    {
        private readonly IBuildingModules _inner;
        private readonly ExpandableXRegistry _registry;
        private readonly ILogger _logger;

        public ConfigurableVariantModules(IBuildingModules inner, ExpandableXRegistry registry, ILogger logger)
        {
            _inner = inner;
            _registry = registry;
            _logger = logger;
        }

        public IEnumerable<IHUDSidePanelModuleData> GetInfoModules(IMapModel map, BuildingModel building)
        {
            if (_inner != null)
            {
                foreach (IHUDSidePanelModuleData module in _inner.GetInfoModules(map, building))
                {
                    yield return module;
                }
            }

            string currentDefName = building.Definition.Id.Name;
            if (!_registry.VariantsByDefId.TryGetValue(currentDefName, out VariantPlacement placement))
            {
                yield break;
            }

            PieceVariantSet set = placement.Set;
            foreach (ConnectorSlot slot in set.Slots)
            {
                var options = new List<IText>();
                var targetDefs = new List<string>();
                int currentIndex = 0;

                foreach (SlotRole role in slot.AllowedRoles)
                {
                    string comboKey = VariantEncoder.ComboKey(set.Slots, WithRole(placement.SlotState, slot.Id, role));
                    if (!set.DefIdByComboKey.TryGetValue(comboKey, out string targetDef))
                    {
                        continue; // unreachable (pruned) — don't offer it
                    }

                    if (role == placement.SlotState[slot.Id])
                    {
                        currentIndex = options.Count;
                    }

                    options.Add(new RawText(role.ToString()));
                    targetDefs.Add(targetDef);
                }

                if (options.Count <= 1)
                {
                    continue; // nothing to choose
                }

                yield return new HUDSidePanelModuleInfoText.Data(new RawText(slot.Id));

                List<string> capturedTargets = targetDefs;
                IMapModel capturedMap = map;
                BuildingModel capturedBuilding = building;
                yield return new HUDSidePanelModuleDropdownSelector.Data(
                    options,
                    currentIndex,
                    index => SwapTo(capturedMap, capturedBuilding, currentDefName, capturedTargets[index]));
            }
        }

        public IEnumerable<IHUDSidePanelModuleData> GetInfoModules(IBuildingDefinition definition) =>
            _inner != null ? _inner.GetInfoModules(definition) : System.Array.Empty<IHUDSidePanelModuleData>();

        private void SwapTo(IMapModel map, BuildingModel building, string currentDefName, string targetDefName)
        {
            if (targetDefName == currentDefName)
            {
                return;
            }

            GameMode mode = _registry.CurrentMode;
            if (mode == null)
            {
                _logger.Info.Log("ExpandableX-Core: slot change: CurrentMode not set yet, aborting");
                return;
            }

#pragma warning disable CS0618
            BuildingDefinitionId targetId = new BuildingDefinitionId(targetDefName);
#pragma warning restore CS0618

            if (!mode.Buildings._DefinitionsById.TryGetValue(targetId, out IBuildingDefinition targetDef))
            {
                _logger.Info.Log($"ExpandableX-Core: slot change: target '{targetDefName}' not found, aborting");
                return;
            }

            GlobalTileTransform transform = building.Transform;
            BuildingId buildingId = building.Id;
            // Carry the existing per-instance configuration across the swap so any settings survive a
            // slot toggle (the target is the same building type, just different connectors). This is
            // null for a config-less building like the painter — do NOT fabricate one via
            // CreateConfiguration(): id-as-truth variants carry no configuration factory, so that call
            // would throw. The base and its variants share config-ness, so a null here is correct.
            IBuildingConfiguration carriedConfig = building.Configuration;
            IMapModel capturedMap = map;

            _registry.EnqueueDeferred(() =>
            {
                capturedMap.DeleteBuilding(in buildingId);
                capturedMap.CreateBuilding(targetDef, in transform, carriedConfig);
            });

            _logger.Info.Log($"ExpandableX-Core: slot change: queued swap {currentDefName} -> {targetDefName}");
        }

        private static IReadOnlyDictionary<string, SlotRole> WithRole(
            IReadOnlyDictionary<string, SlotRole> state, string slotId, SlotRole role)
        {
            var copy = new Dictionary<string, SlotRole>(state) { [slotId] = role };
            return copy;
        }
    }
}
