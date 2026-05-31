// PROTOTYPE — scenarios written in the shape of real mod-author
// registration calls. If the registration API ends up looking awkward
// in this file, that's the signal to change the API, not to wallpaper
// over it.

namespace ExpandableX.Prototype.VariantEncoding;

internal sealed record Scenario(
    string Name,
    string Question,
    Registration Registration,
    IReadOnlyList<string> ResearchKeys);   // harness-only: toggleable research in the TUI

internal static class Scenarios
{
    public static IReadOnlyList<Scenario> All { get; } = new[]
    {
        new Scenario(
            Name: "Painter (single layout, paint slots binary)",
            Question: "Does {I,D} subset + AtLeastOne(I,O) prune the all-disabled paint variant?",
            Registration: Registrations.Painter(),
            ResearchKeys: Array.Empty<string>()),

        new Scenario(
            Name: "Painter (range registration over fluid junctions)",
            Question: "Does Range.AllOf<BuildingFluidJunction> auto-skip the internal junction and yield the same 7 variants?",
            Registration: Registrations.PainterRange(),
            ResearchKeys: Array.Empty<string>()),

        new Scenario(
            Name: "AND-gate (singleton → chain, expandable on any axis)",
            Question: "Singleton shows handles on all sides. Once committed, only the two ends. Can a shrink ever leave the chain output-less?",
            Registration: Registrations.AndGate(),
            ResearchKeys: Array.Empty<string>()),

        new Scenario(
            Name: "Cutter (per-step research, skip locked intermediates)",
            Question: "Unlock Hex6 but not Hex3 — can Half expand straight to Hex6, skipping the locked Hex3?",
            Registration: Registrations.Cutter(),
            ResearchKeys: new[] { "FullCutter", "Hex3Cutter", "Hex6Cutter" }),
    };
}

// ---------------------------------------------------------------------------
// What a mod author writes. Read this file as if it were ExpandableX.cs in
// the consumer mod. ExpandableX-Core governs expansion of an already-placed
// building — the game decides initial placement, not this.
// ---------------------------------------------------------------------------
internal static class Registrations
{
    public static Registration Painter()
    {
        var layout = new Layout.Static(
            LayoutId: "Painter.Default",
            Piece: new PieceSpec(
                BaseDefinitionId: "Painter",
                Role: PieceRole.Singleton,
                SlotSpecs: new SlotSpec[]
                {
                    new SlotSpec.Single("paint_W", BinaryInputDisabled, SlotRole.Input),
                    new SlotSpec.Single("paint_S", BinaryInputDisabled, SlotRole.Input),
                    new SlotSpec.Single("paint_E", BinaryInputDisabled, SlotRole.Input),
                },
                LocalPredicates: new[] { SlotPredicates.AtLeastOne(new[] { SlotRole.Input, SlotRole.Output }) },
                Resolver: FakeResolver.Empty));

        return new Registration("Painter", new Layout[] { layout }, Array.Empty<Expansion>());
    }

    public static Registration PainterRange()
    {
        var layout = new Layout.Static(
            LayoutId: "Painter.Default",
            Piece: new PieceSpec(
                BaseDefinitionId: "Painter",
                Role: PieceRole.Singleton,
                SlotSpecs: new SlotSpec[]
                {
                    new SlotSpec.Range("BuildingFluidJunction", "paint", BinaryInputDisabled, SlotRole.Input, AutoSkipInternal: true),
                },
                LocalPredicates: new[] { SlotPredicates.AtLeastOne(new[] { SlotRole.Input, SlotRole.Output }) },
                Resolver: new FakeResolver(new Dictionary<string, int> { ["BuildingFluidJunction"] = 3 })));

        return new Registration("Painter", new Layout[] { layout }, Array.Empty<Expansion>());
    }

    // AND-gate: one dynamic layout, expandable along ANY axis. A placed
    // singleton offers handles on all four sides; dragging one commits the
    // axis. Custom shrink callback moves the chain's sole output onto the
    // surviving end so a shrink never leaves the gate output-less.
    public static Registration AndGate()
    {
        var chain = new Layout.Dynamic(
            LayoutId: "LogicGateAnd.Chain",
            ConfigurableSingleton: AndGatePiece("LogicGateAnd_ConfigurableSingleton", PieceRole.Singleton, SlotRole.Output),
            Head: AndGatePiece("LogicGateAnd_Head", PieceRole.Head),
            Body: AndGatePiece("LogicGateAnd_Body", PieceRole.Body),
            Tail: AndGatePiece("LogicGateAnd_Tail", PieceRole.Tail),
            ChainPredicates: new IChainPredicate[]
            {
                ChainPredicates.AtLeastN(2, new[] { SlotRole.Input }),
                ChainPredicates.AtLeastN(1, new[] { SlotRole.Output }),
            });

        return new Registration(
            RegistrationId: "LogicGateAnd",
            Layouts: new Layout[] { chain },
            Expansions: new[]
            {
                Expand.Chain(
                    directions: new HashSet<Direction>(Directions.All),
                    layout: chain,
                    shrink: MoveOutputToSurvivor),
            });
    }

    // Custom ShrinkCarry: if the removed end piece carried an output and the
    // post-shrink chain now has none, move it onto the surviving end nearest
    // the removed piece, on the SAME slot id it occupied — so the player's
    // "which slot is the output" choice survives the shrink.
    private static ChainState MoveOutputToSurvivor(ChainState before, ChainState afterDefault, PieceState removed)
    {
        bool removedHadOutput = removed.SlotRoles.Values.Contains(SlotRole.Output);
        bool chainHasOutput = afterDefault.Pieces.Any(p => p.SlotRoles.Values.Contains(SlotRole.Output));
        if (!removedHadOutput || chainHasOutput) return afterDefault;

        // The removed piece's PieceIndex tells us which end was dragged. 0 =
        // head end → the new end is at index 0; otherwise → the new end is
        // at the last index. For the 2→1 (singleton) case both collapse to 0.
        int targetIndex = removed.PieceIndex == 0 ? 0 : afterDefault.Pieces.Count - 1;
        var end = afterDefault.Pieces[targetIndex];

        var removedOutputSlotIds = removed.SlotRoles
            .Where(kv => kv.Value == SlotRole.Output)
            .Select(kv => kv.Key)
            .ToHashSet();
        var target = end.ExpandedSlots.FirstOrDefault(s =>
                removedOutputSlotIds.Contains(s.Id) && s.AllowedRoles.Contains(SlotRole.Output))
            ?? end.ExpandedSlots.FirstOrDefault(s => s.AllowedRoles.Contains(SlotRole.Output));
        if (target is null) return afterDefault;

        var roles = new Dictionary<string, SlotRole>(end.SlotRoles) { [target.Id] = SlotRole.Output };
        var pieces = afterDefault.Pieces.ToList();
        pieces[targetIndex] = end with { SlotRoles = roles };
        return afterDefault with { Pieces = pieces };
    }

    // Cutter: layouts as objects, then referenced directly in the sequences.
    // Square: Half →(East)→ Full, full gated on FullCutter research.
    // Hex:    Half →(East)→ Hex3 →(East)→ Hex6, each hex step its own research.
    // Half is shared (valid in hex too). Per-step research means unlocking
    // Hex6 alone lets Half expand straight to Hex6, skipping the locked Hex3
    // — without the author writing a Half→Hex6 sequence.
    public static Registration Cutter()
    {
        var half = CutterLayout("Cutter.Half", "HalfCutter", outputs: 2);
        var full = CutterLayout("Cutter.Full", "FullCutter", outputs: 4);
        var hex3 = CutterLayout("Cutter.Hex3", "Hex3Cutter", outputs: 3);
        var hex6 = CutterLayout("Cutter.Hex6", "Hex6Cutter", outputs: 6);

        return new Registration(
            RegistrationId: "Cutter",
            Layouts: new Layout[] { half, full, hex3, hex6 },
            Expansions: new[]
            {
                Expand.Sequence(Direction.East,
                    steps: new[]
                    {
                        Expand.Step(half),
                        Expand.Step(full, ExpansionConditions.RequiresResearch("FullCutter")),
                    },
                    conditions: new[] { ExpansionConditions.RequiresMode(GameMode.Default) }),

                Expand.Sequence(Direction.East,
                    steps: new[]
                    {
                        Expand.Step(half),
                        Expand.Step(hex3, ExpansionConditions.RequiresResearch("Hex3Cutter")),
                        Expand.Step(hex6, ExpansionConditions.RequiresResearch("Hex6Cutter")),
                    },
                    conditions: new[] { ExpansionConditions.RequiresMode(GameMode.Hex) }),
            });
    }

    // ---- helpers ----------------------------------------------------------

    private static Layout.Static CutterLayout(string layoutId, string defId, int outputs)
    {
        var slots = new SlotSpec[outputs];
        for (int i = 0; i < outputs; i++)
            slots[i] = new SlotSpec.Single($"out_{i}", BinaryOutputDisabled, SlotRole.Output);

        return new Layout.Static(
            LayoutId: layoutId,
            Piece: new PieceSpec(
                BaseDefinitionId: defId,
                Role: PieceRole.Singleton,
                SlotSpecs: slots,
                LocalPredicates: new[] { SlotPredicates.AtLeastOne(new[] { SlotRole.Output }) },
                Resolver: FakeResolver.Empty));
    }

    private static PieceSpec AndGatePiece(string defId, PieceRole role, SlotRole defaultC = SlotRole.Input) => new(
        BaseDefinitionId: defId,
        Role:             role,
        SlotSpecs: new SlotSpec[]
        {
            new SlotSpec.Single("sig_A", FullSignalRoles, SlotRole.Input),
            new SlotSpec.Single("sig_B", FullSignalRoles, SlotRole.Input),
            new SlotSpec.Single("sig_C", FullSignalRoles, defaultC),
        },
        LocalPredicates: Array.Empty<ISlotPredicate>(),
        Resolver:        FakeResolver.Empty);

    private static readonly SlotRole[] BinaryInputDisabled  = { SlotRole.Input, SlotRole.Disabled };
    private static readonly SlotRole[] BinaryOutputDisabled = { SlotRole.Output, SlotRole.Disabled };
    private static readonly SlotRole[] FullSignalRoles =
        { SlotRole.Input, SlotRole.Output, SlotRole.Disabled, SlotRole.Enabled };
}
