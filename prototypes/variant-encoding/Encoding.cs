// PROTOTYPE — throwaway. The shape proposed here will be lifted into
// ExpandableX-Core if validated. See README.md for the question being answered.

using System.Text;

namespace ExpandableX.Prototype.VariantEncoding;

public enum SlotRole
{
    Input,
    Output,
    Disabled,
    // Enabled = junction acting as both input AND output simultaneously.
    Enabled,
}

// ---------- Encoding ----------
public static class RoleAlphabet
{
    public static char Encode(SlotRole role) => role switch
    {
        SlotRole.Input    => 'I',
        SlotRole.Output   => 'O',
        SlotRole.Disabled => 'D',
        SlotRole.Enabled  => 'E',
        _ => throw new ArgumentOutOfRangeException(nameof(role)),
    };

    public static SlotRole Decode(char c) => c switch
    {
        'I' => SlotRole.Input,
        'O' => SlotRole.Output,
        'D' => SlotRole.Disabled,
        'E' => SlotRole.Enabled,
        _ => throw new ArgumentOutOfRangeException(nameof(c), $"unknown role char '{c}'"),
    };
}

// ---------- Slot model ----------
public sealed record Slot(
    string Id,
    IReadOnlyList<SlotRole> AllowedRoles,
    SlotRole DefaultRole)
{
    public override string ToString() =>
        $"{Id} allowed={{{string.Join(",", AllowedRoles.Select(RoleAlphabet.Encode))}}} default={RoleAlphabet.Encode(DefaultRole)}";
}

// ---------- Range / bulk registration ----------
public abstract record SlotSpec
{
    public abstract IReadOnlyList<Slot> Expand(IConnectorCountResolver resolver);

    public sealed record Single(string Id, IReadOnlyList<SlotRole> AllowedRoles, SlotRole DefaultRole) : SlotSpec
    {
        public override IReadOnlyList<Slot> Expand(IConnectorCountResolver _) =>
            new[] { new Slot(Id, AllowedRoles, DefaultRole) };
    }

    public sealed record Range(
        string ConnectorTypeKey,
        string IdPrefix,
        IReadOnlyList<SlotRole> AllowedRoles,
        SlotRole DefaultRole,
        bool AutoSkipInternal = true) : SlotSpec
    {
        public override IReadOnlyList<Slot> Expand(IConnectorCountResolver resolver)
        {
            int count = resolver.CountVisible(ConnectorTypeKey, AutoSkipInternal);
            var slots = new List<Slot>(count);
            for (int i = 0; i < count; i++)
                slots.Add(new Slot($"{IdPrefix}_{i}", AllowedRoles, DefaultRole));
            return slots;
        }
    }
}

public interface IConnectorCountResolver
{
    int CountVisible(string connectorTypeKey, bool autoSkipInternal);
}

public sealed class FakeResolver(IReadOnlyDictionary<string, int> visibleCounts) : IConnectorCountResolver
{
    public int CountVisible(string connectorTypeKey, bool autoSkipInternal) =>
        visibleCounts.TryGetValue(connectorTypeKey, out int n) ? n : 0;

    public static readonly FakeResolver Empty = new(new Dictionary<string, int>());
}

// ---------- Local predicates (per-piece, prune at variant-explosion time) ----------
public interface ISlotPredicate
{
    bool IsValid(IReadOnlyDictionary<string, SlotRole> pieceState);
    string Describe();
}

public static class SlotPredicates
{
    public static ISlotPredicate AtLeastN(int n, IEnumerable<SlotRole> inRoles) =>
        new AtLeastNAnywhereImpl(n, inRoles.ToHashSet());

    public static ISlotPredicate AtLeastN(int n, IEnumerable<string> slotIds, IEnumerable<SlotRole> inRoles) =>
        new AtLeastNAmongImpl(n, slotIds.ToList(), inRoles.ToHashSet());

    public static ISlotPredicate AtLeastOne(IEnumerable<SlotRole> inRoles) => AtLeastN(1, inRoles);
    public static ISlotPredicate AtLeastOne(IEnumerable<string> slotIds, IEnumerable<SlotRole> inRoles) => AtLeastN(1, slotIds, inRoles);

    public static ISlotPredicate Custom(Func<IReadOnlyDictionary<string, SlotRole>, bool> predicate, string description) =>
        new CustomImpl(predicate, description);

    private static string FormatRoles(HashSet<SlotRole> roles) =>
        "{" + string.Join(",", roles.OrderBy(r => (int)r).Select(RoleAlphabet.Encode)) + "}";

    private sealed class AtLeastNAnywhereImpl(int n, HashSet<SlotRole> roles) : ISlotPredicate
    {
        public bool IsValid(IReadOnlyDictionary<string, SlotRole> state) => state.Values.Count(roles.Contains) >= n;
        public string Describe() => $"local: AtLeast {n} in {FormatRoles(roles)} (any slot)";
    }

    private sealed class AtLeastNAmongImpl(int n, IReadOnlyList<string> ids, HashSet<SlotRole> roles) : ISlotPredicate
    {
        public bool IsValid(IReadOnlyDictionary<string, SlotRole> state) =>
            ids.Count(id => state.TryGetValue(id, out var r) && roles.Contains(r)) >= n;
        public string Describe() => $"local: AtLeast {n} in {FormatRoles(roles)} among {{{string.Join(",", ids)}}}";
    }

    private sealed class CustomImpl(Func<IReadOnlyDictionary<string, SlotRole>, bool> predicate, string description) : ISlotPredicate
    {
        public bool IsValid(IReadOnlyDictionary<string, SlotRole> state) => predicate(state);
        public string Describe() => "local: " + description;
    }
}

// ---------- Chain predicates (span the whole chain, runtime-only) ----------
public interface IChainPredicate
{
    bool IsValid(ChainState chain);
    string Describe();
}

public static class ChainPredicates
{
    public static IChainPredicate AtLeastN(int n, IEnumerable<SlotRole> inRoles) => new AtLeastNChainImpl(n, inRoles.ToHashSet());
    public static IChainPredicate AtLeastOne(IEnumerable<SlotRole> inRoles) => AtLeastN(1, inRoles);
    public static IChainPredicate Custom(Func<ChainState, bool> predicate, string description) => new CustomChainImpl(predicate, description);

    private static string FormatRoles(HashSet<SlotRole> roles) =>
        "{" + string.Join(",", roles.OrderBy(r => (int)r).Select(RoleAlphabet.Encode)) + "}";

    private sealed class AtLeastNChainImpl(int n, HashSet<SlotRole> roles) : IChainPredicate
    {
        public bool IsValid(ChainState chain) => chain.Pieces.Sum(p => p.SlotRoles.Values.Count(roles.Contains)) >= n;
        public string Describe() => $"chain: AtLeast {n} in {FormatRoles(roles)} across whole chain";
    }

    private sealed class CustomChainImpl(Func<ChainState, bool> predicate, string description) : IChainPredicate
    {
        public bool IsValid(ChainState chain) => predicate(chain);
        public string Describe() => "chain: " + description;
    }
}

// ---------- Piece ----------
public enum PieceRole { Singleton, Head, Body, Tail }

public sealed record PieceSpec(
    string BaseDefinitionId,
    PieceRole Role,
    IReadOnlyList<SlotSpec> SlotSpecs,
    IReadOnlyList<ISlotPredicate> LocalPredicates,
    IConnectorCountResolver Resolver);

// ---------- Layout (registration-time spec) ----------
public abstract record Layout(string LayoutId)
{
    public sealed record Static(string LayoutId, PieceSpec Piece) : Layout(LayoutId);

    public sealed record Dynamic(
        string LayoutId,
        PieceSpec? ConfigurableSingleton,
        PieceSpec Head,
        PieceSpec Body,
        PieceSpec Tail,
        IReadOnlyList<IChainPredicate> ChainPredicates) : Layout(LayoutId);
}

public static class LayoutExtensions
{
    public static IEnumerable<PieceSpec> EnumeratePieceSpecs(this Layout layout) => layout switch
    {
        Layout.Static s => new[] { s.Piece },
        Layout.Dynamic d => d.ConfigurableSingleton is null
            ? new[] { d.Head, d.Body, d.Tail }
            : new[] { d.ConfigurableSingleton, d.Head, d.Body, d.Tail },
        _ => Enumerable.Empty<PieceSpec>(),
    };

    public static IReadOnlyList<IChainPredicate> ChainPredicatesOf(this Layout layout) => layout switch
    {
        Layout.Dynamic d => d.ChainPredicates,
        _ => Array.Empty<IChainPredicate>(),
    };
}

// ---------- Registration (umbrella — one per source MetaBuildingDefinition) ----------
//
// ExpandableX-Core governs how an ALREADY-PLACED building expands. It does
// not decide initial placement. A Registration declares the layouts and the
// directional Expansions that move between them.
public sealed record Registration(
    string RegistrationId,
    IReadOnlyList<Layout> Layouts,
    IReadOnlyList<Expansion> Expansions);

public enum GameMode { Default, Hex }

public enum Direction { North, East, South, West }

public static class Directions
{
    public static readonly IReadOnlyList<Direction> All =
        new[] { Direction.North, Direction.East, Direction.South, Direction.West };

    public static Direction Opposite(this Direction d) => d switch
    {
        Direction.North => Direction.South,
        Direction.South => Direction.North,
        Direction.East  => Direction.West,
        Direction.West  => Direction.East,
        _ => throw new ArgumentOutOfRangeException(nameof(d)),
    };
}

// ---------- Expansion conditions ----------
public sealed record ExpansionContext(GameMode Mode, IReadOnlySet<string> Researched);

public interface IExpansionCondition
{
    bool IsMet(ExpansionContext ctx);
    string Describe();
}

public static class ExpansionConditions
{
    public static IExpansionCondition RequiresMode(GameMode mode) => new ModeImpl(mode);
    public static IExpansionCondition RequiresResearch(string researchId) => new ResearchImpl(researchId);
    public static IExpansionCondition Custom(Func<ExpansionContext, bool> predicate, string description) => new CustomImpl(predicate, description);

    private sealed class ModeImpl(GameMode mode) : IExpansionCondition
    {
        public bool IsMet(ExpansionContext ctx) => ctx.Mode == mode;
        public string Describe() => $"requires {mode} mode";
    }

    private sealed class ResearchImpl(string id) : IExpansionCondition
    {
        public bool IsMet(ExpansionContext ctx) => ctx.Researched.Contains(id);
        public string Describe() => $"requires research '{id}'";
    }

    private sealed class CustomImpl(Func<ExpansionContext, bool> predicate, string description) : IExpansionCondition
    {
        public bool IsMet(ExpansionContext ctx) => predicate(ctx);
        public string Describe() => description;
    }
}

// ---------- Carry-over callbacks ----------
//
// State must transfer when a drag changes the shape. The engine VALIDATES
// the carried result and refuses the drag if invalid, so the player can
// never reach an invalid state through expansion.
//
//  - Sequence (static): CarryState maps old single-piece state to the new
//    layout's slots.
//  - Chain grow: convert the dragged end (head/tail) into a body and add a
//    fresh end piece. The existing pieces keep their state; the new end
//    starts at defaults. A GrowCarry callback can override.
//  - Chain shrink: the dragged end piece is removed and folded into its
//    neighbour, which becomes the new end. The removed piece's roles are
//    dropped by default — a ShrinkCarry callback decides how to combine
//    (e.g. move the chain's sole output onto the surviving end).
public delegate ChainState CarryState(ChainState from, ChainState toAtDefaults);
public delegate ChainState GrowCarry(ChainState before, ChainState afterDefault);
public delegate ChainState ShrinkCarry(ChainState before, ChainState afterDefault, PieceState removed);

public static class CarryStateDefaults
{
    public static ChainState MatchById(ChainState from, ChainState toAtDefaults)
    {
        var newPieces = toAtDefaults.Pieces.ToList();
        int shared = Math.Min(newPieces.Count, from.Pieces.Count);
        for (int pi = 0; pi < shared; pi++)
            newPieces[pi] = CarryPieceById(from.Pieces[pi], newPieces[pi]);
        return toAtDefaults with { Pieces = newPieces };
    }

    public static PieceState CarryPieceById(PieceState from, PieceState to)
    {
        var roles = new Dictionary<string, SlotRole>(to.SlotRoles);
        foreach (var slot in to.ExpandedSlots)
            if (from.SlotRoles.TryGetValue(slot.Id, out var r) && slot.AllowedRoles.Contains(r))
                roles[slot.Id] = r;
        return to with { SlotRoles = roles };
    }
}

// ---------- Expansion (directional, declared with layout objects) ----------
public sealed record SequenceStep(Layout Layout, IReadOnlyList<IExpansionCondition> Conditions);

public abstract record Expansion(Direction Direction, IReadOnlyList<IExpansionCondition> Conditions)
{
    // Finite progression of static layouts (cutter). Per-step conditions
    // gate individual layouts; locked steps are skipped when dragging.
    public sealed record Sequence(
        Direction Direction,
        IReadOnlyList<SequenceStep> Steps,
        IReadOnlyList<IExpansionCondition> Conditions,
        CarryState? Carry = null) : Expansion(Direction, Conditions);

    // Unbounded chain along one of several allowed axes (AND gate). A
    // singleton can commit to any Direction in Directions; once committed
    // the two ends of that axis carry the handles.
    public sealed record Chain(
        IReadOnlySet<Direction> Directions,
        Layout.Dynamic Layout,
        IReadOnlyList<IExpansionCondition> Conditions,
        GrowCarry? Grow = null,
        ShrinkCarry? Shrink = null) : Expansion(Direction.North, Conditions);
}

public static class Expand
{
    private static readonly IReadOnlyList<IExpansionCondition> None = Array.Empty<IExpansionCondition>();

    public static SequenceStep Step(Layout layout, params IExpansionCondition[] conditions) =>
        new(layout, conditions);

    public static Expansion Sequence(Direction direction, IReadOnlyList<SequenceStep> steps,
        IReadOnlyList<IExpansionCondition>? conditions = null, CarryState? carry = null) =>
        new Expansion.Sequence(direction, steps, conditions ?? None, carry);

    public static Expansion Chain(IReadOnlySet<Direction> directions, Layout.Dynamic layout,
        IReadOnlyList<IExpansionCondition>? conditions = null, GrowCarry? grow = null, ShrinkCarry? shrink = null) =>
        new Expansion.Chain(directions, layout, conditions ?? None, grow, shrink);
}

// ---------- Drag handles (runtime, derived from Expansions) ----------
public enum DragKind { Expand, Shrink }

public sealed record DragAction(
    Direction Handle,
    DragKind Kind,
    string TargetDescription,
    bool IsAvailable,
    string? BlockedReason,
    ChainState? Result);

public static class ExpansionEngine
{
    public static IReadOnlyList<DragAction> AvailableDrags(Registration reg, ChainState current, ExpansionContext ctx)
    {
        var actions = new List<DragAction>();
        foreach (var exp in reg.Expansions)
        {
            string? unmet = FirstUnmet(exp.Conditions, ctx);
            switch (exp)
            {
                case Expansion.Sequence seq: AddSequenceDrags(actions, seq, current, ctx, unmet); break;
                case Expansion.Chain ch:     AddChainDrags(actions, ch, current, unmet);          break;
            }
        }
        return actions;
    }

    // ---- sequences (per-step gating, skip locked) ----

    private static void AddSequenceDrags(List<DragAction> actions, Expansion.Sequence seq, ChainState current, ExpansionContext ctx, string? unmet)
    {
        int i = IndexOfStep(seq.Steps, current.Layout);
        if (i < 0) return;

        AddSequenceDrag(actions, seq, current, ctx, i, +1, DragKind.Expand, seq.Direction, unmet);
        AddSequenceDrag(actions, seq, current, ctx, i, -1, DragKind.Shrink, seq.Direction, unmet);
    }

    private static void AddSequenceDrag(List<DragAction> actions, Expansion.Sequence seq, ChainState current, ExpansionContext ctx,
        int from, int step, DragKind kind, Direction handle, string? seqUnmet)
    {
        int immediate = from + step;
        if (immediate < 0 || immediate >= seq.Steps.Count) return; // at the end of the sequence

        if (seqUnmet is not null)
        {
            actions.Add(new DragAction(handle, kind, seq.Steps[immediate].Layout.LayoutId, false, seqUnmet, null));
            return;
        }

        // Scan in `step` direction for the first reachable (all-conditions-met) step.
        var skipped = new List<string>();
        int target = -1;
        for (int k = immediate; k >= 0 && k < seq.Steps.Count; k += step)
        {
            if (FirstUnmet(seq.Steps[k].Conditions, ctx) is null) { target = k; break; }
            skipped.Add(seq.Steps[k].Layout.LayoutId);
        }

        if (target < 0)
        {
            // Nothing reachable that way — show the immediate step greyed with its reason.
            string reason = FirstUnmet(seq.Steps[immediate].Conditions, ctx) ?? "no reachable step";
            actions.Add(new DragAction(handle, kind, seq.Steps[immediate].Layout.LayoutId, false, reason, null));
            return;
        }

        var carry = seq.Carry ?? CarryStateDefaults.MatchById;
        var result = carry(current, ChainBuilder.Initial(seq.Steps[target].Layout));
        string desc = seq.Steps[target].Layout.LayoutId + (skipped.Count > 0 ? $" (skips {string.Join(",", skipped)})" : "");
        actions.Add(Finalize(handle, kind, desc, result, null));
    }

    // ---- chains (handle geometry: singleton = all dirs; committed = two ends) ----

    private static void AddChainDrags(List<DragAction> actions, Expansion.Chain ch, ChainState current, string? unmet)
    {
        if (current.Layout is not Layout.Dynamic dyn || dyn.LayoutId != ch.Layout.LayoutId) return;

        if (current.Axis is null)
        {
            // Singleton: an expand handle on every allowed direction.
            foreach (var dir in Directions.All.Where(ch.Directions.Contains))
            {
                var after = ChainBuilder.GrowAtEnd(current, dir);
                var result = ch.Grow?.Invoke(current, after) ?? after;
                actions.Add(Finalize(dir, DragKind.Expand, $"commit {dir} axis → head+tail", result, unmet));
            }
            return;
        }

        // Committed: the two ends of the axis.
        AddChainEnd(actions, ch, current, current.Axis.Value, unmet);
        AddChainEnd(actions, ch, current, current.Axis.Value.Opposite(), unmet);
    }

    private static void AddChainEnd(List<DragAction> actions, Expansion.Chain ch, ChainState current, Direction side, string? unmet)
    {
        // expand (grow this end)
        var grown = ChainBuilder.GrowAtEnd(current, side);
        var grownResult = ch.Grow?.Invoke(current, grown) ?? grown;
        actions.Add(Finalize(side, DragKind.Expand, $"grow {side} end ({grown.Pieces.Count} pieces)", grownResult, unmet));

        // shrink (fold this end inward)
        if (ChainBuilder.TryShrinkAtEnd(current, side, out var shrunk, out var removed))
        {
            var shrunkResult = ch.Shrink?.Invoke(current, shrunk, removed) ?? shrunk;
            string desc = shrunkResult.Axis is null ? "back to singleton" : $"shrink {side} end ({shrunk.Pieces.Count} pieces)";
            actions.Add(Finalize(side, DragKind.Shrink, desc, shrunkResult, unmet));
        }
    }

    // ---- shared ----

    private static DragAction Finalize(Direction handle, DragKind kind, string targetDesc, ChainState result, string? unmet)
    {
        var report = ChainValidator.Validate(result);
        string? blocked = unmet ?? (report.IsValid ? null : FirstFailure(report));
        return new DragAction(handle, kind, targetDesc, blocked is null, blocked, result);
    }

    private static string? FirstUnmet(IReadOnlyList<IExpansionCondition> conditions, ExpansionContext ctx)
    {
        foreach (var c in conditions)
            if (!c.IsMet(ctx)) return c.Describe();
        return null;
    }

    private static string FirstFailure(ValidationReport report) =>
        report.LocalFailures.Concat(report.ChainFailures).FirstOrDefault() ?? "invalid state";

    private static int IndexOfStep(IReadOnlyList<SequenceStep> steps, Layout current)
    {
        for (int i = 0; i < steps.Count; i++)
            if (steps[i].Layout.LayoutId == current.LayoutId) return i;
        return -1;
    }
}

// ---------- Runtime state ----------
public sealed record PieceState(
    int PieceIndex,
    PieceSpec Spec,
    IReadOnlyList<Slot> ExpandedSlots,
    IReadOnlyDictionary<string, SlotRole> SlotRoles)
{
    public string DefinitionId => VariantEncoder.EncodeId(Spec.BaseDefinitionId, ExpandedSlots, SlotRoles);
    public string DisplayLabel => Spec.Role switch
    {
        PieceRole.Singleton => "SINGLETON",
        PieceRole.Head      => "HEAD",
        PieceRole.Body      => $"BODY[{PieceIndex}]",
        PieceRole.Tail      => "TAIL",
        _                   => Spec.Role.ToString(),
    };
}

// Axis = the direction the HEAD end faces (tail faces the opposite). Null
// for a singleton / uncommitted building and for static layouts.
public sealed record ChainState(
    Layout Layout,
    IReadOnlyList<PieceState> Pieces,
    Direction? Axis);

// ---------- Chain build + edit (immutable transforms) ----------
public static class ChainBuilder
{
    public static ChainState Initial(Layout layout) => layout switch
    {
        Layout.Static s => new ChainState(s, new[] { MakePieceState(0, s.Piece) }, null),
        Layout.Dynamic d => new ChainState(d, new[] { MakePieceState(0, d.ConfigurableSingleton ?? d.Head) }, null),
        _ => throw new ArgumentOutOfRangeException(nameof(layout)),
    };

    public static ChainState SetRole(ChainState chain, int pieceIndex, string slotId, SlotRole role)
    {
        var oldPiece = chain.Pieces[pieceIndex];
        var newRoles = new Dictionary<string, SlotRole>(oldPiece.SlotRoles) { [slotId] = role };
        var newPieces = chain.Pieces.ToList();
        newPieces[pieceIndex] = oldPiece with { SlotRoles = newRoles };
        return chain with { Pieces = newPieces };
    }

    // Grow the chain by adding a fresh piece at the given end. The existing
    // end piece is reclassified (head/tail → body) but KEEPS its state; the
    // new end piece starts at defaults. For a singleton, `side` commits the
    // axis: the former singleton becomes the tail, a new head appears at the
    // dragged side.
    public static ChainState GrowAtEnd(ChainState chain, Direction side)
    {
        var d = (Layout.Dynamic)chain.Layout;

        if (chain.Axis is null)
        {
            var formerSingleton = chain.Pieces[0];
            var head = MakePieceState(0, d.Head);                       // new, defaults
            var tail = ReSpec(formerSingleton, d.Tail);                 // keep state
            return new ChainState(d, Reindex(new List<PieceState> { head, tail }), side);
        }

        var pieces = chain.Pieces.ToList();
        if (side == chain.Axis)
        {
            pieces[0] = ReSpec(pieces[0], d.Body);                      // old head → body
            pieces.Insert(0, MakePieceState(0, d.Head));                // new head, defaults
        }
        else
        {
            pieces[^1] = ReSpec(pieces[^1], d.Body);                    // old tail → body
            pieces.Add(MakePieceState(pieces.Count, d.Tail));           // new tail, defaults
        }
        return new ChainState(d, Reindex(pieces), chain.Axis);
    }

    // Shrink the chain by removing the piece at the given end, folding it
    // into its neighbour which becomes the new end. `removed` is handed to
    // a ShrinkCarry callback for combining logic.
    public static bool TryShrinkAtEnd(ChainState chain, Direction side, out ChainState result, out PieceState removed)
    {
        result = chain;
        removed = null!;
        if (chain.Axis is null) return false;            // singleton can't shrink

        var d = (Layout.Dynamic)chain.Layout;
        var pieces = chain.Pieces.ToList();
        bool atHead = side == chain.Axis;

        if (pieces.Count == 2)
        {
            // head+tail → singleton; the surviving end becomes the singleton.
            removed = atHead ? pieces[0] : pieces[1];
            var survivor = atHead ? pieces[1] : pieces[0];
            var singleton = ReSpec(survivor, d.ConfigurableSingleton ?? d.Head);
            result = new ChainState(d, Reindex(new List<PieceState> { singleton }), null);
            return true;
        }

        if (atHead)
        {
            removed = pieces[0];
            pieces.RemoveAt(0);
            pieces[0] = ReSpec(pieces[0], d.Head);                      // former body → head
        }
        else
        {
            removed = pieces[^1];
            pieces.RemoveAt(pieces.Count - 1);
            pieces[^1] = ReSpec(pieces[^1], d.Tail);                    // former body → tail
        }
        result = new ChainState(d, Reindex(pieces), chain.Axis);
        return true;
    }

    // Change a piece's spec (role/definition) while carrying its slot state
    // across by id. Used when a head/tail is reclassified as a body, etc.
    private static PieceState ReSpec(PieceState piece, PieceSpec newSpec)
    {
        var slots = newSpec.SlotSpecs.SelectMany(s => s.Expand(newSpec.Resolver)).ToList();
        var roles = slots.ToDictionary(s => s.Id, s => piece.SlotRoles.TryGetValue(s.Id, out var r) ? r : s.DefaultRole);
        return new PieceState(piece.PieceIndex, newSpec, slots, roles);
    }

    private static IReadOnlyList<PieceState> Reindex(List<PieceState> pieces)
    {
        for (int i = 0; i < pieces.Count; i++)
            pieces[i] = pieces[i] with { PieceIndex = i };
        return pieces;
    }

    private static PieceState MakePieceState(int index, PieceSpec spec)
    {
        var slots = spec.SlotSpecs.SelectMany(s => s.Expand(spec.Resolver)).ToList();
        var roles = slots.ToDictionary(s => s.Id, s => s.DefaultRole);
        return new PieceState(index, spec, slots, roles);
    }
}

// ---------- The chain validator ----------
public sealed record SlotOption(SlotRole Role, bool IsValid, string? InvalidReason, bool IsCurrent);

public sealed record ValidationReport(bool IsValid, IReadOnlyList<string> LocalFailures, IReadOnlyList<string> ChainFailures);

public static class ChainValidator
{
    public static ValidationReport Validate(ChainState chain)
    {
        var locals = new List<string>();
        foreach (var piece in chain.Pieces)
            foreach (var p in piece.Spec.LocalPredicates)
                if (!p.IsValid(piece.SlotRoles))
                    locals.Add($"{piece.DisplayLabel}: {p.Describe()}");

        var chainFails = new List<string>();
        foreach (var g in chain.Layout.ChainPredicatesOf())
            if (!g.IsValid(chain))
                chainFails.Add(g.Describe());

        return new ValidationReport(locals.Count == 0 && chainFails.Count == 0, locals, chainFails);
    }

    public static IReadOnlyList<SlotOption> OptionsFor(ChainState chain, int pieceIndex, string slotId)
    {
        var piece = chain.Pieces[pieceIndex];
        var slot = piece.ExpandedSlots.First(s => s.Id == slotId);
        var current = piece.SlotRoles[slotId];

        var options = new List<SlotOption>(slot.AllowedRoles.Count);
        foreach (var candidate in slot.AllowedRoles)
        {
            var hypothetical = ChainBuilder.SetRole(chain, pieceIndex, slotId, candidate);
            string? reason = null;

            var hypoPiece = hypothetical.Pieces[pieceIndex];
            foreach (var p in piece.Spec.LocalPredicates)
                if (!p.IsValid(hypoPiece.SlotRoles)) { reason = p.Describe(); break; }

            if (reason is null)
                foreach (var g in hypothetical.Layout.ChainPredicatesOf())
                    if (!g.IsValid(hypothetical)) { reason = g.Describe(); break; }

            options.Add(new SlotOption(candidate, reason is null, reason, candidate == current));
        }
        return options;
    }
}

// ---------- Variant encoding + per-piece explosion ----------
public sealed record Variant(string DefinitionId, IReadOnlyDictionary<string, SlotRole> SlotState);

public sealed record PrunedCandidate(string CandidateId, IReadOnlyDictionary<string, SlotRole> SlotState, string PrunedBy);

public sealed record PieceExpansion(
    PieceSpec Spec,
    IReadOnlyList<Slot> ExpandedSlots,
    IReadOnlyList<Variant> Variants,
    IReadOnlyList<PrunedCandidate> Pruned);

public static class VariantEncoder
{
    public const string IdSuffix = "_ExpandableXConfigurable";

    public static IReadOnlyList<PieceExpansion> ExplodeLayout(Layout layout) =>
        layout.EnumeratePieceSpecs().Select(ExplodePiece).ToList();

    public static PieceExpansion ExplodePiece(PieceSpec piece)
    {
        var slots = piece.SlotSpecs.SelectMany(s => s.Expand(piece.Resolver)).ToList();
        var variants = new List<Variant>();
        var pruned = new List<PrunedCandidate>();

        foreach (var combo in CartesianProduct(slots))
        {
            string? prunedBy = null;
            foreach (var p in piece.LocalPredicates)
                if (!p.IsValid(combo)) { prunedBy = p.Describe(); break; }

            string id = EncodeId(piece.BaseDefinitionId, slots, combo);
            if (prunedBy is null) variants.Add(new Variant(id, combo));
            else pruned.Add(new PrunedCandidate(id, combo, prunedBy));
        }

        return new PieceExpansion(piece, slots, variants, pruned);
    }

    public static string EncodeId(string baseDefinitionId, IReadOnlyList<Slot> slots, IReadOnlyDictionary<string, SlotRole> state)
    {
        if (slots.Count == 0) return baseDefinitionId;
        var sb = new StringBuilder(baseDefinitionId.Length + IdSuffix.Length + 1 + slots.Count);
        sb.Append(baseDefinitionId).Append(IdSuffix).Append('_');
        foreach (var slot in slots) sb.Append(RoleAlphabet.Encode(state[slot.Id]));
        return sb.ToString();
    }

    private static IEnumerable<Dictionary<string, SlotRole>> CartesianProduct(IReadOnlyList<Slot> slots)
    {
        if (slots.Count == 0) { yield return new Dictionary<string, SlotRole>(); yield break; }

        var indices = new int[slots.Count];
        while (true)
        {
            var combo = new Dictionary<string, SlotRole>(slots.Count);
            for (int i = 0; i < slots.Count; i++)
                combo[slots[i].Id] = slots[i].AllowedRoles[indices[i]];
            yield return combo;

            int k = slots.Count - 1;
            while (k >= 0)
            {
                indices[k]++;
                if (indices[k] < slots[k].AllowedRoles.Count) break;
                indices[k] = 0;
                k--;
            }
            if (k < 0) yield break;
        }
    }
}
