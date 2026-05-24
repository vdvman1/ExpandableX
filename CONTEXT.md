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
A *position* on a `Building` where a connector can live, with a player-configurable **role**: `Input`, `Output`, or `Disabled`. Per-instance — each `Building` carries its own per-slot role assignment. Generalises the earlier "toggleable connector" concept: a binary toggleable (on/off, fixed direction) is the degenerate case of a slot whose roles are `{Input, Disabled}` (or `{Output, Disabled}`). The general tri-state case lets the player swap input ↔ output on a slot — the AND-gate's gameplay output is one example: the slot is `Output` on one piece (where the player wants the result), `Input` on the other pieces (where they want extra inputs to combine), `Disabled` where they want nothing.

Per-instance state lives in an `IBuildingConfiguration` implementation we ship (call it e.g. `ExpandableXBuildingConfiguration`). The owning `MetaBuildingDefinition` declares the configuration type via `MetaBuildingDefinition<TConfig>` — the game's generic base — so the framework creates a fresh `TConfig` instance per placement. The interface requires a `Sync(ISerializationVisitor)` for saves/blueprints and a value-equality `Equals` for blueprint matching. **Blueprint-stable** by construction: copying a piece with slot states set produces a paste with the same slot states, no per-instance ids involved.
_Avoid_: toggleable connector (use this only for the binary case), optional input, optional output, switchable pin

**Toggleable connector** (deprecated alias):
The earlier name for a `Connector slot` with role set `{Input, Disabled}` or `{Output, Disabled}`. Use **`Connector slot`** going forward, even when only two roles apply.

**Composable expansion**:
The mechanism by which a single "logical building" the player perceives as one unit is in fact N connected `Building` entities, pattern-matched by a `SimulationSystem` into one `Simulation`. "Expanding" places more connected pieces; "shrinking" removes them. The mechanism documented for conveyors/belt ports in Shapez 2 — we adopt it explicitly for ExpandableX.
_Avoid_: resize, footprint change, multi-tile building, growable grid

**Opt-in**:
A `BuildingDefinition` is expandable **only** if something has registered expandability for it with `ExpandableX-Core`. There is no implicit/default expandability — game balance and intentional progression decisions (e.g. separately-unlocked straight vs bent stackers) demand explicit choices.

**Layout** (umbrella):
A specific valid state a player can put an expandable building into. Multiple layouts per expandable `MetaBuildingDefinition`. Layouts are catalog/registry entries, not per-instance state. Comes in two kinds — **`StaticLayout`** and **`DynamicLayout`** — which are implemented via two different runtime mechanisms (see [[project-dual-layout-implementation]]).
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
Within a `DynamicLayout`, each `MetaBuildingDefinition` plays one of these roles:

- **Default singleton** — the 1-piece case as it normally exists in the world. No real join connectors. Has **no** `IBuildingConfiguration` — typically the base-game's own `MetaBuildingDefinition` we don't modify (e.g. `LogicGateAndMetaBuildingDefinition` for AND). What a fresh player placement creates; what existing saves contain unmodified. Doesn't participate in path matching.
- **Configurable singleton** — optional sibling of the default singleton. Visually identical; same connector layout; **but** is a `MetaBuildingDefinition<TConfig>` so it has `IBuildingConfiguration` and can hold per-instance connector-slot state. Used only when the player adjusts a slot on a default singleton — the framework swaps default → configurable to gain the per-instance state surface, then writes the slot change. The configurable singleton's existence is opt-in per registration: buildings whose default already has `IBuildingConfiguration` don't need a configurable singleton; buildings where players never want slot adjustments on the 1-piece case don't need one either.
- **Head** — the start of a multi-piece chain. Its inner-facing join (toward bodies) is real; its outer-facing join is phantom. Implemented as `MetaBuildingDefinition<TConfig>` if the registration supports connector slots, otherwise as a plain `MetaBuildingDefinition`.
- **Body** — interior of a multi-piece chain. Both join connectors are real, on opposite faces. Same Config / plain choice as head, per registration.
- **Tail** — the end of a multi-piece chain. Mirror image of head. Same Config / plain choice as head, per registration.

Connector slots are an opt-in feature of a registration, not intrinsic to any role. A registration that uses them must use them consistently — all chain pieces (head, body, tail) carry the same `TConfig`. A registration that doesn't need slot state declares all chain pieces plain; the configurable singleton is also omitted in that case. A `DynamicLayout` registration introduces **three new `MetaBuildingDefinition`s per expandable building** (head, body, tail), plus optionally a fourth (configurable singleton) when the default singleton lacks `IBuildingConfiguration` and the building wants 1-piece slot state. The drag-handle and slot-change actions swap between roles transparently — singleton → head + tail → head + body + tail → head + body + body + tail for expansion, default singleton ↔ configurable singleton for slot adjustments on the 1-piece case.

_Avoid_: piece kind, piece class

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
Each `Layout` declares its own set of `Connector slot`s, including each slot's allowed role set (e.g. `{Input, Disabled}` for a binary toggle, `{Input, Output, Disabled}` for a full tri-state). The painter's single layout declares its paint-input slots with roles `{Input, Disabled}` — binary toggle. An AND-gate `DynamicLayout` piece declares its logic-signal slots with the full tri-state set so the player can move the gate's gameplay output between pieces. **There is no slot-only path that bypasses the layout concept** — layouts are the universal unit of registration.

**Drag-handle expansion**:
The player-facing interaction model for changing a building's layout. The base `Building` displays expansion handles on its sides; dragging a handle extends the logical building, auto-placing the additional `Building` entities. Anchoring expansion to a specific base building disambiguates "which building does this expansion attach to" — a class of problem build-and-detect (pure pattern-matching from manually-placed adjacent pieces) cannot solve when buildings of the same type sit one cell apart.
_Avoid_: drag-to-resize, grip, stretch handle

**Build-and-detect**:
A simpler, lower-fidelity precursor to drag-handle expansion. The framework's `SimulationSystem` pattern-matches any connected layout of registered pieces and constructs the right `Simulation`, without any explicit affordance. Used as a development proof-of-concept to validate that pattern-matching extensions work before investing in drag-handle UI. Suffers from the "which building does this expansion attach to" ambiguity in real use; not the intended end-user UX.
_Avoid_: passive expansion, implicit expansion

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
- **Painter**. The existing `PainterMetaBuildingDefinition` is a 2×1 footprint — one cell carries the paint-input connectors, the other carries the shape input/output. v1 registers a single `StaticLayout` for it with toggleable paint-input connectors. No composable or dynamic expansion. Demonstrates the toggleable-only path.

This deliberately exercises every mechanism the framework promises (composable, toggleable, variant-aware, mode-conditional, static, dynamic) on a small surface. Adding more buildings is gated by modelling effort, not code — see [[project-modelling-is-bottleneck]]. Excluded for v1: platforms (deferred), stackers (no meaningful expansion), color mixer (cheap-to-add if there's time, but not committed), all other base-game buildings.

## Flagged ambiguities

- **"Connector side"** — at the simulation API level, connectors are indexed (`Input #0`), not per-side. At the `MetaBuildingDefinition` layer they live in eight typed arrays (`BeltInputs/Outputs`, `BeltPortInputs/Outputs`, `FluidInputs/Outputs/Junctions`, `SignalInputs/Outputs/Junctions`). Whether individual array entries carry a per-side position (or just a tile offset, or something else) still needs verification — `BuildingItemInput`, `BeltPortInput`, etc. are not yet decompiled.
- **Multi-tile single pieces** — **resolved.** `MetaBuildingDefinition.Tiles[]` makes single multi-tile buildings native. The painter, mixer, and stacker are single multi-tile `MetaBuildingDefinition`s, not compositions. Our framework only invokes multi-piece composition for `DynamicLayout`s, not for representing multi-tile single buildings.
- **Per-instance `Configuration`** — **resolved.** Per-instance state lives in `BuildingInstance.Configuration` (`IBuildingConfiguration`), distinct from the per-type `Configuration` nested classes on `MetaBuildingDefinition`s. A definition declares its instance-configuration type via the generic `MetaBuildingDefinition<TConfig>`. The interface requires `Sync(visitor)` and `Equals` — visitor-pattern serialisation and value equality, which together cover saves and blueprint matching. See [`docs/research/shapez2-internals.md`](docs/research/shapez2-internals.md#per-instance-state-mechanism-resolved).
- **Pattern-matching extension surface** — only one `SimulationSystem` class surfaced in the search; building types don't appear to own per-type systems. The multi-piece pattern-matching mechanism we need for `DynamicLayout` is likely generic and parameterised by `MetaBuildingDefinition` data. Whether ShapezShifter exposes a hook for it (or we'd have to add patches) is the next thing to investigate.

- **Singleton connector slots** — **resolved.** A `DynamicLayout` registration may optionally supply a *configurable singleton* alongside the default singleton (see Piece role). The framework swaps default → configurable on the first slot adjustment, so 1-piece toggling works without breaking pre-existing saves of the default. The configurable singleton is opt-in per registration.

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
> **Dev:** "Painter is just a `StaticLayout` pointing at the existing `PainterMetaBuildingDefinition` (a 2×1) with its paint-input connectors marked toggleable. No composable or dynamic expansion."
