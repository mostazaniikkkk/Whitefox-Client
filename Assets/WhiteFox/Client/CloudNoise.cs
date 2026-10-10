// WhiteFox - Unity port of Minosoft (https://gitlab.bixilon.de/bixilon/minosoft), Copyright (C) 2020-2023 Moritz Zwerger
// SPDX-License-Identifier: GPL-3.0-or-later

using UnityEngine;

namespace WhiteFox.Client
{
    /// <summary>
    /// The 3D noise of the volumetric clouds (the "Perlin-Worley" noise of real time cloud renderers, as in the
    /// Nubis talks and Iris shader packs like Arc Shader): 64³ texels that tile in every direction.
    /// R: low frequency Perlin-Worley (cloud shapes), G: Worley at 3 frequencies (erodes the edges), B: smooth value
    /// noise (the coverage map, sampled in 2D).
    /// </summary>
    public static class CloudNoise
    {
        private const int Size = 64;

        public static Texture3D Create()
        {
            var colors = new Color32[Size * Size * Size];
            var points4 = FeaturePoints(4, 11);
            var points8 = FeaturePoints(8, 23);
            var points16 = FeaturePoints(16, 37);
            for (var z = 0; z < Size; z++)
            {
                for (var y = 0; y < Size; y++)
                {
                    for (var x = 0; x < Size; x++)
                    {
                        var p = new Vector3(x + 0.5f, y + 0.5f, z + 0.5f) / Size;
                        var value = Fbm(p, 4, 3);
                        var worley = 1 - Worley(p, 4, points4);
                        // Perlin-Worley: the soft noise remapped by the cell noise, billowy shapes
                        var shape = Mathf.Clamp01(Remap(value, worley - 1, 1, 0, 1));
                        var detail = (1 - Worley(p, 8, points8)) * 0.625f + (1 - Worley(p, 16, points16)) * 0.375f;
                        var coverage = Fbm(p, 2, 4);
                        colors[(z * Size + y) * Size + x] = new Color32((byte)(shape * 255), (byte)(Mathf.Clamp01(detail) * 255), (byte)(Mathf.Clamp01(coverage) * 255), 255);
                    }
                }
            }
            var texture = new Texture3D(Size, Size, Size, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Repeat, filterMode = FilterMode.Bilinear, name = "Cloud noise" };
            texture.SetPixels32(colors);
            texture.Apply(false, true);
            return texture;
        }

        private static float Remap(float value, float fromMin, float fromMax, float toMin, float toMax) =>
            toMin + (value - fromMin) / Mathf.Max(fromMax - fromMin, 1e-4f) * (toMax - toMin);

        private static Vector3[] FeaturePoints(int cells, int seed)
        {
            var random = new System.Random(seed);
            var points = new Vector3[cells * cells * cells];
            for (var i = 0; i < points.Length; i++) points[i] = new Vector3((float)random.NextDouble(), (float)random.NextDouble(), (float)random.NextDouble());
            return points;
        }

        /// <summary>Distance to the nearest feature point (one per cell), wrapping around: 0 at a point, ~1 between.</summary>
        private static float Worley(Vector3 p, int cells, Vector3[] points)
        {
            var scaled = p * cells;
            int cx = Mathf.FloorToInt(scaled.x), cy = Mathf.FloorToInt(scaled.y), cz = Mathf.FloorToInt(scaled.z);
            var nearest = 9f;
            for (var dz = -1; dz <= 1; dz++)
            {
                for (var dy = -1; dy <= 1; dy++)
                {
                    for (var dx = -1; dx <= 1; dx++)
                    {
                        int x = cx + dx, y = cy + dy, z = cz + dz;
                        int wx = ((x % cells) + cells) % cells, wy = ((y % cells) + cells) % cells, wz = ((z % cells) + cells) % cells;
                        var feature = new Vector3(x, y, z) + points[(wz * cells + wy) * cells + wx];
                        var distance = (feature - scaled).sqrMagnitude;
                        if (distance < nearest) nearest = distance;
                    }
                }
            }
            return Mathf.Clamp01(Mathf.Sqrt(nearest));
        }

        private static float Hash(int x, int y, int z, int period)
        {
            x = ((x % period) + period) % period;
            y = ((y % period) + period) % period;
            z = ((z % period) + period) % period;
            var h = (uint)(x * 73856093) ^ (uint)(y * 19349663) ^ (uint)(z * 83492791);
            h ^= h >> 13;
            h *= 0x5bd1e995;
            h ^= h >> 15;
            return (h & 0xFFFFFF) / (float)0xFFFFFF;
        }

        /// <summary>Tileable value noise with smooth interpolation.</summary>
        private static float Value(Vector3 p, int period)
        {
            var scaled = p * period;
            int x = Mathf.FloorToInt(scaled.x), y = Mathf.FloorToInt(scaled.y), z = Mathf.FloorToInt(scaled.z);
            float fx = scaled.x - x, fy = scaled.y - y, fz = scaled.z - z;
            fx = fx * fx * (3 - 2 * fx);
            fy = fy * fy * (3 - 2 * fy);
            fz = fz * fz * (3 - 2 * fz);
            float Lerp(float a, float b, float t) => a + (b - a) * t;
            var x00 = Lerp(Hash(x, y, z, period), Hash(x + 1, y, z, period), fx);
            var x10 = Lerp(Hash(x, y + 1, z, period), Hash(x + 1, y + 1, z, period), fx);
            var x01 = Lerp(Hash(x, y, z + 1, period), Hash(x + 1, y, z + 1, period), fx);
            var x11 = Lerp(Hash(x, y + 1, z + 1, period), Hash(x + 1, y + 1, z + 1, period), fx);
            return Lerp(Lerp(x00, x10, fy), Lerp(x01, x11, fy), fz);
        }

        private static float Fbm(Vector3 p, int period, int octaves)
        {
            float sum = 0, amplitude = 0.5f, total = 0;
            for (var i = 0; i < octaves; i++)
            {
                sum += Value(p, period << i) * amplitude;
                total += amplitude;
                amplitude *= 0.5f;
            }
            return sum / total;
        }
    }
}
