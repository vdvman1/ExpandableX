using ExpandableX.Core;
using JetBrains.Annotations;
using ILogger = Core.Logging.ILogger;

[UsedImplicitly]
public class ExpandableXMod : IMod
{
    public ExpandableXMod(ILogger logger)
    {
        ExpandableXRegistry.Instance.Register(new StaticLayout(
            groupId: "PainterDefaultVariant",
            slots: new[]
            {
                new ConnectorSlot(id: "PaintInput0", allowedRoles: new[] { SlotRole.Input, SlotRole.Disabled }, defaultRole: SlotRole.Input),
                new ConnectorSlot(id: "PaintInput1", allowedRoles: new[] { SlotRole.Input, SlotRole.Disabled }, defaultRole: SlotRole.Input),
            }));
        logger.Info.Log("ExpandableX loaded!");
    }
    public void Dispose() { }
}
