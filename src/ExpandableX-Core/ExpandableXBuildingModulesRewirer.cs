using System.Collections.Generic;
using ShapezShifter.Hijack;
using ILogger = Core.Logging.ILogger;

namespace ExpandableX.Core
{
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
            GameMode mode = _registry.CurrentMode;
            if (mode == null)
            {
                _logger.Info.Log("ExpandableX-Core: modules rewirer firing but CurrentMode not captured — skipping");
                return;
            }

            _logger.Info.Log($"ExpandableX-Core: modules rewirer firing — processing {_registry.Registrations.Count} registration(s)");

            foreach (KeyValuePair<string, Layout> kv in _registry.Registrations)
            {
                string sourceGroupIdName = kv.Key;
                Layout layout = kv.Value;

#pragma warning disable CS0618
                BuildingDefinitionGroupId sourceGroupId = new BuildingDefinitionGroupId(sourceGroupIdName);
#pragma warning restore CS0618

                IBuildingDefinitionGroup sourceGroup = mode.Buildings.GetDefinitionGroup(sourceGroupId);
                if (sourceGroup == null)
                {
                    continue;
                }

                StaticLayout staticLayout = layout as StaticLayout;

                foreach (IBuildingDefinition existing in sourceGroup.Definitions)
                {
                    BuildingDefinitionId defId = existing.Id;
                    if (modulesLookup.BuildingModulesMap.TryGetValue(defId, out IBuildingModules inner))
                    {
                        modulesLookup.BuildingModulesMap[defId] = new ExpandableXModuleWrapper(inner, _registry, _logger);
                        _logger.Info.Log($"ExpandableX-Core: modules rewirer: wrapped module provider for '{defId.Name}'");
                    }
                    else
                    {
                        _logger.Info.Log($"ExpandableX-Core: modules rewirer: no existing module provider for '{defId.Name}' — skipping wrap");
                    }

                    if (staticLayout != null)
                    {
#pragma warning disable CS0618
                        BuildingDefinitionId configurableId = new BuildingDefinitionId(defId.Name + "_ExpandableXConfigurable");
#pragma warning restore CS0618

                        if (!modulesLookup.BuildingModulesMap.ContainsKey(configurableId))
                        {
                            modulesLookup.BuildingModulesMap[configurableId] = new ConfigurableVariantModules(staticLayout, _logger);
                            _logger.Info.Log($"ExpandableX-Core: modules rewirer: registered slot modules for '{configurableId.Name}' ({staticLayout.Slots.Count} slot(s))");
                        }
                    }
                }
            }
        }
    }
}
