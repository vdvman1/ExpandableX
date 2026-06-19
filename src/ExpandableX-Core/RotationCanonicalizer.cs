using System;
using System.Collections.Generic;
using Game.Core.Coordinates;

namespace ExpandableX.Core
{
    /// <summary>
    /// Reduces a network piece's per-face role assignment to one canonical orientation per rotational
    /// class, so the framework generates a single <c>MetaBuildingDefinition</c> per class and realises
    /// the other orientations via the placed building's <see cref="GridRotation"/> (CONTEXT.md
    /// "Piece role"; ADR-0012). The mapping is blueprint-stable: every face-role state maps to exactly
    /// one (canonical assignment, rotation) pair, and back.
    ///
    /// v1 scope: planar pieces whose faces are the four planar directions (East/South/West/North).
    /// Up/Down faces and partial face-sets are future (vertical-expansion) work — they would widen the
    /// rotation group beyond the planar 4-cycle used here.
    /// </summary>
    public static class RotationCanonicalizer
    {
        /// <summary>The four planar faces in clockwise order — matches TileDirection's planar value order and GridRotation.</summary>
        private static readonly TileDirection[] ClockwiseFaces =
            { TileDirection.East, TileDirection.South, TileDirection.West, TileDirection.North };

        private static readonly GridRotation[] Rotations =
            { GridRotation.NoRotate, GridRotation.RotateCW, GridRotation.Rotate180, GridRotation.RotateCCW };

        /// <summary>
        /// Rotate a face-role assignment by <paramref name="rotation"/>: the role on face <c>d</c> moves
        /// to face <c>d.Rotate(rotation)</c> (connectors rotate with the building).
        /// </summary>
        public static IReadOnlyDictionary<TileDirection, SlotRole> Rotate(
            IReadOnlyDictionary<TileDirection, SlotRole> faces, GridRotation rotation)
        {
            var result = new Dictionary<TileDirection, SlotRole>(faces.Count);
            foreach (var pair in faces)
                result[pair.Key.Rotate(rotation)] = pair.Value;
            return result;
        }

        /// <summary>
        /// The canonical assignment (lexicographically smallest encoding over its four rotations) and
        /// the <see cref="GridRotation"/> at which to place it so its connectors land back on
        /// <paramref name="faces"/>. For a rotationally symmetric assignment the smallest qualifying
        /// rotation is chosen, so the result is deterministic (hence blueprint-stable).
        /// </summary>
        public static (IReadOnlyDictionary<TileDirection, SlotRole> Canonical, GridRotation Rotation) Canonicalize(
            IReadOnlyDictionary<TileDirection, SlotRole> faces)
        {
            string? bestKey = null;
            GridRotation toCanonical = GridRotation.NoRotate;
            IReadOnlyDictionary<TileDirection, SlotRole> canonical = faces;

            foreach (var r in Rotations)
            {
                var rotated = Rotate(faces, r);
                string key = Encode(rotated);
                if (bestKey is null || string.CompareOrdinal(key, bestKey) < 0)
                {
                    bestKey = key;
                    toCanonical = r;          // canonical == Rotate(faces, toCanonical)
                    canonical = rotated;
                }
            }

            // canonical = Rotate(faces, toCanonical)  =>  faces = Rotate(canonical, -toCanonical).
            return (canonical, -toCanonical);
        }

        /// <summary>Whether <paramref name="faces"/> is already its own canonical representative.</summary>
        public static bool IsCanonical(IReadOnlyDictionary<TileDirection, SlotRole> faces) =>
            Canonicalize(faces).Rotation == GridRotation.NoRotate;

        /// <summary>Role characters in clockwise face order — the key the canonical choice minimises.</summary>
        private static string Encode(IReadOnlyDictionary<TileDirection, SlotRole> faces)
        {
            var chars = new char[ClockwiseFaces.Length];
            int n = 0;
            foreach (var face in ClockwiseFaces)
                if (faces.TryGetValue(face, out var role))
                    chars[n++] = RoleAlphabet.Encode(role);
            return new string(chars, 0, n);
        }
    }
}
