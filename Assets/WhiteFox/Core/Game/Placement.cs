// WhiteFox - Unity port of Minosoft (https://gitlab.bixilon.de/bixilon/minosoft), Copyright (C) 2020-2023 Moritz Zwerger
// SPDX-License-Identifier: GPL-3.0-or-later

using System;
using WhiteFox.Core.Data;
using WhiteFox.Core.Util;

namespace WhiteFox.Core.Game
{
    /// <summary>
    /// Chooses the state of a placed block (a reduced version of vanilla's getStateForPlacement; Minosoft does this per
    /// block class in PlaceableItem). Handles axis, horizontal facing, slab type and stair/trapdoor half.
    /// </summary>
    public static class Placement
    {
        public static bool IsReplaceable(BlockState state)
        {
            if (state == null) return true;
            if (state.Block.IsFluid) return true;
            switch (state.Block.Name.Path)
            {
                case "short_grass":
                case "tall_grass":
                case "fern":
                case "large_fern":
                case "dead_bush":
                case "vine":
                case "fire":
                case "structure_void":
                    return true;
                case "snow":
                    return state["layers"] == "1";
                default:
                    return false;
            }
        }

        public static BlockState GetPlacementState(Block block, BlockHit hit, float yaw, float pitch)
        {
            var state = block.DefaultState;
            var horizontal = HorizontalFacing(yaw);
            var face = hit.Face;
            var localY = hit.Point.Y - hit.Position.Y;
            var upperHalf = face == Direction.Down || (face != Direction.Up && localY > 0.5);
            var path = block.Name.Path;

            if (state["axis"] != null) state = state.With("axis", face.GetAxis().ToString().ToLowerInvariant());

            var facing = state["facing"];
            if (facing != null)
            {
                var facingProperty = block.Properties[block.PropertyIndex("facing")];
                var supportsVertical = facingProperty.IndexOf("up") >= 0;
                Direction value;
                if (path.EndsWith("_stairs")) value = horizontal;
                else if (path.EndsWith("_trapdoor") || path == "ladder" || path.EndsWith("wall_torch")) value = face.GetAxis() == Axis.Y ? horizontal.Opposite() : face;
                else if (supportsVertical) value = Nearest(yaw, pitch).Opposite();
                else value = horizontal.Opposite();
                state = state.With("facing", value.ToString().ToLowerInvariant());
            }

            if (path.EndsWith("_slab")) state = state.With("type", upperHalf ? "top" : "bottom");
            if (state["half"] != null && (path.EndsWith("_stairs") || path.EndsWith("_trapdoor"))) state = state.With("half", upperHalf ? "top" : "bottom");
            return state;
        }

        /// <summary>Direction the player looks at, horizontally (vanilla Direction.fromYRot).</summary>
        public static Direction HorizontalFacing(float yaw)
        {
            var index = (int)Math.Floor(MathUtil.WrapDegrees(yaw) / 90.0 + 0.5) & 3;
            switch (index)
            {
                case 0: return Direction.South;
                case 1: return Direction.West;
                case 2: return Direction.North;
                default: return Direction.East;
            }
        }

        private static Direction Nearest(float yaw, float pitch)
        {
            if (pitch < -45) return Direction.Up;
            if (pitch > 45) return Direction.Down;
            return HorizontalFacing(yaw);
        }
    }
}
