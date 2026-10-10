// WhiteFox - Unity port of Minosoft (https://gitlab.bixilon.de/bixilon/minosoft), Copyright (C) 2020-2023 Moritz Zwerger
// SPDX-License-Identifier: GPL-3.0-or-later

using System;

namespace WhiteFox.Core.Util
{
    public readonly struct BlockPos : IEquatable<BlockPos>
    {
        public readonly int X;
        public readonly int Y;
        public readonly int Z;

        public BlockPos(int x, int y, int z)
        {
            X = x;
            Y = y;
            Z = z;
        }

        public static BlockPos Floor(double x, double y, double z) => new BlockPos((int)Math.Floor(x), (int)Math.Floor(y), (int)Math.Floor(z));
        public static BlockPos Floor(Vec3d v) => Floor(v.X, v.Y, v.Z);

        public BlockPos Offset(Direction d) => new BlockPos(X + d.X(), Y + d.Y(), Z + d.Z());
        public BlockPos Offset(int x, int y, int z) => new BlockPos(X + x, Y + y, Z + z);

        public ChunkPos ChunkPos => new ChunkPos(X >> 4, Z >> 4);
        public int SectionY => Y >> 4;

        public bool Equals(BlockPos o) => X == o.X && Y == o.Y && Z == o.Z;
        public override bool Equals(object obj) => obj is BlockPos o && Equals(o);
        public override int GetHashCode() => (X * 31 + Y) * 31 + Z;
        public override string ToString() => "(" + X + " " + Y + " " + Z + ")";

        public static bool operator ==(BlockPos a, BlockPos b) => a.Equals(b);
        public static bool operator !=(BlockPos a, BlockPos b) => !a.Equals(b);
    }

    public readonly struct ChunkPos : IEquatable<ChunkPos>
    {
        public readonly int X;
        public readonly int Z;

        public ChunkPos(int x, int z)
        {
            X = x;
            Z = z;
        }

        public bool Equals(ChunkPos o) => X == o.X && Z == o.Z;
        public override bool Equals(object obj) => obj is ChunkPos o && Equals(o);
        public override int GetHashCode() => X * 73856093 ^ Z * 19349663;
        public override string ToString() => "[" + X + " " + Z + "]";

        public static bool operator ==(ChunkPos a, ChunkPos b) => a.Equals(b);
        public static bool operator !=(ChunkPos a, ChunkPos b) => !a.Equals(b);
    }

    /// <summary>Identifies one 16x16x16 section in the world.</summary>
    public readonly struct SectionPos : IEquatable<SectionPos>
    {
        public readonly int X;
        public readonly int Y;
        public readonly int Z;

        public SectionPos(int x, int y, int z)
        {
            X = x;
            Y = y;
            Z = z;
        }

        public ChunkPos Chunk => new ChunkPos(X, Z);

        public bool Equals(SectionPos o) => X == o.X && Y == o.Y && Z == o.Z;
        public override bool Equals(object obj) => obj is SectionPos o && Equals(o);
        public override int GetHashCode() => (X * 73856093) ^ (Y * 19349663) ^ (Z * 83492791);
        public override string ToString() => "{" + X + " " + Y + " " + Z + "}";
    }

    /// <summary>Index inside a section: y &lt;&lt; 8 | z &lt;&lt; 4 | x (same layout as Minosoft/vanilla).</summary>
    public static class SectionIndex
    {
        public const int Size = 16;
        public const int Volume = 4096;

        public static int Of(int x, int y, int z) => (y << 8) | (z << 4) | x;
        public static int X(int index) => index & 0x0F;
        public static int Y(int index) => (index >> 8) & 0x0F;
        public static int Z(int index) => (index >> 4) & 0x0F;
    }
}
