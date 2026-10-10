// WhiteFox - Unity port of Minosoft (https://gitlab.bixilon.de/bixilon/minosoft), Copyright (C) 2020-2023 Moritz Zwerger
// SPDX-License-Identifier: GPL-3.0-or-later

using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using WhiteFox.Core.Assets;
using WhiteFox.Core.Util;

namespace WhiteFox.Core.Textures
{
    public enum TextureTransparency
    {
        /// <summary>Every pixel has alpha 255.</summary>
        Opaque,
        /// <summary>Only alpha 0 or 255 (alpha test / discard).</summary>
        Transparent,
        /// <summary>Partial alpha, needs blending.</summary>
        Translucent,
    }

    public sealed class TextureEntry
    {
        public readonly ResourceLocation Name;
        public readonly int Layer;
        public TextureTransparency Transparency = TextureTransparency.Opaque;
        public TextureAnimation Animation;

        public TextureEntry(ResourceLocation name, int layer)
        {
            Name = name;
            Layer = layer;
        }
    }

    /// <summary>Animated texture (.mcmeta). Frames are stored in a separate array and copied into <see cref="Layer"/> when they change.</summary>
    public sealed class TextureAnimation
    {
        public int Layer;
        public int[] FrameLayers;
        public int[] FrameTimes;
        public bool Interpolate;
        public int TotalTime;

        /// <summary>Returns the index into <see cref="FrameLayers"/> for the given game tick.</summary>
        public int FrameAt(long tick)
        {
            var time = (int)(tick % TotalTime);
            for (var i = 0; i < FrameTimes.Length; i++)
            {
                if (time < FrameTimes[i]) return i;
                time -= FrameTimes[i];
            }
            return FrameTimes.Length - 1;
        }
    }

    /// <summary>
    /// Static block texture array. Minosoft keeps one array per resolution; this port keeps a single array of the
    /// largest resolution and upscales smaller textures (nearest neighbour), which keeps the shader trivial.
    /// Pixel rows are stored top row first (png order).
    /// </summary>
    public sealed class TextureManager
    {
        public static readonly ResourceLocation Missing = ResourceLocation.Of("whitefox:missing");

        private readonly Dictionary<ResourceLocation, TextureEntry> entries = new Dictionary<ResourceLocation, TextureEntry>();
        private readonly List<TextureEntry> ordered = new List<TextureEntry>();

        public int Size { get; private set; } = 16;
        public readonly List<byte[]> Layers = new List<byte[]>();
        public readonly List<byte[]> Frames = new List<byte[]>();
        public readonly List<TextureAnimation> Animations = new List<TextureAnimation>();

        /// <summary>LabPBR normal maps (_n), one per layer: RG normal (DirectX, green down), B ambient occlusion, A height.</summary>
        public readonly List<byte[]> NormalLayers = new List<byte[]>();
        /// <summary>LabPBR specular maps (_s), one per layer: R perceptual smoothness, G F0/metal, B porosity/SSS, A emission.</summary>
        public readonly List<byte[]> SpecularLayers = new List<byte[]>();
        public int PbrNormalCount { get; private set; }
        public int PbrSpecularCount { get; private set; }

        /// <summary>Generate normal maps from the albedo when a texture has no _n map (subtle relief for vanilla textures).</summary>
        public bool GenerateNormals = true;
        public float GeneratedNormalStrength = 0.5f;

        public IReadOnlyList<TextureEntry> Entries => ordered;

        public TextureManager()
        {
            Get(Missing);
        }

        /// <summary>Registers (or returns) a texture. Names are "minecraft:block/stone" style (without textures/ and .png).</summary>
        public TextureEntry Get(ResourceLocation name)
        {
            if (entries.TryGetValue(name, out var entry)) return entry;
            entry = new TextureEntry(name, ordered.Count);
            entries[name] = entry;
            ordered.Add(entry);
            return entry;
        }

        public TextureEntry Get(string name) => Get(ResourceLocation.Of(name));

        /// <summary>Loads every registered texture. Missing textures get the magenta/black checkerboard.</summary>
        public void Load(AssetManager assets)
        {
            var images = new RgbaImage[ordered.Count];
            var frameSizes = new int[ordered.Count];
            var metas = new JObject[ordered.Count];
            var size = 16;

            for (var i = 0; i < ordered.Count; i++)
            {
                var entry = ordered[i];
                if (entry.Name == Missing) continue;
                var path = entry.Name.ToAssetPath("textures/", ".png");
                if (!assets.TryGet(path, out var data))
                {
                    Log.Warn("Missing texture " + entry.Name);
                    continue;
                }
                try
                {
                    images[i] = PngDecoder.Decode(data);
                }
                catch (Exception exception)
                {
                    Log.Warn("Can not decode texture " + entry.Name + ": " + exception.Message);
                    continue;
                }
                if (assets.TryGetText(path + ".mcmeta", out var meta))
                {
                    try
                    {
                        metas[i] = JObject.Parse(meta);
                    }
                    catch (Exception exception)
                    {
                        Log.Warn("Invalid mcmeta for " + entry.Name + ": " + exception.Message);
                    }
                }
                var frameWidth = images[i].Width;
                if (metas[i]?["animation"]?["width"] != null) frameWidth = (int)metas[i]["animation"]["width"];
                frameSizes[i] = frameWidth;
                size = Math.Max(size, NextPowerOfTwo(frameWidth));
            }
            Size = Math.Min(size, 512);

            Layers.Clear();
            Frames.Clear();
            Animations.Clear();
            for (var i = 0; i < ordered.Count; i++)
            {
                var entry = ordered[i];
                var image = images[i];
                if (image == null)
                {
                    Layers.Add(CreateMissing(Size));
                    entry.Transparency = TextureTransparency.Opaque;
                    continue;
                }

                var animation = metas[i]?["animation"] as JObject;
                if (animation == null)
                {
                    var square = image.Height > image.Width ? image.Crop(0, 0, image.Width, image.Width) : image;
                    var pixels = square.Scale(Size, Size).Pixels;
                    entry.Transparency = Analyze(pixels);
                    BleedTransparent(pixels);
                    Layers.Add(pixels);
                    continue;
                }

                var frameWidth = frameSizes[i];
                var frameHeight = animation["height"] != null ? (int)animation["height"] : frameWidth;
                var frameCount = Math.Max(1, image.Height / frameHeight);
                var columns = Math.Max(1, image.Width / frameWidth);
                var firstFrame = Frames.Count;
                var transparency = TextureTransparency.Opaque;
                for (var f = 0; f < frameCount * columns; f++)
                {
                    var pixels = image.Crop(f % columns * frameWidth, f / columns * frameHeight, frameWidth, frameHeight).Scale(Size, Size).Pixels;
                    transparency = Max(transparency, Analyze(pixels));
                    BleedTransparent(pixels);
                    Frames.Add(pixels);
                }
                entry.Transparency = transparency;

                var defaultTime = animation["frametime"] != null ? Math.Max(1, (int)animation["frametime"]) : 1;
                var frameLayers = new List<int>();
                var frameTimes = new List<int>();
                if (animation["frames"] is JArray frames)
                {
                    foreach (var frame in frames)
                    {
                        if (frame.Type == JTokenType.Integer)
                        {
                            frameLayers.Add(firstFrame + Math.Min((int)frame, frameCount - 1));
                            frameTimes.Add(defaultTime);
                        }
                        else
                        {
                            frameLayers.Add(firstFrame + Math.Min((int)frame["index"], frameCount - 1));
                            frameTimes.Add(frame["time"] != null ? Math.Max(1, (int)frame["time"]) : defaultTime);
                        }
                    }
                }
                else
                {
                    for (var f = 0; f < frameCount; f++)
                    {
                        frameLayers.Add(firstFrame + f);
                        frameTimes.Add(defaultTime);
                    }
                }

                Layers.Add(Frames[frameLayers[0]]);
                var total = 0;
                foreach (var time in frameTimes) total += time;
                var textureAnimation = new TextureAnimation
                {
                    Layer = entry.Layer,
                    FrameLayers = frameLayers.ToArray(),
                    FrameTimes = frameTimes.ToArray(),
                    Interpolate = (bool?)animation["interpolate"] ?? false,
                    TotalTime = Math.Max(1, total),
                };
                entry.Animation = textureAnimation;
                if (frameLayers.Count > 1) Animations.Add(textureAnimation);
            }
        }

        /// <summary>
        /// Loads the LabPBR maps of every texture ("block/stone_n.png", "block/stone_s.png"), which resource packs such
        /// as the ones made for Iris/OptiFine shader packs provide. Must run after <see cref="Load"/>.
        /// </summary>
        public void LoadMaterials(AssetManager assets)
        {
            NormalLayers.Clear();
            SpecularLayers.Clear();
            PbrNormalCount = 0;
            PbrSpecularCount = 0;
            for (var i = 0; i < ordered.Count; i++)
            {
                var entry = ordered[i];
                var normal = LoadSquare(assets, entry.Name, "_n");
                if (normal != null) PbrNormalCount++;
                else normal = GenerateNormals && entry.Name != Missing ? GenerateNormal(Layers[i], Size, GeneratedNormalStrength) : FlatNormal(Size);
                NormalLayers.Add(normal);

                var specular = LoadSquare(assets, entry.Name, "_s");
                if (specular != null) PbrSpecularCount++;
                else specular = DefaultSpecular(entry.Name.Path, Size);
                SpecularLayers.Add(specular);
            }
            if (PbrNormalCount + PbrSpecularCount > 0) Log.Info($"Loaded {PbrNormalCount} LabPBR normal and {PbrSpecularCount} specular maps");
        }

        private byte[] LoadSquare(AssetManager assets, ResourceLocation name, string suffix)
        {
            if (!assets.TryGet(name.ToAssetPath("textures/", suffix + ".png"), out var data)) return null;
            try
            {
                var image = PngDecoder.Decode(data);
                if (image.Height > image.Width) image = image.Crop(0, 0, image.Width, image.Width); // animated: first frame
                return image.Scale(Size, Size).Pixels;
            }
            catch (Exception exception)
            {
                Log.Warn("Can not decode " + name + suffix + ": " + exception.Message);
                return null;
            }
        }

        private static byte[] FlatNormal(int size)
        {
            var pixels = new byte[size * size * 4];
            for (var i = 0; i < pixels.Length; i += 4)
            {
                pixels[i] = 128;
                pixels[i + 1] = 128;
                pixels[i + 2] = 255;
                pixels[i + 3] = 255;
            }
            return pixels;
        }

        /// <summary>Height = luminance, normal from a sobel filter, encoded like LabPBR (DirectX green).</summary>
        private static byte[] GenerateNormal(byte[] albedo, int size, float strength)
        {
            var height = new float[size * size];
            for (var i = 0; i < height.Length; i++)
            {
                var o = i * 4;
                height[i] = (albedo[o] * 0.299f + albedo[o + 1] * 0.587f + albedo[o + 2] * 0.114f) / 255.0f;
            }
            float H(int x, int y) => height[Math.Max(0, Math.Min(size - 1, y)) * size + Math.Max(0, Math.Min(size - 1, x))];

            var pixels = new byte[size * size * 4];
            var scale = strength * size / 16.0f;
            for (var y = 0; y < size; y++)
            {
                for (var x = 0; x < size; x++)
                {
                    var dx = (H(x + 1, y - 1) + 2 * H(x + 1, y) + H(x + 1, y + 1)) - (H(x - 1, y - 1) + 2 * H(x - 1, y) + H(x - 1, y + 1));
                    var dy = (H(x - 1, y + 1) + 2 * H(x, y + 1) + H(x + 1, y + 1)) - (H(x - 1, y - 1) + 2 * H(x, y - 1) + H(x + 1, y - 1));
                    // tangent space: x right, y up (image rows go down), z out
                    var nx = -dx * scale;
                    var ny = dy * scale;
                    var length = (float)Math.Sqrt(nx * nx + ny * ny + 1);
                    nx /= length;
                    ny /= length;
                    var o = (y * size + x) * 4;
                    pixels[o] = (byte)Math.Round((nx * 0.5f + 0.5f) * 255);
                    pixels[o + 1] = (byte)Math.Round((-ny * 0.5f + 0.5f) * 255);
                    pixels[o + 2] = 255;
                    pixels[o + 3] = (byte)Math.Max(1, height[y * size + x] * 255);
                }
            }
            return pixels;
        }

        /// <summary>Material guesses for vanilla textures without a _s map: rough dielectric, a few smooth or metallic blocks.</summary>
        private static byte[] DefaultSpecular(string path, int size)
        {
            byte smoothness = 20, f0 = 10;
            if (path.Contains("ice") || path.Contains("glass")) smoothness = 235;
            else if (path.Contains("polished") || path.Contains("quartz") || path.Contains("glazed")) smoothness = 150;
            if (path.EndsWith("iron_block") || path.EndsWith("gold_block") || path.EndsWith("copper_block") || path.EndsWith("netherite_block"))
            {
                smoothness = 170;
                f0 = 255;
            }
            var pixels = new byte[size * size * 4];
            for (var i = 0; i < pixels.Length; i += 4)
            {
                pixels[i] = smoothness;
                pixels[i + 1] = f0;
                pixels[i + 2] = 0;
                pixels[i + 3] = 255;
            }
            return pixels;
        }

        private static TextureTransparency Max(TextureTransparency a, TextureTransparency b) => (TextureTransparency)Math.Max((int)a, (int)b);

        private static TextureTransparency Analyze(byte[] pixels)
        {
            var result = TextureTransparency.Opaque;
            for (var i = 3; i < pixels.Length; i += 4)
            {
                var alpha = pixels[i];
                if (alpha == 255) continue;
                if (alpha == 0) result = TextureTransparency.Transparent;
                else return TextureTransparency.Translucent;
            }
            return result;
        }

        /// <summary>
        /// Fully transparent pixels get the average color of the visible ones, so mipmaps of cutout textures (leaves)
        /// do not darken towards black (vanilla does the same with its alpha weighted mipmap generation).
        /// </summary>
        private static void BleedTransparent(byte[] pixels)
        {
            long r = 0, g = 0, b = 0, count = 0;
            for (var i = 0; i < pixels.Length; i += 4)
            {
                if (pixels[i + 3] == 0) continue;
                r += pixels[i];
                g += pixels[i + 1];
                b += pixels[i + 2];
                count++;
            }
            if (count == 0 || count == pixels.Length / 4) return;
            for (var i = 0; i < pixels.Length; i += 4)
            {
                if (pixels[i + 3] != 0) continue;
                pixels[i] = (byte)(r / count);
                pixels[i + 1] = (byte)(g / count);
                pixels[i + 2] = (byte)(b / count);
            }
        }

        private static byte[] CreateMissing(int size)
        {
            var pixels = new byte[size * size * 4];
            var half = size / 2;
            for (var y = 0; y < size; y++)
            {
                for (var x = 0; x < size; x++)
                {
                    var magenta = (x < half) ^ (y < half);
                    var o = (y * size + x) * 4;
                    pixels[o] = magenta ? (byte)248 : (byte)0;
                    pixels[o + 1] = 0;
                    pixels[o + 2] = magenta ? (byte)248 : (byte)0;
                    pixels[o + 3] = 255;
                }
            }
            return pixels;
        }

        private static int NextPowerOfTwo(int value)
        {
            var result = 1;
            while (result < value) result <<= 1;
            return result;
        }
    }
}
