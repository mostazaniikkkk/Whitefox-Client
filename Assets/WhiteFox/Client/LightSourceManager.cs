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
        private readonly List<KeyValuePair<BlockPos, BlockState>> nearest = new List<KeyValuePair<BlockPos, BlockState>>();

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
        }

        private void OnChunkUnloaded(ChunkPos position)
        {
            var removed = new List<BlockPos>();
            foreach (var source in sources.Keys)
            {
                if (source.ChunkPos == position) removed.Add(source);
            }
            foreach (var source in removed) sources.Remove(source);
        }

        private void OnBlockChanged(BlockPos position, BlockState previous, BlockState state)
        {
            if (state != null && state.Luminance > 0) sources[position] = state;
            else sources.Remove(position);
        }

        public void Update(Vector3 camera, float time)
        {
            nearest.Clear();
            if (Enabled)
            {
                var cameraX = camera.x + RenderOrigin.X;
                var cameraY = camera.y;
                var cameraZ = -camera.z + RenderOrigin.Z;
                nearest.AddRange(sources);
                nearest.Sort((a, b) => Distance(a.Key, cameraX, cameraY, cameraZ).CompareTo(Distance(b.Key, cameraX, cameraY, cameraZ)));
                if (nearest.Count > MaxLights) nearest.RemoveRange(MaxLights, nearest.Count - MaxLights);
            }

            while (pool.Count < nearest.Count) pool.Add(CreateLight());
            for (var i = 0; i < pool.Count; i++)
            {
                var light = pool[i];
                if (i >= nearest.Count)
                {
                    if (light.enabled) light.enabled = false;
                    continue;
                }
                var position = nearest[i].Key;
                var state = nearest[i].Value;
                var style = Style(state);
                light.enabled = true;
                light.transform.localPosition = RenderOrigin.ToRender(position.X + 0.5, position.Y + style.Height, position.Z + 0.5);
                light.color = style.Color;
                // range from the Minecraft luminance (level 15 reached ~15 blocks), intensity with a subtle flicker for flames
                light.range = state.Luminance * 1.1f;
                var flicker = style.Flicker ? 1.0f + 0.06f * Mathf.Sin(time * 11.0f + position.X * 1.7f + position.Z * 2.3f) + 0.04f * Mathf.Sin(time * 23.0f + position.Y) : 1.0f;
                light.intensity = style.Intensity * Intensity * Exposure * (state.Luminance / 15.0f) * flicker;
                light.shadows = i < ShadowedLights ? LightShadows.Soft : LightShadows.None;
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
