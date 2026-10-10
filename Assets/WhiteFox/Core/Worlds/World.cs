// WhiteFox - Unity port of Minosoft (https://gitlab.bixilon.de/bixilon/minosoft), Copyright (C) 2020-2023 Moritz Zwerger
// SPDX-License-Identifier: GPL-3.0-or-later

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using WhiteFox.Core.Data;
using WhiteFox.Core.Util;

namespace WhiteFox.Core.Worlds
{
    /// <summary>
    /// Client side world (port of Minosoft's World + ChunkManager). Mutations must happen on the game thread;
    /// mesher threads only read (they snapshot sections before meshing).
    /// </summary>
    public sealed class World
    {
        public int MinY { get; private set; }
        public int Height { get; private set; }
        public int MinSection { get; private set; }
        public int SectionCount { get; private set; }

        private readonly ConcurrentDictionary<ChunkPos, Chunk> chunks = new ConcurrentDictionary<ChunkPos, Chunk>();
        public readonly LightEngine Light;

        /// <summary>Time of day in ticks (0..24000). 6000 = noon.</summary>
        public long TimeOfDay = 6000;

        public readonly BlockEntityStore BlockEntities = new BlockEntityStore();

        /// <summary>Weather (Game Event 1/2/7/8): rain and thunder strength 0..1.</summary>
        public bool Raining;
        public float RainLevel;
        public float ThunderLevel;

        /// <summary>The server's biome registry (empty in the local world: everything is plains).</summary>
        public BiomeRegistry Biomes = new BiomeRegistry();
        public bool HasBiomes => Biomes.ById.Count > 0;

        public void SetBiomes(ChunkPos position, ushort[] biomes)
        {
            var chunk = GetChunk(position.X, position.Z);
            if (chunk == null) return;
            chunk.Biomes = biomes;
            var changed = new List<SectionPos>();
            for (var y = MinSection; y <= MaxSection; y++) changed.Add(new SectionPos(position.X, y, position.Z));
            SectionsChanged?.Invoke(changed);
        }

        public Biome GetBiome(int x, int y, int z)
        {
            var chunk = GetChunk(x >> 4, z >> 4);
            if (chunk?.Biomes == null) return BiomeRegistry.Plains;
            y = Math.Max(MinY, Math.Min(MaxY, y));
            var index = ((y - MinY) >> 4) * 64 + ((((y >> 2) & 3) << 4) | (((z >> 2) & 3) << 2) | ((x >> 2) & 3));
            return index < chunk.Biomes.Length ? Biomes.Get(chunk.Biomes[index]) : BiomeRegistry.Plains;
        }

        public event Action<Chunk> ChunkLoaded;
        public event Action<ChunkPos> ChunkUnloaded;
        public event Action<BlockPos, BlockState, BlockState> BlockChanged;

        /// <summary>Sections whose blocks or light changed and that need to be meshed again.</summary>
        public event Action<IEnumerable<SectionPos>> SectionsChanged;

        /// <summary>Overworld dimensions since 1.18: y -64..319.</summary>
        public World(int minY = -64, int height = 384)
        {
            Light = new LightEngine(this);
            SetDimensions(minY, height);
        }

        private void SetDimensions(int minY, int height)
        {
            MinY = minY;
            Height = height;
            MinSection = minY >> 4;
            SectionCount = height >> 4;
        }

        /// <summary>Unloads everything and switches to another dimension size (join game, respawn, dimension change).</summary>
        public void Reset(int minY, int height)
        {
            Clear();
            SetDimensions(minY, height);
        }

        public int MaxY => MinY + Height - 1;
        public int MaxSection => MinSection + SectionCount - 1;

        public ICollection<Chunk> Chunks => chunks.Values;
        public int ChunkCount => chunks.Count;

        public Chunk GetChunk(ChunkPos position) => chunks.TryGetValue(position, out var chunk) ? chunk : null;
        public Chunk GetChunk(int x, int z) => GetChunk(new ChunkPos(x, z));
        public bool IsLoaded(ChunkPos position) => chunks.ContainsKey(position);

        /// <summary>True if the chunk and all 8 neighbours are loaded (Minosoft's "complete" neighbours, required for meshing).</summary>
        public bool IsComplete(ChunkPos position)
        {
            for (var dx = -1; dx <= 1; dx++)
            {
                for (var dz = -1; dz <= 1; dz++)
                {
                    if (!chunks.ContainsKey(new ChunkPos(position.X + dx, position.Z + dz))) return false;
                }
            }
            return true;
        }

        public BlockState GetBlock(int x, int y, int z)
        {
            if (y < MinY || y > MaxY) return null;
            var chunk = GetChunk(x >> 4, z >> 4);
            return chunk?.Get(x & 0x0F, y, z & 0x0F);
        }

        public BlockState GetBlock(BlockPos p) => GetBlock(p.X, p.Y, p.Z);

        public int GetLight(int x, int y, int z)
        {
            if (y > MaxY) return 0xF0;
            if (y < MinY) return 0;
            var chunk = GetChunk(x >> 4, z >> 4);
            return chunk?.GetLight(x & 0x0F, y, z & 0x0F) ?? 0xF0;
        }

        /// <summary>Adds a fully generated/received chunk, calculates its light and notifies listeners.</summary>
        public void AddChunk(Chunk chunk)
        {
            if (chunks.ContainsKey(chunk.Position)) RemoveChunk(chunk.Position); // full chunk resend
            chunk.RecalculateHeightmap();
            chunks[chunk.Position] = chunk;
            var changed = Light.Relight(new[] { chunk.Position });
            ChunkLoaded?.Invoke(chunk);
            SectionsChanged?.Invoke(changed);
        }

        public void RemoveChunk(ChunkPos position)
        {
            if (!chunks.TryRemove(position, out _)) return;
            BlockEntities.RemoveChunk(position);
            ChunkUnloaded?.Invoke(position);
        }

        public void Clear()
        {
            foreach (var position in new List<ChunkPos>(chunks.Keys)) RemoveChunk(position);
        }

        /// <summary>
        /// Many block changes at once (Update Section Blocks, /fill): the blocks are set first and the light of the touched
        /// region is calculated once, instead of once per block.
        /// </summary>
        public void SetBlocks(IReadOnlyList<KeyValuePair<BlockPos, BlockState>> changes)
        {
            if (changes.Count < 4)
            {
                foreach (var change in changes) SetBlock(change.Key, change.Value);
                return;
            }
            var changed = new HashSet<SectionPos>();
            var region = new HashSet<ChunkPos>();
            var applied = new List<(BlockPos position, BlockState previous, BlockState state)>();
            foreach (var change in changes)
            {
                var p = change.Key;
                var state = change.Value;
                if (p.Y < MinY || p.Y > MaxY) continue;
                var chunk = GetChunk(p.ChunkPos);
                if (chunk == null) continue;
                var previous = chunk.SetRaw(p.X & 0x0F, p.Y, p.Z & 0x0F, state);
                if (previous == state) continue;
                if (previous?.Block != state?.Block) BlockEntities.Remove(p);
                var oldHeight = chunk.GetHeight(p.X & 0x0F, p.Z & 0x0F);
                chunk.RecalculateHeightmap(p.X & 0x0F, p.Z & 0x0F);
                var newHeight = chunk.GetHeight(p.X & 0x0F, p.Z & 0x0F);
                for (var dx = -1; dx <= 1; dx++)
                {
                    for (var dy = -1; dy <= 1; dy++)
                    {
                        for (var dz = -1; dz <= 1; dz++) changed.Add(new SectionPos((p.X + dx) >> 4, (p.Y + dy) >> 4, (p.Z + dz) >> 4));
                    }
                }
                var lightRelevant = (previous?.Luminance ?? 0) != (state?.Luminance ?? 0) || (previous?.Opacity ?? 0) != (state?.Opacity ?? 0) || oldHeight != newHeight;
                if (lightRelevant)
                {
                    for (var y = Math.Min(oldHeight, newHeight) >> 4; y <= Math.Max(oldHeight, newHeight) >> 4; y++) changed.Add(new SectionPos(p.X >> 4, y, p.Z >> 4));
                    for (var dx = -1; dx <= 1; dx++)
                    {
                        for (var dz = -1; dz <= 1; dz++)
                        {
                            var position = new ChunkPos(chunk.Position.X + dx, chunk.Position.Z + dz);
                            if (IsLoaded(position)) region.Add(position);
                        }
                    }
                }
                applied.Add((p, previous, state));
            }
            if (region.Count > 0) changed.UnionWith(Light.Relight(new List<ChunkPos>(region)));
            foreach (var (position, previous, state) in applied) BlockChanged?.Invoke(position, previous, state);
            if (changed.Count > 0) SectionsChanged?.Invoke(changed);
        }

        /// <summary>Sets a block, updates heightmap and light, and fires change events (Minosoft: Chunk.apply + light update).</summary>
        public void SetBlock(BlockPos p, BlockState state)
        {
            if (p.Y < MinY || p.Y > MaxY) return;
            var chunk = GetChunk(p.ChunkPos);
            if (chunk == null) return;

            var previous = chunk.SetRaw(p.X & 0x0F, p.Y, p.Z & 0x0F, state);
            if (previous == state) return;
            if (previous?.Block != state?.Block) BlockEntities.Remove(p);

            var oldHeight = chunk.GetHeight(p.X & 0x0F, p.Z & 0x0F);
            chunk.RecalculateHeightmap(p.X & 0x0F, p.Z & 0x0F);
            var newHeight = chunk.GetHeight(p.X & 0x0F, p.Z & 0x0F);

            var changed = new HashSet<SectionPos>();
            // the block itself and its neighbours across section borders
            for (var dx = -1; dx <= 1; dx++)
            {
                for (var dy = -1; dy <= 1; dy++)
                {
                    for (var dz = -1; dz <= 1; dz++)
                    {
                        changed.Add(new SectionPos((p.X + dx) >> 4, (p.Y + dy) >> 4, (p.Z + dz) >> 4));
                    }
                }
            }

            var lightRelevant = (previous?.Luminance ?? 0) != (state?.Luminance ?? 0) || (previous?.Opacity ?? 0) != (state?.Opacity ?? 0) || oldHeight != newHeight;
            if (lightRelevant)
            {
                // the implicit sky light (above the heightmap) changed for the whole range between the two heights
                for (var y = Math.Min(oldHeight, newHeight) >> 4; y <= Math.Max(oldHeight, newHeight) >> 4; y++)
                {
                    changed.Add(new SectionPos(p.X >> 4, y, p.Z >> 4));
                }
                var region = new List<ChunkPos>(9);
                for (var dx = -1; dx <= 1; dx++)
                {
                    for (var dz = -1; dz <= 1; dz++)
                    {
                        var position = new ChunkPos(chunk.Position.X + dx, chunk.Position.Z + dz);
                        if (IsLoaded(position)) region.Add(position);
                    }
                }
                changed.UnionWith(Light.Relight(region));
            }

            BlockChanged?.Invoke(p, previous, state);
            SectionsChanged?.Invoke(changed);
        }
    }
}
