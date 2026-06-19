using System.Collections.Generic;

namespace ExpandableX.Core
{
    /// <summary>
    /// The umbrella a mod supplies for one expandable building (see CONTEXT.md "Registration").
    /// Owns the building's layouts and the directional expansions that move between them.
    /// ExpandableX-Core governs how an already-placed building expands; it does not decide initial
    /// placement.
    /// </summary>
    public sealed record Registration(
        string RegistrationId,
        IReadOnlyList<Layout> Layouts,
        IReadOnlyList<Expansion> Expansions);
}
