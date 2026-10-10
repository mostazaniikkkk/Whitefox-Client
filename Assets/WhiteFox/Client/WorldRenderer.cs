// WhiteFox - Unity port of Minosoft (https://gitlab.bixilon.de/bixilon/minosoft), Copyright (C) 2020-2023 Moritz Zwerger
// SPDX-License-Identifier: GPL-3.0-or-later

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Rendering;
using WhiteFox.Core.Meshing;
using WhiteFox.Core.Util;
using WhiteFox.Core.Worlds;
using Debug = UnityEngine.Debug;

namespace WhiteFox.Client
{
    /// <summary>
    /// Chunk renderer (Minosoft's ChunkRenderer + ChunkMeshingQueue + MeshLoadingQueue): sections that changed are
    /// snapshotted on the main thread, meshed on worker threads (nearest first, at most cores - 1 at a time) and
    /// uploaded with a per frame time budget. Every section is a GameObject, so Unity does the frustum culling.
    /// </summary>
    public sealed class WorldRenderer : IDisposable
    {
        public static readonly VertexAttributeDescriptor[] Layout =
        {
            new VertexAttributeDescriptor(VertexAttribute.Position, VertexAttributeFormat.Float32, 3),
            new VertexAttributeDescriptor(VertexAttribute.Color, VertexAttributeFormat.UNorm8, 4),
            new VertexAttributeDescriptor(VertexAttribute.TexCoord0, VertexAttributeFormat.Float32, 4),
            new VertexAttributeDescriptor(VertexAttribute.TexCoord1, VertexAttributeFormat.Float32, 2),
            new VertexAttributeDescriptor(VertexAttribute.TexCoord2, VertexAttributeFormat.SNorm8, 4),
            new VertexAttributeDescriptor(VertexAttribute.TexCoord3, VertexAttributeFormat.SNorm8, 4),
        };

        private const MeshUpdateFlags UploadFlags = MeshUpdateFlags.DontRecalculateBounds | MeshUpdateFlags.DontValidateIndices | MeshUpdateFlags.DontNotifyMeshUsers;

        private readonly World world;
        private SectionMesher mesher;
        private Material[] materials;
        private bool castShadows;
        private readonly Transform root;

        private readonly Dictionary<SectionPos, GameObject> sections = new Dictionary<SectionPos, GameObject>();
        private readonly HashSet<SectionPos> dirty = new HashSet<SectionPos>();
        private readonly HashSet<SectionPos> running = new HashSet<SectionPos>();
        private readonly ConcurrentQueue<SectionMeshData> finished = new ConcurrentQueue<SectionMeshData>();
        private readonly ConcurrentQueue<Exception> errors = new ConcurrentQueue<Exception>();
        private readonly List<SectionPos> candidates = new List<SectionPos>();
        private readonly int maxWorkers = Math.Max(1, Environment.ProcessorCount - 1);

        /// <summary>Upload budget per frame in milliseconds.</summary>
        public float UploadBudget = 4.0f;

        // cave culling: occlusion graph of every meshed section, recomputed when the camera section or a graph changes
        private readonly Dictionary<SectionPos, ulong> graphs = new Dictionary<SectionPos, ulong>();
        private static readonly List<BlockPos> EmptySpecial = new List<BlockPos>();
        private readonly OcclusionCuller[] cullers = { new OcclusionCuller(), new OcclusionCuller() };
        private int nextCuller;
        private Task<OcclusionCuller> cullTask;
        private bool graphsChanged = true;
        private SectionPos? cullCamera;
        private float lastCull = -1;
        private bool cullingEnabled = true;

        /// <summary>Chunk radius the culling walk explores.</summary>
        public int ViewDistance = 12;
        public int VisibleCount { get; private set; }

        public bool CullingEnabled
        {
            get => cullingEnabled;
            set
            {
                if (cullingEnabled == value) return;
                cullingEnabled = value;
                graphsChanged = true;
            }
        }

        public int SectionCount => sections.Count;

        /// <summary>The blocks of a section drawn by block entity renderers changed (empty list: none or unloaded).</summary>
        public event Action<SectionPos, List<BlockPos>> SpecialBlocksChanged;

        private OcclusionCuller appliedCuller;

        /// <summary>Whether cave culling currently shows the section.</summary>
        public bool IsVisible(SectionPos position) => appliedCuller == null || appliedCuller.Visible.Contains(position);
        public int QueueSize => dirty.Count;
        public int RunningCount => running.Count;

        /// <param name="materials">opaque, translucent and water material (one per submesh)</param>
        public WorldRenderer(World world, SectionMesher mesher, Material[] materials, bool castShadows, Transform root)
        {
            this.world = world;
            this.mesher = mesher;
            this.root = root;
            this.materials = materials;
            this.castShadows = castShadows;

            world.SectionsChanged += OnSectionsChanged;
            world.ChunkLoaded += OnChunkLoaded;
            world.ChunkUnloaded += OnChunkUnloaded;
            RenderOrigin.Changed += OnOriginChanged;
        }

        private static Vector3 SectionOrigin(SectionPos position) => RenderOrigin.ToRender(position.X * 16, position.Y * 16, position.Z * 16);

        private void OnOriginChanged()
        {
            foreach (var pair in sections) pair.Value.transform.localPosition = SectionOrigin(pair.Key);
        }

        /// <summary>Switches every section to other materials (classic / realistic rendering).</summary>
        /// <summary>Which of the opaque / translucent / water parts each section has (bits 0, 1, 2).</summary>
        private readonly Dictionary<SectionPos, int> parts = new Dictionary<SectionPos, int>();
        private readonly Dictionary<int, Material[]> partMaterials = new Dictionary<int, Material[]>();

        /// <summary>
        /// The materials of a section: only those of its non-empty parts. An empty submesh still costs a draw call, and
        /// the water material between the translucent ones of every section (sorted back to front) kept the batches
        /// from merging, which was most of the cost of the realistic water.
        /// </summary>
        private Material[] MaterialsFor(int mask)
        {
            if (partMaterials.TryGetValue(mask, out var list)) return list;
            var result = new List<Material>();
            for (var i = 0; i < 3; i++)
            {
                if ((mask & (1 << i)) != 0) result.Add(materials[i]);
            }
            list = result.ToArray();
            partMaterials[mask] = list;
            return list;
        }

        public void SetMaterials(Material[] materials, bool castShadows)
        {
            this.materials = materials;
            this.castShadows = castShadows;
            partMaterials.Clear();
            foreach (var pair in sections)
            {
                var section = pair.Value;
                var renderer = section.GetComponent<MeshRenderer>();
                renderer.sharedMaterials = MaterialsFor(parts.TryGetValue(pair.Key, out var mask) ? mask : 7);
                renderer.shadowCastingMode = castShadows ? ShadowCastingMode.On : ShadowCastingMode.Off;
            }
            cullCamera = null; // apply the shadow modes of hidden sections again
        }

        private void OnSectionsChanged(IEnumerable<SectionPos> changed)
        {
            foreach (var section in changed) dirty.Add(section);
        }

        /// <summary>A new chunk can complete its neighbours, so their sections are meshed again (border culling).</summary>
        private void OnChunkLoaded(Chunk chunk)
        {
            // every section of the chunk itself (a resent chunk may have emptied some), the neighbours below
            for (var y = world.MinSection; y <= world.MaxSection; y++) dirty.Add(new SectionPos(chunk.Position.X, y, chunk.Position.Z));
            for (var dx = -1; dx <= 1; dx++)
            {
                for (var dz = -1; dz <= 1; dz++)
                {
                    var neighbour = world.GetChunk(chunk.Position.X + dx, chunk.Position.Z + dz);
                    if (neighbour == null) continue;
                    for (var i = 0; i < neighbour.Sections.Length; i++)
                    {
                        var section = neighbour.Sections[i];
                        if (section == null || section.IsEmpty) continue;
                        dirty.Add(new SectionPos(neighbour.Position.X, i + world.MinSection, neighbour.Position.Z));
                    }
                }
            }
        }

        private void OnChunkUnloaded(ChunkPos position)
        {
            for (var y = world.MinSection; y <= world.MaxSection; y++)
            {
                var key = new SectionPos(position.X, y, position.Z);
                dirty.Remove(key);
                Remove(key);
                graphs.Remove(key);
                SpecialBlocksChanged?.Invoke(key, EmptySpecial);
            }
        }

        /// <param name="cameraPosition">Camera position in render space (relative to <see cref="RenderOrigin"/>).</param>
        /// <summary>No new meshing jobs (resource reload in progress).</summary>
        public bool Paused;

        /// <summary>After a resource reload: the new mesher (colors, fluid textures) and every section meshed again.</summary>
        public void SetMesher(SectionMesher mesher)
        {
            this.mesher = mesher;
            foreach (var chunk in world.Chunks)
            {
                for (var i = 0; i < chunk.Sections.Length; i++)
                {
                    var section = chunk.Sections[i];
                    if (section == null || section.IsEmpty) continue;
                    dirty.Add(new SectionPos(chunk.Position.X, i + world.MinSection, chunk.Position.Z));
                }
            }
        }

        public void Update(Vector3 cameraPosition)
        {
            while (errors.TryDequeue(out var error)) Debug.LogException(error);
            var camera = new Vec3d(cameraPosition.x + RenderOrigin.X, cameraPosition.y, -cameraPosition.z + RenderOrigin.Z);
            Upload();
            Schedule(camera);
            Cull(camera);
        }

        /// <summary>
        /// Hides the sections the culling walk does not reach. The walk runs on a worker thread over a copy of the graphs
        /// (at most 20 times per second, and whenever the camera enters another section); the result is applied when done.
        /// </summary>
        private void Cull(Vec3d camera)
        {
            if (cullTask != null && cullTask.IsCompleted)
            {
                var finishedCuller = cullTask.Result;
                cullTask = null;
                ApplyVisibility(finishedCuller);
            }

            var section = new SectionPos((int)Math.Floor(camera.X / 16), (int)Math.Floor(camera.Y / 16), (int)Math.Floor(camera.Z / 16));
            var moved = !cullCamera.HasValue || !cullCamera.Value.Equals(section);
            if (!moved && (!graphsChanged || Time.unscaledTime - lastCull < 0.05f)) return;
            if (!cullingEnabled)
            {
                cullCamera = section;
                graphsChanged = false;
                ApplyVisibility(null);
                return;
            }
            if (cullTask != null) return; // the next walk starts when this one is applied
            cullCamera = section;
            graphsChanged = false;
            lastCull = Time.unscaledTime;

            var graphCopy = new Dictionary<SectionPos, ulong>(graphs);
            var loaded = new HashSet<ChunkPos>();
            foreach (var chunk in world.Chunks) loaded.Add(chunk.Position);
            int minSection = world.MinSection, maxSection = world.MaxSection, distance = ViewDistance + 1;
            var culler = cullers[nextCuller];
            nextCuller ^= 1;
            cullTask = Task.Run(() =>
            {
                culler.Compute(section, minSection, maxSection, distance, loaded.Contains, position => graphCopy.TryGetValue(position, out var graph) ? graph : OcclusionGraph.All);
                return culler;
            });
        }

        private void ApplyVisibility(OcclusionCuller culler)
        {
            appliedCuller = culler;
            var visible = 0;
            foreach (var pair in sections)
            {
                var shown = culler == null || culler.Visible.Contains(pair.Key);
                SetVisible(pair.Value.GetComponent<MeshRenderer>(), shown);
                if (shown) visible++;
            }
            VisibleCount = visible;
        }

        /// <summary>
        /// Hidden sections still cast sun shadows (a cave must stay dark even if the rock above it is culled), they are
        /// only left out of the camera passes.
        /// </summary>
        private void SetVisible(MeshRenderer renderer, bool shown)
        {
            var enabled = shown || castShadows;
            var mode = !castShadows ? ShadowCastingMode.Off : shown ? ShadowCastingMode.On : ShadowCastingMode.ShadowsOnly;
            if (renderer.enabled != enabled) renderer.enabled = enabled;
            if (renderer.shadowCastingMode != mode) renderer.shadowCastingMode = mode;
        }

        private void Schedule(Vec3d camera)
        {
            if (Paused || dirty.Count == 0 || running.Count >= maxWorkers) return;

            var cameraSection = new Vector3((float)(camera.X / 16.0), (float)(camera.Y / 16.0), (float)(camera.Z / 16.0));
            candidates.Clear();
            foreach (var section in dirty)
            {
                if (running.Contains(section)) continue;
                candidates.Add(section);
            }
            candidates.Sort((a, b) => Distance(a, cameraSection).CompareTo(Distance(b, cameraSection)));

            foreach (var position in candidates)
            {
                if (running.Count >= maxWorkers) break;
                var chunk = world.GetChunk(position.X, position.Z);
                if (chunk == null)
                {
                    dirty.Remove(position);
                    continue;
                }
                var section = chunk.GetSection(position.Y);
                if (section == null || section.IsEmpty)
                {
                    dirty.Remove(position);
                    Remove(position);
                    if (graphs.Remove(position)) graphsChanged = true;
                    SpecialBlocksChanged?.Invoke(position, EmptySpecial);
                    continue;
                }
                if (!world.IsComplete(chunk.Position)) continue; // wait for all neighbours (Minosoft: ChunkNeighbours.complete)

                dirty.Remove(position);
                running.Add(position);
                var snapshot = SectionMesher.Snapshot(world, position);
                Task.Run(() =>
                {
                    try
                    {
                        finished.Enqueue(mesher.Mesh(snapshot));
                    }
                    catch (Exception exception)
                    {
                        errors.Enqueue(exception);
                        finished.Enqueue(new SectionMeshData { Position = snapshot.Position });
                    }
                });
            }
        }

        private static float Distance(SectionPos section, Vector3 camera)
        {
            var dx = section.X + 0.5f - camera.x;
            var dy = section.Y + 0.5f - camera.y;
            var dz = section.Z + 0.5f - camera.z;
            return dx * dx + dy * dy + dz * dz;
        }

        private void Upload()
        {
            var watch = Stopwatch.StartNew();
            while (watch.Elapsed.TotalMilliseconds < UploadBudget && finished.TryDequeue(out var data))
            {
                running.Remove(data.Position);
                if (!world.IsLoaded(data.Position.Chunk)) continue;
                if (!graphs.TryGetValue(data.Position, out var old) || old != data.Occlusion)
                {
                    graphs[data.Position] = data.Occlusion;
                    graphsChanged = true;
                }
                SpecialBlocksChanged?.Invoke(data.Position, data.Special);
                if (data.IsEmpty)
                {
                    Remove(data.Position);
                    continue;
                }
                Apply(data);
            }
        }

        private void Apply(SectionMeshData data)
        {
            if (!sections.TryGetValue(data.Position, out var gameObject))
            {
                gameObject = new GameObject("Section " + data.Position, typeof(MeshFilter), typeof(MeshRenderer));
                gameObject.transform.SetParent(root, false);
                gameObject.transform.localPosition = SectionOrigin(data.Position);
                graphsChanged = true;
                var renderer = gameObject.GetComponent<MeshRenderer>();
                renderer.shadowCastingMode = castShadows ? ShadowCastingMode.On : ShadowCastingMode.Off;
                renderer.receiveShadows = true;
                renderer.lightProbeUsage = LightProbeUsage.Off;
                renderer.reflectionProbeUsage = ReflectionProbeUsage.Off;
                var created = new Mesh { name = "Section " + data.Position };
                created.MarkDynamic();
                gameObject.GetComponent<MeshFilter>().sharedMesh = created;
                sections[data.Position] = gameObject;
            }

            var mesh = gameObject.GetComponent<MeshFilter>().sharedMesh;
            var vertexCount = data.Vertices.Count;
            mesh.Clear();
            mesh.SetVertexBufferParams(vertexCount, Layout);
            mesh.SetVertexBufferData(data.Vertices, 0, 0, vertexCount, 0, UploadFlags);

            var opaque = data.OpaqueIndices.Count;
            var translucent = data.TranslucentIndices.Count;
            var water = data.WaterIndices.Count;
            var indices = new int[opaque + translucent + water];
            data.OpaqueIndices.CopyTo(indices, 0);
            data.TranslucentIndices.CopyTo(indices, opaque);
            data.WaterIndices.CopyTo(indices, opaque + translucent);
            mesh.SetIndexBufferParams(indices.Length, IndexFormat.UInt32);
            mesh.SetIndexBufferData(indices, 0, 0, indices.Length, UploadFlags);

            // one submesh per non-empty part, with the matching materials
            var bounds = new Bounds(new Vector3(8, 8, -8), new Vector3(16, 16, 16));
            var counts = new[] { opaque, translucent, water };
            var mask = 0;
            var submeshes = 0;
            for (var i = 0; i < 3; i++)
            {
                if (counts[i] > 0)
                {
                    mask |= 1 << i;
                    submeshes++;
                }
            }
            mesh.subMeshCount = Mathf.Max(1, submeshes);
            var start = 0;
            var index = 0;
            for (var i = 0; i < 3; i++)
            {
                if (counts[i] > 0) mesh.SetSubMesh(index++, new SubMeshDescriptor(start, counts[i]) { bounds = bounds, vertexCount = vertexCount }, UploadFlags);
                start += counts[i];
            }
            if (submeshes == 0) mesh.SetSubMesh(0, new SubMeshDescriptor(0, 0) { bounds = bounds, vertexCount = vertexCount }, UploadFlags);
            mesh.bounds = bounds;
            if (mask == 0) mask = 1;
            if (!parts.TryGetValue(data.Position, out var previous) || previous != mask || gameObject.GetComponent<MeshRenderer>().sharedMaterials.Length != submeshes)
            {
                parts[data.Position] = mask;
                gameObject.GetComponent<MeshRenderer>().sharedMaterials = MaterialsFor(mask);
            }
        }

        private void Remove(SectionPos position)
        {
            if (!sections.TryGetValue(position, out var gameObject)) return;
            sections.Remove(position);
            parts.Remove(position);
            UnityEngine.Object.Destroy(gameObject.GetComponent<MeshFilter>().sharedMesh);
            UnityEngine.Object.Destroy(gameObject);
        }

        public void Dispose()
        {
            world.SectionsChanged -= OnSectionsChanged;
            world.ChunkLoaded -= OnChunkLoaded;
            world.ChunkUnloaded -= OnChunkUnloaded;
            RenderOrigin.Changed -= OnOriginChanged;
            foreach (var position in new List<SectionPos>(sections.Keys)) Remove(position);
        }
    }
}
