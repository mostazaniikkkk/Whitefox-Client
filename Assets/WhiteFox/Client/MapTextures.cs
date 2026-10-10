// WhiteFox - Unity port of Minosoft (https://gitlab.bixilon.de/bixilon/minosoft), Copyright (C) 2020-2023 Moritz Zwerger
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Collections.Generic;
using UnityEngine;
using WhiteFox.Core.Data;
using WhiteFox.Core.Game;
using WhiteFox.Core.Network;

namespace WhiteFox.Client
{
    /// <summary>
    /// Map item textures (vanilla MapRenderer): 128x128 color ids of the Map Data packets, each id = base color * 4 +
    /// brightness (180, 220, 255, 135 / 255), base colors of vanilla MapColor. The texture has twice the resolution so the
    /// markers (textures/map/map_icons.png, 16 icons of 8x8 per row, 8 map pixels big, turned in 16ths) look like vanilla's.
    /// </summary>
    public sealed class MapTextures : System.IDisposable
    {
        private static readonly int[] BaseColors =
        {
            0x000000, 0x7FB238, 0xF7E9A3, 0xC7C7C7, 0xFF0000, 0xA0A0FF, 0xA7A7A7, 0x007C00, 0xFFFFFF, 0xA4A8B8, 0x976D4D, 0x707070, 0x4040FF,
            0x8F7748, 0xFFFCF5, 0xD87F33, 0xB24CD8, 0x6699D8, 0xE5E533, 0x7FCC19, 0xF27FA5, 0x4C4C4C, 0x999999, 0x4C7F99, 0x7F3FB2, 0x334CB2,
            0x664C33, 0x667F33, 0x993333, 0x191919, 0xFAEE4D, 0x5CDBD5, 0x4A80FF, 0x00D93A, 0x815631, 0x700200, 0xD1B1A1, 0x9F5224, 0x95576C,
            0x706C8A, 0xBA8524, 0x677535, 0xA04D4E, 0x392923, 0x876B62, 0x575C5C, 0x7A4958, 0x4C3E5C, 0x4C3223, 0x4C522A, 0x8E3C2E, 0x251610,
            0xBD3031, 0x943F61, 0x5C191D, 0x167E86, 0x3A8E8C, 0x562C3E, 0x14B485, 0x646464, 0xD8AF93, 0x7FA796,
        };

        private static readonly int[] Brightness = { 180, 220, 255, 135 };

        private readonly Dictionary<int, (Texture2D texture, int version)> textures = new Dictionary<int, (Texture2D, int)>();
        private const int Size = 256;
        private readonly Color32[] pixels = new Color32[Size * Size];

        /// <summary>Source of the marker atlas (set by the client).</summary>
        public Gui.GuiAssets Assets;
        private Color32[] icons;
        private int iconsSize;

        /// <summary>The map id of a filled map item (1.20.4: tag "map"), or -1.</summary>
        public static int MapId(ItemStack stack) => stack?.Item?.Name.Path == "filled_map" ? Nbt.GetInt(stack.Nbt, "map", -1) : -1;

        /// <summary>The texture of a map, rebuilt when new data arrived; null if nothing was received yet.</summary>
        public Texture2D Get(GameSession session, int id)
        {
            if (id < 0 || !session.Maps.TryGetValue(id, out var map)) return null;
            if (textures.TryGetValue(id, out var cached) && cached.version == map.Version) return cached.texture;
            var texture = cached.texture;
            if (texture == null) texture = new Texture2D(Size, Size, TextureFormat.RGBA32, false) { filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Clamp, name = "Map " + id };
            for (var y = 0; y < 128; y++)
            {
                for (var x = 0; x < 128; x++)
                {
                    var value = map.Colors[y * 128 + x] & 0xFF;
                    var color = value >> 2;
                    Color32 pixel = default;
                    if (color > 0 && color < BaseColors.Length)
                    {
                        var rgb = BaseColors[color];
                        var shade = Brightness[value & 3];
                        pixel = new Color32((byte)(((rgb >> 16) & 0xFF) * shade / 255), (byte)(((rgb >> 8) & 0xFF) * shade / 255), (byte)((rgb & 0xFF) * shade / 255), 255);
                    }
                    // texture rows are bottom up; every map pixel is 2x2
                    var row = (Size - 1 - y * 2) * Size + x * 2;
                    pixels[row] = pixels[row + 1] = pixels[row - Size] = pixels[row - Size + 1] = pixel;
                }
            }
            DrawDecorations(map);
            texture.SetPixels32(pixels);
            texture.Apply(false);
            textures[id] = (texture, map.Version);
            return texture;
        }

        private void DrawDecorations(MapData map)
        {
            if (map.Decorations.Count == 0) return;
            if (icons == null)
            {
                var sprite = Assets?.Get("assets/minecraft/textures/map/map_icons.png");
                if (sprite == null || !sprite.Texture.isReadable) return;
                icons = sprite.Texture.GetPixels32();
                iconsSize = sprite.Texture.width;
            }
            var cell = iconsSize / 16;
            foreach (var decoration in map.Decorations)
            {
                if (decoration.Type < 0 || decoration.Type >= 256) continue;
                // center in map pixels, then each texture pixel inside the turned 8x8 square samples the icon
                var centerX = decoration.X / 2.0f + 64;
                var centerY = decoration.Z / 2.0f + 64;
                var angle = decoration.Rotation * 22.5f * Mathf.Deg2Rad;
                var cos = Mathf.Cos(angle);
                var sin = Mathf.Sin(angle);
                var iconX = decoration.Type % 16 * cell;
                var iconTop = decoration.Type / 16 * cell;
                for (var py = -12; py < 12; py++)
                {
                    for (var px = -12; px < 12; px++)
                    {
                        var tx = Mathf.FloorToInt(centerX * 2) + px;
                        var ty = Mathf.FloorToInt(centerY * 2) + py;
                        if (tx < 0 || ty < 0 || tx >= Size || ty >= Size) continue;
                        // offset in map pixels (y down), turned back, in icon units -1..1 (the icon is 8 map pixels)
                        var dx = (tx + 0.5f) / 2 - centerX;
                        var dy = (ty + 0.5f) / 2 - centerY;
                        var lx = (dx * cos + dy * sin) / 4;
                        var ly = (-dx * sin + dy * cos) / 4;
                        if (lx < -1 || lx >= 1 || ly < -1 || ly >= 1) continue;
                        // vanilla's quad puts the top of the icon at +y (down on the map): the icon is drawn flipped
                        var u = iconX + Mathf.Min(cell - 1, (int)((lx + 1) / 2 * cell));
                        var vTop = iconTop + Mathf.Min(cell - 1, (int)((1 - ly) / 2 * cell));
                        var source = icons[(iconsSize - 1 - vTop) * iconsSize + u];
                        if (source.a < 128) continue;
                        pixels[(Size - 1 - ty) * Size + tx] = source;
                    }
                }
            }
        }

        public void Dispose()
        {
            foreach (var entry in textures.Values) Object.Destroy(entry.texture);
            textures.Clear();
        }
    }
}
