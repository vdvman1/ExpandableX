using System;
using System.Collections.Generic;
using System.Linq;

namespace ExpandableX.Core
{
    /// <summary>The four planar directions an expansion can run along. The game's <c>TileDirection</c> also has Up/Down, reserved for future vertical expansion.</summary>
    public static class PlanarDirections
    {
        public static readonly IReadOnlyList<TileDirection> All =
            new[] { TileDirection.East, TileDirection.South, TileDirection.West, TileDirection.North };
    }

    // ---- Expansion conditions ----------------------------------------------

    /// <summary>
    /// The game state an expansion condition is evaluated against. Mode is a string id to keep
    /// Core decoupled from the game's GameMode type; the framework populates it from the live mode.
    /// </summary>
    public sealed record ExpansionContext(string? CurrentModeId, IReadOnlyCollection<string> Researched);

    public interface IExpansionCondition
    {
        bool IsMet(ExpansionContext ctx);
        string Describe();
    }

    public static class ExpansionConditions
    {
        public static IExpansionCondition RequiresMode(string modeId) => new ModeImpl(modeId);
        public static IExpansionCondition RequiresResearch(string researchId) => new ResearchImpl(researchId);
        public static IExpansionCondition Custom(Func<ExpansionContext, bool> predicate, string description) => new CustomImpl(predicate, description);

        private sealed class ModeImpl : IExpansionCondition
        {
            private readonly string _modeId;
            public ModeImpl(string modeId) { _modeId = modeId; }
            public bool IsMet(ExpansionContext ctx) => string.Equals(ctx.CurrentModeId, _modeId, StringComparison.Ordinal);
            public string Describe() => $"requires {_modeId} mode";
        }

        private sealed class ResearchImpl : IExpansionCondition
        {
            private readonly string _id;
            public ResearchImpl(string id) { _id = id; }
            public bool IsMet(ExpansionContext ctx) => ctx.Researched.Contains(_id);
            public string Describe() => $"requires research '{_id}'";
        }

        private sealed class CustomImpl : IExpansionCondition
        {
            private readonly Func<ExpansionContext, bool> _predicate;
            private readonly string _description;
            public CustomImpl(Func<ExpansionContext, bool> predicate, string description) { _predicate = predicate; _description = description; }
            public bool IsMet(ExpansionContext ctx) => _predicate(ctx);
            public string Describe() => _description;
        }
    }

    // ---- Carry-over callbacks ----------------------------------------------
    //
    // State must transfer when a drag changes the shape. The engine validates the carried result
    // and refuses the drag if invalid, so the player can never reach an invalid state.

    public delegate ChainState CarryState(ChainState from, ChainState toAtDefaults);
    public delegate ChainState GrowCarry(ChainState before, ChainState afterDefault);
    public delegate ChainState ShrinkCarry(ChainState before, ChainState afterDefault, PieceState removed);

    public static class CarryStateDefaults
    {
        public static ChainState MatchById(ChainState from, ChainState toAtDefaults)
        {
            var newPieces = toAtDefaults.Pieces.ToList();
            int shared = Math.Min(newPieces.Count, from.Pieces.Count);
            for (int pi = 0; pi < shared; pi++)
                newPieces[pi] = CarryPieceById(from.Pieces[pi], newPieces[pi]);
            return toAtDefaults with { Pieces = newPieces };
        }

        public static PieceState CarryPieceById(PieceState from, PieceState to)
        {
            var roles = new Dictionary<string, SlotRole>(to.SlotRoles);
            foreach (var slot in to.ExpandedSlots)
                if (from.SlotRoles.TryGetValue(slot.Id, out var r) && slot.AllowedRoles.Contains(r))
                    roles[slot.Id] = r;
            return to with { SlotRoles = roles };
        }
    }

    // ---- Expansion (declared with layout objects) --------------------------

    public sealed record SequenceStep(Layout Layout, IReadOnlyList<IExpansionCondition> Conditions);

    /// <summary>
    /// A declared way a player can move a placed building between layouts. See CONTEXT.md "Expansion".
    /// Directions are stored per-kind (a single direction for a sequence, a set for a chain), not on
    /// the base, since their shape differs between kinds.
    /// </summary>
    public abstract record Expansion(IReadOnlyList<IExpansionCondition> Conditions)
    {
        /// <summary>Finite progression of static layouts (cutter). Locked steps are skipped when dragging.</summary>
        public sealed record Sequence(
            TileDirection Direction,
            IReadOnlyList<SequenceStep> Steps,
            IReadOnlyList<IExpansionCondition> Conditions,
            CarryState? Carry = null) : Expansion(Conditions);

        /// <summary>Unbounded chain along one of several allowed axes (AND gate).</summary>
        public sealed record Chain(
            IReadOnlyCollection<TileDirection> Directions,
            Layout.Dynamic Layout,
            IReadOnlyList<IExpansionCondition> Conditions,
            GrowCarry? Grow = null,
            ShrinkCarry? Shrink = null) : Expansion(Conditions);
    }

    public static class Expand
    {
        private static readonly IReadOnlyList<IExpansionCondition> None = Array.Empty<IExpansionCondition>();

        public static SequenceStep Step(Layout layout, params IExpansionCondition[] conditions) =>
            new(layout, conditions);

        public static Expansion Sequence(TileDirection direction, IReadOnlyList<SequenceStep> steps,
            IReadOnlyList<IExpansionCondition>? conditions = null, CarryState? carry = null) =>
            new Expansion.Sequence(direction, steps, conditions ?? None, carry);

        public static Expansion Chain(IReadOnlyCollection<TileDirection> directions, Layout.Dynamic layout,
            IReadOnlyList<IExpansionCondition>? conditions = null, GrowCarry? grow = null, ShrinkCarry? shrink = null) =>
            new Expansion.Chain(directions, layout, conditions ?? None, grow, shrink);
    }
}
