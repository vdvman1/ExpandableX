using System;
using System.Collections.Generic;

namespace ExpandableX.Core
{
    /// <summary>
    /// How a <see cref="ConnectorSlot"/> names the physical connector it controls — by connector
    /// type + visible index (the game's own access idiom). See CONTEXT.md "Connector reference".
    /// The index is into <c>BuildingConnectorsOfType&lt;T&gt;()</c>; when <see cref="AutoSkipInternal"/>
    /// is set, the resolver skips internal connectors (e.g. <c>BuildingFluidIOType.None</c>) so the
    /// index matches the connectors the player can see.
    /// </summary>
    public sealed record ConnectorReference(
        Type ConnectorType,
        int VisibleIndex,
        bool AutoSkipInternal,
        Func<IBuildingConnectorData, IReadOnlyList<IBuildingIO>> Accessor)
    {
        /// <summary>Bind to the <paramref name="visibleIndex"/>th connector of type <typeparamref name="T"/>.</summary>
        public static ConnectorReference Of<T>(int visibleIndex, bool autoSkipInternal = true)
            where T : class, IBuildingIO
            => new(typeof(T), visibleIndex, autoSkipInternal,
                   data => data.BuildingConnectorsOfType<T>());

        public override string ToString() =>
            $"{ConnectorType.Name}[{VisibleIndex}]{(AutoSkipInternal ? "" : " (no-skip)")}";
    }
}
