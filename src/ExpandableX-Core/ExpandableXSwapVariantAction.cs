using System.Linq;
using Game.Core.Coordinates;

namespace ExpandableX.Core
{
    /// <summary>
    /// Undoable swap of a building to another variant definition, preserving its id, transform, and
    /// configuration (id-as-truth, ADR-0008). Scheduled via the session's <c>PlayerActionManager</c>
    /// so it runs at a safe point and lands on the undo stack. Its reverse swaps back to the previous
    /// definition, so undo restores the prior variant.
    /// </summary>
    internal sealed class ExpandableXSwapVariantAction : PlayerAction
    {
        private readonly BuildingId _buildingId;
        private readonly GlobalTileTransform _transform;
        private readonly IBuildingConfiguration _configuration;
        private readonly IBuildingDefinition _fromDefinition;
        private readonly IBuildingDefinition _toDefinition;

        public ExpandableXSwapVariantAction(
            IMapModel map,
            Player executor,
            in BuildingId buildingId,
            in GlobalTileTransform transform,
            IBuildingConfiguration configuration,
            IBuildingDefinition fromDefinition,
            IBuildingDefinition toDefinition)
            : base(map, executor)
        {
            _buildingId = buildingId;
            _transform = transform;
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
            BuildingModel created = Map.CreateBuilding(_toDefinition, in _transform, in _buildingId, _configuration);

            if (wasSelected)
            {
                // Set() alone no-ops for a same-id model, so the panel's OnChanged → MarkDirty never
                // fires. Clear then Set forces the event; both run within this tick, so there's no
                // visible flicker, and the panel rebuilds against the new variant.
                selection.Clear();
                selection.Set(new[] { created });
            }

            // Undo: swap back to the definition we came from (same id/transform/config).
            reverseAction = new ExpandableXSwapVariantAction(
                Map, Executor, in _buildingId, in _transform, _configuration, _toDefinition, _fromDefinition);
        }
    }
}
