using System;
using System.Collections.Generic;
using System.Linq;
using Game.Core.Coordinates;

namespace ExpandableX.Core
{
    /// <summary>
    /// The drag handles for a whole selected logical building — the unified control surface that replaces
    /// the per-face HUD buttons (issue #5 / ADR-0014). Handles are not focus-scoped: they cover the entire
    /// building (Q3). This dispatches by layout kind to produce one uniform <see cref="ExpansionHandle"/>
    /// list; the gesture is the same across kinds, only the per-drag granularity differs (the input layer
    /// routes a dynamic drag through <see cref="NetworkExpansionEngine.GrowChainFor"/> /
    /// <see cref="NetworkExpansionEngine.ShrinkChainFor"/> and a static-sequence drag through
    /// <see cref="SequenceEngine"/> steps):
    ///
    /// <list type="bullet">
    /// <item><b>Dynamic network</b> — the union of <see cref="NetworkExpansionEngine.HandlesFor"/> over every
    /// network member (so the whole building shows handles, not just the clicked piece). A lone singleton
    /// (not yet a network member) contributes only its own grow handles.</item>
    /// <item><b>Static sequence</b> — one handle per sequence direction: the outward direction grows
    /// (advance a step) and the inward direction shrinks (retreat), each live iff the sequence has an
    /// available step that way.</item>
    /// </list>
    ///
    /// Empty for a building that isn't registered as expandable, or has no live handle.
    /// </summary>
    internal static class ExpansionHandles
    {
        public static IReadOnlyList<ExpansionHandle> For(
            IMapModel map, Player executor, ExpandableXRegistry registry, BuildingModel selected)
        {
            if (!registry.VariantsByDefId.TryGetValue(selected.Definition.Id.Name, out VariantPlacement? placement))
            {
                return [];
            }

            PieceVariantSet set = placement.Set;
            return set.Layout switch
            {
                Layout.Dynamic => NetworkHandles(map, executor, registry, selected),
                Layout.Static => SequenceHandles(set, selected),
                _ => [],
            };
        }

        /// <summary>Union of every network member's per-piece handles; a lone singleton contributes only its own.</summary>
        private static IReadOnlyList<ExpansionHandle> NetworkHandles(
            IMapModel map, Player executor, ExpandableXRegistry registry, BuildingModel selected)
        {
            if (registry.NetworkSimulation is not { } simulation
                || !simulation.TryGetNetworkMembers(selected.Transform.Position, out IReadOnlyCollection<BuildingInstance>? members))
            {
                // A dynamic singleton isn't tracked as a network member (it carries no joins), so it has no
                // membership to read — its handles are just its own growable faces.
                return NetworkExpansionEngine.HandlesFor(map, executor, registry, selected);
            }

            var handles = new List<ExpansionHandle>();
            foreach (BuildingInstance member in members)
            {
                if (map.TryGetBuilding(member.Transform.Position, out BuildingModel piece))
                {
                    handles.AddRange(NetworkExpansionEngine.HandlesFor(map, executor, registry, piece));
                }
            }

            return handles;
        }

        /// <summary>
        /// One handle per sequence direction. A sequence runs along a single authored direction; the outward
        /// world face grows (advance) and the inward one shrinks (retreat). The authored direction is treated
        /// as local and rotated into world space by the building's placement (consistent with how dynamic
        /// pieces map local faces to world — to be confirmed in-game for static layouts).
        /// </summary>
        private static IReadOnlyList<ExpansionHandle> SequenceHandles(PieceVariantSet set, BuildingModel selected)
        {
            IReadOnlyList<ExpansionOption> options = SequenceEngine.OptionsFor(set.Registration, set.Layout);
            if (options.Count == 0)
            {
                return [];
            }

            var handles = new List<ExpansionHandle>();
            foreach (IGrouping<TileDirection, ExpansionOption> byDirection in options.GroupBy(option => option.Direction))
            {
                bool canGrow = byDirection.Any(option => option.Kind == ExpansionKind.Expand && option.Available);
                bool canShrink = byDirection.Any(option => option.Kind == ExpansionKind.Shrink && option.Available);
                if (canGrow || canShrink)
                {
                    handles.Add(new ExpansionHandle(
                        selected.Id, selected.Transform.Position,
                        byDirection.Key.Rotate(selected.Transform.Rotation), canGrow, canShrink));
                }
            }

            return handles;
        }
    }
}
