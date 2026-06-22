extern alias monomod;
using System;
using System.Collections.Generic;
using System.Linq.Expressions;
using Game.Core.Coordinates;
using monomod::MonoMod.RuntimeDetour;
using ShapezShifter.SharpDetour;
using ILogger = Core.Logging.ILogger;

namespace ExpandableX.Core
{
    /// <summary>
    /// The whole-network delete chokepoint (ADR-0013): deleting any member of a <see cref="Layout.Dynamic"/>
    /// network deletes the whole network, so a building is never left partially-removed or invalid. This
    /// was the last hole in the "a network is only ever created or destroyed as a whole" invariant after
    /// grow/shrink/fusion (issue #9, absorbing #10).
    ///
    /// Two detours, because the base game's delete and its neighbour-downgrade run off different inputs:
    ///
    /// <list type="bullet">
    /// <item><b><see cref="ActionModifyBuildings"/>.IsPossible</b> — the universal funnel every delete
    /// gesture passes through before execution. We rewrite <see cref="ActionModifyBuildings.Data"/> so the
    /// deletion set expands to whole networks. This alone covers the paths that don't run downgrades
    /// (single-panel delete, cut), matching the base game (those paths don't reshape neighbours either).</item>
    /// <item><b><see cref="HUDBuildingMassSelection"/>.CreateDeleteAction</b> — the mass/area/quick path
    /// additionally asks the base game's downgrade resolver to reshape auto-shaping neighbours
    /// (belts/mergers/splitters, pipes, wires) of the deleted buildings. That resolver is handed the same
    /// <c>entries</c> used to build the delete payload, so we expand <c>entries</c> <i>here</i> — before
    /// both run — or a wire/belt/pipe touching an auto-added member would be left in a stale shape. (The
    /// IsPossible hook then no-ops: the set is already whole.)</item>
    /// </list>
    ///
    /// Expanding inside <c>IsPossible</c> (rather than at execution) means the action manager validates the
    /// expanded set, and the reverse action rebuilds from the already-expanded <c>Data.Delete</c> — so undo
    /// re-places the whole network for free. Both rewrites are idempotent (re-running on an already-whole
    /// set grows nothing). The delete rewrite skips force-allow-delete payloads, which are the
    /// reverse/internal deletes the undo system generates — only player-initiated deletes pull in a network.
    ///
    /// These are ExpandableX's first behaviour-altering detours; the existing session-init hook only reads
    /// state. They tolerate a not-yet-initialised session (no matcher / no map) by leaving the delete
    /// untouched, and any failure fails open (the original delete proceeds) so a bug here can never wedge
    /// the delete tool.
    /// </summary>
    internal sealed class ExpandableXNetworkDeleteHook : IDisposable
    {
        private readonly ExpandableXRegistry _registry;
        private readonly ILogger _logger;
        private readonly Hook _isPossibleHook;
        private readonly Hook _massDeleteHook;

        public ExpandableXNetworkDeleteHook(ExpandableXRegistry registry, ILogger logger)
        {
            _registry = registry;
            _logger = logger;

            Expression<Func<ActionModifyBuildings, IInteractionMode, bool>> isPossible =
                (action, mode) => action.IsPossible(mode);
            _isPossibleHook = DetourHelper.CreatePrefixHook<ActionModifyBuildings, IInteractionMode, bool>(
                isPossible,
                (action, mode) =>
                {
                    try
                    {
                        action.Data = ExpandDeletes(action.Data);
                    }
                    catch (Exception e)
                    {
                        _logger.Info.Log(
                            $"ExpandableX-Core: whole-network delete expansion failed, leaving the delete unchanged: {e}");
                    }

                    return mode;
                });

            // Expand the entries the mass/area/quick delete builds from, so the base game computes neighbour
            // downgrades against the whole network (not just the originally-touched pieces). CreateDeleteAction
            // is protected; it is reachable here because the game assembly is publicized.
            Expression<Func<HUDBuildingMassSelection, IReadOnlyCollection<BuildingModel>, IPlayerAction>> createDelete =
                (hud, entries) => hud.CreateDeleteAction(entries);
            _massDeleteHook = DetourHelper.CreatePrefixHook<HUDBuildingMassSelection, IReadOnlyCollection<BuildingModel>, IPlayerAction>(
                createDelete,
                (_, entries) =>
                {
                    try
                    {
                        return ExpandEntries(entries);
                    }
                    catch (Exception e)
                    {
                        _logger.Info.Log(
                            $"ExpandableX-Core: whole-network delete entry expansion failed, leaving the selection unchanged: {e}");
                        return entries;
                    }
                });
        }

        public void Dispose()
        {
            _isPossibleHook?.Dispose();
            _massDeleteHook?.Dispose();
        }

        /// <summary>
        /// Returns <paramref name="data"/> with every player-initiated deletion of a network member expanded
        /// to include all of that network's members; returns the original unchanged when nothing expands.
        /// </summary>
        private ModifyBuildingsPayload ExpandDeletes(ModifyBuildingsPayload data)
        {
            if (data.Delete.Count == 0 || _registry.LocalPlayer?.CurrentMap is not { } map)
            {
                return data;
            }

            // Seed with the originals (their own ForceAllowDelete flags preserved), keyed by id so an
            // expanded member already named explicitly is never duplicated.
            var deletes = new Dictionary<BuildingId, DeleteBuildingPayload>(data.Delete.Count);
            foreach (DeleteBuildingPayload payload in data.Delete)
            {
                deletes[payload.BuildingId] = payload;
            }

            bool grew = false;
            foreach (DeleteBuildingPayload payload in data.Delete)
            {
                // Reverse/internal deletes (force-allow) are already whole-network by construction; only
                // player-initiated deletes pull in their network. This is also what makes redo idempotent.
                if (payload.ForceAllowDelete
                    || !map.TryGetBuilding(in payload.BuildingId, out BuildingModel anchor)
                    || NetworkMembership.Of(_registry, anchor.Transform.Position, map) is not { } members)
                {
                    continue;
                }

                foreach (BuildingModel member in members)
                {
                    if (deletes.TryAdd(member.Id, new DeleteBuildingPayload(member.Id)))
                    {
                        grew = true;
                    }
                }
            }

            return grew
                ? new ModifyBuildingsPayload(data.Place, [.. deletes.Values], data.BlueprintCurrencyModification)
                : data;
        }

        /// <summary>
        /// Returns <paramref name="entries"/> expanded so any network member pulls in its whole network;
        /// returns the original unchanged when nothing expands.
        /// </summary>
        private IReadOnlyCollection<BuildingModel> ExpandEntries(IReadOnlyCollection<BuildingModel> entries)
        {
            if (entries.Count == 0 || _registry.LocalPlayer?.CurrentMap is not { } map)
            {
                return entries;
            }

            var result = new Dictionary<BuildingId, BuildingModel>(entries.Count);
            foreach (BuildingModel building in entries)
            {
                result[building.Id] = building;
            }

            bool grew = false;
            foreach (BuildingModel building in entries)
            {
                if (NetworkMembership.Of(_registry, building.Transform.Position, map) is not { } members)
                {
                    continue;
                }

                foreach (BuildingModel member in members)
                {
                    if (result.TryAdd(member.Id, member))
                    {
                        grew = true;
                    }
                }
            }

            return grew ? [.. result.Values] : entries;
        }
    }
}
