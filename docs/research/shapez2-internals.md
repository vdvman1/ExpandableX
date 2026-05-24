# Shapez 2 internals — decompile notes

Findings from decompiling `shapez2_Data\Managed\` with `ilspycmd`. These ground our design in how Shapez 2 actually works. Update this file as more is verified.

> Decompiled output itself lives under `.decompiled/` (gitignored). This file is the curated takeaway.

## Building definitions are Unity assets, not code

`MetaBuildingDefinition` (in `SPZGameAssembly.dll`) is a Unity `ScriptableObject`. Each in-game building type is a concrete subclass — `HalfCutterMetaBuildingDefinition`, `LogicGateAndMetaBuildingDefinition`, `PainterMetaBuildingDefinition`, etc. The subclasses are usually thin: they declare custom `DrawData`/`SoundData`/`Configuration` types and not much else. The actual values that make a building what it is (tile footprint, connector arrays, mesh references) live in the corresponding `.asset` files loaded by Unity, **not in the assembly code**.

Implication: we can describe the *shape* of a building's definition by reading the assembly, but not the *content* (e.g. "what are the AndGate's connectors?") without inspecting asset data.

## Multi-tile single buildings ARE native to the game

The base `MetaBuildingDefinition` declares `public TileVector[] Tiles = new TileVector[0];` — the footprint is a free-form array of tile offsets that the single Building occupies. This means:

- A single `Building` entity can occupy multiple tiles.
- Multi-tile is **not** done exclusively via multi-Building composition + pattern matching.
- Our prior assumption that all multi-tile arrangements would be "N pattern-matched single-tile pieces" was wrong for the typical case.

The mixer (3×2 with corner cut), the painter (2×1), and the stacker (2-tall) are single multi-tile `Building`s, not compositions.

## Connectors are typed arrays on `MetaBuildingDefinition`

There is no generic `SimulationConnector[]`. The base class declares **eight separate arrays** of typed connector entries, by direction (input / output / junction) and medium:

- `BuildingItemInput[] BeltInputs` — shape inputs
- `BuildingItemOutput[] BeltOutputs` — shape outputs
- `BeltPortInput[] BeltPortInputs` — belt-port (cross-platform/space) inputs
- `BeltPortOutput[] BeltPortOutputs` — belt-port outputs
- `BuildingFluidInput[] FluidConsumerConnectorIOs` — fluid inputs
- `BuildingFluidOutput[] FluidProviderConnectorIOs` — fluid outputs
- `BuildingFluidJunction[] FluidJunctionIOs` — fluid junctions
- `BuildingSignalInput[] SignalConsumerConnectorIOs` — wire/signal inputs
- `BuildingSignalOutput[] SignalProviderConnectorIOs` — wire/signal outputs
- `BuildingSignalJunction[] SignalJunctionIOs` — wire/signal junctions

The docs' `SimulationConnector` (`Input #0`, `Output #0`) is a higher-level abstraction unifying these into an indexed list at the *simulation* layer — but at the definition layer, they're heterogeneous.

Implication: "toggleable connector" needs to be sensitive to which array the connector lives in. The on/off semantics are likely the same across media but the storage isn't unified.

## Variants are separate `MetaBuildingDefinition` subclasses, not a field

Each logical-operation gate is its own class: `LogicGateAndMetaBuildingDefinition`, `LogicGateOrMetaBuildingDefinition`, `LogicGateXOrMetaBuildingDefinition`, `LogicGateNotMetaBuildingDefinition`, `LogicGateIfMetaBuildingDefinition`, `LogicGateCompareMetaBuildingDefinition`. There is no `Variants[]` field on `MetaBuildingDefinition`.

What the user described as "variants" (mirrored cutter, mirrored bent stacker, belt bends, mirrored comparison gates) are likely *also* separate `MetaBuildingDefinition`s — and what differentiates them visually is the `IBuildingMirrorableCustomDrawData` interface (`AndGate.DrawData.Mirror(...)` returns itself for symmetric buildings; asymmetric ones return a flipped `DrawData`).

This means our [[ADR-0003]] (register per `(BuildingDefinition, Variant)`) needs reframing: in Shapez's actual model, "variant" maps cleanly onto "separate `MetaBuildingDefinition`". Registration unit is just **per `MetaBuildingDefinition`** — there is no separate `Variant` concept on top of it.

## Cutter expansion isn't a thing in the base game — it's a building swap

There are two cutter definitions: `HalfCutterMetaBuildingDefinition` (2-output, the default) and `FullCutterMetaBuildingDefinition` (4-output). Plus a `GameRules.NoFullCutter` rule that disables the Full cutter in some scenarios.

In the base game, the player doesn't *expand* a cutter — they place either Half or Full. There's no existing in-game UX for swapping between them or growing one into the other.

Implication for ExpandableX: when the player "expands" a cutter via a drag-handle, we have to *swap one MetaBuildingDefinition for another* — there's no game-native mechanism for "grow a Half into a Full." And for the hex-mode 3-piece / 6-piece options we want, we'd be **adding new `MetaBuildingDefinition`s** to the game (and modelling them). The framework's "composable expansion" via multi-piece pattern matching isn't the cutter's path.

## Per-instance state vs per-type config

`MetaBuildingDefinition` carries a `Configuration` (e.g. `HalfCutterMetaBuildingDefinition.Configuration` with `BeltSpeed`, `ProcessingDelay`). These look **per-type / per-scenario**, not per-instance — they describe how cutters in general behave, not how *this specific cutter* is configured.

The modding docs talked about "per-`Entity` `Configuration` data accessible to the player" — that's a different field on the `Entity` / `Building`, not on the definition. We haven't located it yet; decompile of the `Building` entity itself is pending.

Open: where exactly toggleable-connector state would persist per-instance — likely on the `Building` entity's own data, possibly a `CustomDataHolder` (we noticed this type in `Game.Core.Map.Simulation`).

## Pattern-matching: generic, not per-system

Only one explicit `SimulationSystem` class surfaced in the search — `NotchAdapterSimulationSystem`. Other building types don't appear to have their own `SimulationSystem`. This suggests the pattern-matching is done by a small number of *generic* systems that read configuration off `MetaBuildingDefinition`, rather than each building type owning a per-type system.

Open: how does the belt's "N belt buildings → one conveyor Simulation" actually get matched? The mechanism is the closest parallel to our `DynamicLayout` design, and we still don't know whether we can hook into it directly or have to extend it. Listed under task #4 to investigate further.

## Open from the decompile task

These remain unverified or only partially understood:

- (c) The exact API surface for pattern-matching extension (how does ShapezShifter's `Atomic`/`Multi` building extension actually plug into the game?). Reading [ShapezShifter source](https://github.com/tobspr-games/shapez2-shifter) will likely answer this faster than further decompile.
- (d) Per-instance state mechanism — find `Building` entity class and its custom data holder.
- (e) Connector-to-side mapping — find `BuildingItemInput`/`Output`/`BeltPort*` definitions to see what positional fields they carry.
- (g) Stability of `MetaBuildingDefinition` ids across game versions — likely the `Id` string field, but stability over time isn't verifiable from a single snapshot.

## Initial Shifter source survey (in progress)

ShapezShifter cloned to `.decompiled/ShapezShifter/` (gitignored). Layout:

- `src/ShapezShifter.Flow/` — the fluent public API mods use. Contains `AtomicExtender/` (single-building extensions), `Building/`, `BuildingGroup/`, `ConnectorData/`, `Island/`, `IslandGroup/`, `Toolbar/`, `Translation/`, `Tick/`, `SaveData/`, `Console/`. **No `MultiBuildingExtender` / `CompoundExtender` is visible at this level** — the public API appears to cover atomic (single-building) extension only.
- `src/ShapezShifter.Hijack/` — the lower-level patch surface. `GameHijackers/Simulation/` contains `ISimulationSystemsRewirer.cs`, `SimulationSystemsDependencies.cs`, `SimulationSystemsInterceptor.cs` — the names suggest this is the seam where we'd intercept simulation-system creation. Also `Placement/`, `Predictions/`, `Buildings/`, `Islands/` patch surfaces.

**Working hypothesis** for ExpandableX implementation paths:

- **`StaticLayout` (swap)** — likely achievable via the Flow `AtomicExtender` for any new `MetaBuildingDefinition`s we author, plus a swap-on-trigger patch we add ourselves (probably via Hijack).
- **`DynamicLayout` (multi-piece composition)** — likely requires Hijack-level work in `GameHijackers/Simulation/` (intercept the simulation system that pattern-matches buildings into simulations, and inject multi-piece recognition for our registered `MetaBuildingDefinition`s).

Next step: read `SimulationSystemsInterceptor.cs` and a known multi-piece example (the belt path) to confirm the seam and the data model the interceptor sees.
