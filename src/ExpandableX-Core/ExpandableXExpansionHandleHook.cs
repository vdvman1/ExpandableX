extern alias monomod;
using System;
using System.Collections.Generic;
using System.Linq;
using Game.Core.Coordinates;
using ShapezShifter.SharpDetour;
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
        private readonly ExpandableXRegistry _registry;
        private readonly ILogger _logger;
        private readonly monomod::MonoMod.RuntimeDetour.Hook _drawHook;

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
                    return;
                }

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
                }
            }
            catch (Exception e)
            {
                _logger.Info.Log($"ExpandableX-Core: expansion-handle draw failed, leaving the plain selection: {e}");
            }
        }
    }
}
