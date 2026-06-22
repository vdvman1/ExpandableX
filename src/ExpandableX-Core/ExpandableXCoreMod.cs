extern alias monomod;
using ExpandableX.Core;
using Game.Orchestration;
using JetBrains.Annotations;
using monomod::MonoMod.RuntimeDetour;
using ShapezShifter.Hijack;
using ShapezShifter.SharpDetour;
using ILogger = Core.Logging.ILogger;

[UsedImplicitly]
public class ExpandableXCoreMod : IMod
{
    private readonly Hook _initHook;
    private readonly ExpandableXNetworkDeleteHook _deleteHook;
    private readonly ExpandableXNetworkHudHook _hudHook;
    private readonly ExpandableXFocusHighlightHook _focusHighlightHook;
    private ExpandableXNetworkSelection? _selection;

    public ExpandableXCoreMod(ILogger logger)
    {
        ExpandableXRegistry.Initialize(logger);
        GameRewirers.AddRewirer<ISimulationSystemsRewirer>(
            new ExpandableXSimulationSystemsRewirer(logger, ExpandableXRegistry.Instance));
        GameRewirers.AddRewirer<IBuildingModulesRewirer>(
            new ExpandableXBuildingModulesRewirer(logger, ExpandableXRegistry.Instance));

        // Whole-network delete chokepoint (ADR-0013, issue #9 absorbing #10): deleting any network
        // member deletes the whole network, across every delete gesture. Installed once at load; it
        // no-ops until a session populates the matcher and map.
        _deleteHook = new ExpandableXNetworkDeleteHook(ExpandableXRegistry.Instance, logger);

        // HUD panel gating (ADR-0013): a single-clicked network shows the per-piece panel (focused on the
        // clicked piece), not the many-buildings panel. Installed once at load; inert until a session
        // populates the selection manager. Reads focus from the registry.
        _hudHook = new ExpandableXNetworkHudHook(ExpandableXRegistry.Instance, logger);

        // Colour the focus piece distinctly on top of the network's blue selection highlight (ADR-0013).
        _focusHighlightHook = new ExpandableXFocusHighlightHook(ExpandableXRegistry.Instance, logger);

        // Capture the session's PlayerActionManager once per session so slot changes can be
        // dispatched as undoable actions. Init() is the stable, non-version-numbered orchestrator
        // entry point; by the time it returns, PlayerActions (and LocalPlayer) are populated.
        // The selector is a typed local to force the void (Action) hook overload, since the multi-arg
        // CreatePostfixHook overloads are otherwise ambiguous with the result-returning ones.
        System.Linq.Expressions.Expression<System.Action<GameSessionOrchestrator, IGameStartOptions, GlobalsData, IGameData>> initSelector =
            (orchestrator, options, globals, data) => orchestrator.Init(options, globals, data);
        _initHook = DetourHelper.CreatePostfixHook<GameSessionOrchestrator, IGameStartOptions, GlobalsData, IGameData>(
            initSelector,
            (orchestrator, _, _, _) =>
            {
                if (orchestrator.PlayerActions != null)
                {
                    ExpandableXRegistry.Instance.PlayerActions = orchestrator.PlayerActions;
                    ExpandableXRegistry.Instance.LocalPlayer = orchestrator.LocalPlayer;

                    // Resolve the session theme from the game's DI container (bound during Init) and cache
                    // it, so our draw hooks read the theme via DI rather than the obsolete per-frame
                    // FrameDrawOptions.Theme. Null-tolerant: the focus highlight just skips if unavailable.
                    ExpandableXRegistry.Instance.SessionTheme =
                        orchestrator.DependencyContainer.TryResolve(out VisualTheme theme) ? theme : null;

                    // Keep networks atomic in the building selection and track the focus piece for this
                    // session (ADR-0013). Re-created per session; the old one unsubscribes on dispose.
                    // Guarded because this runs on the session-init critical path: a throw here would
                    // fault loading and freeze the game, so it fails open (no atomic selection) instead.
                    try
                    {
                        _selection?.Dispose();
                        _selection = new ExpandableXNetworkSelection(
                            ExpandableXRegistry.Instance, orchestrator.LocalPlayer, logger);
                        ExpandableXRegistry.Instance.NetworkSelection = _selection;
                    }
                    catch (System.Exception e)
                    {
                        _selection = null;
                        ExpandableXRegistry.Instance.NetworkSelection = null;
                        logger.Info.Log($"ExpandableX-Core: network selection manager init failed: {e}");
                    }
                }
            });

        logger.Info.Log("ExpandableX-Core loaded!");
    }
    public void Dispose()
    {
        _initHook?.Dispose();
        _deleteHook?.Dispose();
        _hudHook?.Dispose();
        _focusHighlightHook?.Dispose();
        _selection?.Dispose();
    }
}
