using System.Collections.Generic;
using Core.Localization;
using ILogger = Core.Logging.ILogger;

namespace ExpandableX.Core
{
    internal class ConfigurableVariantModules : IBuildingModules
    {
        private readonly StaticLayout _layout;
        private readonly ILogger _logger;

        public ConfigurableVariantModules(StaticLayout layout, ILogger logger)
        {
            _layout = layout;
            _logger = logger;
        }

        public IEnumerable<IHUDSidePanelModuleData> GetInfoModules(IMapModel map, BuildingModel building)
        {
            ExpandableXBuildingConfiguration config = building.Configuration as ExpandableXBuildingConfiguration;
            if (config == null)
            {
                yield break;
            }

            foreach (ConnectorSlot slot in _layout.Slots)
            {
                yield return new HUDSidePanelModuleInfoText.Data(new RawText(slot.Id));

                SlotRole currentRole;
                if (!config.TryGetRole(slot.Id, out currentRole))
                {
                    currentRole = slot.DefaultRole;
                }

                int currentIndex = -1;
                List<IText> roleLabels = new List<IText>(slot.AllowedRoles.Count);
                for (int i = 0; i < slot.AllowedRoles.Count; i++)
                {
                    roleLabels.Add(new RawText(slot.AllowedRoles[i].ToString()));
                    if (slot.AllowedRoles[i] == currentRole)
                    {
                        currentIndex = i;
                    }
                }

                ConnectorSlot slotCaptured = slot;
                ExpandableXBuildingConfiguration configCaptured = config;

                yield return new HUDSidePanelModuleDropdownSelector.Data(
                    roleLabels,
                    currentIndex,
                    index =>
                    {
                        SlotRole newRole = slotCaptured.AllowedRoles[index];
                        configCaptured.SetRole(slotCaptured.Id, newRole);
                        _logger.Info.Log($"ExpandableX-Core: slot '{slotCaptured.Id}' set to {newRole}");
                    });
            }
        }

        public IEnumerable<IHUDSidePanelModuleData> GetInfoModules(IBuildingDefinition definition)
        {
            yield break;
        }
    }
}
