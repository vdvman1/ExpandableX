# Drag-handle as target UX, with build-and-detect as a development stepping stone

The player-facing interaction for expanding a building is **drag-handle**: the base `Building` shows expansion handles on its sides; dragging a handle extends the logical building and auto-places the additional `Building` entities. Build-and-detect (no UI, framework infers layouts from manually-placed adjacent pieces) is built first as an internal proof-of-concept to validate the pattern-matching extensions, then we move to drag-handle as soon as possible. If the deadline forces it, the proof-of-concept ships; the goal is to ship drag-handle.

## Considered Options

- **Drag-handle only (target).** *(Chosen as the destination.)* Anchored to a specific building, so the "which building does this attach to" ambiguity cannot arise. Discoverable: players see the handles and understand the feature exists. Higher implementation cost (placement preview, snap-to-valid-layout, gesture handling, undo).
- **Build-and-detect only.** Cheapest implementation — pure `SimulationSystem` pattern-matching, no new UI. But it has two showstopper problems for real use: (a) buildings of the same type can already be placed adjacent for unrelated reasons, so the framework cannot tell "this is an expansion" apart from "these are two normal buildings"; (b) when a single cell separates two buildings of the same type, an expansion placed in that gap is irresolvably ambiguous about which building it attaches to.
- **Configure-menu.** A right-click panel listing valid layouts. Cheaper than drag-handle, but feels less natural for spatial expansion (the cutter going 2→4 outputs is fundamentally about adding tiles in space, not picking a menu item).
- **Drag-handle straight from day one, no stepping stone.** Cleanest in retrospect, but bigger up-front risk: we'd be writing drag-handle UI against an unverified `SimulationSystem` extension. If the pattern-matching extension turns out to be infeasible, we've also wasted the drag-handle work.

## Consequences

- **Build-and-detect is intentionally incomplete.** The proof-of-concept doesn't try to resolve the ambiguity cases — it picks an arbitrary tie-breaker (e.g. "leftmost base building wins") and moves on. Players shouldn't be exposed to it for long.
- **Drag-handle is the v1 target, not an aspiration.** We should plan as if drag-handle ships; the proof-of-concept is a safety net, not the default. Treating build-and-detect as "the real plan" risks the deadline collapsing us into the worse UX.
- **The `SimulationSystem` extension does most of the heavy lifting.** Whether the player drags a handle or places pieces manually, the underlying mechanism is the same: `SimulationSystem` pattern-matches a connected layout of `Building` entities into a single `Simulation`. Drag-handle is mostly UI on top of that.
- **No configure-menu in v1.** A configure-menu may still be useful later as an accessibility/keyboard-driven affordance, but isn't planned for the first release.
