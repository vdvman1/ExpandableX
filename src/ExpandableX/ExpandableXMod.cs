using JetBrains.Annotations;
using ILogger = Core.Logging.ILogger;

[UsedImplicitly]
public class ExpandableXMod : IMod
{
    public ExpandableXMod(ILogger logger)
    {
        logger.Info.Log("ExpandableX loaded!");
    }
    public void Dispose() { }
}
