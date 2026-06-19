using System;

namespace ExpandableX.Core
{
    /// <summary>
    /// The dedicated "join" connector that stitches adjacent <c>DynamicLayout</c> pieces into one
    /// logical building (CONTEXT.md "Join connector", ADR-0012). It is deliberately its own
    /// <see cref="BuildingBaseIO"/> subtype — <b>not</b> a fluid or signal junction — so our network
    /// can never fuse with the vanilla fluid/signal networks: pieces are matched only via
    /// <c>ConnectorData.BuildingConnectorsOfType&lt;JoinJunction&gt;()</c> (a runtime type match, so a
    /// custom subtype is found with no registration), and a join junction is compatible only with
    /// another join junction.
    ///
    /// A <c>DynamicLayout</c> piece carries a join junction on each face it shares with an interior
    /// neighbour and none on its outer faces (border closing); its <see cref="SlotRole.Join"/> role
    /// is topology-driven, set by grow/shrink rather than by the player. Geometry (position +
    /// facing) is the inherited <see cref="BuildingBaseIO.Position_L"/> /
    /// <see cref="BuildingBaseIO.TileDirection"/>; construct with an object initializer.
    /// </summary>
    [Serializable]
    public sealed class JoinJunction : BuildingBaseIO
    {
        /// <summary>Join junctions bind only to other join junctions; they are invisible to gameplay I/O.</summary>
        public override bool IsCompatibleConnection(BuildingBaseIO otherIO) => otherIO is JoinJunction;
    }
}
