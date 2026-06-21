# ExpandableX

The domain language for a pair of Shapez 2 mods that let players adjust the size and connector layout of in-game buildings. `ExpandableX-Core` is the library; `ExpandableX` is the base-game consumer.

## Language

### From Shapez 2 (canonical game terms — keep verbatim)

**Building**:
A placed entity on the map with a `Definition`, `Transform`, `State`, and `Configuration`.
_Avoid_: component, structure, block

**BuildingDefinition** (`MetaBuildingDefinition`):
The shared data template for a building type. In the game's actual model, this is `MetaBuildingDefinition` — a Unity `ScriptableObject` defined in `SPZGameAssembly` with the building's tile footprint (`Tiles[]`), connector arrays (`BeltInputs/Outputs`, `BeltPortInputs/Outputs`, `FluidInputs/Outputs/Junctions`, `SignalInputs/Outputs/Junctions`), and visual/sound configuration. We treat `MetaBuildingDefinition` as the canonical term; the docs sometimes shorten it to `BuildingDefinition`.
_Avoid_: building type, building class

**Entity**:
The abstract parent of `Building` and `Island`. Anything placed on the map.
_Avoid_: object, instance, thing

**Simulation**:
The per-logic-cluster runtime that drives behaviour. Stateless w.r.t. spatial info. Pattern-matched from one or more `Building`s by a `SimulationSystem`.
_Avoid_: controller, logic, behaviour

**SimulationConnector**:
An indexed I/O point on a `Simulation` (e.g. `Input #0`, `Output #0`) used by `SimulationGraph` to wire simulations together. This is a *simulation-layer* abstraction; at the `MetaBuildingDefinition` layer, connectors live in **eight typed arrays** by medium and direction (`BeltInputs/Outputs`, `BeltPortInputs/Outputs`, `FluidInputs/Outputs/Junctions`, `SignalInputs/Outputs/Junctions`). The indexed `SimulationConnector` view unifies those heterogeneous arrays at runtime.
_Avoid_: input, output, port, pin, socket

**SimulationSystem**:
Pattern-matches `Building`s on the map and creates/destroys `Simulation`s accordingly. Atomic (1 building → 1 simulation) or multi-building (N → 1).
_Avoid_: registry, manager

**Configuration**:
The per-`Entity` data the player can adjust (e.g. "the shape this item producer should make"). Persisted in saves.
_Avoid_: settings, options, state

**Variant** (deprecated as a separate concept):
Earlier in the design we thought "variant" was a sub-axis of `MetaBuildingDefinition` (mirrored cutter, mirrored bent stacker, belt bends, mirrored comparison gates). Decompilation revealed Shapez has **no `Variant` field** — each of those is its own `MetaBuildingDefinition`. The visual mirror relationship is expressed via the `IBuildingMirrorableCustomDrawData` interface (`DrawData.Mirror(...)` returns the flipped form for asymmetric definitions; symmetric ones return themselves). **Avoid the word "variant" in new design discussion — use `MetaBuildingDefinition` directly.**

### ExpandableX domain terms

**Expandability**:
The umbrella property a building may have, opting it in to player-adjustable size and/or connector layout. **Opt-in per building**, defined entirely by what is registered with `ExpandableX-Core`.
_Avoid_: stretching, resizing, customisation, configurability

**Connector slot**:
A *position* on a `Building` where a connector can live, with a player-configurable **role**: `Input`, `Output`, `Disabled`, or `Enabled`. Generalises the earlier "toggleable connector" concept: a binary toggleable (on/off, fixed direction) is the degenerate case of a slot whose roles are `{Input, Disabled}` (or `{Output, Disabled}`). The general tri-state case lets the player swap input ↔ output on a slot — the AND-gate's gameplay output is one example: the slot is `Output` on one piece (where the player wants the result), `Input` on the other pieces (where they want extra inputs to combine), `Disabled` where they want nothing.

Slot role is **not** per-instance state. It is encoded in the `MetaBuildingDefinition` id (see **Variant id** below). Each reachable combination of slot roles is a distinct generated `MetaBuildingDefinition`; changing a slot's role swaps the `Building` to the definition whose id encodes the new combination. This follows from connectors being read statically off the definition (an `IBuildingConfiguration` role would be stored but never consulted when the game assembles connector arrays). See [ADR-0008](docs/adr/0008-slot-state-encoded-in-definition-id.md). **Blueprint-stable** by construction: two identically-configured pieces share one definition, so copy/paste and save/load are exact, with no per-instance ids involved.
_Avoid_: toggleable connector (use this only for the binary case), optional input, optional output, switchable pin

**Toggleable connector** (deprecated alias):
The earlier name for a `Connector slot` with role set `{Input, Disabled}` or `{Output, Disabled}`. Use **`Connector slot`** going forward, even when only two roles apply.

**Connector reference**:
How a `Connector slot` names the physical connector it controls. Connectors carry no id or name — their only intrinsic identity is their **pivot** (`Position_L` + `TileDirection`). Authors bind a slot by **connector type + visible index** (e.g. `Of<BuildingFluidJunction>(0)`), where the index is into the game's `BuildingConnectorsOfType<T>()` list *after* internal connectors are skipped (see **Auto-skip internal**), so the index matches the connectors the player can actually see. This mirrors the game's own connector-access idiom (`BeltPortSystem`, the splitters). The reference is resolved once, against the base `MetaBuildingDefinition`, into a concrete connector whose pivot variant generation then uses. The connector type also tells the framework which roles are legal (junction → `Enabled`-capable, input array → `Input`, output array → `Output`). An explicit pivot form (`At(pivot)`) is the escape hatch when index can't cleanly name a connector.
_Avoid_: connector id, connector name, slot key

**Variant id**:
The `MetaBuildingDefinition` id of a framework-generated variant. Formed by suffixing the base definition id with `_ExpandableXConfigurable_` followed by one **role character** per face/slot, in order (see **Role** for the character alphabet — including `J` for a join face on `DynamicLayout` pieces). A piece with no slots keeps its base id unchanged. Because the id fully and deterministically encodes the slot state, it is both the storage of that state (per [ADR-0008](docs/adr/0008-slot-state-encoded-in-definition-id.md)) and the unit of blueprint identity.
_Avoid_: variant key, slot hash, configuration string

**Role**:
What a face of a piece is set to. Five values, each with a single-character encoding for the **Variant id**: `Input` = `I`, `Output` = `O`, `Disabled` = `D`, `Enabled` = `E`, `Join` = `J`. `Enabled` is **junction-specific** — it means a bidirectional connector (`BuildingFluidJunction`, signal junction) is active as a pass-through, both accepting and providing at once. `Input`/`Output` apply to directional connectors; `Disabled` removes the connector entirely. `Join` means the face carries a `Join connector` toward an interior neighbour of the same building, and applies to `DynamicLayout` pieces only.

`Join` is **topology-driven, not player-driven**: the grow/shrink action assigns it as the building's shape changes, and an interior face is *forced* to `Join` (the player can't toggle it), whereas the other four roles on a border face stay player-driven. Because a face holds exactly one connector, `Join` is simply another letter in the same per-face alphabet — the join-face set needs no separate id section.

A slot's *allowed* roles are derived from its **Connector reference**'s connector type — there is exactly one "active" role per type (junction → `Enabled`, input connector → `Input`, output connector → `Output`), plus `Disabled`. `Enabled` doubles as the author-facing generic word for "active": writing `Enabled` against a non-junction connector is **auto-corrected** to that connector's native active role. Auto-correction happens at slot-resolution time, *before* id encoding, so a variant id is always canonical — `E` appears in an id only for a genuine junction. (Tri-state directional slots that flip `Input` ↔ `Output` are a separate case — see **Connector slot** and the AND gate.)
_Avoid_: state, setting, mode, direction

**Composable expansion**:
The mechanism by which a single "logical building" the player perceives as one unit is in fact N connected `Building` entities, pattern-matched by a `SimulationSystem` into one `Simulation`. "Expanding" places more connected pieces; "shrinking" removes them. The mechanism documented for conveyors/belt ports in Shapez 2 — we adopt it explicitly for ExpandableX.
_Avoid_: resize, footprint change, multi-tile building, growable grid

**Opt-in**:
A `BuildingDefinition` is expandable **only** if something has registered expandability for it with `ExpandableX-Core`. There is no implicit/default expandability — game balance and intentional progression decisions (e.g. separately-unlocked straight vs bent stackers) demand explicit choices.

**Registration**:
The umbrella object a mod supplies for one expandable **building family** (via **Register** / **Override**). It owns the family's `Layout`s and its `Expansion`s. A family may span **multiple `MetaBuildingDefinition`s across multiple groups** — the cutter family owns `Half` (group `CutterHalfVariant`) and `Full` (group `CutterDefaultVariant`); the painter family happens to be a single definition. `RegistrationId` is a **logical family identifier the author chooses** (e.g. `"Cutter"`), *not* a game group id; the actual game definitions are referenced per-`Layout` via each piece's configurable-base id. It is the noun produced by the `Register` verb. In code: `Registration`.
_Avoid_: entry, record, config

**Layout** (umbrella):
A specific valid state a player can put an expandable building into. Multiple layouts per expandable `MetaBuildingDefinition`. Layouts are catalog/registry entries, not per-instance state. Comes in two kinds — **`StaticLayout`** and **`DynamicLayout`** — which are implemented via two different runtime mechanisms (see [[project-dual-layout-implementation]]). In code the two kinds are the nested forms `Layout.Static` / `Layout.Dynamic`.
_Avoid_: form, composition, variant, pattern, shape

**StaticLayout**:
A layout that corresponds 1-to-1 with a specific `MetaBuildingDefinition`. The case where a building has a finite, hand-crafted set of valid sizes, each modelled as its own definition. The cutter is the canonical example, though the base-game sizes turned out smaller than first assumed (confirmed in-game — see [`docs/research/building-definition-ids.md`](docs/research/building-definition-ids.md)): `CutterHalfInternalVariant` is the **half-destroyer** (deletes half, 1 output) and `CutterDefaultInternalVariant` is the **2-output cutter** (splits into halves). There is **no 4-output ("quarter") cutter** in the base game — a quarter cutter, and the hex-mode 3- and 6-output cutters, would be **new authored `MetaBuildingDefinition`s with their own simulations** (authored via ShapezShifter, referenced by id — [ADR-0011](docs/adr/0011-orchestrate-by-id-delegate-authoring-to-shapezshifter.md)). The painter is also a `StaticLayout` — the existing 2×1 `PainterMetaBuildingDefinition` — with toggleable paint-junction connectors.

A `StaticLayout` itself doesn't enumerate pieces or footprint; that data already lives on the referenced `MetaBuildingDefinition`. The `StaticLayout` just records: which definition is this layout, which of its connectors are user-toggleable, and what conditions (game mode etc.) gate availability.

**Runtime mechanism:** *swap*. Transitioning between static layouts swaps the `MetaBuildingDefinition` of the placed building; Shapez handles the resulting tile / connector reconfiguration natively.

**DynamicLayout**:
A rule-based layout that matches arbitrary connected arrangements. The case where the player can keep expanding a building into more tiles — the AND gate with N inputs for unbounded N is the canonical example. A single `DynamicLayout` registration covers a family of generated `MetaBuildingDefinition`s — one per reachable **join-face set** (which faces join a neighbour) × **slot-role** combination — that together represent one logical expandable building.

**Runtime mechanism:** *multi-piece composition* by a **connected-components network** simulation modeled on the game's own — the fluid (`FluidNetworkSystem<>`) and signal (`SignalNetworkSystem`) networks are two separate copies of the same pattern, each hardwired to its own junction connector type. ExpandableX ships its own such system, keyed on a dedicated `Join connector` type (the game's base is not connector-generic, so we copy the pattern rather than subclass it). The pieces of one building form a single connected network: matching is **connector pivot equality** (a piece's join connector matches a neighbour's counterpart pivot), and the network merges every piece it touches into one `Simulation`. Unlike the belt path system there is **no one-in/one-out restriction**, so a piece may join neighbours on any number of faces — the building can **branch in any planar direction** (and, in principle, vertically). See [ADR-0012](docs/adr/0012-network-model-for-dynamic-layouts.md).

**Border closing:** a piece carries a join connector *only* on faces shared with an interior neighbour of the same building; outer faces have **no** join connector. Two separate buildings placed adjacent therefore meet on faces that are outer for both — no matching pivots, no accidental fusion — making cross-building fusion structurally impossible without any phantom/dummy connectors.

A single `MetaBuildingDefinition` registration may carry static layouts, dynamic layouts, or both, but a player-placed arrangement matches at most one of them at a time.
_Avoid_: parametric layout, generator, pattern (clashes with Shapez's `SimulationSystem` pattern-matching language at a different abstraction level)

**Piece role**:
A `DynamicLayout` building is made of two kinds of piece:

- **Singleton** — the standalone, 1-tile case. Carries **no join connectors**, so it is *not* a member of the network and behaves as an ordinary building until expanded. Comes as the **Default singleton** (below) and the **configurable-singleton** variants — the 0-join generated variants of the layout's single declared piece (see "One declared piece" below). `StaticLayout`s also use a singleton (painter, each cutter size).
- **Network piece** — any tile of a multi-tile `DynamicLayout` building. Identified *only* by its **join-face set** (which of its faces carry a join connector — end / straight / corner / T / cross …); there is **no head/body/tail distinction**. Generated as id-encoded variants (see **Variant id**) where the join-face set is part of the encoding, so a network piece's id captures both its join topology and its slot roles.

**One declared piece, emergent kinds.** The author declares a *single* gameplay piece (its connector slots in their `Input`/`Output`/`Disabled` roles); the framework adds `Join` as an allowed role on every face-slot. The generated variants then split by **join count**, with no extra declaration: the **0-join** variants are the configurable singleton, the **≥1-join** variants are the network pieces. The join rules — any face may join, a network piece has at least one join, a singleton none — are intrinsic to the kind and imposed by the framework, not authored. (Separate per-kind declaration **isn't supported today**; it could be added as a future option if a building ever needs genuinely different *gameplay* configs for its singleton vs its network pieces. The common case is one piece.)

To keep generation small, the framework generates **one canonical orientation per rotational class** of join-face set and realises the other orientations via the placed building's `GridRotation` — just as belts reuse one bend model across four rotations. A piece's id encodes **local-frame** face roles; rotation maps local faces to world faces at swap time. The grow/shrink and slot-change actions swap between these definitions transparently (default singleton ↔ singleton variant for 1-piece slot adjustments; singleton ↔ network pieces for expansion).

_Avoid_: piece kind, piece class, head, body, tail

**Default singleton**:
The pre-existing `MetaBuildingDefinition` the game places and that saves contain — the **swap origin**. Typically the base-game's own definition we never modify (e.g. `LogicGateAndMetaBuildingDefinition` for AND; the base `PainterMetaBuildingDefinition`; `HalfCutterMetaBuildingDefinition`). It carries no slot state and no join connectors, so it is not a network member. The framework swaps *away from* it when the player first configures a slot, and *back to* it when all slots return to default — preserving save-compatibility, since uninstalling the mod then only affects buildings the player actively configured. Not a generated piece role; it is the origin the singleton variants are swapped to and from.
_Avoid_: base singleton, plain singleton, original

**Configurable base**:
The `MetaBuildingDefinition` a slotted piece's connector slots are declared against — what defines **where the connectors are and which exist**. It must carry the **connector superset**: every slot's connector present (in its active role), because variant generation can only *remove* (`Disabled`) or *flip type* (`Input`↔`Output`) at an existing pivot — it cannot invent connector geometry (see [ADR-0009](docs/adr/0009-variant-generation-synthesizes-connectors.md)). A slot's *default role* is independent of the base and may be `Disabled` (so the default variant omits that connector). The configurable base often differs from the **Default singleton**: the painter's coincides (every paint junction already exists on the base painter), but the AND gate's is a *new* maximal `MetaBuildingDefinition` — the 3-input/1-output form — because the base-game AND lacks a 3rd-input position. The default roles, not the base, drive transitions (static-sequence steps, dynamic expansion).
_Avoid_: superset definition, master definition, template

**Variant override**:
A registration-supplied mapping from a specific slot-role combination to a **named pre-existing `MetaBuildingDefinition`**, used instead of a synthesised variant. Declared on the piece (alongside its configurable base and slots). Serves three needs: mapping the all-defaults combination to the **Default singleton** (the swap origin); reusing definitions that already model a variant (the cutter's `HalfCutterMetaBuildingDefinition` / `FullCutterMetaBuildingDefinition`, never re-synthesised); and acting as a per-variant escape hatch when synthesis would yield a wrong model. The variant id of an overridden combination is the named definition's own id; decoding a placed building's id back to its slot state uses a session-init dictionary (`def id → slot state`), never id-string parsing, so arbitrary override ids are fine.
_Avoid_: variant alias, definition mapping, manual variant

**Join connector**:
A dedicated connector type — distinct from the building's gameplay connectors (`BuildingItemInput/Output`, `BuildingSignalInput/Output`, etc.) — used exclusively to decide which adjacent pieces form one logical building. It is the **single, framework-owned** connector type the shared network system groups on (mod authors never see or register it); the network groups pieces by where their join connectors line up. A piece carries a join connector on each face shared with an interior neighbour and **none** on outer faces (see **DynamicLayout** "Border closing").

Using a *dedicated* type — rather than reusing an existing connector type, which our `ConnectorSynthesizer`'s extension point would also allow — keeps the join semantics independent of the building's actual I/O, and authors needn't sacrifice a gameplay connector for joining. Joins are about composition, not flow. **Families share the one join type but never merge across families:** a single shared network system handles every `DynamicLayout` family, and two pieces fuse only when their join pivots line up *and* both pieces resolve to the same family (the family is recoverable from any placed piece's definition). So family isolation is enforced by the matcher, not by a connector type per family — the geometric border-closing is the first line of defence, same-family adjacency the second.

Bends and branches are natural: join faces only ever point at occupied same-building tiles, so a building takes whatever connected shape its grows produced.
_Avoid_: link connector, expand pin, glue connector, join-in, join-out, phantom join connector

**Incidental fusion**:
When a grow makes a new `Network piece` adjacent to one or more pieces *already in the same network*, the faces they now share become interior and are **fused** — each (on both pieces) becomes a `Join` and any gameplay connector that sat there is dropped — upholding the invariant that an interior face is always a `Join` (see **Role**). Fusion is strictly **intra-network**: it only fuses faces between pieces of the *same* logical building and never merges two separate networks (growing beside a *different* building leaves both as ordinary adjacent buildings). A building's gameplay I/O therefore lives only on its outer faces, and a building never feeds its own output back into its own input internally — **no internal self-feedback**. (Players can still build feedback deliberately with external wires, where it is visible and intentional; the game's signal simulation is also loop-unstable.)
_Avoid_: merge, weld, auto-join, self-wire, self-feedback connector

**Logical building**:
The unit the player perceives — and the HUD treats — as a single building. Either an ordinary (non-network) `Building`, or a whole `DynamicLayout` network (all its join-connected `Network piece`s together). Selection, highlight, copy/blueprint, and delete all operate per logical building, not per `Building` entity.
_Avoid_: logical unit, group, super-building

**Network selection**:
The rule that a network is selected all-or-nothing: any action that would add one `Network piece` to the player's building selection adds the **whole** network, and any that would remove one removes the **whole** network. The selection can therefore never hold a partial network — the same invariant grow/shrink/fusion already uphold (a network is only ever created or destroyed as a whole — see **Incidental fusion**), carried into selection. Whole-network highlight and copy follow for free because they read the selection.
_Avoid_: partial selection, member selection, mass selection (the game's own many-buildings gesture — a different concept)

**Focus piece**:
When the player single-clicks one member of a network, the whole network becomes the selection (one `Logical building`) **and** the clicked member is the *focus piece* — the within-selection target whose `Connector slot` config and grow/shrink the per-building HUD shows. It is the canonical "drill into one piece of the one building" concept (it leads toward, but does not require, `Drag-handle expansion`). A focus piece exists **only** from a single-click into a network, never from a mass/area gesture — those show the many-buildings HUD even when they happen to cover exactly one whole network. Transient, session-only state, never persisted, so it does not touch saves or blueprints and the no-per-instance-ids rule (blueprint stability) is untouched.
_Avoid_: sub-selection, active piece, sub-building, selected piece

**Connector slots as a property of a layout**:
Each `Layout` declares its own set of `Connector slot`s; each slot's allowed role set follows from its connector type (see **Role**). The painter's single layout declares its paint **junction** slots with roles `{Enabled, Disabled}` — a binary toggle on a bidirectional connector. An AND-gate `DynamicLayout` piece declares its logic-signal slots with the full tri-state set so the player can move the gate's gameplay output between pieces. **There is no slot-only path that bypasses the layout concept** — layouts are the universal unit of registration.

**Drag-handle expansion**:
The player-facing interaction model for changing a building's layout. The base `Building` displays expansion handles on its sides; dragging a handle extends the logical building, auto-placing the additional `Building` entities. Anchoring expansion to a specific base building disambiguates "which building does this expansion attach to" — a class of problem build-and-detect (pure pattern-matching from manually-placed adjacent pieces) cannot solve when buildings of the same type sit one cell apart.
_Avoid_: drag-to-resize, grip, stretch handle

**Build-and-detect**:
A simpler, lower-fidelity precursor to drag-handle expansion. The framework's `SimulationSystem` pattern-matches any connected layout of registered pieces and constructs the right `Simulation`, without any explicit affordance. Used as a development proof-of-concept to validate that pattern-matching extensions work before investing in drag-handle UI. Suffers from the "which building does this expansion attach to" ambiguity in real use; not the intended end-user UX.
_Avoid_: passive expansion, implicit expansion

**Expansion**:
A declared way a player can move a placed building between `Layout`s, owned by a `Registration`. Two kinds:

- **Sequence** — a finite, ordered progression of `StaticLayout`s along one direction, advanced/retreated by *swap* (the cutter: Half → Full, or Half → Hex3 → Hex6). Steps carry per-step **Condition**s; a locked intermediate step is **skipped** rather than blocking the ones past it.
- **Network** — unbounded multi-piece growth of a `DynamicLayout` into one connected network (the AND gate). Grow/shrink is **directed from a specific face** (a HUD button per face initially, drag handles later), so the framework always knows which face of which building is being expanded; the gameplay connector on that face **translates along the grow axis** ("pinch and stretch") onto the new end, and reverses on shrink. Which faces a player may grow from is gated by a **shape-limit predicate** (`Line`, `Rectangle`, `Free`, … — framework-supplied or author-written, live-state aware, see **Condition**), and the whole result by a **building-wide predicate** (e.g. "≥1 `Output`") plus connectivity (a shrink may not disconnect the building).

`Expansion` (the declared transition) is distinct from **Composable expansion** (the multi-piece *mechanism* a Network rides on) and **Drag-handle expansion** (the *UX* that triggers expansions). In code: `Expansion.Sequence` / `Expansion.Network`.
_Avoid_: transition, progression, growth, chain

**Condition**:
A test gating an `Expansion` step or whole expansion (e.g. "only in hex scenarios", "only once research X is unlocked"). To keep ExpandableX-Core game-agnostic (see [ADR-0011](docs/adr/0011-orchestrate-by-id-delegate-authoring-to-shapezshifter.md)), a condition is an **author-provided predicate** evaluated against live game state when available expansions are computed — Core provides only the generic `When(predicate, description)`; the consumer reads game state for the game-specific checks (e.g. hex = `ShapesConfiguration.PartCount == 6`, which is scenario-driven, so there is no built-in "hex mode" flag). Common-condition helpers (`RequiresShapeParts`, `RequiresResearch`) may be added later but stay predicates underneath.
_Avoid_: rule, gate, requirement, ExpansionContext (the prototype's framework-populated context is not used)
_Avoid_: transition, progression, growth

### Mod structure

**ExpandableX-Core**:
The library mod. Owns the registry, the two concepts above, the shared UI shell, and the game hooks. Contains **no** base-game-specific knowledge.

**ExpandableX**:
The consumer mod that uses `ExpandableX-Core` to register expandability for base-game `BuildingDefinition`s.

**Compatibility mod**:
A third-party mod that uses `ExpandableX-Core` to register expandability for `BuildingDefinition`s owned by some *other* mod (one that doesn't ship with expandability built in).
_Avoid_: bridge mod, adapter mod

**Register**:
The default API call by which a mod claims a `MetaBuildingDefinition` as expandable and supplies its layouts. **First-wins, yielding caller.** If no registration exists for the `MetaBuildingDefinition`, this call succeeds and the caller becomes the registration. If another mod has already registered (whether via `Register` or `RegisterOverride`), this call becomes a **no-op with a warning** stating the calling mod is no longer needed — it does not error and does not clobber. This is what makes compatibility mods inert automatically when the modded mod ships first-party expandability.

**Override**:
The explicit, intentional API call (e.g. `RegisterOverride`) by which a mod replaces whatever registration exists (or doesn't) for a `MetaBuildingDefinition`. **Last-wins, loud-by-default.** Always succeeds; always wins. If a prior `Register` exists, it is replaced silently (the user opted in by choosing `RegisterOverride`). If no prior registration exists, the override simply becomes the registration. If multiple `RegisterOverride` calls target the same `MetaBuildingDefinition`, the last to load wins — so an override-vs-override conflict is resolvable only by `manifest.json` load order.
_Avoid_: replace, supersede

## v1 scope (competition release)

The `ExpandableX` mod's first release registers expandability for exactly three base-game buildings:

- **One logic gate** (specific gate TBD — pick whichever has the cleanest existing model to tweak). Uses a `DynamicLayout` (unbounded network expansion) plus connector slots. The default singleton reuses the existing base-game `MetaBuildingDefinition` (e.g. `LogicGateAndMetaBuildingDefinition` for AND) — so existing saves and freshly-placed 1-piece gates stay on the unmodified base-game definition. We generate the network-piece variants (per join-face set × slot-role combination, one canonical orientation per rotational class) for the expanded cases, plus a configurable-singleton variant family for 1-piece slot state; the gate's gameplay signal connectors are connector slots the player can flip between `Input` / `Output` / `Disabled` per face.
- **Cutter**. Uses `StaticLayout`s implemented via `MetaBuildingDefinition` swaps: the existing `HalfCutterMetaBuildingDefinition` (2-output default) and `FullCutterMetaBuildingDefinition` (4-output), plus **new `MetaBuildingDefinition`s we ship** for 3-piece and 6-piece hex-mode layouts. Exercises mode-conditional layouts and reuses an existing in-game definition we hadn't previously realised existed.
- **Painter**. The existing `PainterMetaBuildingDefinition` is a 2×1 footprint — one cell carries the paint **junction** connectors (three visible bidirectional `BuildingFluidJunction`s plus one internal `IOType==None` junction), the other carries the shape input/output. v1 registers a single `StaticLayout` for it with toggleable paint-junction slots (`{Enabled, Disabled}`). No composable or dynamic expansion. Demonstrates the toggleable-only path.

This deliberately exercises every mechanism the framework promises (composable, toggleable, variant-aware, mode-conditional, static, dynamic) on a small surface. Adding more buildings is gated by modelling effort, not code — see [[project-modelling-is-bottleneck]]. Excluded for v1: platforms (deferred), stackers (no meaningful expansion), color mixer (cheap-to-add if there's time, but not committed), all other base-game buildings.

## Flagged ambiguities

- **"Connector side"** — at the simulation API level, connectors are indexed (`Input #0`), not per-side. At the `MetaBuildingDefinition` layer they live in eight typed arrays (`BeltInputs/Outputs`, `BeltPortInputs/Outputs`, `FluidInputs/Outputs/Junctions`, `SignalInputs/Outputs/Junctions`). Whether individual array entries carry a per-side position (or just a tile offset, or something else) still needs verification — `BuildingItemInput`, `BeltPortInput`, etc. are not yet decompiled.
- **Multi-tile single pieces** — **resolved.** `MetaBuildingDefinition.Tiles[]` makes single multi-tile buildings native. The painter, mixer, and stacker are single multi-tile `MetaBuildingDefinition`s, not compositions. Our framework only invokes multi-piece composition for `DynamicLayout`s, not for representing multi-tile single buildings.
- **Per-instance `Configuration` for slot state** — **resolved, reversed.** Slot state does *not* live in `BuildingInstance.Configuration`. Connectors are read statically off the `MetaBuildingDefinition`, so a per-instance role would be stored but never consulted when the game assembles connector arrays. Slot state is therefore encoded in the definition id and realised as generated variant definitions (see **Variant id** and [ADR-0008](docs/adr/0008-slot-state-encoded-in-definition-id.md)). `IBuildingConfiguration` / `MetaBuildingDefinition<TConfig>` are not used for slot state. (The mechanism itself is still documented in [`docs/research/shapez2-internals.md`](docs/research/shapez2-internals.md#per-instance-state-mechanism-resolved); we simply don't use it for this.)
- **Pattern-matching extension surface** — **resolved.** `DynamicLayout` uses a connected-components network system modeled on the game's own (`FluidNetworkSystem<>` / `SignalNetworkSystem`, which are two copies of the pattern, each hardwired to its junction type); a mod adds its own such system instance through ShapezShifter's `ISimulationSystemsRewirer` (a mutable collection — no game patching). The system self-selects member buildings by the presence of our `Join connector` type, so there is no per-id registration to maintain. See [ADR-0012](docs/adr/0012-network-model-for-dynamic-layouts.md).

- **Singleton connector slots** — **resolved.** A `DynamicLayout` registration may optionally supply a *configurable singleton* alongside the default singleton (see Piece role) — now a family of id-encoded variant definitions, not a single config-bearing definition. The framework swaps default → the variant encoding the new slot state on the first slot adjustment, so 1-piece toggling works without breaking pre-existing saves of the default. The configurable singleton is opt-in per registration.

- **Dynamic-layout disambiguation** — **resolved.** Pieces fuse only where their `Join connector` pivots line up, and join connectors exist only on faces shared with an interior same-building neighbour (outer faces have none), so two separate buildings can never accidentally fuse. See [ADR-0012](docs/adr/0012-network-model-for-dynamic-layouts.md).

## Example dialogue

> **User:** "Make the cutter expandable so it can produce 4 outputs."
>
> **Dev:** "Good news — the 4-output cutter already exists as `FullCutterMetaBuildingDefinition` in the base game; we don't need to model it. I register two `StaticLayout`s on the cutter — one pointing at `HalfCutterMetaBuildingDefinition` (the 2-output default), one at `FullCutterMetaBuildingDefinition`. When the player expands via the drag-handle, the framework swaps the placed building from Half to Full; Shapez handles the resulting tile / connector reconfiguration. For the hex-mode 3-piece and 6-piece layouts, we'll need to author *new* `MetaBuildingDefinition`s — those don't exist yet — and register them as additional `StaticLayout`s with a hex-mode condition."
>
> **User:** "And the AND gate? I want players to be able to expand it however much they like."
>
> **Dev:** "That's a `DynamicLayout` — no fixed upper bound, so swap-based static layouts don't scale. The base-game `LogicGateAndMetaBuildingDefinition` stays as-is and serves as the *default singleton* — what a fresh placement creates and what existing saves contain. When the player expands it, the framework grows it into a connected **network** of pieces, matched into one logical gate by our own connected-components network system (modeled on the game's fluid and signal networks, which are two copies of the same pattern), keyed on a dedicated `Join connector` type. Each piece carries join connectors only on the faces it shares with a neighbour — outer faces have none — so two separate gates placed next to each other can never fuse, with no phantom-connector trickery needed. Growing is directed from a face (a HUD button, drag handles later): the gate's output connector rides along the grow axis onto the new end, and the freed faces become extra signal inputs. The signal inputs and output are connector slots the player can flip between `Input`, `Output`, and `Disabled` per face."
>
> **User:** "And the painter?"
>
> **Dev:** "Painter is just a `StaticLayout` pointing at the existing `PainterMetaBuildingDefinition` (a 2×1) with its three visible paint **junctions** marked as `{Enabled, Disabled}` slots. No composable or dynamic expansion."
