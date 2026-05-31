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

        /// <summary>A rule-based multi-piece layout (the AND gate). Carries the chain predicates that govern the whole family.</summary>
        public sealed record Dynamic(
            string LayoutId,
            PieceSpec? ConfigurableSingleton,
            PieceSpec Head,
            PieceSpec Body,
            PieceSpec Tail,
            IReadOnlyList<IChainPredicate> ChainPredicates) : Layout(LayoutId);
    }

    public static class LayoutExtensions
    {
        public static IEnumerable<PieceSpec> EnumeratePieceSpecs(this Layout layout) => layout switch
        {
            Layout.Static s => new[] { s.Piece },
            Layout.Dynamic d => d.ConfigurableSingleton is null
                ? new[] { d.Head, d.Body, d.Tail }
                : new[] { d.ConfigurableSingleton, d.Head, d.Body, d.Tail },
            _ => Enumerable.Empty<PieceSpec>(),
        };

        public static IReadOnlyList<IChainPredicate> ChainPredicatesOf(this Layout layout) => layout switch
        {
            Layout.Dynamic d => d.ChainPredicates,
            _ => Array.Empty<IChainPredicate>(),
        };
    }
}
