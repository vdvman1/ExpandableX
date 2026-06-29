extern alias monomod;
using System;
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
    /// Network layouts grow/shrink a multi-tile chain; static sequence layouts (cutter etc.) step one stop
    /// per gesture (multi-step-per-drag is a follow-up). Other follow-ups: a true ghost-piece preview
    /// (currently placeholder caps along the target tiles, network only); and a cell-based hit-test (cursor
    /// tile → adjacent faces) instead of enumerating the whole network each frame. Fails open throughout.
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

            // Track hold time and whether this gesture ever became a real drag, so a release can tell a quick
            // click apart from a drag (returned to zero or not) and from a slow hold.
            _heldTime += options.DeltaTime;
            _dragged |= _magnitude > 0;

            if (context.IsActive(PressAction))
            {
                // The placeholder preview draws one cap per grown tile, which only matches the network chain;
                // a static sequence step isn't a per-tile extent, so skip it there (no preview until the real
                // ghost renderer lands).
                if (_magnitude > 0 && IsDynamic(building))
                {
                    DrawPreview(options, building);
                }

                return;
            }

            // Released. A real drag commits its magnitude. A quick click — pressed and released on the handle
            // under ClickHoldThreshold without ever dragging out a tile — grows or shrinks by one in the
            // direction of the cap the cursor was on (TryHitTest's grow flag), so a single tile needs no drag.
            // A drag returned to zero, or a slow hold that never dragged, commits nothing.
            if (_magnitude == 0 && !_dragged && _heldTime < ClickHoldThreshold)
            {
                _grow = _clickGrow;
                _magnitude = 1;
            }

            if (_magnitude > 0)
            {
                Commit(map, player, actions, building);
            }

            _dragging = false;
        }

        /// <summary>Cursor distance past the face edge along the face axis, in tiles (rounded; + outward, - inward).</summary>
        private int SignedTiles(BuildingModel building, float3 cursor)
        {
            float3 center = (float3)building.Transform.Position.ToCenter_W();
            float3 axis = (float3)building.Transform.Position.Move(_handle.Face).ToCenter_W() - center;

            float tileLength = math.length(axis);
            if (tileLength < 1e-3f)
            {
                return 0;
            }

            float3 axisUnit = axis / tileLength;
            float3 faceEdge = center + 0.5f * axis; // boundary between this tile and the neighbour
            float tiles = math.dot(cursor - faceEdge, axisUnit) / tileLength;
            return math.clamp((int)math.round(tiles), -MaxDragTiles, MaxDragTiles);
        }

        private void Commit(IMapModel map, Player player, PlayerActionManager actions, BuildingModel building)
        {
            // Static sequence layouts (cutter etc.) step the sequence one stop per gesture; Dynamic network
            // layouts grow/shrink a multi-tile chain.
            if (_registry.VariantsByDefId.TryGetValue(building.Definition.Id.Name, out VariantPlacement placement)
                && placement.Set.Layout is Layout.Static)
            {
                CommitSequenceStep(map, player, actions, building, placement.Set);
                return;
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
        /// Step a static sequence (cutter etc.) one stop in the gesture's direction: find the matching
        /// expand/shrink option for the grabbed face and swap the building to that step's target layout base
        /// definition at the same orientation, as one undoable action — the same move the old sequence HUD
        /// buttons made. One step per gesture for now; mapping a drag's magnitude to several sequence steps
        /// is a follow-up.
        /// </summary>
        private void CommitSequenceStep(IMapModel map, Player player, PlayerActionManager actions, BuildingModel building, PieceVariantSet set)
        {
            ExpansionKind kind = _grow ? ExpansionKind.Expand : ExpansionKind.Shrink;
            foreach (ExpansionOption option in SequenceEngine.OptionsFor(set.Registration, set.Layout))
            {
                if (option.Kind != kind
                    || !option.Available
                    || option.Direction.Rotate(building.Transform.Rotation) != _handle.Face
                    || option.TargetLayout is not Layout.Static target
                    || !TryResolveDefinition(target.Piece.BaseDefinitionId, out IBuildingDefinition? targetDefinition))
                {
                    continue;
                }

                // A sequence swaps to a different building definition at the same orientation (id-as-truth).
                var swap = new ExpandableXSwapVariantAction(
                    map, player, building.Id,
                    building.Transform,
                    new GlobalTileTransform(building.Transform.Position, building.Transform.Rotation),
                    building.Configuration, building.Definition, targetDefinition);
                actions.TryScheduleAction(swap);
                return;
            }
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
        /// Placeholder preview: a faint cap at each target tile so the drag extent is visible. (Follow-up:
        /// render the actual would-be piece ghosts via the blueprint renderer.) Shows the raw drag magnitude;
        /// the commit clamps to what's reachable.
        /// </summary>
        private void DrawPreview(FrameDrawOptions options, BuildingModel building)
        {
            if (_registry.SessionTheme is not { } theme)
            {
                return;
            }

            VisualThemeBaseResources resources = theme.BaseResources;
            LODMeshAsset[] caps = _grow ? resources.BeltCapOutput : resources.BeltCapInput;
            MaterialReference material = resources.UXBuildingBlueprintSpotIndicatorMaterial;

            // Grow previews the new outward tiles; shrink previews the end pieces it would peel back (inward).
            TileDirection direction = _grow ? _handle.Face : _handle.Face.Opposite;
            GlobalTileCoordinate tile = _grow
                ? building.Transform.Position
                : building.Transform.Position.Move(direction, -1);

            for (int k = 0; k < _magnitude; k++)
            {
                tile = tile.Move(direction);
                WorldCoordinate center = tile.ToCenter_W() + ExpansionHandleGeometry.LiftHeight * WorldVector.Up;
                ExpansionHandleGeometry.DrawCap(options, caps, material, center, _handle.Face, alpha: 0.5f);
            }
        }

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
