extern alias monomod;
using System;
using System.Linq.Expressions;
using System.Reflection;
using monomod::MonoMod.RuntimeDetour;
using ShapezShifter.SharpDetour;
using ILogger = Core.Logging.ILogger;

namespace ExpandableX.Core
{
    /// <summary>
    /// HUD panel gating for a focused network (ADR-0013). The game chooses the building HUD purely by
    /// <c>BuildingSelection.Count</c>: <c>== 1</c> shows <see cref="HUDBuildingDetails"/> (the per-piece
    /// panel, where the slot-config + grow/shrink buttons live), <c>&gt; 1</c> shows
    /// <see cref="HUDBuildingSelectionDetails"/> (the many-buildings panel). A single-clicked network is
    /// <c>Count &gt; 1</c>, so without help it would show the mass panel and hide the per-piece UI. Two
    /// postfix detours, both keyed on the <see cref="ExpandableXNetworkSelection.TryGetFocus">focus
    /// piece</see>, fix that:
    ///
    /// <list type="bullet">
    /// <item><b>HUDBuildingDetails.CurrentTargetBuilding</b> — when the selection is a focused network,
    /// return the focus piece. Everything in that panel (its <c>ShouldShowSidePanel</c>, modules, title,
    /// actions) derives from this one property, so the per-piece panel renders for the clicked piece even
    /// at <c>Count &gt; 1</c>.</item>
    /// <item><b>HUDBuildingSelectionDetails.ShouldShowSidePanel</b> — suppress the mass panel for that same
    /// case, keeping the two panels mutually exclusive.</item>
    /// </list>
    ///
    /// A genuine mass selection (no focus) leaves both untouched. The <c>CurrentTargetBuilding</c> getter
    /// has no method-call expression form, so it is hooked via a raw <see cref="Hook"/> over the reflected
    /// getter; <c>ShouldShowSidePanel</c> goes through <see cref="DetourHelper"/>. Both read the focus from
    /// <see cref="ExpandableXRegistry.NetworkSelection"/>, which is null outside a session, and both fail
    /// open (return the game's original result) so a bug here can never wedge the HUD.
    /// </summary>
    internal sealed class ExpandableXNetworkHudHook : IDisposable
    {
        private readonly Hook _targetHook;
        private readonly Hook _massPanelHook;

        public ExpandableXNetworkHudHook(ExpandableXRegistry registry, ILogger logger)
        {
            // The per-piece panel targets the focus piece when the selection is one focused network.
            MethodInfo currentTargetGetter = typeof(HUDBuildingDetails)
                .GetProperty(
                    nameof(HUDBuildingDetails.CurrentTargetBuilding),
                    BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)!
                .GetGetMethod(nonPublic: true)!;

            _targetHook = new Hook(
                currentTargetGetter,
                (Func<HUDBuildingDetails, BuildingModel> orig, HUDBuildingDetails self) =>
                {
                    BuildingModel result = orig(self);
                    try
                    {
                        if (registry.NetworkSelection?.TryGetFocus(out BuildingModel focus) == true)
                        {
                            return focus;
                        }
                    }
                    catch (Exception e)
                    {
                        logger.Info.Log($"ExpandableX-Core: focus-piece HUD target failed, using default: {e}");
                    }

                    return result;
                });

            // Hide the many-buildings panel when the per-piece panel above is handling a focused network.
            _massPanelHook = DetourHelper.CreatePostfixHook(
                (HUDBuildingSelectionDetails hud) => hud.ShouldShowSidePanel(),
                (self, result) =>
                {
                    if (!result)
                    {
                        return false;
                    }

                    try
                    {
                        if (registry.NetworkSelection?.TryGetFocus(out _) == true)
                        {
                            return false;
                        }
                    }
                    catch (Exception e)
                    {
                        logger.Info.Log($"ExpandableX-Core: mass-panel suppression failed, using default: {e}");
                    }

                    return result;
                });
        }

        public void Dispose()
        {
            _targetHook?.Dispose();
            _massPanelHook?.Dispose();
        }
    }
}
