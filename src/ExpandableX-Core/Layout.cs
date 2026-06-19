using System;
using System.Collections.Generic;
using System.Linq;

namespace ExpandableX.Core
{
    /// <summary>
    /// A specific valid state a player can put an expandable building into. Two kinds —
    /// <see cref="Static"/> (swap) and <see cref="Dynamic"/> (multi-piece composition).
    /// See CONTEXT.md "Layout".
    ///
    /// The two kinds carry their own, fit-for-purpose simulation hook, because they are simulated
    /// differently: a <see cref="Static"/> building is a regular single building, so it uses an atomic
    /// per-definition simulation (<see cref="Static.Simulation"/>) exactly as the base game does; a
    /// <see cref="Dynamic"/> building is a connected network of pieces, so it uses a network node
    /// (<see cref="Dynamic.SimulationFactory"/>). A pure-swap family like the cutter needs neither.
    /// </summary>
    public abstract record Layout(string LayoutId)
    {
        /// <summary>
        /// A layout backed by a single piece (painter, each cutter size). <see cref="Simulation"/>, when
        /// set, attaches a per-definition atomic simulation to each variant the framework synthesises —
        /// the way regular base-game buildings are simulated. Build it with the <see cref="StaticSimulation"/>
        /// helpers (which wrap the game's atomic simulation systems). Optional: a swap-only family whose
        /// variants reuse already-simulated base-game definitions (the cutter) needs none.
        /// </summary>
        public sealed record Static(
            string LayoutId,
            PieceSpec Piece,
            AtomicSimulationInstaller? Simulation = null) : Layout(LayoutId);

        /// <summary>
        /// A network-model multi-piece layout (the AND gate). The author declares <b>one</b> gameplay
        /// <see cref="Piece"/> (its connector slots in their I/O/Disabled roles); the framework derives
        /// the two generated kinds from it (see <see cref="LayoutExtensions.EnumeratePieceSpecs"/>),
        /// because the join rules are intrinsic to the piece kind, not the author's concern: a standalone
        /// <b>singleton</b> (slots exactly as declared, never a join) and a <b>network piece</b> (each
        /// face-slot may also be a <c>Join</c>, and at least one must be). <see cref="ShapeLimit"/>
        /// constrains which shapes are reachable; <see cref="NetworkPredicates"/> are the building-wide
        /// validity rules. <see cref="SimulationFactory"/> builds the one network node per connected
        /// component (the singleton is the one-member case). See CONTEXT.md "DynamicLayout" and ADR-0012.
        /// </summary>
        public sealed record Dynamic(
            string LayoutId,
            PieceSpec Piece,
            IShapeLimit ShapeLimit,
            IReadOnlyList<INetworkPredicate> NetworkPredicates,
            ExpandableSimulationFactory? SimulationFactory = null) : Layout(LayoutId);
    }

    public static class LayoutExtensions
    {
        public static IEnumerable<PieceSpec> EnumeratePieceSpecs(this Layout layout) => layout switch
        {
            Layout.Static s => new[] { s.Piece },
            Layout.Dynamic d => new[] { WithJoinFaces(d.Piece) },
            _ => Enumerable.Empty<PieceSpec>(),
        };

        // A network-model layout's author declares its gameplay slots (I/O/Disabled) per face; the
        // framework adds Join as an allowed role on every face-slot, because any face can join a
        // neighbour — that's intrinsic to the kind, not author config (ADR-0012). The kinds are then
        // emergent from a generated variant's join count: a 0-join variant is the standalone
        // configurable singleton, a variant with >=1 join is a network piece. No separate families or
        // >=1-join constraint are needed — the same combinations result either way (a 0-join combo is a
        // valid singleton, not an impossible network piece), and overrides keyed by combo naturally
        // target the right one (a singleton combo carries no J; a network combo does).
        // v1 treats every declared slot as a join-capable face (the planar-four-face model).
        private static PieceSpec WithJoinFaces(PieceSpec declared) =>
            declared with { SlotSpecs = declared.SlotSpecs.Select(WithJoinAllowed).ToList() };

        private static ConnectorSlotSpec WithJoinAllowed(ConnectorSlotSpec spec) => spec switch
        {
            ConnectorSlotSpec.Single s => s with { AllowedRoles = WithJoin(s.AllowedRoles) },
            ConnectorSlotSpec.Range r => r with { AllowedRoles = WithJoin(r.AllowedRoles) },
            _ => spec,
        };

        private static IReadOnlyList<SlotRole> WithJoin(IReadOnlyList<SlotRole> roles) =>
            roles.Contains(SlotRole.Join) ? roles : new List<SlotRole>(roles) { SlotRole.Join };

        public static IReadOnlyList<INetworkPredicate> NetworkPredicatesOf(this Layout layout) => layout switch
        {
            Layout.Dynamic d => d.NetworkPredicates,
            _ => Array.Empty<INetworkPredicate>(),
        };
    }
}
