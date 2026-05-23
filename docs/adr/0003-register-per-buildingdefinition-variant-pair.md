# Registration unit is (BuildingDefinition, Variant), not BuildingDefinition alone

Expandability is registered against a (`BuildingDefinition`, `Variant`) pair, not against a `BuildingDefinition` alone. Every layout in a registration applies to that exact pair. For `BuildingDefinition`s without meaningful variants, callers register under a default/sole variant id; a syntactic shortcut (e.g. `Register(buildingDefinitionId, layouts)` overload) hides the variant from callers who don't care about it.

## Considered Options

- **Per (BuildingDefinition, Variant) pair.** *(Chosen.)* Each variant has its own registration with its own layouts. Matches the fact that variants are geometric — a mirrored cutter expands in a different direction than the default cutter, so the layouts are physically distinct.
- **Per BuildingDefinition with a `variantFilter` on each layout.** A single registration would hold all layouts for all variants, with each layout declaring which variants it applies to. Slightly fewer registration calls, but pushes variant-dispatch logic into the layout instead of the registry, and complicates `Register`/`RegisterOverride` semantics (which variants does an "override" cover?).
- **Per BuildingDefinition, variant-agnostic.** A single registration covers all variants with shared layouts. Doesn't work because mirrored variants typically need physically different layouts.

## Consequences

- **Mirrored variants get separate registrations.** A mod adding expandability to the cutter writes two `Register` calls (one for default, one for mirrored) with mirror-image layout sets. If this becomes verbose, we can later add a `Mirror`/`Transform` helper that derives the mirrored variant's layouts from the default variant's — but that's syntactic sugar; the underlying model stays per-pair.
- **Buildings without variants are clean.** A painter — which has no meaningful variant axis — registers once against `(painter, default)`. The shortcut overload hides the second argument.
- **`Register`/`RegisterOverride` semantics scope to a pair.** Two mods registering different variants of the same `BuildingDefinition` do not conflict — they're different keys. This makes adding partial coverage feasible (e.g. a mod could register only the default cutter and leave the mirrored cutter unregistered).
- **Conflict detection happens at the pair level.** A second `Register` for `(cutter, default)` yields to the first; a `Register` for `(cutter, mirrored)` succeeds independently.
- **Variant identity needs to be stable.** The mod author refers to a specific variant by id. If Shapez 2's variant ids are unstable across game versions (e.g. an internal int that re-orders), registrations would silently target the wrong variant. Stability of variant ids needs verification when we decompile.
