using System.Collections.Generic;
using Game.Core.Coordinates;

namespace ExpandableX.Core
{
    /// <summary>
    /// Builds a variant's <c>ConnectorData</c> from a configurable base and a slot-role combination
    /// (ADR-0009). Each slot's connector is transformed per its role via <see cref="ConnectorFactory"/>;
    /// connectors not owned by any slot (shape I/O, join connectors) are kept untouched.
    /// </summary>
    internal static class ConnectorSynthesizer
    {
        public static IBuildingConnectorData Synthesize(
            IBuildingConnectorData baseData,
            ConnectorDataResolver resolver,
            IReadOnlyList<ConnectorSlot> slots,
            IReadOnlyDictionary<string, SlotRole> roles)
        {
            // Map each slot's resolved base connector (by pivot) to the role to apply.
            var roleByPivot = new Dictionary<LocalTilePivot, SlotRole>(slots.Count);
            foreach (ConnectorSlot slot in slots)
            {
                IBuildingIO? connector = resolver.ResolveVisible(slot.Connector);
                if (connector is null)
                {
                    // The base definition lacks the referenced connector — a registration error.
                    // TODO(slice 4): surface this as a loud validation failure and skip the registration.
                    continue;
                }

                roleByPivot[connector.Pivot()] = roles.TryGetValue(slot.Id, out SlotRole r) ? r : slot.DefaultRole;
            }

            var connectors = new List<IBuildingIO>(baseData.AllBuildingConnectors.Length);
            foreach (IBuildingIO connector in baseData.AllBuildingConnectors)
            {
                if (roleByPivot.TryGetValue(connector.Pivot(), out SlotRole role))
                {
                    IBuildingIO? emitted = ConnectorFactory.Build(role, connector);
                    if (emitted is not null)
                    {
                        connectors.Add(emitted);
                    }
                }
                else
                {
                    connectors.Add(connector); // not a slot — kept as-is (shape I/O, joins)
                }
            }

            // Reuse the base geometry. Tiles lives only on the concrete BuildingConnectorData, the
            // sole implementation today. Check defensively rather than blind-cast — this is the
            // extension point if a new IBuildingConnectorData implementation ever appears.
            if (baseData is not BuildingConnectorData concrete)
            {
                throw new System.NotSupportedException(
                    $"Synthesis needs the tile geometry on BuildingConnectorData but got " +
                    $"'{baseData.GetType().Name}'. Extend ConnectorSynthesizer to support it.");
            }

            return new BuildingConnectorData(
                connectors,
                concrete.Tiles,
                concrete.TileBounds,
                concrete.TileBoundsCenter,
                concrete.TileDimensions);
        }
    }
}
