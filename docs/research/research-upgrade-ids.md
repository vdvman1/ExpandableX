# Base-game research upgrade ids

The runtime `ResearchUpgradeId` strings, captured 2026-06-07. These are what
`ResearchManager.Progress.IsUnlocked(new ResearchUpgradeId(id))` expects (see
`IsResearched(...)` in `ExpandableXMod`). All follow `CB<Category>_<Name>`.
Asset/scenario-assigned, so re-verify against a fresh dump if the game updates.

> `IsUnlocked` returns `false` for an unknown id (and throws on an EMPTY id), so a
> typo'd id silently reads as "not researched" rather than erroring.

## The ones we gate on

The cutter sequence gates each step on its building's research (CONTEXT.md "Cutter"):

- **`CBCutting_HalfDestroyer`** — unlocks the half-destroyer (`CutterHalfInternalVariant`).
- **`CBCutting_FullCutter`** — unlocks the 2-output cutter (`CutterDefaultInternalVariant`).
- `CBCutting_Swapper` — halves swapper (not used yet).

## Full list (by category)

```
CBBelts_Core, CBBelts_OverflowSplitter, CBBelts_Trash

CBCutting_FullCutter, CBCutting_HalfDestroyer, CBCutting_Swapper

CBDecorations_Labels

CBFluids_Extraction, CBFluids_Mixer, CBFluids_PainterTop, CBFluids_Storage

CBPlatformPack_Blocky, CBPlatformPack_Irregular, CBPlatformPack_Large,
CBPlatformPack_Linear, CBPlatformPack_Small

CBRotating_ReverseRotator, CBRotating_Rotator, CBRotating_Rotator180

CBShapes_Extraction

CBSpecial_Blueprinting, CBSpecial_Crystals, CBSpecial_FactoryFloor2,
CBSpecial_FactoryFloor3, CBSpecial_PinPusher, CBSpecial_RandomizedGoals,
CBSpecial_SideUpgrades, CBSpecial_SpaceBuilding, CBSpecial_SpaceFloor3

CBStacking_BentStacker, CBStacking_Stacker

CBTrains_Core, CBTrains_EmptyTransport, CBTrains_FluidTransport,
CBTrains_LinePackPrimary, CBTrains_LinePackSecondary, CBTrains_LineRed,
CBTrains_LineWhite, CBTrains_ShapeTransport, CBTrains_StationSpacer,
CBTrains_TrainStopHalting, CBTrains_TrainStopQuick, CBTrains_TransferStations,
CBTrains_VortexDelivery

CBWires_Core, CBWires_GoalReceiver, CBWires_LogicGates,
CBWires_UniversalTransmission, CBWires_VirtualProcessing
```
