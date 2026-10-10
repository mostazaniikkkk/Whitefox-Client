// WhiteFox - Unity port of Minosoft (https://gitlab.bixilon.de/bixilon/minosoft), Copyright (C) 2020-2023 Moritz Zwerger
// SPDX-License-Identifier: GPL-3.0-or-later

using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using UnityEngine;
using WhiteFox.Core.Assets;
using WhiteFox.Core.Util;

namespace WhiteFox.Client.Gui
{
    /// <summary>A GUI texture with its vanilla scaling info (textures/gui/sprites/*.png.mcmeta: "gui.scaling").</summary>
    public sealed class GuiSprite
    {
        public Texture2D Texture;
        /// <summary>Nine slice borders in texture pixels (left, top, right, bottom), or null for stretched sprites.</summary>
        public RectOffset NineSlice;
        public int Width => Texture.width;
        public int Height => Texture.height;
    }

    /// <summary>
    /// Loads GUI textures from the client jar / resource packs (Minosoft's GUI atlas). Everything is point filtered so
    /// the pixel art stays sharp at integer GUI scales.
    /// </summary>
    public sealed class GuiAssets : IDisposable
    {
        private readonly AssetManager assets;
        private readonly Dictionary<string, GuiSprite> sprites = new Dictionary<string, GuiSprite>();
        private readonly List<Texture2D> owned = new List<Texture2D>();

        public GuiAssets(AssetManager assets)
        {
            this.assets = assets;
        }

        public AssetManager Assets => assets;

        /// <summary>A sprite of textures/gui/sprites (for example "widget/button" or "hud/hotbar").</summary>
        public GuiSprite Sprite(string name) => Get("assets/minecraft/textures/gui/sprites/" + name + ".png");

        /// <summary>Any texture by asset path; returns null if it does not exist.</summary>
        public GuiSprite Get(string path)
        {
            if (sprites.TryGetValue(path, out var cached)) return cached;
            GuiSprite sprite = null;
            if (assets.TryGet(path, out var data))
            {
                try
                {
                    var image = PngDecoder.Decode(data);
                    sprite = new GuiSprite { Texture = ToTexture(image, path) };
                    if (assets.TryGetText(path + ".mcmeta", out var meta)) ReadScaling(sprite, JObject.Parse(meta));
                }
                catch (Exception exception)
                {
                    Log.Warn("Can not load GUI texture " + path + ": " + exception.Message);
                }
            }
            sprites[path] = sprite;
            return sprite;
        }

        public Texture2D ToTexture(RgbaImage image, string name)
        {
            var texture = new Texture2D(image.Width, image.Height, TextureFormat.RGBA32, false)
            {
                name = name,
                filterMode = FilterMode.Point,
                wrapMode = TextureWrapMode.Clamp,
            };
            var flipped = new byte[image.Pixels.Length];
            var stride = image.Width * 4;
            for (var y = 0; y < image.Height; y++) Buffer.BlockCopy(image.Pixels, y * stride, flipped, (image.Height - 1 - y) * stride, stride);
            texture.SetPixelData(flipped, 0);
            texture.Apply(false, false); // stays readable: the font measures its glyphs from the pixels
            owned.Add(texture);
            return texture;
        }

        private static void ReadScaling(GuiSprite sprite, JObject meta)
        {
            var scaling = meta["gui"]?["scaling"];
            if (scaling == null || (string)scaling["type"] != "nine_slice") return;
            var border = scaling["border"];
            if (border is JObject sides)
            {
                sprite.NineSlice = new RectOffset((int?)sides["left"] ?? 0, (int?)sides["right"] ?? 0, (int?)sides["top"] ?? 0, (int?)sides["bottom"] ?? 0);
            }
            else
            {
                var size = (int?)border ?? 0;
                sprite.NineSlice = new RectOffset(size, size, size, size);
            }
        }

        public void Dispose()
        {
            foreach (var texture in owned) UnityEngine.Object.Destroy(texture);
            owned.Clear();
            sprites.Clear();
        }
    }
}
