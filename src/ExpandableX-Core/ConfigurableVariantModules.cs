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
                yield return new HUDSidePanelModuleInfoText.Data(new RawText(slot.Id));

                SlotRole currentRole = placement.SlotState[slot.Id];
                var buttons = new List<PlacementKeybindingHintData>(slot.AllowedRoles.Count);

                foreach (SlotRole role in slot.AllowedRoles)
                {
                    string comboKey = VariantEncoder.ComboKey(set.Slots, WithRole(placement.SlotState, slot.Id, role));
                    // Reachable iff its combination exists in the table (pruned combos are absent).
                    bool reachable = set.DefIdByComboKey.TryGetValue(comboKey, out string targetDef);
                    bool isCurrent = role == currentRole;
                    // A role is selectable only if it's reachable and not already current; otherwise the
                    // button is shown but disabled (ActiveIf=false) so the player sees the option exists.
                    bool selectable = reachable && !isCurrent;

                    IMapModel capturedMap = map;
                    BuildingModel capturedBuilding = building;
                    buttons.Add(new PlacementKeybindingHintData
                    {
                        OverrideTitle = new RawText(isCurrent ? $"{role} (current)" : role.ToString()),
                        ActiveIf = () => selectable,
                        Handler = () =>
                        {
                            if (selectable)
                            {
                                SwapTo(capturedMap, capturedBuilding, currentDefName, targetDef);
                            }
                        },
                    });
                }

                yield return new HUDSidePanelModuleActionButtons.Data(buttons);
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
            PlayerActionManager playerActions = _registry.PlayerActions;
            Player executor = _registry.LocalPlayer;
            if (mode == null || playerActions == null || executor == null)
            {
                _logger.Info.Log("ExpandableX-Core: slot change: session managers not captured yet, aborting");
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

            // Schedule an undoable swap to the variant whose id encodes the new slot state. It keeps
            // the same BuildingId (so the HUD selection / panel survives) and carries the building's
            // configuration across — null for a config-less building like the painter, which is
            // correct (id-as-truth variants have no configuration factory). The action system runs it
            // at a safe point and records it on the undo stack; its reverse swaps back.
            var swap = new ExpandableXSwapVariantAction(
                map, executor, building.Id, building.Transform, building.Configuration, building.Definition, targetDef);
            playerActions.TryScheduleAction(swap);

            _logger.Info.Log($"ExpandableX-Core: slot change: scheduled swap {currentDefName} -> {targetDefName}");
        }

        private static IReadOnlyDictionary<string, SlotRole> WithRole(
            IReadOnlyDictionary<string, SlotRole> state, string slotId, SlotRole role)
        {
            var copy = new Dictionary<string, SlotRole>(state) { [slotId] = role };
            return copy;
        }
    }
}
