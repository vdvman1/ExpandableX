# Drag-handle control surface: one gesture over both expansion engines, injected by a BuildingsIdle detour

**Status:** accepted — realizes [ADR-0002](./0002-drag-handle-target-with-build-and-detect-stepping-stone.md) (drag-handle as the target UX) and rides the selection/HUD detours of [ADR-0013](./0013-network-selection-atomic-with-focus-piece.md). Resolves issue #5.

Static-`Sequence` (swap-based) and `Network` (multi-piece) grow/shrink are logically the same player action but were surfaced as two separate HUD button groups in `ConfigurableVariantModules` — scaffolding from the ADR-0002 stepping stone. We replace both with a **single drag-handle control surface**: when a `Logical building` is selected, each growable outer face shows a directional **expansion handle**; dragging **outward** grows and **inward** shrinks. The gesture is uniform; only granularity differs — a `Network` moves one `Building` per dragged cell, while a `Sequence` snaps to whichever step's footprint the cursor sits within along the axis, flipping to the next step (in the drag's direction, skipping locked steps) once the cursor passes the current step's bounds — so cells-per-step tracks each step's actual extent rather than a fixed 1:1 (exact thresholds tunable). Either way the target `MetaBuildingDefinition`'s ghost previews the result. The whole drag commits as one undoable action, clamping at the first cell a per-cell check (shape limit, occupancy, incidental fusion, network predicates) rejects, with the reason shown. The per-face HUD buttons are removed; `Connector slot` role buttons stay in the side panel.

The two engines (`SequenceEngine`, `NetworkExpansionEngine`) are **not** unified underneath — the "one control surface" is the player's gesture, not the API. Multi-tile-per-drag needs the grow chain computed *ahead of* the real map (grow cell 1, then compute cell 2 as if cell 1 existed, …); that is an **additive forward-simulation helper** over the existing per-tile logic, not a refactor.

## Scope (v1)

- **Per-piece-face only (Axis A).** A drag changes one piece's face, with magnitude running along that face's axis. **Whole-edge expansion** (one handle growing the whole contiguous run perpendicular to the face) is deferred to **#15** — it has well-defined semantics even for `ShapeLimits.Free` shapes (expand every piece in the perpendicular run), so it is a follow-up, not a blocker.
- **Expansion decoupled from the `Focus piece`.** Handles act on the whole selected building (a face is grabbed directly in the world); the focus piece governs only slot config.
- **Inward-drag shrink** only where building material lies behind the face along the drag axis, and only on single-join ends. Looped/branched shapes can't shrink piece-by-piece (**#12**); inward drag stays blocked there. Handles light only their live directions.

## Injection mechanism

Shapez's `BuildingsIdle` per-frame world input/draw exposes **no ShapezShifter rewirer seam** (the same wall ADR-0013 hit for selection/HUD gating). So the handles are drawn, and the drag captured, by a **behaviour-altering detour** into that path, gated on a logical building being selected. **Arbitration:** a press that hits a drawn handle is claimed by the handle (grow/shrink) and suppressed from area-select/camera; a press anywhere else is left entirely untouched, so existing input behaviour is unchanged and the blast radius is just "gestures starting on our own handle." Right-click / Esc cancels an in-progress drag, committing nothing. Visuals reuse base-game world-indicator infra (no new artistic assets); exact appearance is iterated in-game.

## Considered Options

- **Drag-handle surface over both engines, detour-injected.** *(Chosen.)* Delivers the ADR-0002 target UX and the issue-#5 unification at the gesture level, keeping the engines and the easier authoring API intact.
- **Keep the per-face buttons, just unify their option model.** Rejected: the buttons were always temporary scaffolding; "unify the API" was never the goal — the player-facing gesture is.
- **±1 tile per drag (no forward-simulation).** Rejected: reduces handles to buttons-you-drag, losing the "drag, don't click repeatedly" payoff that motivated the whole surface.
- **Whole-edge expansion in v1.** Deferred (#15) to keep the competition-deadline scope small; per-piece-face is the smaller, self-contained slice.
- **Modifier-key to engage handle-drag.** Rejected for v1: handle-hit-first-priority needs no modifier and avoids teaching one; a modifier may distinguish per-piece vs whole-edge later (#15).

## Consequences

- **A second behaviour-altering detour** (after ADR-0013), version-fragile against Shapez's input/HUD internals — accepted because no rewirer seam decouples `BuildingsIdle` world input/draw.
- **No keyboard/menu fallback for expansion in v1.** Expansion is drag-handle-only after this change; a configure-menu accessibility affordance (floated in ADR-0002) remains future work.
- **Always-visible blocked reasons are lost.** Today a blocked grow is a greyed button with its predicate text; that feedback now appears only contextually, when a drag clamps. Accepted as the cost of removing the buttons.
- **Forward-simulation is additive**, so the "engines stay as-is" constraint holds unless implementation later forces otherwise.
