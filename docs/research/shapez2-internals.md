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

## Simulation systems — confirmed mechanism

ShapezShifter cloned to `.decompiled/ShapezShifter/` (gitignored). Combined with decompiling `Game.Orchestration.BuiltinSimulationSystems`, the picture is:

### How Shapez creates simulation systems

`Game.Orchestration.BuiltinSimulationSystems.CreateSimulationSystems()` is the single factory that produces all the in-game `ISimulationSystem` instances. It calls private `CreateXxxSystems()` methods per building family. The key shape:

- Most buildings are **atomic** — one `Building` entity → one `Simulation`. They're created as `AtomicStatefulBuildingSimulationSystem<TSimulation, TState>` (or the stateless variant), one per `IBuildingDefinition` inside a `IBuildingDefinitionGroup`:
  ```csharp
  foreach (var definition in definitionGroup.Definitions)
      yield return new AtomicStatefulBuildingSimulationSystem<HalfCutterSimulation, HalfCutterSimulationState>(
          factory, definition.Id, Logger);
  ```
  Cutter (Half and Full), painter, mixer, stacker, **all logic gates including AND**, rotator, merger, splitter, label, button, display, signal transmitter — all atomic.

- **`ConveyorSimulationSystem`** is currently the *only* multi-piece pattern matcher in the base game:
  ```csharp
  var definitions = Mode.Buildings.GetDefinitionGroup(Mode.Buildings.BeltBuildingId).Definitions;
  yield return new ConveyorSimulationSystem(definitions, conveyorConfig, Logger);
  ```
  It takes the entire belt definition list and matches N connected belt buildings into one conveyor `Simulation`. Belts are the canonical reference for "multi-piece composition" — there is no second example in the base game.

- A few other multi-instance-aware systems exist (`ShapeMiningSystem`, `FluidExtractingSystem`, `SpaceBeltPortSystem`, `HubSystem`, `BeltPortSystem`, `FluidPortSystem`, `SignalPortSystem`, `TrainSystem`, `FluidNetworkSimulationSystem`, `SignalNetworkSystem`, `NotchAdapterSimulationSystem<,,>`) — these are network/port-matching systems, related but not "expand a building by adding pieces" in the way we'd want.

### `BuildingDefinitionGroup` is the higher unit of building identity

Each call uses `Mode.Buildings.GetDefinitionGroup(SomeBuildingDefinitionGroupId)` and iterates `.Definitions`. So a *group* (e.g. `HalfCutterDefinitionGroupId`) holds multiple `IBuildingDefinition`s — likely the default + mirrored + hex variations sharing a common simulation behaviour. Each definition in the group becomes its own atomic simulation system instance.

**Design implication:** our `MetaBuildingDefinition` registration unit (per [[ADR-0005]]) may actually want to be **per `BuildingDefinitionGroup`** in many cases — one registration covering all definitions in the group — with the option to target a specific definition when granularity is needed. To be confirmed when we sketch the API.

### Configuration is read from `definition.CustomData`

`MergerSimulationFactory` reads its connector count from the definition's `CustomData`:
```csharp
new MergerConfiguration(conveyorSpeed,
    definition.CustomData.Get<IBuildingConnectorData>()
        .BuildingConnectorsOfType<BuildingItemInput>().Count)
```
So `IBuildingDefinition.CustomData` is the typed-bag the simulation reads its parameters from at startup. **This is likely where we attach our ExpandableX data** (e.g. registered layouts) — a custom data type on the definition, fetched by our system.

### Extension surface: `ISimulationSystemsRewirer` (Shifter Hijack)

`ShapezShifter.Hijack.SimulationSystemsInterceptor` uses MonoMod to postfix-hook `BuiltinSimulationSystems.CreateSimulationSystems()`. After the game's built-in systems are created, any `ISimulationSystemsRewirer` mods can `ModifySimulationSystems(ICollection<ISimulationSystem> systems, SimulationSystemsDependencies deps)` — adding, removing, or replacing systems. `SimulationSystemsDependencies` exposes `ShapeRegistry`, `FluidRegistry`, `SignalChannelRegistry`, `ResearchUnlockManager`, `GameMode`, `Logger`, etc.

This is **the seam ExpandableX-Core needs.** For a `DynamicLayout` on the AND gate, we'd:
1. Implement a new `ISimulationSystem` (modelled on `ConveyorSimulationSystem`) that matches multiple AND-gate buildings into one combined `Simulation`.
2. Register an `ISimulationSystemsRewirer` that removes the game's `AtomicStatefulBuildingSimulationSystem<LogicGateAndSimulation, ...>` for the AND-gate definition and inserts our multi-piece system instead.

The same surface lets a swap-implementation for a `StaticLayout` not touch simulation systems at all — both Half and Full cutter already have their atomic systems; only the player-action / placement layer needs patching to swap the placed building when the handle is dragged.

## Pattern matching mechanism: `BuildingPathSimulationSystem<TConnectable, TInput, TOutput>`

`Game.Content.BuildingPath.BuildingPathSimulationSystem<TConnectable, TInput, TOutput>` (in `Game.Content.dll`) is the generic base class for **all "multi-piece path" simulation systems**. `ConveyorSimulationSystem` extends it with `<ConnectableConveyorSimulation, BuildingItemInput, BuildingItemOutput>`.

### The shape of the base class

```csharp
public abstract class BuildingPathSimulationSystem<TConnectableSimulation, TInput, TOutput>
    : ITileSimulationSystem, ISimulationSystem,
      ISpecializedBuildingTenantSimulationSystem, IBuildingTenantSimulationSystem
    where TConnectableSimulation : class, IConnectablePathSimulation
    where TInput : IBuildingIO
    where TOutput : IBuildingIO
```

Subclasses override:
- `CreateSimulation(buildings)` — build the unified `Simulation` from N buildings
- `ExtendSimulationAtTheBeginning` / `ExtendSimulationAtTheEnd` — handle appending/prepending a building
- `RemovePathFirstBuilding` / `RemovePathLastBuilding` / `RemoveBuildingAt(index)` — handle removals
- `OnBeforeIntegrateBuildingIntoPath` (optional) — initialise per-building state

### Disambiguation (the user-flagged concern) — RESOLVED

The base class enforces that each member building has **exactly one** `TInput` connector and **exactly one** `TOutput` connector (constructor throws otherwise). Pattern matching happens via **global tile pivots**:

- Every connector has a `Pivot(transform)` method computing its global tile coordinate (factors in the building's rotation).
- When a new building is placed, the system computes:
  ```csharp
  var key  = GetInputPivot(building).CounterpartConnector();   // where an upstream building's OUTPUT would have to be
  var key2 = GetOutputPivot(building).CounterpartConnector();  // where a downstream building's INPUT would have to be
  ```
- It checks `PathByOutputPivot[key]` and `PathByInputPivot[key2]` to find existing paths that match. Outcomes: extend-end, extend-start, merge-two-paths, or new-singleton-path.

**Two unrelated buildings placed side-by-side don't fuse** because their input/output pivots don't satisfy the counterpart-equality test. Disambiguation is automatic and emerges from connector geometry — no flags, no head/body distinction needed in the basic case. Rotation matters: a belt rotated 90° has different pivots than one rotated 0°.

### Implications for our `DynamicLayout`

The mechanism is reusable. For our AND-gate dynamic expansion, we'd:

1. Either subclass `BuildingPathSimulationSystem<,,>` directly (parameterising on signal connector types), or implement a separate matcher modelled on it. **Subclassing is preferred** if the AND-gate's expansion is path-shaped (1×N). For non-path shapes (trees, grids), we'd need a more general matcher.
2. Declare the "joining" connectors on the AND-gate `MetaBuildingDefinition`. Open design question: should we **reuse** existing connector types on the gate (`BuildingSignalInput`/`BuildingSignalOutput`) or introduce **dedicated** "expansion-join" connector types? Reusing existing types is simpler but risks accidental joins from the player's wiring. Dedicated types are cleaner but need framework-level support.
3. The base class only supports **exactly one input + one output** per piece. For an AND gate where the head has 1 output and N-1 inputs distributed across pieces, this is the wrong shape — we'd need a more general "compound building" matcher that allows multiple inputs per piece. **This is the key design question for `DynamicLayout` API.**

So the `BuildingPathSimulationSystem` mechanism gives us the disambiguation logic we need but doesn't directly support the AND-gate shape. We either generalise it (subclass + override the 1-in/1-out validation) or write a sibling system for non-path shapes.

## Per-instance state mechanism — RESOLVED

`BuildingInstance` (the runtime struct passed to simulations) carries **four** fields:

```csharp
public readonly struct BuildingInstance(
    IBuildingDefinition definition,             // per-TYPE template
    in GlobalTileTransform transform,           // position + rotation
    SimulationStateContainer state,             // per-INSTANCE simulation runtime state
    IBuildingConfiguration configuration        // per-INSTANCE player-set state
);
```

`State` is volatile-ish simulation runtime (item slots, processing progress, etc.). `Configuration` is the player-settable state that persists in saves and blueprints — exactly the field we need.

### `IBuildingConfiguration` interface

```csharp
public interface IBuildingConfiguration : IEntityConfiguration, IEquatable<IEntityConfiguration> { }
public interface IEntityConfiguration : IEquatable<IEntityConfiguration> {
    void Sync(ISerializationVisitor visitor);
}
```

Two contract methods: `Sync` (visitor-pattern serialization for saves and blueprints) and `Equals` (value equality for blueprint matching). Lean.

### How a definition declares its configuration type

The generic `MetaBuildingDefinition<TConfig>` exists alongside the non-generic `MetaBuildingDefinition`:

```csharp
public abstract class MetaBuildingDefinition<TConfig> : MetaBuildingDefinition, IBuildingConfigurationFactoryProvider
    where TConfig : IBuildingConfiguration, new()
{
    public IFactory<IBuildingConfiguration> ConfigurationFactory { get; } =
        new ParameterlessConstructionFactory<TConfig>() as IFactory<IBuildingConfiguration>;
}
```

A `MetaBuildingDefinition<TMyConfig>` produces fresh `TMyConfig` instances on placement via the factory. Buildings whose definition is just the non-generic `MetaBuildingDefinition` have `null` configuration.

### Real-world example: `ConstantSignalConfiguration` (44 lines)

```csharp
public class ConstantSignalConfiguration : IConstantSignalConfiguration, IBuildingConfiguration,
                                           IEntityConfiguration, IEquatable<IEntityConfiguration>,
                                           IEquatable<ConstantSignalConfiguration>
{
    private ISignal _Value = NullSignal.Instance;
    public ISignal Value { get; set; }

    public void Sync(ISerializationVisitor visitor) {
        visitor.Sync(ref _Value);
    }

    public bool Equals(IEntityConfiguration other) =>
        other is ConstantSignalConfiguration c && Equals(c);
    public bool Equals(ConstantSignalConfiguration other) =>
        other != null && Value.Equals(other.Value);
}
```

### Implications for ExpandableX

- **Connector slot state lives in an `IBuildingConfiguration` implementation we ship** (call it e.g. `ExpandableXBuildingConfiguration`). Fields hold per-slot `SlotRole` values; `Sync` serialises them via the visitor; `Equals` enables blueprint matching.
- **Our head / body / tail `MetaBuildingDefinition`s derive from `MetaBuildingDefinition<ExpandableXBuildingConfiguration>`** so the factory wires up automatically. Configuration is created per-instance on placement.
- **The singleton role uses the base-game `MetaBuildingDefinition` unmodified — which has no `Configuration`.** This means the **singleton case can't have toggleable / multi-state connector slots**: the 1-piece AND gate's third input being toggleable would require either (a) accepting it isn't toggleable until the player expands to a multi-piece chain, (b) shipping our own singleton replacement definition (loses the "reuse base-game" win for AND), or (c) patching the base-game `LogicGateAndMetaBuildingDefinition` to add Configuration. Probably (a) for v1 simplicity, but worth surfacing to the user.

## What still needs investigation

- Player-action / drag-handle plumbing — where placement → "swap definition" lives in the game's interaction surface (Shifter's `Placement/` and `Predictions/` patch surfaces are the likely places, but unverified). Worth investigating closer to implementation, when we know exactly which patch we need.
- `IBuildingIO.Pivot(transform)` and `CounterpartConnector()` internals — they're what make disambiguation work. Worth confirming we can compute and compare these from a mod, but probably trivial.
