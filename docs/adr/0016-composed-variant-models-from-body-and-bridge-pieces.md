# Composed variant models from a body piece plus per-connector bridge pieces

**Status:** accepted — a partial implementation of issue #8 (config-driven variant rendering). Builds on [ADR-0008](./0008-slot-state-encoded-in-definition-id.md) (slot state as generated variant definitions) and [ADR-0009](./0009-variant-generation-synthesizes-connectors.md) (variant connector synthesis). The full 3D "9-slice" system of #8 remains future work.

Synthesised variant definitions currently clone the base definition's `CustomData` wholesale — including its `IBuildingDrawData` — so every generated variant shows the base's authored body model, only visually rotated by `GridRotation`. That model has the per-connector attachment geometry (the bit joining a connector's cap to the building body) **baked in**, so a `Disabled` slot leaves an orphaned port on the body and an `Input`↔`Output` flip can't change the body's appearance. Authors were forced toward (near) rotationally-symmetric, configuration-agnostic models.

Decompilation clarified the game's own model, which reframes the problem:

- The in-world renderer (`StaticBuildingMeshBuilder`) draws the authored `MainMeshLOD` body **plus** a per-connector cap/stand/socket (`BuildEndCaps`) chosen by each connector's runtime **type + `IOType`**, sourced from the theme's `VisualThemeBaseResources`. So **show/hide and "different kinds" of the stock caps are already driven by the connector data we synthesise per variant** — a disabled slot drops its cap, an input↔output flip swaps it, a type change swaps the socket. That part is effectively free today.
- The genuine gap is the **body**: the authored `MainMeshLOD` and the connector-attachment geometry welded into it.
- The game bakes `MainMeshLOD` and derives the blueprint/preview/isolated meshes from it via `BuildingDrawDataFactory` / `BuildingMeshGenerator`, combining meshes through `MeshBuilder.AddTranslateRotate` + `GenerateSingleMeshMax65KVertices`.

**Decision.** Add an **opt-in** model-composition step to variant generation. An author declares, on a `PieceSpec`, a **Body piece** (the clean main model with connector-attachment geometry stripped) and **Bridge piece**s keyed by **(medium, role)** — e.g. `(Wire, Input)`, `(Fluid, Enabled)` — with optional per-slot overrides. For each **synthesised** variant, the framework bakes `Body + {bridge for each live connector's role}` into a single `MainMeshLOD` and regenerates the derived draw-data meshes, then attaches a fresh `IBuildingDrawData`. The stock theme cap path is left untouched. A piece that declares no pieces keeps today's clone-the-base behaviour.

- A `Disabled` face contributes no bridge (flat body face). A `Join` face contributes a **seam piece** (the `Join`-role bridge, medium-agnostic); each of the two joined pieces bakes its own half.
- Composition runs **only** on synthesised variants. The `MatchesBase` combination, **Variant override** targets, and the **Default singleton** keep their own hand-authored models, so an author's pieces must visually match those at the boundary (benign for the painter, whose synthesised variants only ever have *fewer* junctions than the vanilla default).
- Bridges bake into the definition's **local frame**, so they rotate with `GridRotation` canonicalisation for free — a local-East bridge lands on world-North with the body.

## Considered Options

- **Static bake per variant (chosen).** Compose `MainMeshLOD` once per generated definition, using the game's own `MeshBuilder` path; regenerate the derived blueprint/preview/isolated meshes so the drag-preview ghost ([ADR-0014](./0014-drag-handle-control-surface.md)) and build-menu preview stay consistent. No renderer hook. Variants are already a finite enumerated set of definitions, so a finite set of baked bodies is the natural fit, and local-frame baking gives correct rotation for free.
- **Dynamic per-frame rendering.** Hook the in-world renderer and draw bridge pieces each frame from live slot state. Rejected for this step: needed only when the visual set is not finitely enumerable (the full dynamic 9-slice future work), costs a per-frame recompute, and would have to redo the rotation mapping that baking gets for free. Kept in reserve for #8 proper.
- **Reuse the theme end-cap path for the bridge geometry.** Rejected: the end-cap meshes are theme-global generic caps with no per-building hook for custom attachment geometry, and the attachment geometry is building-specific art, not a stock cap.
- **Model every variant by hand (status quo).** Rejected: the modelling bottleneck is the main gate on adding buildings; a body + a few bridges covers a combinatorial set of variants from a handful of assets.

## Consequences

- **Opt-in and non-breaking.** With no pieces declared, rendering is byte-for-byte today's behaviour, so the framework ships for v0.1 with no new models required; authors add pieces per building as modelling time allows.
- **Draw data is regenerated, not cloned.** Synthesised variants build their own `IBuildingDrawData` (main + isolated/combined blueprint + preview) from the composed body and synthesised connector data, rather than inheriting the base's. Slightly more work per variant at session init.
- **A body piece is required if any bridge is declared** — without it there is no clean body to attach bridges to, and the base's baked-in ports would remain. Missing-body-with-bridges is a loud registration error.
- **Mirroring is deferred.** Composed bodies do not implement `IBuildingMirrorableCustomDrawData` yet; mirrored cases (the cutter) resolve to hand-authored **Variant override** targets with their own models, so no synthesised mirror is needed for v1.
- **IP stance unchanged.** Bodies/bridges are author-modelled (often extract-and-tweak of base-game meshes, which is accepted); this is code-only plumbing and generates no assets. See the model-pipeline note in project memory.
