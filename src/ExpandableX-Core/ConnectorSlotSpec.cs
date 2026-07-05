using System;
using System.Collections.Generic;

namespace ExpandableX.Core
{
    /// <summary>
    /// Resolves how many *visible* connectors of a given reference exist on a configurable base,
    /// applying <see cref="ConnectorReference.AutoSkipInternal"/>. The real implementation reads
    /// <c>ConnectorData</c> off the live <c>MetaBuildingDefinition</c>.
    /// </summary>
    public interface IConnectorCountResolver
    {
        int CountVisible(ConnectorReference reference);
    }

    /// <summary>
    /// Registration-time declaration of connector slots. Expands against an
    /// <see cref="IConnectorCountResolver"/> into concrete <see cref="ConnectorSlot"/>s.
    /// </summary>
    public abstract record ConnectorSlotSpec
    {
        public abstract IReadOnlyList<ConnectorSlot> Expand(IConnectorCountResolver resolver);

        /// <summary>One explicitly-referenced slot.</summary>
        public sealed record Single(
            string Id,
            ConnectorReference Connector,
            IReadOnlyList<SlotRole> AllowedRoles,
            SlotRole DefaultRole) : ConnectorSlotSpec
        {
            public override IReadOnlyList<ConnectorSlot> Expand(IConnectorCountResolver _) =>
                new[] { new ConnectorSlot(Id, AllowedRoles, DefaultRole, Connector) };
        }

        /// <summary>
        /// One slot per visible connector of a type, over a (sub)range of them — the common "same
        /// options for all of this connector type" case (the default <c>..</c> selection), with
        /// partial ranges (<c>1..3</c>, <c>..^1</c>) also supported. Use <see cref="Of{T}"/>.
        /// Slot ids and connector references use the absolute visible index, so they are stable
        /// regardless of the selected sub-range.
        /// </summary>
        public sealed record Range(
            string IdPrefix,
            IReadOnlyList<SlotRole> AllowedRoles,
            SlotRole DefaultRole,
            System.Range Selection,
            ConnectorReference Probe,
            Func<int, ConnectorReference> ReferenceAt,
            Func<int, string>? LabelAt = null) : ConnectorSlotSpec
        {
            public override IReadOnlyList<ConnectorSlot> Expand(IConnectorCountResolver resolver)
            {
                int count = resolver.CountVisible(Probe);
                var (offset, length) = Selection.GetOffsetAndLength(count);
                var slots = new List<ConnectorSlot>(length);
                for (int i = 0; i < length; i++)
                {
                    int idx = offset + i;
                    slots.Add(new ConnectorSlot(
                        $"{IdPrefix}_{idx}", AllowedRoles, DefaultRole, ReferenceAt(idx), LabelAt?.Invoke(idx)));
                }
                return slots;
            }

            /// <param name="selection">Which visible connectors to cover, e.g. <c>1..3</c>. Null = all (<c>..</c>).</param>
            /// <param name="labelAt">
            /// Optional player-facing label per visible index (see <see cref="ConnectorSlot.Label"/>). Only
            /// meaningful for static layouts; dynamic layouts label by compass direction. Null → the slot's
            /// id is used as its label.
            /// </param>
            public static Range Of<T>(
                string idPrefix,
                IReadOnlyList<SlotRole> allowedRoles,
                SlotRole defaultRole,
                System.Range? selection = null,
                bool autoSkipInternal = true,
                Func<int, string>? labelAt = null) where T : class, IBuildingIO
                => new(idPrefix, allowedRoles, defaultRole,
                       selection ?? System.Range.All,
                       ConnectorReference.Of<T>(0, autoSkipInternal),
                       i => ConnectorReference.Of<T>(i, autoSkipInternal),
                       labelAt);
        }
    }
}
