using Game.Core.Coordinates;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;

namespace ExpandableX.Core
{
    /// <summary>
    /// Converts between a network piece's per-<b>world</b>-face role assignment and a placed building's
    /// <c>(definition id, GridRotation)</c> — the inverse of the rotational-class canonicalisation that
    /// variant generation applies (ADR-0012, and <see cref="RotationCanonicalizer"/>). Grow/shrink reason
    /// in world-face terms ("put a Join on the East face, carry the Output to the new end"), then realise
    /// the result back into a concrete definition + rotation to place via an id-as-truth swap.
    ///
    /// Two regimes, matching generation: a piece with at least one <see cref="SlotRole.Join"/> is a
    /// network piece (one def generated per rotational class), so it realises at the canonical def and the
    /// canonicalisation rotation; a 0-join piece is a configurable singleton (generated in full,
    /// player-rotated), so it realises at the building's current rotation with the literal local config.
    /// Both look the resulting slot-role combo up in the family's table, so a pruned / invalid combo
    /// cleanly fails to realise (returns false) rather than producing a non-existent definition id.
    /// </summary>
    internal static class NetworkPieceRealization
    {
        /// <summary>
        /// The per-world-face role map of a placed piece: each slot's declared (local) face rotated into
        /// world space by the building's rotation, carrying that slot's current role.
        /// </summary>
        public static IReadOnlyDictionary<TileDirection, SlotRole> WorldFaceRoles(
            PieceVariantSet set, IReadOnlyDictionary<string, SlotRole> slotState, GridRotation rotation)
        {
            var world = new Dictionary<TileDirection, SlotRole>(set.Slots.Count);
            if (set.SlotFaceDirections is not { } faces)
            {
                return world;
            }

            foreach (ConnectorSlot slot in set.Slots)
            {
                if (faces.TryGetValue(slot.Id, out TileDirection localFace)
                    && slotState.TryGetValue(slot.Id, out SlotRole role))
                {
                    world[localFace.Rotate(rotation)] = role;
                }
            }

            return world;
        }

        /// <summary>
        /// Realise a desired per-world-face role assignment into a placed building's definition id and
        /// rotation. A piece carrying a <see cref="SlotRole.Join"/> canonicalises (its rotation is forced
        /// by the canonical class); a 0-join piece keeps <paramref name="singletonRotation"/> (the
        /// building's current rotation). Returns false if the family does not generate that combo
        /// (locally pruned, or not a valid singleton), so callers can gate the action.
        /// </summary>
        public static bool TryRealize(
            PieceVariantSet set,
            IReadOnlyDictionary<TileDirection, SlotRole> worldFaces,
            GridRotation singletonRotation,
            out string definitionId,
            out GridRotation rotation) =>
            TryRealize(set, worldFaces, singletonRotation, out definitionId, out rotation, out _);

        /// <summary>
        /// As <see cref="TryRealize(PieceVariantSet,IReadOnlyDictionary{TileDirection,SlotRole},GridRotation,out string,out GridRotation)"/>,
        /// additionally returning the realised piece's slot-id-keyed roles — the shape a
        /// <see cref="PieceState"/> needs, so a caller can assemble a candidate <see cref="NetworkState"/>
        /// (e.g. to re-validate a grow's result against the network predicates) without re-deriving the
        /// face→slot mapping. Only meaningful when the method returns true.
        /// </summary>
        public static bool TryRealize(
            PieceVariantSet set,
            IReadOnlyDictionary<TileDirection, SlotRole> worldFaces,
            GridRotation singletonRotation,
            out string definitionId,
            out GridRotation rotation,
            [NotNullWhen(true)] out IReadOnlyDictionary<string, SlotRole>? slotRoles)
        {
            definitionId = string.Empty;
            slotRoles = null;

            IReadOnlyDictionary<TileDirection, SlotRole> localFaces;
            if (HasJoin(worldFaces))
            {
                (localFaces, rotation) = RotationCanonicalizer.Canonicalize(worldFaces);
            }
            else
            {
                // A singleton's def is the literal local config; undo the building's rotation to recover it.
                rotation = singletonRotation;
                localFaces = RotationCanonicalizer.Rotate(worldFaces, -singletonRotation);
            }

            if (set.SlotFaceDirections is not { } faces)
            {
                return false;
            }

            var roles = new Dictionary<string, SlotRole>(set.Slots.Count);
            foreach (ConnectorSlot slot in set.Slots)
            {
                if (!faces.TryGetValue(slot.Id, out TileDirection localFace)
                    || !localFaces.TryGetValue(localFace, out SlotRole role))
                {
                    return false;
                }

                roles[slot.Id] = role;
            }

            string comboKey = VariantEncoder.ComboKey(set.Slots, roles);
            if (!set.DefIdByComboKey.TryGetValue(comboKey, out definitionId))
            {
                return false;
            }

            slotRoles = roles;
            return true;
        }

        private static bool HasJoin(IReadOnlyDictionary<TileDirection, SlotRole> faces) => faces.Values.Contains(SlotRole.Join);
    }
}
