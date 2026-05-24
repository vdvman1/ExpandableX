# Registration is per MetaBuildingDefinition; static layouts swap, dynamic layouts compose

Supersedes [ADR-0003](./0003-register-per-buildingdefinition-variant-pair.md).

`ExpandableX-Core` accepts a registration per `MetaBuildingDefinition`. There is no `Variant` axis on top of that — what we previously thought were variants are themselves separate `MetaBuildingDefinition`s in Shapez's actual model (e.g. the mirrored cutter is its own definition, not a variant of the default cutter).

A registration carries a list of `StaticLayout`s and/or `DynamicLayout`s; these two layout kinds map to two **different runtime mechanisms**, and the framework must support both:

- **`StaticLayout` → swap.** Each `StaticLayout` points at one specific `MetaBuildingDefinition`. Transitioning between static layouts (e.g. expanding a cutter from `HalfCutterMetaBuildingDefinition` to `FullCutterMetaBuildingDefinition`) swaps the placed building's definition. Shapez handles the resulting tile/connector reconfiguration natively. This lets us reuse existing in-game definitions (the Full cutter already exists) instead of inventing new ones for every layout.
- **`DynamicLayout` → multi-piece composition.** A single `MetaBuildingDefinition` is paired with a matching rule that recognises N connected instances of that definition as one logical building. This is the same mechanism Shapez already uses for belts (N connected belt buildings → one conveyor `Simulation`). Necessary for unbounded-size buildings like an AND gate with N inputs.

## Considered Options

- **Per-`MetaBuildingDefinition` registration, with `StaticLayout` (swap) and `DynamicLayout` (composition) as the two layout-kinds.** *(Chosen.)* Matches Shapez's actual model (no `Variant` field anywhere; multi-tile single buildings are native via `Tiles[]`; existing definitions like `FullCutterMetaBuildingDefinition` can be reused). Splits the two genuinely different mechanisms cleanly.
- **Per-(`MetaBuildingDefinition`, `Variant`) registration with one composition-only implementation.** *(Original ADR-0003 framing.)* Based on a misunderstanding of Shapez (no `Variant` concept exists). And forcing everything through composition is wasteful when finite-size definitions like `FullCutterMetaBuildingDefinition` already exist and can be reused via swap.
- **Per-`MetaBuildingDefinition` registration with swap-only.** Would require us to author a new `MetaBuildingDefinition` per dynamic layout size — doesn't scale for unbounded expansion (AND gate).
- **Per-`MetaBuildingDefinition` registration with composition-only.** Would require ignoring the existing `FullCutterMetaBuildingDefinition` and reconstructing cutter expansion entirely from cutter-piece composition — discards modelling work the base game already did.

## Consequences

- **Two implementation paths to build, not one.** The framework needs both the swap mechanism (for `StaticLayout`) and a multi-piece pattern-matching mechanism (for `DynamicLayout`). The pattern-matching mechanism may end up being a wrapper over a ShapezShifter extension point or may require us to patch the game directly — TBD pending Shifter source analysis.
- **Static layouts can reuse existing in-game definitions for free.** The cutter `StaticLayout`s for sizes 2 and 4 reference `HalfCutterMetaBuildingDefinition` and `FullCutterMetaBuildingDefinition`, neither of which we author. Only the 3-piece and 6-piece hex-mode layouts need new definitions (and corresponding 3D models — see [[project-modelling-is-bottleneck]]).
- **Mirrored "variants" are just additional registrations.** A mirrored cutter is a separate `MetaBuildingDefinition` and gets its own registration with its own (mirrored) `StaticLayout`s. Mod authors with both default and mirrored variants will write two registrations. Future syntactic sugar (a `Mirror`/`Transform` helper that derives one set of layouts from another) is possible but not in scope for v1.
- **First-wins / explicit-override semantics from [[ADR-0001]] still apply**, now scoped to `MetaBuildingDefinition`. Two mods registering different `MetaBuildingDefinition`s never conflict; two mods registering the same one resolve via `Register` (yields with warning) or `RegisterOverride` (last-wins, loud).
- **`MetaBuildingDefinition` is the registration key.** We need a stable identifier — likely the `Id` string field on `MetaBuildingDefinition`, but its stability across game versions still wants verification.

## Open follow-ups

- Decide the exact `DynamicLayout` matching API (task #6, blocked on understanding Shifter's pattern-matching extension surface — task #4).
- Verify the painter's per-instance state mechanism for toggleable-connector flags (`Building` entity has not yet been decompiled).
