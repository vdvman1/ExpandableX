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

        /// <summary>
        /// The face carries a <c>Join connector</c> toward an interior neighbour of the same
        /// building (<c>DynamicLayout</c> pieces only). Unlike the other roles this is
        /// <b>topology-driven, not player-driven</b>: the grow/shrink action assigns it as the
        /// building's shape changes, and an interior face is forced to it. See CONTEXT.md "Role"
        /// and ADR-0012.
        /// </summary>
        Join,
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
            SlotRole.Join => 'J',
            _ => throw new ArgumentOutOfRangeException(nameof(role)),
        };

        public static SlotRole Decode(char c) => c switch
        {
            'I' => SlotRole.Input,
            'O' => SlotRole.Output,
            'D' => SlotRole.Disabled,
            'E' => SlotRole.Enabled,
            'J' => SlotRole.Join,
            _ => throw new ArgumentOutOfRangeException(nameof(c), $"unknown role char '{c}'"),
        };
    }
}
