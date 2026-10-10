// WhiteFox - Unity port of Minosoft (https://gitlab.bixilon.de/bixilon/minosoft), Copyright (C) 2020-2023 Moritz Zwerger
// SPDX-License-Identifier: GPL-3.0-or-later

using System;
using System.Collections.Generic;
using WhiteFox.Core.Data;
using WhiteFox.Core.Util;
using WhiteFox.Core.Worlds;

namespace WhiteFox.Core.Network
{
    /// <summary>Decoded "Chunk Data and Update Light" packet; turned into a <see cref="Chunk"/> on the game thread.</summary>
    public sealed class ChunkPacketData
    {
        public ChunkPos Position;
        /// <summary>One array per section (bottom to top), null for empty sections.</summary>
        public BlockState[][] Sections;
        /// <summary>Biome id per quart, 64 per section (see <see cref="Chunk.Biomes"/>).</summary>
        public ushort[] Biomes;
        /// <summary>Block entities of the chunk: position, type id, NBT.</summary>
        public readonly List<(BlockPos Position, int Type, object Nbt)> BlockEntities = new List<(BlockPos, int, object)>();
    }

    /// <summary>
    /// Chunk section decoding (Minosoft's ChunkPacketUtil.readPaletteChunk + PalettedContainerReader):
    /// per section a block count, a block state container (16³) and a biome container (4³).
    /// Like in Minosoft, the palettes only exist on the wire; sections are expanded into plain arrays.
    /// </summary>
    public static class ChunkDecoder
    {
        public static ChunkPacketData Read(InBuffer buffer, BlockRegistry registry, int sectionCount, int biomeBits)
        {
            var x = buffer.ReadInt();
            var z = buffer.ReadInt();
            buffer.ReadNbt(); // heightmaps: calculated on the client
            var data = buffer.ReadByteArray();
            // block entities and light follow; light is calculated client side (Minosoft ignores server light too)

            var result = new ChunkPacketData { Position = new ChunkPos(x, z), Sections = new BlockState[sectionCount][], Biomes = new ushort[sectionCount * 64] };
            var sections = new InBuffer(data);
            for (var i = 0; i < sectionCount && sections.Remaining > 0; i++)
            {
                var blockCount = sections.ReadShort();
                var blocks = ReadBlocks(sections, registry);
                ReadBiomes(sections, biomeBits, result.Biomes, i * 64);
                result.Sections[i] = blockCount == 0 ? null : blocks;
            }

            var count = buffer.ReadVarInt();
            for (var i = 0; i < count; i++)
            {
                var packed = buffer.ReadByte();
                var y = buffer.ReadShort();
                var type = buffer.ReadVarInt();
                var nbt = buffer.ReadNbt();
                result.BlockEntities.Add((new BlockPos((x << 4) + (packed >> 4), y, (z << 4) + (packed & 0x0F)), type, nbt));
            }
            return result;
        }

        /// <summary>Paletted container of 4096 block states (null = air).</summary>
        public static BlockState[] ReadBlocks(InBuffer buffer, BlockRegistry registry)
        {
            var bits = buffer.ReadByte();
            if (bits == 0)
            {
                // single valued
                var state = registry.GetState(buffer.ReadVarInt());
                var length = buffer.ReadVarInt();
                buffer.Skip(length * 8);
                if (state == null) return null;
                var filled = new BlockState[SectionIndex.Volume];
                for (var i = 0; i < filled.Length; i++) filled[i] = state;
                return filled;
            }

            BlockState[] palette = null;
            if (bits <= 8)
            {
                bits = Math.Max(bits, (byte)4);
                palette = new BlockState[buffer.ReadVarInt()];
                for (var i = 0; i < palette.Length; i++) palette[i] = registry.GetState(buffer.ReadVarInt());
            }
            var longs = buffer.ReadLongArray(buffer.ReadVarInt());

            var result = new BlockState[SectionIndex.Volume];
            var valuesPerLong = 64 / bits;
            var mask = (1L << bits) - 1;
            var expected = (SectionIndex.Volume + valuesPerLong - 1) / valuesPerLong;
            if (longs.Length != expected) throw new FormatException($"Invalid block data length {longs.Length}, expected {expected} for {bits} bits");
            for (var index = 0; index < SectionIndex.Volume; index++)
            {
                // since 1.16 values never span two longs
                var value = (int)((longs[index / valuesPerLong] >> (index % valuesPerLong * bits)) & mask);
                if (palette != null) result[index] = value < palette.Length ? palette[value] : null;
                else result[index] = registry.GetState(value);
            }
            return result;
        }

        /// <summary>Paletted container of the 64 biomes of a section (single value, palette up to 3 bits, or direct).</summary>
        public static void ReadBiomes(InBuffer buffer, int directBits, ushort[] target, int offset)
        {
            var bits = buffer.ReadByte();
            int[] palette = null;
            if (bits == 0)
            {
                var value = (ushort)buffer.ReadVarInt();
                buffer.Skip(buffer.ReadVarInt() * 8);
                for (var i = 0; i < 64; i++) target[offset + i] = value;
                return;
            }
            if (bits <= 3)
            {
                palette = new int[buffer.ReadVarInt()];
                for (var i = 0; i < palette.Length; i++) palette[i] = buffer.ReadVarInt();
            }
            else
            {
                bits = (byte)directBits;
            }
            var longs = buffer.ReadLongArray(buffer.ReadVarInt());
            var valuesPerLong = 64 / bits;
            var mask = (1L << bits) - 1;
            for (var i = 0; i < 64; i++)
            {
                var word = i / valuesPerLong;
                if (word >= longs.Length) break;
                var value = (int)((longs[word] >> (i % valuesPerLong * bits)) & mask);
                target[offset + i] = (ushort)(palette != null ? (value < palette.Length ? palette[value] : 0) : value);
            }
        }

        /// <summary>Creates the world chunk (game thread).</summary>
        public static Chunk ToChunk(ChunkPacketData data, World world)
        {
            var chunk = new Chunk(world, data.Position) { Biomes = data.Biomes };
            var count = Math.Min(data.Sections.Length, chunk.Sections.Length);
            for (var i = 0; i < count; i++)
            {
                var blocks = data.Sections[i];
                if (blocks == null) continue;
                var section = chunk.Sections[i] = new ChunkSection();
                for (var index = 0; index < blocks.Length; index++)
                {
                    if (blocks[index] != null) section.Set(index, blocks[index]);
                }
                if (section.IsEmpty) chunk.Sections[i] = null;
            }
            return chunk;
        }
    }
}
