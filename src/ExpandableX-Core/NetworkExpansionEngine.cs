using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using Game.Core.Coordinates;

namespace ExpandableX.Core
{
    /// <summary>One growable face of a placed network piece: grow outward (place a joined neighbour) in this direction.</summary>
    public sealed record GrowOption(TileDirection Face, bool Available, string? BlockedReason, IPlayerAction? Action);

    /// <summary>Removing this end piece, folding its leading connector back onto its neighbour (reverse pinch-and-stretch).</summary>
    public sealed record ShrinkOption(bool Available, string? BlockedReason, IPlayerAction? Action);

    /// <summary>
    /// Computes directed grow/shrink moves for a placed <see cref="Layout.Dynamic"/> piece and builds the
    /// undoable actions that perform them (sibling to <see cref="SequenceEngine"/>, for network layouts).
    /// Grow/shrink are id-as-truth definition swaps plus a placement/removal; the network re-forms from
    /// geometry via <see cref="ExpandableSimulationSystem"/>, so nothing is linked or stored (ADR-0012).
    ///
    /// v1 (lean) scope and its known seams:
    /// <list type="bullet">
    /// <item>Grow only into an <b>empty</b> neighbour tile; the new piece joins <i>only</i> the source (its
    /// other faces are <see cref="SlotRole.Disabled"/> spacers, never join faces), so nothing fuses here —
    /// and a grow can never merge two <b>separate</b> networks. <b>Future fusion:</b> when a grow lands a
    /// piece beside another piece of the <i>same</i> network, the connectors trapped between them are
    /// inaccessible and should be fused away — which then <b>requires re-checking the network predicates</b>,
    /// since dropping those slots changes the building's I/O. (Edge case to prototype: if the trapped faces
    /// are an output meeting an input, the building would feed itself — possibly unwanted, possibly a
    /// feature.) In this lean version such trapped connectors are just left in place (harmless; the player
    /// can disable them), and free-branch grows with deliberate side joins are not generated.</item>
    /// <item>Grow carries the leading face's gameplay role out to the new end ("pinch and stretch"); the
    /// new piece's side faces default to a <see cref="SlotRole.Disabled"/> spacer. Author-chosen grow
    /// defaults are future work.</item>
    /// <item>Grow does <b>not</b> re-check network predicates: pinch-and-stretch only relocates the
    /// leading role and adds a spacer, so role counts are invariant and the framework's
    /// <c>AtLeastN</c>-style predicates stay satisfied. A <i>Custom</i> author predicate has no such
    /// guarantee — re-validating a grow's result is future work.</item>
    /// <item>Shrink is offered on a removable end (exactly one join) and folds that end's leading role
    /// back onto its neighbour; only that axis role carries (a role manually set on the end's side face
    /// is dropped).</item>
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

            GridRotation rotation = building.Transform.Rotation;
            foreach (TileDirection face in PlanarFaces)
            {
                // Only a face that currently carries a gameplay/disabled role can grow; a Join already has
                // a same-building neighbour there.
                if (!worldFaces.TryGetValue(face, out SlotRole currentRole) || currentRole == SlotRole.Join)
                {
                    continue;
                }

                options.Add(BuildGrowOption(map, executor, registry, set, building, worldFaces, rotation, face, currentRole));
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

            if (!NetworkPieceRealization.TryRealize(neighbourPlacement.Set, neighbourWorld, neighbourRotation, out string neighbourDef, out GridRotation neighbourNewRotation)
                || !TryResolveDefinition(registry, neighbourDef, out var neighbourDefinition))
            {
                return new ShrinkOption(false, "no variant for the folded neighbour", null);
            }

            var remove = new ExpandableXRemoveBuildingAction(
                map, executor, building.Id, building.Definition, building.Transform, building.Configuration);
            var swapNeighbour = new ExpandableXSwapVariantAction(
                map, executor, neighbour.Id,
                new GlobalTileTransform(neighbour.Transform.Position, neighbourNewRotation),
                neighbour.Configuration, neighbour.Definition, neighbourDefinition);

            return new ShrinkOption(true, null, new CombinedUndoablePlayerAction(remove, swapNeighbour));
        }

        private static GrowOption BuildGrowOption(
            IMapModel map, Player executor, ExpandableXRegistry registry, PieceVariantSet set,
            BuildingModel building, IReadOnlyDictionary<TileDirection, SlotRole> worldFaces,
            GridRotation rotation, TileDirection face, SlotRole carriedRole)
        {
            GlobalTileCoordinate neighbourPos = building.Transform.Position.Move(face);
            if (map.TryGetBuilding(neighbourPos, out _))
            {
                return new GrowOption(face, false, "tile occupied", null);
            }

            // No fusion guard needed: the new piece joins only the source (its other faces are Disabled
            // spacers, not joins), so placing it next to a separate network can't merge them — fusion
            // requires matching join faces on both sides. Incidental fusion would only arise from a
            // free-branch grow (side joins), which is future work.

            // Source: the grown face becomes a Join (it now abuts the new piece).
            var sourceWorld = new Dictionary<TileDirection, SlotRole>(worldFaces) { [face] = SlotRole.Join };
            if (!NetworkPieceRealization.TryRealize(set, sourceWorld, rotation, out string sourceDef, out GridRotation sourceRotation)
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
            if (!NetworkPieceRealization.TryRealize(set, pieceWorld, GridRotation.NoRotate, out string pieceDef, out GridRotation pieceRotation)
                || !TryResolveDefinition(registry, pieceDef, out var pieceDefinition))
            {
                return new GrowOption(face, false, "no variant for the new piece", null);
            }

            var swapSource = new ExpandableXSwapVariantAction(
                map, executor, building.Id,
                new GlobalTileTransform(building.Transform.Position, sourceRotation),
                building.Configuration, building.Definition, sourceDefinition);
            var placePiece = new ExpandableXPlaceBuildingAction(
                map, executor, pieceDefinition, new GlobalTileTransform(neighbourPos, pieceRotation), configuration: null);

            return new GrowOption(face, true, null, new CombinedUndoablePlayerAction(swapSource, placePiece));
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
