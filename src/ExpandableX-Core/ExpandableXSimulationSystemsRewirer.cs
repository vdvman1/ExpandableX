using ShapezShifter.Hijack;
using System.Collections.Generic;
using System.Linq;
using ILogger = Core.Logging.ILogger;

namespace ExpandableX.Core
{
    /// <summary>
    /// Per session, explodes each registration's pieces into id-encoded variant definitions
    /// (ADR-0008/0009/0010): resolves the configurable base by id, synthesises each reachable
    /// slot-role combination's connector data, registers the new definitions in a hidden group,
    /// attaches simulation systems, and records a decode catalog on the registry. The combination
    /// matching the base's own connector roles reuses the base definition (no redundant def);
    /// explicit overrides reuse a named pre-existing definition.
    /// </summary>
    internal class ExpandableXSimulationSystemsRewirer : ISimulationSystemsRewirer
    {
        private static bool _loggedAvailableDefinitions;

        private readonly ILogger _logger;
        private readonly ExpandableXRegistry _registry;

        public ExpandableXSimulationSystemsRewirer(ILogger logger, ExpandableXRegistry registry)
        {
            _logger = logger;
            _registry = registry;
        }

        public void ModifySimulationSystems(
            ICollection<ISimulationSystem> simulationSystems,
            SimulationSystemsDependencies dependencies)
        {
            _registry.CurrentMode = dependencies.Mode;
            _logger.Info.Log($"ExpandableX-Core: rewirer firing — {_registry.Registrations.Count} registration(s) to process");

            foreach (Registration registration in _registry.Registrations.Values)
            {
                ProcessRegistration(registration, simulationSystems, dependencies);
            }

            AttachExpandableSimulationSystem(simulationSystems, dependencies);
        }

        /// <summary>
        /// Add the shared network matcher (<see cref="ExpandableSimulationSystem"/>) when at least one
        /// registered <see cref="Layout.Dynamic"/> supplies a simulation factory. Keyed per family by
        /// layout id, so the one system handles every network-model family — each connected component of
        /// joined pieces (a singleton being the one-member case) is served by its family's factory.
        /// Static layouts are simulated separately, per definition, by their own atomic installer.
        /// With no dynamic factories registered nothing is added.
        /// </summary>
        private void AttachExpandableSimulationSystem(
            ICollection<ISimulationSystem> simulationSystems,
            SimulationSystemsDependencies dependencies)
        {
            var factories = new Dictionary<string, ExpandableSimulationFactory>();
            foreach (Registration registration in _registry.Registrations.Values)
            {
                foreach (Layout layout in registration.Layouts)
                {
                    if (layout is Layout.Dynamic { SimulationFactory: { } factory })
                    {
                        factories[layout.LayoutId] = factory;
                    }
                }
            }

            if (factories.Count == 0)
            {
                return;
            }

            simulationSystems.Add(new ExpandableSimulationSystem(_registry, factories, dependencies.Logger));
            _logger.Info.Log($"ExpandableX-Core: attached ExpandableSimulationSystem for {factories.Count} simulated family(ies)");
        }

        private void ProcessRegistration(
            Registration registration,
            ICollection<ISimulationSystem> simulationSystems,
            SimulationSystemsDependencies dependencies)
        {
            // RegistrationId is a logical family id, NOT a game group (ADR-0011 / family-scoped
            // registrations). A family may span several groups (cutter: CutterHalfVariant +
            // CutterDefaultVariant) and a slot-less family (sequences only) needs no hidden group at
            // all. So hidden groups are created lazily, keyed by each base definition's own group,
            // only when a piece actually synthesises variants.
            var hiddenGroups = new Dictionary<BuildingDefinitionGroupId, BuildingDefinitionGroup>();

            int synthesised = 0;
            foreach (Layout layout in registration.Layouts)
            {
                foreach (PieceSpec piece in layout.EnumeratePieceSpecs())
                {
                    synthesised += GenerateVariants(registration, layout, piece, hiddenGroups, simulationSystems, dependencies);
                }
            }

            _logger.Info.Log($"ExpandableX-Core: family '{registration.RegistrationId}': {synthesised} synthesised variant(s) across {hiddenGroups.Count} hidden group(s)");
        }

        /// <summary>Generate every variant for one piece against its configurable base. Returns the number of synthesised definitions created.</summary>
        private int GenerateVariants(
            Registration registration,
            Layout layout,
            PieceSpec piece,
            Dictionary<BuildingDefinitionGroupId, BuildingDefinitionGroup> hiddenGroups,
            ICollection<ISimulationSystem> simulationSystems,
            SimulationSystemsDependencies dependencies)
        {
#pragma warning disable CS0618
            BuildingDefinitionId baseDefId = new BuildingDefinitionId(piece.BaseDefinitionId);
#pragma warning restore CS0618

            if (!dependencies.Mode.Buildings._DefinitionsById.TryGetValue(baseDefId, out IBuildingDefinition baseDef))
            {
                // Base definition ids are asset-assigned; if one is wrong, the one-time discovery dump
                // below lists every real id so the consumer can correct its registration.
                _logger.Info.Log($"ExpandableX-Core: base definition '{piece.BaseDefinitionId}' not found for a piece in family '{registration.RegistrationId}' — skipping piece");
                LogAvailableDefinitionsOnce(dependencies);
                return 0;
            }

            var resolver = new ConnectorDataResolver(baseDef.ConnectorData);

            // Expand slots once and, for a network-model (DynamicLayout) piece, resolve each slot's
            // planar face. We canonicalise (one definition per rotational class of join-face set, the
            // rest realised via GridRotation — ADR-0012) only when the piece is a clean planar
            // four-face shape; otherwise every variant is generated as-is.
            var slots = piece.SlotSpecs.SelectMany(s => s.Expand(resolver)).ToList();
            IReadOnlyDictionary<string, TileDirection>? slotFaces =
                layout is Layout.Dynamic ? ResolveSlotFaces(slots, resolver) : null;
            bool canonicalize = slotFaces != null && IsPlanarFourFace(slots, slotFaces);

            // Re-key the overrides onto the canonical orientation of each one's rotation class before
            // exploding, so an override the author declared on any orientation lands on whichever
            // orientation is actually generated.
            PieceSpec effectivePiece = canonicalize && piece.VariantOverrides != null
                ? piece with { VariantOverrides = CanonicalizeOverrideKeys(piece.Overrides, slots, slotFaces!) }
                : piece;

            PieceExpansion expansion = VariantEncoder.ExplodePiece(effectivePiece, resolver);

            // Network predicates describe a complete building. A singleton (0-join) variant IS a
            // complete one-piece building, so it must satisfy them too — prune any that can't. This is
            // what lets one declaration of e.g. "at least one input + one output" both gate networks at
            // runtime and trim the impossible singleton variants here, with no separate local predicate.
            // Network pieces (>=1 join) are partial (another piece may supply the missing role), so they
            // are validated only at runtime, not pruned here.
            IReadOnlyList<INetworkPredicate> networkPredicates = layout.NetworkPredicatesOf();
            bool SingletonSatisfiesNetwork(Variant v) =>
                HasJoin(v.SlotState)
                || networkPredicates.Count == 0
                || networkPredicates.All(p => p.IsValid(new NetworkState(layout, new[]
                {
                    new PieceState(effectivePiece, expansion.ExpandedSlots, v.SlotState),
                })));

            // Canonicalise only the network (>=1-join) variants: their rotation is framework-managed by
            // grow/shrink, so one def per rotational class is right. Singleton (0-join) variants are
            // placed and rotated by the player, so each local config is its own def (no rotation reuse) —
            // exactly like a static slot config; canonicalising them would hijack the player's rotation
            // and break the slot UI.
            bool CanonicalKept(Variant v) =>
                !canonicalize || !HasJoin(v.SlotState) || RotationCanonicalizer.IsCanonical(FaceMap(v.SlotState, slotFaces!));

            IReadOnlyList<Variant> variants = expansion.Variants
                .Where(v => SingletonSatisfiesNetwork(v) && CanonicalKept(v))
                .ToList();

            string kind = layout is Layout.Dynamic ? "dynamic" : "static";
            _logger.Info.Log(
                $"ExpandableX-Core:   {kind} piece '{baseDef.Id.Name}': {variants.Count} kept of {expansion.Variants.Count} " +
                $"(network pieces canonicalised, singletons trimmed by network predicates; {expansion.Pruned.Count} locally pruned)");

            // First pass: resolve each combination to a definition id (synthesising new defs as needed).
            var defIdByComboKey = new Dictionary<string, string>(variants.Count);
            var placements = new List<(string DefIdName, IReadOnlyDictionary<string, SlotRole> State)>(variants.Count);
            int synthesised = 0;

            foreach (Variant variant in variants)
            {
                string comboKey = VariantEncoder.ComboKey(expansion.ExpandedSlots, variant.SlotState);
                string defIdName = ResolveOrSynthesise(
                    variant, layout, baseDef, effectivePiece, expansion, resolver, hiddenGroups, simulationSystems, dependencies, ref synthesised);

                defIdByComboKey[comboKey] = defIdName;
                placements.Add((defIdName, variant.SlotState));
            }

            // Second pass: register each placement against the shared, complete table.
            var set = new PieceVariantSet(registration, layout, effectivePiece, baseDef.Id.Name, expansion.ExpandedSlots, defIdByComboKey, slotFaces);
            foreach ((string defIdName, IReadOnlyDictionary<string, SlotRole> state) in placements)
            {
                _registry.RecordVariant(defIdName, new VariantPlacement(set, state));
            }

            return synthesised;
        }

        /// <summary>Map a combination to its definition id, creating a synthesised definition when it isn't the base or an override.</summary>
        private string ResolveOrSynthesise(
            Variant variant,
            Layout layout,
            IBuildingDefinition baseDef,
            PieceSpec piece,
            PieceExpansion expansion,
            ConnectorDataResolver resolver,
            Dictionary<BuildingDefinitionGroupId, BuildingDefinitionGroup> hiddenGroups,
            ICollection<ISimulationSystem> simulationSystems,
            SimulationSystemsDependencies dependencies,
            ref int synthesised)
        {
            // The combination matching the base's own connector roles is the base definition itself.
            // (A slot-less piece — e.g. a cutter sequence layout — always matches here, so it's
            // cataloged against its base def id and synthesises nothing.)
            if (MatchesBase(expansion.ExpandedSlots, variant.SlotState, resolver))
            {
                return baseDef.Id.Name;
            }

            // Otherwise variant.DefinitionId is an explicit override target or a synthesised id.
            string defIdName = variant.DefinitionId;

#pragma warning disable CS0618
            BuildingDefinitionId defId = new BuildingDefinitionId(defIdName);
#pragma warning restore CS0618

            if (dependencies.Mode.Buildings._DefinitionsById.ContainsKey(defId))
            {
                // Pre-existing definition — an explicit override, or one we already synthesised this
                // session (re-run). Reuse it; don't synthesise again.
                return defIdName;
            }

            BuildingDefinitionGroup? hiddenGroup = GetOrCreateHiddenGroup(baseDef, hiddenGroups, dependencies);
            if (hiddenGroup is null)
            {
                _logger.Info.Log($"ExpandableX-Core: could not resolve a group for base '{baseDef.Id.Name}'; cataloging variant '{defIdName}' as the base def");
                return baseDef.Id.Name;
            }

            IBuildingConnectorData synthData = ConnectorSynthesizer.Synthesize(
                baseDef.ConnectorData, resolver, expansion.ExpandedSlots, variant.SlotState);

#pragma warning disable CS0618
            BuildingDefinition variantDef = new BuildingDefinition(defId, synthData);
#pragma warning restore CS0618

            foreach (object item in baseDef.CustomData.All)
            {
                // Connector data lives in BOTH BuildingDefinition.ConnectorData and CustomData
                // (e.g. BuildingModel reads CustomData.Get<IBuildingConnectorData>() for placement).
                // Skip the base's original connectors and attach the synthesised set instead, so
                // every reader sees the same variant connectors — otherwise placement/attraction
                // would still "see" a disabled connector the fluid network has correctly dropped.
                if (item is IBuildingConnectorData)
                {
                    continue;
                }

                variantDef.CustomData.Attach(item);
            }

            variantDef.CustomData.Attach(synthData);

            hiddenGroup.AddInternalVariant(variantDef);
            dependencies.Mode.Buildings._DefinitionsById.Add(variantDef.Id, variantDef);

            // Attach the static layout's per-definition simulation to this newly synthesised variant —
            // the way regular buildings are simulated. Only synthesised variants reach here (the base and
            // override targets returned earlier, so they keep the game's own simulation). A dynamic layout
            // simulates via the network system instead and installs nothing here.
            (layout as Layout.Static)?.Simulation?.Invoke(variantDef, simulationSystems, dependencies);
            synthesised++;
            return defIdName;
        }

        /// <summary>
        /// The hidden, non-buildable group that houses a base definition's synthesised variants —
        /// one per base group, created lazily and copying that group's render/placement properties.
        /// Idempotent across same-session re-runs. Returns null if the base's group can't be found.
        /// </summary>
        private BuildingDefinitionGroup? GetOrCreateHiddenGroup(
            IBuildingDefinition baseDef,
            Dictionary<BuildingDefinitionGroupId, BuildingDefinitionGroup> cache,
            SimulationSystemsDependencies dependencies)
        {
            if (!baseDef.CustomData.TryGet<IBuildingDefinitionGroup>(out IBuildingDefinitionGroup groupData)
                || groupData is not BuildingDefinitionGroup baseGroup)
            {
                return null;
            }

#pragma warning disable CS0618
            BuildingDefinitionGroupId hiddenId = new BuildingDefinitionGroupId(baseGroup.Id.Id + "_ExpandableXConfigurable");
#pragma warning restore CS0618

            if (cache.TryGetValue(hiddenId, out BuildingDefinitionGroup cached))
            {
                return cached;
            }

            if (dependencies.Mode.Buildings._VariantsById.TryGetValue(hiddenId, out IBuildingDefinitionGroup existing)
                && existing is BuildingDefinitionGroup existingGroup)
            {
                cache[hiddenId] = existingGroup; // already created this session
                return existingGroup;
            }

            BuildingDefinitionGroup hiddenGroup = CreateHiddenGroup(hiddenId, baseGroup);
            dependencies.Mode.Buildings._VariantsById.Add(hiddenId, hiddenGroup);
            dependencies.Mode.Buildings._All.Add(hiddenGroup);
            cache[hiddenId] = hiddenGroup;
            return hiddenGroup;
        }

        /// <summary>Once per process, dump every definition id so a consumer can find the real id behind a wrong base id.</summary>
        private void LogAvailableDefinitionsOnce(SimulationSystemsDependencies dependencies)
        {
            if (_loggedAvailableDefinitions)
            {
                return;
            }

            _loggedAvailableDefinitions = true;
            var ids = dependencies.Mode.Buildings._DefinitionsById.Keys.Select(k => k.Name).OrderBy(n => n);
            _logger.Info.Log($"ExpandableX-Core: discovery — all definition ids: {string.Join(", ", ids)}");

            // Also dump research upgrade ids so consumers can find the id behind a research gate
            // (e.g. the cutter unlock), the same way they find a base definition id.
            var researchIds = dependencies.Mode.ResearchLayout.AllUpgrades.Select(u => u.Id.ToString()).OrderBy(n => n);
            _logger.Info.Log($"ExpandableX-Core: discovery — all research upgrade ids: {string.Join(", ", researchIds)}");
        }

        /// <summary>True when every slot's role equals the role its base connector already has (so synthesis would reproduce the base).</summary>
        private static bool MatchesBase(
            IReadOnlyList<ConnectorSlot> slots,
            IReadOnlyDictionary<string, SlotRole> state,
            ConnectorDataResolver resolver)
        {
            foreach (ConnectorSlot slot in slots)
            {
                IBuildingIO? connector = resolver.ResolveVisible(slot.Connector);
                if (connector is null)
                {
                    return false;
                }

                if (state[slot.Id] != ConnectorFactory.NativeRole(connector))
                {
                    return false;
                }
            }

            return true;
        }

        /// <summary>Resolve each slot's planar face from its base connector (slot id → face direction).</summary>
        private static IReadOnlyDictionary<string, TileDirection> ResolveSlotFaces(
            IReadOnlyList<ConnectorSlot> slots, ConnectorDataResolver resolver)
        {
            var faces = new Dictionary<string, TileDirection>(slots.Count);
            foreach (ConnectorSlot slot in slots)
            {
                if (resolver.ResolveVisible(slot.Connector) is BuildingBaseIO geometry)
                {
                    faces[slot.Id] = geometry.TileDirection;
                }
            }

            return faces;
        }

        /// <summary>
        /// Re-key a piece's variant overrides onto the canonical orientation of each one's rotational
        /// class, so an override the author declared on any orientation applies to the single
        /// generated def for that class (ADR-0012). Only used for canonicalised (planar four-face) pieces.
        /// </summary>
        private static IReadOnlyDictionary<string, string> CanonicalizeOverrideKeys(
            IReadOnlyDictionary<string, string> overrides,
            IReadOnlyList<ConnectorSlot> slots,
            IReadOnlyDictionary<string, TileDirection> slotFaces)
        {
            var result = new Dictionary<string, string>(overrides.Count);
            foreach (KeyValuePair<string, string> entry in overrides)
            {
                IReadOnlyDictionary<string, SlotRole> state = ParseComboKey(slots, entry.Key);

                // Only network (>=1-join) variants are canonicalised, so only their override keys need
                // re-keying onto the canonical orientation. A 0-join (singleton) override stays exactly
                // as declared — those variants are generated in full, un-canonicalised.
                result[HasJoin(state) ? VariantEncoder.ComboKey(slots, CanonicalState(state, slots, slotFaces)) : entry.Key]
                    = entry.Value;
            }

            return result;
        }

        private static bool HasJoin(IReadOnlyDictionary<string, SlotRole> state) => state.Values.Contains(SlotRole.Join);

        /// <summary>Decode a combo key (one role char per slot, in slot order) back into a slot-role map.</summary>
        private static IReadOnlyDictionary<string, SlotRole> ParseComboKey(IReadOnlyList<ConnectorSlot> slots, string comboKey)
        {
            var state = new Dictionary<string, SlotRole>(slots.Count);
            for (int i = 0; i < slots.Count && i < comboKey.Length; i++)
            {
                state[slots[i].Id] = RoleAlphabet.Decode(comboKey[i]);
            }

            return state;
        }

        /// <summary>The canonical-orientation slot-role map for a state: rotate its face map to canonical, then map back to slots.</summary>
        private static IReadOnlyDictionary<string, SlotRole> CanonicalState(
            IReadOnlyDictionary<string, SlotRole> state,
            IReadOnlyList<ConnectorSlot> slots,
            IReadOnlyDictionary<string, TileDirection> slotFaces)
        {
            IReadOnlyDictionary<TileDirection, SlotRole> canonicalFaces =
                RotationCanonicalizer.Canonicalize(FaceMap(state, slotFaces)).Canonical;

            var canonical = new Dictionary<string, SlotRole>(slots.Count);
            foreach (ConnectorSlot slot in slots)
            {
                if (slotFaces.TryGetValue(slot.Id, out TileDirection face) && canonicalFaces.TryGetValue(face, out SlotRole role))
                {
                    canonical[slot.Id] = role;
                }
            }

            return canonical;
        }

        /// <summary>Project a slot-role combination onto its faces (slot id → face direction → role).</summary>
        private static IReadOnlyDictionary<TileDirection, SlotRole> FaceMap(
            IReadOnlyDictionary<string, SlotRole> state, IReadOnlyDictionary<string, TileDirection> slotFaces)
        {
            var map = new Dictionary<TileDirection, SlotRole>(slotFaces.Count);
            foreach (KeyValuePair<string, TileDirection> slotFace in slotFaces)
            {
                map[slotFace.Value] = state[slotFace.Key];
            }

            return map;
        }

        /// <summary>True when the slots are exactly the four planar faces (East/South/West/North), one slot each.</summary>
        private static bool IsPlanarFourFace(
            IReadOnlyList<ConnectorSlot> slots, IReadOnlyDictionary<string, TileDirection> slotFaces)
        {
            if (slots.Count != 4 || slotFaces.Count != 4)
            {
                return false;
            }

            var faces = new HashSet<TileDirection>(slotFaces.Values);
            return faces.Count == 4
                && faces.Contains(TileDirection.East) && faces.Contains(TileDirection.South)
                && faces.Contains(TileDirection.West) && faces.Contains(TileDirection.North);
        }

        private static BuildingDefinitionGroup CreateHiddenGroup(
            BuildingDefinitionGroupId configurableGroupId,
            BuildingDefinitionGroup sourceGroup) =>
            new BuildingDefinitionGroup(
                id: configurableGroupId,
                icon: sourceGroup.Icon,
                title: sourceGroup.Title,
                description: sourceGroup.Description,
                isTransportBuilding: sourceGroup.IsTransportBuilding,
                selectable: false,
                playerBuildable: false,
                removable: sourceGroup.Removable,
                allowPlaceOnNonFilledTiles: sourceGroup.AllowPlaceOnNonFilledTiles,
                pipetteOverrideId: sourceGroup.PipetteOverrideId,
                defaultPreferredPlacementMode: sourceGroup.DefaultPreferredPlacementMode,
                allowPlaceOnNotch: sourceGroup.AllowPlaceOnNotch,
                autoAttractIOScoreMultiplier: sourceGroup.AutoAttractIOScoreMultiplier,
                autoConnect: sourceGroup.AutoConnect,
                autoRotateToFitStructures: sourceGroup.AutoRotateToFitStructures,
                allowNonForcingReplacementByOtherBuildings: sourceGroup.AllowNonForcingReplacementByOtherBuildings,
                shouldSkipReplacementIOChecks: sourceGroup.ShouldSkipReplacementIOChecks,
                alwaysProducesConflictIndicators: sourceGroup.AlwaysProducesConflictIndicators,
                renderConflictIndicatorMeshes: sourceGroup.RenderConflictIndicatorMeshes,
                renderConflictIndicatorVisualization: sourceGroup.RenderConflictIndicatorVisualization,
                renderConnectorIndicators: sourceGroup.RenderConnectorIndicators,
                renderConflictingConnectorIndicators: sourceGroup.RenderConflictingConnectorIndicators,
                showNotchIndicators: sourceGroup.ShowNotchIndicators,
                showStatBeltProcessingTime: sourceGroup.ShowStatBeltProcessingTime,
                showStatBuildingsPerFullBelt: sourceGroup.ShowStatBuildingsPerFullBelt,
                showInSpeedOverview: sourceGroup.ShowInSpeedOverview,
                showAsResearchReward: false,
                requireStoreContentId: sourceGroup.RequiredStoreContentId,
                linkedWikiEntry: sourceGroup.LinkedWikiEntry,
                placementIndicatorTypes: sourceGroup.PlacementIndicatorTypes,
                placementRequirements: sourceGroup.PlacementRequirements,
                structureOverview: sourceGroup.StructureOverview);
    }
}
