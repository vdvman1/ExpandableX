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
    private readonly Hook _tickHook;
    private GameSessionOrchestrator _capturedSession;
    private readonly ExpandableXNetworkDeleteHook _deleteHook;
    private readonly ExpandableXNetworkHudHook _hudHook;
    private readonly ExpandableXFocusHighlightHook _focusHighlightHook;
    private readonly ExpandableXExpansionHandleHook _expansionHandleHook;
    private readonly ExpandableXExpansionDragHook _expansionDragHook;
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

        // Draw the drag handles on the selected logical building's growable/shrinkable faces (#5, ADR-0014).
        _expansionHandleHook = new ExpandableXExpansionHandleHook(ExpandableXRegistry.Instance, logger);

        // Grab + drag a handle to grow/shrink (#5, ADR-0014, slice 3c). Claims the press over a handle on the
        // BuildingsIdle input seam and commits the drag as an undoable grow/shrink chain.
        _expansionDragHook = new ExpandableXExpansionDragHook(ExpandableXRegistry.Instance, logger);

        // Capture the session's managers once per session. They live only on the GameSessionOrchestrator's
        // per-session DI container, which the game populates as the session loads; the game exposes no
        // mod-facing "session started" callback (confirmed against the official docs + samples — mods
        // declare content at load and the game owns runtime, so reaching live session services is off the
        // documented path). We therefore capture lazily on the orchestrator's Tick (a stable public method):
        // on the first Tick where the container can resolve our services we resolve them once and stop, and a
        // new session swaps the orchestrator instance in so we re-capture. Resolving from the container is
        // the DI-blessed "inject only what you need" — preferred over the obsolete StaticGameCoreAccessor.
        // The selector is a typed local to force the void (Action) hook overload.
        System.Linq.Expressions.Expression<System.Action<GameSessionOrchestrator, float>> tickSelector =
            (orchestrator, realDeltaTime) => orchestrator.Tick(realDeltaTime);
        _tickHook = DetourHelper.CreatePostfixHook<GameSessionOrchestrator, float>(
            tickSelector,
            (orchestrator, _) => CaptureSession(orchestrator, logger));

        logger.Info.Log("ExpandableX-Core loaded!");
    }

    /// <summary>
    /// Lazily capture the live session's managers by resolving them from the orchestrator's DI container on
    /// the first Tick where they're bound (see the constructor for why this seam). A no-op once captured for
    /// the current orchestrator instance; re-runs when a new session swaps the instance in.
    /// </summary>
    private void CaptureSession(GameSessionOrchestrator orchestrator, ILogger logger)
    {
        if (ReferenceEquals(orchestrator, _capturedSession))
        {
            return;
        }

        // Resolve from the session DI container. PlayerActionManager (undoable actions) and IMapModel
        // (drag-handle hit-testing/draw) are the must-haves; on early load ticks the container hasn't bound
        // them yet, so bail and retry next tick — leaving this session uncaptured so we try again.
        var container = orchestrator.DependencyContainer;
        if (container == null
            || !container.TryResolve(out PlayerActionManager playerActions)
            || !container.TryResolve(out IMapModel map))
        {
            return;
        }

        _capturedSession = orchestrator;

        ExpandableXRegistry registry = ExpandableXRegistry.Instance;
        registry.PlayerActions = playerActions;
        registry.Map = map;
        registry.SessionTheme = container.TryResolve(out VisualTheme theme) ? theme : null;
        registry.Viewport = container.TryResolve(out Viewport viewport) ? viewport : null;
        // LocalPlayer isn't registered as a resolvable service; read the orchestrator's public property.
        registry.LocalPlayer = orchestrator.LocalPlayer;

        // Keep networks atomic in the building selection and track the focus piece (ADR-0013). Re-created
        // per session; the old one unsubscribes on dispose. Fails open so a bug here can't wedge the game.
        try
        {
            _selection?.Dispose();
            _selection = new ExpandableXNetworkSelection(registry, orchestrator.LocalPlayer, logger);
            registry.NetworkSelection = _selection;
        }
        catch (System.Exception e)
        {
            _selection = null;
            registry.NetworkSelection = null;
            logger.Info.Log($"ExpandableX-Core: network selection manager init failed: {e}");
        }
    }
    public void Dispose()
    {
        _tickHook?.Dispose();
        _deleteHook?.Dispose();
        _hudHook?.Dispose();
        _focusHighlightHook?.Dispose();
        _expansionHandleHook?.Dispose();
        _expansionDragHook?.Dispose();
        _selection?.Dispose();
    }
}
