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
                _logger.Info.Log($"ExpandableX-Core: base definition '{piece.BaseDefinitionId}' not found for {piece.Role} piece in family '{registration.RegistrationId}' — skipping piece");
                LogAvailableDefinitionsOnce(dependencies);
                return 0;
            }

            var resolver = new ConnectorDataResolver(baseDef.ConnectorData);
            PieceExpansion expansion = VariantEncoder.ExplodePiece(piece, resolver);
            _logger.Info.Log($"ExpandableX-Core:   {piece.Role} '{baseDef.Id.Name}': {expansion.Variants.Count} variant(s), {expansion.Pruned.Count} pruned");

            // First pass: resolve each combination to a definition id (synthesising new defs as
            // needed) and build the per-piece combo → def-id table.
            var defIdByComboKey = new Dictionary<string, string>(expansion.Variants.Count);
            var placements = new List<(string DefIdName, IReadOnlyDictionary<string, SlotRole> State)>(expansion.Variants.Count);
            int synthesised = 0;

            foreach (Variant variant in expansion.Variants)
            {
                string comboKey = VariantEncoder.ComboKey(expansion.ExpandedSlots, variant.SlotState);
                string defIdName = ResolveOrSynthesise(
                    variant, baseDef, piece, expansion, resolver, hiddenGroups, simulationSystems, dependencies, ref synthesised);

                defIdByComboKey[comboKey] = defIdName;
                placements.Add((defIdName, variant.SlotState));
            }

            // Second pass: register each placement against the shared, complete table.
            var set = new PieceVariantSet(registration, layout, piece, baseDef.Id.Name, expansion.ExpandedSlots, defIdByComboKey);
            foreach ((string defIdName, IReadOnlyDictionary<string, SlotRole> state) in placements)
            {
                _registry.RecordVariant(defIdName, new VariantPlacement(set, state));
            }

            return synthesised;
        }

        /// <summary>Map a combination to its definition id, creating a synthesised definition when it isn't the base or an override.</summary>
        private string ResolveOrSynthesise(
            Variant variant,
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
            AttachPainterSimulation(variantDef, simulationSystems, dependencies);
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

        private void AttachPainterSimulation(
            BuildingDefinition variantDef,
            ICollection<ISimulationSystem> simulationSystems,
            SimulationSystemsDependencies dependencies)
        {
            if (!variantDef.CustomData.TryGet<IPainterConfiguration>(out IPainterConfiguration painterConfig))
            {
                return;
            }

            var paintOp = new ShapeOperationPaintTopmost(dependencies.ShapeRegistry, dependencies.ShapeIdManager);
            var simFactory = new TopmostPainterSimulationFactory(painterConfig, paintOp, dependencies.ShapeRegistry);
            simulationSystems.Add(new AtomicStatefulBuildingSimulationSystem<TopmostPainterSimulation, PainterSimulationState>(
                simFactory, variantDef.Id, dependencies.Logger));
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
