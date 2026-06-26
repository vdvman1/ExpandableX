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
        // TUNABLE (in-game): how far out the handle sits, in TILE units along the face (0.5 = on the face
        // edge, 1.0 = the adjacent tile's centre); its lift off the ground (world units); and the draw alpha.
        private const float HandleDistance = 1.1f;
        private const float LiftHeight = 0.05f;
        private const float Alpha = 0.9f;

        // TUNABLE: when a face offers both grow and shrink (a removable end's leading face), the two caps
        // are nudged apart sideways — ±this many tiles along the face's perpendicular — so they don't
        // overlap. Zero when the face offers only one direction (no nudge).
        private const float HandleSeparation = 0.2f;

        // TUNABLE: belt-cap meshes are LOD arrays indexed by belt height; index 0 (height 1) is the
        // shortest. Which height reads best as a handle is an in-game call.
        private const int CapHeightIndex = 0;

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
                    // Outward-facing cap for grow, inward-facing for shrink. A face that is both (a removable
                    // end's leading face) shows both — drag out grows, drag in shrinks — nudged apart
                    // sideways so they don't draw on top of each other.
                    float lateral = handle.CanGrow && handle.CanShrink ? HandleSeparation : 0f;

                    if (handle.CanGrow)
                    {
                        DrawCap(options, handle.Position, handle.Face, resources.BeltCapOutput, resources.UXBuildingBlueprintSpotIndicatorMaterial, lateral);
                    }

                    if (handle.CanShrink)
                    {
                        DrawCap(options, handle.Position, handle.Face, resources.BeltCapInput, resources.UXBuildingBlueprintSpotIndicatorMaterial, -lateral);
                    }
                }
            }
            catch (Exception e)
            {
                _logger.Info.Log($"ExpandableX-Core: expansion-handle draw failed, leaving the plain selection: {e}");
            }
        }

        private void DrawCap(
            FrameDrawOptions options, GlobalTileCoordinate position, TileDirection face,
            LODMeshAsset[] caps, MaterialReference material, float lateralTiles)
        {
            if (caps is null || caps.Length <= CapHeightIndex
                || !caps[CapHeightIndex].TryGet(options.LOD.BuildingLOD, out IMeshReference mesh))
            {
                return;
            }

            // Sit the cap outside the face by HandleDistance *tiles*, lifted a touch off the ground, oriented
            // to point along the face (GlobalRotationTo maps a TileDirection to the GridRotation facing it).
            // The offset is derived from the real centre-to-centre step (one tile in world units), so it is
            // independent of the tile↔world scale — a fixed world offset sat almost at the centre, because
            // WorldVector.ByDirection is a 1-unit vector while a tile spans ~20 world units.
            WorldCoordinate selfCenter = position.ToCenter_W();
            WorldVector tileStep = position.Move(face).ToCenter_W() - selfCenter;
            WorldCoordinate translation = selfCenter + HandleDistance * tileStep + LiftHeight * WorldVector.Up;

            // Sideways nudge (along the face's perpendicular) so a both-directions face's two caps don't
            // overlap. Same scale-independent centre-to-centre derivation, on the perpendicular tile.
            if (lateralTiles != 0f)
            {
                TileDirection sideways = face.Rotate(GridRotation.RotateCW);
                WorldVector lateralStep = position.Move(sideways).ToCenter_W() - selfCenter;
                translation += lateralTiles * lateralStep;
            }

            GridRotation rotation = face.GlobalRotationTo().ZRotation;

            options.Renderers.RegularNonInstanced.DrawMesh(
                mesh, material,
                FastMatrix.TranslateRotate(in translation, rotation),
                RenderCategory.AnalogUI,
                MaterialPropertyHelpers.CreateAlphaBlock(Alpha));
        }
    }
}
