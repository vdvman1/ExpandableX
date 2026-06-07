using System;
using System.Collections.Generic;
using System.Linq;

namespace ExpandableX.Core
{
    /// <summary>
    /// A specific valid state a player can put an expandable building into. Two kinds —
    /// <see cref="Static"/> (swap) and <see cref="Dynamic"/> (multi-piece composition).
    /// See CONTEXT.md "Layout".
    /// </summary>
    public abstract record Layout(string LayoutId)
    {
        /// <summary>A layout backed by a single piece (painter, each cutter size).</summary>
        public sealed record Static(string LayoutId, PieceSpec Piece) : Layout(LayoutId);

        /// <summary>
        /// A network-model multi-piece layout (the AND gate). A single configurable-base
        /// <see cref="PieceSpec"/> generates the whole family (one variant per join-face set ×
        /// slot-role combination); the building grows/shrinks as a connected network.
        /// <see cref="ShapeLimit"/> constrains which shapes are reachable; <see cref="NetworkPredicates"/>
        /// are the building-wide validity rules. See CONTEXT.md "DynamicLayout" and ADR-0012.
        /// </summary>
        public sealed record Dynamic(
            string LayoutId,
            PieceSpec Piece,
            IShapeLimit ShapeLimit,
            IReadOnlyList<INetworkPredicate> NetworkPredicates) : Layout(LayoutId);
    }

    public static class LayoutExtensions
    {
        public static IEnumerable<PieceSpec> EnumeratePieceSpecs(this Layout layout) => layout switch
        {
            Layout.Static s => new[] { s.Piece },
            Layout.Dynamic d => new[] { d.Piece },
            _ => Enumerable.Empty<PieceSpec>(),
        };

        public static IReadOnlyList<INetworkPredicate> NetworkPredicatesOf(this Layout layout) => layout switch
        {
            Layout.Dynamic d => d.NetworkPredicates,
            _ => Array.Empty<INetworkPredicate>(),
        };
    }
}
