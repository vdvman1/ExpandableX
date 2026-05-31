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
                    modulesLookup.BuildingModulesMap[defId] = new ConfigurableVariantModules(inner, _registry, _logger);
                }

                _logger.Info.Log($"ExpandableX-Core: modules rewirer: slot UI on {defNames.Count} definition(s) for base '{set.BaseDefinitionId}'");
            }
        }
    }
}
