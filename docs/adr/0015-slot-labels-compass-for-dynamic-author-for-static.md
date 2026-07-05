# Slot labels: world-absolute compass for dynamic, author-supplied for static

**Status:** accepted — refines the HUD presentation of `Connector slot`s from [ADR-0008](./0008-slot-state-encoded-in-definition-id.md) (id-as-truth slots) as surfaced by `ConfigurableVariantModules`. Follow-ups: #27 (visual/overlay HUD), #28 (translation).

The per-building side panel used the slot's internal `Id` (`in_0`, `in_1`, `out_0`, …) as its player-facing header, and listed slots in registration/visible-index order. Those ids are arbitrary and carry no indication of *which face* a slot controls, and for `DynamicLayout` pieces the order was meaningless because variants are generated one-per-rotational-class (canonicalised), so `in_0` lands on a different world face per placement. We split labelling by layout kind:

- **`DynamicLayout` slots** are labelled by the **world-absolute compass direction** of the face the connector sits on — the piece's local face (`SlotFaceDirections`) rotated by the placed building's `GridRotation` — using full compass words ("North", "East", "South", "West"), and are **ordered clockwise from North**. Each piece is a 1×1 building holding exactly one connector per face, so direction is a reliable, unique identity, and the order mirrors the physical building face-by-face.
- **`StaticLayout` slots** are labelled by an **author-supplied, building-relative** string (e.g. the painter's "Left Fluid" / "Bottom Fluid" / "Right Fluid") and stay in **registration order**. Static geometry is hand-crafted and arbitrary — two connectors can share a world direction (a 2-long building has two north faces) and there is no `SlotFaceDirections` map — so the author must name them; the framework cannot derive a safe direction.

The player-facing label is decoupled from the slot `Id`: `Id` is an internal, unpersisted dictionary key that plays no part in the variant id (which is base + role characters in slot *order*), so changing labels can never affect blueprint identity. `ConnectorSlot` carries a nullable `Label`; `ConnectorSlotSpec.Single` sets it from a readable `Id` (no separate field), `ConnectorSlotSpec.Range` from an optional `LabelAt(index)`; rendering uses the world-compass word for dynamic and `Label ?? Id` for static.

## Considered Options

- **World-absolute compass for dynamic, author labels for static.** *(Chosen.)* Matches what the player sees on the map for the geometry-regular dynamic pieces, while letting the author name the irregular static ones.
- **Screen-relative words ("Top/Right/Bottom/Left") for dynamic.** Rejected: the player can orbit the camera, so screen-relative labels become lies. The in-game compass makes absolute directions legible, so the mismatch is minor and honest.
- **Local-frame direction for dynamic (ignore placement rotation).** Rejected: stable per definition but wrong for any rotated placement — the label wouldn't match the connector's visible side.
- **Direction labels for static too.** Rejected: static geometry can put two connectors on the same world direction and stores no face map, so direction is neither unique nor available.
- **Keep `Id` as the label.** Rejected: that is the status quo the change exists to remove.

## Consequences

- **Static labels are building-relative and fixed** — "Left Fluid" means "left in the building's default orientation." A rotated static building's labels no longer track the compass (dynamic labels do). Accepted for the placeholder HUD; the visual HUD (#27) is the real fix.
- **Mirrored definitions need their own labels.** A mirror is a separate `MetaBuildingDefinition`, so left/right swap; the mirrored registration supplies swapped labels (index→face verified in-game). A shared registration helper `(baseDefId, labelForIndex)` keeps the two painter registrations DRY.
- **Labels are raw English (`RawText`).** Localization is deferred to #28.
