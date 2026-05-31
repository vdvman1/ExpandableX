using System;
using ExpandableX.Core;
using JetBrains.Annotations;
using ILogger = Core.Logging.ILogger;

[UsedImplicitly]
public class ExpandableXMod : IMod
{
    public ExpandableXMod(ILogger logger)
    {
        // Painter: a single static layout whose three visible paint junctions are toggleable
        // {Enabled, Disabled} slots. At least one must stay enabled (an all-disabled painter is
        // pointless). See CONTEXT.md "Painter" and the Role / Connector slot entries.
        ExpandableXRegistry.Instance.Register(new Registration(
            RegistrationId: "PainterDefaultVariant",
            Layouts: new Layout[]
            {
                new Layout.Static(
                    LayoutId: "Painter.Default",
                    Piece: new PieceSpec(
                        // The configurable-base *definition* inside the "PainterDefaultVariant" group
                        // (confirmed in-game). The group also contains a mirrored definition
                        // (PainterDefaultInternalVariantMirrored) which would be its own registration.
                        BaseDefinitionId: "PainterDefaultInternalVariant",
                        Role: PieceRole.Singleton,
                        SlotSpecs: new ConnectorSlotSpec[]
                        {
                            ConnectorSlotSpec.Range.Of<BuildingFluidJunction>(
                                idPrefix: "paint",
                                allowedRoles: new[] { SlotRole.Enabled, SlotRole.Disabled },
                                defaultRole: SlotRole.Enabled),
                        },
                        LocalPredicates: new[]
                        {
                            SlotPredicates.AtLeastOne(new[] { SlotRole.Enabled }),
                        })),
            },
            Expansions: Array.Empty<Expansion>()));

        logger.Info.Log("ExpandableX loaded!");
    }

    public void Dispose() { }
}
