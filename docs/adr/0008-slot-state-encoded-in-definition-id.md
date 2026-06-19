# Connector-slot state is encoded in the MetaBuildingDefinition id, not in per-instance Configuration

Supersedes the per-instance-`IBuildingConfiguration` parts of [ADR-0005](./0005-register-per-metabuildingdefinition-with-dual-implementation.md) and [ADR-0007](./0007-head-body-tail-singleton-with-phantom-up-joins.md).

A `Connector slot`'s role (`Input` / `Output` / `Disabled` / `Enabled`) is **not** stored as per-instance state on a placed `Building`. Instead, every reachable combination of slot roles for a piece is materialised at session init as its own `MetaBuildingDefinition`, whose **id encodes the slot state** as a fixed-width string of role characters (`I`/`O`/`D`/`E`), one per slot:

```
PainterMetaBuildingDefinition_ExpandableXConfigurable_ID
                                                      └┴─ slot 0 = Input, slot 1 = Disabled
```

Changing a slot's role is therefore **a definition swap** — the same operation already used for static-layout expansion — to the variant whose id encodes the desired state. There is no `IBuildingConfiguration`, no `Sync`, and no value-equality `Equals` for slot state.

## Why

The driving constraint is that **connectors are read statically off the `MetaBuildingDefinition`**. The game builds a building's connector arrays (`BuildingConnectorsOfType<T>()` etc.) from the definition, once, when the simulation systems are wired — there is no per-instance hook that consults `Building.Configuration` to decide which connectors exist. An `IBuildingConfiguration` could *store* a chosen role, but nothing in the game would *read* it when assembling the connector layout or wiring the `SimulationGraph`. The only lever that actually changes a building's connectors is *which definition it is*. So the state that determines connectors must live at the definition level, which means one definition per distinct connector layout.

This was discovered twice — once before prototyping and again confirmed during the `prototypes/variant-encoding/` session — hence this ADR, to stop it being rediscovered a third time.

## Considered Options

- **Encode slot state in the definition id; one variant definition per reachable slot combination; slot change = swap.** *(Chosen.)* The only model where the thing that controls connectors (the definition) is also the thing that carries the state. Reuses the existing swap mechanism for both expansion and slot changes. Maximally blueprint-stable: zero per-instance ids, and two identically-configured pieces literally share one definition, so copy/paste and save/load are exact by construction. Deletes the entire serialization surface (`Sync`) and its bugs.
- **Per-instance `IBuildingConfiguration` holding slot roles.** *(Originally documented in 0005/0007.)* Rejected: connectors are static per definition, so a per-instance role would be stored but never consulted when building the connector arrays — the feature simply wouldn't function. Also adds a `Sync`/`Equals` surface we'd have to keep blueprint-correct.
- **Patch the connector-array construction to consult per-instance state.** Would make `IBuildingConfiguration` viable but requires invasive Hijack-layer patching of core simulation wiring, fights the engine's static-connector assumption throughout, and risks breaking every other building. Not worth it when the swap model is available and simpler.

## Consequences

- **Eager variant explosion.** Each piece with N slots over an alphabet of A reachable roles generates up to Aᴺ definitions, registered at session init (the `Simulator`'s system list is fixed at construction — no runtime `AddSystem`, so lazy generation isn't available). Local predicates prune impossible combinations before generation. The real count is smaller than the prototype's 4ⁿ worst case: directional signal slots are 3-role (`Enabled` auto-corrects away), and chain pieces have *fewer* gameplay slots than a singleton because join connectors consume faces — a 1-tile body has ~2 gameplay slots, head/tail ~3 (the phantom join points `Up`, costing no planar face). So the AND gate is on the order of 3² + 3³ + 3³ across body/head/tail plus the singleton family, not 4³ × 4. Acceptable for v1's three buildings. Whether the framework needs a variant budget or a different mechanism before scaling past a handful of buildings is an open follow-up, tracked in `prototypes/variant-encoding/README.md` ("Open / deferred").
- **The "configurable singleton" is now a set of variant definitions, not one definition with config.** The role distinction (default vs configurable singleton) survives — see below — but the configurable side is a family of id-encoded variants rather than a single `MetaBuildingDefinition<TConfig>`.
- **`MetaBuildingDefinition<TConfig>` is no longer used for slot state.** Pieces are plain `MetaBuildingDefinition`s. We may still attach a small marker via `CustomData` so the framework can recognise its own generated variants, but it carries no player-adjustable state.
- **Default singleton still matters for save compatibility.** A fresh placement and existing saves still use the unmodified base-game definition. The first slot adjustment swaps it to the appropriate id-encoded variant, exactly as the old default→configurable swap did — only the target is now a specific encoded variant, not a single configurable definition.
- **Slot change and expansion are the same primitive.** Both compute a target definition id and swap the placed building to it (Delete+Create on the structural-mutation queue). The drag-handle and the slot dropdown differ only in *how they pick the target id*.
- **Variant id is the unit of blueprint identity.** Because the id fully encodes slot state, a blueprint records the variant id and pastes an identical building. This is what satisfies [[project-blueprint-compatibility]] without per-instance identifiers.
