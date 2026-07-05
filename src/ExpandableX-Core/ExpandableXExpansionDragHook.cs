extern alias monomod;
using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using Game.Core.Coordinates;
using ShapezShifter.SharpDetour;
using Unity.Mathematics;
using ILogger = Core.Logging.ILogger;

namespace ExpandableX.Core
{
    /// <summary>
    /// The drag-gesture half of the drag-handle control surface (issue #5 / ADR-0014, slice 3c): grabbing a
    /// face's handle and dragging it grows/shrinks the building.
    ///
    /// <b>Seam.</b> A <b>prefix</b> on <see cref="HUD.OnGameUpdate"/> — the HUD dispatcher that runs each
    /// frame and calls every part's <c>OnGameUpdate(context, drawOptions)</c> in turn. We must hook here
    /// rather than the mass-selection part's own <c>OnGameUpdate</c>, which lives on the <i>generic</i>
    /// <c>HUDMassSelectionBase&lt;,&gt;</c> and so can't be detoured (MonoMod rejects generic methods). Running
    /// before the parts on the <i>same</i> <see cref="InputDownstreamContext"/> lets us <b>claim</b> the press
    /// — consuming <c>mass-selection.select-base</c> when it lands on a handle, so the selection part (later
    /// in the loop) never sees it (no deselect / area-select), while still drawing the selection normally.
    ///
    /// <b>Gesture.</b> A face has one handle; the <i>signed</i> projection of the cursor onto the face axis
    /// gives both direction and magnitude — drag out grows, drag in (past the building) shrinks, and crossing
    /// zero flips between them mid-drag — each gated by what the face allows (<see cref="ExpansionHandle.CanGrow"/>
    /// / <see cref="ExpansionHandle.CanShrink"/>). Release commits via the chain methods (one undoable action,
    /// clamped + predicate-checked); Esc / right-click cancels. A quick click (held under a short threshold,
    /// no drag) changes by a single tile in the direction of the cap clicked — grow or shrink — so a one-tile
    /// change needs no drag; a longer hold that never drags commits nothing.
    ///
    /// Network layouts grow/shrink a multi-tile chain, previewed as the game's blueprint ghosts of the
    /// clamped result (new pieces blue, connector→join changes amber, removals red); static sequence layouts
    /// (cutter etc.) footprint-track (#16) — the drag can cross several stops in one gesture, the building's
    /// far edge following the cursor, previewed as the target step's ghost. Other follow-ups: a cell-based
    /// hit-test (cursor tile → adjacent faces) instead of enumerating the whole network each frame.
    /// Fails open throughout.
    /// </summary>
    internal sealed class ExpandableXExpansionDragHook : IDisposable
    {
        // TUNABLE: sanity cap on tiles per single drag.
        private const int MaxDragTiles = 64;

        // TUNABLE: longest a press may be held (seconds) and still count as a click (grow/shrink one tile) on
        // release. The game's input exposes no click-vs-hold distinction, so we time it ourselves by summing
        // FrameDrawOptions.DeltaTime while held; a longer hold that never drags commits nothing.
        private const float ClickHoldThreshold = 0.3f;

        private const string PressAction = "mass-selection.select-base";
        private const string CancelAction = "global.cancel";

        // The mouse-drag camera-pan input; consumed while dragging so a handle drag doesn't also pan.
        private const string CameraMouseDragModifier = "camera.mouse-drag-modifier";

        private readonly ExpandableXRegistry _registry;
        private readonly ILogger _logger;
        private readonly monomod::MonoMod.RuntimeDetour.Hook _hudHook;
        private readonly monomod::MonoMod.RuntimeDetour.Hook _cameraHook;
        private readonly monomod::MonoMod.RuntimeDetour.Hook _wireTooltipHook;

        // Transient drag state. The grabbed face-handle carries its CanGrow/CanShrink; direction + magnitude
        // are recomputed from the cursor each frame.
        private bool _dragging;
        private ExpansionHandle _handle;
        private bool _grow;
        private int _magnitude;

        // Whether the current gesture ever dragged out at least one tile — lets a release at zero magnitude
        // tell a plain click (grow one tile) from a drag taken back to zero (cancel).
        private bool _dragged;

        // Seconds the press has been held (summed FrameDrawOptions.DeltaTime), and which cap the press landed
        // on (true = grow, false = shrink). A release under ClickHoldThreshold that never dragged commits one
        // tile in this direction.
        private float _heldTime;
        private bool _clickGrow;

        // A throwaway non-null GameObject parked in InputDownstreamContext.UIHoverElement while the cursor is
        // over (or dragging) a handle, so the mass-selection HUD's "cursor over UI" guard suppresses its world
        // hover-highlight under the handle. Created lazily; destroyed on dispose.
        private UnityEngine.GameObject? _hoverSentinel;

        // True for the frame while the cursor is over (or dragging) a handle. Set in the HUD prefix (which
        // runs before the parts) and read by the wire-tooltip postfix — some hover-info parts find the
        // building under the cursor directly (ignoring UIHoverElement), so they need explicit suppression.
        private bool _cursorOverHandle;

        public ExpandableXExpansionDragHook(ExpandableXRegistry registry, ILogger logger)
        {
            _registry = registry;
            _logger = logger;

            _hudHook = DetourHelper.CreatePrefixHook(
                (HUD hud, InputDownstreamContext context, FrameDrawOptions options) => hud.OnGameUpdate(context, options),
                (hud, context, options) =>
                {
                    Update(context, options);
                    return (context, options);
                });

            // While a handle drag is active, swallow the mouse-drag pan input so the camera doesn't pan under
            // the cursor. A prefix runs right before the camera body, so the consume lands regardless of the
            // camera's position in the global update order (it isn't a HUD part, so the HUD hook can't cover it).
            _cameraHook = DetourHelper.CreatePrefixHook(
                (CameraController camera, InputDownstreamContext context, FrameDrawOptions options) => camera.OnGameUpdate(context, options),
                (camera, context, options) =>
                {
                    if (_dragging)
                    {
                        context.ConsumeIsActive(CameraMouseDragModifier);
                    }

                    return (context, options);
                });

            // The wire-state hover tooltip (HUDWireContentsHelper) finds the building under the cursor itself
            // and ignores UIHoverElement, so the UIHoverElement guard doesn't suppress it. Hide its tooltip
            // after it updates whenever the cursor is over one of our handles — the handle owns that position.
            _wireTooltipHook = DetourHelper.CreatePostfixHook(
                (HUDWireContentsHelper helper, InputDownstreamContext context, FrameDrawOptions options) => helper.OnGameUpdate(context, options),
                (helper, context, options) =>
                {
                    if (_cursorOverHandle)
                    {
                        helper.gameObject.SetActive(false);
                    }
                });
        }

        public void Dispose()
        {
            _hudHook?.Dispose();
            _cameraHook?.Dispose();
            _wireTooltipHook?.Dispose();
            if (_hoverSentinel != null)
            {
                UnityEngine.Object.Destroy(_hoverSentinel);
            }
        }

        private void Update(InputDownstreamContext context, FrameDrawOptions options)
        {
            try
            {
                // Reset each frame; SuppressWorldHover raises it while the cursor is over (or dragging) a handle.
                _cursorOverHandle = false;

                if (_registry.Map is not { } map
                    || _registry.LocalPlayer is not { } player
                    || _registry.PlayerActions is not { } actions
                    || _registry.Viewport is not { } viewport)
                {
                    _dragging = false;
                    return;
                }

                // Handles only exist in the buildings-idle state; ignore every other HUD context.
                if (player.InteractionState.State != PlayerInteractionState.BuildingsIdle)
                {
                    _dragging = false;
                    return;
                }

                if (!TryCursorWorld(viewport, out float3 cursor))
                {
                    return;
                }

                if (_dragging)
                {
                    // While dragging a handle the cursor isn't hovering buildings — suppress the world hover.
                    SuppressWorldHover(context);
                    UpdateDrag(context, options, map, player, actions, cursor);
                    return;
                }

                // Idle: a hit means the cursor is over a handle. Suppress the world hover-highlight under it,
                // then claim the press when it lands (the hit-test gates the consuming read, so a normal click
                // elsewhere is untouched).
                if (SelectedBuilding(player) is not { } selected
                    || !ExpansionHandleGeometry.TryHitTest(map, player, _registry, selected, cursor, out ExpansionHandle hit, out _clickGrow))
                {
                    return;
                }

                SuppressWorldHover(context);

                if (context.ConsumeWasActivated(PressAction))
                {
                    _dragging = true;
                    _handle = hit;
                    _grow = true;
                    _magnitude = 0;
                    _dragged = false;
                    _heldTime = 0f;
                }
            }
            catch (Exception e)
            {
                _dragging = false;
                _logger.Info.Log($"ExpandableX-Core: drag-handle input failed, aborting the drag: {e}");
            }
        }

        private void UpdateDrag(
            InputDownstreamContext context, FrameDrawOptions options,
            IMapModel map, Player player, PlayerActionManager actions, float3 cursor)
        {
            // Esc / right-click cancels, committing nothing (consume so the base doesn't also clear the selection).
            if (context.ConsumeWasActivated(CancelAction))
            {
                _dragging = false;
                return;
            }

            // The grabbed building can vanish mid-drag (e.g. undo); abort if so. Resolve by id, not by the
            // handle's tile — a static handle is anchored on a footprint edge tile, not the building origin.
            if (!map.TryGetBuilding(_handle.Piece, out BuildingModel building))
            {
                _dragging = false;
                return;
            }

            // Signed projection sets direction + magnitude: + = outward (grow), - = inward (shrink), gated by
            // what the face allows. Crossing zero flips the direction mid-drag.
            int signed = SignedTiles(building, cursor);
            if (signed > 0 && _handle.CanGrow)
            {
                _grow = true;
                _magnitude = signed;
            }
            else if (signed < 0 && _handle.CanShrink)
            {
                _grow = false;
                _magnitude = -signed;
            }
            else
            {
                _magnitude = 0;
            }

            // Track hold time and whether this gesture became a real drag, so a release can tell a quick click
            // apart from a drag (out-and-back or not) and from a slow hold. A network drag counts once it moves
            // a tile; a static drag counts once the cursor crosses into a different step's tile (which the
            // round-based magnitude can lag, so it's read from the footprint-tracking target directly).
            _heldTime += options.DeltaTime;
            if (IsDynamic(building))
            {
                _dragged |= _magnitude > 0;
            }
            else if (TryStaticSet(building, out PieceVariantSet staticSet))
            {
                _dragged |= SequenceTargetForDrag(building, staticSet, cursor) is not null;
            }

            if (context.IsActive(PressAction))
            {
                // Preview the would-be change while held, as the game's blueprint ghosts of the clamped result.
                if (IsDynamic(building))
                {
                    // Network: the whole chain — new pieces blue, changed (connector→join) pieces amber,
                    // removed pieces red.
                    if (_magnitude > 0)
                    {
                        var ghosts = _grow
                            ? NetworkExpansionEngine.GrowChainFor(map, player, _registry, building, _handle.Face, _magnitude, buildAction: false).Ghosts
                            : NetworkExpansionEngine.ShrinkChainFor(map, player, _registry, building, _magnitude, buildAction: false).Ghosts;
                        DrawGhosts(options, ghosts);
                    }
                }
                else if (TryStaticSet(building, out PieceVariantSet set))
                {
                    // Static sequence (#16): preview the footprint-tracked target step's ghost at the same
                    // transform — a no-op (cursor still on the current step) draws nothing.
                    DrawSequenceStepPreview(options, building, set, cursor);
                }

                return;
            }

            // Released. A real drag commits whatever the footprint-tracking / chain result was (an out-and-back
            // drag lands on the current step / zero magnitude and commits nothing). A quick click — pressed and
            // released on the handle under ClickHoldThreshold without ever dragging — grows or shrinks by one
            // step (static) or one tile (network) in the direction of the cap the cursor was on. A slow hold
            // that never dragged commits nothing.
            if (_dragged)
            {
                Commit(map, player, actions, building, cursor, oneStep: false);
            }
            else if (_heldTime < ClickHoldThreshold)
            {
                _grow = _clickGrow;
                _magnitude = 1;
                Commit(map, player, actions, building, cursor, oneStep: true);
            }

            _dragging = false;
        }

        /// <summary>
        /// Cursor distance past the building's current FAR edge along the face axis, in tiles (rounded;
        /// + outward = grow, - inward = shrink). Measured from the footprint's far edge (its depth along the
        /// face), not the origin tile, so a multi-tile building (a cutter) measures the drag from where its
        /// edge actually is; for a 1x1 network piece the far edge is the origin edge, so this is unchanged there.
        /// </summary>
        private int SignedTiles(BuildingModel building, float3 cursor)
        {
            float past = CursorDepthAlongFace(building, cursor) - FootprintDepth(building.Definition, building);
            return math.clamp((int)math.round(past), -MaxDragTiles, MaxDragTiles);
        }

        private void Commit(IMapModel map, Player player, PlayerActionManager actions, BuildingModel building, float3 cursor, bool oneStep)
        {
            // Static sequence layouts (cutter etc.) footprint-track (#16); Dynamic network layouts grow/shrink
            // a multi-tile chain.
            if (TryStaticSet(building, out PieceVariantSet set))
            {
                CommitSequenceStep(map, player, actions, building, set, cursor, oneStep);
                return;
            }

            if (_magnitude <= 0)
            {
                return; // network drag returned to zero magnitude — nothing to commit
            }

            if (_grow)
            {
                GrowChainResult grown = NetworkExpansionEngine.GrowChainFor(map, player, _registry, building, _handle.Face, _magnitude);
                if (grown.Action is { } action)
                {
                    actions.TryScheduleAction(action);
                }

                return;
            }

            ShrinkChainResult shrunk = NetworkExpansionEngine.ShrinkChainFor(map, player, _registry, building, _magnitude);
            if (shrunk.Action is { } shrinkAction && actions.TryScheduleAction(shrinkAction))
            {
                // Keep configuring on the surviving end once the shrink settles (the removed end was the focus).
                _registry.NetworkSelection?.RequestFocusAfterChange(shrunk.FocusAfter);
            }
        }

        /// <summary>
        /// Commit a static sequence (cutter etc.) drag, footprint-tracking (#16): swap the building to the step
        /// the gesture maps onto at the same orientation, as one undoable action. A drag lands on the
        /// footprint-tracked step (possibly several stops in one gesture); a click (<paramref name="oneStep"/>)
        /// moves exactly one step in the clicked cap's direction. A no-op schedules nothing.
        /// </summary>
        private void CommitSequenceStep(
            IMapModel map, Player player, PlayerActionManager actions, BuildingModel building, PieceVariantSet set, float3 cursor, bool oneStep)
        {
            Layout? targetLayout = oneStep
                ? SequenceOneStep(building, set, _grow)
                : SequenceTargetForDrag(building, set, cursor);

            if (targetLayout is not Layout.Static target
                || !TryResolveDefinition(target.Piece.BaseDefinitionId, out IBuildingDefinition? targetDefinition))
            {
                return;
            }

            // A sequence swaps to a different building definition at the same orientation (id-as-truth).
            var swap = new ExpandableXSwapVariantAction(
                map, player, building.Id,
                building.Transform,
                new GlobalTileTransform(building.Transform.Position, building.Transform.Rotation),
                building.Configuration, building.Definition, targetDefinition);
            actions.TryScheduleAction(swap);
        }

        /// <summary>
        /// Footprint-tracking drag target (#16): the step the cursor's drag maps onto, or null for a no-op. The
        /// building's far edge follows the cursor — the target is the cell the cursor sits in (ceil of the
        /// cursor's depth), rounded to the nearest reachable step in the drag's direction. Growing rounds
        /// <b>up</b> to the first step whose footprint reaches the cursor's cell; shrinking rounds <b>down</b>
        /// to the first step at or within it. This stays consistent across gaps in the step depths — e.g. with
        /// steps at depth 2 and 4 (no 3), entering the 3rd cell grows to the depth-4 step (and, when shrinking,
        /// retreats to the depth-2 step) rather than sticking until the cursor crosses the whole gap. The
        /// direction is set by the cursor's position relative to the current far edge (not mouse motion), so it
        /// doesn't chatter. The grabbed face picks the sequence — forward grows, backward shrinks.
        /// </summary>
        private Layout? SequenceTargetForDrag(BuildingModel building, PieceVariantSet set, float3 cursor)
        {
            int currentDepth = FootprintDepth(building.Definition, building);
            // The tile the cursor sits in has its outer edge at ceil(depth) — that's where the far edge wants to be.
            int desired = math.clamp((int)math.ceil(CursorDepthAlongFace(building, cursor)), 1, currentDepth + MaxDragTiles);
            if (desired == currentDepth)
            {
                return null; // cursor still within the current step's tile — no change
            }

            ExpansionKind kind = desired > currentDepth ? ExpansionKind.Expand : ExpansionKind.Shrink;
            if (kind == ExpansionKind.Expand ? !_handle.CanGrow : !_handle.CanShrink)
            {
                return null;
            }

            if (MatchingSequence(building, set) is not { } sequence)
            {
                return null;
            }

            // Reachable steps are nearest-first (grow = increasing depth, shrink = decreasing). Grow picks the
            // first step whose depth reaches the cursor's cell (>= desired); shrink the first at/within it
            // (<= desired). If the cursor is past the end of the ladder, clamp to the furthest reachable step.
            Layout? furthest = null;
            foreach (Layout candidate in SequenceEngine.ReachableLadder(sequence, set.Layout, kind))
            {
                if (candidate is not Layout.Static staticStep
                    || !TryResolveDefinition(staticStep.Piece.BaseDefinitionId, out IBuildingDefinition? definition))
                {
                    continue;
                }

                furthest = candidate;
                int depth = FootprintDepth(definition, building);
                bool reached = kind == ExpansionKind.Expand ? depth >= desired : depth <= desired;
                if (reached)
                {
                    return candidate;
                }
            }

            return furthest;
        }

        /// <summary>The nearest reachable step one stop from the current layout in <paramref name="grow"/>'s
        /// direction along the grabbed face's sequence, or null — the target for a click (which moves one step).</summary>
        private Layout? SequenceOneStep(BuildingModel building, PieceVariantSet set, bool grow)
        {
            if ((grow ? !_handle.CanGrow : !_handle.CanShrink) || MatchingSequence(building, set) is not { } sequence)
            {
                return null;
            }

            IReadOnlyList<Layout> ladder = SequenceEngine.ReachableLadder(
                sequence, set.Layout, grow ? ExpansionKind.Expand : ExpansionKind.Shrink);
            return ladder.Count > 0 ? ladder[0] : null;
        }

        /// <summary>The registration's sequence whose local direction rotates to the grabbed face, if any.</summary>
        private Expansion.Sequence? MatchingSequence(BuildingModel building, PieceVariantSet set)
        {
            foreach (Expansion expansion in set.Registration.Expansions)
            {
                if (expansion is Expansion.Sequence sequence
                    && sequence.Direction.Rotate(building.Transform.Rotation) == _handle.Face)
                {
                    return sequence;
                }
            }

            return null;
        }

        /// <summary>
        /// How many tiles <paramref name="definition"/>'s footprint spans forward along the grabbed face axis
        /// when placed at <paramref name="building"/>'s transform — the far edge's depth from the fixed input
        /// edge at the origin tile (1 for a 1x1, 2 for a 2x1 extending one tile out, …). A static sequence
        /// keeps the origin tile fixed and grows forward (no re-anchoring), so this depth is what the cursor
        /// tracks. Assumes the origin tile is the anchored back edge — the same invariant the swap relies on.
        /// </summary>
        private int FootprintDepth(IBuildingDefinition definition, BuildingModel building)
        {
            GlobalTileCoordinate origin = building.Transform.Position;
            float3 originCenter = (float3)origin.ToCenter_W();
            float3 step = (float3)origin.Move(_handle.Face).ToCenter_W() - originCenter;
            float tileLength = math.length(step);
            if (tileLength < 1e-3f)
            {
                return 1;
            }

            float3 stepUnit = step / tileLength;
            float maxProjection = 0f;
#pragma warning disable CS0618
            TileVector[] tiles = definition.ConnectorData.Tiles;
#pragma warning restore CS0618
            foreach (TileVector local in tiles)
            {
                GlobalTileCoordinate tile = local.ToGlobal(building.Transform);
                float projection = math.dot((float3)tile.ToCenter_W() - originCenter, stepUnit) / tileLength;
                maxProjection = math.max(maxProjection, projection);
            }

            return (int)math.round(maxProjection) + 1;
        }

        /// <summary>
        /// The cursor's absolute depth along the grabbed face axis, in tiles from the footprint's fixed back
        /// edge (that back edge = depth 0; the origin tile spans depth 0→1, so its centre is 0.5). The far edge
        /// of a step at footprint depth <c>d</c> sits at depth <c>d</c>, so ceil(this) is the outer edge of the
        /// tile the cursor is in — where the far edge should follow to.
        /// </summary>
        private float CursorDepthAlongFace(BuildingModel building, float3 cursor)
        {
            float3 originCenter = (float3)building.Transform.Position.ToCenter_W();
            float3 axis = (float3)building.Transform.Position.Move(_handle.Face).ToCenter_W() - originCenter;
            float tileLength = math.length(axis);
            if (tileLength < 1e-3f)
            {
                return 0.5f;
            }

            float3 axisUnit = axis / tileLength;
            return math.dot(cursor - originCenter, axisUnit) / tileLength + 0.5f;
        }

        /// <summary>
        /// Preview a static-sequence drag as the footprint-tracked target step's blueprint ghost at the
        /// building's transform (#16). The whole building is replaced on commit, so it renders as a "valid
        /// replacement" (amber) — the building you'll get. A shrink shows the smaller result; per-tile removal
        /// highlighting isn't done here (the solid building still draws underneath).
        /// </summary>
        private void DrawSequenceStepPreview(FrameDrawOptions options, BuildingModel building, PieceVariantSet set, float3 cursor)
        {
            if (SequenceTargetForDrag(building, set, cursor) is not Layout.Static target
                || !TryResolveDefinition(target.Piece.BaseDefinitionId, out IBuildingDefinition? targetDefinition))
            {
                return;
            }

            DrawGhosts(options, new[]
            {
                new GhostPiece(
                    targetDefinition,
                    new GlobalTileTransform(building.Transform.Position, building.Transform.Rotation),
                    GhostKind.Changed),
            });
        }

        /// <summary>The building's registered static piece set, when it's a static-sequence layout.</summary>
        private bool TryStaticSet(BuildingModel building, out PieceVariantSet set)
        {
            if (_registry.VariantsByDefId.TryGetValue(building.Definition.Id.Name, out VariantPlacement placement)
                && placement.Set.Layout is Layout.Static)
            {
                set = placement.Set;
                return true;
            }

            set = null!;
            return false;
        }

        private bool TryResolveDefinition(string definitionId, [NotNullWhen(true)] out IBuildingDefinition? definition)
        {
            definition = null;
            GameMode mode = _registry.CurrentMode;
            if (mode is null)
            {
                return false;
            }

#pragma warning disable CS0618
            var id = new BuildingDefinitionId(definitionId);
#pragma warning restore CS0618
            return mode.Buildings._DefinitionsById.TryGetValue(id, out definition);
        }

        /// <summary>True when the building is a network (Dynamic) piece — used to gate the network-only drag preview.</summary>
        private bool IsDynamic(BuildingModel building) =>
            _registry.VariantsByDefId.TryGetValue(building.Definition.Id.Name, out VariantPlacement placement)
            && placement.Set.Layout is Layout.Dynamic;

        /// <summary>
        /// Render a chain's would-be pieces as the game's own blueprint ghosts, coloured by kind — the same
        /// pipeline the game uses for placement previews (<see cref="SmartBuildingBlueprintRenderer"/>). The
        /// chain methods clamp to what actually fits and validates, so the ghosts show exactly what a release
        /// will do — the preview respects the blocked extent for free.
        /// </summary>
        private static void DrawGhosts(FrameDrawOptions options, IReadOnlyList<GhostPiece> ghosts)
        {
            if (ghosts.Count == 0)
            {
                return;
            }

            SmartBuildingBlueprintRenderer.Draw(
                options,
                ghosts.Select(g => new SmartBuildingBlueprintRenderer.DrawData(g.Definition, g.Transform, GhostAllowability(g.Kind))).ToArray());
        }

        /// <summary>Maps a preview piece's kind to the game's placement colour: a new placement is blue, an existing piece whose variant changes (a "valid replacement", e.g. a connector becoming a join) is amber, a removal is red.</summary>
        private static PlacementAllowability GhostAllowability(GhostKind kind) => kind switch
        {
            GhostKind.Changed => PlacementAllowability.ValidPlacementButDisplaysWarning,
            GhostKind.Removed => PlacementAllowability.InvalidPlacement,
            _ => PlacementAllowability.ValidPlacement,
        };

        private static bool TryCursorWorld(Viewport viewport, out float3 cursor)
        {
            if (ScreenUtils.TryGetWorldCoordinate(viewport, viewport.CursorScreenPosition, out var worldCoordinate))
            {
                cursor = (float3)worldCoordinate;
                return true;
            }

            cursor = default;
            return false;
        }

        /// <summary>
        /// Mark the cursor as "over UI" for this frame by parking a sentinel in the input context's
        /// <see cref="InputDownstreamContext.UIHoverElement"/> — but only when nothing real is there — so the
        /// mass-selection HUD skips its world hover-highlight under the handle (its guard is
        /// <c>UIHoverElement == null</c>). A handle is an interactive overlay, so this is the same treatment a
        /// real UI element gets. Self-clearing: the context is rebuilt each frame, so leaving the handle drops it.
        /// </summary>
        private void SuppressWorldHover(InputDownstreamContext context)
        {
            _cursorOverHandle = true;
            if (context.UIHoverElement == null)
            {
                context.UIHoverElement = _hoverSentinel ??= new UnityEngine.GameObject("ExpandableX_HandleHoverSuppressor");
            }
        }

        /// <summary>The selected logical building to hit-test against: the network focus piece, else a single selection.</summary>
        private BuildingModel? SelectedBuilding(Player player)
        {
            if (_registry.NetworkSelection?.TryGetFocus(out BuildingModel focus) == true)
            {
                return focus;
            }

            var selection = player.InteractionState.BuildingSelection;
            return selection.Count == 1 ? selection.First() : null;
        }
    }
}
