extern alias monomod;
using System;
using System.Collections.Generic;
using System.Linq;
using Game.Core.Coordinates;
using ShapezShifter.SharpDetour;
using Unity.Mathematics;
using ILogger = Core.Logging.ILogger;

namespace ExpandableX.Core
{
    /// <summary>
    /// Draws the drag handles on the selected logical building's growable/shrinkable faces (issue #5 /
    /// ADR-0014) — the world-space half of the control surface that replaces the per-face HUD buttons.
    /// A postfix on <see cref="HUDBuildingMassSelection.Draw_ExistingSelection"/> (the same per-frame seam
    /// the focus highlight rides, see <see cref="ExpandableXFocusHighlightHook"/>) computes
    /// <see cref="ExpansionHandles.For"/> for the selected building and draws one indicator per live
    /// direction: an outward-facing belt-cap mesh for a grow face and an inward-facing one for a shrink
    /// face, via the game's own <c>RegularNonInstanced.DrawMesh</c> (the path
    /// <see cref="HUDBuildingMassSelection.Draw_HoverState"/> uses). Reusing the belt-cap meshes keeps this
    /// asset-free and copies nothing (cf. the model-IP note); the visuals are a deliberate placeholder.
    ///
    /// <b>This is the draw half only (3b).</b> Input/hit-testing (3c) is separate. Everything here fails
    /// open — any error skips the handles, never the selection draw.
    ///
    /// The visual parameters (offset out from the tile, lift off the ground, alpha, which belt-cap height
    /// mesh, and the materials) are <c>TUNABLE</c> constants — they need in-game iteration and are expected
    /// to change once seen in the running game.
    /// </summary>
    internal sealed class ExpandableXExpansionHandleHook : IDisposable
    {
        // Why we reimplement the hover fade instead of reusing the game's — it's structure, not access (the
        // publicizer would happily let us read the privates):
        //   - The game's hover lives in HUDMassSelectionBase as a private HoverAnimation struct + private
        //     list, and the alpha is computed inline inside DrawAndUpdateHoverAnimations right next to the
        //     building-mesh draw (Draw_HoverState); the 0.04/0.15 timings are inline literals, not fields, so
        //     there's no standalone "hover alpha" to call nor constant to read.
        //   - That class is a HUDPart (a Unity HUDComponent the game builds through its HUD DI), generic on a
        //     *selectable* with ~18 abstract selection members (Selection, area-select, delete, …). We can't
        //     instantiate or register a subclass for "hoverable handles", and its Draw_HoverState draws the
        //     selectable's building mesh, not our cap. Driving the live instance's machinery would likewise
        //     draw a building, not a handle.
        // So we copy the game's exact fade formula + constants and reuse only the hover *material*. (The game
        // itself inlines this same fade in several HUD classes rather than sharing it.)

        // TUNABLE: the hover glow fades in over HoverFadeInSeconds and out over HoverFadeOutSeconds after the
        // cursor leaves (matching the game's building-hover animation). It must draw at the cap's *exact*
        // position — scaling the cap mesh shifts it, because the mesh pivot is off-centre (the same offset the
        // render-vs-hit distance split works around). So to make the faint hover material read on a small cap,
        // we stack HoverPasses overlay draws at full size rather than enlarging it.
        private const float HoverFadeInSeconds = 0.04f;
        private const float HoverFadeOutSeconds = 0.15f;
        private const int HoverPasses = 3;

        private readonly ExpandableXRegistry _registry;
        private readonly ILogger _logger;
        private readonly monomod::MonoMod.RuntimeDetour.Hook _drawHook;

        // The handle the cursor is on (and which of its caps), plus the fade timing — one at a time, since the
        // cursor is over at most one. _hoverLastTime freezes when the cursor leaves, so the glow fades out.
        private ExpansionHandle? _hoverHandle;
        private bool _hoverGrow;
        private float _hoverInitialTime;
        private float _hoverLastTime;

        public ExpandableXExpansionHandleHook(ExpandableXRegistry registry, ILogger logger)
        {
            _registry = registry;
            _logger = logger;

            _drawHook = DetourHelper.CreatePostfixHook(
                (HUDBuildingMassSelection hud, FrameDrawOptions options, IReadOnlyCollection<BuildingModel> selection) =>
                    hud.Draw_ExistingSelection(options, selection),
                (hud, options, selection) => DrawHandles(options, selection));
        }

        public void Dispose() => _drawHook?.Dispose();

        private void DrawHandles(FrameDrawOptions options, IReadOnlyCollection<BuildingModel> selection)
        {
            try
            {
                // Handles target the selected logical building: the focus piece when a network is selected
                // (ExpansionHandles.For unions the whole network from any member), else a single non-network
                // selection. A genuine mass selection shows no handles.
                BuildingModel? selected = null;
                if (_registry.NetworkSelection?.TryGetFocus(out BuildingModel focus) == true)
                {
                    selected = focus;
                }
                else if (selection.Count == 1)
                {
                    selected = selection.First();
                }

                if (selected is null
                    || _registry.Map is not { } map
                    || _registry.LocalPlayer is not { } executor
                    || _registry.SessionTheme is not { } theme)
                {
                    _hoverHandle = null;
                    return;
                }

                // Which handle (and cap) is the cursor on? Reuses the input hit-test, so the glow matches what
                // a click would grab. Feeds the fade timing below.
                UpdateHover(map, executor, selected.Value);
                float hoverAlpha = HoverAlpha();

                VisualThemeBaseResources resources = theme.BaseResources;
                foreach (ExpansionHandle handle in ExpansionHandles.For(map, executor, _registry, selected.Value))
                {
                    // Outward-facing cap for grow, inward-facing for shrink. A both-directions face (a
                    // removable end's leading face) shows both, nudged apart sideways so they don't overlap.
                    float lateral = ExpansionHandleGeometry.LateralFor(handle);

                    if (handle.CanGrow)
                    {
                        ExpansionHandleGeometry.DrawCap(
                            options, resources.BeltCapOutput, resources.UXBuildingBlueprintSpotIndicatorMaterial,
                            ExpansionHandleGeometry.CapCenter(handle.Position, handle.Face, lateral), handle.Face);
                    }

                    if (handle.CanShrink)
                    {
                        ExpansionHandleGeometry.DrawCap(
                            options, resources.BeltCapInput, resources.UXBuildingBlueprintSpotIndicatorMaterial,
                            ExpansionHandleGeometry.CapCenter(handle.Position, handle.Face, -lateral), handle.Face);
                    }

                    // Hover glow: the cap the cursor is nearest, drawn over its normal cap with the game's
                    // hover material at the fade alpha — the same light-white fade every building hover uses.
                    if (hoverAlpha > 0f && _hoverHandle is { } hovered && SameHandle(hovered, handle))
                    {
                        // Same cap, same position as the normal draw (no scale/offset), the game's hover
                        // material stacked HoverPasses times so the faint white reads on a small cap.
                        LODMeshAsset[] caps = _hoverGrow ? resources.BeltCapOutput : resources.BeltCapInput;
                        WorldCoordinate glowCenter = ExpansionHandleGeometry.CapCenter(handle.Position, handle.Face, _hoverGrow ? lateral : -lateral);
                        for (int pass = 0; pass < HoverPasses; pass++)
                        {
                            ExpansionHandleGeometry.DrawCap(
                                options, caps, resources.UXBuildingHoverIndicatorMaterial, glowCenter, handle.Face, hoverAlpha);
                        }
                    }
                }
            }
            catch (Exception e)
            {
                _logger.Info.Log($"ExpandableX-Core: expansion-handle draw failed, leaving the plain selection: {e}");
            }
        }

        /// <summary>Update which handle/cap the cursor is over (via the input hit-test) and its fade timing. Leaves the last handle in place when the cursor is off a handle, so <see cref="HoverAlpha"/> fades it out.</summary>
        private void UpdateHover(IMapModel map, Player executor, BuildingModel selected)
        {
            if (_registry.Viewport is { } viewport
                && TryCursorWorld(viewport, out float3 cursor)
                && ExpansionHandleGeometry.TryHitTest(map, executor, _registry, selected, cursor, out ExpansionHandle current, out bool grow))
            {
                float now = UnityEngine.Time.realtimeSinceStartup;

                // Restart the fade-in only when the hovered handle itself changes — not when the cursor slides
                // between the two caps of the same handle.
                if (_hoverHandle is not { } prev || !SameHandle(prev, current))
                {
                    _hoverInitialTime = now;
                }

                _hoverHandle = current;
                _hoverGrow = grow;
                _hoverLastTime = now;
            }
        }

        /// <summary>The current hover-glow alpha (fade-in × fade-out), clearing the hovered handle once it has fully faded out.</summary>
        private float HoverAlpha()
        {
            if (_hoverHandle is null)
            {
                return 0f;
            }

            float now = UnityEngine.Time.realtimeSinceStartup;
            float fadeIn = math.saturate((now - _hoverInitialTime) / HoverFadeInSeconds);
            float fadeOut = 1f - math.saturate((now - _hoverLastTime) / HoverFadeOutSeconds);
            float alpha = fadeIn * fadeOut;
            if (alpha <= 0f)
            {
                _hoverHandle = null;
            }

            return alpha;
        }

        /// <summary>Identity match (piece + tile + face) for two handles, ignoring the transient grow/shrink liveness flags.</summary>
        private static bool SameHandle(in ExpansionHandle a, in ExpansionHandle b) =>
            a.Piece == b.Piece && a.Position == b.Position && a.Face == b.Face;

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
    }
}
