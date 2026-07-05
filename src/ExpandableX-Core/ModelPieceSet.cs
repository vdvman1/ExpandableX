using System;
using System.Collections.Generic;
using Game.Core.Coordinates;
using Game.Core.Rendering.MeshGeneration;
using float3 = Unity.Mathematics.float3;

namespace ExpandableX.Core
{
    /// <summary>
    /// A source of a multi-LOD mesh, resolved once (and cached) at bake time into an
    /// <see cref="ILODMesh"/>. Kept abstract so <c>ExpandableX-Core</c> stays independent of any file
    /// format. Build these with <see cref="ModelMesh"/> — a single loaded instance can be shared across
    /// pieces and roles (e.g. the same mesh as a seam and as an input bridge).
    /// </summary>
    public interface IModelMesh
    {
        ILODMesh Resolve(IMeshCache meshCache);
    }

    /// <summary>
    /// A bridge piece resolved for one connector: the mesh, plus how to place it. By default the
    /// framework places it at the connector's pivot (tile centre + <c>TileDirection</c>) — the same
    /// transform the game uses for end caps — so one mesh authored in the canonical frame (connector on
    /// the <b>East</b>/+X face, the identity rotation) is reused across every matching face.
    /// <see cref="Offset"/> nudges it in its own local frame (for model-specific spacing);
    /// <see cref="ExplicitPivot"/> (per-slot only) replaces the derived pivot; <see cref="InPlace"/>
    /// (per-slot only) skips connector-derived placement entirely and uses the mesh as authored in the
    /// building's model frame — the simplest option for irregular static layouts where reasoning about
    /// the canonical East face is awkward.
    /// </summary>
    public readonly struct ResolvedBridge
    {
        public readonly IModelMesh Mesh;
        public readonly float3 Offset;
        public readonly LocalTilePivot? ExplicitPivot;
        public readonly bool InPlace;

        public ResolvedBridge(IModelMesh mesh, float3 offset, LocalTilePivot? explicitPivot, bool inPlace = false)
        {
            Mesh = mesh;
            Offset = offset;
            ExplicitPivot = explicitPivot;
            InPlace = inPlace;
        }
    }

    /// <summary>
    /// The opt-in authored model of a piece: a <b>Body piece</b> (the clean main model with the
    /// connector-attachment geometry stripped) plus <b>Bridge piece</b>s that re-add that geometry per
    /// live connector, keyed by <b>connector IO type</b> + role with optional per-slot overrides, and an
    /// optional seam for <see cref="SlotRole.Join"/> faces. See CONTEXT.md "Composed model" and ADR-0016.
    /// Declared on a <see cref="PieceSpec"/>; consumed by <see cref="VariantModelComposer"/>.
    ///
    /// Bridges are keyed by <see cref="IBuildingIO"/> subtype rather than a fixed medium enum, so
    /// connector types added by other mods work, and an author can register against a base type
    /// (matches every subtype) or a specific subtype — the <b>most specific</b> registered type
    /// assignable from the connector wins (see <see cref="ResolveBridge"/>).
    /// </summary>
    public sealed class ModelPieceSet
    {
        /// <summary>The clean body model, shared by every variant of the piece. Required.</summary>
        public IModelMesh Body { get; }

        /// <summary>
        /// Optional custom static blueprint model, shared by every variant (the game's
        /// <c>CustomBlueprintMesh</c> path). Use it to include static forms of parts the composed body
        /// omits because they're drawn/animated by a simulation renderer from custom draw data (e.g. the
        /// painter's roller/hinge). When null, the blueprint is auto-derived from the composed body.
        /// Connector end caps are added to it from the variant's connector data either way.
        /// </summary>
        public IModelMesh? Blueprint { get; }

        private readonly ResolvedBridge? _seam;
        private readonly IReadOnlyList<(Type IoType, SlotRole Role, IModelMesh Mesh, float3 Offset)> _byType;
        private readonly IReadOnlyDictionary<(string SlotId, SlotRole Role), ResolvedBridge> _bySlot;

        private ModelPieceSet(
            IModelMesh body,
            IModelMesh? blueprint,
            ResolvedBridge? seam,
            IReadOnlyList<(Type, SlotRole, IModelMesh, float3)> byType,
            IReadOnlyDictionary<(string, SlotRole), ResolvedBridge> bySlot)
        {
            Body = body ?? throw new ArgumentNullException(nameof(body));
            Blueprint = blueprint;
            _seam = seam;
            _byType = byType;
            _bySlot = bySlot;
        }

        /// <summary>
        /// The bridge for a connector of runtime type <paramref name="connectorType"/> in the given
        /// active role, or null if none is declared (a flat face). A per-slot override wins; otherwise
        /// the most-specific registered <c>(ioType, role)</c> whose type is assignable from the
        /// connector's type is used.
        /// </summary>
        public ResolvedBridge? ResolveBridge(string slotId, Type connectorType, SlotRole role)
        {
            if (_bySlot.TryGetValue((slotId, role), out ResolvedBridge perSlot))
            {
                return perSlot;
            }

            (Type IoType, SlotRole Role, IModelMesh Mesh, float3 Offset)? best = null;
            foreach ((Type IoType, SlotRole Role, IModelMesh Mesh, float3 Offset) candidate in _byType)
            {
                if (candidate.Role != role || !candidate.IoType.IsAssignableFrom(connectorType))
                {
                    continue;
                }

                // Keep the more specific type: prefer `candidate` when the current best is a base of it.
                if (best is null || best.Value.IoType.IsAssignableFrom(candidate.IoType))
                {
                    best = candidate;
                }
            }

            return best is { } b ? new ResolvedBridge(b.Mesh, b.Offset, null) : null;
        }

        /// <summary>The seam placed on a <see cref="SlotRole.Join"/> face (at that face's connector pivot), or null for a flat join face.</summary>
        public ResolvedBridge? ResolveSeam() => _seam;

        /// <summary>Start building a set from its required body piece.</summary>
        public static Builder WithBody(IModelMesh body) => new(body);

        public sealed class Builder
        {
            private readonly IModelMesh _body;
            private IModelMesh? _blueprint;
            private ResolvedBridge? _seam;
            private readonly List<(Type, SlotRole, IModelMesh, float3)> _byType = new();
            private readonly Dictionary<(string, SlotRole), ResolvedBridge> _bySlot = new();

            internal Builder(IModelMesh body) => _body = body;

            /// <summary>Supply a custom static blueprint model (see <see cref="ModelPieceSet.Blueprint"/>).</summary>
            public Builder Blueprint(IModelMesh mesh)
            {
                _blueprint = mesh;
                return this;
            }

            /// <summary>Declare the default bridge for every connector assignable to <typeparamref name="T"/> in <paramref name="role"/>.</summary>
            public Builder Bridge<T>(SlotRole role, IModelMesh mesh, float3 offset = default) where T : class, IBuildingIO
                => Bridge(typeof(T), role, mesh, offset);

            /// <summary>Declare the default bridge for every connector assignable to <paramref name="ioType"/> in <paramref name="role"/>.</summary>
            public Builder Bridge(Type ioType, SlotRole role, IModelMesh mesh, float3 offset = default)
            {
                _byType.Add((ioType, role, mesh, offset));
                return this;
            }

            /// <summary>Override the bridge for one slot in <paramref name="role"/>, placed at its connector pivot (+ optional offset). Wins over the type default.</summary>
            public Builder BridgeForSlot(string slotId, SlotRole role, IModelMesh mesh, float3 offset = default)
            {
                _bySlot[(slotId, role)] = new ResolvedBridge(mesh, offset, null);
                return this;
            }

            /// <summary>Override the bridge for one slot in <paramref name="role"/> at an explicit local pivot (position + orientation), bypassing the connector-derived placement.</summary>
            public Builder BridgeForSlotAt(string slotId, SlotRole role, IModelMesh mesh, LocalTilePivot pivot, float3 offset = default)
            {
                _bySlot[(slotId, role)] = new ResolvedBridge(mesh, offset, pivot);
                return this;
            }

            /// <summary>
            /// Override the bridge for one slot in <paramref name="role"/>, authored <b>in place</b> in the
            /// building's model frame and used verbatim (no connector-derived rotation/translation). Author
            /// it in the same scene as the body, where it sits on the real building — no need to reason
            /// about the canonical East face. Ideal for irregular static layouts (e.g. the painter).
            /// </summary>
            public Builder BridgeForSlotInPlace(string slotId, SlotRole role, IModelMesh mesh, float3 offset = default)
            {
                _bySlot[(slotId, role)] = new ResolvedBridge(mesh, offset, null, inPlace: true);
                return this;
            }

            /// <summary>Declare the seam added on a <see cref="SlotRole.Join"/> face.</summary>
            public Builder Seam(IModelMesh mesh, float3 offset = default)
            {
                _seam = new ResolvedBridge(mesh, offset, null);
                return this;
            }

            public ModelPieceSet Build() => new(_body, _blueprint, _seam, _byType, _bySlot);
        }
    }
}
