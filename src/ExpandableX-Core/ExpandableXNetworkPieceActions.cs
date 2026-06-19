using System.Linq;
using Game.Core.Coordinates;

namespace ExpandableX.Core
{
    /// <summary>
    /// Undoable placement of a new network piece (the building a grow adds). On first execution it lets
    /// the map allocate a fresh <see cref="BuildingId"/> exactly as normal placement does — no minted
    /// ids, so blueprints/copy-paste stay exact (see the blueprint-compatibility note). Its reverse
    /// removes that building, and the remove's reverse re-creates it with the <b>same</b> id, so
    /// undo/redo are stable. Composed with a variant swap via <c>CombinedUndoablePlayerAction</c> to make
    /// a grow atomic. The network re-forms from geometry — no linking is stored.
    /// </summary>
    internal sealed class ExpandableXPlaceBuildingAction : PlayerAction
    {
        private readonly IBuildingDefinition _definition;
        private readonly GlobalTileTransform _transform;
        private readonly IBuildingConfiguration _configuration;
        private readonly BuildingId? _id;

        public ExpandableXPlaceBuildingAction(
            IMapModel map,
            Player executor,
            IBuildingDefinition definition,
            in GlobalTileTransform transform,
            IBuildingConfiguration configuration,
            BuildingId? id = null)
            : base(map, executor)
        {
            _definition = definition;
            _transform = transform;
            _configuration = configuration;
            _id = id;
        }

        public override PlayerActionMode Mode => PlayerActionMode.Undoable;

        public override bool IsPossible(IInteractionMode interactionMode) => true;

        public override void ExecuteInternal(IInteractionMode interactionMode, out IPlayerAction reverseAction)
        {
            // Re-create with the original id on redo; otherwise let the map assign one (normal placement).
            BuildingModel created = _id is { } id
                ? Map.CreateBuilding(_definition, in _transform, in id, _configuration)
                : Map.CreateBuilding(_definition, in _transform, _configuration);

            reverseAction = new ExpandableXRemoveBuildingAction(
                Map, Executor, created.Id, _definition, in _transform, _configuration);
        }
    }

    /// <summary>
    /// Undoable removal of a network piece (the building a shrink drops). Its reverse re-places the same
    /// building with the same id/transform/config, so undo restores it exactly. Mirror of
    /// <see cref="ExpandableXPlaceBuildingAction"/>.
    /// </summary>
    internal sealed class ExpandableXRemoveBuildingAction : PlayerAction
    {
        private readonly BuildingId _id;
        private readonly IBuildingDefinition _definition;
        private readonly GlobalTileTransform _transform;
        private readonly IBuildingConfiguration _configuration;

        public ExpandableXRemoveBuildingAction(
            IMapModel map,
            Player executor,
            in BuildingId id,
            IBuildingDefinition definition,
            in GlobalTileTransform transform,
            IBuildingConfiguration configuration)
            : base(map, executor)
        {
            _id = id;
            _definition = definition;
            _transform = transform;
            _configuration = configuration;
        }

        public override PlayerActionMode Mode => PlayerActionMode.Undoable;

        public override bool IsPossible(IInteractionMode interactionMode) => true;

        public override void ExecuteInternal(IInteractionMode interactionMode, out IPlayerAction reverseAction)
        {
            // Drop this building from the selection before deleting it, so its selection visual / ghost
            // model doesn't linger once the map no longer has it (the swap action does the same upkeep).
            var selection = Executor.InteractionState.BuildingSelection;
            bool wasSelected = selection.Any(b => b.Id == _id);
            BuildingModel[] remaining = wasSelected
                ? selection.Where(b => b.Id != _id).ToArray()
                : System.Array.Empty<BuildingModel>();

            Map.DeleteBuilding(in _id);

            if (wasSelected)
            {
                selection.Clear();
                if (remaining.Length > 0)
                {
                    selection.Set(remaining);
                }
            }

            reverseAction = new ExpandableXPlaceBuildingAction(
                Map, Executor, _definition, in _transform, _configuration, _id);
        }
    }
}
