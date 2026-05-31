using System;

namespace ExpandableX.Core
{
    /// <summary>
    /// What a <see cref="ConnectorSlot"/> is set to. See the "Role" entry in CONTEXT.md.
    /// </summary>
    public enum SlotRole
    {
        Input,
        Output,
        Disabled,

        /// <summary>
        /// Junction-specific: a bidirectional connector (fluid/signal junction) active as a
        /// pass-through. Auto-corrected to <see cref="Input"/>/<see cref="Output"/> on a
        /// directional connector before id encoding (see CONTEXT.md "Role").
        /// </summary>
        Enabled,
    }

    /// <summary>Single-character encoding of a <see cref="SlotRole"/> for the variant id.</summary>
    public static class RoleAlphabet
    {
        public static char Encode(SlotRole role) => role switch
        {
            SlotRole.Input => 'I',
            SlotRole.Output => 'O',
            SlotRole.Disabled => 'D',
            SlotRole.Enabled => 'E',
            _ => throw new ArgumentOutOfRangeException(nameof(role)),
        };

        public static SlotRole Decode(char c) => c switch
        {
            'I' => SlotRole.Input,
            'O' => SlotRole.Output,
            'D' => SlotRole.Disabled,
            'E' => SlotRole.Enabled,
            _ => throw new ArgumentOutOfRangeException(nameof(c), $"unknown role char '{c}'"),
        };
    }
}
