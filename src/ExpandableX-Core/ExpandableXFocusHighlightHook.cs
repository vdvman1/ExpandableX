extern alias monomod;
using System;
using System.Collections.Generic;
using Game.Core.Coordinates;
using monomod::MonoMod.RuntimeDetour;
using ShapezShifter.SharpDetour;
using Unity.Mathematics;
using UnityEngine;
using ILogger = Core.Logging.ILogger;

namespace ExpandableX.Core
{
    /// <summary>
    /// Draws the focus piece (ADR-0013) in a distinct colour so the player can see which piece of the
    /// selected network the per-piece HUD is configuring. The whole network is highlighted blue by
    /// <see cref="HUDBuildingMassSelection.Draw_ExistingSelection"/> every frame (it draws each selected
    /// building as a blueprint overlay tinted <c>ThemeWillBePlaced</c>). Two hooks on that method:
    ///
    /// <list type="bullet">
    /// <item>A <b>prefix</b> drops the focus piece from the collection the base draws, so it gets <i>no</i>
    /// blue — otherwise the translucent blue and amber blend to a washed-out near-white that reads too close
    /// to the platform grey.</item>
    /// <item>A <b>postfix</b> re-draws the focus building in <see cref="FocusColor"/>, nudged a touch toward
    /// the camera, giving full control of its colour.</item>
    /// </list>
    ///
    /// The magenta tint is chosen to be unique: the placement palette occupies the warm half (yellow →
    /// amber → red) and the selection owns blue, so magenta matches none of them — including
    /// <c>ThemeWontBePlaced</c>, the amber "placement will modify an existing building" colour an earlier
    /// amber tint sat too close to. It is also not a typical wire/signal colour (those are green/red). It is
    /// a flat colour parameter, not an authored asset. Drawn through the game's own blueprint material +
    /// instanced building renderer (the same path the selection uses), so nothing about the render pipeline
    /// is reimplemented. The theme comes from <see cref="ExpandableXRegistry.SessionTheme"/> (resolved via
    /// DI), not the obsolete per-frame <c>FrameDrawOptions.Theme</c>. Fails open — any error just skips the
    /// overlay.
    /// </summary>
    internal sealed class ExpandableXFocusHighlightHook : IDisposable
    {
        /// <summary>Vivid magenta. Matches none of the placement-allowability colours, the selection blue, or wire signal colours.</summary>
        private static readonly Color FocusColor = new(0.85f, 0.1f, 0.85f);

        /// <summary>
        /// How far to nudge the overlay toward the camera. The selection's own blueprint overlay uses
        /// 0.01 for a valid placement; a larger value renders the focus tint in front of it.
        /// </summary>
        private const float CameraNudge = 0.02f;

        private readonly ExpandableXRegistry _registry;
        private readonly ILogger _logger;
        private readonly FixedShaderColorProvider _color = new(FocusColor);
        private readonly Hook _filterHook;
        private readonly Hook _drawHook;

        public ExpandableXFocusHighlightHook(ExpandableXRegistry registry, ILogger logger)
        {
            _registry = registry;
            _logger = logger;

            // Exclude the focus piece from the blue selection draw (so only our amber colours it).
            _filterHook = DetourHelper.CreatePrefixHook(
                (HUDBuildingMassSelection hud, FrameDrawOptions options, IReadOnlyCollection<BuildingModel> selection) =>
                    hud.Draw_ExistingSelection(options, selection),
                (hud, options, selection) =>
                    (options, WithoutFocus(selection)));

            // Draw the focus piece in its own colour.
            _drawHook = DetourHelper.CreatePostfixHook(
                (HUDBuildingMassSelection hud, FrameDrawOptions options, IReadOnlyCollection<BuildingModel> selection) =>
                    hud.Draw_ExistingSelection(options, selection),
                (hud, options, selection) =>
                    DrawFocusOverlay(options)
            );
        }

        public void Dispose()
        {
            _filterHook?.Dispose();
            _drawHook?.Dispose();
        }

        /// <summary>The selection minus the focus piece (which our overlay colours instead); unchanged when there's no focus.</summary>
        private IReadOnlyCollection<BuildingModel> WithoutFocus(IReadOnlyCollection<BuildingModel> selection)
        {
            try
            {
                if (_registry.NetworkSelection?.TryGetFocus(out BuildingModel focus) != true)
                {
                    return selection;
                }

                var remaining = new List<BuildingModel>(selection.Count);
                foreach (BuildingModel building in selection)
                {
                    if (!building.Equals(focus))
                    {
                        remaining.Add(building);
                    }
                }

                return remaining;
            }
            catch (Exception e)
            {
                _logger.Info.Log($"ExpandableX-Core: focus highlight filter failed, drawing the plain selection: {e}");
                return selection;
            }
        }

        private void DrawFocusOverlay(FrameDrawOptions options)
        {
            try
            {
                if (_registry.SessionTheme is not { } theme
                    || (_registry.NetworkSelection?.TryGetFocus(out BuildingModel focus) != true))
                {
                    return;
                }

                if (!focus.Definition.CustomData.Get<IBuildingDrawData>().CombinedBlueprintMesh
                        .TryGet(options.LOD.BuildingLOD, out IMeshReference mesh))
                {
                    return;
                }

                // Match how the selection blueprint overlay positions itself: nudge the mesh toward the
                // camera (so it doesn't z-fight the blue), by a hair more than the selection does.
                float3 center = focus.Transform.Position.ToCenter_W();
                float3 toCamera = options.CameraPosition_W - center;
                float3 nudge = toCamera / math.lengthsq(toCamera) * CameraNudge;
                float3 position = center + nudge;
                Matrix4x4 trs = FastMatrix.TranslateRotate(in position, focus.Rotation_G);

                options.Renderers.Buildings.AddWithProperties(
                    mesh,
                    theme.BaseResources.UXBuildingBlueprintMaterial,
                    trs,
                    _color.PropertyBlock,
                    _color.PropertyBlockKey,
                    options.LOD.Shadows,
                    options.LOD.Shadows);
            }
            catch (Exception e)
            {
                _logger.Info.Log($"ExpandableX-Core: focus highlight draw failed, leaving the plain selection: {e}");
            }
        }
    }
}
