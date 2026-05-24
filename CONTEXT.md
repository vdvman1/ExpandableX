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

**Toggleable connector**:
A `SimulationConnector` whose enabled/disabled state the player can flip per-`Building`-instance. Lives in the `Entity`'s `Configuration` field. Does **not** change footprint, does **not** add or remove `Building` entities. Useful both for changing logical behaviour (e.g. enabling a gate's third input) and for placement reasons (e.g. preventing a painter from sharing paint with an adjacent painter).
_Avoid_: optional input, optional output, switchable pin

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
A rule-based layout that matches arbitrary connected arrangements of one `MetaBuildingDefinition`. The case where the player can keep expanding indefinitely along some axis — the AND gate with N inputs for unbounded N is the canonical example. Authoring a `MetaBuildingDefinition` per size doesn't scale, so a single definition is paired with a matching rule that recognises N connected instances as one logical building.

**Runtime mechanism:** *multi-piece composition*, modelled on how Shapez pattern-matches multiple belt buildings into one conveyor `Simulation`. The matching rule decides which arrangements count and computes which connectors participate (and which are toggleable) on the matched composition.

**Open question:** whether `ExpandableX-Core` invents its own pattern-matching system or hooks into the game's existing one. Resolution depends on understanding the ShapezShifter extension surface; tracked under task #6 (blocked on task #4).

A single `MetaBuildingDefinition` registration may carry static layouts, dynamic layouts, or both, but a player-placed arrangement matches at most one of them at a time.
_Avoid_: parametric layout, generator, pattern (clashes with Shapez's `SimulationSystem` pattern-matching language at a different abstraction level)

**Toggleable connectors as a property of a layout**:
Each `Layout` declares its own set of toggleable connectors. A 1×1 logic gate layout may declare its back input as toggleable; a 1×2 layout may declare three toggleable connectors on the rear tile. A building that uses *only* toggleable connectors (no spatial expansion, e.g. the painter) is modelled as a single layout that happens to include toggles. **There is no toggleable-only path that bypasses the layout concept** — layouts are the universal unit of registration.

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

- **One logic gate** (specific gate TBD — pick whichever has the cleanest existing model to tweak). Uses a `DynamicLayout` (unbounded 1×N expansion) plus toggleable connectors. Implemented via multi-piece composition.
- **Cutter**. Uses `StaticLayout`s implemented via `MetaBuildingDefinition` swaps: the existing `HalfCutterMetaBuildingDefinition` (2-output default) and `FullCutterMetaBuildingDefinition` (4-output), plus **new `MetaBuildingDefinition`s we ship** for 3-piece and 6-piece hex-mode layouts. Exercises mode-conditional layouts and reuses an existing in-game definition we hadn't previously realised existed.
- **Painter**. The existing `PainterMetaBuildingDefinition` is a 2×1 footprint — one cell carries the paint-input connectors, the other carries the shape input/output. v1 registers a single `StaticLayout` for it with toggleable paint-input connectors. No composable or dynamic expansion. Demonstrates the toggleable-only path.

This deliberately exercises every mechanism the framework promises (composable, toggleable, variant-aware, mode-conditional, static, dynamic) on a small surface. Adding more buildings is gated by modelling effort, not code — see [[project-modelling-is-bottleneck]]. Excluded for v1: platforms (deferred), stackers (no meaningful expansion), color mixer (cheap-to-add if there's time, but not committed), all other base-game buildings.

## Flagged ambiguities

- **"Connector side"** — at the simulation API level, connectors are indexed (`Input #0`), not per-side. At the `MetaBuildingDefinition` layer they live in eight typed arrays (`BeltInputs/Outputs`, `BeltPortInputs/Outputs`, `FluidInputs/Outputs/Junctions`, `SignalInputs/Outputs/Junctions`). Whether individual array entries carry a per-side position (or just a tile offset, or something else) still needs verification — `BuildingItemInput`, `BeltPortInput`, etc. are not yet decompiled.
- **Multi-tile single pieces** — **resolved.** `MetaBuildingDefinition.Tiles[]` makes single multi-tile buildings native. The painter, mixer, and stacker are single multi-tile `MetaBuildingDefinition`s, not compositions. Our framework only invokes multi-piece composition for `DynamicLayout`s, not for representing multi-tile single buildings.
- **Per-instance `Configuration`** — the `Configuration` nested class on each `MetaBuildingDefinition` (e.g. `HalfCutterMetaBuildingDefinition.Configuration`) carries per-*type* tuning (belt speed, processing delay), not per-instance state. Where toggleable-connector state would persist per-instance is still open; likely on the `Building` entity itself or via a `CustomDataHolder`, but we need to decompile the `Building` class to confirm.
- **Pattern-matching extension surface** — only one `SimulationSystem` class surfaced in the search; building types don't appear to own per-type systems. The multi-piece pattern-matching mechanism we need for `DynamicLayout` is likely generic and parameterised by `MetaBuildingDefinition` data. Whether ShapezShifter exposes a hook for it (or we'd have to add patches) is the next thing to investigate.

- **Dynamic-layout disambiguation** — a `DynamicLayout` pattern matcher needs some way to distinguish "these adjacent same-type buildings are part of the same logical building" from "these adjacent same-type buildings are two separate logical buildings the player happened to place next to each other." Two AND gates side by side shouldn't merge into one expanded AND gate unless the player intended it. Mechanisms to evaluate: (a) the pieces involved in an extension carry a flag / per-instance data marking them as joined, (b) introduce piece kinds (e.g. a "head" `MetaBuildingDefinition` plus a "body" `MetaBuildingDefinition`, where the matching rule requires one head and N bodies linked through specific connector faces), or (c) use rotation / orientation to encode the joining relationship. We don't yet know which fits Shapez's pattern-matching surface — depends on what the `SimulationSystemsInterceptor` exposes. Tracked under task #6.

## Example dialogue

> **User:** "Make the cutter expandable so it can produce 4 outputs."
>
> **Dev:** "Good news — the 4-output cutter already exists as `FullCutterMetaBuildingDefinition` in the base game; we don't need to model it. I register two `StaticLayout`s on the cutter — one pointing at `HalfCutterMetaBuildingDefinition` (the 2-output default), one at `FullCutterMetaBuildingDefinition`. When the player expands via the drag-handle, the framework swaps the placed building from Half to Full; Shapez handles the resulting tile / connector reconfiguration. For the hex-mode 3-piece and 6-piece layouts, we'll need to author *new* `MetaBuildingDefinition`s — those don't exist yet — and register them as additional `StaticLayout`s with a hex-mode condition."
>
> **User:** "And the AND gate? I want players to be able to expand it however much they like."
>
> **Dev:** "That's a `DynamicLayout` — no fixed upper bound, so swap-based static layouts don't scale. One `MetaBuildingDefinition` (the existing `LogicGateAndMetaBuildingDefinition`) is paired with a matching rule that recognises any connected line of AND-gate buildings as one logical gate. The rule computes which connectors participate (outputs on the front face of the head piece, inputs on the outer faces of the rest) and which are toggleable. Mechanically this is how belts already work in Shapez — pattern-matching multiple connected instances into one `Simulation`."
>
> **User:** "And the painter?"
>
> **Dev:** "Painter is just a `StaticLayout` pointing at the existing `PainterMetaBuildingDefinition` (a 2×1) with its paint-input connectors marked toggleable. No composable or dynamic expansion."
