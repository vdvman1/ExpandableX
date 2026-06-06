using System.Collections.Generic;
using System.Linq;
using ShapezShifter.Hijack;
using ILogger = Core.Logging.ILogger;

namespace ExpandableX.Core
{
    /// <summary>
    /// Attaches the slot-toggle UI to every catalogued definition (the base building and all of its
    /// generated variants), wrapping the building's existing module provider so the player keeps the
    /// native panel plus per-slot dropdowns. Reads the decode catalog the simulation-systems rewirer
    /// populated, so it must run after it.
    /// </summary>
    internal class ExpandableXBuildingModulesRewirer : IBuildingModulesRewirer
    {
        private readonly ILogger _logger;
        private readonly ExpandableXRegistry _registry;

        public ExpandableXBuildingModulesRewirer(ILogger logger, ExpandableXRegistry registry)
        {
            _logger = logger;
            _registry = registry;
        }

        public void AddModules(BuildingsModulesLookup modulesLookup)
        {
            // One variant set per piece, keyed by its (unique) base definition id.
            var setsByBase = new Dictionary<string, PieceVariantSet>();
            foreach (VariantPlacement placement in _registry.VariantsByDefId.Values)
            {
                setsByBase[placement.Set.BaseDefinitionId] = placement.Set;
            }

            _logger.Info.Log($"ExpandableX-Core: modules rewirer firing — {setsByBase.Count} configurable base(s)");

            GameMode mode = _registry.CurrentMode;
            if (mode == null)
            {
                _logger.Info.Log("ExpandableX-Core: modules rewirer: CurrentMode not set, skipping");
                return;
            }

            foreach (PieceVariantSet set in setsByBase.Values)
            {
#pragma warning disable CS0618
                BuildingDefinitionId baseId = new BuildingDefinitionId(set.BaseDefinitionId);
#pragma warning restore CS0618

                // The base's existing provider becomes the inner of every wrapped definition. Read it
                // once, before wrapping, so variants don't inherit an already-wrapped provider.
                modulesLookup.BuildingModulesMap.TryGetValue(baseId, out IBuildingModules inner);

                var defNames = set.DefIdByComboKey.Values.Distinct().ToList();
                foreach (string defName in defNames)
                {
#pragma warning disable CS0618
                    BuildingDefinitionId defId = new BuildingDefinitionId(defName);
#pragma warning restore CS0618

                    var wrapped = new ConfigurableVariantModules(inner, _registry, _logger);

                    if (modulesLookup.BuildingModulesMap.ContainsKey(defId))
                    {
                        // Already registered (the base definition, or an override target): replace the
                        // provider only — its BuildingSimulationData entry is already present.
                        modulesLookup.BuildingModulesMap[defId] = wrapped;
                    }
                    else if (mode.Buildings._DefinitionsById.TryGetValue(defId, out IBuildingDefinition variantDef))
                    {
                        // Synthesised variant: register in BOTH maps so the definition-level
                        // GetInfoModules(defId) path (the native def panel content) resolves too.
                        modulesLookup.AddModule(defId, variantDef, wrapped);
                    }
                    else
                    {
                        _logger.Info.Log($"ExpandableX-Core: modules rewirer: variant '{defName}' not found in definitions, skipping");
                    }
                }

                _logger.Info.Log($"ExpandableX-Core: modules rewirer: slot UI on {defNames.Count} definition(s) for base '{set.BaseDefinitionId}'");
            }
        }
    }
}
