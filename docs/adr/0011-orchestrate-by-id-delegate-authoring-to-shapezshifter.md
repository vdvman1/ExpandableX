# ExpandableX-Core orchestrates definitions by id; building authoring is delegated to ShapezShifter

A `Registration` references every layout/piece definition **by id**. ExpandableX-Core treats base-game definitions (`Half`, `Full`) and consumer-authored definitions (`Hex3`, `Hex6`) identically — it sequences, swaps, and composes over their ids; it does **not** author new buildings.

The only definitions ExpandableX-Core *creates* are **synthesized slot-variants** (ADR-0008/0009): same building, connectors rewritten per slot roles. Anything with a genuinely new footprint, model, or simulation — e.g. the hex cutters — is **authored by the consumer mod through ShapezShifter's normal building-creation path** (and its custom simulation registered the same way), then referenced by id in the family's `Layout`s.

## Why

The hex cutters need a footprint, a model, and a **cut simulation that does not exist in the base game**. Building those is exactly what ShapezShifter (and the official modding API) already does well. Re-implementing a building-authoring API inside ExpandableX-Core would duplicate that surface, couple the framework to the details of every connector medium and simulation type, and grow it far beyond its job — which is *expandability* (moving an already-placed building between sizes/connector layouts), not building creation.

Referencing by id keeps the boundary crisp: ShapezShifter (or the base game) owns *what a building is*; ExpandableX-Core owns *how a placed building expands/shrinks/reconfigures* across a family of those buildings.

## Considered Options

- **Orchestrate by id; delegate authoring to ShapezShifter.** *(Chosen.)* Smallest framework surface, leverages the existing modding API for footprints/models/simulations, keeps custom simulations (hex cut) a consumer concern. Consumers author hex via ShapezShifter and hand ExpandableX-Core the ids.
- **A building-authoring API inside ExpandableX-Core.** Rejected: duplicates ShapezShifter, couples the framework to connector/simulation specifics, and balloons scope. The user was explicit about not wanting this.
- **Generate hex definitions by synthesis (as we do slot-variants).** Rejected: synthesis only rewrites connectors on an existing base — it cannot invent a new footprint, model, or the hex-cut simulation. Hex is a genuinely new building, not a connector variation of an existing one.

## Consequences

- **Consumers author new sizes/buildings via ShapezShifter** and register them with ExpandableX-Core by id. The hex cutters and their custom cut simulation are consumer + ShapezShifter responsibilities; ExpandableX-Core only sequences `Half` → `Hex3` → `Hex6` across the ids.
- **The decode catalog maps every layout def id (authored or base-game) to its `Registration` + `Layout`**, so the runtime can find a placed building's family and compute its available expansions regardless of who authored the definition.
- **Synthesized slot-variants remain Core's job**, including re-attaching the base definition's simulation to each variant id. Generalising the current painter-specific attachment into "re-attach the base's simulation to its variants" is the one simulation concern Core keeps — distinct from authoring a *new* simulation.
- **Narrowly-scoped helpers are permitted later, layered on ShapezShifter, never replacing it:** deriving the head/body/tail/singleton split for a dynamic layout from a single authored base (so authors don't hand-write four near-identical pieces), and connector-variation rendering (so a disabled connector's mesh can be hidden). These reduce authoring toil for the dynamic-layout and visual cases; they do not constitute a general building-creation API.
- **A new building medium or simulation type is added in ShapezShifter/the consumer, not here.** ExpandableX-Core never needs to learn about it beyond referencing the resulting definition id.
