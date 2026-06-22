using System;
using System.Collections.Generic;
using System.Linq;
using Core.Collections;
using ILogger = Core.Logging.ILogger;

namespace ExpandableX.Core
{
    /// <summary>
    /// Keeps a <see cref="Layout.Dynamic"/> network atomic in the player's building selection and tracks
    /// the focus piece (ADR-0013). Subscribes to the selection's <c>OnAdded</c>/<c>OnRemoved</c> deltas and
    /// reconciles every change so the selection can never hold a partial network:
    ///
    /// <list type="bullet">
    /// <item><b>Single-click</b> — the only gesture that drives the selection down to exactly one building
    /// (a plain click is <c>Set([one])</c>; area/additive use <c>Add</c>). If that building is a network
    /// member, it becomes the <see cref="Focus"/> and the whole network is pulled back in. This also makes
    /// re-clicking a different piece of an already-selected network just move the focus, rather than
    /// collapsing the selection.</item>
    /// <item><b>Additive gesture</b> (area/box select that touches a network) — pull the rest of each
    /// touched network in.</item>
    /// <item><b>Subtractive gesture</b> (area-deselect, <c>NarrowDownSelection</c>) — remove the rest of
    /// each touched network out, so narrowing onto any member drops the whole network.</item>
    /// </list>
    ///
    /// The focus piece is written only on a single-click into a network; its <i>validity</i> (whether the
    /// per-piece HUD should show) is judged at read time via <see cref="TryGetFocus"/> (still selected AND
    /// the selection equals its network), never maintained reactively — so the trailing events of a
    /// network switch can't clobber it, and a mass selection simply fails the check. Focus is transient
    /// session state, never persisted.
    ///
    /// All work is gated to the <see cref="PlayerInteractionState.BuildingsIdle"/> state (inert during
    /// placement) and guarded against re-entry from its own selection edits; any failure is swallowed so a
    /// bug here can never wedge selection.
    /// </summary>
    internal sealed class ExpandableXNetworkSelection : IDisposable
    {
        private readonly ExpandableXRegistry _registry;
        private readonly Player _player;
        private readonly ISelection<BuildingModel> _selection;
        private readonly ILogger _logger;

        private bool _reconciling;
        private BuildingId? _focus;

        public ExpandableXNetworkSelection(ExpandableXRegistry registry, Player player, ILogger logger)
        {
            _registry = registry;
            _player = player;
            _logger = logger;
            _selection = player.InteractionState.BuildingSelection;
            _selection.OnAdded.Register(OnAdded);
            _selection.OnRemoved.Register(OnRemoved);
        }

        public void Dispose()
        {
            // TryUnregister, not Unregister: when this manager belonged to a now-torn-down session, the
            // game has already disposed that session's selection (clearing its listener lists), so the
            // throwing Unregister would fault session init and freeze loading. TryUnregister is a no-op then.
            _selection.OnAdded.TryUnregister(OnAdded);
            _selection.OnRemoved.TryUnregister(OnRemoved);
        }

        /// <summary>
        /// The focus piece, valid only when it is still selected and the current selection is exactly its
        /// network — i.e. the player single-clicked into one network (not a mass selection that merely
        /// happens to cover it). False otherwise; that is the signal the HUD uses to choose the per-piece
        /// panel over the many-buildings panel.
        /// </summary>
        public bool TryGetFocus(out BuildingModel focus)
        {
            focus = default;
            if (_focus is not { } id
                || _registry.LocalPlayer?.CurrentMap is not { } map
                || !map.TryGetBuilding(in id, out BuildingModel building)
                || !_selection.Contains(building))
            {
                return false;
            }

            if (NetworkMembership.Of(_registry, building.Transform.Position, map) is not { } members
                || members.Count != _selection.Count)
            {
                return false;
            }

            focus = building;
            return true;
        }

        private void OnAdded(IReadOnlyCollection<BuildingModel> added) => Reconcile(added, adding: true);

        private void OnRemoved(IReadOnlyCollection<BuildingModel> removed) => Reconcile(removed, adding: false);

        private void Reconcile(IReadOnlyCollection<BuildingModel> delta, bool adding)
        {
            if (_reconciling
                || _player.InteractionState.State != PlayerInteractionState.BuildingsIdle
                || _registry.LocalPlayer?.CurrentMap is not { } map)
            {
                return;
            }

            try
            {
                // Single-click (or re-click) into a network: the selection collapsed to exactly one
                // building. Focus it and pull the whole network back in.
                if (_selection.Count == 1)
                {
                    BuildingModel only = _selection.First();
                    if (NetworkMembership.Of(_registry, only.Transform.Position, map) is { } clicked)
                    {
                        _focus = only.Id;
                        Run(() => _selection.Add(clicked));
                    }
                    else
                    {
                        _focus = null;
                    }

                    return;
                }

                // Mass gesture: keep every touched network whole. Focus is left as-is — TryGetFocus will
                // reject it because the selection no longer equals one network.
                List<BuildingModel>? change = null;
                foreach (BuildingModel building in delta)
                {
                    if (NetworkMembership.Of(_registry, building.Transform.Position, map) is not { } members)
                    {
                        continue;
                    }

                    foreach (BuildingModel member in members)
                    {
                        if (_selection.Contains(member) != adding)
                        {
                            (change ??= []).Add(member);
                        }
                    }
                }

                if (change is null)
                {
                    return;
                }

                if (adding)
                {
                    Run(() => _selection.Add(change));
                }
                else
                {
                    Run(() => _selection.Remove(change));
                }
            }
            catch (Exception e)
            {
                _logger.Info.Log($"ExpandableX-Core: network selection reconcile failed: {e}");
            }
        }

        private void Run(Action mutate)
        {
            _reconciling = true;
            try
            {
                mutate();
            }
            finally
            {
                _reconciling = false;
            }
        }
    }
}
