namespace ExpandableX.Core
{
    /// <summary>
    /// Produces the connector a slot's resolved <see cref="SlotRole"/> demands, using a template
    /// connector (the base connector at the slot's pivot) for geometry and medium. This is the
    /// heart of synthesis (ADR-0009): <see cref="SlotRole.Disabled"/> omits the connector, the
    /// connector's native role keeps the template unchanged, a flipped directional role constructs
    /// the opposite type at the same pivot, and <see cref="SlotRole.Enabled"/> keeps a junction.
    /// <see cref="SlotRole.Enabled"/> on a directional connector is auto-corrected to that
    /// connector's native directional role (CONTEXT.md "Role").
    /// </summary>
    internal static class ConnectorFactory
    {
        /// <summary>
        /// The connector to emit for <paramref name="role"/>, or null when the slot is disabled.
        /// <paramref name="template"/> is the configurable-base connector at the slot's pivot.
        /// </summary>
        public static IBuildingIO? Build(SlotRole role, IBuildingIO template)
        {
            SlotRole effective = Normalize(role, template);
            if (effective == SlotRole.Disabled)
            {
                return null;
            }

            if (effective == NativeRole(template))
            {
                return template; // unchanged: native input/output, or an Enabled junction
            }

            // The only remaining case is a directional flip (Input <-> Output).
            return Flip(template, toOutput: effective == SlotRole.Output);
        }

        /// <summary>The active (non-disabled) role a connector natively carries.</summary>
        public static SlotRole NativeRole(IBuildingIO io) => io switch
        {
            BuildingFluidJunction => SlotRole.Enabled,
            BuildingSignalJunction => SlotRole.Enabled,
            BuildingItemOutput => SlotRole.Output,
            BuildingFluidOutput => SlotRole.Output,
            BuildingSignalOutput => SlotRole.Output,
            _ => SlotRole.Input, // item / fluid / signal inputs (incl. belt-port input)
        };

        /// <summary>Auto-correct <see cref="SlotRole.Enabled"/> on a directional connector to its native role.</summary>
        public static SlotRole Normalize(SlotRole role, IBuildingIO template) =>
            role == SlotRole.Enabled && !IsJunction(template) ? NativeRole(template) : role;

        private static bool IsJunction(IBuildingIO io) =>
            io is BuildingFluidJunction or BuildingSignalJunction;

        /// <summary>
        /// Construct the opposite directional connector of the same concrete subtype and geometry.
        /// Subtype is preserved (belt ports stay belt ports) because simulations pattern-match on
        /// concrete connector types. Throws on a directional type we don't know the in/out pairing
        /// of — the extension point for new built-in or custom connector types
        /// (see project-custom-connector-types-deferred).
        /// </summary>
        private static IBuildingIO Flip(IBuildingIO template, bool toOutput)
        {
            // Geometry lives on BuildingBaseIO (the IBuildingIO interface omits it). Every connector
            // is one today; check defensively rather than blind-cast. The medium casts in the switch
            // arms below are safe — each is guarded by the pattern that precedes it.
            if (template is not BuildingBaseIO geometry)
            {
                throw new System.NotSupportedException(
                    $"Cannot read geometry from connector of type '{template.GetType().Name}': " +
                    "not a BuildingBaseIO. Extend ConnectorFactory.Flip to support it.");
            }

            var pos = geometry.Position_L;
            var dir = geometry.TileDirection;

            return template switch
            {
                // Item medium — check the belt-port subtype before the general item case.
                BeltPortInput or BeltPortOutput => CopyItem(toOutput ? new BeltPortOutput() : new BeltPortInput(), (BuildingItemIO)template),
                BuildingItemInput or BuildingItemOutput => CopyItem(toOutput ? new BuildingItemOutput() : new BuildingItemInput(), (BuildingItemIO)template),

                BuildingFluidInput or BuildingFluidOutput => toOutput
                    ? new BuildingFluidOutput { Position_L = pos, TileDirection = dir, _IOType = ((BuildingFluidIO)template).IOType }
                    : new BuildingFluidInput { Position_L = pos, TileDirection = dir, _IOType = ((BuildingFluidIO)template).IOType },

                BuildingSignalInput or BuildingSignalOutput => toOutput
                    ? new BuildingSignalOutput { Position_L = pos, TileDirection = dir, _IOType = ((BuildingSignalIO)template).IOType }
                    : new BuildingSignalInput { Position_L = pos, TileDirection = dir, _IOType = ((BuildingSignalIO)template).IOType },

                _ => throw new System.NotSupportedException(
                    $"Cannot flip connector of type '{template.GetType().Name}' between Input and Output: " +
                    "no known directional pairing. Only built-in item (incl. belt port), fluid, and signal " +
                    "directional connectors are supported. Extend ConnectorFactory.Flip to add more."),
            };
        }

        private static BuildingItemIO CopyItem(BuildingItemIO target, BuildingItemIO source)
        {
            target.Position_L = source.Position_L;
            target.TileDirection = source.TileDirection;
            target.IOType = source.IOType;
            target.StandType = source.StandType;
            target.Seperators = source.Seperators;
            return target;
        }
    }
}
