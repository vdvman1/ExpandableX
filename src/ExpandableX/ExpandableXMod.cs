using ExpandableX;
using ExpandableX.Core;
using Game.Core.Research;
using JetBrains.Annotations;
using ShapezShifter.Hijack;
using ShapezShifter.Kit;
using System;
using System.Collections.Generic;
using System.Linq;
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
        // Painter: two registrations, one per definition in the PainterDefaultVariant group — the base and
        // its mirror. Each is a single static layout whose three visible paint junctions are toggleable
        // {Enabled, Disabled} slots, with at least one always enabled (an all-disabled painter is
        // pointless). Mirroring is a separate MetaBuildingDefinition the game swaps to, so it gets its own
        // registration (ADR-0015); Left/Right are physically swapped on the mirror, so its labels swap too.
        // The exact visible-index -> face mapping is to be confirmed in-game. See CONTEXT.md "Painter",
        // "Slot label", and the Role / Connector slot entries.
        RegisterPainterVariant(
            registrationId: "PainterDefaultVariant",
            layoutId: "Painter.Default",
            baseDefinitionId: "PainterDefaultInternalVariant",
            paintLabel: index => index switch
            {
                0 => "Left Fluid",
                1 => "Bottom Fluid",
                2 => "Right Fluid",
                _ => $"Fluid {index}",
            },
            models: PainterModels());

        // The mirror reuses the same model set: the framework reflects the authored meshes via the
        // game's mesh mirror (the mirrored base is its own definition with mirrored connector data), so
        // no separate mirrored FBX is needed.
        RegisterPainterVariant(
            registrationId: "PainterDefaultVariantMirrored",
            layoutId: "Painter.DefaultMirrored",
            baseDefinitionId: "PainterDefaultInternalVariantMirrored",
            paintLabel: index => index switch
            {
                0 => "Right Fluid",
                1 => "Bottom Fluid",
                2 => "Left Fluid",
                _ => $"Fluid {index}",
            },
            models: PainterModels());
    }

    /// <summary>
    /// The painter's composed model (ADR-0016): a clean body, a custom static blueprint (keeps the
    /// roller/hinge the animated main mesh omits), and one fluid-junction bridge reused across all three
    /// paint junctions — authored canonically (connector at origin facing East), so the framework just
    /// rotates a copy onto each junction (offset zero; the model's origin is set for rotation-only).
    /// LOD0-1 are exported; higher LODs reuse the last supplied level until authored. The mirror reuses
    /// this same set, reflected by the framework. Loaded from the mod's Resources dir at bake time.
    /// </summary>
    private static ModelPieceSet PainterModels()
    {
        ModFolderLocator painter = ModDirectoryLocator.CreateLocator<ExpandableXMod>()
            .SubLocator("Resources").SubLocator("Painter");

        return ModelPieceSet
            .WithBody(ModelMesh.FromFiles(Enumerable.Range(0, 6).Select(i => painter.SubPath($"Main_LOD{i}.fbx")).ToArray()))
            .Blueprint(ModelMesh.FromFiles(Enumerable.Range(0, 6).Select(i => painter.SubPath($"Blueprint_LOD{i}.fbx")).ToArray()))
            .Bridge<BuildingFluidJunction>(SlotRole.Enabled, ModelMesh.FromFiles(Enumerable.Range(0, 6).Select(i => painter.SubPath($"FluidConnector_LOD{i}.fbx")).ToArray()))
            .Build();
    }

    /// <summary>
    /// Registers one painter definition (base or mirror) as a single toggleable-junction static layout.
    /// The only difference between the two is the base definition id and the per-index paint label
    /// (Left/Right swap on the mirror), so both share this body.
    /// </summary>
    private static void RegisterPainterVariant(
        string registrationId, string layoutId, string baseDefinitionId, Func<int, string> paintLabel,
        ModelPieceSet models = null)
    {
        ExpandableXRegistry.Instance.Register(new Registration(
            RegistrationId: registrationId,
            Layouts: new Layout[]
            {
                new Layout.Static(
                    LayoutId: layoutId,
                    Piece: new PieceSpec(
                        BaseDefinitionId: baseDefinitionId,
                        SlotSpecs: new ConnectorSlotSpec[]
                        {
                            ConnectorSlotSpec.Range.Of<BuildingFluidJunction>(
                                idPrefix: "paint",
                                allowedRoles: new[] { SlotRole.Enabled, SlotRole.Disabled },
                                defaultRole: SlotRole.Enabled,
                                labelAt: paintLabel),
                        },
                        LocalPredicates: new[]
                        {
                            SlotPredicates.AtLeastOne(new[] { SlotRole.Enabled }),
                        },
                        Models: models),
                    // Each synthesised painter variant is simulated like the stock painter: the framework
                    // attaches an atomic per-definition simulation, we just hand it the base game's own
                    // TopmostPainterSimulation factory (config read off the variant's definition). The
                    // base definition itself keeps the game's simulation — only synthesised variants get this.
                    Simulation: StaticSimulation.Stateful(
                        (definition, deps) => new TopmostPainterSimulationFactory(
                            definition.ConfigAs<IPainterConfiguration>(),
                            new ShapeOperationPaintTopmost(deps.ShapeRegistry, deps.ShapeIdManager),
                            deps.ShapeRegistry))),
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
        // The mirrored 2-output cutter (the game's own mirror of CutterDefaultInternalVariant): same input
        // cell + face, second output flipped to the opposite side — used to grow the destroyer the other way.
        var cutterMirrored = new Layout.Static("Cutter.TwoOutputMirrored", NoSlotPiece("CutterDefaultInternalVariantMirrored"));
        var hex3 = new Layout.Static("Cutter.Hex3", NoSlotPiece("Hex3Cutter")); // TODO(hex): unbuilt
        var hex6 = new Layout.Static("Cutter.Hex6", NoSlotPiece("Hex6Cutter")); // TODO(hex): unbuilt

        ExpandableXRegistry.Instance.Register(new Registration(
            RegistrationId: "Cutter",
            Layouts: new Layout[] { destroyer, cutter, cutterMirrored, hex3, hex6 },
            Expansions: new[]
            {
                Expand.Sequence(
                    TileDirection.North,
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

                // The same square sequence the other way: grow South into the mirrored cutter, so the second
                // output lands on the opposite side while the input keeps its cell + face (no re-anchoring).
                Expand.Sequence(
                    TileDirection.South,
                    new[]
                    {
                        Expand.Step(destroyer, ExpansionConditions.When(
                            () => IsResearched("CBCutting_HalfDestroyer"), "half-destroyer research")),
                        Expand.Step(cutterMirrored, ExpansionConditions.When(
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
        // Override: the standalone 2-input-AND config reuses the base-game AND definition instead of a
        // synthesised one. Slot order is in_0=N, in_1=S, in_2=W, out_0=E (the connector authoring order
        // in AndGateNetworkBaseRewirer), so the 2-input AND — inputs N+S, W disabled, output E — is the
        // combo "IIDO". The framework canonicalises this key to whichever orientation it generates, so
        // it lands correctly despite rotational canonicalisation.
        var gameplayRoles = new[] { SlotRole.Input, SlotRole.Output, SlotRole.Disabled };
        var piece = new PieceSpec(
            BaseDefinitionId: AndGateNetworkBaseRewirer.BaseDefinitionId,
            SlotSpecs: new ConnectorSlotSpec[]
            {
                ConnectorSlotSpec.Range.Of<BuildingSignalInput>("in", gameplayRoles, SlotRole.Input),
                ConnectorSlotSpec.Range.Of<BuildingSignalOutput>("out", gameplayRoles, SlotRole.Output),
            },
            LocalPredicates: Array.Empty<ISlotPredicate>(),
            VariantOverrides: new Dictionary<string, string> { ["IIDO"] = "LogicGateAndInternalVariant" },
            Models: AndGateModels(),
            // The configurable base (ExpandableXAndNetworkBase) carries 3 inputs but its model was cloned
            // from the 2-input base-game AND, so compose the base's model too or the 3rd-input bridge is
            // missing. (The 2-input default reaches the base-game AND via the IIDO override, which keeps
            // its own model.)
            ComposeBaseModel: true);

        var layout = new Layout.Dynamic(
            LayoutId: "AndGate.Network",
            Piece: piece,
            ShapeLimit: ShapeLimits.Free,
            // One declaration of the gate's connector rules: an AND needs at least one input and at least
            // one output across the whole building. These gate networks at runtime AND prune impossible
            // singleton variants (no input, or no output) at generation — no separate local predicate.
            NetworkPredicates: new[]
            {
                NetworkPredicates.AtLeastOne(new[] { SlotRole.Input }),
                NetworkPredicates.AtLeastOne(new[] { SlotRole.Output }),
            },
            // The simulation is the N-input AND (AndGateSignalSimulation) wrapped by the framework's
            // reusable signal node; a lambda factory suffices — no dedicated factory class.
            SimulationFactory: (members, tiles) =>
                new SignalExpandableSimulation<AndGateSignalSimulation>(
                    members, tiles, (inputs, outputs) => new AndGateSignalSimulation(inputs, outputs)));

        ExpandableXRegistry.Instance.Register(new Registration(
            RegistrationId: "AndGate",
            Layouts: new Layout[] { layout },
            Expansions: new[] { Expand.Network(layout) }));
    }

    /// <summary>
    /// The AND gate's composed model (ADR-0016). One clean body (per-gate, under LogicGate/And) plus the
    /// shared logic-gate connector bridges (under LogicGate/Common). Bridges key on the common
    /// <see cref="BuildingSignalIO"/> base + role, not the specific input/output type, because a tri-state
    /// slot may be flipped: the composer reports each slot's <b>base</b> connector type, so an input face
    /// switched to Output must still resolve the output bridge (and vice versa). The join/seam reuses the
    /// input connector — the closest thing to a two-way connection without a new model. Authored
    /// canonically (connector on the East/+X face); the framework rotates a copy onto each face and per the
    /// piece's GridRotation. Up to 6 LODs per file (Main_LOD0..5, etc.); missing levels reuse the nearest
    /// lower one, so exporting more LODs needs no code change. No custom blueprint — the gate isn't
    /// animated, so the auto-derived blueprint (from the composed body) is correct.
    /// </summary>
    private static ModelPieceSet AndGateModels()
    {
        ModFolderLocator gate = ModDirectoryLocator.CreateLocator<ExpandableXMod>()
            .SubLocator("Resources").SubLocator("LogicGate");
        ModFolderLocator common = gate.SubLocator("Common");
        ModFolderLocator and = gate.SubLocator("And");

        IModelMesh input = ModelMesh.FromFiles(Enumerable.Range(0, 6).Select(i => common.SubPath($"InputConnector_LOD{i}.fbx")).ToArray());
        IModelMesh output = ModelMesh.FromFiles(Enumerable.Range(0, 6).Select(i => common.SubPath($"OutputConnector_LOD{i}.fbx")).ToArray());

        return ModelPieceSet
            .WithBody(ModelMesh.FromFiles(Enumerable.Range(0, 6).Select(i => and.SubPath($"Main_LOD{i}.fbx")).ToArray()))
            .Bridge<BuildingSignalIO>(SlotRole.Input, input)
            .Bridge<BuildingSignalIO>(SlotRole.Output, output)
            .Seam(input)
            .Build();
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
