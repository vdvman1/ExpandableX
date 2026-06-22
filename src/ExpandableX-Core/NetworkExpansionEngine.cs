using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using Game.Core.Coordinates;

namespace ExpandableX.Core
{
    /// <summary>One growable face of a placed network piece: grow outward (place a joined neighbour) in this direction.</summary>
    public sealed record GrowOption(TileDirection Face, bool Available, string? BlockedReason, IPlayerAction? Action);

    /// <summary>
    /// Removing this end piece, folding its leading connector back onto its neighbour (reverse
    /// pinch-and-stretch). <see cref="FocusAfter"/> is the surviving neighbour the role folds onto — the
    /// piece focus should move to once the shrink settles (the removed end was the focus), so configuring
    /// continues on the remaining network instead of dropping to the many-buildings panel.
    /// </summary>
    public sealed record ShrinkOption(bool Available, string? BlockedReason, IPlayerAction? Action, BuildingId FocusAfter = default);

    /// <summary>
    /// Computes directed grow/shrink moves for a placed <see cref="Layout.Dynamic"/> piece and builds the
    /// undoable actions that perform them (sibling to <see cref="SequenceEngine"/>, for network layouts).
    /// Grow/shrink are id-as-truth definition swaps plus a placement/removal; the network re-forms from
    /// geometry via <see cref="ExpandableSimulationSystem"/>, so nothing is linked or stored (ADR-0012).
    ///
    /// v1 (lean) scope and its known seams:
    /// <list type="bullet">
    /// <item>Grow only into an <b>empty</b> neighbour tile. The new piece joins the source on its back face;
    /// its side/leading faces start as the carried role + <see cref="SlotRole.Disabled"/> spacers.
    /// <b>Incidental fusion (#4):</b> where the new piece lands beside a piece <i>already in the same
    /// network</i>, those shared faces fuse to joins on both sides, dropping the now-trapped gameplay
    /// connectors. The fuse test is <b>same connected component</b> (not merely same family), so a grow can
    /// <b>never</b> merge two separate networks (a grow beside a different building leaves both as ordinary
    /// neighbours), and there is <b>no internal self-feedback</b> — an interior face is always a join, never
    /// a live output→input pair (ADR-0012 amendment 2026-06-21). Fusion changes the building's I/O, so the
    /// result is re-validated against the network predicates and the grow is gated if invalid. A gap-fill
    /// grow (the leading face fuses) drops the carried role; the predicate check catches any invalid state.
    /// Free-branch grows (deliberately growing a side branch) are still not generated.</item>
    /// <item>Grow carries the leading face's gameplay role out to the new end ("pinch and stretch"); the
    /// new piece's side faces default to a <see cref="SlotRole.Disabled"/> spacer. Author-chosen grow
    /// defaults are future work.</item>
    /// <item>Grow re-validates its resulting whole-building network against the layout's network
    /// predicates and gates the option (greyed out with the predicate's reason) if any fails (issue #7).
    /// Pinch-and-stretch keeps role counts invariant, so the framework's <c>AtLeastN</c>-style predicates
    /// always still hold and never gate a grow; a <i>Custom</i> author predicate has no such guarantee,
    /// which is what this check exists for.</item>
    /// <item>Shrink is offered on a removable end (exactly one join) and folds that end's leading role
    /// back onto its neighbour; only that axis role carries (a role manually set on the end's side face
    /// is dropped). Like grow, it re-validates the resulting network against the layout's predicates and
    /// gates the option with the failing reason (issue #7) — the realise-failure path only catches the
    /// 2-piece case, where the folded neighbour becomes a prunable singleton. <b>Limitation:</b> once
    /// incidental fusion gives a piece ≥2 joins (a loop / branch / T / cross), no piece is a single-join
    /// end, so such shapes can't be shrunk piece-by-piece — only whole-network delete. Accepted for v1,
    /// tracked in #12.</item>
    /// <item>Drives the HUD's per-face buttons today; these are a stepping stone to drag handles, which
    /// will grow/shrink whole <i>sides</i> rather than one piece/face — that may want a different surface
    /// than the per-face options here.</item>
    /// </list>
    /// </summary>
    internal static class NetworkExpansionEngine
    {
        private static readonly TileDirection[] PlanarFaces =
            { TileDirection.East, TileDirection.South, TileDirection.West, TileDirection.North };

        /// <summary>Grow options for each planar face of <paramref name="building"/> (empty for non-network families).</summary>
        public static IReadOnlyList<GrowOption> GrowOptionsFor(
            IMapModel map, Player executor, ExpandableXRegistry registry, BuildingModel building)
        {
            var options = new List<GrowOption>();
            if (!TryResolveNetworkPiece(registry, building, out var set, out var worldFaces))
            {
                return options;
            }

            // Read the building's current network once. Used both to detect incidental fusion (a grow that
            // lands the new piece beside same-network pieces, #4) and to re-validate the result against the
            // network predicates (#7). Null only if no matcher is attached (unreachable for a placed dynamic
            // building) — then the grow neither fuses nor gates, exactly as before.
            NetworkCandidate.TryReadFrom(registry, building.Transform.Position, out NetworkCandidate? network);

            GridRotation rotation = building.Transform.Rotation;
            foreach (TileDirection face in PlanarFaces)
            {
                // Only a face that currently carries a gameplay/disabled role can grow; a Join already has
                // a same-building neighbour there.
                if (!worldFaces.TryGetValue(face, out SlotRole currentRole) || currentRole == SlotRole.Join)
                {
                    continue;
                }

                options.Add(BuildGrowOption(map, executor, registry, set, building, worldFaces, rotation, face, currentRole, network));
            }

            return options;
        }

        /// <summary>The shrink move for <paramref name="building"/> if it is a removable end piece; null for a non-network or singleton piece.</summary>
        public static ShrinkOption? ShrinkOptionFor(
            IMapModel map, Player executor, ExpandableXRegistry registry, BuildingModel building)
        {
            if (!TryResolveNetworkPiece(registry, building, out var set, out var worldFaces))
            {
                return null;
            }

            var joinFaces = worldFaces.Where(f => f.Value == SlotRole.Join).Select(f => f.Key).ToList();
            if (joinFaces.Count == 0)
            {
                return null; // a singleton has nothing to shrink — it is already minimal
            }
            if (joinFaces.Count != 1)
            {
                return new ShrinkOption(false, "only an end piece (one join) can shrink", null);
            }

            TileDirection joinDir = joinFaces[0];
            GlobalTileCoordinate neighbourPos = building.Transform.Position.Move(joinDir);
            if (!map.TryGetBuilding(neighbourPos, out BuildingModel neighbour)
                || !registry.VariantsByDefId.TryGetValue(neighbour.Definition.Id.Name, out VariantPlacement neighbourPlacement)
                || neighbourPlacement.Set.Layout.LayoutId != set.Layout.LayoutId)
            {
                return new ShrinkOption(false, "no same-family neighbour to fold into", null);
            }

            // Fold this end's leading (outward) role back onto the neighbour's shared face (reverse of grow).
            SlotRole carried = worldFaces.TryGetValue(joinDir.Opposite, out SlotRole r) ? r : SlotRole.Disabled;
            GridRotation neighbourRotation = neighbour.Transform.Rotation;
            var neighbourWorld = new Dictionary<TileDirection, SlotRole>(
                NetworkPieceRealization.WorldFaceRoles(neighbourPlacement.Set, neighbourPlacement.SlotState, neighbourRotation))
            {
                [joinDir.Opposite] = carried, // neighbour's face pointing back at us drops its join
            };

            if (!NetworkPieceRealization.TryRealize(neighbourPlacement.Set, neighbourWorld, neighbourRotation, out string neighbourDef, out GridRotation neighbourNewRotation, out var neighbourRoles)
                || !TryResolveDefinition(registry, neighbourDef, out var neighbourDefinition))
            {
                return new ShrinkOption(false, "no variant for the folded neighbour", null);
            }

            // Re-validate the resulting whole-building network: shrink removes this end piece and folds its
            // leading role onto the neighbour. For a 2-piece network the neighbour becomes a singleton and
            // an invalid result already fails to realise above; but with more pieces the folded neighbour
            // stays a (never-pruned) network piece, so the building-wide predicates must be checked
            // explicitly (issue #7). Removing a single-join end piece is a leaf removal — the network can't
            // split — so the candidate is just the current network with the end removed and neighbour folded.
            if (set.Layout.NetworkPredicatesOf().Count > 0
                && NetworkCandidate.TryReadFrom(registry, building.Transform.Position, out NetworkCandidate? network))
            {
                NetworkCandidate shrunk = network.With(
                    NetworkChange.Remove(building.Transform.Position),
                    NetworkChange.Place(neighbour.Transform.Position, new PieceState(neighbourPlacement.Set.Piece, neighbourPlacement.Set.Slots, neighbourRoles))
                );

                if (shrunk.FirstViolation() is { } blockedReason)
                {
                    return new ShrinkOption(false, blockedReason, null);
                }
            }

            var remove = new ExpandableXRemoveBuildingAction(
                map, executor, building.Id, building.Definition, building.Transform, building.Configuration);
            var swapNeighbour = new ExpandableXSwapVariantAction(
                map, executor, neighbour.Id,
                new GlobalTileTransform(neighbour.Transform.Position, neighbourNewRotation),
                neighbour.Configuration, neighbour.Definition, neighbourDefinition);

            return new ShrinkOption(true, null, new CombinedUndoablePlayerAction(remove, swapNeighbour), neighbour.Id);
        }

        private static GrowOption BuildGrowOption(
            IMapModel map, Player executor, ExpandableXRegistry registry, PieceVariantSet set,
            BuildingModel building, IReadOnlyDictionary<TileDirection, SlotRole> worldFaces,
            GridRotation rotation, TileDirection face, SlotRole carriedRole, NetworkCandidate? network)
        {
            GlobalTileCoordinate neighbourPos = building.Transform.Position.Move(face);
            if (map.TryGetBuilding(neighbourPos, out _))
            {
                return new GrowOption(face, false, "tile occupied", null);
            }

            // Source: the grown face becomes a Join (it now abuts the new piece).
            var sourceWorld = new Dictionary<TileDirection, SlotRole>(worldFaces) { [face] = SlotRole.Join };
            if (!NetworkPieceRealization.TryRealize(set, sourceWorld, rotation, out string sourceDef, out GridRotation sourceRotation, out var sourceRoles)
                || !TryResolveDefinition(registry, sourceDef, out var sourceDefinition))
            {
                return new GrowOption(face, false, "no variant for the grown source", null);
            }

            // New piece: join back at the source, carry the leading role out to the far face, sides spacer.
            var pieceWorld = new Dictionary<TileDirection, SlotRole>
            {
                [face.Opposite] = SlotRole.Join,
                [face] = carriedRole,
                [face.Rotate(GridRotation.RotateCW)] = SlotRole.Disabled,
                [face.Rotate(GridRotation.RotateCCW)] = SlotRole.Disabled,
            };

            // Incidental fusion (#4): any non-back face of the new piece that abuts a piece already in the
            // source's network becomes interior — fuse it to a Join on both sides, dropping the neighbour's
            // now-trapped gameplay connector. The fuse test is same connected component (network.Contains),
            // never just same family, so a grow can never merge two separate networks; a grow beside a
            // different building just leaves both as ordinary neighbours. Applied as an overlay after the
            // pinch-and-stretch carry above, so a gap-fill grow (the leading face landing against a
            // same-network piece) fuses and drops the carried role — the predicate check below is the safety
            // net. Each fused neighbour folds its facing face to a Join and rides along as an extra swap.
            var fusedNeighbours = new List<(BuildingModel Building, IBuildingDefinition Definition, GridRotation Rotation, PieceState State)>();
            foreach (TileDirection side in PlanarFaces)
            {
                if (side == face.Opposite)
                {
                    continue; // the back face already joins the source
                }

                GlobalTileCoordinate adjacentPos = neighbourPos.Move(side);
                if (network is null
                    || !map.TryGetBuilding(adjacentPos, out BuildingModel adjacent)
                    || adjacent.Id == building.Id
                    || !network.Contains(adjacent.Transform.Position)
                    || !registry.VariantsByDefId.TryGetValue(adjacent.Definition.Id.Name, out VariantPlacement? adjacentPlacement))
                {
                    continue;
                }

                pieceWorld[side] = SlotRole.Join;

                var adjacentWorld = new Dictionary<TileDirection, SlotRole>(
                    NetworkPieceRealization.WorldFaceRoles(adjacentPlacement.Set, adjacentPlacement.SlotState, adjacent.Transform.Rotation))
                {
                    [side.Opposite] = SlotRole.Join, // the neighbour's face pointing back at the new piece
                };
                if (!NetworkPieceRealization.TryRealize(adjacentPlacement.Set, adjacentWorld, adjacent.Transform.Rotation, out string adjacentDef, out GridRotation adjacentRotation, out var adjacentRoles)
                    || !TryResolveDefinition(registry, adjacentDef, out var adjacentDefinition))
                {
                    return new GrowOption(face, false, "no variant for a fused neighbour", null);
                }

                fusedNeighbours.Add((adjacent, adjacentDefinition, adjacentRotation,
                    new PieceState(adjacentPlacement.Set.Piece, adjacentPlacement.Set.Slots, adjacentRoles)));
            }

            if (!NetworkPieceRealization.TryRealize(set, pieceWorld, GridRotation.NoRotate, out string pieceDef, out GridRotation pieceRotation, out var pieceRoles)
                || !TryResolveDefinition(registry, pieceDef, out var pieceDefinition))
            {
                return new GrowOption(face, false, "no variant for the new piece", null);
            }

            // Re-validate the resulting whole-building network (issue #7): the grown source, the new piece,
            // and any fused neighbours (whose dropped connectors change the building's I/O). Pinch-and-
            // stretch alone keeps role counts invariant, but fusion does not — so this gate is what stops a
            // grow that would, say, fuse away the building's last Output.
            if (network is not null)
            {
                var grown = network.With([
                    NetworkChange.Place(building.Transform.Position, new PieceState(set.Piece, set.Slots, sourceRoles)),
                    NetworkChange.Place(neighbourPos, new PieceState(set.Piece, set.Slots, pieceRoles)),
                    ..fusedNeighbours.Select(fusedNeighbour => NetworkChange.Place(fusedNeighbour.Building.Transform.Position, fusedNeighbour.State))
                ]);

                if (grown.FirstViolation() is { } blockedReason)
                {
                    return new GrowOption(face, false, blockedReason, null);
                }
            }

            // One combined undoable action: swap the source, swap each fused neighbour, then place the new
            // piece. The network re-forms from geometry once the bunch edit settles (ADR-0012).
            List<IPlayerAction> actions =
            [
                new ExpandableXSwapVariantAction(
                    map, executor, building.Id,
                    new GlobalTileTransform(building.Transform.Position, sourceRotation),
                    building.Configuration, building.Definition, sourceDefinition),
                ..fusedNeighbours.Select(fusedNeighbour => new ExpandableXSwapVariantAction(
                    map, executor, fusedNeighbour.Building.Id,
                    new GlobalTileTransform(fusedNeighbour.Building.Transform.Position, fusedNeighbour.Rotation),
                    fusedNeighbour.Building.Configuration, fusedNeighbour.Building.Definition, fusedNeighbour.Definition)),
                new ExpandableXPlaceBuildingAction(
                    map, executor, pieceDefinition, new GlobalTileTransform(neighbourPos, pieceRotation), configuration: null),
            ];

            return new GrowOption(face, true, null, new CombinedUndoablePlayerAction(actions));
        }

        /// <summary>Resolve a placed building to its network family's variant set + current world-face roles, if it is one.</summary>
        private static bool TryResolveNetworkPiece(
            ExpandableXRegistry registry, BuildingModel building,
            [NotNullWhen(true)] out PieceVariantSet? set,
            [NotNullWhen(true)] out IReadOnlyDictionary<TileDirection, SlotRole>? worldFaces)
        {
            set = null;
            worldFaces = null;
            if (!registry.VariantsByDefId.TryGetValue(building.Definition.Id.Name, out VariantPlacement placement)
                || placement.Set.Layout is not Layout.Dynamic)
            {
                return false;
            }

            set = placement.Set;
            worldFaces = NetworkPieceRealization.WorldFaceRoles(set, placement.SlotState, building.Transform.Rotation);
            return true;
        }

        private static bool TryResolveDefinition(ExpandableXRegistry registry, string definitionId, [NotNullWhen(true)] out IBuildingDefinition? definition)
        {
            definition = null;
            GameMode mode = registry.CurrentMode;
            if (mode is null)
            {
                return false;
            }

#pragma warning disable CS0618
            var id = new BuildingDefinitionId(definitionId);
#pragma warning restore CS0618
            return mode.Buildings._DefinitionsById.TryGetValue(id, out definition);
        }
    }
}
