using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using Game.Core.Coordinates;

namespace ExpandableX.Core
{
    /// <summary>
    /// One piece-level edit to a candidate network: place (or replace) the piece anchored at
    /// <see cref="Position"/>, or — when <see cref="Piece"/> is null — remove whatever is there. A batch
    /// of these is how a modification is expressed (a slot change is one <see cref="Place"/>; a grow is a
    /// <see cref="Place"/> on the grown source plus one for the new piece; a future drag or incidental
    /// fusion is several at once).
    /// </summary>
    internal readonly record struct NetworkChange(GlobalTileCoordinate Position, PieceState? Piece)
    {
        /// <summary>Place or replace the piece anchored at <paramref name="position"/>.</summary>
        public static NetworkChange Place(GlobalTileCoordinate position, PieceState piece) => new(position, piece);

        /// <summary>Remove the piece anchored at <paramref name="position"/>.</summary>
        public static NetworkChange Remove(GlobalTileCoordinate position) => new(position, null);
    }

    /// <summary>
    /// A whole logical building's pieces keyed by their anchor tile, used to ask "if I made these
    /// change(s), would the building still satisfy its network predicates?" — without mutating anything
    /// placed. Read the current connected network from the authoritative matcher with
    /// <see cref="TryReadFrom"/>, derive the post-modification network with <see cref="With"/> (which
    /// returns a fresh copy, leaving the original untouched), then validate via
    /// <see cref="NetworkState.FirstPredicateViolation"/>.
    ///
    /// Both places that re-validate a network after a change share this: grow re-validation
    /// (<see cref="NetworkExpansionEngine"/>) and slot-config changes (<see cref="ConfigurableVariantModules"/>).
    /// Keying by anchor tile is what lets a change target a specific piece, since a <see cref="PieceState"/>
    /// is otherwise anonymous (blueprint compatibility — no per-instance ids). Predicate evaluation itself
    /// is positionless: <see cref="ToNetworkState"/> drops the keys (CONTEXT.md "DynamicLayout", ADR-0012).
    /// </summary>
    internal sealed class NetworkCandidate
    {
        private readonly Layout _layout;
        private readonly IReadOnlyDictionary<GlobalTileCoordinate, PieceState> _pieces;

        private NetworkCandidate(Layout layout, IReadOnlyDictionary<GlobalTileCoordinate, PieceState> pieces)
        {
            _layout = layout;
            _pieces = pieces;
        }

        /// <summary>
        /// The current connected network containing the piece at <paramref name="memberTile"/>, read from
        /// the session's network matcher (its membership is the authoritative connected component — the
        /// join-adjacency graph is not re-derived here). False when no matcher is attached or the tile
        /// belongs to no tracked dynamic network.
        /// </summary>
        public static bool TryReadFrom(
            ExpandableXRegistry registry, in GlobalTileCoordinate memberTile, [NotNullWhen(true)] out NetworkCandidate? network)
        {
            network = null;
            if (registry.NetworkSimulation is not { } simulation
                || !simulation.TryGetNetworkMembers(memberTile, out IReadOnlyCollection<BuildingInstance>? members))
            {
                return false;
            }

            Layout? layout = null;
            var pieces = new Dictionary<GlobalTileCoordinate, PieceState>(members.Count);
            foreach (BuildingInstance member in members)
            {
                if (!registry.VariantsByDefId.TryGetValue(member.Definition.Id.Name, out VariantPlacement? placement))
                {
                    continue;
                }

                layout ??= placement.Set.Layout;
                pieces[member.Transform.Position] =
                    new PieceState(placement.Set.Piece, placement.Set.Slots, placement.SlotState);
            }

            if (layout is null)
            {
                return false;
            }

            network = new NetworkCandidate(layout, pieces);
            return true;
        }

        /// <summary>Whether a piece anchored at <paramref name="anchor"/> is a member of this network — the intra-network test incidental fusion uses to decide what may fuse.</summary>
        public bool Contains(in GlobalTileCoordinate anchor) => _pieces.ContainsKey(anchor);

        /// <summary>A fresh copy with the given piece edits applied; this instance is unchanged.</summary>
        public NetworkCandidate With(params IEnumerable<NetworkChange> changes)
        {
            var pieces = new Dictionary<GlobalTileCoordinate, PieceState>(_pieces);
            foreach (NetworkChange change in changes)
            {
                if (change.Piece is { } piece)
                {
                    pieces[change.Position] = piece;
                }
                else
                {
                    pieces.Remove(change.Position);
                }
            }

            return new NetworkCandidate(_layout, pieces);
        }

        /// <summary>The positionless <see cref="NetworkState"/> these pieces form (the view predicates see).</summary>
        public NetworkState ToNetworkState() => new(_layout, [.. _pieces.Values]);

        /// <summary>The blocked reason if this candidate violates a network predicate, else null (see <see cref="NetworkState.FirstPredicateViolation"/>).</summary>
        public string? FirstViolation() => ToNetworkState().FirstPredicateViolation();
    }
}
