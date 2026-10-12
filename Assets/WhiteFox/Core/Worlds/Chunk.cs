// WhiteFox - Unity port of Minosoft (https://gitlab.bixilon.de/bixilon/minosoft), Copyright (C) 2020-2023 Moritz Zwerger
// SPDX-License-Identifier: GPL-3.0-or-later

using System;
using WhiteFox.Core.Data;
using WhiteFox.Core.Util;

namespace WhiteFox.Core.Worlds
{
    /// <summary>
    /// 16x16x16 blocks. Like Minosoft, blocks are stored unpaletted (palettes only exist on the wire) and air is null.
    /// Light is packed per block: high nibble sky light, low nibble block light. Arrays are allocated lazily.
    /// </summary>
    public sealed class ChunkSection
    {
        public BlockState[] Blocks;
        public byte[] Light;

        public int NonAirCount;
        public int LuminantCount;

        /// <summary>Incremented on every block change; lets the renderer drop stale meshes.</summary>
        public int Version;

        public BlockState Get(int index) => Blocks?[index];

        public BlockState Set(int index, BlockState state)
        {
            if (Blocks == null)
            {
                if (state == null) return null;
                Blocks = new BlockState[SectionIndex.Volume];
            }
            var previous = Blocks[index];
            if (previous == state) return previous;
            Blocks[index] = state;

            if (previous == null) NonAirCount++;
            if (state == null) NonAirCount--;
            if (previous != null && previous.Luminance > 0) LuminantCount--;
            if (state != null && state.Luminance > 0) LuminantCount++;
            if (NonAirCount == 0) Blocks = null;
            Version++;
            return previous;
        }

        public bool IsEmpty => NonAirCount == 0;
    }

    public sealed class Chunk
    {
        public readonly ChunkPos Position;
        public readonly World World;
        public readonly ChunkSection[] Sections;

        /// <summary>Per column: the lowest y from which upwards every block lets sky light pass unchanged (opacity 0).</summary>
        public readonly int[] Heightmap = new int[256];

        /// <summary>
        /// Per column: one above the highest block that stops rain (vanilla MOTION_BLOCKING: a collision shape or a
        /// fluid), so slabs, glass or leaves keep the rain out although sky light passes them.
        /// </summary>
        public readonly int[] RainHeightmap = new int[256];

        /// <summary>Biome ids per quart (4x4x4): section index * 64 + (y &lt;&lt; 4 | z &lt;&lt; 2 | x), or null (local world).</summary>
        public ushort[] Biomes;

        public Chunk(World world, ChunkPos position)
        {
            World = world;
            Position = position;
            Sections = new ChunkSection[world.SectionCount];
            for (var i = 0; i < Heightmap.Length; i++) Heightmap[i] = RainHeightmap[i] = world.MinY;
        }

        public ChunkSection GetSection(int sectionY)
        {
            var index = sectionY - World.MinSection;
            if (index < 0 || index >= Sections.Length) return null;
            return Sections[index];
        }

        public ChunkSection GetOrCreateSection(int sectionY)
        {
            var index = sectionY - World.MinSection;
            if (index < 0 || index >= Sections.Length) return null;
            return Sections[index] ?? (Sections[index] = new ChunkSection());
        }

        /// <summary>x and z are inside the chunk (0..15), y is a world y.</summary>
        public BlockState Get(int x, int y, int z)
        {
            var section = GetSection(y >> 4);
            return section?.Get(SectionIndex.Of(x, y & 0x0F, z));
        }

        /// <summary>Raw set without light/heightmap updates (used by generators before the chunk is added).</summary>
        public BlockState SetRaw(int x, int y, int z, BlockState state)
        {
            var section = state == null ? GetSection(y >> 4) : GetOrCreateSection(y >> 4);
            return section?.Set(SectionIndex.Of(x, y & 0x0F, z), state);
        }

        public int GetHeight(int x, int z) => Heightmap[(z << 4) | x];

        /// <summary>The lowest y rain falls down to in this column (see <see cref="RainHeightmap"/>).</summary>
        public int GetRainHeight(int x, int z) => RainHeightmap[(z << 4) | x];

        public void RecalculateHeightmap()
        {
            for (var z = 0; z < 16; z++)
            {
                for (var x = 0; x < 16; x++)
                {
                    RecalculateHeightmap(x, z);
                }
            }
        }

        /// <summary>Returns true if the height of the column changed.</summary>
        public bool RecalculateHeightmap(int x, int z)
        {
            var height = World.MinY;
            var rain = World.MinY;
            for (var s = Sections.Length - 1; s >= 0 && height == World.MinY; s--)
            {
                var section = Sections[s];
                if (section == null || section.IsEmpty) continue;
                var baseY = (s + World.MinSection) << 4;
                for (var y = 15; y >= 0; y--)
                {
                    var state = section.Get(SectionIndex.Of(x, y, z));
                    if (state == null) continue;
                    if (rain == World.MinY && (state.HasCollision || state.Block.IsFluid || state["waterlogged"] == "true")) rain = baseY + y + 1;
                    if (state.Opacity > 0)
                    {
                        height = baseY + y + 1;
                        break;
                    }
                }
            }
            var index = (z << 4) | x;
            RainHeightmap[index] = Math.Max(rain, height);
            if (Heightmap[index] == height) return false;
            Heightmap[index] = height;
            return true;
        }

        /// <summary>Packed light (sky &lt;&lt; 4 | block). Sky light is implicitly 15 above the heightmap.</summary>
        public int GetLight(int x, int y, int z)
        {
            var section = GetSection(y >> 4);
            int packed = section?.Light?[SectionIndex.Of(x, y & 0x0F, z)] ?? 0;
            if (y >= Heightmap[(z << 4) | x]) packed = (packed & 0x0F) | 0xF0;
            return packed;
        }
    }
}
