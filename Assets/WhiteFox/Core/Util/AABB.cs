// WhiteFox - Unity port of Minosoft (https://gitlab.bixilon.de/bixilon/minosoft), Copyright (C) 2020-2023 Moritz Zwerger
// SPDX-License-Identifier: GPL-3.0-or-later

using System;

namespace WhiteFox.Core.Util
{
    /// <summary>Axis aligned bounding box in double precision. Port of Minosoft's AABB.</summary>
    public readonly struct AABB
    {
        public const double Epsilon = 1.0E-7;

        public readonly Vec3d Min;
        public readonly Vec3d Max;

        public static readonly AABB FullBlock = new AABB(0, 0, 0, 1, 1, 1);

        public AABB(Vec3d min, Vec3d max)
        {
            Min = new Vec3d(Math.Min(min.X, max.X), Math.Min(min.Y, max.Y), Math.Min(min.Z, max.Z));
            Max = new Vec3d(Math.Max(min.X, max.X), Math.Max(min.Y, max.Y), Math.Max(min.Z, max.Z));
        }

        public AABB(double minX, double minY, double minZ, double maxX, double maxY, double maxZ) : this(new Vec3d(minX, minY, minZ), new Vec3d(maxX, maxY, maxZ))
        {
        }

        public AABB Offset(Vec3d v) => new AABB(Min + v, Max + v);
        public AABB Offset(double x, double y, double z) => Offset(new Vec3d(x, y, z));
        public AABB Offset(BlockPos p) => Offset(p.X, p.Y, p.Z);
        public AABB Grow(double size) => new AABB(Min - new Vec3d(size, size, size), Max + new Vec3d(size, size, size));
        public AABB Shrink(double size = Epsilon) => Grow(-size);

        /// <summary>Extends the box in the direction of the movement (vanilla expandTowards).</summary>
        public AABB Extend(Vec3d movement)
        {
            double minX = Min.X, minY = Min.Y, minZ = Min.Z, maxX = Max.X, maxY = Max.Y, maxZ = Max.Z;
            if (movement.X < 0) minX += movement.X;
            else maxX += movement.X;
            if (movement.Y < 0) minY += movement.Y;
            else maxY += movement.Y;
            if (movement.Z < 0) minZ += movement.Z;
            else maxZ += movement.Z;
            return new AABB(minX, minY, minZ, maxX, maxY, maxZ);
        }

        public bool Intersects(AABB o) =>
            Min.X < o.Max.X && Max.X > o.Min.X &&
            Min.Y < o.Max.Y && Max.Y > o.Min.Y &&
            Min.Z < o.Max.Z && Max.Z > o.Min.Z;

        private static bool Overlaps(double min1, double max1, double min2, double max2) => max1 > min2 + Epsilon && max2 > min1 + Epsilon;

        /// <summary>
        /// How far <paramref name="other"/> (moved by <paramref name="offset"/>) can travel on <paramref name="axis"/> before hitting this box.
        /// This is the per-axis clipping used by vanilla collision.
        /// </summary>
        public double CalculateMaxDistance(AABB other, Vec3d offset, double maxDistance, Axis axis)
        {
            Axis a1, a2;
            switch (axis)
            {
                case Axis.X:
                    a1 = Axis.Y;
                    a2 = Axis.Z;
                    break;
                case Axis.Y:
                    a1 = Axis.X;
                    a2 = Axis.Z;
                    break;
                default:
                    a1 = Axis.X;
                    a2 = Axis.Y;
                    break;
            }
            if (!Overlaps(Min[a1], Max[a1], other.Min[a1] + offset[a1], other.Max[a1] + offset[a1])) return maxDistance;
            if (!Overlaps(Min[a2], Max[a2], other.Min[a2] + offset[a2], other.Max[a2] + offset[a2])) return maxDistance;

            var otherMin = other.Min[axis] + offset[axis];
            var otherMax = other.Max[axis] + offset[axis];

            if (maxDistance > 0)
            {
                var distance = Min[axis] - otherMax;
                if (distance >= -Epsilon) return Math.Min(maxDistance, Math.Max(0, distance));
            }
            else if (maxDistance < 0)
            {
                var distance = Max[axis] - otherMin;
                if (distance <= Epsilon) return Math.Max(maxDistance, Math.Min(0, distance));
            }
            return maxDistance;
        }

        /// <summary>Slab ray intersection; returns the distance along <paramref name="direction"/> or null.</summary>
        public double? Raycast(Vec3d origin, Vec3d direction, out Direction hitFace)
        {
            var tMin = double.NegativeInfinity;
            var tMax = double.PositiveInfinity;
            hitFace = Direction.Up;
            for (var i = 0; i < 3; i++)
            {
                var axis = (Axis)i;
                var d = direction[axis];
                if (Math.Abs(d) < 0.00001)
                {
                    if (origin[axis] < Min[axis] || origin[axis] > Max[axis]) return null;
                    continue;
                }
                var t1 = (Min[axis] - origin[axis]) / d;
                var t2 = (Max[axis] - origin[axis]) / d;
                // entering through the min face if moving positive
                var enterFace = d > 0 ? NegativeFace(axis) : NegativeFace(axis).Opposite();
                if (t1 > t2)
                {
                    var t = t1;
                    t1 = t2;
                    t2 = t;
                }
                if (t1 > tMin)
                {
                    tMin = t1;
                    hitFace = enterFace;
                }
                if (t2 < tMax) tMax = t2;
                if (tMin > tMax) return null;
            }
            if (tMax < 0) return null;
            return Math.Max(0, tMin);
        }

        private static Direction NegativeFace(Axis axis) => axis == Axis.X ? Direction.West : axis == Axis.Y ? Direction.Down : Direction.North;

        public override string ToString() => "AABB" + Min + "->" + Max;
    }
}
