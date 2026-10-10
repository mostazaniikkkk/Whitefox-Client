// WhiteFox - Unity port of Minosoft (https://gitlab.bixilon.de/bixilon/minosoft), Copyright (C) 2020-2023 Moritz Zwerger
// SPDX-License-Identifier: GPL-3.0-or-later

using System;
using System.Collections.Generic;
using WhiteFox.Core.Data;
using WhiteFox.Core.Util;
using WhiteFox.Core.Worlds;

namespace WhiteFox.Core.Local
{
    public interface IChunkGenerator
    {
        void Generate(Chunk chunk);
    }

    /// <summary>Superflat generator (Minosoft's FlatGenerator); default layers match vanilla's "Classic Flat".</summary>
    public sealed class FlatGenerator : IChunkGenerator
    {
        private readonly BlockState[] layers;
        private readonly int minY;

        public FlatGenerator(int minY, params BlockState[] layers)
        {
            this.minY = minY;
            this.layers = layers;
        }

        public static FlatGenerator Default(BlockRegistry registry, int minY)
        {
            var bedrock = registry.Get("bedrock").DefaultState;
            var dirt = registry.Get("dirt").DefaultState;
            var grass = registry.Get("grass_block").DefaultState;
            return new FlatGenerator(minY, bedrock, dirt, dirt, grass);
        }

        public int SurfaceY => minY + layers.Length;

        public void Generate(Chunk chunk)
        {
            for (var i = 0; i < layers.Length; i++)
            {
                var y = minY + i;
                for (var z = 0; z < 16; z++)
                {
                    for (var x = 0; x < 16; x++)
                    {
                        chunk.SetRaw(x, y, z, layers[i]);
                    }
                }
            }
        }
    }

    /// <summary>
    /// Places a row of test blocks next to the spawn so models, culling, transparency, tint and light can be checked
    /// visually without a server.
    /// </summary>
    public sealed class ShowcaseDecorator : IChunkGenerator
    {
        private readonly IChunkGenerator parent;
        private readonly List<KeyValuePair<BlockPos, BlockState>> blocks = new List<KeyValuePair<BlockPos, BlockState>>();

        public ShowcaseDecorator(IChunkGenerator parent, BlockRegistry registry, int surfaceY)
        {
            this.parent = parent;
            string[] names =
            {
                "stone", "oak_planks", "oak_log", "glass", "oak_leaves", "birch_leaves", "oak_stairs", "stone_brick_slab",
                "oak_fence", "cobblestone_wall", "torch", "glowstone", "short_grass", "poppy", "dandelion", "sugar_cane",
                "white_stained_glass", "ice", "crafting_table", "furnace", "bookshelf", "oak_door", "ladder", "lantern",
                "redstone_lamp", "sea_lantern", "magma_block", "oak_trapdoor", "anvil", "hay_block",
            };
            var x = 3;
            foreach (var name in names)
            {
                var block = registry.Get(name);
                if (block == null) continue;
                blocks.Add(new KeyValuePair<BlockPos, BlockState>(new BlockPos(x, surfaceY, 4), block.DefaultState));
                x += 2;
            }
            var glass = registry.Get("glass")?.DefaultState;
            var planks = registry.Get("oak_planks")?.DefaultState;
            var torch = registry.Get("torch")?.DefaultState;
            // small dark hut to check block light
            for (var dx = -3; dx <= 3; dx++)
            {
                for (var dz = -3; dz <= 3; dz++)
                {
                    for (var dy = 0; dy <= 4; dy++)
                    {
                        var wall = Math.Abs(dx) == 3 || Math.Abs(dz) == 3 || dy == 4;
                        if (!wall) continue;
                        if (dy == 1 && dz == -3 && dx == 0) continue; // entrance
                        if (dy == 0 && dz == -3 && dx == 0) continue;
                        var state = dy == 2 && Math.Abs(dx) == 3 && dz == 0 ? glass : planks;
                        blocks.Add(new KeyValuePair<BlockPos, BlockState>(new BlockPos(dx, surfaceY + dy, 14 + dz), state));
                    }
                }
            }
            blocks.Add(new KeyValuePair<BlockPos, BlockState>(new BlockPos(1, surfaceY, 16), torch));

            // fluids: a water pool and a lava pool sunk into the ground, and a flowing water stream on top of it
            var water = registry.Get("water")?.DefaultState;
            var lava = registry.Get("lava")?.DefaultState;
            var sand = registry.Get("sand")?.DefaultState;
            for (var dx = 0; dx < 6; dx++)
            {
                for (var dz = 0; dz < 6; dz++)
                {
                    blocks.Add(new KeyValuePair<BlockPos, BlockState>(new BlockPos(-12 + dx, surfaceY - 1, dz), water));
                    blocks.Add(new KeyValuePair<BlockPos, BlockState>(new BlockPos(-12 + dx, surfaceY - 2, dz), sand));
                }
            }
            for (var dx = 0; dx < 3; dx++)
            {
                for (var dz = 0; dz < 3; dz++)
                {
                    blocks.Add(new KeyValuePair<BlockPos, BlockState>(new BlockPos(-10 + dx, surfaceY - 1, 9 + dz), lava));
                }
            }
            if (water != null)
            {
                blocks.Add(new KeyValuePair<BlockPos, BlockState>(new BlockPos(-15, surfaceY, 0), water));
                for (var level = 1; level <= 7; level++)
                {
                    blocks.Add(new KeyValuePair<BlockPos, BlockState>(new BlockPos(-15, surfaceY, -level), water.With("level", level.ToString())));
                }
            }
        }

        public void Generate(Chunk chunk)
        {
            parent.Generate(chunk);
            foreach (var pair in blocks)
            {
                if (pair.Value == null) continue;
                if (pair.Key.ChunkPos != chunk.Position) continue;
                chunk.SetRaw(pair.Key.X & 0x0F, pair.Key.Y, pair.Key.Z & 0x0F, pair.Value);
            }
        }
    }

    /// <summary>Loads chunks around the player and unloads far ones (Minosoft's LocalChunkManager).</summary>
    public sealed class LocalChunkManager
    {
        private readonly World world;
        private readonly IChunkGenerator generator;
        public int ViewDistance;

        /// <summary>Upper bound of chunks generated per call, to keep frame times stable.</summary>
        public int ChunksPerTick = 6;

        public LocalChunkManager(World world, IChunkGenerator generator, int viewDistance)
        {
            this.world = world;
            this.generator = generator;
            ViewDistance = viewDistance;
        }

        public void Update(ChunkPos center)
        {
            var radius = ViewDistance;
            var unload = new List<ChunkPos>();
            foreach (var chunk in world.Chunks)
            {
                var dx = chunk.Position.X - center.X;
                var dz = chunk.Position.Z - center.Z;
                if (Math.Max(Math.Abs(dx), Math.Abs(dz)) > radius + 1) unload.Add(chunk.Position);
            }
            foreach (var position in unload) world.RemoveChunk(position);

            var generated = 0;
            for (var distance = 0; distance <= radius && generated < ChunksPerTick; distance++)
            {
                for (var dx = -distance; dx <= distance && generated < ChunksPerTick; dx++)
                {
                    for (var dz = -distance; dz <= distance && generated < ChunksPerTick; dz++)
                    {
                        if (Math.Max(Math.Abs(dx), Math.Abs(dz)) != distance) continue;
                        var position = new ChunkPos(center.X + dx, center.Z + dz);
                        if (world.IsLoaded(position)) continue;
                        var chunk = new Chunk(world, position);
                        generator.Generate(chunk);
                        world.AddChunk(chunk);
                        generated++;
                    }
                }
            }
        }
    }
}
