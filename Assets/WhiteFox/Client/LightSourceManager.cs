// WhiteFox - Unity port of Minosoft (https://gitlab.bixilon.de/bixilon/minosoft), Copyright (C) 2020-2023 Moritz Zwerger
// SPDX-License-Identifier: GPL-3.0-or-later

using System;
using System.Collections.Generic;
using UnityEngine;
using WhiteFox.Core.Data;
using WhiteFox.Core.Util;
using WhiteFox.Core.Worlds;

namespace WhiteFox.Client
{
    /// <summary>
    /// Realistic mode: every light emitting block (torch, glowstone, lava, lantern...) near the camera becomes a real
    /// point light with physical inverse square falloff, the nearest ones with shadows. Replaces Minecraft's stepped
    /// block light levels, which only the classic mode uses.
    /// </summary>
    public sealed class LightSourceManager : IDisposable
    {
        private readonly World world;
        private readonly Transform root;
        private readonly Dictionary<BlockPos, BlockState> sources = new Dictionary<BlockPos, BlockState>();
        private readonly List<Light> pool = new List<Light>();

        /// <summary>Maximum number of point lights (Forward+ handles a few hundred, but every light costs).</summary>
        public int MaxLights = 96;
        /// <summary>The nearest lights cast shadows (point light shadows render 6 faces each).</summary>
        public int ShadowedLights = 4;
        public float Intensity = 1.0f;
        /// <summary>Eye adaptation stand-in: block lights are much weaker than daylight (set by the environment).</summary>
        public float Exposure = 1.0f;
        public bool Enabled = true;

        public int SourceCount => sources.Count;

        private readonly List<Light> dynamicPool = new List<Light>();

        /// <summary>
        /// Moving lights (held torches, burning entities, luminous dropped items): one point light each, placed every
        /// frame; they do not cast shadows (they move with the camera most of the time).
        /// </summary>
        public void SetDynamicLights(List<KeyValuePair<Vector3, BlockState>> lights, float time)
        {
            while (dynamicPool.Count < lights.Count) dynamicPool.Add(CreateLight());
            for (var i = 0; i < dynamicPool.Count; i++)
            {
                var light = dynamicPool[i];
                if (!Enabled || i >= lights.Count)
                {
                    if (light.enabled) light.enabled = false;
                    continue;
                }
                var state = lights[i].Value;
                var style = Style(state);
                light.enabled = true;
                light.transform.localPosition = lights[i].Key;
                light.color = style.Color;
                light.range = state.Luminance * 1.1f;
                var flicker = style.Flicker ? 1.0f + 0.06f * Mathf.Sin(time * 11.0f + i * 1.3f) + 0.04f * Mathf.Sin(time * 23.0f + i) : 1.0f;
                light.intensity = style.Intensity * Intensity * Exposure * (state.Luminance / 15.0f) * flicker;
                light.shadows = LightShadows.None;
            }
        }

        public LightSourceManager(World world, Transform parent)
        {
            this.world = world;
            root = new GameObject("Light sources").transform;
            root.SetParent(parent, false);
            world.ChunkLoaded += OnChunkLoaded;
            world.ChunkUnloaded += OnChunkUnloaded;
            world.BlockChanged += OnBlockChanged;
            foreach (var chunk in world.Chunks) OnChunkLoaded(chunk);
        }

        private void OnChunkLoaded(Chunk chunk)
        {
            for (var i = 0; i < chunk.Sections.Length; i++)
            {
                var section = chunk.Sections[i];
                if (section == null || section.LuminantCount <= 0 || section.Blocks == null) continue;
                var baseX = chunk.Position.X << 4;
                var baseY = (i + world.MinSection) << 4;
                var baseZ = chunk.Position.Z << 4;
                for (var index = 0; index < SectionIndex.Volume; index++)
                {
                    var state = section.Blocks[index];
                    if (state == null || state.Luminance <= 0) continue;
                    sources[new BlockPos(baseX + SectionIndex.X(index), baseY + SectionIndex.Y(index), baseZ + SectionIndex.Z(index))] = state;
                }
            }
            cellsDirty = true;
        }

        private void OnChunkUnloaded(ChunkPos position)
        {
            var removed = new List<BlockPos>();
            foreach (var source in sources.Keys)
            {
                if (source.ChunkPos == position) removed.Add(source);
            }
            foreach (var source in removed) sources.Remove(source);
            if (removed.Count > 0) cellsDirty = true;
        }

        private void OnBlockChanged(BlockPos position, BlockState previous, BlockState state)
        {
            if (state != null && state.Luminance > 0) sources[position] = state;
            else if (!sources.Remove(position)) return;
            cellsDirty = true;
        }

        /// <summary>Sections the cave culling hides (their lights are skipped); null: every section counts.</summary>
        public System.Func<SectionPos, bool> SectionVisible;

        // light sources grouped into 4x4x4 cells: a lava lake becomes a few lights instead of hundreds
        private sealed class Cell
        {
            public BlockState State;
            public int Count;
            public int Luminance;
            public double X, Y, Z;
            public float Distance;
        }

        private readonly Dictionary<BlockPos, Cell> cells = new Dictionary<BlockPos, Cell>();
        private readonly List<Cell> candidates = new List<Cell>();
        private readonly Plane[] frustum = new Plane[6];
        private bool cellsDirty = true;
        private int lastSelection = -100;
        /// <summary>Lights farther than this (blocks) are not used: their light hardly reaches the view.</summary>
        public float MaxDistance = 48;

        private static BlockPos CellOf(BlockPos p) => new BlockPos(p.X >> 2, p.Y >> 2, p.Z >> 2);

        private void RebuildCells()
        {
            cells.Clear();
            foreach (var pair in sources)
            {
                var key = CellOf(pair.Key);
                if (!cells.TryGetValue(key, out var cell)) cells[key] = cell = new Cell();
                cell.Count++;
                cell.X += pair.Key.X + 0.5;
                cell.Y += pair.Key.Y + Style(pair.Value).Height;
                cell.Z += pair.Key.Z + 0.5;
                if (pair.Value.Luminance >= cell.Luminance)
                {
                    cell.Luminance = pair.Value.Luminance;
                    cell.State = pair.Value;
                }
            }
            cellsDirty = false;
        }

        /// <summary>
        /// Picks the lights of this frame: the nearest light cells in view (camera frustum and cave culling) within
        /// <see cref="MaxDistance"/>, chosen again every 4 frames; colors and flicker update every frame.
        /// </summary>
        public void Update(Vector3 camera, float time, Camera view = null)
        {
            if (!Enabled)
            {
                candidates.Clear();
            }
            else if (cellsDirty || Time.frameCount - lastSelection >= 4)
            {
                if (cellsDirty) RebuildCells();
                lastSelection = Time.frameCount;
                var cameraX = camera.x + RenderOrigin.X;
                var cameraY = camera.y;
                var cameraZ = -camera.z + RenderOrigin.Z;
                if (view != null) GeometryUtility.CalculateFrustumPlanes(view, frustum);
                candidates.Clear();
                var max = MaxDistance * MaxDistance;
                foreach (var cell in cells.Values)
                {
                    double x = cell.X / cell.Count, y = cell.Y / cell.Count, z = cell.Z / cell.Count;
                    var dx = x - cameraX;
                    var dy = y - cameraY;
                    var dz = z - cameraZ;
                    var distance = dx * dx + dy * dy + dz * dz;
                    if (distance > max) continue;
                    var range = cell.Luminance * 1.1f + 2;
                    // a light in a section the cave culling hides only matters when the camera is within its reach
                    if (SectionVisible != null && distance > range * range && !SectionVisible(new SectionPos((int)Math.Floor(x) >> 4, (int)Math.Floor(y) >> 4, (int)Math.Floor(z) >> 4))) continue;
                    if (view != null && !GeometryUtility.TestPlanesAABB(frustum, new Bounds(RenderOrigin.ToRender(x, y, z), Vector3.one * range * 2))) continue;
                    cell.Distance = (float)distance;
                    candidates.Add(cell);
                }
                candidates.Sort((first, second) => first.Distance.CompareTo(second.Distance));
                if (candidates.Count > MaxLights) candidates.RemoveRange(MaxLights, candidates.Count - MaxLights);
            }

            while (pool.Count < candidates.Count) pool.Add(CreateLight());
            for (var i = 0; i < pool.Count; i++)
            {
                var light = pool[i];
                if (i >= candidates.Count)
                {
                    if (light.enabled) light.enabled = false;
                    continue;
                }
                var cell = candidates[i];
                var state = cell.State;
                var style = Style(state);
                if (!light.enabled) light.enabled = true;
                double x = cell.X / cell.Count, y = cell.Y / cell.Count, z = cell.Z / cell.Count;
                light.transform.localPosition = RenderOrigin.ToRender(x, y, z);
                light.color = style.Color;
                // range from the Minecraft luminance (level 15 reaches ~15 blocks), a little more for a group of sources
                light.range = state.Luminance * 1.1f + (cell.Count > 1 ? 2 : 0);
                var flicker = style.Flicker ? 1.0f + 0.06f * Mathf.Sin(time * 11.0f + (float)x * 1.7f + (float)z * 2.3f) + 0.04f * Mathf.Sin(time * 23.0f + (float)y) : 1.0f;
                light.intensity = style.Intensity * Intensity * Exposure * (state.Luminance / 15.0f) * flicker * Mathf.Min(Mathf.Sqrt(cell.Count), 3);
                var shadows = i < ShadowedLights ? LightShadows.Soft : LightShadows.None;
                if (light.shadows != shadows) light.shadows = shadows;
            }
        }

        private static float Distance(BlockPos p, float x, float y, float z)
        {
            var dx = p.X + 0.5f - x;
            var dy = p.Y + 0.5f - y;
            var dz = p.Z + 0.5f - z;
            return dx * dx + dy * dy + dz * dz;
        }

        private Light CreateLight()
        {
            var gameObject = new GameObject("Block light");
            gameObject.transform.SetParent(root, false);
            var light = gameObject.AddComponent<Light>();
            light.type = LightType.Point;
            light.renderMode = LightRenderMode.ForcePixel;
            light.shadowNearPlane = 0.3f;
            return light;
        }

        private readonly struct LightStyle
        {
            public readonly Color Color;
            public readonly float Intensity;
            public readonly float Height;
            public readonly bool Flicker;

            public LightStyle(Color color, float intensity, float height, bool flicker)
            {
                Color = color;
                Intensity = intensity;
                Height = height;
                Flicker = flicker;
            }
        }

        /// <summary>Approximate color temperature of each kind of light source.</summary>
        private static LightStyle Style(BlockState state)
        {
            var path = state.Block.Name.Path;
            if (path.Contains("soul")) return new LightStyle(new Color(0.45f, 0.85f, 1.0f), 2.5f, 0.6f, path.Contains("torch") || path.Contains("fire"));
            if (path.Contains("torch")) return new LightStyle(new Color(1.0f, 0.62f, 0.32f), 3.0f, 0.7f, true);
            if (path.Contains("lava") || path.Contains("magma")) return new LightStyle(new Color(1.0f, 0.42f, 0.12f), 3.5f, 0.9f, false);
            if (path.Contains("fire") || path.Contains("campfire")) return new LightStyle(new Color(1.0f, 0.55f, 0.25f), 3.5f, 0.6f, true);
            if (path.Contains("lantern") && !path.Contains("sea")) return new LightStyle(new Color(1.0f, 0.7f, 0.4f), 3.0f, 0.4f, false);
            if (path.Contains("sea_lantern") || path.Contains("beacon") || path.Contains("end_rod")) return new LightStyle(new Color(0.8f, 0.92f, 1.0f), 3.0f, 0.5f, false);
            if (path.Contains("froglight") || path.Contains("shroomlight") || path.Contains("glowstone")) return new LightStyle(new Color(1.0f, 0.82f, 0.55f), 3.5f, 0.5f, false);
            if (path.Contains("redstone")) return new LightStyle(new Color(1.0f, 0.75f, 0.45f), 2.5f, 0.5f, false);
            return new LightStyle(new Color(1.0f, 0.85f, 0.65f), 2.5f, 0.5f, false);
        }

        public void Dispose()
        {
            world.ChunkLoaded -= OnChunkLoaded;
            world.ChunkUnloaded -= OnChunkUnloaded;
            world.BlockChanged -= OnBlockChanged;
            if (root != null) UnityEngine.Object.Destroy(root.gameObject);
        }
    }
}
