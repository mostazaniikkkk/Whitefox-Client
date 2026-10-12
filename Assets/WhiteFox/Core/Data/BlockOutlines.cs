// WhiteFox - Unity port of Minosoft (https://gitlab.bixilon.de/bixilon/minosoft), Copyright (C) 2020-2023 Moritz Zwerger
// SPDX-License-Identifier: GPL-3.0-or-later

using WhiteFox.Core.Util;

namespace WhiteFox.Core.Data
{
    /// <summary>
    /// Vanilla getShape of blocks without collision whose model is wider than the block looks (the torch and plant
    /// models are crossed planes over the whole block, mostly transparent): the outline and the part a click hits.
    /// Other blocks without collision use the bounds of their model.
    /// </summary>
    public static class BlockOutlines
    {
        private static AABB Box(double x1, double y1, double z1, double x2, double y2, double z2) =>
            new AABB(x1 / 16, y1 / 16, z1 / 16, x2 / 16, y2 / 16, z2 / 16);

        private static readonly AABB[] Torch = { Box(6, 0, 6, 10, 10, 10) };
        private static readonly AABB[] Flower = { Box(5, 0, 5, 11, 10, 11) };
        private static readonly AABB[] Sapling = { Box(2, 0, 2, 14, 12, 14) };
        private static readonly AABB[] Grass = { Box(2, 0, 2, 14, 13, 14) };
        private static readonly AABB[] Mushroom = { Box(5, 0, 5, 11, 6, 11) };
        private static readonly AABB[] Cane = { Box(2, 0, 2, 14, 16, 14) };
        private static readonly AABB[] Rail = { Box(0, 0, 0, 16, 2, 16) };
        private static readonly AABB[] AscendingRail = { Box(0, 0, 0, 16, 8, 16) };

        private static readonly System.Collections.Generic.HashSet<string> Flowers = new System.Collections.Generic.HashSet<string>
        {
            "dandelion", "poppy", "blue_orchid", "allium", "azure_bluet", "red_tulip", "orange_tulip", "white_tulip", "pink_tulip",
            "oxeye_daisy", "cornflower", "lily_of_the_valley", "wither_rose", "torchflower",
        };

        /// <summary>The vanilla outline of the state, or null when the model bounds are right.</summary>
        public static AABB[] Get(BlockState state)
        {
            var path = state.Block.Name.Path;
            switch (path)
            {
                case "torch":
                case "soul_torch":
                case "redstone_torch":
                    return Torch;
                case "wall_torch":
                case "soul_wall_torch":
                case "redstone_wall_torch":
                    switch (state["facing"])
                    {
                        case "north": return new[] { Box(5.5, 3, 11, 10.5, 13, 16) };
                        case "south": return new[] { Box(5.5, 3, 0, 10.5, 13, 5) };
                        case "west": return new[] { Box(11, 3, 5.5, 16, 13, 10.5) };
                        default: return new[] { Box(0, 3, 5.5, 5, 13, 10.5) };
                    }
                case "short_grass":
                case "fern":
                case "dead_bush":
                    return Grass;
                case "brown_mushroom":
                case "red_mushroom":
                    return Mushroom;
                case "sugar_cane":
                    return Cane;
                case "wheat":
                case "carrots":
                case "potatoes":
                    return new[] { Box(0, 0, 0, 16, 2 * (Age(state) + 1), 16) };
                case "beetroots":
                    return new[] { Box(0, 0, 0, 16, 2 * (Age(state) + 1), 16) };
            }
            if (Flowers.Contains(path)) return Flower;
            if (path.EndsWith("_sapling") || path == "mangrove_propagule" && state["hanging"] != "true") return Sapling;
            if (path == "rail" || path.EndsWith("_rail")) return (state["shape"] ?? "").StartsWith("ascending") ? AscendingRail : Rail;
            return null;
        }

        private static int Age(BlockState state) => int.TryParse(state["age"], out var age) ? age : 0;
    }
}
