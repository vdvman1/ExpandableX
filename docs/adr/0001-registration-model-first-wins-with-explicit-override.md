# Registration model: first-wins (yielding) with explicit override (last-wins)

`ExpandableX-Core` provides two registration APIs for declaring a `BuildingDefinition` as expandable:

- **`Register(...)`** — first-wins, yielding caller. Succeeds and claims the `BuildingDefinition` if nothing else has registered it yet. If another registration already exists (regardless of which API created it), this call becomes a no-op with a warning stating the calling mod is no longer needed. It never errors and never clobbers.
- **`RegisterOverride(...)`** — last-wins, loud-by-default. Always succeeds and always replaces whatever exists (or doesn't). If multiple `RegisterOverride` calls target the same `BuildingDefinition`, the last to load wins, resolvable via `meta.json` load order.

The split exists so compatibility mods and first-party mods can both call the natural `Register` API safely, and so deliberate clobbering requires opting into a separate, named API call.

## Considered Options

- **Model A — Two APIs: `Register` (yielding) + `RegisterOverride` (intentional clobber).** *(Chosen.)* `Register` is the safe default; mods that don't know about each other can both call it and the second one gracefully steps aside with a clear warning. Intentional clobbering is loud (it's a different function name).
- **Model A-strict — `Register` errors on conflict; explicit `RegisterOverride` for clobbering.** *(Earlier draft of this ADR.)* Erroring on conflict surfaces collisions immediately but breaks the killer use case: a compatibility mod going inert automatically when the modded mod ships first-party expandability. Forces users to uninstall/update compatibility mods rather than letting them quietly retire themselves.
- **Model B — Last-wins by default (single API).** Whoever loads last wins. Quietly clobbers prior registrations if two mods unknowingly target the same `BuildingDefinition`. Same load-order dependency as Model A but with riskier failure mode and no graceful inert-compatibility-mod path.
- **Model C — Layouts compose additively (union).** Each mod adds layouts; no ownership; the active set is the union. Friendly for add-on packs (an "ExtremeCutter" mod adding 8-piece) but adds API complexity for replacement and removes any single source of truth for a building's layout set.
- **Model D — Per-layout ownership, composition + override.** Like C, but each layout has an owner and an explicit override-by-id API. Most flexible, most complex.

## Consequences

- **Compatibility mods retire themselves gracefully.** When the modded mod later ships first-party expandability, both `Register` calls happen, the first-loaded wins by virtue of being first, and the second emits a "this mod is no longer needed" warning visible to the user. No update or uninstall required.
- **Override is the only way to deliberately clobber.** `Register` cannot accidentally overwrite. Mod authors who want to replace someone else's registration must consciously choose `RegisterOverride`; their name appears in the registry as the override-er, not the registerer.
- **Add-on mods are not a first-class case in v1.** A future "ExtremeCutter Layouts" mod that wants to *extend* `ExpandableX`'s cutter layouts (rather than replace them) cannot do so cleanly under Model A — it would need to use override and reproduce all existing layouts. If this use case becomes important, Model D is the upgrade path; the migration would change semantics, so it's a breaking change.
- **Deterministic load order matters, but only for `RegisterOverride`-vs-`RegisterOverride` conflicts.** For the typical `Register`-vs-`Register` case, load order decides who wins (and who warns), but both outcomes are valid. Override-vs-override is the only genuinely ambiguous case and must be resolved by `meta.json` dependencies.
- **No removal API in v1.** A registration cannot be un-registered or replaced with "nothing"; only overridden. If a future need emerges to mark a building *no longer* expandable, an explicit `RegisterRemoval` would be added.
