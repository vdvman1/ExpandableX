using Game.Core.Coordinates;
using Unity.Mathematics;

namespace ExpandableX.Core
{
    /// <summary>
    /// Shared world-space geometry for the drag handles (issue #5 / ADR-0014): where each handle cap sits,
    /// how to draw it, and how to hit-test the cursor against it. One source of truth for the draw hook
    /// (<see cref="ExpandableXExpansionHandleHook"/>), the input/drag hook's hit-testing, and the drag
    /// preview — so a tweak to placement/visuals moves all three together.
    ///
    /// All offsets are in <b>tile</b> units derived from real centre-to-centre steps, so they're independent
    /// of the tile↔world scale (a tile spans ~20 world units while <c>WorldVector.ByDirection</c> is a
    /// 1-unit vector). The constants are <c>TUNABLE</c> — they need in-game iteration.
    /// </summary>
    internal static class ExpansionHandleGeometry
    {
        // TUNABLE — drawing. RenderDistance is how far out the cap is drawn along the face. The game's mesh
        // rendering applies an external ~2× scaling we don't control: a value of 1.0 lands the cap right on
        // the face, so ~1.1 sits it just past the face, in the adjacent cell. Hit-testing does NOT go through
        // that scaling (the cursor is in true world space), so it uses its own distances below — see
        // HitFarDistance, which must be kept in step with this. LiftHeight is the cap's lift off the ground
        // (world units), HandleSeparation the both-directions sideways nudge (tiles), Alpha the draw alpha,
        // and CapHeightIndex the belt-cap LOD.
        public const float RenderDistance = 1.1f;
        public const float LiftHeight = 0.05f;
        public const float HandleSeparation = 0.2f;
        public const float Alpha = 0.9f;
        public const int CapHeightIndex = 0;

        // TUNABLE — hit-testing, in true-world tiles out from the source centre (independent of the render
        // scaling above). A cap drawn at RenderDistance actually appears at about RenderDistance × 0.5 in
        // world space, so HitFarDistance ≈ RenderDistance / 2 puts the hit zone on the *visible* cap; keep the
        // two in that ratio when tuning. Also keep HitFarDistance + HitRadiusTiles ≲ 1.0 so the zone stays on
        // the source side of the neighbour-tile centre: past the centre, two perpendicular handles in a
        // concave corner each overshoot toward the *opposite* side of the shared pocket and become grabbable
        // from the far end of the cell (issue #5). The zone is the segment from HitNearDistance out to
        // HitFarDistance (not the far point alone), so perpendicular corner handles are disambiguated by which
        // face the cursor is nearest; HitRadiusTiles is the capsule radius around that segment.
        public const float HitFarDistance = 0.55f;
        public const float HitNearDistance = 0.4f;
        public const float HitRadiusTiles = 0.25f;

        /// <summary>World-space centre of the cap on <paramref name="face"/> of the tile at <paramref name="position"/>, nudged sideways by <paramref name="lateralTiles"/> (perpendicular to the face).</summary>
        public static WorldCoordinate CapCenter(GlobalTileCoordinate position, TileDirection face, float lateralTiles) =>
            Anchor(position, face, lateralTiles, RenderDistance);

        /// <summary>A point <paramref name="distanceTiles"/> out from <paramref name="position"/>'s centre along <paramref name="face"/> (1.0 = the neighbour tile centre), nudged sideways by <paramref name="lateralTiles"/> and lifted off the ground.</summary>
        public static WorldCoordinate Anchor(GlobalTileCoordinate position, TileDirection face, float lateralTiles, float distanceTiles)
        {
            WorldCoordinate selfCenter = position.ToCenter_W();
            WorldVector tileStep = position.Move(face).ToCenter_W() - selfCenter;
            WorldCoordinate center = selfCenter + distanceTiles * tileStep + LiftHeight * WorldVector.Up;

            if (lateralTiles != 0f)
            {
                TileDirection sideways = face.Rotate(GridRotation.RotateCW);
                WorldVector lateralStep = position.Move(sideways).ToCenter_W() - selfCenter;
                center += lateralTiles * lateralStep;
            }

            return center;
        }

        /// <summary>The sideways nudge for a face: ±<see cref="HandleSeparation"/> when it offers both directions (so the two caps don't overlap), else 0.</summary>
        public static float LateralFor(in ExpansionHandle handle) =>
            handle.CanGrow && handle.CanShrink ? HandleSeparation : 0f;

        /// <summary>One tile's world length along <paramref name="face"/> (for hit radius and drag-magnitude projection).</summary>
        public static float TileWorldLength(GlobalTileCoordinate position, TileDirection face) =>
            math.distance((float3)position.Move(face).ToCenter_W(), (float3)position.ToCenter_W());

        /// <summary>Draw one belt-cap at <paramref name="center"/>, oriented along <paramref name="face"/>, via the game's instanced UI mesh renderer.</summary>
        public static void DrawCap(
            FrameDrawOptions options, LODMeshAsset[] caps, MaterialReference material,
            WorldCoordinate center, TileDirection face, float alpha = Alpha)
        {
            if (caps is null || caps.Length <= CapHeightIndex
                || !caps[CapHeightIndex].TryGet(options.LOD.BuildingLOD, out IMeshReference mesh))
            {
                return;
            }

            GridRotation rotation = face.GlobalRotationTo().ZRotation;
            options.Renderers.RegularNonInstanced.DrawMesh(
                mesh, material,
                FastMatrix.TranslateRotate(in center, rotation),
                RenderCategory.AnalogUI,
                MaterialPropertyHelpers.CreateAlphaBlock(alpha));
        }

        /// <summary>
        /// The face-handle closest to <paramref name="cursorWorld"/> within the hit radius, for the selected
        /// building (its whole network). There is one handle per face — grow vs shrink is decided by the
        /// drag direction, not by which of its (visual) caps the cursor is nearest — so a hit on either cap
        /// counts as grabbing that one handle. False when no handle is under the cursor.
        /// </summary>
        public static bool TryHitTest(
            IMapModel map, Player executor, ExpandableXRegistry registry, BuildingModel selected,
            float3 cursorWorld, out ExpansionHandle hit)
        {
            hit = default;
            float bestDistanceSq = float.MaxValue;
            bool found = false;

            foreach (ExpansionHandle handle in ExpansionHandles.For(map, executor, registry, selected))
            {
                float lateral = LateralFor(handle);
                float radius = HitRadiusTiles * TileWorldLength(handle.Position, handle.Face);
                float radiusSq = radius * radius;

                // Each direction's hit zone is the segment from the face edge out to its cap; either segment
                // of a both-directions face grabs the single handle. Measuring along the segment (not just the
                // far cap point) is what disambiguates a concave corner — see HitNearDistance.
                bool near =
                    (handle.CanGrow && CloserToHandle(handle, lateral, cursorWorld, radiusSq, ref bestDistanceSq))
                    | (handle.CanShrink && CloserToHandle(handle, -lateral, cursorWorld, radiusSq, ref bestDistanceSq));

                if (near)
                {
                    hit = handle;
                    found = true;
                }
            }

            return found;
        }

        /// <summary>True (updating <paramref name="bestDistanceSq"/>) when the cursor is the new closest hit along this handle's edge→cap segment and within the radius.</summary>
        private static bool CloserToHandle(in ExpansionHandle handle, float lateral, float3 cursor, float radiusSq, ref float bestDistanceSq)
        {
            float3 edge = (float3)Anchor(handle.Position, handle.Face, lateral, HitNearDistance);
            float3 cap = (float3)Anchor(handle.Position, handle.Face, lateral, HitFarDistance);
            float distanceSq = SegmentDistanceSq(cursor, edge, cap);
            if (distanceSq > radiusSq || distanceSq >= bestDistanceSq)
            {
                return false;
            }

            bestDistanceSq = distanceSq;
            return true;
        }

        /// <summary>Squared distance from <paramref name="p"/> to the segment [<paramref name="a"/>, <paramref name="b"/>].</summary>
        private static float SegmentDistanceSq(float3 p, float3 a, float3 b)
        {
            float3 ab = b - a;
            float lengthSq = math.lengthsq(ab);
            float t = lengthSq < 1e-6f ? 0f : math.saturate(math.dot(p - a, ab) / lengthSq);
            return math.distancesq(p, a + t * ab);
        }
    }
}
