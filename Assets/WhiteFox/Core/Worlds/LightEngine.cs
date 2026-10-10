// WhiteFox - Unity port of Minosoft (https://gitlab.bixilon.de/bixilon/minosoft), Copyright (C) 2020-2023 Moritz Zwerger
// SPDX-License-Identifier: GPL-3.0-or-later

using System;
using System.Collections.Generic;
using WhiteFox.Core.Util;

namespace WhiteFox.Core.Worlds
{
    /// <summary>
    /// Client side light engine. Like Minosoft, server light is ignored and everything is calculated locally:
    /// sky light is implicit (15) above the heightmap and flood filled below it, block light is flood filled from
    /// luminant blocks. Minosoft decreases light by resetting and recalculating every section in range; this port does
    /// the same for the 3x3 chunk region around a change, but with an iterative queue instead of recursion.
    /// </summary>
    public sealed class LightEngine
    {
        private const int Block = 0;
        private const int Sky = 1;

        private readonly World world;
        private readonly Queue<BlockPos> queue = new Queue<BlockPos>();
        private HashSet<SectionPos> written;
        private HashSet<ChunkPos> regionSet;

        public LightEngine(World world)
        {
            this.world = world;
        }

        /// <summary>Recalculates light of the given chunks and returns every section whose light changed.</summary>
        public HashSet<SectionPos> Relight(IList<ChunkPos> region)
        {
            regionSet = new HashSet<ChunkPos>(region);
            written = new HashSet<SectionPos>();
            var snapshots = new Dictionary<SectionPos, byte[]>();

            foreach (var position in region)
            {
                var chunk = world.GetChunk(position);
                if (chunk == null) continue;
                for (var i = 0; i < chunk.Sections.Length; i++)
                {
                    var section = chunk.Sections[i];
                    if (section == null) continue;
                    snapshots[new SectionPos(position.X, i + world.MinSection, position.Z)] = section.Light;
                    section.Light = null;
                }
            }

            Propagate(region, Block);
            Propagate(region, Sky);

            var changed = new HashSet<SectionPos>();
            foreach (var position in region)
            {
                var chunk = world.GetChunk(position);
                if (chunk == null) continue;
                for (var i = 0; i < chunk.Sections.Length; i++)
                {
                    var section = chunk.Sections[i];
                    var key = new SectionPos(position.X, i + world.MinSection, position.Z);
                    snapshots.TryGetValue(key, out var before);
                    if (!SameLight(before, section?.Light)) changed.Add(key);
                }
            }
            foreach (var section in written)
            {
                if (!regionSet.Contains(section.Chunk)) changed.Add(section);
            }
            written = null;
            regionSet = null;
            return changed;
        }

        private static bool SameLight(byte[] a, byte[] b)
        {
            if (a == b) return true;
            if (a == null || b == null)
            {
                var other = a ?? b;
                foreach (var value in other)
                {
                    if (value != 0) return false;
                }
                return true;
            }
            for (var i = 0; i < a.Length; i++)
            {
                if (a[i] != b[i]) return false;
            }
            return true;
        }

        private void Propagate(IList<ChunkPos> region, int channel)
        {
            foreach (var position in region)
            {
                var chunk = world.GetChunk(position);
                if (chunk == null) continue;
                if (channel == Block) SeedBlockSources(chunk);
                else SeedSky(chunk);
                SeedBorders(chunk, channel);
            }

            while (queue.Count > 0)
            {
                var p = queue.Dequeue();
                var level = Get(p.X, p.Y, p.Z, channel);
                if (level <= 1) continue;
                foreach (var direction in Directions.All)
                {
                    TrySpread(p.X + direction.X(), p.Y + direction.Y(), p.Z + direction.Z(), level, channel);
                }
            }
        }

        private void TrySpread(int x, int y, int z, int level, int channel)
        {
            if (y < world.MinY || y > world.MaxY) return;
            var chunk = world.GetChunk(x >> 4, z >> 4);
            if (chunk == null) return;
            if (channel == Sky && y >= chunk.GetHeight(x & 0x0F, z & 0x0F)) return; // implicit 15
            var state = chunk.Get(x & 0x0F, y, z & 0x0F);
            var opacity = state?.Opacity ?? 0;
            if (opacity >= 15) return;
            var next = level - Math.Max(1, opacity);
            if (next <= 0) return;
            if (Get(chunk, x, y, z, channel) >= next) return;
            Set(chunk, x, y, z, channel, next);
            queue.Enqueue(new BlockPos(x, y, z));
        }

        private void SeedBlockSources(Chunk chunk)
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
                    var x = baseX + SectionIndex.X(index);
                    var y = baseY + SectionIndex.Y(index);
                    var z = baseZ + SectionIndex.Z(index);
                    if (Get(chunk, x, y, z, Block) >= state.Luminance) continue;
                    Set(chunk, x, y, z, Block, state.Luminance);
                    queue.Enqueue(new BlockPos(x, y, z));
                }
            }
        }

        /// <summary>The implicit light above the heightmap shines into neighbouring columns and the block below.</summary>
        private void SeedSky(Chunk chunk)
        {
            var baseX = chunk.Position.X << 4;
            var baseZ = chunk.Position.Z << 4;
            for (var z = 0; z < 16; z++)
            {
                for (var x = 0; x < 16; x++)
                {
                    var height = chunk.GetHeight(x, z);
                    var wx = baseX + x;
                    var wz = baseZ + z;
                    if (height > world.MinY) TrySpread(wx, height - 1, wz, 15, Sky);

                    foreach (var direction in Directions.All)
                    {
                        if (direction.GetAxis() == Axis.Y) continue;
                        var nx = wx + direction.X();
                        var nz = wz + direction.Z();
                        var neighbour = world.GetChunk(nx >> 4, nz >> 4);
                        if (neighbour == null) continue;
                        var neighbourHeight = neighbour.GetHeight(nx & 0x0F, nz & 0x0F);
                        for (var y = height; y < neighbourHeight; y++)
                        {
                            TrySpread(nx, y, nz, 15, Sky);
                        }
                    }
                }
            }
        }

        /// <summary>Light already present in loaded chunks outside the region flows back in.</summary>
        private void SeedBorders(Chunk chunk, int channel)
        {
            var baseX = chunk.Position.X << 4;
            var baseZ = chunk.Position.Z << 4;
            foreach (var direction in Directions.All)
            {
                if (direction.GetAxis() == Axis.Y) continue;
                var neighbourPosition = new ChunkPos(chunk.Position.X + direction.X(), chunk.Position.Z + direction.Z());
                if (regionSet.Contains(neighbourPosition)) continue;
                var neighbour = world.GetChunk(neighbourPosition);
                if (neighbour == null) continue;

                for (var i = 0; i < 16; i++)
                {
                    int x, z;
                    switch (direction)
                    {
                        case Direction.North:
                            x = baseX + i;
                            z = baseZ - 1;
                            break;
                        case Direction.South:
                            x = baseX + i;
                            z = baseZ + 16;
                            break;
                        case Direction.West:
                            x = baseX - 1;
                            z = baseZ + i;
                            break;
                        default:
                            x = baseX + 16;
                            z = baseZ + i;
                            break;
                    }
                    // sky: cells above both heightmaps are 15 on both sides, nothing to propagate there
                    var inside = chunk.GetHeight((x - direction.X()) & 0x0F, (z - direction.Z()) & 0x0F);
                    var top = channel == Sky ? Math.Max(neighbour.GetHeight(x & 0x0F, z & 0x0F), inside) : world.MaxY + 1;
                    for (var y = world.MinY; y < top; y++)
                    {
                        if (Get(neighbour, x, y, z, channel) > 1) queue.Enqueue(new BlockPos(x, y, z));
                    }
                }
            }
        }

        private int Get(int x, int y, int z, int channel)
        {
            var chunk = world.GetChunk(x >> 4, z >> 4);
            return chunk == null ? 0 : Get(chunk, x, y, z, channel);
        }

        private static int Get(Chunk chunk, int x, int y, int z, int channel)
        {
            var packed = chunk.GetLight(x & 0x0F, y, z & 0x0F);
            return channel == Sky ? packed >> 4 : packed & 0x0F;
        }

        private void Set(Chunk chunk, int x, int y, int z, int channel, int level)
        {
            var section = chunk.GetOrCreateSection(y >> 4);
            if (section == null) return;
            if (section.Light == null) section.Light = new byte[SectionIndex.Volume];
            var index = SectionIndex.Of(x & 0x0F, y & 0x0F, z & 0x0F);
            var packed = section.Light[index];
            section.Light[index] = channel == Sky ? (byte)((packed & 0x0F) | (level << 4)) : (byte)((packed & 0xF0) | level);
            written.Add(new SectionPos(chunk.Position.X, y >> 4, chunk.Position.Z));
        }
    }
}
