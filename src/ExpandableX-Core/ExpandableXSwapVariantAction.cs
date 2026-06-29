using System.Linq;
using Game.Core.Coordinates;

namespace ExpandableX.Core
{
    /// <summary>
    /// Undoable swap of a building to another variant definition, preserving its id and configuration
    /// (id-as-truth, ADR-0008). Scheduled via the session's <c>PlayerActionManager</c> so it runs at a
    /// safe point and lands on the undo stack.
    ///
    /// The swap carries <b>both</b> the from- and to-transforms because a variant's orientation is realised
    /// via <see cref="GridRotation"/> (canonicalisation can place the same world-face roles at a different
    /// rotation), so the destination rotation routinely differs from the source's. The reverse swaps back to
    /// the <i>from</i> definition <b>at the from transform</b> — restoring the original rotation, not the
    /// grown one — so undo returns the piece to exactly its prior state (connectors and all). Passing a
    /// single transform here was a bug: undo restored the right definition at the wrong rotation, leaving the
    /// surviving piece's connectors pointing the wrong way.
    /// </summary>
    internal sealed class ExpandableXSwapVariantAction : PlayerAction
    {
        private readonly BuildingId _buildingId;
        private readonly GlobalTileTransform _fromTransform;
        private readonly GlobalTileTransform _toTransform;
        private readonly IBuildingConfiguration _configuration;
        private readonly IBuildingDefinition _fromDefinition;
        private readonly IBuildingDefinition _toDefinition;

        public ExpandableXSwapVariantAction(
            IMapModel map,
            Player executor,
            in BuildingId buildingId,
            in GlobalTileTransform fromTransform,
            in GlobalTileTransform toTransform,
            IBuildingConfiguration configuration,
            IBuildingDefinition fromDefinition,
            IBuildingDefinition toDefinition)
            : base(map, executor)
        {
            _buildingId = buildingId;
            _fromTransform = fromTransform;
            _toTransform = toTransform;
            _configuration = configuration;
            _fromDefinition = fromDefinition;
            _toDefinition = toDefinition;
        }

        public override PlayerActionMode Mode => PlayerActionMode.Undoable;

        public override bool IsPossible(IInteractionMode interactionMode) => true;

        public override void ExecuteInternal(IInteractionMode interactionMode, out IPlayerAction reverseAction)
        {
            // If this building is currently selected, the same-id swap leaves a stale BuildingModel
            // in the selection and the detail panel never rebuilds. Re-selecting the recreated model
            // fires Selection.OnChanged so the panel refreshes to the new state. Only do this when it
            // was already selected, so undo/redo doesn't force-select a building the player isn't on.
            var selection = Executor.InteractionState.BuildingSelection;
            bool wasSelected = selection.Any(b => b.Id == _buildingId);

            Map.DeleteBuilding(in _buildingId);
            BuildingModel created = Map.CreateBuilding(_toDefinition, in _toTransform, in _buildingId, _configuration);

            if (wasSelected)
            {
                // Set() alone no-ops for a same-id model, so the panel's OnChanged → MarkDirty never
                // fires. Clear then Set forces the event; both run within this tick, so there's no
                // visible flicker, and the panel rebuilds against the new variant.
                selection.Clear();
                selection.Set(new[] { created });
            }

            // Undo: swap back to the definition and transform we came from (same id/config), so the original
            // rotation is restored too. The reverse's from/to are this action's to/from.
            reverseAction = new ExpandableXSwapVariantAction(
                Map, Executor, in _buildingId, in _toTransform, in _fromTransform, _configuration, _toDefinition, _fromDefinition);
        }
    }
}
