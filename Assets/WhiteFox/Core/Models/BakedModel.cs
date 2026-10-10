// WhiteFox - Unity port of Minosoft (https://gitlab.bixilon.de/bixilon/minosoft), Copyright (C) 2020-2023 Moritz Zwerger
// SPDX-License-Identifier: GPL-3.0-or-later

using System;
using System.Numerics;
using WhiteFox.Core.Textures;
using WhiteFox.Core.Util;

namespace WhiteFox.Core.Models
{
    /// <summary>
    /// A baked face (Minosoft's BakedFace). Vertices are in block local minecraft coordinates (0..1) and ordered
    /// top-left, bottom-left, bottom-right, top-right as seen from outside (counter clockwise, right handed).
    /// </summary>
    public sealed class BakedQuad
    {
        public readonly Vector3[] Positions = new Vector3[4];
        /// <summary>Texture coordinates 0..1, v pointing down (minecraft convention).</summary>
        public readonly Vector2[] UVs = new Vector2[4];
        public TextureEntry Texture;
        public int TintIndex = -1;
        public bool Shade = true;
        /// <summary>Direction the face points to after all rotations.</summary>
        public Direction Face;
        /// <summary>Neighbour that hides this face, if any.</summary>
        public Direction? CullFace;
        /// <summary>The face lies flat on the block boundary of <see cref="Face"/>; light is then taken from the neighbour.</summary>
        public bool OnBoundary;

        /// <summary>True if the face covers the whole 1x1 side of the block.</summary>
        public bool CoversFullSide
        {
            get
            {
                if (!OnBoundary) return false;
                float minA = float.MaxValue, maxA = float.MinValue, minB = float.MaxValue, maxB = float.MinValue;
                foreach (var p in Positions)
                {
                    float a, b;
                    switch (Face.GetAxis())
                    {
                        case Axis.X:
                            a = p.Y;
                            b = p.Z;
                            break;
                        case Axis.Y:
                            a = p.X;
                            b = p.Z;
                            break;
                        default:
                            a = p.X;
                            b = p.Y;
                            break;
                    }
                    minA = Math.Min(minA, a);
                    maxA = Math.Max(maxA, a);
                    minB = Math.Min(minB, b);
                    maxB = Math.Max(maxB, b);
                }
                const float e = 0.0001f;
                return minA <= e && minB <= e && maxA >= 1 - e && maxB >= 1 - e;
            }
        }
    }

    public sealed class BakedModel
    {
        public BakedQuad[] Quads;
        public bool AmbientOcclusion = true;

        public static readonly BakedModel Empty = new BakedModel { Quads = Array.Empty<BakedQuad>() };
    }

    /// <summary>What the mesher renders for a block state: one model or a weighted random choice (Minosoft's WeightedBlockRender).</summary>
    public sealed class BlockRenderModel
    {
        public BakedModel[] Models;
        public int[] Weights;
        public int TotalWeight;

        public BakedModel Pick(int x, int y, int z)
        {
            if (Models.Length == 1) return Models[0];
            var seed = PositionSeed(x, y, z);
            var value = (int)((ulong)seed % (ulong)TotalWeight);
            for (var i = 0; i < Models.Length; i++)
            {
                value -= Weights[i];
                if (value < 0) return Models[i];
            }
            return Models[0];
        }

        /// <summary>Vanilla's Mth.getSeed.</summary>
        public static long PositionSeed(int x, int y, int z)
        {
            var seed = (long)(x * 3129871) ^ (z * 116129781L) ^ y;
            seed = seed * seed * 42317861L + seed * 11L;
            return seed >> 16;
        }
    }
}
