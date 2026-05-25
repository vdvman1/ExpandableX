using System.Collections.Generic;
using System.Linq;
using Core.Factory;
using Game.Orchestration;
using ShapezShifter.Hijack;
using ILogger = Core.Logging.ILogger;

namespace ExpandableX.Core
{
    internal class ExpandableXSimulationSystemsRewirer : ISimulationSystemsRewirer
    {
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

            foreach (KeyValuePair<string, Layout> kv in _registry.Registrations)
            {
                string sourceGroupIdName = kv.Key;
                Layout layout = kv.Value;

#pragma warning disable CS0618
                BuildingDefinitionGroupId sourceGroupId = new BuildingDefinitionGroupId(sourceGroupIdName);
#pragma warning restore CS0618

                IBuildingDefinitionGroup sourceGroupInterface = dependencies.Mode.Buildings.GetDefinitionGroup(sourceGroupId);
                if (sourceGroupInterface == null)
                {
                    _logger.Info.Log($"ExpandableX-Core: no group found for '{sourceGroupIdName}' — skipping");
                    continue;
                }

                BuildingDefinitionGroup sourceGroup = sourceGroupInterface as BuildingDefinitionGroup;
                if (sourceGroup == null)
                {
                    _logger.Info.Log($"ExpandableX-Core: group '{sourceGroupIdName}' is not a BuildingDefinitionGroup — skipping");
                    continue;
                }

                string configurableGroupIdName = sourceGroupIdName + "_ExpandableXConfigurable";

#pragma warning disable CS0618
                BuildingDefinitionGroupId configurableGroupId = new BuildingDefinitionGroupId(configurableGroupIdName);
#pragma warning restore CS0618

                if (dependencies.Mode.Buildings._VariantsById.ContainsKey(configurableGroupId))
                {
                    _logger.Info.Log($"ExpandableX-Core: configurable group '{configurableGroupIdName}' already exists in this session — skipping");
                    continue;
                }

                _logger.Info.Log($"ExpandableX-Core: source group '{sourceGroupIdName}' has {sourceGroup.Definitions.Count} definition(s) — creating hidden configurable group");

                BuildingDefinitionGroup configurableGroup = new BuildingDefinitionGroup(
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

                foreach (IBuildingDefinition existing in sourceGroup.Definitions.ToArray())
                {
                    string newDefName = existing.Id.Name + "_ExpandableXConfigurable";

#pragma warning disable CS0618
                    BuildingDefinitionId newDefId = new BuildingDefinitionId(newDefName);
                    BuildingDefinition configurableVariant = new BuildingDefinition(newDefId, existing.ConnectorData);
#pragma warning restore CS0618

                    foreach (object item in existing.CustomData.All)
                    {
                        configurableVariant.CustomData.Attach(item);
                    }

                    IFactory<IBuildingConfiguration> factory =
                        new ParameterlessConstructionFactory<ExpandableXBuildingConfiguration>() as IFactory<IBuildingConfiguration>;
                    configurableVariant.CustomData.Attach(factory);

                    configurableGroup.AddInternalVariant(configurableVariant);
                    dependencies.Mode.Buildings._DefinitionsById.Add(configurableVariant.Id, configurableVariant);

                    _logger.Info.Log($"ExpandableX-Core:   + variant '{newDefName}' supports config: {configurableVariant.CanCreateConfiguration()}");

                    IReadOnlyList<BuildingItemInput> beltIn = configurableVariant.ConnectorData.BuildingConnectorsOfType<BuildingItemInput>();
                    IReadOnlyList<BuildingItemOutput> beltOut = configurableVariant.ConnectorData.BuildingConnectorsOfType<BuildingItemOutput>();
                    IReadOnlyList<BuildingFluidInput> fluidIn = configurableVariant.ConnectorData.BuildingConnectorsOfType<BuildingFluidInput>();
                    IReadOnlyList<BuildingFluidOutput> fluidOut = configurableVariant.ConnectorData.BuildingConnectorsOfType<BuildingFluidOutput>();
                    IReadOnlyList<BuildingFluidJunction> fluidJunc = configurableVariant.ConnectorData.BuildingConnectorsOfType<BuildingFluidJunction>();
                    IReadOnlyList<BuildingSignalInput> sigIn = configurableVariant.ConnectorData.BuildingConnectorsOfType<BuildingSignalInput>();
                    IReadOnlyList<BuildingSignalOutput> sigOut = configurableVariant.ConnectorData.BuildingConnectorsOfType<BuildingSignalOutput>();
                    IReadOnlyList<BuildingSignalJunction> sigJunc = configurableVariant.ConnectorData.BuildingConnectorsOfType<BuildingSignalJunction>();
                    _logger.Info.Log($"ExpandableX-Core:     connector counts: belt(in={beltIn.Count}, out={beltOut.Count}); fluid(in={fluidIn.Count}, out={fluidOut.Count}, junction={fluidJunc.Count}); signal(in={sigIn.Count}, out={sigOut.Count}, junction={sigJunc.Count})");

                    for (int i = 0; i < beltIn.Count; i++) { _logger.Info.Log($"ExpandableX-Core:       BuildingItemInput[{i}] pos={beltIn[i].Position_L} dir={beltIn[i].TileDirection}"); }
                    for (int i = 0; i < beltOut.Count; i++) { _logger.Info.Log($"ExpandableX-Core:       BuildingItemOutput[{i}] pos={beltOut[i].Position_L} dir={beltOut[i].TileDirection}"); }
                    for (int i = 0; i < fluidIn.Count; i++) { _logger.Info.Log($"ExpandableX-Core:       BuildingFluidInput[{i}] pos={fluidIn[i].Position_L} dir={fluidIn[i].TileDirection}"); }
                    for (int i = 0; i < fluidOut.Count; i++) { _logger.Info.Log($"ExpandableX-Core:       BuildingFluidOutput[{i}] pos={fluidOut[i].Position_L} dir={fluidOut[i].TileDirection}"); }
                    for (int i = 0; i < fluidJunc.Count; i++) { _logger.Info.Log($"ExpandableX-Core:       BuildingFluidJunction[{i}] pos={fluidJunc[i].Position_L} dir={fluidJunc[i].TileDirection} ioType={fluidJunc[i].IOType}"); }
                    for (int i = 0; i < sigIn.Count; i++) { _logger.Info.Log($"ExpandableX-Core:       BuildingSignalInput[{i}] pos={sigIn[i].Position_L} dir={sigIn[i].TileDirection}"); }
                    for (int i = 0; i < sigOut.Count; i++) { _logger.Info.Log($"ExpandableX-Core:       BuildingSignalOutput[{i}] pos={sigOut[i].Position_L} dir={sigOut[i].TileDirection}"); }
                    for (int i = 0; i < sigJunc.Count; i++) { _logger.Info.Log($"ExpandableX-Core:       BuildingSignalJunction[{i}] pos={sigJunc[i].Position_L} dir={sigJunc[i].TileDirection}"); }

                    if (configurableVariant.CustomData.TryGet<IPainterConfiguration>(out IPainterConfiguration painterConfig))
                    {
                        ShapeOperationPaintTopmost paintOp = new ShapeOperationPaintTopmost(dependencies.ShapeRegistry, dependencies.ShapeIdManager);
                        TopmostPainterSimulationFactory simFactory = new TopmostPainterSimulationFactory(painterConfig, paintOp, dependencies.ShapeRegistry);
                        AtomicStatefulBuildingSimulationSystem<TopmostPainterSimulation, PainterSimulationState> simSystem =
                            new AtomicStatefulBuildingSimulationSystem<TopmostPainterSimulation, PainterSimulationState>(
                                simFactory, configurableVariant.Id, dependencies.Logger);
                        simulationSystems.Add(simSystem);
                        _logger.Info.Log($"ExpandableX-Core:     + painter simulation system registered for '{newDefName}'");
                    }
                }

                dependencies.Mode.Buildings._VariantsById.Add(configurableGroupId, configurableGroup);
                dependencies.Mode.Buildings._All.Add(configurableGroup);

                _logger.Info.Log($"ExpandableX-Core: added hidden group '{configurableGroupIdName}' with {configurableGroup.Definitions.Count} variant(s); source group '{sourceGroupIdName}' still has {sourceGroup.Definitions.Count} definition(s)");
            }
        }
    }
}
