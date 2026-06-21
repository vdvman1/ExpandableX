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
                }
            });

        logger.Info.Log("ExpandableX-Core loaded!");
    }
    public void Dispose()
    {
        _initHook?.Dispose();
        _deleteHook?.Dispose();
    }
}
