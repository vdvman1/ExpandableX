using JetBrains.Annotations;
using ILogger = Core.Logging.ILogger;

[UsedImplicitly]
public class ExpandableXCoreMod : IMod
{
    public ExpandableXCoreMod(ILogger logger)
    {
        logger.Info.Log("ExpandableX-Core loaded!");
    }
    public void Dispose() { }
}
