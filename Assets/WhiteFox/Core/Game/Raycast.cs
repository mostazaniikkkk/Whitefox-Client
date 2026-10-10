// WhiteFox - Unity port of Minosoft (https://gitlab.bixilon.de/bixilon/minosoft), Copyright (C) 2020-2023 Moritz Zwerger
// SPDX-License-Identifier: GPL-3.0-or-later

using System;
using WhiteFox.Core.Data;
using WhiteFox.Core.Util;
using WhiteFox.Core.Worlds;

namespace WhiteFox.Core.Game
{
    public struct BlockHit
    {
        public BlockPos Position;
        public Direction Face;
        public BlockState State;
        public Vec3d Point;
        public double Distance;
    }

    /// <summary>Block targeting (Minosoft's TargetHandler): voxel stepping plus a ray test against each outline shape.</summary>
    public static class Raycast
    {
        public static BlockHit? Blocks(World world, Vec3d origin, Vec3d direction, double maxDistance)
        {
            var x = (int)Math.Floor(origin.X);
            var y = (int)Math.Floor(origin.Y);
            var z = (int)Math.Floor(origin.Z);
            int stepX = Math.Sign(direction.X), stepY = Math.Sign(direction.Y), stepZ = Math.Sign(direction.Z);
            var deltaX = stepX == 0 ? double.PositiveInfinity : Math.Abs(1.0 / direction.X);
            var deltaY = stepY == 0 ? double.PositiveInfinity : Math.Abs(1.0 / direction.Y);
            var deltaZ = stepZ == 0 ? double.PositiveInfinity : Math.Abs(1.0 / direction.Z);
            var maxX = stepX == 0 ? double.PositiveInfinity : (stepX > 0 ? x + 1 - origin.X : origin.X - x) * deltaX;
            var maxY = stepY == 0 ? double.PositiveInfinity : (stepY > 0 ? y + 1 - origin.Y : origin.Y - y) * deltaY;
            var maxZ = stepZ == 0 ? double.PositiveInfinity : (stepZ > 0 ? z + 1 - origin.Z : origin.Z - z) * deltaZ;

            for (var i = 0; i < 256; i++)
            {
                var hit = Test(world, new BlockPos(x, y, z), origin, direction, maxDistance);
                if (hit.HasValue) return hit;

                if (maxX < maxY && maxX < maxZ)
                {
                    if (maxX > maxDistance) break;
                    x += stepX;
                    maxX += deltaX;
                }
                else if (maxY < maxZ)
                {
                    if (maxY > maxDistance) break;
                    y += stepY;
                    maxY += deltaY;
                }
                else
                {
                    if (maxZ > maxDistance) break;
                    z += stepZ;
                    maxZ += deltaZ;
                }
            }
            return null;
        }

        private static BlockHit? Test(World world, BlockPos position, Vec3d origin, Vec3d direction, double maxDistance)
        {
            var state = world.GetBlock(position);
            if (state == null || state.Block.IsFluid) return null;
            BlockHit? best = null;
            foreach (var box in state.OutlineShape)
            {
                var distance = box.Offset(position).Raycast(origin, direction, out var face);
                if (!distance.HasValue || distance.Value > maxDistance) continue;
                if (best.HasValue && best.Value.Distance <= distance.Value) continue;
                best = new BlockHit
                {
                    Position = position,
                    Face = face,
                    State = state,
                    Distance = distance.Value,
                    Point = origin + direction * distance.Value,
                };
            }
            return best;
        }
    }
}
