# ExpandableX

Two [Shapez 2](https://store.steampowered.com/app/2162800/shapez_2/) mods that let players *expand* or *stretch* selected in-game buildings — adjusting their size, adding inputs/outputs, and enabling/disabling specific connectors per instance.

> **Status: early design.** Code does not exist in this repo yet. What's here is the design language, the architectural decision records, and a few agent-tooling files used by the people working on it.

## The two mods

This repository contains both mods side-by-side:

- **ExpandableX-Core** — the library mod. Provides the registry, the shared expandability concepts, the UI shell, and the game hooks. Contains no base-game-specific knowledge.
- **ExpandableX** — the base-game consumer mod. Uses `ExpandableX-Core` to register expandability for selected base-game buildings.

The split lets other mods adopt `ExpandableX-Core` to make their own components expandable without forcing users to have expandability for base-game buildings.

## What "expandable" means

Two core mechanisms, each opt-in per building (no implicit defaults — buildings only become expandable when something registers them):

- **Composable expansion** — a single logical building the player perceives as one unit is actually N connected `Building` entities that Shapez's `SimulationSystem` pattern-matches into one `Simulation`. "Expanding" places more pieces. Used for growing a logic gate to take more inputs, or growing a cutter to produce more outputs.
- **Toggleable connectors** — a per-instance flag on a `SimulationConnector` that enables/disables it without changing the building's footprint. Used for things like disabling individual paint inputs on a painter to avoid sharing paint with adjacent buildings.

A building can use either, both, or neither.

See [CONTEXT.md](./CONTEXT.md) for the full domain language and a worked example dialogue.

## v1 scope

The first release of `ExpandableX` registers expandability for exactly three base-game buildings:

- **A single logic gate** — composable + toggleable, with unbounded `1×N` expansion (chosen for ease of modelling)
- **The cutter** (default + mirrored variants) — composable, with mode-conditional layouts (hex mode adds 3-piece and 6-piece options on top of the 2-piece and 4-piece base-mode layouts)
- **The painter** — toggleable-only, no composable expansion

The framework itself supports more than this slice exercises; subsequent releases will broaden coverage. Adding more buildings is gated by modelling effort, not code.

## Design

- [CONTEXT.md](./CONTEXT.md) — domain language, glossary, v1 scope, flagged ambiguities, worked example dialogue
- [docs/adr/](./docs/adr/) — architectural decision records:
  - [ADR-0001](./docs/adr/0001-registration-model-first-wins-with-explicit-override.md) — registration model: `Register` (first-wins, yielding) + `RegisterOverride` (last-wins, loud)
  - [ADR-0002](./docs/adr/0002-drag-handle-target-with-build-and-detect-stepping-stone.md) — drag-handle UX with build-and-detect proof-of-concept
  - [ADR-0003](./docs/adr/0003-register-per-buildingdefinition-variant-pair.md) — (superseded by ADR-0005) original (`BuildingDefinition`, `Variant`) framing, kept for history
  - [ADR-0004](./docs/adr/0004-dynamic-layout-as-sibling-to-static-layout.md) — `DynamicLayout` as a sibling concept to `StaticLayout`
  - [ADR-0005](./docs/adr/0005-register-per-metabuildingdefinition-with-dual-implementation.md) — registration per `MetaBuildingDefinition`; static layouts swap, dynamic layouts compose
- [docs/research/](./docs/research/) — notes from decompiling the game to ground design decisions:
  - [shapez2-internals.md](./docs/research/shapez2-internals.md) — findings about Shapez 2's building model, connector layout, and pattern-matching

## Modding references

Shapez 2 modding documentation lives outside this repo:

- [Official modding documentation](https://tobspr-games.notion.site/shapez2-modding-documentation) (Notion)
- [Modding page on the wiki](https://shapez2.wiki.gg/wiki/Modding)
- [Unofficial modding docs (Raphdf201)](https://shapez2.raphdf201.net/) — may be slightly outdated; written during early access

## Asset policy

This project will not include AI-generated artistic assets — textures, models, sounds, or anything else along those lines. Placeholder assets, where needed, come from the base Shapez 2 game or are hand-authored. This is a deliberate ethical stance.

## License

See [LICENSE](./LICENSE).
