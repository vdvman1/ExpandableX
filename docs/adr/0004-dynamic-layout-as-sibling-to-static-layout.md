# DynamicLayout as a sibling concept to StaticLayout

`Layout` is an umbrella for two distinct registry-entry kinds: `StaticLayout` (enumerated `PieceSpec` list, used when valid sizes are finite — e.g. the cutter's 2/3/4/6) and `DynamicLayout` (a rule-based matcher over arbitrary connected arrangements, used when sizes are unbounded — e.g. an AND gate that accepts N inputs for arbitrary N). A single (`BuildingDefinition`, `Variant`) registration may carry static layouts, dynamic layouts, or a mix; at runtime, a player-placed arrangement matches at most one layout.

## Considered Options

- **Two sibling types: `StaticLayout` + `DynamicLayout` under a shared `Layout` umbrella.** *(Chosen.)* The data shapes are genuinely different: static is a list of pieces; dynamic is matching/connector-computation logic. Sibling types under a common interface keep registration uniform (one list of `Layout`s per registration) while preserving the structural difference.
- **One type with a `kind: static | dynamic` discriminant.** Same effect from the registry's perspective, but the static and dynamic cases share zero fields, so the type is awkward — every consumer immediately pattern-matches on the discriminant.
- **Dynamic-only model — every layout is a matcher.** A static layout would be expressed as a degenerate matcher that matches exactly one arrangement. Theoretically clean, practically painful: most layouts are static, and forcing static cases through a generic matcher API adds boilerplate and erases the "I want exactly these N pieces here" intent.
- **Static-only model — emulate dynamic via "infinite enumeration" or many static layouts.** Cannot represent unbounded expansion (AND gate N inputs) without either capping `N` artificially or generating layouts up to some sentinel limit. Both options leak the limit into the API and produce a worse player experience.

## Consequences

- **Two registry-entry shapes the framework must support.** The `SimulationSystem` extension has to handle both: scan for exact arrangements (static) and evaluate rules against arrangements (dynamic). The base mechanism is the same (pattern-matching over connected `Building`s), but the inner logic differs.
- **Mod authors pick the right kind per layout.** Where a building is genuinely finite (cutter), the static form is more declarative and easier to read. Where it's genuinely unbounded (AND gate), the dynamic form is the only option. We document this in CONTEXT.md so mod authors don't reach for the heavier dynamic form by default.
- **A single registration can mix static and dynamic.** Useful theoretically — e.g. a building with a few "blessed" small layouts (`StaticLayout`s) plus an "any other connected shape" `DynamicLayout` as a catch-all. We don't have a v1 building that needs this, but the model permits it. At runtime, an arrangement that matches both a static and a dynamic layout: the static layout wins (it's more specific). This precedence rule keeps semantics predictable.
- **DynamicLayout's API shape is its own decision (task #6).** This ADR commits to the *existence* of a `DynamicLayout` sibling. *How* mod authors express the matching/connector logic — code callbacks vs declarative DSL vs helper library — is the next sub-decision, recorded separately when settled.
- **Drag-handle UX is unaffected at the concept level.** A drag-handle on a `StaticLayout` snaps to the next valid enumerated layout; on a `DynamicLayout` it can extend continuously. Both are natural fits.
