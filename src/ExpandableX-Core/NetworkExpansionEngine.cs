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

    /// <summary>How a <see cref="GhostPiece"/> previews: a newly-<see cref="Placed"/> piece, an existing piece whose variant is <see cref="Changed"/> (e.g. a connector fusing to a join), or one that will be <see cref="Removed"/>.</summary>
    public enum GhostKind { Placed, Changed, Removed }

    /// <summary>One previewed piece of an in-progress grow/shrink — a building definition at a transform, drawn as a blueprint ghost coloured by its <see cref="Kind"/> (new placements vs. existing pieces whose connectors change to joins vs. removals).</summary>
    public readonly record struct GhostPiece(IBuildingDefinition Definition, GlobalTileTransform Transform, GhostKind Kind);

    /// <summary>
    /// The result of forward-simulating a multi-tile grow along one face (the basis for drag-handle
    /// expansion, issue #5 / ADR-0014): how many tiles can actually be added — <see cref="Tiles"/>,
    /// clamped at the first blocked cell, 0 if not even one fits — the single combined undoable action
    /// that adds exactly that many (null when <see cref="Tiles"/> is 0 or the caller asked not to build it),
    /// and <see cref="BlockedReason"/>, the reason the chain stopped short of the request (null when the full
    /// request was satisfied or it simply ran out of empty space without an error). <see cref="Ghosts"/> are
    /// the clamped preview pieces for the drag — the new placements plus the existing pieces whose variant
    /// changes (connectors fusing to joins) — so previewing them inherently respects the blocked extent.
    /// </summary>
    public sealed record GrowChainResult(int Tiles, IPlayerAction? Action, string? BlockedReason, IReadOnlyList<GhostPiece> Ghosts);

    /// <summary>
    /// The result of forward-simulating a multi-tile inward shrink from a removable end (the inward-drag
    /// mirror of <see cref="GrowChainResult"/>, issue #5 / ADR-0014): how many end pieces can actually be
    /// removed — <see cref="Tiles"/>, clamped, 0 if none — the single combined undoable action that removes
    /// exactly that many and folds the grabbed end's leading role onto the survivor, <see cref="BlockedReason"/>
    /// (the predicate/realisation reason a longer shrink was refused, null when it simply ran out of spine),
    /// <see cref="Ghosts"/>, the clamped preview pieces (the end pieces being removed, plus the folded
    /// survivor in its new variant), and <see cref="FocusAfter"/>, the surviving piece the focus should move
    /// to once the shrink settles.
    /// </summary>
    public sealed record ShrinkChainResult(int Tiles, IPlayerAction? Action, string? BlockedReason, IReadOnlyList<GhostPiece> Ghosts, BuildingId FocusAfter = default);

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
        // NOTE(refactor, post-#5): the single-tile path (GrowOptionsFor/BuildGrowOption, ShrinkOptionFor)
        // and the multi-tile chain path (GrowChainFor/ShrinkChainFor) repeat the same realise → fuse →
        // predicate-validate shape at two granularities — single-tile is just a chain of length 1. They can
        // likely converge once the drag UI lands; kept separate for now to avoid churn mid-feature.
        private static readonly TileDirection[] PlanarFaces =
            { TileDirection.East, TileDirection.South, TileDirection.West, TileDirection.North };

        // TUNABLE: how far HandlesFor probes a *blocked* face for a valid longer grow before hiding its
        // handle (tiles). It only runs when the cheap single-tile grow is already blocked, so it stays cheap;
        // bounded (rather than the drag's full range) because it realises a trial chain. A grow that becomes
        // valid only beyond this distance won't show a handle — the common drag-through case needs a tile or
        // two — while the drag itself, once started, still runs to its own larger cap.
        private const int GrowLivenessProbeTiles = 8;

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
                neighbour.Transform,
                new GlobalTileTransform(neighbour.Transform.Position, neighbourNewRotation),
                neighbour.Configuration, neighbour.Definition, neighbourDefinition);

            return new ShrinkOption(true, null, new CombinedUndoablePlayerAction(remove, swapNeighbour), neighbour.Id);
        }

        /// <summary>
        /// Forward-simulate growing <paramref name="building"/> outward along <paramref name="face"/> by up
        /// to <paramref name="maxTiles"/> tiles — the multi-tile-per-drag basis for drag handles (ADR-0014).
        /// The chain is a straight pinch-and-stretch run: the source's grown face becomes a Join, each new
        /// piece joins the one behind it (sides are <see cref="SlotRole.Disabled"/> spacers), and the
        /// carried gameplay role rides out to the final end piece. The walk stops at the first occupied tile
        /// (or a piece that can't be realised), then shrinks the count until the whole-building result
        /// satisfies the network predicates — so the returned <see cref="GrowChainResult.Tiles"/> is always
        /// a valid grow.
        ///
        /// v1 first cut and its seams (matching <see cref="BuildGrowOption"/>'s single-tile scope):
        /// <list type="bullet">
        /// <item><b>Incidental fusion is applied along the chain</b> (#4, at parity with the single-tile
        /// path): any side face of a new piece — and the final end piece's leading face — that abuts a piece
        /// already in the source's network fuses to a Join on both sides, dropping the neighbour's now-trapped
        /// connector. So a chain dragged alongside an existing same-network arm fuses the touching faces, and
        /// a chain whose end lands against a same-network piece gap-fills into it. Fusion changes the
        /// building's I/O, so the whole-building result is re-validated and the count shrinks until it holds
        /// (see <see cref="TryBuildChainAction"/>).</item>
        /// <item><b>Grow-through threads the chain past same-network tiles</b> (#5): a tile holding a piece
        /// already in this building's network is passable — the spine passes through it (its entry and exit
        /// faces fuse to Joins, merging it in) and keeps going, as long as the whole-building result stays
        /// valid. A <i>foreign</i> building (or a same-network tile whose variant can't be resolved) is still
        /// a hard stop. The chain can end on either a new end piece or a pass-through piece: pinch-and-stretch
        /// carries the source's leading role onto the final tile. Ending on a pass-through is unconditional
        /// when the grown face carries no live connector (a Disabled spacer); when it carries a real connector
        /// the pass-through's leading face must be free (Disabled) to receive it, else a shorter chain is
        /// taken so an in-use connector is never clobbered.</item>
        /// <item><b>ShapeLimit is not gated here</b> — neither does the single-tile path; the v1 AND gate is
        /// <see cref="ShapeLimits.Free"/>. Wire alongside the Line/Rectangle limits (#27).</item>
        /// </list>
        /// </summary>
        public static GrowChainResult GrowChainFor(
            IMapModel map, Player executor, ExpandableXRegistry registry, BuildingModel building,
            TileDirection face, int maxTiles, bool buildAction = true)
        {
            if (maxTiles <= 0 || !TryResolveNetworkPiece(registry, building, out var set, out var worldFaces))
            {
                return new GrowChainResult(0, null, null, []);
            }

            // Only a face that currently carries a gameplay/disabled role can grow; a Join already abuts a
            // same-building neighbour, and a face with no slot has no connector to carry out.
            if (!worldFaces.TryGetValue(face, out SlotRole carriedRole) || carriedRole == SlotRole.Join)
            {
                return new GrowChainResult(0, null, "cannot grow from this face", []);
            }

            NetworkCandidate.TryReadFrom(registry, building.Transform.Position, out NetworkCandidate? network);
            GridRotation rotation = building.Transform.Rotation;

            // Walk outward, building the path the chain would follow. An empty tile is a slot for a new
            // piece; a tile holding a piece already in this building's network is *passable* — the chain
            // threads through it (fusing the touched faces) and keeps going (grow-through, #5); a foreign
            // building, or a same-network tile we can't resolve, is a hard stop. maxTiles bounds the drag.
            var path = new List<(GlobalTileCoordinate Pos, bool IsNew)>(maxTiles);
            GlobalTileCoordinate pos = building.Transform.Position;
            string? clampReason = null;
            for (int k = 0; k < maxTiles; k++)
            {
                pos = pos.Move(face);
                if (!map.TryGetBuilding(pos, out BuildingModel occupant))
                {
                    path.Add((pos, true)); // empty: a new piece
                    continue;
                }

                // Pass through a same-network piece (never the source — it is behind us); stop at anything
                // else. The pass-through test is same connected component, so grow-through can't thread into a
                // *different* network — that stays a hard stop, exactly like a foreign building.
                if (network is not null
                    && occupant.Id != building.Id
                    && network.Contains(occupant.Transform.Position)
                    && registry.VariantsByDefId.ContainsKey(occupant.Definition.Id.Name))
                {
                    path.Add((pos, false)); // same-network: a pass-through
                    continue;
                }

                // Only the very first tile being a hard stop is an actual error to report; clamping farther
                // out is just "grew as far as the space allowed".
                clampReason = k == 0 ? "tile occupied" : null;
                break;
            }

            // Take the largest reachable count whose realised whole-building result satisfies the network
            // predicates, shrinking until it validates. The chain can end on either a new tile or a
            // pass-through tile: pinch-and-stretch carries the source's leading role onto whatever the final
            // tile is — a new end piece, or an existing pass-through piece whose leading face is free
            // (TryBuildChainAction gates the latter on that face currently being Disabled, refusing the count
            // otherwise so a shorter chain is tried).
            for (int count = path.Count; count >= 1; count--)
            {
                if (TryBuildChainAction(
                        map, executor, registry, set, building, worldFaces, rotation, face, carriedRole,
                        path, count, network, buildAction, out IPlayerAction? action, out string? reason, out IReadOnlyList<GhostPiece> ghosts))
                {
                    // Surface a reason only when we couldn't satisfy the full request: a hard occupancy
                    // error on the first tile, or the predicate that forced us to stop short.
                    string? blocked = count < maxTiles ? (clampReason ?? reason) : null;
                    return new GrowChainResult(count, action, blocked, ghosts);
                }

                clampReason ??= reason;
            }

            return new GrowChainResult(0, null, clampReason ?? "no valid grow", []);
        }

        /// <summary>
        /// Build the combined swap-source + swap-existing + place-new-pieces action for the first
        /// <paramref name="count"/> steps of <paramref name="path"/> (a straight run of empty "new" tiles and
        /// pass-through same-network tiles), applying incidental fusion (#4) to any new-piece face that abuts
        /// a same-network piece, threading the spine through each pass-through tile (grow-through, #5), and
        /// validating the result against the network predicates. Returns false (with a <paramref name="reason"/>)
        /// if any piece can't be realised or the result is invalid, so <see cref="GrowChainFor"/> can shrink
        /// the count and retry. The chain's final tile carries the stretched role: a new end piece, or — when
        /// the last step is a pass-through — that existing piece, but only if its leading face is free
        /// (currently <see cref="SlotRole.Disabled"/>); otherwise this count is refused so the caller shrinks.
        /// </summary>
        private static bool TryBuildChainAction(
            IMapModel map, Player executor, ExpandableXRegistry registry, PieceVariantSet set,
            BuildingModel building, IReadOnlyDictionary<TileDirection, SlotRole> worldFaces,
            GridRotation rotation, TileDirection face, SlotRole carriedRole,
            IReadOnlyList<(GlobalTileCoordinate Pos, bool IsNew)> path, int count,
            NetworkCandidate? network, bool buildAction,
            out IPlayerAction? action, out string? reason, out IReadOnlyList<GhostPiece> ghosts)
        {
            action = null;
            reason = null;
            ghosts = [];

            // Source: the grown face becomes a Join (it now abuts the first chain tile).
            var sourceWorld = new Dictionary<TileDirection, SlotRole>(worldFaces) { [face] = SlotRole.Join };
            if (!NetworkPieceRealization.TryRealize(set, sourceWorld, rotation, out string sourceDef, out GridRotation sourceRotation, out var sourceRoles)
                || !TryResolveDefinition(registry, sourceDef, out var sourceDefinition))
            {
                reason = "no variant for the grown source";
                return false;
            }

            TileDirection back = face.Opposite;
            TileDirection sideCw = face.Rotate(GridRotation.RotateCW);
            TileDirection sideCcw = face.Rotate(GridRotation.RotateCCW);

            // Existing pieces this chain modifies, accumulated and swapped once each: pass-through tiles the
            // spine threads through (grow-through, #5) and side-fusion neighbours a new piece runs alongside
            // (#4). Keyed by position. WorldFaceRoles seeds the entry on first touch; later touches just layer
            // more Join overrides on the shared dict.
            var modifications = new Dictionary<GlobalTileCoordinate, (BuildingModel Building, PieceVariantSet Set, Dictionary<TileDirection, SlotRole> World)>();

            bool TryModify(GlobalTileCoordinate at, [NotNullWhen(true)] out Dictionary<TileDirection, SlotRole>? world)
            {
                if (modifications.TryGetValue(at, out var existing))
                {
                    world = existing.World;
                    return true;
                }

                world = null;
                if (!map.TryGetBuilding(at, out BuildingModel piece)
                    || !registry.VariantsByDefId.TryGetValue(piece.Definition.Id.Name, out VariantPlacement? placement))
                {
                    return false;
                }

                world = new Dictionary<TileDirection, SlotRole>(
                    NetworkPieceRealization.WorldFaceRoles(placement.Set, placement.SlotState, piece.Transform.Rotation));
                modifications[at] = (piece, placement.Set, world);
                return true;
            }

            var places = new List<(GlobalTileCoordinate Pos, IBuildingDefinition Def, GridRotation Rot, PieceState State)>();
            for (int k = 1; k <= count; k++)
            {
                (GlobalTileCoordinate piecePos, bool isNew) = path[k - 1];
                bool isLast = k == count;

                if (!isNew)
                {
                    // Pass-through: thread the spine through an existing same-network piece, merging it in.
                    // Its back face (toward the previous tile) fuses to a Join. While the chain continues past
                    // it, its leading face becomes a Join too. When this is the final tile the existing piece
                    // is the new end: if a real gameplay connector is being carried out (the grown face wasn't
                    // a Disabled spacer) it lands on the leading face — but only onto a free (Disabled) slot,
                    // so an in-use connector is never clobbered (otherwise refuse and let GrowChainFor shrink).
                    // A carried Disabled spacer has nothing to place, so the leading face is left untouched.
                    if (!TryModify(piecePos, out Dictionary<TileDirection, SlotRole>? throughWorld))
                    {
                        reason = "cannot pass through this tile";
                        return false;
                    }

                    throughWorld[back] = SlotRole.Join;

                    if (!isLast)
                    {
                        throughWorld[face] = SlotRole.Join;
                    }
                    else if (carriedRole != SlotRole.Disabled)
                    {
                        if (!throughWorld.TryGetValue(face, out SlotRole leadingRole) || leadingRole != SlotRole.Disabled)
                        {
                            reason = "cannot end the grow on this piece (its leading face is in use)";
                            return false;
                        }

                        throughWorld[face] = carriedRole;
                    }

                    continue;
                }

                // New piece: joins the tile behind it; an intermediate new piece also joins the next tile
                // ahead, while the final piece carries the stretched gameplay role out to the new end.
                var pieceWorld = new Dictionary<TileDirection, SlotRole>
                {
                    [back] = SlotRole.Join,
                    [sideCw] = SlotRole.Disabled,
                    [sideCcw] = SlotRole.Disabled,
                    [face] = isLast ? carriedRole : SlotRole.Join,
                };

                // Incidental fusion (#4): any face that isn't already a spine Join, where it abuts a piece in
                // the source's network, fuses to a Join on both sides — a side alongside an existing arm, or
                // the final leading face gap-filling into a piece just beyond. Same connected component only
                // (network.Contains), so a chain can never merge two separate networks (ADR-0012 amendment).
                if (network is not null)
                {
                    foreach (TileDirection side in PlanarFaces)
                    {
                        if (pieceWorld[side] == SlotRole.Join)
                        {
                            continue;
                        }

                        GlobalTileCoordinate adjacentPos = piecePos.Move(side);
                        if (!map.TryGetBuilding(adjacentPos, out BuildingModel adjacent)
                            || adjacent.Id == building.Id
                            || !network.Contains(adjacent.Transform.Position)
                            || !TryModify(adjacentPos, out Dictionary<TileDirection, SlotRole>? neighbourWorld))
                        {
                            continue;
                        }

                        pieceWorld[side] = SlotRole.Join;
                        neighbourWorld[side.Opposite] = SlotRole.Join; // the neighbour's face pointing back at this piece
                    }
                }

                if (!NetworkPieceRealization.TryRealize(set, pieceWorld, GridRotation.NoRotate, out string pieceDef, out GridRotation pieceRotation, out var pieceRoles)
                    || !TryResolveDefinition(registry, pieceDef, out var pieceDefinition))
                {
                    reason = "no variant for a chain piece";
                    return false;
                }

                places.Add((piecePos, pieceDefinition, pieceRotation, new PieceState(set.Piece, set.Slots, pieceRoles)));
            }

            // Realize each modified existing piece (pass-throughs + fused side-neighbours) with its
            // accumulated face changes.
            var swaps = new List<(BuildingModel Building, IBuildingDefinition Def, GridRotation Rot, PieceState State)>(modifications.Count);
            foreach (var (existing, existingSet, existingWorld) in modifications.Values)
            {
                if (!NetworkPieceRealization.TryRealize(existingSet, existingWorld, existing.Transform.Rotation, out string existingDef, out GridRotation existingRotation, out var existingRoles)
                    || !TryResolveDefinition(registry, existingDef, out var existingDefinition))
                {
                    reason = "no variant for a fused neighbour";
                    return false;
                }

                swaps.Add((existing, existingDefinition, existingRotation,
                    new PieceState(existingSet.Piece, existingSet.Slots, existingRoles)));
            }

            // Re-validate the projected whole-building network (issue #7): the grown source, every new chain
            // piece, and every modified existing piece (pass-throughs and fused neighbours change the
            // building's I/O). Pure pinch-and-stretch keeps role counts invariant, but fusion / pass-through
            // do not — this gate stops a chain that would fuse away a required connector or otherwise break a
            // predicate, and GrowChainFor shrinks the count until it holds.
            if (network is not null)
            {
                NetworkCandidate grown = network.With([
                    NetworkChange.Place(building.Transform.Position, new PieceState(set.Piece, set.Slots, sourceRoles)),
                    ..places.Select(place => NetworkChange.Place(place.Pos, place.State)),
                    ..swaps.Select(swap => NetworkChange.Place(swap.Building.Transform.Position, swap.State)),
                ]);

                if (grown.FirstViolation() is { } violation)
                {
                    reason = violation;
                    return false;
                }
            }

            // Everything the chain changes, for the drag preview to render as ghosts: the new pieces (Placed),
            // and the existing pieces whose variant changes — the grown source and every fused / pass-through
            // neighbour (Changed, e.g. a connector becoming a join). The Changed ghosts let the player see the
            // connector→join changes happening on adjacent pieces alongside the new ones.
            ghosts = [
                new GhostPiece(sourceDefinition, new GlobalTileTransform(building.Transform.Position, sourceRotation), GhostKind.Changed),
                ..swaps.Select(swap => new GhostPiece(swap.Def, new GlobalTileTransform(swap.Building.Transform.Position, swap.Rot), GhostKind.Changed)),
                ..places.Select(place => new GhostPiece(place.Def, new GlobalTileTransform(place.Pos, place.Rot), GhostKind.Placed))
            ];

            // The caller may only want the validated ghosts (preview / liveness), not the undoable action.
            if (!buildAction)
            {
                return true;
            }

            // One combined undoable action: swap the source to its Join'd variant, swap each modified existing
            // piece, then place each new piece. The network re-forms from geometry once the bunch edit settles
            // (ADR-0012).
            List<IPlayerAction> actions =
            [
                new ExpandableXSwapVariantAction(
                    map, executor, building.Id,
                    building.Transform,
                    new GlobalTileTransform(building.Transform.Position, sourceRotation),
                    building.Configuration, building.Definition, sourceDefinition),
                ..swaps.Select(swap => new ExpandableXSwapVariantAction(
                    map, executor, swap.Building.Id,
                    swap.Building.Transform,
                    new GlobalTileTransform(swap.Building.Transform.Position, swap.Rot),
                    swap.Building.Configuration, swap.Building.Definition, swap.Def)),
                ..places.Select(place => new ExpandableXPlaceBuildingAction(
                    map, executor, place.Def, new GlobalTileTransform(place.Pos, place.Rot), configuration: null)),
            ];

            action = new CombinedUndoablePlayerAction(actions);
            return true;
        }

        /// <summary>
        /// Forward-simulate shrinking <paramref name="building"/> inward from its end by up to
        /// <paramref name="maxTiles"/> pieces — the multi-tile-per-drag inward mirror of
        /// <see cref="GrowChainFor"/> (ADR-0014). <paramref name="building"/> must be a removable end (one
        /// join); the walk follows the spine inward <i>in a straight line</i> (the drag is one straight
        /// gesture — it never bends around corners, matching <see cref="GrowChainFor"/>), removing each
        /// successive end piece and folding the grabbed end's leading (outward) role onto the final survivor
        /// (reverse pinch-and-stretch). It stops at the far end (the survivor becomes a singleton), a corner
        /// (the spine turns), or a branch (a piece with ≥2 joins after the removal, never a single-join
        /// end — #12), then shrinks the count until the result satisfies the network predicates, so the
        /// returned <see cref="ShrinkChainResult.Tiles"/> is always a valid shrink that leaves the network
        /// connected (leaf removals can't split it).
        /// </summary>
        public static ShrinkChainResult ShrinkChainFor(
            IMapModel map, Player executor, ExpandableXRegistry registry, BuildingModel building, int maxTiles,
            bool buildAction = true)
        {
            if (maxTiles <= 0 || !TryResolveNetworkPiece(registry, building, out var set, out var worldFaces))
            {
                return new ShrinkChainResult(0, null, null, []);
            }

            var joinFaces = worldFaces.Where(f => f.Value == SlotRole.Join).Select(f => f.Key).ToList();
            if (joinFaces.Count == 0)
            {
                return new ShrinkChainResult(0, null, null, []); // a singleton is already minimal
            }
            if (joinFaces.Count != 1)
            {
                return new ShrinkChainResult(0, null, "only an end piece (one join) can shrink", []);
            }

            // The role carried back to the surviving end: the grabbed end's leading (outward) role rides
            // onto the final survivor's freed face (reverse of grow's pinch-and-stretch).
            TileDirection grabbedJoin = joinFaces[0];
            SlotRole carried = worldFaces.TryGetValue(grabbedJoin.Opposite, out SlotRole r) ? r : SlotRole.Disabled;

            NetworkCandidate.TryReadFrom(registry, building.Transform.Position, out NetworkCandidate? network);

            // Walk the spine inward along a fixed straight axis. Each step records the piece removed, the
            // neighbour the role would fold onto if the shrink stops here, that neighbour's set/world-faces,
            // and the face on it pointing back at the removed piece (which the carried role lands on). We keep
            // walking only while the neighbour stays a straight pass-through along that axis; a corner, a
            // branch, or the far end caps the spine (see the continue check below).
            var steps = new List<(BuildingModel Remove, BuildingModel Survivor, PieceVariantSet SurvivorSet,
                IReadOnlyDictionary<TileDirection, SlotRole> SurvivorWorld, TileDirection FoldFace)>();
            BuildingModel cur = building;
            TileDirection inwardDir = grabbedJoin;
            while (steps.Count < maxTiles)
            {
                GlobalTileCoordinate neighbourPos = cur.Transform.Position.Move(inwardDir);
                if (!map.TryGetBuilding(neighbourPos, out BuildingModel neighbour)
                    || !registry.VariantsByDefId.TryGetValue(neighbour.Definition.Id.Name, out VariantPlacement? neighbourPlacement)
                    || neighbourPlacement.Set.Layout.LayoutId != set.Layout.LayoutId)
                {
                    break; // no same-family neighbour to fold into
                }

                var neighbourWorld = NetworkPieceRealization.WorldFaceRoles(
                    neighbourPlacement.Set, neighbourPlacement.SlotState, neighbour.Transform.Rotation);
                var neighbourJoins = neighbourWorld.Where(f => f.Value == SlotRole.Join).Select(f => f.Key).ToList();
                TileDirection backFace = inwardDir.Opposite; // neighbour's face pointing back at cur

                steps.Add((cur, neighbour, neighbourPlacement.Set, neighbourWorld, backFace));

                // Continue the spine only while it runs *straight*. Removing cur frees the neighbour's join on
                // backFace; the spine carries on only if the neighbour then becomes a single-join end whose
                // one remaining join points the same way we're travelling (inwardDir). A corner (the remaining
                // join is perpendicular), a branch (≥2 remaining), or the far end (0 remaining) caps the spine
                // — the neighbour is the survivor. The drag is one straight gesture, so it never bends around
                // corners (matching GrowChainFor); path-tracing shrink could be a deliberate future gesture.
                // The joins the neighbour keeps once cur is gone: all of them except the seam toward cur
                // (backFace). backFace is guaranteed present here — border-closing (ADR-0012) pairs joins
                // across a shared seam, and cur is known to join the neighbour on inwardDir — so this is
                // Count - 1 in practice; expressing it as "all but backFace" stays correct even if that
                // invariant is ever violated, without a bare magic subtraction.
                int remainingJoins = neighbourJoins.Count(direction => direction != backFace);
                if (remainingJoins != 1 || !neighbourJoins.Contains(inwardDir))
                {
                    break;
                }

                cur = neighbour;
                // inwardDir stays fixed — the spine, like the drag, is straight.
            }

            // Take the largest reachable count whose folded survivor realises and whose whole-building result
            // satisfies the network predicates. Removals carry no gameplay role and the one carried role just
            // moves, so role counts stay invariant — only a Custom predicate can gate, so shrink until valid.
            string? lastReason = null;
            for (int count = steps.Count; count >= 1; count--)
            {
                var step = steps[count - 1];
                var survivorWorld = new Dictionary<TileDirection, SlotRole>(step.SurvivorWorld)
                {
                    [step.FoldFace] = carried, // the face that joined the last removed piece now carries the role
                };

                if (!NetworkPieceRealization.TryRealize(step.SurvivorSet, survivorWorld, step.Survivor.Transform.Rotation, out string survivorDef, out GridRotation survivorRotation, out var survivorRoles)
                    || !TryResolveDefinition(registry, survivorDef, out var survivorDefinition))
                {
                    lastReason = "no variant for the folded survivor";
                    continue;
                }

                if (network is not null)
                {
                    var shrunk = network.With([
                        ..steps.Take(count).Select(remove => NetworkChange.Remove(remove.Remove.Transform.Position)),
                        NetworkChange.Place(
                            step.Survivor.Transform.Position,
                            new PieceState(step.SurvivorSet.Piece, step.SurvivorSet.Slots, survivorRoles))
                    ]);

                    if (shrunk.FirstViolation() is { } violation)
                    {
                        lastReason = violation;
                        continue;
                    }
                }

                // Preview ghosts: the end pieces being peeled back (Removed → red) and the folded survivor in
                // its new variant (Changed → amber, where the carried role lands).
                List<GhostPiece> ghosts = [
                    ..steps.Take(count).Select(remove => new GhostPiece(remove.Remove.Definition, remove.Remove.Transform, GhostKind.Removed)),
                    new GhostPiece(survivorDefinition, new GlobalTileTransform(step.Survivor.Transform.Position, survivorRotation), GhostKind.Changed)
                ];

                // The caller may only want the validated ghosts (preview), not the undoable action.
                IPlayerAction? action = null;
                if (buildAction)
                {
                    List<IPlayerAction> actions = [
                        ..steps.Take(count).Select(remove => new ExpandableXRemoveBuildingAction(
                            map, executor, remove.Remove.Id, remove.Remove.Definition, remove.Remove.Transform, remove.Remove.Configuration)),
                        new ExpandableXSwapVariantAction(
                            map, executor, step.Survivor.Id,
                            step.Survivor.Transform,
                            new GlobalTileTransform(step.Survivor.Transform.Position, survivorRotation),
                            step.Survivor.Configuration, step.Survivor.Definition, survivorDefinition)
                    ];
                    action = new CombinedUndoablePlayerAction(actions);
                }

                // Surface a reason only when a longer shrink was actually refused (predicate/realisation);
                // simply running out of spine (a shorter chain than requested) is not an error.
                string? blocked = count < steps.Count ? lastReason : null;
                return new ShrinkChainResult(count, action, blocked, ghosts, step.Survivor.Id);
            }

            return new ShrinkChainResult(0, null, lastReason, [], default);
        }

        /// <summary>
        /// The drag handles on <paramref name="building"/> itself (one network piece) — the per-piece slice
        /// of the unified control surface (issue #5 / ADR-0014). Each outer face that can grow and/or the
        /// single leading face that can shrink becomes one <see cref="ExpansionHandle"/>, with the two
        /// directions merged: the leading face of a removable end is both a grow face (drag out) and a shrink
        /// face (drag in). Shrink liveness reuses the single-tile <see cref="ShrinkOptionFor"/>; grow liveness
        /// takes the single-tile <see cref="GrowOptionsFor"/> and, when that face is blocked, falls back to a
        /// bounded <see cref="GrowChainFor"/> probe (a longer drag-through can be valid where one tile is not).
        /// The drag magnitude itself then runs through <see cref="GrowChainFor"/> / <see cref="ShrinkChainFor"/>.
        /// A face that can do neither is omitted
        /// (Q9). Empty for a non-network piece. The whole-building handle set is the union of this over every
        /// network member (expansion is not focus-scoped — Q3), assembled by the draw/input layer.
        /// </summary>
        public static IReadOnlyList<ExpansionHandle> HandlesFor(
            IMapModel map, Player executor, ExpandableXRegistry registry, BuildingModel building)
        {
            var handles = new List<ExpansionHandle>();
            if (!TryResolveNetworkPiece(registry, building, out _, out var worldFaces))
            {
                return handles;
            }

            // The shrink acts on the leading (outward) face of a single-join end: the join's opposite. Null
            // when the piece isn't a removable end, so no face gets an inward (shrink) direction.
            var joinFaces = worldFaces.Where(f => f.Value == SlotRole.Join).Select(f => f.Key).ToList();
            TileDirection? shrinkFace = joinFaces.Count == 1
                && ShrinkOptionFor(map, executor, registry, building) is { Available: true }
                    ? joinFaces[0].Opposite
                    : null;

            // Grow liveness per outer face (every role-carrying, non-Join face shows an option; Available is
            // its single-tile reachability).
            var growable = GrowOptionsFor(map, executor, registry, building)
                .ToDictionary(option => option.Face, option => option.Available);

            var faces = new HashSet<TileDirection>(growable.Keys);
            if (shrinkFace is { } face)
            {
                faces.Add(face);
            }

            foreach (TileDirection direction in faces)
            {
                // Grow liveness is multi-tile: a single-tile grow can be blocked (e.g. it would gap-fill into
                // a same-network piece one tile further along, fusing away the pinched connector) while a
                // longer drag — through that piece, or ending on it — is valid (#5). So when the cheap
                // single-tile check says no, probe the chain a bounded distance before hiding the handle. The
                // && short-circuits for a shrink-only face (not in growable), so the probe runs only for a
                // grow-candidate face that the single-tile path rejected.
                bool canGrow = growable.TryGetValue(direction, out bool available)
                    && (available
                        || GrowChainFor(map, executor, registry, building, direction, GrowLivenessProbeTiles, buildAction: false).Tiles > 0);
                bool canShrink = shrinkFace == direction;
                if (canGrow || canShrink)
                {
                    handles.Add(new ExpansionHandle(building.Id, building.Transform.Position, direction, canGrow, canShrink));
                }
            }

            return handles;
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
                    building.Transform,
                    new GlobalTileTransform(building.Transform.Position, sourceRotation),
                    building.Configuration, building.Definition, sourceDefinition),
                ..fusedNeighbours.Select(fusedNeighbour => new ExpandableXSwapVariantAction(
                    map, executor, fusedNeighbour.Building.Id,
                    fusedNeighbour.Building.Transform,
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
