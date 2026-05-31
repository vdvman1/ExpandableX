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
The `MetaBuildingDefinition` id of a framework-generated variant. Formed by suffixing the base definition id with `_ExpandableXConfigurable_` followed by one **role character** per slot, in slot order (see **Role** for the character alphabet). A piece with no slots keeps its base id unchanged. Because the id fully and deterministically encodes the slot state, it is both the storage of that state (per [ADR-0008](docs/adr/0008-slot-state-encoded-in-definition-id.md)) and the unit of blueprint identity.
_Avoid_: variant key, slot hash, configuration string

**Role**:
What a `Connector slot` is set to. Four values, each with a single-character encoding for the **Variant id**: `Input` = `I`, `Output` = `O`, `Disabled` = `D`, `Enabled` = `E`. `Enabled` is **junction-specific** — it means a bidirectional connector (`BuildingFluidJunction`, signal junction) is active as a pass-through, both accepting and providing at once. `Input`/`Output` apply to directional connectors; `Disabled` removes the connector entirely.

A slot's *allowed* roles are derived from its **Connector reference**'s connector type — there is exactly one "active" role per type (junction → `Enabled`, input connector → `Input`, output connector → `Output`), plus `Disabled`. `Enabled` doubles as the author-facing generic word for "active": writing `Enabled` against a non-junction connector is **auto-corrected** to that connector's native active role. Auto-correction happens at slot-resolution time, *before* id encoding, so a variant id is always canonical — `E` appears in an id only for a genuine junction. (Tri-state directional slots that flip `Input` ↔ `Output` are a separate case — see **Connector slot** and the AND gate.)
_Avoid_: state, setting, mode, direction

**Composable expansion**:
The mechanism by which a single "logical building" the player perceives as one unit is in fact N connected `Building` entities, pattern-matched by a `SimulationSystem` into one `Simulation`. "Expanding" places more connected pieces; "shrinking" removes them. The mechanism documented for conveyors/belt ports in Shapez 2 — we adopt it explicitly for ExpandableX.
_Avoid_: resize, footprint change, multi-tile building, growable grid

**Opt-in**:
A `BuildingDefinition` is expandable **only** if something has registered expandability for it with `ExpandableX-Core`. There is no implicit/default expandability — game balance and intentional progression decisions (e.g. separately-unlocked straight vs bent stackers) demand explicit choices.

**Registration**:
The umbrella object a mod supplies for one expandable `MetaBuildingDefinition` (via **Register** / **Override**). It owns the building's `Layout`s and its `Expansion`s. It is the noun produced by the `Register` verb. In code: `Registration`.
_Avoid_: entry, record, config

**Layout** (umbrella):
A specific valid state a player can put an expandable building into. Multiple layouts per expandable `MetaBuildingDefinition`. Layouts are catalog/registry entries, not per-instance state. Comes in two kinds — **`StaticLayout`** and **`DynamicLayout`** — which are implemented via two different runtime mechanisms (see [[project-dual-layout-implementation]]). In code the two kinds are the nested forms `Layout.Static` / `Layout.Dynamic`.
_Avoid_: form, composition, variant, pattern, shape

**StaticLayout**:
A layout that corresponds 1-to-1 with a specific `MetaBuildingDefinition`. The case where a building has a finite, hand-crafted set of valid sizes, each modelled as its own definition. The cutter is the canonical example: `HalfCutterMetaBuildingDefinition` (2-output, already in the base game) and `FullCutterMetaBuildingDefinition` (4-output, also already in the base game) are two `StaticLayout`s on the cutter; the hex-mode 3-piece and 6-piece cases are additional `StaticLayout`s backed by **new `MetaBuildingDefinition`s we ship**. The painter is also a `StaticLayout` — the existing 2×1 `PainterMetaBuildingDefinition` — with toggleable paint-input connectors.

A `StaticLayout` itself doesn't enumerate pieces or footprint; that data already lives on the referenced `MetaBuildingDefinition`. The `StaticLayout` just records: which definition is this layout, which of its connectors are user-toggleable, and what conditions (game mode etc.) gate availability.

**Runtime mechanism:** *swap*. Transitioning between static layouts swaps the `MetaBuildingDefinition` of the placed building; Shapez handles the resulting tile / connector reconfiguration natively.

**DynamicLayout**:
A rule-based layout that matches arbitrary connected arrangements. The case where the player can keep expanding indefinitely along some axis — the AND gate with N inputs for unbounded N is the canonical example. A single `DynamicLayout` registration covers a **role-split family** of `MetaBuildingDefinition`s (see `Piece role` below) that together represent one logical expandable building.

**Runtime mechanism:** *multi-piece composition* built on the game's `BuildingPathSimulationSystem<TConnectable, TInput, TOutput>` — the same generic class `ConveyorSimulationSystem` extends to make belts into conveyors. Matching happens via **connector pivot equality**: each piece declares a join-in connector and a join-out connector with global tile pivots; two pieces join when one's join-out pivot equals the counterpart of another's join-in pivot, in global tile coordinates after each piece's rotation is applied.

**v1 limitation:** the `BuildingPathSimulationSystem` enforces exactly one in/out per piece, so dynamic layouts are **path-shaped** (1×N along the joining axis). Multi-input/output non-path expansion (e.g. a 1×1 building expanding into 2×2 then 3×3) is deferred — see [GitHub issue #2](https://github.com/vdvman1/ExpandableX/issues/2). Path-shaped covers our v1 use case (the AND gate). End pieces satisfy the invariant via **phantom join connectors** — see `Phantom join connector` below.

A single `MetaBuildingDefinition` registration may carry static layouts, dynamic layouts, or both, but a player-placed arrangement matches at most one of them at a time.
_Avoid_: parametric layout, generator, pattern (clashes with Shapez's `SimulationSystem` pattern-matching language at a different abstraction level)

**Piece role**:
Each *generated* piece in a layout plays one of four roles: **Singleton**, **Head**, **Body**, or **Tail**. The **Default singleton** (below) is *not* a generated role — it is the pre-existing definition the framework swaps to and from, sitting outside this taxonomy.

- **Singleton** — a standalone, non-chain piece with connector slots. Used by a `StaticLayout`'s lone piece (painter, each cutter size) and by a `DynamicLayout`'s 1-piece case (the configurable singleton). Generated as a family of id-encoded variants from a **Configurable base** (see below).
- **Head** — the start of a multi-piece chain. Its inner-facing join (toward bodies) is real; its outer-facing join is phantom. If the registration declares connector slots, generated as a family of id-encoded variants like any slotted piece; otherwise a single plain `MetaBuildingDefinition`.
- **Body** — interior of a multi-piece chain. Both join connectors are real, on opposite faces. Same slot treatment as head.
- **Tail** — the end of a multi-piece chain. Mirror image of head. Same slot treatment as head.

Connector slots are opt-in per registration, not intrinsic to a role. When used, all chain pieces (head, body, tail) declare the same slots. A `DynamicLayout` registration introduces **three new configurable-base `MetaBuildingDefinition`s** (head, body, tail), each of which, if slotted, explodes into a family of id-encoded variant definitions (see **Variant id**) — plus optionally a singleton family for 1-piece slot state. The drag-handle and slot-change actions swap between definitions transparently — default singleton ↔ singleton variant for 1-piece slot adjustments, and singleton → head + tail → head + body + tail → … for expansion.

_Avoid_: piece kind, piece class

**Default singleton**:
The pre-existing `MetaBuildingDefinition` the game places and that saves contain — the **swap origin**. Typically the base-game's own definition we never modify (e.g. `LogicGateAndMetaBuildingDefinition` for AND; the base `PainterMetaBuildingDefinition`; `HalfCutterMetaBuildingDefinition`). It carries no slot state and doesn't participate in path matching. The framework swaps *away from* it when the player first configures a slot, and *back to* it when all slots return to default — preserving save-compatibility, since uninstalling the mod then only affects buildings the player actively configured. Not a generated piece role; it is the origin the singleton variants are swapped to and from.
_Avoid_: base singleton, plain singleton, original

**Configurable base**:
The `MetaBuildingDefinition` a slotted piece's connector slots are declared against — what defines **where the connectors are and which exist**. It must carry the **connector superset**: every slot's connector present (in its active role), because variant generation can only *remove* (`Disabled`) or *flip type* (`Input`↔`Output`) at an existing pivot — it cannot invent connector geometry (see [ADR-0009](docs/adr/0009-variant-generation-synthesizes-connectors.md)). A slot's *default role* is independent of the base and may be `Disabled` (so the default variant omits that connector). The configurable base often differs from the **Default singleton**: the painter's coincides (every paint junction already exists on the base painter), but the AND gate's is a *new* maximal `MetaBuildingDefinition` — the 3-input/1-output form — because the base-game AND lacks a 3rd-input position. The default roles, not the base, drive transitions (static-sequence steps, dynamic expansion).
_Avoid_: superset definition, master definition, template

**Variant override**:
A registration-supplied mapping from a specific slot-role combination to a **named pre-existing `MetaBuildingDefinition`**, used instead of a synthesised variant. Declared on the piece (alongside its configurable base and slots). Serves three needs: mapping the all-defaults combination to the **Default singleton** (the swap origin); reusing definitions that already model a variant (the cutter's `HalfCutterMetaBuildingDefinition` / `FullCutterMetaBuildingDefinition`, never re-synthesised); and acting as a per-variant escape hatch when synthesis would yield a wrong model. The variant id of an overridden combination is the named definition's own id; decoding a placed building's id back to its slot state uses a session-init dictionary (`def id → slot state`), never id-string parsing, so arbitrary override ids are fine.
_Avoid_: variant alias, definition mapping, manual variant

**Join connector**:
A dedicated connector type — distinct from the building's gameplay connectors (`BuildingItemInput/Output`, `BuildingSignalInput/Output`, etc.) — used exclusively by `DynamicLayout` pattern matching to decide which adjacent pieces form one logical building. Every piece in a `DynamicLayout`-registered family (head, body, tail) carries one join-in connector and one join-out connector — satisfying the `BuildingPathSimulationSystem<,,>` invariant of exactly one in + one out per piece.

Using a *dedicated* connector type keeps the join semantics independent of the building's actual I/O — players wiring two adjacent expanded AND gates with the same signal type don't accidentally fuse them, and mod authors don't have to sacrifice an existing connector channel for joining. Joins are about composition, not about flow.

Bends are supported naturally: a piece with non-collinear join-in and join-out positions (joins on perpendicular faces) routes the logical building around a corner.
_Avoid_: link connector, expand pin, glue connector

**Phantom join connector**:
A join connector that exists (so the 1-in/1-out invariant is satisfied) but cannot match anything else. Used for the outer-facing connector on head pieces (phantom join-in) and tail pieces (phantom join-out), and on both connectors of a singleton if the singleton is implemented as a separate definition.

Implementation: the phantom connector's pivot points `TileDirection.Up`. `TileDirection` is an enum with six values — East / South / West / North / Up / Down — and `GlobalTileCoordinate` is genuinely 3D (`x`, `y`, `z` fields). The counterpart of an Up-facing pivot is at `(x, y, z+1)` facing Down. Since join connectors are a dedicated type and no piece in any `DynamicLayout` family ever produces a real Down-facing join pivot, the phantom never matches anything.

This property is **robust against future vertical-expansion features**: even if we someday allow dynamic layouts to chain across z-layers using real Up/Down joins, our existing phantoms still don't match because we wouldn't author a real Down-facing join at the specific `(x, y, z+1)` above an end piece unless we *meant* that end piece to chain upward — which is exactly when the phantom should match.
_Avoid_: void connector, null connector, dummy connector

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

- **Sequence** — a finite, ordered progression of `StaticLayout`s along one direction, advanced/retreated by *swap* (the cutter: Half → Full, or Half → Hex3 → Hex6). Steps carry per-step conditions (game mode, research); a locked intermediate step is **skipped** rather than blocking the ones past it.
- **Chain** — unbounded multi-piece growth of a `DynamicLayout` along an axis (the AND gate). A singleton offers a handle on every allowed direction; committing one fixes the axis, after which only the two ends carry handles. Growing/shrinking adds or folds pieces.

`Expansion` (the declared transition) is distinct from **Composable expansion** (the multi-piece *mechanism* a Chain rides on) and **Drag-handle expansion** (the *UX* that triggers expansions). In code: `Expansion.Sequence` / `Expansion.Chain`.
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

- **One logic gate** (specific gate TBD — pick whichever has the cleanest existing model to tweak). Uses a `DynamicLayout` (unbounded 1×N expansion) plus connector slots. Implemented via multi-piece composition. The default singleton reuses the existing base-game `MetaBuildingDefinition` (e.g. `LogicGateAndMetaBuildingDefinition` for AND) — so existing saves and freshly-placed 1-piece gates stay on the unmodified base-game definition. We ship a *configurable singleton* with `IBuildingConfiguration` (sharing the default's visual) that the framework swaps in when the player adjusts a 1-piece slot, plus new head / body / tail definitions for the expanded cases.
- **Cutter**. Uses `StaticLayout`s implemented via `MetaBuildingDefinition` swaps: the existing `HalfCutterMetaBuildingDefinition` (2-output default) and `FullCutterMetaBuildingDefinition` (4-output), plus **new `MetaBuildingDefinition`s we ship** for 3-piece and 6-piece hex-mode layouts. Exercises mode-conditional layouts and reuses an existing in-game definition we hadn't previously realised existed.
- **Painter**. The existing `PainterMetaBuildingDefinition` is a 2×1 footprint — one cell carries the paint **junction** connectors (three visible bidirectional `BuildingFluidJunction`s plus one internal `IOType==None` junction), the other carries the shape input/output. v1 registers a single `StaticLayout` for it with toggleable paint-junction slots (`{Enabled, Disabled}`). No composable or dynamic expansion. Demonstrates the toggleable-only path.

This deliberately exercises every mechanism the framework promises (composable, toggleable, variant-aware, mode-conditional, static, dynamic) on a small surface. Adding more buildings is gated by modelling effort, not code — see [[project-modelling-is-bottleneck]]. Excluded for v1: platforms (deferred), stackers (no meaningful expansion), color mixer (cheap-to-add if there's time, but not committed), all other base-game buildings.

## Flagged ambiguities

- **"Connector side"** — at the simulation API level, connectors are indexed (`Input #0`), not per-side. At the `MetaBuildingDefinition` layer they live in eight typed arrays (`BeltInputs/Outputs`, `BeltPortInputs/Outputs`, `FluidInputs/Outputs/Junctions`, `SignalInputs/Outputs/Junctions`). Whether individual array entries carry a per-side position (or just a tile offset, or something else) still needs verification — `BuildingItemInput`, `BeltPortInput`, etc. are not yet decompiled.
- **Multi-tile single pieces** — **resolved.** `MetaBuildingDefinition.Tiles[]` makes single multi-tile buildings native. The painter, mixer, and stacker are single multi-tile `MetaBuildingDefinition`s, not compositions. Our framework only invokes multi-piece composition for `DynamicLayout`s, not for representing multi-tile single buildings.
- **Per-instance `Configuration` for slot state** — **resolved, reversed.** Slot state does *not* live in `BuildingInstance.Configuration`. Connectors are read statically off the `MetaBuildingDefinition`, so a per-instance role would be stored but never consulted when the game assembles connector arrays. Slot state is therefore encoded in the definition id and realised as generated variant definitions (see **Variant id** and [ADR-0008](docs/adr/0008-slot-state-encoded-in-definition-id.md)). `IBuildingConfiguration` / `MetaBuildingDefinition<TConfig>` are not used for slot state. (The mechanism itself is still documented in [`docs/research/shapez2-internals.md`](docs/research/shapez2-internals.md#per-instance-state-mechanism-resolved); we simply don't use it for this.)
- **Pattern-matching extension surface** — only one `SimulationSystem` class surfaced in the search; building types don't appear to own per-type systems. The multi-piece pattern-matching mechanism we need for `DynamicLayout` is likely generic and parameterised by `MetaBuildingDefinition` data. Whether ShapezShifter exposes a hook for it (or we'd have to add patches) is the next thing to investigate.

- **Singleton connector slots** — **resolved.** A `DynamicLayout` registration may optionally supply a *configurable singleton* alongside the default singleton (see Piece role) — now a family of id-encoded variant definitions, not a single config-bearing definition. The framework swaps default → the variant encoding the new slot state on the first slot adjustment, so 1-piece toggling works without breaking pre-existing saves of the default. The configurable singleton is opt-in per registration.

- **Dynamic-layout disambiguation** — **resolved.** `DynamicLayout` matching uses dedicated join connectors and the game's existing `BuildingPathSimulationSystem<,,>` mechanism (pivot equality with counterpart matching). Two pieces only fuse when their join connectors line up; rotation and position differences naturally keep separate logical buildings apart.

## Example dialogue

> **User:** "Make the cutter expandable so it can produce 4 outputs."
>
> **Dev:** "Good news — the 4-output cutter already exists as `FullCutterMetaBuildingDefinition` in the base game; we don't need to model it. I register two `StaticLayout`s on the cutter — one pointing at `HalfCutterMetaBuildingDefinition` (the 2-output default), one at `FullCutterMetaBuildingDefinition`. When the player expands via the drag-handle, the framework swaps the placed building from Half to Full; Shapez handles the resulting tile / connector reconfiguration. For the hex-mode 3-piece and 6-piece layouts, we'll need to author *new* `MetaBuildingDefinition`s — those don't exist yet — and register them as additional `StaticLayout`s with a hex-mode condition."
>
> **User:** "And the AND gate? I want players to be able to expand it however much they like."
>
> **Dev:** "That's a `DynamicLayout` — no fixed upper bound, so swap-based static layouts don't scale. The base-game `LogicGateAndMetaBuildingDefinition` stays as-is and serves as the *default singleton* — what a fresh placement creates and what existing saves contain. We ship a *configurable singleton* with `IBuildingConfiguration` (sharing the default's visual) for when the player adjusts a slot on a 1-piece gate — the framework swaps default → configurable on that interaction. We also ship three new `MetaBuildingDefinition`s for the *head*, *body*, and *tail* roles, each carrying dedicated join-in and join-out connectors so they can be pattern-matched into one logical gate by a subclass of Shapez's `BuildingPathSimulationSystem<,,>`. End pieces (head, tail) have their outer-facing join connector point `TileDirection.Up` — a phantom direction that can never match a real planar join, so two unrelated chains can't accidentally fuse end-to-end. The gameplay output and inputs are modelled as connector slots that the player can flip between `Input`, `Output`, and `Disabled` per piece — the slot defaults make the head-end's output active and the rest of the slots input."
>
> **User:** "And the painter?"
>
> **Dev:** "Painter is just a `StaticLayout` pointing at the existing `PainterMetaBuildingDefinition` (a 2×1) with its three visible paint **junctions** marked as `{Enabled, Disabled}` slots. No composable or dynamic expansion."
