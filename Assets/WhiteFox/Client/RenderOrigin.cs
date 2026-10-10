// WhiteFox - Unity port of Minosoft (https://gitlab.bixilon.de/bixilon/minosoft), Copyright (C) 2020-2023 Moritz Zwerger
// SPDX-License-Identifier: GPL-3.0-or-later

using System;
using UnityEngine;

namespace WhiteFox.Client
{
    /// <summary>
    /// Floating origin: Unity works with 32 bit floats, which only have ~0.06 block precision a million blocks away
    /// (vertices shake, seams open). Everything is rendered relative to an origin near the camera, kept in doubles on
    /// the game side and moved in whole sections when the camera gets too far from it.
    /// Render space = (x - origin x, y, -(z - origin z)).
    /// </summary>
    public static class RenderOrigin
    {
        /// <summary>Distance from the origin (blocks) that triggers a move: float precision there is still ~0.0001.</summary>
        public const double MaxDistance = 1024;

        private static readonly int OffsetId = Shader.PropertyToID("_WF_OriginOffset");

        /// <summary>Only for comparisons (-wf-noorigin): render at absolute coordinates like before.</summary>
        public static bool Enabled = true;

        /// <summary>Origin in block coordinates, always a multiple of 16.</summary>
        public static long X { get; private set; }
        public static long Z { get; private set; }

        /// <summary>Raised after the origin moved; every absolute positioned object must be placed again.</summary>
        public static event Action Changed;

        public static Vector3 ToRender(double x, double y, double z) => new Vector3((float)(x - X), (float)y, (float)-(z - Z));

        /// <summary>Minecraft space position relative to the origin (for matrices that are converted later).</summary>
        public static Vector3 Relative(double x, double y, double z) => new Vector3((float)(x - X), (float)y, (float)(z - Z));

        /// <summary>Moves the origin to the camera when it is too far away. Returns true when it moved.</summary>
        public static bool Follow(double x, double z)
        {
            if (!Enabled) return false;
            if (Math.Abs(x - X) < MaxDistance && Math.Abs(z - Z) < MaxDistance) return false;
            Set((long)Math.Floor(x / 16) * 16, (long)Math.Floor(z / 16) * 16);
            return true;
        }

        public static void Set(long x, long z)
        {
            if (x == X && z == Z) return;
            X = x;
            Z = z;
            // shaders that use world positions as pattern coordinates (water waves) stay continuous
            Shader.SetGlobalVector(OffsetId, new Vector4((float)(x % 4096), (float)(-z % 4096), 0, 0));
            Changed?.Invoke();
        }
    }
}
