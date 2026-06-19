# Configurable base is the connector superset; specific variants may override synthesis with a named definition

Builds on [ADR-0008](./0008-slot-state-encoded-in-definition-id.md) and [ADR-0009](./0009-variant-generation-synthesizes-connectors.md).

Two related rules govern which `MetaBuildingDefinition` backs a given slot-role combination:

1. **The configurable base is the connector superset.** The definition a slotted piece's connector slots are declared against must carry *every* slot's connector, present in its active role. Variant generation only ever *removes* a connector (`Disabled`) or *flips its type* at a fixed pivot (`Input`↔`Output`); it cannot invent connector geometry. So the base must already contain a connector position for every slot. A slot's **default role is independent of the base** and may be `Disabled`.

2. **A registration may override specific variants with a named pre-existing definition.** Per piece, a registration may map a specific slot-role combination to an existing `MetaBuildingDefinition` id, which is used verbatim instead of a synthesised variant.

## Why

**Superset base.** The base-game AND gate has 2 inputs + 1 output; we want a 3rd input to be enableable. That connector does not exist on the base-game definition, and synthesis cannot fabricate its geometry/visual. The definition the slots reference must therefore be the maximal form — a 3-input/1-output definition we author — from which the 2-input default is synthesised by disabling the 3rd input. The configurable base's job is to define *where the connectors are and which exist*; the default roles' job is to drive *transitions* (static-sequence steps, dynamic expansion). These are different concerns, so the base is not "the default variant" — it is the superset.

**Overrides.** Three forces require a per-variant escape hatch from synthesis:
- The **all-defaults combination must map to the swap origin** (the base-game `Default singleton`), so an unconfigured building stays on the untouched base-game definition (save-compatibility, per [ADR-0007](./0007-head-body-tail-singleton-with-phantom-up-joins.md)).
- **Definitions that already model a variant** should be reused, not re-synthesised — the cutter's `HalfCutterMetaBuildingDefinition` and `FullCutterMetaBuildingDefinition` already exist in the base game.
- **Synthesis can produce a wrong model.** Removing a connector leaves its mesh port behind; until dynamic model composition exists (deferred — see prototype handoff), an author can supply a hand-modelled definition for that specific combination.

## Considered Options

- **Superset configurable base + per-piece variant-override map; synthesise the rest.** *(Chosen.)* Handles the AND gate's added input, reuses pre-existing definitions (cutter), preserves the swap-back-to-base-game property, and gives a per-variant model escape hatch — all without a dynamic-model system in v1.
- **Require the base to be the default variant and synthesise additions.** Impossible: synthesis cannot add connector geometry, so a default 2-input base could never yield a 3-input variant.
- **No overrides; synthesise every variant including all-defaults.** Would generate a near-duplicate of the base-game AND and of `FullCutterMetaBuildingDefinition` instead of reusing them, breaking save-compatibility (unconfigured buildings would sit on a synthesised def) and discarding existing models.

## Consequences

- **`PieceSpec` carries three things together:** the configurable base id, the connector slots (with references resolved against that base), and the variant-override map. This co-location is deliberate — they are one authoring unit.
- **Variant id of an overridden combination is the named definition's own id**, not the `_ExpandableXConfigurable_<chars>` synthesised form. Id-as-truth (ADR-0008) still holds because decoding uses a **session-init dictionary** (`def id → (registration, piece, slot state)`) covering both synthesised and overridden ids — we never parse role characters to decode. The role-character suffix exists only to give synthesised variants unique, legible ids.
- **The all-defaults override is the swap-back target.** When the player returns every slot to default, the framework swaps to the all-defaults override (the base-game default singleton) rather than a synthesised all-defaults variant.
- **Authoring validation at session init:** the configurable base must contain a connector matching each slot's active role at the referenced index; each override target must exist and its connector layout must match the combination it claims to represent (sanity check, best-effort). Failures are logged loudly and the registration is skipped rather than half-applied.
- **The configurable base may need its own model.** Contrary to [ADR-0007](./0007-head-body-tail-singleton-with-phantom-up-joins.md)'s assumption that the configurable singleton "shares the default's visual, so it adds no modelling work," the AND gate's 3-input base is new geometry. Modelling cost stands where the connector set genuinely differs from the default singleton.
