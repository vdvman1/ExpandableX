using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace ExpandableX.Core
{
    /// <summary>A generated variant: its definition id and the slot state it encodes.</summary>
    public sealed record Variant(string DefinitionId, IReadOnlyDictionary<string, SlotRole> SlotState);

    /// <summary>A slot-role combination that a local predicate pruned before generation.</summary>
    public sealed record PrunedCandidate(string CandidateId, IReadOnlyDictionary<string, SlotRole> SlotState, string PrunedBy);

    /// <summary>The full set of variants (and pruned candidates) generated for one piece.</summary>
    public sealed record PieceExpansion(
        PieceSpec Spec,
        IReadOnlyList<ConnectorSlot> ExpandedSlots,
        IReadOnlyList<Variant> Variants,
        IReadOnlyList<PrunedCandidate> Pruned);

    /// <summary>
    /// Encodes slot state into a definition id (ADR-0008) and explodes a piece into its reachable
    /// variants (ADR-0008/0010). Override-aware via <see cref="PieceSpec.Overrides"/>.
    /// </summary>
    public static class VariantEncoder
    {
        public const string IdSuffix = "_ExpandableXConfigurable";

        public static IReadOnlyList<PieceExpansion> ExplodeLayout(Layout layout, IConnectorCountResolver resolver) =>
            layout.EnumeratePieceSpecs().Select(p => ExplodePiece(p, resolver)).ToList();

        public static PieceExpansion ExplodePiece(PieceSpec piece, IConnectorCountResolver resolver)
        {
            var slots = piece.SlotSpecs.SelectMany(s => s.Expand(resolver)).ToList();
            var variants = new List<Variant>();
            var pruned = new List<PrunedCandidate>();

            foreach (var combo in CartesianProduct(slots))
            {
                string? prunedBy = null;
                foreach (var p in piece.LocalPredicates)
                    if (!p.IsValid(combo)) { prunedBy = p.Describe(); break; }

                string id = ResolveId(piece, slots, combo);
                if (prunedBy is null) variants.Add(new Variant(id, combo));
                else pruned.Add(new PrunedCandidate(id, combo, prunedBy));
            }

            return new PieceExpansion(piece, slots, variants, pruned);
        }

        /// <summary>The role-character key for a slot-role combination (no base id / suffix). Used as the override-map key.</summary>
        public static string ComboKey(IReadOnlyList<ConnectorSlot> slots, IReadOnlyDictionary<string, SlotRole> state)
        {
            if (slots.Count == 0) return string.Empty;
            var sb = new StringBuilder(slots.Count);
            foreach (var slot in slots) sb.Append(RoleAlphabet.Encode(state[slot.Id]));
            return sb.ToString();
        }

        /// <summary>The synthesised variant id: base + suffix + role chars. A slot-less piece keeps its base id.</summary>
        public static string EncodeId(string baseDefinitionId, IReadOnlyList<ConnectorSlot> slots, IReadOnlyDictionary<string, SlotRole> state)
        {
            if (slots.Count == 0) return baseDefinitionId;
            var sb = new StringBuilder(baseDefinitionId.Length + IdSuffix.Length + 1 + slots.Count);
            sb.Append(baseDefinitionId).Append(IdSuffix).Append('_');
            foreach (var slot in slots) sb.Append(RoleAlphabet.Encode(state[slot.Id]));
            return sb.ToString();
        }

        /// <summary>The definition id for a combination: the override target if one exists, else the synthesised id.</summary>
        public static string ResolveId(PieceSpec piece, IReadOnlyList<ConnectorSlot> slots, IReadOnlyDictionary<string, SlotRole> state)
        {
            string key = ComboKey(slots, state);
            if (piece.Overrides.TryGetValue(key, out var overrideId)) return overrideId;
            return EncodeId(piece.BaseDefinitionId, slots, state);
        }

        private static IEnumerable<Dictionary<string, SlotRole>> CartesianProduct(IReadOnlyList<ConnectorSlot> slots)
        {
            if (slots.Count == 0) { yield return new Dictionary<string, SlotRole>(); yield break; }

            var indices = new int[slots.Count];
            while (true)
            {
                var combo = new Dictionary<string, SlotRole>(slots.Count);
                for (int i = 0; i < slots.Count; i++)
                    combo[slots[i].Id] = slots[i].AllowedRoles[indices[i]];
                yield return combo;

                int k = slots.Count - 1;
                while (k >= 0)
                {
                    indices[k]++;
                    if (indices[k] < slots[k].AllowedRoles.Count) break;
                    indices[k] = 0;
                    k--;
                }
                if (k < 0) yield break;
            }
        }
    }
}
