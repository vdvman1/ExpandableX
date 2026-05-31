// PROTOTYPE TUI — throwaway shell over Encoding.cs.
// Two modes: explosion view (variant id list, per layout/piece) and live
// view (slot editing + drag-handle expansion with validity/gating preview).

using ExpandableX.Prototype.VariantEncoding;

const string ResearchKeyLetters = "abcd";   // toggle keys for scenario.ResearchKeys

var scenarios = Scenarios.All;
int scenarioIdx = 0;
ViewMode mode = ViewMode.Live;

var sessions = scenarios.Select(s => new LiveSession(s.Registration)).ToList();
int focusPiece = 0;
int focusSlot = 0;
string? lastError = null;

while (true)
{
    var scenario = scenarios[scenarioIdx];
    var session = sessions[scenarioIdx];
    if (mode == ViewMode.Live) RenderLive(scenario, session, focusPiece, focusSlot, lastError);
    else RenderExplosion(scenario);
    lastError = null;

    Console.Write("\n> ");
    var key = Console.ReadKey(intercept: true);
    Console.WriteLine();

    if (key.KeyChar == 'q') break;
    if (key.KeyChar == 'n') { scenarioIdx = (scenarioIdx + 1) % scenarios.Count; ResetFocus(); continue; }
    if (key.KeyChar == 'p') { scenarioIdx = (scenarioIdx - 1 + scenarios.Count) % scenarios.Count; ResetFocus(); continue; }
    if (key.KeyChar == 't') { mode = mode == ViewMode.Live ? ViewMode.Explosion : ViewMode.Live; continue; }
    if (key.KeyChar == 'r') { sessions[scenarioIdx] = new LiveSession(scenarios[scenarioIdx].Registration); ResetFocus(); continue; }

    if (mode == ViewMode.Live) HandleLiveKey(key.KeyChar, scenarios[scenarioIdx], sessions[scenarioIdx]);
}

return;

void ResetFocus() { focusPiece = 0; focusSlot = 0; }

void HandleLiveKey(char keyChar, Scenario scenario, LiveSession session)
{
    var chain = session.Chain;
    var piece = chain.Pieces[focusPiece];

    switch (keyChar)
    {
        case 'h':
            focusPiece = Math.Max(0, focusPiece - 1);
            focusSlot = Math.Min(focusSlot, chain.Pieces[focusPiece].ExpandedSlots.Count - 1);
            break;
        case 'l':
            focusPiece = Math.Min(chain.Pieces.Count - 1, focusPiece + 1);
            focusSlot = Math.Min(focusSlot, chain.Pieces[focusPiece].ExpandedSlots.Count - 1);
            break;
        case 'k': focusSlot = Math.Max(0, focusSlot - 1); break;
        case 'j': focusSlot = Math.Min(piece.ExpandedSlots.Count - 1, focusSlot + 1); break;

        case 'i': TrySet(session, SlotRole.Input);    break;
        case 'o': TrySet(session, SlotRole.Output);   break;
        case 'e': TrySet(session, SlotRole.Enabled);  break;
        case 'x': TrySet(session, SlotRole.Disabled); break;

        case 'm':
            session.Mode = session.Mode == GameMode.Default ? GameMode.Hex : GameMode.Default;
            break;

        default:
            int researchIdx = ResearchKeyLetters.IndexOf(keyChar);
            if (researchIdx >= 0 && researchIdx < scenario.ResearchKeys.Count)
            {
                var id = scenario.ResearchKeys[researchIdx];
                if (!session.Researched.Add(id)) session.Researched.Remove(id);
            }
            else if (char.IsDigit(keyChar))
            {
                TryDrag(session, keyChar - '1');
            }
            break;
    }
}

void TrySet(LiveSession session, SlotRole role)
{
    var chain = session.Chain;
    var slot = chain.Pieces[focusPiece].ExpandedSlots[focusSlot];

    if (!slot.AllowedRoles.Contains(role)) { lastError = $"{role} not in allowed set for {slot.Id}"; return; }

    var match = ChainValidator.OptionsFor(chain, focusPiece, slot.Id).First(o => o.Role == role);
    if (!match.IsValid) { lastError = $"would violate — {match.InvalidReason}"; return; }

    session.Chain = ChainBuilder.SetRole(chain, focusPiece, slot.Id, role);
}

void TryDrag(LiveSession session, int index)
{
    var drags = ExpansionEngine.AvailableDrags(session.Reg, session.Chain, session.Context);
    if (index < 0 || index >= drags.Count) { lastError = "no such drag handle"; return; }

    var drag = drags[index];
    if (!drag.IsAvailable || drag.Result is null) { lastError = $"drag blocked — {drag.BlockedReason}"; return; }

    session.Chain = drag.Result;
    ResetFocus();
}

static void RenderLive(Scenario scenario, LiveSession session, int focusPiece, int focusSlot, string? lastError)
{
    Console.Clear();
    var chain = session.Chain;
    Header(scenario, "LIVE");

    var report = ChainValidator.Validate(chain);

    string axis = chain.Axis is { } a ? $"{a}↔{a.Opposite()}" : "(uncommitted)";
    Console.WriteLine($"\x1b[1mLayout:\x1b[0m {chain.Layout.LayoutId} \x1b[2m({LayoutKind(chain.Layout)})\x1b[0m   " +
                      $"\x1b[1maxis:\x1b[0m {axis}   \x1b[1mmode:\x1b[0m {session.Mode}");
    if (scenario.ResearchKeys.Count > 0)
    {
        var bits = scenario.ResearchKeys.Select((id, i) =>
        {
            bool on = session.Researched.Contains(id);
            string color = on ? "\x1b[32m" : "\x1b[2m";
            return $"{color}[{ResearchKeyLetters[i]}] {id}={(on ? "on" : "off")}\x1b[0m";
        });
        Console.WriteLine($"\x1b[1mresearch:\x1b[0m {string.Join("  ", bits)}");
    }
    Console.WriteLine();

    Bold("Chain:");
    for (int pi = 0; pi < chain.Pieces.Count; pi++)
    {
        var piece = chain.Pieces[pi];
        bool pieceFocused = pi == focusPiece;
        Console.WriteLine($" {(pieceFocused ? "▶" : " ")} {piece.DisplayLabel} \x1b[2m({piece.Spec.BaseDefinitionId})\x1b[0m");
        for (int si = 0; si < piece.ExpandedSlots.Count; si++)
        {
            var slot = piece.ExpandedSlots[si];
            var role = piece.SlotRoles[slot.Id];
            bool slotFocused = pieceFocused && si == focusSlot;
            Console.WriteLine($"{(slotFocused ? "  ▶" : "   ")} {slot.Id} = {ColorForRole(role)}{RoleAlphabet.Encode(role)}\x1b[0m  \x1b[2m(allowed {{{string.Join(",", slot.AllowedRoles.Select(RoleAlphabet.Encode))}}})\x1b[0m");
        }
        Console.WriteLine($"     \x1b[2mvariant id: {piece.DefinitionId}\x1b[0m");
    }
    Console.WriteLine();

    var focusedPiece = chain.Pieces[focusPiece];
    var focusedSlot = focusedPiece.ExpandedSlots[focusSlot];

    Bold($"Options for {focusedPiece.DisplayLabel}.{focusedSlot.Id}:");
    foreach (var opt in ChainValidator.OptionsFor(chain, focusPiece, focusedSlot.Id))
    {
        string keyChar = KeyForRole(opt.Role);
        string label = opt.Role.ToString();
        if (opt.IsCurrent)
            Console.WriteLine($"  \x1b[36m[{keyChar}] {label}\x1b[0m  \x1b[36m(current)\x1b[0m");
        else if (opt.IsValid)
            Console.WriteLine($"  \x1b[32m[{keyChar}] {label}\x1b[0m  available");
        else
            Console.WriteLine($"  \x1b[2m[{keyChar}] {label}\x1b[0m  \x1b[31m× {opt.InvalidReason}\x1b[0m");
    }
    Console.WriteLine();

    var drags = ExpansionEngine.AvailableDrags(session.Reg, chain, session.Context);
    Bold("Drag handles:");
    if (drags.Count == 0)
    {
        Dim("  (none — no expansion applies from this layout in this mode)");
    }
    else
    {
        for (int i = 0; i < drags.Count; i++)
        {
            var d = drags[i];
            string verb = d.Kind == DragKind.Expand ? "expand" : "shrink";
            string head = $"{d.Handle,-5} {verb} → {d.TargetDescription}";
            if (d.IsAvailable)
                Console.WriteLine($"  \x1b[32m[{i + 1}] {head}\x1b[0m  available");
            else
                Console.WriteLine($"  \x1b[2m[{i + 1}] {head}\x1b[0m  \x1b[31m× {d.BlockedReason}\x1b[0m");
        }
    }
    Console.WriteLine();

    if (report.IsValid)
    {
        Console.WriteLine("\x1b[32m✓ chain valid\x1b[0m");
    }
    else
    {
        Console.WriteLine("\x1b[31m✗ chain INVALID\x1b[0m");
        foreach (var f in report.LocalFailures) Console.WriteLine($"  \x1b[31m{f}\x1b[0m");
        foreach (var f in report.ChainFailures) Console.WriteLine($"  \x1b[31m{f}\x1b[0m");
    }
    Console.WriteLine();

    if (lastError is not null) Console.WriteLine($"\x1b[33m! {lastError}\x1b[0m\n");

    Bold("Keys (live):");
    Console.WriteLine("  \x1b[1m[h/l]\x1b[0m focus piece   \x1b[1m[k/j]\x1b[0m focus slot   \x1b[1m[i/o/e/x]\x1b[0m Input/Output/Enabled/Disabled");
    string researchHint = scenario.ResearchKeys.Count > 0 ? "   \x1b[1m[a/b/c]\x1b[0m toggle research" : "";
    Console.WriteLine($"  \x1b[1m[1-9]\x1b[0m pull drag handle   \x1b[1m[m]\x1b[0m toggle game mode{researchHint}");
    Console.WriteLine("  \x1b[1m[r]\x1b[0m reset   \x1b[1m[t]\x1b[0m explosion view   \x1b[1m[n/p]\x1b[0m scenario   \x1b[1m[q]\x1b[0m quit");
}

static void RenderExplosion(Scenario scenario)
{
    Console.Clear();
    Header(scenario, "EXPLOSION");

    var reg = scenario.Registration;
    Bold($"Registration '{reg.RegistrationId}' — {reg.Layouts.Count} layout(s), {reg.Expansions.Count} expansion(s)");

    foreach (var layout in reg.Layouts)
    {
        Console.WriteLine();
        Bold($"╶ Layout {layout.LayoutId} ({LayoutKind(layout)}):");
        foreach (var pe in VariantEncoder.ExplodeLayout(layout))
        {
            Console.WriteLine($"   • {LabelFor(pe.Spec)} [{pe.Spec.BaseDefinitionId}] — {pe.ExpandedSlots.Count} slot(s), {pe.Variants.Count} kept, {pe.Pruned.Count} pruned");
            foreach (var v in pe.Variants)
                Console.WriteLine($"       \x1b[32m{v.DefinitionId}\x1b[0m");
            foreach (var pc in pe.Pruned)
                Console.WriteLine($"       \x1b[31m{pc.CandidateId}\x1b[0m  \x1b[2m← {pc.PrunedBy}\x1b[0m");
        }
        foreach (var g in layout.ChainPredicatesOf())
            Console.WriteLine($"     \x1b[2m{g.Describe()} (runtime-only)\x1b[0m");
    }

    if (reg.Expansions.Count > 0)
    {
        Console.WriteLine();
        Bold("Expansions:");
        foreach (var exp in reg.Expansions)
        {
            string conds = exp.Conditions.Count == 0 ? "always" : string.Join(" & ", exp.Conditions.Select(c => c.Describe()));
            string body = exp switch
            {
                Expansion.Sequence s => "seq[" + string.Join(" → ", s.Steps.Select(DescribeStep)) + "]",
                Expansion.Chain c => $"chain[{c.Layout.LayoutId}] axes {{{string.Join(",", c.Directions)}}}",
                _ => exp.ToString() ?? "",
            };
            Console.WriteLine($"  {body}  \x1b[2m[{conds}]\x1b[0m");
        }
    }

    Console.WriteLine();
    Bold("Keys (explosion):");
    Console.WriteLine("  \x1b[1m[t]\x1b[0m to live view   \x1b[1m[n/p]\x1b[0m scenario   \x1b[1m[q]\x1b[0m quit");
}

static string DescribeStep(SequenceStep s) =>
    s.Layout.LayoutId + (s.Conditions.Count > 0 ? $"({string.Join(",", s.Conditions.Select(c => c.Describe()))})" : "");

static string LabelFor(PieceSpec spec) => spec.Role switch
{
    PieceRole.Singleton => "SINGLETON",
    PieceRole.Head      => "HEAD",
    PieceRole.Body      => "BODY",
    PieceRole.Tail      => "TAIL",
    _                   => spec.Role.ToString(),
};

static string LayoutKind(Layout layout) => layout switch
{
    Layout.Static  => "static",
    Layout.Dynamic => "dynamic",
    _              => "unknown",
};

static string KeyForRole(SlotRole role) => role switch
{
    SlotRole.Input    => "i",
    SlotRole.Output   => "o",
    SlotRole.Enabled  => "e",
    SlotRole.Disabled => "x",
    _                 => "?",
};

static string ColorForRole(SlotRole role) => role switch
{
    SlotRole.Disabled => "\x1b[31m",
    SlotRole.Enabled  => "\x1b[35m",
    _                 => "\x1b[32m",
};

static void Header(Scenario scenario, string modeTag)
{
    Bold($"== ExpandableX variant-encoding prototype  [{modeTag}]  ==");
    Dim($"Scenario: {scenario.Name}");
    Dim($"Q: {scenario.Question}");
    Console.WriteLine();
}

static void Bold(string text) => Console.WriteLine($"\x1b[1m{text}\x1b[0m");
static void Dim(string text) => Console.WriteLine($"\x1b[2m{text}\x1b[0m");

internal enum ViewMode { Explosion, Live }

internal sealed class LiveSession
{
    public Registration Reg { get; }
    public ChainState Chain { get; set; }
    public GameMode Mode { get; set; } = GameMode.Default;
    public HashSet<string> Researched { get; } = new();
    public ExpansionContext Context => new(Mode, Researched);

    public LiveSession(Registration reg)
    {
        Reg = reg;
        // ExpandableX-Core doesn't place buildings; the game does. Start
        // from the registration's first layout to have something to drive.
        Chain = ChainBuilder.Initial(reg.Layouts[0]);
    }
}
