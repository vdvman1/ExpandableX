using System;
using ExpandableX;
using ExpandableX.Core;
using Game.Core.Research;
using JetBrains.Annotations;
using ShapezShifter.Hijack;
using ShapezShifter.Kit;
using ILogger = Core.Logging.ILogger;

[UsedImplicitly]
public class ExpandableXMod : IMod
{
    public ExpandableXMod(ILogger logger)
    {
        RegisterPainter();
        RegisterCutter();

        // Network-model AND gate: author its configurable base (reusing the AND visual) before the
        // Core variant-generation rewirer runs, then register the dynamic layout that references it.
        GameRewirers.AddRewirer<IBuildingsRewirer>(new AndGateNetworkBaseRewirer(logger));
        RegisterAndGate();

        logger.Info.Log("ExpandableX loaded!");
    }

    private static void RegisterPainter()
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
    }

    private static void RegisterCutter()
    {
        // Cutter family. Corrected semantics, confirmed in-game (docs/research/building-definition-ids.md):
        //   CutterHalfInternalVariant    = half-destroyer (deletes half, 1 output)
        //   CutterDefaultInternalVariant = 2-output cutter (splits a shape into its two halves)
        // There is NO 4-output "quarter" cutter in the base game; a quarter cutter (and the hex 3/6
        // cutters) would be new authored buildings with their own simulations (ADR-0011) — future work.
        //
        // Square sequence: half-destroyer -> 2-output cutter. Expanding the destroyer INTO the cutter
        // requires the cutter to be researched (an existing research — this exercises research gating).
        // Hex sequence is declared (API stays hex-ready) but Hex3/Hex6 are unbuilt, so resolving them
        // logs a benign "not found"; it's gated off in square play anyway. The destroyer is shared.
        var destroyer = new Layout.Static("Cutter.HalfDestroyer", NoSlotPiece("CutterHalfInternalVariant"));
        var cutter = new Layout.Static("Cutter.TwoOutput", NoSlotPiece("CutterDefaultInternalVariant"));
        var hex3 = new Layout.Static("Cutter.Hex3", NoSlotPiece("Hex3Cutter")); // TODO(hex): unbuilt
        var hex6 = new Layout.Static("Cutter.Hex6", NoSlotPiece("Hex6Cutter")); // TODO(hex): unbuilt

        ExpandableXRegistry.Instance.Register(new Registration(
            RegistrationId: "Cutter",
            Layouts: new Layout[] { destroyer, cutter, hex3, hex6 },
            Expansions: new[]
            {
                Expand.Sequence(
                    TileDirection.East,
                    new[]
                    {
                        // Each step is gated on its own building's research: expanding INTO a step (or
                        // shrinking back to it) requires that building to be unlocked — the engine checks
                        // the target step's conditions. Both halves have a real existing research.
                        Expand.Step(destroyer, ExpansionConditions.When(
                            () => IsResearched("CBCutting_HalfDestroyer"), "half-destroyer research")),
                        Expand.Step(cutter, ExpansionConditions.When(
                            () => IsResearched("CBCutting_FullCutter"), "cutter research")),
                    },
                    conditions: new[] { ExpansionConditions.When(() => ShapeParts() == 4, "square shapes") }),

                // TODO(hex): per-step research + skip-locked wired when Hex3/Hex6 are authored.
                Expand.Sequence(
                    TileDirection.East,
                    new[] { Expand.Step(destroyer), Expand.Step(hex3), Expand.Step(hex6) },
                    conditions: new[] { ExpansionConditions.When(() => ShapeParts() == 6, "hex shapes") }),
            }));
    }

    private static void RegisterAndGate()
    {
        // Network-model AND gate (ADR-0012). Declare ONE gameplay piece — 3 signal inputs (N/S/W) + 1
        // output (E), reusing the AND visual via AndGateNetworkBaseRewirer, with the player-facing
        // I/O/Disabled roles per face. The framework adds Join as an allowed role on each face; the
        // singleton (0-join variants) and network pieces (>=1-join variants) are emergent from the
        // generated variant's join count, so the join rules aren't authored here.
        // TODO(#28 override): map the singleton 2-input-AND combo (output E, inputs N+S, W disabled) to
        // "LogicGateAndInternalVariant" once override resolution is canonicalisation-aware.
        var gameplayRoles = new[] { SlotRole.Input, SlotRole.Output, SlotRole.Disabled };
        var piece = new PieceSpec(
            BaseDefinitionId: AndGateNetworkBaseRewirer.BaseDefinitionId,
            SlotSpecs: new ConnectorSlotSpec[]
            {
                ConnectorSlotSpec.Range.Of<BuildingSignalInput>("in", gameplayRoles, SlotRole.Input),
                ConnectorSlotSpec.Range.Of<BuildingSignalOutput>("out", gameplayRoles, SlotRole.Output),
            },
            LocalPredicates: Array.Empty<ISlotPredicate>());

        var layout = new Layout.Dynamic(
            LayoutId: "AndGate.Network",
            Piece: piece,
            ShapeLimit: ShapeLimits.Free,
            NetworkPredicates: new[] { NetworkPredicates.AtLeastOne(new[] { SlotRole.Output }) });

        ExpandableXRegistry.Instance.Register(new Registration(
            RegistrationId: "AndGate",
            Layouts: new Layout[] { layout },
            Expansions: new[] { Expand.Network(layout) }));
    }

    private static PieceSpec NoSlotPiece(string baseDefinitionId) => new PieceSpec(
        BaseDefinitionId: baseDefinitionId,
        SlotSpecs: Array.Empty<ConnectorSlotSpec>(),
        LocalPredicates: Array.Empty<ISlotPredicate>());

    /// <summary>The current scenario's shape part count (4 = square, 6 = hex); 0 when no session is active.</summary>
    private static int ShapeParts() => GameHelper.Core?.Mode?.ShapesConfiguration?.PartCount ?? 0;

    /// <summary>Whether a research upgrade is unlocked in the current session (false when no session / unknown id).</summary>
    private static bool IsResearched(string researchUpgradeId)
    {
        ResearchManager research = GameHelper.Core?.Research;
        return research != null && research.Progress.IsUnlocked(new ResearchUpgradeId(researchUpgradeId));
    }

    public void Dispose() { }
}
