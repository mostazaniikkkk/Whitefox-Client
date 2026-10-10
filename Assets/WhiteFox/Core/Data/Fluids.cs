// WhiteFox - Unity port of Minosoft (https://gitlab.bixilon.de/bixilon/minosoft), Copyright (C) 2020-2023 Moritz Zwerger
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Collections.Generic;

namespace WhiteFox.Core.Data
{
    public enum FluidType
    {
        None,
        Water,
        Lava,
    }

    /// <summary>Fluid state of blocks (Minosoft's Fluid / FluidHolder / FluidFilled): water, lava, waterlogged blocks.</summary>
    public static class Fluids
    {
        private static readonly HashSet<string> AlwaysWater = new HashSet<string> { "water", "kelp", "kelp_plant", "seagrass", "tall_seagrass", "bubble_column" };

        private static readonly HashSet<string> Climbable = new HashSet<string>
        {
            "ladder", "vine", "scaffolding", "weeping_vines", "weeping_vines_plant", "twisting_vines", "twisting_vines_plant",
            "cave_vines", "cave_vines_plant",
        };

        public static FluidType Get(BlockState state)
        {
            if (state == null) return FluidType.None;
            var path = state.Block.Name.Path;
            if (path == "lava") return FluidType.Lava;
            if (AlwaysWater.Contains(path) || state["waterlogged"] == "true") return FluidType.Water;
            return FluidType.None;
        }

        /// <summary>Height of the fluid surface inside the block (amount / 9; sources and falling fluid 8/9).</summary>
        public static float Height(BlockState state)
        {
            if (Get(state) == FluidType.None) return 0;
            var level = state.Block.IsFluid ? int.Parse(state["level"] ?? "0") : 0;
            if (level == 0 || level >= 8) return 8.0f / 9.0f;
            return (8 - level) / 9.0f;
        }

        /// <summary>Ladders, vines, scaffolding (vanilla #climbable tag).</summary>
        public static bool IsClimbable(BlockState state) => state != null && Climbable.Contains(state.Block.Name.Path);
    }
}
