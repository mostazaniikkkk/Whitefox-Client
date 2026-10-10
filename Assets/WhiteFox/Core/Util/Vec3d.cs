// WhiteFox - Unity port of Minosoft (https://gitlab.bixilon.de/bixilon/minosoft), Copyright (C) 2020-2023 Moritz Zwerger
// SPDX-License-Identifier: GPL-3.0-or-later

using System;

namespace WhiteFox.Core.Util
{
    /// <summary>Double precision vector. Physics must run in double precision to match vanilla.</summary>
    public struct Vec3d
    {
        public double X;
        public double Y;
        public double Z;

        public static readonly Vec3d Zero = new Vec3d(0, 0, 0);

        public Vec3d(double x, double y, double z)
        {
            X = x;
            Y = y;
            Z = z;
        }

        public double this[Axis axis]
        {
            get => axis == Axis.X ? X : axis == Axis.Y ? Y : Z;
            set
            {
                if (axis == Axis.X) X = value;
                else if (axis == Axis.Y) Y = value;
                else Z = value;
            }
        }

        public double LengthSquared => X * X + Y * Y + Z * Z;
        public double HorizontalLengthSquared => X * X + Z * Z;
        public double Length => Math.Sqrt(LengthSquared);

        public static Vec3d operator +(Vec3d a, Vec3d b) => new Vec3d(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
        public static Vec3d operator -(Vec3d a, Vec3d b) => new Vec3d(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
        public static Vec3d operator *(Vec3d a, double s) => new Vec3d(a.X * s, a.Y * s, a.Z * s);

        public static Vec3d Lerp(Vec3d a, Vec3d b, double t) => new Vec3d(a.X + (b.X - a.X) * t, a.Y + (b.Y - a.Y) * t, a.Z + (b.Z - a.Z) * t);

        public override string ToString() => $"({X:0.###} {Y:0.###} {Z:0.###})";
    }

    public static class MathUtil
    {
        public const double DegToRad = Math.PI / 180.0;

        public static int FloorDiv(int a, int b) => (int)Math.Floor((double)a / b);

        public static float WrapDegrees(float value)
        {
            value %= 360.0f;
            if (value >= 180.0f) value -= 360.0f;
            if (value < -180.0f) value += 360.0f;
            return value;
        }

        public static float LerpYaw(float a, float b, float t) => a + WrapDegrees(b - a) * t;

        /// <summary>Front vector of a minecraft rotation (yaw 0 = south/+Z, pitch &gt; 0 = down).</summary>
        public static Vec3d Front(float yaw, float pitch)
        {
            var y = yaw * DegToRad;
            var p = pitch * DegToRad;
            var cp = Math.Cos(p);
            return new Vec3d(-Math.Sin(y) * cp, -Math.Sin(p), Math.Cos(y) * cp);
        }
    }
}
