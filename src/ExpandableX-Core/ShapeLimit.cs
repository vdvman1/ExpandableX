using System;
using System.Collections.Generic;
using Game.Core.Coordinates;

namespace ExpandableX.Core
{
    /// <summary>
    /// Constrains which shapes a network-model <see cref="Layout.Dynamic"/> supports by gating each
    /// candidate grow (CONTEXT.md "Expansion" — the Network kind; ADR-0012). The simulation can hold
    /// any connected shape; the shape limit is what the layout chooses to allow. Framework limits are
    /// just predicates, and authors can supply their own — a <see cref="Custom"/> limit may close over
    /// live game state, so the supported shapes can vary by scenario.
    /// </summary>
    public interface IShapeLimit
    {
        /// <summary>
        /// Whether growing the building to also occupy <paramref name="candidate"/> is allowed, given
        /// its current footprint <paramref name="occupied"/> (local tile offsets, candidate excluded).
        /// </summary>
        bool AllowsGrow(IReadOnlyCollection<TileVector> occupied, TileVector candidate);
        string Describe();
    }

    public static class ShapeLimits
    {
        /// <summary>No constraint — the building may branch freely in any direction.</summary>
        public static IShapeLimit Free { get; } = new FreeImpl();

        /// <summary>An author predicate over the candidate grow (may read live game state).</summary>
        public static IShapeLimit Custom(Func<IReadOnlyCollection<TileVector>, TileVector, bool> predicate, string description) =>
            new CustomImpl(predicate, description);

        // TODO(#27): Line and Rectangle framework limits, added once directed grow geometry is wired
        // (they need to reason over local tile offsets — kept out of the API surface until then so the
        // coordinate handling lives in one place).

        private sealed class FreeImpl : IShapeLimit
        {
            public bool AllowsGrow(IReadOnlyCollection<TileVector> occupied, TileVector candidate) => true;
            public string Describe() => "any shape";
        }

        private sealed class CustomImpl : IShapeLimit
        {
            private readonly Func<IReadOnlyCollection<TileVector>, TileVector, bool> _predicate;
            private readonly string _description;
            public CustomImpl(Func<IReadOnlyCollection<TileVector>, TileVector, bool> predicate, string description) { _predicate = predicate; _description = description; }
            public bool AllowsGrow(IReadOnlyCollection<TileVector> occupied, TileVector candidate) => _predicate(occupied, candidate);
            public string Describe() => _description;
        }
    }
}
