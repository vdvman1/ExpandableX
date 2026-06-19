# DynamicLayout uses dedicated join connectors built on BuildingPathSimulationSystem

**Status:** superseded by [ADR-0012](./0012-network-model-for-dynamic-layouts.md). The dedicated `Join connector` type survives; the `BuildingPathSimulationSystem<,,>` foundation (and its 1×N / one-in-one-out consequences) does not — `DynamicLayout` now rides the connected-components network system instead.

`DynamicLayout` pattern matching is implemented on Shapez's existing `BuildingPathSimulationSystem<TConnectableSimulation, TInput, TOutput>` (the same generic base `ConveyorSimulationSystem` extends to make belts into conveyors). The "joining" mechanism — how the matcher decides which adjacent pieces form one logical building — uses **dedicated join connectors** that are distinct from the building's gameplay connectors (item, signal, fluid, etc.).

A piece in a `DynamicLayout`-registered `MetaBuildingDefinition` declares:
- One **join-in connector** with a pivot relative to the piece's transform.
- One **join-out connector** likewise.

Two pieces fuse when one piece's join-out pivot equals the counterpart of another piece's join-in pivot in global tile coordinates (after each piece's rotation is applied). This is the same pivot-equality test Shapez uses for belts; we just parameterise the base class with our own connector types.

## Considered Options

- **Dedicated join connectors.** *(Chosen.)* The join channel is separate from any gameplay I/O the building uses. Mod authors don't have to give up a signal/item/fluid connector for joining; the framework owns the join semantics.
- **Reuse an existing connector type** (e.g. piggyback on `BuildingSignalInput`/`Output` for an AND gate). Simpler structurally — `BuildingPathSimulationSystem<,,>` works out of the box with existing connector types — but conflates "what flows through this connector" with "is this piece joined to its neighbour", and any building author would have to sacrifice one of their gameplay connectors for joining. Also causes a real bug class: two adjacent AND gates the player wires with a signal would accidentally fuse into one logical gate, since the wiring would create matching pivots.
- **Per-instance "joined to neighbour" flag.** Each piece would carry a `Configuration` field marking which neighbours it's joined with. More flexible (allows building shapes the path system doesn't support) but offloads matching work onto us — we'd need our own pattern matcher rather than reusing `BuildingPathSimulationSystem<,,>`, and we'd carry per-instance state we'd otherwise avoid.
- **Head + body piece kinds.** Introduce separate `MetaBuildingDefinition`s for the "head" of a dynamic layout and the "body" pieces, requiring exactly one head per logical building. Verbose for mod authors (two definitions per dynamic-layout building) and doesn't avoid the join-connector question — the head/body still need some matching mechanism — so this is orthogonal, not an alternative.

## Consequences

- **`DynamicLayout` is path-shaped in v1.** `BuildingPathSimulationSystem<,,>` enforces exactly one input and one output per piece (constructor throws otherwise). A piece in a dynamic layout therefore has exactly one join-in and one join-out, and the resulting logical building is a 1×N chain along the joining axis. This fits the AND gate and most building expansions we've imagined. Non-path expansion (1×1 → 2×2 → 3×3) is deferred — see the matching GitHub issue.
- **Bends are free.** A piece whose join-in and join-out are on perpendicular faces (rather than opposite faces) routes the logical building around a corner. As long as pivots line up across pieces, the system doesn't care about the piece's class or shape. The same is already true for belts (straight, left-bend, right-bend all participate in the same conveyor).
- **The framework needs to define and surface the join-connector type.** `ExpandableX-Core` introduces a new connector type (e.g. `JoinIn` / `JoinOut`) usable on any `MetaBuildingDefinition` that registers a `DynamicLayout`. The connector type implements `IBuildingIO` so the game's `BuildingPathSimulationSystem<,,>` can be instantiated with it.
- **Mod authors author the join connectors per piece definition.** Authoring is a per-`MetaBuildingDefinition` task — set the join-in/join-out positions in the definition asset. No code is needed beyond registering the layout. Building authors who don't use `DynamicLayout` never touch join connectors.
- **The game's atomic system for the affected building must be replaced.** For the AND gate, Shapez creates one `AtomicStatefulBuildingSimulationSystem<LogicGateAndSimulation, ...>` per definition. `ExpandableX-Core` will register an `ISimulationSystemsRewirer` (via ShapezShifter's `Hijack` surface) that removes those atomic systems for any `MetaBuildingDefinition` we've registered a `DynamicLayout` for, and inserts our path-based system in their place.
