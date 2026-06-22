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

            // For a network (Dynamic) piece, a per-piece slot-role change can still leave the *whole
            // building* invalid: every combo is generated for network pieces (validity is resolved across
            // the network, not per piece — ADR-0012), so the variant exists and a plain reachability check
            // passes. Re-validate each candidate change against the layout's network predicates and gate it
            // like an unreachable option. Read the network once here. Null for a static/singleton layout or
            // a layout with no predicates — there the absent variants already prevent invalid states, so no
            // cross-piece check is needed.
            NetworkCandidate? network =
                set.Layout.NetworkPredicatesOf().Count > 0
                && NetworkCandidate.TryReadFrom(_registry, building.Transform.Position, out NetworkCandidate? read)
                    ? read
                    : null;

            foreach (ConnectorSlot slot in set.Slots)
            {
                SlotRole currentRole = placement.SlotState[slot.Id];

                // A join face is topology, owned by grow/shrink — never a configurable slot. Switching it
                // to a gameplay role would sever the join and break the network, so omit the slot entirely.
                if (currentRole == SlotRole.Join)
                {
                    continue;
                }

                yield return new HUDSidePanelModuleInfoText.Data(new RawText(slot.Id));

                var buttons = new List<PlacementKeybindingHintData>(slot.AllowedRoles.Count);

                foreach (SlotRole role in slot.AllowedRoles)
                {
                    // Join is topology-driven (the grow/shrink action sets it), never a player choice —
                    // don't offer it as a slot-config button. (Per-face grow/shrink handles joins; #27.)
                    if (role == SlotRole.Join)
                    {
                        continue;
                    }

                    // Resolve the swap target. For a network (canonicalised) piece a literal combo lookup
                    // misses — variants are stored one-per-rotational-class — so resolve via the world-face
                    // realisation, which also yields the rotation to place at. Reachable iff the family
                    // actually generates that combination (pruned/invalid combos resolve to nothing).
                    bool reachable = TryResolveSlotChange(
                        set, placement, building, slot, role, out string targetDef, out GridRotation targetRotation);
                    bool isCurrent = role == currentRole;

                    // A reachable, non-current change on a network piece must also not break the building's
                    // network predicates. blockedReason is the failing predicate's text (null if it holds).
                    string? blockedReason = null;
                    if (reachable && !isCurrent && network is not null)
                    {
                        blockedReason = network
                            .With(NetworkChange.Place(
                                building.Transform.Position,
                                new PieceState(set.Piece, set.Slots, WithRole(placement.SlotState, slot.Id, role))))
                            .FirstViolation();
                    }

                    // A role is selectable only if it's reachable, not already current, and wouldn't leave
                    // the network invalid; otherwise the button is shown but disabled (ActiveIf=false) so
                    // the player sees the option exists and, when blocked, why.
                    bool selectable = reachable && !isCurrent && blockedReason is null;

                    // The closures below run later (button click / each frame). It's safe to capture
                    // map/building/currentDefName and the per-iteration loop locals directly: foreach
                    // variables are per-iteration in C#, and the rest are method-scope and never
                    // reassigned — no defensive per-iteration copies are needed.
                    buttons.Add(new PlacementKeybindingHintData
                    {
                        OverrideTitle = new RawText(
                            isCurrent ? $"{role} (current)"
                            : blockedReason is not null ? $"{role} — {blockedReason}"
                            : role.ToString()),
                        ActiveIf = () => selectable,
                        Handler = () =>
                        {
                            if (selectable)
                            {
                                SwapTo(map, building, currentDefName, targetDef, targetRotation);
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
                                // A sequence swaps to a different building def at the same orientation.
                                SwapTo(map, building, currentDefName, targetDef, building.Transform.Rotation);
                            }
                        },
                    });
                }

                yield return new HUDSidePanelModuleActionButtons.Data(expandButtons);
            }

            // Network-model grow/shrink (the AND gate): a grow button per growable face plus a shrink
            // button on a removable end piece, driven by NetworkExpansionEngine. Each option carries a
            // ready-to-run undoable action; clicking just schedules it (like SwapTo). These per-face
            // buttons are the stepping stone to drag handles (ADR-0002). Needs the session managers, so
            // skip the section until they're captured.
            Player executor = _registry.LocalPlayer;
            PlayerActionManager playerActions = _registry.PlayerActions;
            if (executor != null && playerActions != null && set.Layout is Layout.Dynamic)
            {
                var growButtons = new List<PlacementKeybindingHintData>();
                foreach (GrowOption grow in NetworkExpansionEngine.GrowOptionsFor(map, executor, _registry, building))
                {
                    // Per-iteration locals are safe to capture (see the slot loop); playerActions is
                    // method-scope and never reassigned.
                    IPlayerAction? action = grow.Action;
                    bool enabled = grow.Available && action != null;
                    growButtons.Add(new PlacementKeybindingHintData
                    {
                        OverrideTitle = new RawText(enabled ? $"Grow {grow.Face}" : $"Grow {grow.Face} — {grow.BlockedReason}"),
                        ActiveIf = () => enabled,
                        Handler = () =>
                        {
                            if (enabled && action != null)
                            {
                                playerActions.TryScheduleAction(action);
                            }
                        },
                    });
                }

                if (growButtons.Count > 0)
                {
                    yield return new HUDSidePanelModuleInfoText.Data(new RawText("Grow"));
                    yield return new HUDSidePanelModuleActionButtons.Data(growButtons);
                }

                ShrinkOption? shrink = NetworkExpansionEngine.ShrinkOptionFor(map, executor, _registry, building);
                if (shrink != null)
                {
                    IPlayerAction? shrinkAction = shrink.Action;
                    bool shrinkEnabled = shrink.Available && shrinkAction != null;
                    yield return new HUDSidePanelModuleActionButtons.Data(new[]
                    {
                        new PlacementKeybindingHintData
                        {
                            OverrideTitle = new RawText(shrinkEnabled ? "Shrink (remove this piece)" : $"Shrink — {shrink.BlockedReason}"),
                            ActiveIf = () => shrinkEnabled,
                            Handler = () =>
                            {
                                if (shrinkEnabled && shrinkAction != null && playerActions.TryScheduleAction(shrinkAction))
                                {
                                    // Move focus to the surviving neighbour once the shrink settles, so
                                    // configuring continues on the remaining network (the removed end was
                                    // the focus). Applied by the selection manager on the membership change.
                                    _registry.NetworkSelection?.RequestFocusAfterChange(shrink.FocusAfter);
                                }
                            },
                        },
                    });
                }
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

        /// <summary>
        /// Resolve the definition (and rotation) a slot-role change should swap to. For a network
        /// (<see cref="Layout.Dynamic"/>) piece this is canonicalisation-aware — variants are generated
        /// one-per-rotational-class, so the changed world-face assignment is realised back to its
        /// canonical def + the rotation that reproduces it (<see cref="NetworkPieceRealization"/>). For a
        /// static layout the table holds every literal combo at the placed rotation, so a direct lookup
        /// suffices. Returns false when the family doesn't generate the result (pruned/invalid).
        /// </summary>
        private bool TryResolveSlotChange(
            PieceVariantSet set, VariantPlacement placement, BuildingModel building,
            ConnectorSlot slot, SlotRole role, out string targetDef, out GridRotation targetRotation)
        {
            GridRotation rotation = building.Transform.Rotation;
            targetRotation = rotation;

            if (set.Layout is Layout.Dynamic
                && set.SlotFaceDirections is { } faces
                && faces.TryGetValue(slot.Id, out TileDirection localFace))
            {
                var worldFaces = new Dictionary<TileDirection, SlotRole>(
                    NetworkPieceRealization.WorldFaceRoles(set, placement.SlotState, rotation))
                {
                    [localFace.Rotate(rotation)] = role,
                };
                return NetworkPieceRealization.TryRealize(set, worldFaces, rotation, out targetDef, out targetRotation);
            }

            string comboKey = VariantEncoder.ComboKey(set.Slots, WithRole(placement.SlotState, slot.Id, role));
            return set.DefIdByComboKey.TryGetValue(comboKey, out targetDef);
        }

        private void SwapTo(IMapModel map, BuildingModel building, string currentDefName, string targetDefName, GridRotation targetRotation)
        {
            if (targetDefName == currentDefName && targetRotation == building.Transform.Rotation)
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
            // correct (id-as-truth variants have no configuration factory). The target rotation may
            // differ from the current one for a network piece (its orientation is realised via
            // GridRotation), but the connectors land on the same world faces by construction. The
            // action system runs it at a safe point and records it on the undo stack; its reverse swaps back.
            var transform = new GlobalTileTransform(building.Transform.Position, targetRotation);
            var swap = new ExpandableXSwapVariantAction(
                map, executor, building.Id, transform, building.Configuration, building.Definition, targetDef);
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
