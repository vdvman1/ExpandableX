using System.Collections.Generic;
using Core.Localization;
using Game.Core.Coordinates;
using ILogger = Core.Logging.ILogger;

namespace ExpandableX.Core
{
    /// <summary>
    /// Wraps a building's existing module provider and appends the expandability UI: one button per
    /// connector-slot role, plus expand/shrink buttons for sequence layouts. Acting on a button swaps
    /// the building to the target variant/layout definition (id-as-truth, ADR-0008) via the undoable
    /// swap action. A slot role is offered iff its combination exists in the piece's table (pruned
    /// ones are absent → membership is the validity check); invalid/current options are shown disabled.
    ///
    /// UI text uses <see cref="RawText"/> — i.e. it is NOT translated. That's an accepted placeholder:
    /// translation support is deferred (and may never be needed if the eventual drag-handle UI doesn't
    /// surface this text). See the project note on untranslated UI text.
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
                    // Join is topology-driven (the grow/shrink action sets it), never a player choice —
                    // don't offer it as a slot-config button. (Per-face grow/shrink handles joins; #27.)
                    if (role == SlotRole.Join)
                    {
                        continue;
                    }

                    string comboKey = VariantEncoder.ComboKey(set.Slots, WithRole(placement.SlotState, slot.Id, role));
                    // Reachable iff its combination exists in the table (pruned combos are absent).
                    bool reachable = set.DefIdByComboKey.TryGetValue(comboKey, out string targetDef);
                    bool isCurrent = role == currentRole;
                    // A role is selectable only if it's reachable and not already current; otherwise the
                    // button is shown but disabled (ActiveIf=false) so the player sees the option exists.
                    bool selectable = reachable && !isCurrent;

                    // The closures below run later (button click / each frame). It's safe to capture
                    // map/building/currentDefName and the per-iteration loop locals directly: foreach
                    // variables are per-iteration in C#, and the rest are method-scope and never
                    // reassigned — no defensive per-iteration copies are needed.
                    buttons.Add(new PlacementKeybindingHintData
                    {
                        OverrideTitle = new RawText(isCurrent ? $"{role} (current)" : role.ToString()),
                        ActiveIf = () => selectable,
                        Handler = () =>
                        {
                            if (selectable)
                            {
                                SwapTo(map, building, currentDefName, targetDef);
                            }
                        },
                    });
                }

                yield return new HUDSidePanelModuleActionButtons.Data(buttons);
            }

            // Sequence expand/shrink (the cutter etc.): two buttons driven by the sequence engine.
            IReadOnlyList<ExpansionOption> expansions = SequenceEngine.OptionsFor(set.Registration, set.Layout);
            if (expansions.Count > 0)
            {
                var expandButtons = new List<PlacementKeybindingHintData>(expansions.Count);
                foreach (ExpansionOption option in expansions)
                {
                    // TODO(dynamic layouts): sequences only ever target static layouts. When chains
                    // (the AND gate) gain expand/shrink, the target may be a Layout.Dynamic, so this
                    // becomes a proper per-kind dispatch rather than a Static cast.
                    string? targetDef = (option.TargetLayout as Layout.Static)?.Piece.BaseDefinitionId;
                    bool enabled = option.Available && targetDef != null;

                    // Direct capture is safe (see the slot loop above): option/targetDef/enabled are
                    // per-iteration; map/building/currentDefName are method-scope and never reassigned.
                    expandButtons.Add(new PlacementKeybindingHintData
                    {
                        OverrideTitle = new RawText(DescribeOption(option)),
                        ActiveIf = () => enabled,
                        Handler = () =>
                        {
                            if (enabled && targetDef != null)
                            {
                                SwapTo(map, building, currentDefName, targetDef);
                            }
                        },
                    });
                }

                yield return new HUDSidePanelModuleActionButtons.Data(expandButtons);
            }
        }

        private static string DescribeOption(ExpansionOption option)
        {
            string label = option.Kind == ExpansionKind.Expand ? "Expand" : "Shrink";
            if (option.SkippedLayoutIds.Count > 0)
            {
                label += $" (skips {string.Join(", ", option.SkippedLayoutIds)})";
            }

            if (!option.Available && option.BlockedReason != null)
            {
                label += $" — {option.BlockedReason}";
            }

            return label;
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
