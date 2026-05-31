# Variant generation synthesizes connectors from (pivot, role), not filter-only

Builds on [ADR-0008](./0008-slot-state-encoded-in-definition-id.md).

When the framework generates a variant `MetaBuildingDefinition` for a given slot-role combination, it **rebuilds the connector arrays from scratch** — for each `Connector slot` it constructs the connector type demanded by that slot's resolved `Role`, placed at the slot's resolved pivot (`Position_L` + `TileDirection`). It does *not* merely filter the base definition's existing connectors.

Per slot, generation emits:

| Resolved role | Emitted at the slot's pivot |
|---|---|
| `Disabled` | nothing — the connector is omitted |
| native type (e.g. `Input` for an input-typed reference) | the connector type matching the role |
| flipped type (e.g. `Output` for an input-typed reference) | a **newly constructed** connector of the opposite directional type, same pivot |
| `Enabled` (junction reference) | the junction connector |

This requires a small **connector factory**: given `(role, position, direction, medium)` produce the correct `BuildingItemInput/Output`, `BuildingFluidInput/Output/Junction`, or `BuildingSignalInput/Output`. The set of connector types is finite and enumerable.

## Why

A `Connector slot` may be tri-state — `Input` on one piece, `Output` on another (the AND gate moving its gameplay output between pieces, per `CONTEXT.md`). `Input` and `Output` are **different connector classes** (`BuildingSignalInput` vs `BuildingSignalOutput`), and the base definition carries only one of them at the slot's pivot. Producing the variant where the slot takes the other role is impossible by filtering — there is no connector of that type in the base to keep. The framework must construct one. Filtering is therefore only the degenerate case (painter: a junction is kept when `Enabled`, removed when `Disabled`), not the general mechanism.

## Considered Options

- **Synthesize connector arrays from (pivot, resolved role) via a connector factory.** *(Chosen.)* Supports tri-state directional slots, keeps the base definition clean (one connector per pivot, authored in its default role), and centralises connector construction in the framework. Filtering falls out as the special case where every slot is native-type-or-disabled.
- **Filter-only (`FilteredBuildingConnectorData` wrapper that removes disabled connectors).** *(The earlier framing in the prototype handoff.)* Sufficient for the painter but cannot express `Input`→`Output` flips, so the AND gate's gameplay-output relocation is unimplementable. Rejected.
- **Filter-only, but require the base definition to author *both* an input and an output connector at every tri-state pivot, and select one.** Avoids a connector factory, but pushes redundant connectors onto the modeller, makes the base definition's own (pre-variant) behaviour ambiguous (two connectors at one pivot), and still needs logic to suppress the unwanted one. More authoring burden and modelling confusion than synthesis, for no real benefit.

## Consequences

- **A connector factory is part of `ExpandableX-Core`.** It maps `(role, pivot, medium)` to a concrete connector instance. Adding support for a new connector medium (e.g. belt ports) means teaching the factory that medium.
- **The `Connector reference` resolves to a pivot, and its connector type fixes the slot's *active* role.** The configurable base must carry, at each slot's pivot, the connector **present** in its active role (junction → `Enabled`, input connector → `Input`, output connector → `Output`) — that is what `Of<T>(index)` indexes. A slot's *default* role is **independent** and may be `Disabled` (the AND gate's 3rd input is present in the base but defaults to `Disabled`). We validate at session init that the referenced connector's type matches the slot's *active* role, not its default role. This is why the base must be the connector **superset** — see [ADR-0010](./0010-variant-overrides-and-superset-configurable-base.md).
- **Geometry is shared across roles.** A flipped connector reuses the base connector's `Position_L` and `TileDirection`; only the connector *type* (flow direction) changes. The lift must confirm against a real signal connector that an output constructed at an input's pivot/direction is geometrically valid (expected: yes — the classes differ by flow, not geometry).
- **"FilteredBuildingConnectorData" is retired as a concept.** Any reference to it in handoffs or notes should be read as the synthesis pipeline's degenerate case.
