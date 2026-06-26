using Game.Core.Coordinates;

namespace ExpandableX.Core
{
    /// <summary>
    /// One drag handle on an outer face of a selected logical building — the unified control surface that
    /// replaces the per-face HUD buttons (issue #5 / ADR-0014). The handle sits on <see cref="Face"/> (a
    /// world direction) of the piece anchored at <see cref="Position"/> (whose entity is <see cref="Piece"/>).
    /// <see cref="CanGrow"/> lights the outward direction (drag out → grow) and <see cref="CanShrink"/> the
    /// inward direction (drag in → shrink); a handle is emitted only when at least one is live, and the draw
    /// layer lights only the live directions (Q9). The input layer maps an outward drag to
    /// <see cref="NetworkExpansionEngine.GrowChainFor"/> and an inward drag to
    /// <see cref="NetworkExpansionEngine.ShrinkChainFor"/> on this <see cref="Piece"/> / <see cref="Face"/>.
    /// </summary>
    public readonly record struct ExpansionHandle(
        BuildingId Piece,
        GlobalTileCoordinate Position,
        TileDirection Face,
        bool CanGrow,
        bool CanShrink);
}
