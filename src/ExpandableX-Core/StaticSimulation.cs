using System;
using Core.Factory;
using Game.Core.Simulation;
using ShapezShifter.Hijack;

namespace ExpandableX.Core
{
    /// <summary>
    /// Builds <see cref="AtomicSimulationInstaller"/>s that attach the game's own atomic building
    /// simulation systems to each synthesised variant of a <see cref="Layout.Static"/> — mirroring how
    /// the base game registers one simulation per building definition. The author supplies only the part
    /// the game stores no metadata for (which simulation, and how to build its factory from the variant's
    /// definition plus the rewirer dependencies); the framework iterates the synthesised variants and
    /// wires each atomic system to its definition id.
    ///
    /// The four flavours mirror the game's four atomic simulation systems. Pass a <c>buildFactory</c> that
    /// produces the game's own factory — e.g. read the variant's configuration with
    /// <c>definition.ConfigAs&lt;...&gt;()</c> and construct the matching base-game simulation factory —
    /// so a static expandable reuses the stock simulation directly.
    /// </summary>
    public static class StaticSimulation
    {
        /// <summary>Per-building state, no configuration (painter, rotator, cutter, ...).</summary>
        public static AtomicSimulationInstaller Stateful<TSimulation, TState>(
            Func<IBuildingDefinition, SimulationSystemsDependencies, IFactory<TState, TSimulation>> buildFactory)
            where TSimulation : ISimulation
            where TState : class, ISimulationState, new()
            => (definition, simulationSystems, dependencies) =>
                simulationSystems.Add(new AtomicStatefulBuildingSimulationSystem<TSimulation, TState>(
                    buildFactory(definition, dependencies), definition.Id, dependencies.Logger));

        /// <summary>Per-building state and a building configuration (stacker, ...).</summary>
        public static AtomicSimulationInstaller StatefulConfigured<TSimulation, TState, TConfiguration>(
            Func<IBuildingDefinition, SimulationSystemsDependencies, IFactory<TConfiguration, TState, TSimulation>> buildFactory)
            where TSimulation : ISimulation
            where TState : class, ISimulationState, new()
            => (definition, simulationSystems, dependencies) =>
                simulationSystems.Add(new AtomicStatefulBuildingSimulationSystem<TSimulation, TState, TConfiguration>(
                    buildFactory(definition, dependencies), definition.Id, dependencies.Logger));

        /// <summary>Stateless, no configuration (label, ...).</summary>
        public static AtomicSimulationInstaller Stateless<TSimulation>(
            Func<IBuildingDefinition, SimulationSystemsDependencies, IFactory<TSimulation>> buildFactory)
            where TSimulation : ISimulation
            => (definition, simulationSystems, dependencies) =>
                simulationSystems.Add(new AtomicStatelessBuildingSimulationSystem<TSimulation>(
                    buildFactory(definition, dependencies), definition.Id, dependencies.Logger));

        /// <summary>Stateless with a building configuration (fluid producer, ...).</summary>
        public static AtomicSimulationInstaller StatelessConfigured<TSimulation, TConfiguration>(
            Func<IBuildingDefinition, SimulationSystemsDependencies, IFactory<TConfiguration, TSimulation>> buildFactory)
            where TSimulation : ISimulation
            where TConfiguration : IBuildingConfiguration
            => (definition, simulationSystems, dependencies) =>
                simulationSystems.Add(new AtomicStatelessBuildingSimulationSystem<TSimulation, TConfiguration>(
                    buildFactory(definition, dependencies), definition.Id, dependencies.Logger));
    }
}
