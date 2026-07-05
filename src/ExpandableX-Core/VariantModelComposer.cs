using System;
using System.Collections.Generic;
using Game.Core.BuildingLogic.Data;
using Game.Core.Coordinates;
using Game.Core.Rendering.MeshGeneration;
using UnityEngine;
using float3 = Unity.Mathematics.float3;
using ILogger = Core.Logging.ILogger;

namespace ExpandableX.Core
{
    /// <summary>
    /// Bakes a synthesised variant's <b>Composed model</b> (CONTEXT.md, ADR-0016): the piece's
    /// <see cref="ModelPieceSet.Body"/> plus a bridge piece for each connector the variant actually
    /// carries, in the role/type it takes, combined into <c>MainMeshPerLayer</c> and the derived
    /// blueprint/preview meshes via the game's own <see cref="BuildingMeshGenerator"/> path. The
    /// stock theme end-cap path (<c>StaticBuildingMeshBuilder</c>) is untouched — bridges add only the
    /// geometry joining a cap to the body.
    ///
    /// Runs during variant synthesis (<see cref="ExpandableXSimulationSystemsRewirer"/>), which lacks
    /// the mesh cache and theme resources; those are stashed on the registry by
    /// <see cref="ExpandableXBuildingsRewirer"/> (the earlier buildings phase) and passed in here.
    /// Any failure returns null so the caller falls back to cloning the base model (fail-open).
    ///
    /// Each bridge is placed at its connector's local pivot with the connector's <c>TileDirection</c>
    /// rotation — the game's own end-cap transform — plus the author's optional per-bridge offset (or an
    /// explicit per-slot pivot). The load-time orientation correction lives in the <see cref="ModelMesh"/>
    /// loader, so meshes reaching here are already in game orientation.
    ///
    /// For a mirrored base, each source mesh is reflected via <see cref="IMeshCache.GenerateMirroredMesh"/>
    /// and placed against that base's already-mirrored connector data — an exact reflection of the
    /// non-mirrored model, so a single <see cref="ModelPieceSet"/> serves both a base and its mirror.
    /// </summary>
    internal static class VariantModelComposer
    {
        private const int LodCount = 6;

        public static IBuildingDrawData? TryCompose(
            string variantIdName,
            IBuildingDrawData baseDraw,
            IBuildingConnectorData synthData,
            IReadOnlyList<ConnectorSlot> expandedSlots,
            IReadOnlyDictionary<string, SlotRole> slotState,
            ConnectorDataResolver resolver,
            ModelPieceSet models,
            IMeshCache meshCache,
            VisualThemeBaseResources theme,
            bool mirrored,
            ILogger logger)
        {
            try
            {
                // A mirrored variant reuses the same authored meshes, reflected by the game's own mesh
                // mirror (no separate mirrored FBX). Placement uses this base's connector data, which is
                // already mirrored, so reflecting each source mesh + normal placement = an exact reflection
                // of the non-mirrored composed model.
                ILODMesh Resolve(IModelMesh mesh)
                {
                    ILODMesh lod = mesh.Resolve(meshCache);
                    return mirrored ? meshCache.GenerateMirroredMesh(lod) : lod;
                }

                ILODMesh body = Resolve(models.Body);

                // Gather the (mesh, transform) of each live connector's bridge for this variant.
                var bridges = new List<(ILODMesh Mesh, Matrix4x4 Transform)>();
                foreach (ConnectorSlot slot in expandedSlots)
                {
                    if (!slotState.TryGetValue(slot.Id, out SlotRole role) || role == SlotRole.Disabled)
                    {
                        continue; // no connector on this face -> flat body, no bridge
                    }

                    if (resolver.ResolveVisible(slot.Connector) is not BuildingBaseIO geometry)
                    {
                        continue;
                    }

                    ResolvedBridge? resolved = role == SlotRole.Join
                        ? models.ResolveSeam()
                        : models.ResolveBridge(slot.Id, geometry.GetType(), role);
                    if (resolved is not { } bridge)
                    {
                        continue; // author supplied no piece for this combination
                    }

                    // Offset is in the mesh's own frame; reflecting the mesh reflects its X, so the offset's
                    // X reflects too to stay exact under mirroring.
                    float offsetX = mirrored ? -bridge.Offset.x : bridge.Offset.x;
                    Matrix4x4 offsetMatrix = Matrix4x4.Translate(new Vector3(offsetX, bridge.Offset.y, bridge.Offset.z));
                    Matrix4x4 transform;
                    if (bridge.InPlace)
                    {
                        // Authored in the building's model frame — use verbatim (offset nudge only), no
                        // connector-derived rotation/translation.
                        transform = offsetMatrix;
                    }
                    else
                    {
                        // Place at the connector pivot, then nudge by the author's offset in the mesh's own frame.
                        LocalTilePivot pivot = bridge.ExplicitPivot ?? new LocalTilePivot(geometry.Position_L, geometry.TileDirection);
                        WorldCoordinate position = (GlobalTileCoordinate.Origin + pivot.Position).ToCenter_W();
                        transform = FastMatrix.TranslateRotate(position, pivot.Direction) * offsetMatrix;
                    }

                    bridges.Add((Resolve(bridge.Mesh), transform));
                }

                ILODMesh composed = CombineLayerMesh(variantIdName, body, bridges, meshCache);

                // Replace layer 0 (the body) with the composed mesh; keep any upper layers as-is. Our
                // expandable buildings are single-layer today, so those are empty in practice — multi-layer
                // composed bodies (a body/bridge mesh per building layer) are future work.
                ILODMesh[] mainPerLayer = (ILODMesh[])baseDraw.MainMeshPerLayer.Clone();
                mainPerLayer[0] = composed;

                // A custom blueprint model overrides the auto-derived one (used for static forms of parts
                // the composed body omits — e.g. the painter's animated roller). We compose the same
                // bridge pieces onto it as onto the main body, so the blueprint reflects each variant's
                // connectors with real bridge geometry (not just the auto-added end caps). Null => derive
                // the blueprint from the composed body, which already carries the bridges.
                ILODMesh? blueprintOverride = models.Blueprint is { } blueprint
                    ? CombineLayerMesh($"{variantIdName}_blueprint", Resolve(blueprint), bridges, meshCache)
                    : null;

                ILODMesh isolated = BuildingMeshGenerator.GenerateIsolatedBlueprintMesh(
                    variantIdName, mainPerLayer, blueprintOverride, baseDraw.GlassMesh, meshCache);
                ILODMesh combined = BuildingMeshGenerator.GenerateFullBlueprintMesh(
                    variantIdName, isolated, synthData, theme, meshCache);
                IMeshReference preview = BuildingMeshGenerator.GeneratePreviewMeshPerLayer(
                    variantIdName, mainPerLayer, blueprintOverride, synthData, meshCache, theme);

                return new BuildingDrawData(
                    baseDraw.RenderVoidBelow,
                    mainPerLayer,
                    isolated,
                    combined,
                    preview,
                    baseDraw.GlassMesh,
                    baseDraw.Colliders,
                    baseDraw.CustomDrawData,
                    baseDraw.HasCustomOverviewMesh,
                    baseDraw.CustomOverviewMesh,
                    baseDraw.SimulationRendererDrawsMainMesh);
            }
            catch (Exception e)
            {
                logger.Info.Log($"ExpandableX-Core: model composition for '{variantIdName}' failed, falling back to cloned base model: {e}");
                return null;
            }
        }

        /// <summary>Combine the body and its bridge pieces into one <see cref="ILODMesh"/>, per LOD, via the game's <see cref="MeshBuilder"/>.</summary>
        private static ILODMesh CombineLayerMesh(
            string id,
            ILODMesh body,
            IReadOnlyList<(ILODMesh Mesh, Matrix4x4 Transform)> bridges,
            IMeshCache meshCache)
        {
            var perLod = new List<IMeshReference>(LodCount);
            for (int lod = 0; lod < LodCount; lod++)
            {
                using var builder = new MeshBuilder($"ExpandableXComposedBody({id})", lod);
                builder.AddTranslate(body, float3.zero);
                foreach ((ILODMesh mesh, Matrix4x4 transform) in bridges)
                {
                    builder.AddTRS(mesh, transform);
                }

                if (builder.Empty)
                {
                    perLod.Add(null);
                    continue;
                }

                TemporaryMeshReference combined = builder.GenerateSingleMeshMax65KVertices();
                meshCache.Register(combined);
                perLod.Add(combined);
            }

            return new RuntimeLODMesh(perLod);
        }
    }
}
