using System.Collections.Generic;
using ShapezShifter.Hijack;

namespace ExpandableX.Core
{
    /// <summary>
    /// Installs a per-building (atomic) simulation for one synthesised <see cref="Layout.Static"/>
    /// variant definition — the way regular base-game buildings are simulated (one simulation system per
    /// definition id). The framework invokes this for each variant it synthesises (never the base or an
    /// override target — those reuse the game's own simulation), passing the variant's definition (so the
    /// installer can read its configuration off <c>CustomData</c>), the live simulation-systems
    /// collection to add to, and the rewirer dependencies.
    ///
    /// Build one with the <see cref="StaticSimulation"/> helpers, which wrap the game's atomic simulation
    /// systems; or write one directly to add any <see cref="ISimulationSystem"/>. The framework
    /// deliberately does not hide ShapezShifter or the base game here — the installer works directly with
    /// them, so reusing a base-game simulation is as direct as the game's own registration.
    /// </summary>
    public delegate void AtomicSimulationInstaller(
        IBuildingDefinition variantDefinition,
        ICollection<ISimulationSystem> simulationSystems,
        SimulationSystemsDependencies dependencies);
}
