// WhiteFox - Unity port of Minosoft (https://gitlab.bixilon.de/bixilon/minosoft), Copyright (C) 2020-2023 Moritz Zwerger
// SPDX-License-Identifier: GPL-3.0-or-later

using System;

namespace WhiteFox.Core.Util
{
    public enum Axis
    {
        X,
        Y,
        Z,
    }

    /// <summary>Same order and ids as vanilla/Minosoft Directions. Minecraft coordinates: +X east, +Y up, +Z south.</summary>
    public enum Direction
    {
        Down = 0,
        Up = 1,
        North = 2,
        South = 3,
        West = 4,
        East = 5,
    }

    public static class Directions
    {
        public const int Count = 6;

        public static readonly Direction[] All = { Direction.Down, Direction.Up, Direction.North, Direction.South, Direction.West, Direction.East };

        private static readonly int[] OffsetX = { 0, 0, 0, 0, -1, 1 };
        private static readonly int[] OffsetY = { -1, 1, 0, 0, 0, 0 };
        private static readonly int[] OffsetZ = { 0, 0, -1, 1, 0, 0 };

        public static int X(this Direction d) => OffsetX[(int)d];
        public static int Y(this Direction d) => OffsetY[(int)d];
        public static int Z(this Direction d) => OffsetZ[(int)d];

        public static Direction Opposite(this Direction d) => (Direction)((int)d ^ 1);

        public static Axis GetAxis(this Direction d)
        {
            switch (d)
            {
                case Direction.Down:
                case Direction.Up:
                    return Axis.Y;
                case Direction.North:
                case Direction.South:
                    return Axis.Z;
                default:
                    return Axis.X;
            }
        }

        public static bool IsPositive(this Direction d) => d == Direction.Up || d == Direction.South || d == Direction.East;

        public static Direction Parse(string name)
        {
            switch (name)
            {
                case "down":
                case "bottom":
                    return Direction.Down;
                case "up":
                case "top":
                    return Direction.Up;
                case "north":
                    return Direction.North;
                case "south":
                    return Direction.South;
                case "west":
                    return Direction.West;
                case "east":
                    return Direction.East;
                default:
                    throw new ArgumentException("Unknown direction: " + name);
            }
        }

        /// <summary>Returns the direction closest to the given (already axis aligned) vector.</summary>
        public static Direction FromVector(float x, float y, float z)
        {
            var ax = Math.Abs(x);
            var ay = Math.Abs(y);
            var az = Math.Abs(z);
            if (ay >= ax && ay >= az) return y > 0 ? Direction.Up : Direction.Down;
            if (ax >= az) return x > 0 ? Direction.East : Direction.West;
            return z > 0 ? Direction.South : Direction.North;
        }
    }
}
