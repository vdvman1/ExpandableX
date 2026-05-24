using ExpandableX.Core;
using Game.Orchestration;
using JetBrains.Annotations;
using MonoMod.RuntimeDetour;
using ShapezShifter.Hijack;
using ShapezShifter.SharpDetour;
using ILogger = Core.Logging.ILogger;

[UsedImplicitly]
public class ExpandableXCoreMod : IMod
{
    private readonly Hook _finalizeLogicUpdateHook;

    public ExpandableXCoreMod(ILogger logger)
    {
        ExpandableXRegistry.Initialize(logger);
        GameRewirers.AddRewirer<ISimulationSystemsRewirer>(
            new ExpandableXSimulationSystemsRewirer(logger, ExpandableXRegistry.Instance));
        GameRewirers.AddRewirer<IBuildingModulesRewirer>(
            new ExpandableXBuildingModulesRewirer(logger, ExpandableXRegistry.Instance));

        _finalizeLogicUpdateHook = DetourHelper.CreatePostfixHook<GameSessionOrchestrator>(
            orchestrator => orchestrator.FinalizeLogicUpdate(),
            _ => ExpandableXRegistry.Instance.DrainDeferred());

        logger.Info.Log("ExpandableX-Core loaded!");
    }
    public void Dispose()
    {
        _finalizeLogicUpdateHook?.Dispose();
    }
}
