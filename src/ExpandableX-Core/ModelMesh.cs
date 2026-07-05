using System.Collections.Generic;
using Game.Core.Rendering.MeshGeneration;
using ShapezShifter.Kit;
using UnityEngine;
using float3 = Unity.Mathematics.float3;

namespace ExpandableX.Core
{
    /// <summary>Options for loading an authored model file into an <see cref="IModelMesh"/>.</summary>
    public sealed record ModelImportOptions
    {
        /// <summary>
        /// Rotation (Euler degrees) applied on load to correct the FBX root transform the game's mesh
        /// loader omits (see the painter extraction README). Default <b>+90° about X</b> (Blender Z-up →
        /// game Y-up), confirmed against prior testing — still worth re-verifying with a live round-trip.
        /// </summary>
        public float3 RotationEulerDegrees { get; init; } = new float3(90f, 0f, 0f);
    }

    /// <summary>
    /// Factory for <see cref="IModelMesh"/> values used in a <see cref="ModelPieceSet"/>. The file
    /// loaders apply the load-time rotation correction and cache the built mesh — so the load happens
    /// once and the same value can back several pieces or roles (e.g. reused as both a seam and an input
    /// bridge). Loading is lazy (on first <see cref="IModelMesh.Resolve"/>, during session-init synthesis)
    /// so it isn't attempted at mod-load time before the game is ready.
    /// </summary>
    public static class ModelMesh
    {
        /// <summary>Wrap an already-built, already-correctly-oriented <see cref="ILODMesh"/> (escape hatch).</summary>
        public static IModelMesh FromLodMesh(ILODMesh mesh) => new PrebuiltMesh(mesh);

        /// <summary>
        /// Load one single-mesh FBX/GLB and use it at every LOD level. Convenience for the common
        /// single-LOD case; for distinct per-LOD meshes use <see cref="FromFiles"/>.
        /// </summary>
        public static IModelMesh FromFile(string path, ModelImportOptions? options = null)
            => FromFiles(new[] { path }, options);

        /// <summary>
        /// Load one single-mesh FBX/GLB per LOD level (<paramref name="lodPaths"/>[0] = LOD0, …). Levels
        /// beyond the supplied count — or with a null/empty entry — reuse the nearest lower LOD, matching
        /// how the game's own building meshes fall back. Up to 6 levels are used.
        /// </summary>
        public static IModelMesh FromFiles(IReadOnlyList<string> lodPaths, ModelImportOptions? options = null)
            => new FileMesh(lodPaths, options ?? new ModelImportOptions());

        private sealed class PrebuiltMesh : IModelMesh
        {
            private readonly ILODMesh _mesh;
            public PrebuiltMesh(ILODMesh mesh) => _mesh = mesh;
            public ILODMesh Resolve(IMeshCache meshCache) => _mesh;
        }

        private sealed class FileMesh : IModelMesh
        {
            private const int LodCount = 6;

            private readonly IReadOnlyList<string> _lodPaths;
            private readonly ModelImportOptions _options;
            private ILODMesh _cached;

            public FileMesh(IReadOnlyList<string> lodPaths, ModelImportOptions options)
            {
                _lodPaths = lodPaths;
                _options = options;
            }

            public ILODMesh Resolve(IMeshCache meshCache)
            {
                if (_cached != null)
                {
                    return _cached;
                }

                Quaternion rotation = Quaternion.Euler(
                    _options.RotationEulerDegrees.x, _options.RotationEulerDegrees.y, _options.RotationEulerDegrees.z);

                // Load each supplied LOD once; a missing OR unloadable level falls back to the nearest
                // lower one (same as a blank entry), so one bad/absent file degrades gracefully.
                var byLod = new Mesh[LodCount];
                for (int lod = 0; lod < LodCount; lod++)
                {
                    if (lod >= _lodPaths.Count || string.IsNullOrWhiteSpace(_lodPaths[lod]))
                    {
                        continue;
                    }

                    try
                    {
                        Mesh mesh = FileMeshLoader.LoadSingleMeshFromFile(_lodPaths[lod]);
                        ApplyRotation(mesh, rotation);
                        byLod[lod] = mesh;
                    }
                    catch (System.Exception e)
                    {
                        Debug.LogWarning($"ExpandableX: could not load LOD{lod} model '{_lodPaths[lod]}', falling back to a lower LOD: {e.Message}");
                    }
                }

                if (byLod[0] == null)
                {
                    // No LOD0 (missing or all-failed) — nothing to fall back to. Surface it so the
                    // composer's fail-open catch reverts this variant to the cloned base model.
                    throw new System.InvalidOperationException(
                        $"No usable LOD0 mesh from [{string.Join(", ", _lodPaths)}]");
                }

                _cached = BuildLodMesh(byLod);
                return _cached;
            }

            /// <summary>Build a 6-LOD mesh, using each supplied level or reusing the nearest lower one via the LOD builder.</summary>
            private static ILODMesh BuildLodMesh(Mesh[] byLod)
            {
                ILodMesh1Builder b0 = MeshLod.Create().AddLod0Mesh(byLod[0]);
                ILodMesh2Builder b1 = byLod[1] != null ? b0.AddLod1Mesh(byLod[1]) : b0.UseLod0AsLod1();
                ILodMesh3Builder b2 = byLod[2] != null ? b1.AddLod2Mesh(byLod[2]) : b1.UseLod1AsLod2();
                ILodMesh4Builder b3 = byLod[3] != null ? b2.AddLod3Mesh(byLod[3]) : b2.UseLod2AsLod3();
                ILodMesh5Builder b4 = byLod[4] != null ? b3.AddLod4Mesh(byLod[4]) : b3.UseLod3AsLod4();
                ILodMeshBuilder b5 = byLod[5] != null ? b4.AddLod5Mesh(byLod[5]) : b4.UseLod4AsLod5();
                return b5.BuildLod6Mesh();
            }

            private static void ApplyRotation(Mesh mesh, Quaternion q)
            {
                if (q == Quaternion.identity)
                {
                    return;
                }

                Vector3[] vertices = mesh.vertices;
                for (int i = 0; i < vertices.Length; i++)
                {
                    vertices[i] = q * vertices[i];
                }
                mesh.vertices = vertices;

                Vector3[] normals = mesh.normals;
                if (normals is { Length: > 0 })
                {
                    for (int i = 0; i < normals.Length; i++)
                    {
                        normals[i] = q * normals[i];
                    }
                    mesh.normals = normals;
                }

                Vector4[] tangents = mesh.tangents;
                if (tangents is { Length: > 0 })
                {
                    for (int i = 0; i < tangents.Length; i++)
                    {
                        Vector3 rotated = q * new Vector3(tangents[i].x, tangents[i].y, tangents[i].z);
                        tangents[i] = new Vector4(rotated.x, rotated.y, rotated.z, tangents[i].w);
                    }
                    mesh.tangents = tangents;
                }

                mesh.RecalculateBounds();
            }
        }
    }
}
