using System;
using System.Collections.Generic;
using System.Linq;
using Game.Core.Coordinates;
using Unity.Mathematics;

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
        /// One handle per sequence direction. A sequence runs along a single authored direction (local,
        /// rotated into world by the building's placement): dragging out along it grows (advance), inward
        /// shrinks (retreat). Unlike a network piece (one tile, handle on its own face), a static building is
        /// one multi-tile entity, so the handle is anchored on the footprint's furthest tile in that
        /// direction — the edge where the next cell is added / the last one removed — read from the building's
        /// own footprint, not its origin.
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
                if (!canGrow && !canShrink)
                {
                    continue;
                }

                TileDirection worldDirection = byDirection.Key.Rotate(selected.Transform.Rotation);
                handles.Add(new ExpansionHandle(
                    selected.Id, FurthestFootprintTile(selected, worldDirection), worldDirection, canGrow, canShrink));
            }

            return handles;
        }

        /// <summary>The building's occupied footprint tile furthest along <paramref name="worldDirection"/> — the edge a sequence grows from / shrinks back to. Falls back to the origin if the footprint can't be read.</summary>
        private static GlobalTileCoordinate FurthestFootprintTile(BuildingModel building, TileDirection worldDirection)
        {
            GlobalTileCoordinate origin = building.Transform.Position;
            float3 originCenter = (float3)origin.ToCenter_W();
            float3 step = (float3)origin.Move(worldDirection).ToCenter_W() - originCenter;

            GlobalTileCoordinate furthest = origin;
            float best = float.NegativeInfinity;

            // ConnectorData is marked obsolete ("should be attached") but still returns the live footprint
            // tiles; reading it is the delegate-to-game approach the mod already takes for such APIs.
#pragma warning disable CS0618
            TileVector[] tiles = building.Definition.ConnectorData.Tiles;
#pragma warning restore CS0618
            foreach (TileVector local in tiles)
            {
                GlobalTileCoordinate tile = local.ToGlobal(building.Transform);
                float projection = math.dot((float3)tile.ToCenter_W() - originCenter, step);
                if (projection > best)
                {
                    best = projection;
                    furthest = tile;
                }
            }

            return furthest;
        }
    }
}
