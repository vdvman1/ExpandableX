using System.Collections.Generic;
using Game.Core.Coordinates;

namespace ExpandableX.Core
{
    /// <summary>
    /// Resolves the <see cref="BuildingModel"/>s making up a <see cref="Layout.Dynamic"/> network, the one
    /// way both the whole-network selection and the whole-network delete read membership (ADR-0013). The
    /// authoritative membership is the matcher's connected component (<see cref="ExpandableSimulationSystem"/>);
    /// this only maps its <c>BuildingInstance</c>s back to the placed <see cref="BuildingModel"/>s the HUD
    /// and player-action layer work in.
    /// </summary>
    internal static class NetworkMembership
    {
        /// <summary>
        /// The members of the network the building at <paramref name="anchorPosition"/> belongs to (itself
        /// included), or <c>null</c> when no matcher is attached or it isn't part of a tracked multi-member
        /// network (a 0-join singleton or a non-network building yields null).
        /// </summary>
        public static IReadOnlyList<BuildingModel>? Of(
            ExpandableXRegistry registry, in GlobalTileCoordinate anchorPosition, IMapModel map)
        {
            if (registry.NetworkSimulation is not { } simulation
                || !simulation.TryGetNetworkMembers(anchorPosition, out IReadOnlyCollection<BuildingInstance>? members)
                || members.Count <= 1)
            {
                return null;
            }

            var models = new List<BuildingModel>(members.Count);
            foreach (BuildingInstance member in members)
            {
                if (map.TryGetBuilding(member.Transform.Position, out BuildingModel model))
                {
                    models.Add(model);
                }
            }

            return models;
        }
    }
}
