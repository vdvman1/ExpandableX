using System;
using System.Collections.Generic;
using Game.Content.Features.Signals.Connections;
using Game.Content.Features.Signals.Simulation;
using Game.Core.Coordinates;
using Game.Core.Map.Simulation;

namespace ExpandableX.Core
{
    /// <summary>
    /// An <see cref="ExpandableSimulation"/> for a signal building: it presents the union of its
    /// members' gameplay signal connectors — each member's <c>BuildingSignalInput</c>s feeding the inner
    /// simulation's receivers and its <c>BuildingSignalOutput</c>s driven by its providers — at their
    /// world pivots. Because a signal connector and a join connector never share a face, the union is
    /// just the concatenation of every member's signal connectors (no interior cancellation). A
    /// one-member set is the standalone building; nothing special-cases it.
    ///
    /// Reusable by any signal-based expandable: supply <paramref name="createSimulation"/>, which builds
    /// the inner <typeparamref name="TSimulation"/> from the discovered input/output counts (the gate
    /// logic), and this wires the rest.
    /// </summary>
    public sealed class SignalExpandableSimulation<TSimulation> : ExpandableSimulation
        where TSimulation : class, ISignalSimulation
    {
        /// <summary>The inner gameplay signal simulation this node drives.</summary>
        public TSimulation SignalSimulation { get; }

        public SignalExpandableSimulation(
            IReadOnlyCollection<BuildingInstance> members,
            IReadOnlyList<GlobalTileCoordinate> occupiedTiles,
            Func<int, int, TSimulation> createSimulation)
            : this(Wire(members, createSimulation), members, occupiedTiles)
        {
        }

        // The connectors and the inner sim must be built together (the sim's receiver/provider count is
        // the discovered connector count), so a static helper prepares both and feeds the base ctor.
        private SignalExpandableSimulation(
            (TSimulation Simulation, List<ISimulationConnector> Connectors) wired,
            IReadOnlyCollection<BuildingInstance> members,
            IReadOnlyList<GlobalTileCoordinate> occupiedTiles)
            : base(wired.Simulation, members, occupiedTiles, wired.Connectors) =>
            SignalSimulation = wired.Simulation;

        private static (TSimulation, List<ISimulationConnector>) Wire(
            IReadOnlyCollection<BuildingInstance> members,
            Func<int, int, TSimulation> createSimulation)
        {
            // Gather every member's gameplay signal connectors (their world pivots), inputs then outputs.
            var inputPivots = new List<GlobalTilePivot>();
            var outputPivots = new List<GlobalTilePivot>();
            foreach (BuildingInstance building in members)
            {
                if (!building.Definition.CustomData.TryGet<IBuildingConnectorData>(out IBuildingConnectorData connectorData))
                {
                    continue;
                }

                foreach (BuildingSignalInput input in connectorData.BuildingConnectorsOfType<BuildingSignalInput>())
                {
                    inputPivots.Add(input.Pivot(building.Transform));
                }
                foreach (BuildingSignalOutput output in connectorData.BuildingConnectorsOfType<BuildingSignalOutput>())
                {
                    outputPivots.Add(output.Pivot(building.Transform));
                }
            }

            TSimulation simulation = createSimulation(inputPivots.Count, outputPivots.Count);

            var connectors = new List<ISimulationConnector>(inputPivots.Count + outputPivots.Count);
            for (int i = 0; i < inputPivots.Count; i++)
            {
                connectors.Add(new SignalReceiverConnector(simulation.GetSignalReceiver(i), inputPivots[i]));
            }
            for (int j = 0; j < outputPivots.Count; j++)
            {
                connectors.Add(new SignalProviderConnector(simulation.GetSignalProvider(j), outputPivots[j]));
            }

            return (simulation, connectors);
        }
    }
}
