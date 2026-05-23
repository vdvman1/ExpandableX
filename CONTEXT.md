# ExpandableX

The domain language for a pair of Shapez 2 mods that let players adjust the size and connector layout of in-game buildings. `ExpandableX-Core` is the library; `ExpandableX` is the base-game consumer.

## Language

### From Shapez 2 (canonical game terms — keep verbatim)

**Building**:
A placed entity on the map with a `Definition`, `Transform`, `State`, and `Configuration`.
_Avoid_: component, structure, block

**BuildingDefinition**:
The shared data template for all instances of a building type, identified by `BuildingDefinitionId`. Holds connector data and custom data.
_Avoid_: building type, building class

**Entity**:
The abstract parent of `Building` and `Island`. Anything placed on the map.
_Avoid_: object, instance, thing

**Simulation**:
The per-logic-cluster runtime that drives behaviour. Stateless w.r.t. spatial info. Pattern-matched from one or more `Building`s by a `SimulationSystem`.
_Avoid_: controller, logic, behaviour

**SimulationConnector**:
An indexed I/O point on a `Simulation` (e.g. `Input #0`, `Output #0`). Used by `SimulationGraph` to wire simulations together. Indexed, not per-side at the API level.
_Avoid_: input, output, port, pin, socket

**SimulationSystem**:
Pattern-matches `Building`s on the map and creates/destroys `Simulation`s accordingly. Atomic (1 building → 1 simulation) or multi-building (N → 1).
_Avoid_: registry, manager

**Configuration**:
The per-`Entity` data the player can adjust (e.g. "the shape this item producer should make"). Persisted in saves.
_Avoid_: settings, options, state

**Variant**:
Shapez's term for a geometric/orientation sub-type of a `BuildingDefinition`. Variants are **the same logic in a different physical arrangement** — e.g. the mirrored cutter, the mirrored bent stacker, the different bend angles of belts, the mirrored comparison gates. They are **not** different logical operations: AND, OR, and XOR are separate `BuildingDefinition`s, not variants of each other. Because variants are geometric, two variants of the same `BuildingDefinition` often need *different layouts* (e.g. one expands rightward, the other leftward).
_Avoid_: kind, flavour, version

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
A specific valid state a player can put an expandable building into — the spatial composition (which pieces go where, at what rotation) plus the set of toggleable connectors available in that state. Multiple layouts per expandable (`BuildingDefinition`, `Variant`) pair. Layouts are catalog/registry entries, not per-instance state. Comes in two kinds: **`StaticLayout`** and **`DynamicLayout`**.
_Avoid_: form, composition, variant, pattern, shape

**StaticLayout**:
An enumerated layout with a fixed list of `PieceSpec`s — the case where a building has a finite set of valid sizes that can be listed up-front. The cutter is the canonical example (`2-piece, 3-piece, 4-piece, 6-piece` for its default variant). Each `PieceSpec` carries relative position, rotation, the connectors that participate in the unified `Simulation`, and the subset of those connectors that are user-toggleable per-instance. All pieces in a single `StaticLayout` share the same `BuildingDefinition` and `Variant` — the **homogeneous pieces** assumption — which holds for every base-game case we've examined.

**DynamicLayout**:
A rule-based layout that matches arbitrary connected arrangements rather than enumerating them. The case where a building has no clear upper bound on size — the AND gate, where the player may want as many inputs as they like. Modelled on how Shapez already pattern-matches belts into one conveyor `Simulation` regardless of length. A `DynamicLayout` provides matching logic (does this arrangement count?) and connector-computation logic (given a matching arrangement, which connectors participate and which are toggleable?). A single (`BuildingDefinition`, `Variant`) registration may carry static layouts, dynamic layouts, or both, but a player-placed arrangement matches at most one of them at a time.

**Open question:** whether `ExpandableX-Core` needs a custom dynamic-layout matcher at all, or whether it can hook into Shapez's existing `SimulationSystem` pattern-matching directly. Resolution requires decompiling Shapez. If direct hooking works, `DynamicLayout` may collapse from "framework abstraction" to "thin wrapper over a Shapez extension point." The conceptual role stays the same either way.
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
The default API call by which a mod claims a (`BuildingDefinition`, `Variant`) pair as expandable and supplies its layouts. **First-wins, yielding caller.** If no registration exists for the pair, this call succeeds and the caller becomes the registration. If another mod has already registered (whether via `Register` or `RegisterOverride`), this call becomes a **no-op with a warning** stating the calling mod is no longer needed — it does not error and does not clobber. This is what makes compatibility mods inert automatically when the modded mod ships first-party expandability. For `BuildingDefinition`s with no meaningful variant distinction, callers register against a default/sole variant id (a syntactic shortcut for this should be provided).

**Override**:
The explicit, intentional API call (e.g. `RegisterOverride`) by which a mod replaces whatever registration exists (or doesn't) for a (`BuildingDefinition`, `Variant`) pair. **Last-wins, loud-by-default.** Always succeeds; always wins. If a prior `Register` exists, it is replaced silently (the user opted in by choosing `RegisterOverride`). If no prior registration exists, the override simply becomes the registration. If multiple `RegisterOverride` calls target the same pair, the last to load wins — so an override-vs-override conflict is resolvable only by `meta.json` load order.
_Avoid_: replace, supersede

## v1 scope (competition release)

The `ExpandableX` mod's first release registers expandability for exactly three base-game buildings:

- **One logic gate** (specific gate TBD — pick whichever has the cleanest existing model to tweak). Uses a `DynamicLayout` (unbounded 1×N expansion) plus toggleable connectors.
- **Cutter** (default + mirrored variants). Uses `StaticLayout`s — 2-piece (normal-mode default), 3-piece and 6-piece (hex-mode-only), 4-piece. Exercises mode-conditional layouts.
- **Painter**. The painter occupies a 2×1 footprint in the base game — one cell carries the paint-input connectors and the other carries the shape input/output. v1 registers a single `StaticLayout` for it with toggleable paint-input connectors on the paint cell; no composable or dynamic expansion. Demonstrates the toggleable-only path. Whether the painter is internally one multi-cell `Building` or two pattern-matched `Building`s is unresolved (see Flagged ambiguities) — the layout shape will need to match whichever it turns out to be.

This deliberately exercises every mechanism the framework promises (composable, toggleable, variant-aware, mode-conditional, static, dynamic) on a small surface. Adding more buildings is gated by modelling effort, not code — see [[project-modelling-is-bottleneck]]. Excluded for v1: platforms (deferred), stackers (no meaningful expansion), color mixer (cheap-to-add if there's time, but not committed), all other base-game buildings.

## Flagged ambiguities

- **"Connector side"** — at the simulation API level, connectors are indexed (`Input #0`), not per-side. Whether they map to a specific face of a tile (and how) is unresolved and needs decompilation. Mental models that talk about "the back input" of a gate are about *visual placement on a tile face*, which may or may not align with connector index.
- **Multi-tile single pieces** — observed in the base game (mixer is a 3×2 with a corner cut; stacker is 2 tiles tall), but it's unknown whether each multi-tile building is one `Building` with a multi-cell footprint *or* multiple `Building`s pattern-matched into one `Simulation` (the same mechanism we plan to use for composable expansion). Needs decompile. If multi-tile single pieces are real, `PieceSpec` will need a footprint shape, not just a single grid offset.

## Example dialogue

> **User:** "Make the cutter expandable so it can produce 4 outputs."
>
> **Dev:** "OK — for a 4-output cutter we need a footprint with at least 4 output-facing tiles, so this is composable expansion. The cutter has a finite set of valid sizes (2/3/4/6 depending on game mode), so I'll register `StaticLayout`s — one per size. I register against the (`cutter`, `default`) pair and separately against (`cutter`, `mirrored`) since the mirrored variant's layouts go the other way. The 3-piece and 6-piece layouts carry a hex-mode condition. At runtime, `SimulationSystem` pattern-matches whichever layout the player has placed and constructs the right `Simulation`."
>
> **User:** "And the AND gate? I want players to be able to expand it however much they like."
>
> **Dev:** "That's a `DynamicLayout` — no fixed upper bound, so it can't be a static enumeration. I register a rule that matches any connected line of same-variant gate pieces. The rule computes which connectors are active (outputs are on the front face of the head piece, inputs are on the outer faces of the rest) and which are toggleable. Mechanically, this is how belts already work in Shapez — we just plug into the same pattern-matching extension."
>
> **User:** "And the painter?"
>
> **Dev:** "Painter occupies 2×1 in the base game — paint connectors on one cell, shape input/output on the other. v1 registers one `StaticLayout` describing that arrangement, with the paint-input connectors marked toggleable. No composable or dynamic expansion. Each paint-input `SimulationConnector` gets a flag in the painter `Entity`'s `Configuration`; flipping it disables that connector for placement and simulation purposes."
