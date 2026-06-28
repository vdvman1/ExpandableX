extern alias monomod;
using System;
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
    /// clamped + predicate-checked); Esc / right-click cancels.
    ///
    /// First cut, network layouts only. Follow-ups: static-sequence drag (cutter steps); a true ghost-piece
    /// preview (currently placeholder caps along the target tiles); and a cell-based hit-test (cursor tile →
    /// adjacent faces) instead of enumerating the whole network each frame. Fails open throughout.
    /// </summary>
    internal sealed class ExpandableXExpansionDragHook : IDisposable
    {
        // TUNABLE: sanity cap on tiles per single drag.
        private const int MaxDragTiles = 64;

        private const string PressAction = "mass-selection.select-base";
        private const string CancelAction = "global.cancel";

        // The mouse-drag camera-pan input; consumed while dragging so a handle drag doesn't also pan.
        private const string CameraMouseDragModifier = "camera.mouse-drag-modifier";

        private readonly ExpandableXRegistry _registry;
        private readonly ILogger _logger;
        private readonly monomod::MonoMod.RuntimeDetour.Hook _hudHook;
        private readonly monomod::MonoMod.RuntimeDetour.Hook _cameraHook;

        // Transient drag state. The grabbed face-handle carries its CanGrow/CanShrink; direction + magnitude
        // are recomputed from the cursor each frame.
        private bool _dragging;
        private ExpansionHandle _handle;
        private bool _grow;
        private int _magnitude;

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
        }

        public void Dispose()
        {
            _hudHook?.Dispose();
            _cameraHook?.Dispose();
        }

        private void Update(InputDownstreamContext context, FrameDrawOptions options)
        {
            try
            {
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
                    UpdateDrag(context, options, map, player, actions, cursor);
                    return;
                }

                // Idle: claim the press only when it lands on a handle (the hit-test gates the consuming read,
                // so a normal click elsewhere is untouched).
                if (SelectedBuilding(player) is not { } selected)
                {
                    return;
                }

                if (ExpansionHandleGeometry.TryHitTest(map, player, _registry, selected, cursor, out ExpansionHandle hit)
                    && context.ConsumeWasActivated(PressAction))
                {
                    _dragging = true;
                    _handle = hit;
                    _grow = true;
                    _magnitude = 0;
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

            // The grabbed building can vanish mid-drag (e.g. undo); abort if so.
            if (!map.TryGetBuilding(_handle.Position, out BuildingModel building))
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

            if (context.IsActive(PressAction))
            {
                if (_magnitude > 0)
                {
                    DrawPreview(options, building);
                }

                return;
            }

            // Released: realise the drag as one undoable action (the chain methods clamp + predicate-check).
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
