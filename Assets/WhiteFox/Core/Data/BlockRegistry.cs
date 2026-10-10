// WhiteFox - Unity port of Minosoft (https://gitlab.bixilon.de/bixilon/minosoft), Copyright (C) 2020-2023 Moritz Zwerger
// SPDX-License-Identifier: GPL-3.0-or-later

using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using WhiteFox.Core.Util;

namespace WhiteFox.Core.Data
{
    /// <summary>
    /// Block and block state registry. Minosoft fills it from PixLyzer data; this port reads minecraft-data's
    /// blocks.json + blockCollisionShapes.json, which contain the same global state ids for 1.20.4.
    /// </summary>
    public sealed class BlockRegistry
    {
        private readonly Dictionary<ResourceLocation, Block> byName = new Dictionary<ResourceLocation, Block>();
        private BlockState[] states = Array.Empty<BlockState>();
        private readonly List<Block> blocks = new List<Block>();

        public IReadOnlyList<Block> Blocks => blocks;
        public int StateCount => states.Length;

        /// <summary>Returns the state or null for air/unknown ids (air is null, like in Minosoft).</summary>
        public BlockState GetState(int id)
        {
            if (id < 0 || id >= states.Length) return null;
            var state = states[id];
            return state == null || state.Block.IsAir ? null : state;
        }

        public Block Get(ResourceLocation name) => byName.TryGetValue(name, out var block) ? block : null;
        public Block Get(string name) => Get(ResourceLocation.Of(name));

        public IEnumerable<BlockState> AllStates()
        {
            foreach (var state in states)
            {
                if (state != null) yield return state;
            }
        }

        /// <summary>vanilla state id order: the first property is the most significant "digit".</summary>
        public static int ComputeStateIndex(BlockProperty[] properties, int[] valueIndices)
        {
            var index = 0;
            for (var i = 0; i < properties.Length; i++)
            {
                index = index * properties[i].Values.Length + valueIndices[i];
            }
            return index;
        }

        public static BlockRegistry Load(string blocksJson, string shapesJson)
        {
            var registry = new BlockRegistry();
            var blocks = JArray.Parse(blocksJson);
            var maxState = 0;
            foreach (var entry in blocks) maxState = Math.Max(maxState, (int)entry["maxStateId"]);
            registry.states = new BlockState[maxState + 1];

            var shapes = JObject.Parse(shapesJson);
            var shapeTable = new Dictionary<int, AABB[]>();
            foreach (var shape in (JObject)shapes["shapes"])
            {
                var boxes = new List<AABB>();
                foreach (var box in (JArray)shape.Value)
                {
                    boxes.Add(new AABB((double)box[0], (double)box[1], (double)box[2], (double)box[3], (double)box[4], (double)box[5]));
                }
                shapeTable[int.Parse(shape.Key)] = boxes.ToArray();
            }
            var blockShapes = (JObject)shapes["blocks"];

            foreach (var entry in blocks)
            {
                var name = ResourceLocation.Minecraft((string)entry["name"]);
                var properties = new List<BlockProperty>();
                if (entry["states"] is JArray propertyList)
                {
                    foreach (var property in propertyList)
                    {
                        properties.Add(new BlockProperty((string)property["name"], ReadValues(property)));
                    }
                }

                var block = new Block((int)entry["id"], name, properties.ToArray(), (int)entry["minStateId"], (int)entry["defaultState"])
                {
                    Hardness = entry["hardness"]?.Type == JTokenType.Null ? -1 : (float?)entry["hardness"] ?? -1,
                    Transparent = (bool?)entry["transparent"] ?? false,
                    EmitLight = (int?)entry["emitLight"] ?? 0,
                    FilterLight = (int?)entry["filterLight"] ?? 15,
                };
                var path = name.Path;
                block.IsAir = path == "air" || path == "cave_air" || path == "void_air";
                block.IsFluid = path == "water" || path == "lava";
                block.RequiresTool = entry["harvestTools"] != null;
                block.Material = (string)entry["material"];
                if (entry["harvestTools"] is JObject tools)
                {
                    block.HarvestTools = new HashSet<int>();
                    foreach (var tool in tools) block.HarvestTools.Add(int.Parse(tool.Key));
                }
                ApplyHardcodedProperties(block);

                var count = (int)entry["maxStateId"] - block.MinStateId + 1;
                block.States = new BlockState[count];
                var shapeEntry = blockShapes[path];
                for (var index = 0; index < count; index++)
                {
                    var valueIndices = DecodeStateIndex(block.Properties, index);
                    var state = new BlockState(block.MinStateId + index, block, valueIndices);

                    var shapeId = shapeEntry == null ? 0 : shapeEntry.Type == JTokenType.Array ? (int)shapeEntry[index] : (int)shapeEntry;
                    state.CollisionShape = shapeTable.TryGetValue(shapeId, out var shape) ? shape : Array.Empty<AABB>();
                    state.OutlineShape = state.CollisionShape;
                    state.Luminance = block.EmitLight;
                    if (state["lit"] == "false") state.Luminance = 0;
                    state.Opacity = block.FilterLight;

                    block.States[index] = state;
                    registry.states[state.Id] = state;
                }

                registry.blocks.Add(block);
                registry.byName[name] = block;
            }
            return registry;
        }

        private static string[] ReadValues(JToken property)
        {
            if (property["values"] is JArray values)
            {
                var result = new string[values.Count];
                for (var i = 0; i < values.Count; i++) result[i] = (string)values[i];
                return result;
            }
            if ((string)property["type"] == "bool") return new[] { "true", "false" };
            var count = (int)property["num_values"];
            var range = new string[count];
            for (var i = 0; i < count; i++) range[i] = i.ToString();
            return range;
        }

        private static int[] DecodeStateIndex(BlockProperty[] properties, int index)
        {
            var result = new int[properties.Length];
            for (var i = properties.Length - 1; i >= 0; i--)
            {
                var size = properties[i].Values.Length;
                result[i] = index % size;
                index /= size;
            }
            return result;
        }

        private static readonly HashSet<string> GrassTinted = new HashSet<string> { "grass_block", "short_grass", "tall_grass", "fern", "large_fern", "potted_fern", "sugar_cane" };
        private static readonly HashSet<string> FoliageTinted = new HashSet<string> { "oak_leaves", "jungle_leaves", "acacia_leaves", "dark_oak_leaves", "mangrove_leaves", "vine" };

        private static readonly Dictionary<string, int> ConstantTints = new Dictionary<string, int>
        {
            { "spruce_leaves", 0x619961 },
            { "birch_leaves", 0x80A755 },
            { "lily_pad", 0x208030 },
            { "attached_melon_stem", 0xE0C71C },
            { "attached_pumpkin_stem", 0xE0C71C },
            { "melon_stem", 0x00FF00 },
            { "pumpkin_stem", 0x00FF00 },
            { "redstone_wire", 0xFF0000 },
        };

        private static void ApplyHardcodedProperties(Block block)
        {
            var path = block.Name.Path;
            switch (path)
            {
                case "ice":
                case "packed_ice":
                case "frosted_ice":
                    block.Friction = 0.98f;
                    break;
                case "blue_ice":
                    block.Friction = 0.989f;
                    break;
                case "slime_block":
                    block.Friction = 0.8f;
                    break;
                case "soul_sand":
                    block.SpeedFactor = 0.4f;
                    break;
                case "honey_block":
                    block.SpeedFactor = 0.4f;
                    block.JumpFactor = 0.5f;
                    break;
            }

            if (GrassTinted.Contains(path)) block.Tint = TintType.Grass;
            else if (FoliageTinted.Contains(path)) block.Tint = TintType.Foliage;
            else if (path == "water" || path == "bubble_column" || path == "water_cauldron") block.Tint = TintType.Water;
            else if (ConstantTints.TryGetValue(path, out var color))
            {
                block.Tint = TintType.Constant;
                block.ConstantTint = color;
            }
        }
    }
}
