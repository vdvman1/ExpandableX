# Prototype — registration API, encoding, predicates, live validation

**Throwaway.** Validate the design here, then lift `Encoding.cs` into
`ExpandableX-Core` and delete this directory.

## The question

What does it look like to **register** a static layout and a dynamic
layout with this system, such that one registration call carries
everything — slots, local rules, chain rules — in one place?

`Scenarios.cs` is written as if it were the consumer mod's registration
code. If it reads awkwardly, the API is wrong.

Sub-problems folded in:

- **A.** Variant-id encoding for ≥3-state slots (now 4: `I`/`O`/`D`/`E`)
- **B.** Range / bulk slot registration
- **C. Local predicates** — per-piece, pruned at variant-explosion time
- **D. Chain predicates** — span the chain, declared on the dynamic
  layout (NOT a separate runtime object), enforced only at runtime
- **E.** Live slot editing with per-candidate validity preview
- **F. Layout transitions** — how a mod author declares that a building
  can move between layouts (cutter Half↔Full↔hex), gated by game mode /
  research, carrying slot state across the shape change

## Proposed answers (up for grilling)

### Alphabet — four roles

`Input=I`, `Output=O`, `Disabled=D`, `Enabled=E`. `Enabled` = a junction
acting as input **and** output at once (bidirectional pass-through). A
slot declares its allowed subset, so the painter's paint slots stay `{I,D}`
while AND-gate signal junctions use `{I,O,D,E}`.

### Registration — one `Layout` per building, two shapes

```
Layout.Static(registrationId, piece)
Layout.Dynamic(registrationId, configurableSingleton?, head, body, tail, chainPredicates)
```

`Layout.Dynamic` carries the chain predicates directly — they govern the
whole family, which is the mod author's concern, not a per-chain runtime
detail. The configurable singleton is optional (the 1-piece swap variant).

### Predicates — `AtLeastN`, generalised

- Local: `SlotPredicates.AtLeastN(n, roles)` (any slot in piece) or
  `AtLeastN(n, slotIds, roles)` (named slots). `AtLeastOne(...)` =
  `AtLeastN(1, ...)`. `Custom(...)` escape hatch.
- Chain: `ChainPredicates.AtLeastN(n, roles)` across the whole chain.

The AND-gate chain rule is `AtLeastN(2, [Input])` + `AtLeastN(1, [Output])`
— **no local rules on the pieces**, because a piece with all slots
Disabled is a valid spacer.

### Live editing — `ChainValidator.OptionsFor(chain, pieceIndex, slotId)`

Returns one `SlotOption` per allowed role: `IsValid` + first-violation
`InvalidReason` + `IsCurrent`. This is the primitive the dropdown UI uses
to enable / grey-out + tooltip / mark-selected each role.

### Registration umbrella + directional expansions

A `Registration` (one per source `MetaBuildingDefinition`) owns its
`Layout`s and the `Expansion`s that move between them. ExpandableX-Core
governs expansion of an **already-placed** building — it does NOT decide
initial placement, so there is no "initial layout" concept.

```
Registration(registrationId, layouts[], expansions[])
Expand.Sequence(direction, steps[], conditions?, carry?)            // finite: cutter
Expand.Chain(directions{}, dynamicLayout, conditions?, grow?, shrink?)  // unbounded: AND
Expand.Step(layout, ...perStepConditions)
```

- **Layout objects, not strings.** Steps and chains reference `Layout`
  objects directly — `var half = ...;` then `Expand.Step(half)`.
- **Handles are per-side; the drag gesture decides expand vs shrink.**
  - A `Sequence` has one handle on `direction`: drag out = next step,
    drag in = previous step.
  - A `Chain` singleton offers a handle on **every** allowed direction
    (any can commit the axis). Once committed, only the two ends of that
    axis carry handles, each able to grow (drag out) or shrink (drag in).
- **Per-step conditions + skip.** Each `Expand.Step` carries its own
  conditions. Locked intermediate steps are **skipped**: unlock `Hex6`
  but not `Hex3`, and Half expands straight to Hex6 (shown as
  `Cutter.Hex6 (skips Cutter.Hex3)`) — the author never writes a
  Half→Hex6 sequence. Sequence-level `conditions` gate the whole sequence
  (e.g. game mode), avoiding repetition on every step.
- **Shared layouts.** `half` is in both the square sequence `[half, full]`
  and the hex sequence `[half, hex3, hex6]`, mode-exclusive so the East
  handle isn't ambiguous. (Half is valid in hex — 6 splits in half.)

### Carry-over callbacks; invalid states unreachable

Expansion changes the shape, so slot state must transfer. The engine
**validates** every candidate result and greys the handle if it's
invalid, so the player can never drag into an invalid state.

- **Sequence:** optional `CarryState (from, toAtDefaults) => result`,
  default `CarryStateDefaults.MatchById`.
- **Chain grow:** the dragged end (head/tail) is reclassified to a body
  and **keeps its state**; a fresh end piece is added at defaults. The
  existing pieces aren't "moved". Optional `GrowCarry` override.
- **Chain shrink:** the dragged end piece is removed and its neighbour
  becomes the new end. The removed piece's roles are dropped by default —
  which can leave the chain output-less. The AND-gate registration passes
  a custom `ShrinkCarry` (`MoveOutputToSurvivor`) that moves the sole
  output onto the surviving end. This is the "combining depends on chain
  conditions" case the default can't handle — without it, the shrink
  handle would simply grey out (still safe, just not as smart).

## Run

```
dotnet run --project prototypes/variant-encoding
```

## Keys

**Always:** `[t]` toggle Explosion/Live · `[n]`/`[p]` scenario · `[r]`
reset chain · `[q]` quit

**Live mode:** `[h]`/`[l]` focus piece · `[k]`/`[j]` focus slot ·
`[i]`/`[o]`/`[e]`/`[x]` set Input/Output/Enabled/Disabled (refused if it
would violate a predicate) · `[1-9]` pull a drag handle · `[m]` toggle
game mode · `[a]`/`[b]`/`[c]` toggle each research independently

## Worth testing

- **The registration code itself** (`Scenarios.cs` → `Registrations`).
  Does declaring painter, AND-gate, and especially the **cutter**
  (4 layouts + 4 transitions + gating) read cleanly? Is anything in the
  wrong place?
- **Skip locked intermediates** (cutter, scenario 4). Press `[m]` for hex
  mode. From `Cutter.Half`, toggle `[c]` (research Hex6Cutter) but leave
  `[b]` (Hex3Cutter) off. The expand handle should read
  `Cutter.Hex6 (skips Cutter.Hex3)` — Half jumps straight to Hex6. Turn
  `[c]` off and `[b]` on: now it expands to Hex3 only.
- **Singleton handles on all sides** (AND, scenario 3). A fresh AND gate
  is a singleton — the drag panel lists expand handles for N/E/S/W. Pull
  one; the axis commits and the panel collapses to two end-handles, each
  with grow + shrink.
- **Custom shrink keeps validity** (AND). Build a chain, make the head's
  `sig_C` the only Output, then shrink the head end. The `ShrinkCarry`
  callback moves the output to the surviving end, so the shrink stays
  available instead of greying out. Compare against the cutter, which has
  no custom carry and *does* grey the shrink when it would drop the last
  output.
- AND-gate live: set *every* slot of a body piece to Disabled. It should
  be allowed (spacer) — no local rule fires.
- AND-gate live: shrink the chain (`[-]`) and try to drop below 2 total
  inputs or 0 outputs. The Options panel should grey out the offending
  roles with the chain reason.
- Explosion view (`[t]`) on the AND-gate: each piece now explodes to
  4³ = 64 variants (no local pruning). Note the count — eager generation
  means 4 piece specs × 64 = 256 definitions for one gate. Flag if that
  feels like too many.
- Compare the painter explicit vs range scenarios — same 7 variants.

## Open concern surfaced by this round

4-role slots blow up fast: 4ⁿ per piece. The AND-gate is 256 definitions
eagerly generated. Fine for v1's three buildings, but worth deciding
whether the framework needs a variant budget / lazy generation story
before scaling past a handful of buildings.

## Answer

User signoff 2026-05-31: "looks good enough for the initial version." Lift
`Encoding.cs` into `ExpandableX-Core` as the basis for the real registration
API. Further API cleanup deferred (the deferred items are listed under
"Open / deferred" below).

Validated:

- [x] Alphabet — I/O/D/E, one char per slot
- [x] Registration umbrella — layouts + expansions, no initial-layout concept
- [x] Layout — `Static` / `Dynamic` kinds
- [x] Chain predicates declared on the dynamic layout
- [x] `AtLeastN` predicate family (local + chain)
- [x] Pieces may be all-Disabled spacers (no forced local rule)
- [x] `OptionsFor(...)` as the dropdown-UI primitive
- [x] `Expand.Sequence` / `Expand.Chain` — directional, layout objects not strings
- [x] Per-side handles; drag gesture decides expand/shrink; singleton = all sides
- [x] Per-step conditions with skip (no combination enumeration)
- [x] `CarryState` / `GrowCarry` / `ShrinkCarry` callbacks; validate-then-gate
      so invalid states are unreachable via the drag handle

## Open / deferred (not blocking lift)

- **Variant-count explosion.** 4ⁿ per piece × 4 pieces = 256 definitions per
  AND-gate registration, eagerly generated at session init. Acceptable for
  v1's three buildings; revisit if scaling past a handful of buildings.
- **API ergonomic cleanup.** User noted the chain-vs-local predicate split
  could be context-inferred for the configurable singleton (chain predicates
  could also cull singleton variants locally). The amount of allocation in
  hypothetical-state validation is also non-ideal, but reducing it would
  greatly increase predicate API complexity — leave as-is.
- **Per-building role-set restrictions.** AND gate slots really only need
  I/O/D (not E); painter only needs E/D (not I/O). The framework allows any
  subset, so this is just a registration-author concern, not a framework one.
- **Show-vs-hide skipped intermediates.** Prototype shows them
  (`Cutter.Hex6 (skips Cutter.Hex3)`); user signed off without prescribing
  a final UX choice. Leave as-is for the lift; UX call when the real
  building panel exists.
- **Non-path dynamic layouts** (1×1 → 2×1 → 2×2). Tracked in GitHub
  issue #2; the chain mechanism here is path-shaped per v1 scope.

## What to lift into `ExpandableX-Core`

`Encoding.cs` types and engines (everything except `Program.cs` and
`Scenarios.cs`, which are throwaway harness). In particular:

- `SlotRole`, `RoleAlphabet`, `Slot`, `SlotSpec.{Single,Range}`,
  `IConnectorCountResolver`
- `ISlotPredicate`, `SlotPredicates`, `IChainPredicate`, `ChainPredicates`
- `PieceRole`, `PieceSpec`
- `Layout.{Static,Dynamic}` with `LayoutExtensions`
- `Registration`, `Direction`, `Directions.Opposite`, `GameMode`
- `ExpansionContext`, `IExpansionCondition`, `ExpansionConditions`
- `CarryState` / `GrowCarry` / `ShrinkCarry` delegates,
  `CarryStateDefaults.MatchById`
- `SequenceStep`, `Expansion.{Sequence,Chain}`, `Expand` factory
- `DragKind`, `DragAction`, `ExpansionEngine`
- `PieceState`, `ChainState` (with `Axis`)
- `ChainBuilder`, `ChainValidator`, `SlotOption`, `ValidationReport`
- `Variant`, `PrunedCandidate`, `PieceExpansion`, `VariantEncoder`

The fake `IConnectorCountResolver` (`FakeResolver`) is throwaway — the
real implementation reads `ConnectorData` off the live
`MetaBuildingDefinition`.
