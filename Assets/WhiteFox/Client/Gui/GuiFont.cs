// WhiteFox - Unity port of Minosoft (https://gitlab.bixilon.de/bixilon/minosoft), Copyright (C) 2020-2023 Moritz Zwerger
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using UnityEngine;
using WhiteFox.Core.Util;

namespace WhiteFox.Client.Gui
{
    /// <summary>
    /// The vanilla bitmap font (font/include/default.json: ascii.png, accented.png, nonlatin_european.png and the
    /// space provider), like Minosoft's BitmapFontType. Glyph widths come from the pixels, as in vanilla.
    /// </summary>
    public sealed class GuiFont
    {
        private struct Glyph
        {
            public Texture2D Texture;
            public Rect UV;
            public float Width;   // drawn width in gui pixels
            public float Height;  // drawn height in gui pixels
            public float Advance; // horizontal advance
            public float Top;     // offset from the line top
        }

        public const float LineHeight = 9;

        private readonly Dictionary<int, Glyph> glyphs = new Dictionary<int, Glyph>();
        private readonly Dictionary<int, float> spaces = new Dictionary<int, float>();

        public GuiFont(GuiAssets assets)
        {
            if (assets.Assets.TryGetText("assets/minecraft/font/include/space.json", out var spaceJson))
            {
                foreach (var provider in (JArray)JObject.Parse(spaceJson)["providers"])
                {
                    if (!(provider["advances"] is JObject advances)) continue;
                    foreach (var pair in advances) spaces[char.ConvertToUtf32(pair.Key, 0)] = (float)pair.Value;
                }
            }
            if (!assets.Assets.TryGetText("assets/minecraft/font/include/default.json", out var json)) return;
            foreach (var provider in (JArray)JObject.Parse(json)["providers"])
            {
                if ((string)provider["type"] != "bitmap") continue;
                LoadBitmap(assets, provider);
            }
        }

        private void LoadBitmap(GuiAssets assets, JToken provider)
        {
            var file = ResourceLocation.Of((string)provider["file"]);
            var sprite = assets.Get(file.ToAssetPath("textures/", ""));
            if (sprite == null) return;
            var texture = sprite.Texture;
            var rows = (JArray)provider["chars"];
            var height = (float?)provider["height"] ?? 8;
            var ascent = (float?)provider["ascent"] ?? 7;
            var pixels = texture.GetPixels32();
            var cellHeight = texture.height / rows.Count;
            for (var row = 0; row < rows.Count; row++)
            {
                var line = (string)rows[row];
                var codepoints = new List<int>();
                for (var i = 0; i < line.Length; i++)
                {
                    var codepoint = char.ConvertToUtf32(line, i);
                    if (char.IsHighSurrogate(line[i])) i++;
                    codepoints.Add(codepoint);
                }
                var cellWidth = texture.width / Mathf.Max(1, codepoints.Count);
                var scale = height / cellHeight;
                for (var column = 0; column < codepoints.Count; column++)
                {
                    var codepoint = codepoints[column];
                    if (codepoint == 0 || glyphs.ContainsKey(codepoint)) continue;
                    var x0 = column * cellWidth;
                    var y0 = row * cellHeight; // from the top
                    var width = MeasureWidth(pixels, texture.width, texture.height, x0, y0, cellWidth, cellHeight);
                    if (width == 0 && codepoint != ' ') continue;
                    glyphs[codepoint] = new Glyph
                    {
                        Texture = texture,
                        // texture rows are stored bottom up
                        UV = new Rect((float)x0 / texture.width, 1.0f - (float)(y0 + cellHeight) / texture.height, (float)width / texture.width, (float)cellHeight / texture.height),
                        Width = width * scale,
                        Height = height,
                        Advance = (int)(0.5f + width * scale) + 1,
                        Top = 7 - ascent,
                    };
                }
            }
        }

        private static int MeasureWidth(Color32[] pixels, int textureWidth, int textureHeight, int x0, int y0, int cellWidth, int cellHeight)
        {
            for (var x = cellWidth - 1; x >= 0; x--)
            {
                for (var y = 0; y < cellHeight; y++)
                {
                    var row = textureHeight - 1 - (y0 + y);
                    if (pixels[row * textureWidth + x0 + x].a > 0) return x + 1;
                }
            }
            return 0;
        }

        /// <summary>True if every character has a glyph (the unicode fallback font is not loaded).</summary>
        public bool CanDisplay(string text)
        {
            for (var i = 0; i < text.Length; i++)
            {
                var codepoint = char.ConvertToUtf32(text, i);
                if (char.IsHighSurrogate(text[i])) i++;
                if (!glyphs.ContainsKey(codepoint) && !spaces.ContainsKey(codepoint)) return false;
            }
            return true;
        }

        public float Width(string text)
        {
            float width = 0;
            var bold = false;
            for (var i = 0; i < text.Length; i++)
            {
                if (text[i] == '§' && i + 1 < text.Length)
                {
                    var length = CodeLength(text, i);
                    var code = char.ToLowerInvariant(text[i + 1]);
                    if (code == 'l') bold = true;
                    else if (code == 'r' || "0123456789abcdef#".IndexOf(code) >= 0) bold = false;
                    i += length - 1;
                    continue;
                }
                width += Advance(char.ConvertToUtf32(text, i)) + (bold ? 1 : 0);
                if (char.IsHighSurrogate(text[i])) i++;
            }
            return width;
        }

        /// <summary>Length of the formatting code at i: §x, or §#RRGGBB for hex colors.</summary>
        private static int CodeLength(string text, int i)
        {
            if (i + 1 < text.Length && text[i + 1] == '#' && i + 8 <= text.Length) return 8;
            return 2;
        }

        private static bool TryHex(string text, int i, out Color color)
        {
            color = Color.white;
            if (i + 8 > text.Length) return false;
            if (!int.TryParse(text.Substring(i + 2, 6), System.Globalization.NumberStyles.HexNumber, null, out var rgb)) return false;
            color = new Color32((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb, 255);
            return true;
        }

        /// <summary>The formatting codes still active at the end of a text (to continue them on the next wrapped line).</summary>
        /// <summary>Word wraps text to a width in pixels; formatting continues on the next line like in vanilla.</summary>
        public string[] Wrap(string text, float width)
        {
            var lines = new List<string>();
            foreach (var paragraph in (text ?? "").Split('\n'))
            {
                var line = "";
                foreach (var word in paragraph.Split(' '))
                {
                    var candidate = line.Length == 0 ? word : line + " " + word;
                    if (Width(candidate) > width && line.Length > 0)
                    {
                        lines.Add(line);
                        line = word;
                    }
                    else
                    {
                        line = candidate;
                    }
                }
                lines.Add(line);
            }
            for (var i = 1; i < lines.Count; i++) lines[i] = ActiveCodes(lines[i - 1]) + lines[i];
            return lines.ToArray();
        }

        public static string ActiveCodes(string text)
        {
            string color = "";
            var styles = new System.Text.StringBuilder();
            for (var i = 0; i < text.Length - 1; i++)
            {
                if (text[i] != '§') continue;
                var length = CodeLength(text, i);
                var code = char.ToLowerInvariant(text[i + 1]);
                if (code == 'r')
                {
                    color = "";
                    styles.Clear();
                }
                else if (code == '#' || "0123456789abcdef".IndexOf(code) >= 0)
                {
                    color = text.Substring(i, length);
                    styles.Clear();
                }
                else if ("klmno".IndexOf(code) >= 0)
                {
                    styles.Append('§').Append(code);
                }
                i += length - 1;
            }
            return color + styles;
        }

        private float Advance(int codepoint)
        {
            if (spaces.TryGetValue(codepoint, out var space)) return space;
            if (glyphs.TryGetValue(codepoint, out var glyph)) return glyph.Advance;
            return glyphs.TryGetValue('?', out var fallback) ? fallback.Advance : 6;
        }

        /// <summary>Draws text at the top left corner (gui pixels). Must be called during a repaint event.</summary>
        public void Draw(string text, float x, float y, Color color, bool shadow = true)
        {
            if (Event.current.type != EventType.Repaint || string.IsNullOrEmpty(text)) return;
            if (shadow) DrawRun(text, x + 1, y + 1, new Color(color.r * 0.25f, color.g * 0.25f, color.b * 0.25f, color.a), true);
            DrawRun(text, x, y, color, false);
        }

        private static Texture2D white;

        private void DrawRun(string text, float x, float y, Color color, bool isShadow)
        {
            var previous = GUI.color;
            var current = color;
            bool bold = false, underline = false, strike = false, obfuscated = false, italic = false;
            if (white == null) white = Texture2D.whiteTexture;
            Color Shadowed(Color value) => isShadow ? new Color(value.r * 0.25f, value.g * 0.25f, value.b * 0.25f, color.a) : new Color(value.r, value.g, value.b, color.a);
            for (var i = 0; i < text.Length; i++)
            {
                if (text[i] == '§' && i + 1 < text.Length)
                {
                    var length = CodeLength(text, i);
                    var code = char.ToLowerInvariant(text[i + 1]);
                    if (code == '#' && TryHex(text, i, out var hex))
                    {
                        current = Shadowed(hex);
                        bold = underline = strike = obfuscated = italic = false;
                    }
                    else if (code == 'r')
                    {
                        current = isShadow ? new Color(color.r, color.g, color.b, color.a) : color;
                        bold = underline = strike = obfuscated = italic = false;
                    }
                    else if ("0123456789abcdef".IndexOf(code) >= 0)
                    {
                        current = Shadowed(FormattingColor(code, color));
                        bold = underline = strike = obfuscated = italic = false;
                    }
                    else if (code == 'l') bold = true;
                    else if (code == 'n') underline = true;
                    else if (code == 'm') strike = true;
                    else if (code == 'k') obfuscated = true;
                    else if (code == 'o') italic = true;
                    i += length - 1;
                    continue;
                }
                var codepoint = char.ConvertToUtf32(text, i);
                if (char.IsHighSurrogate(text[i])) i++;
                float advance;
                if (spaces.TryGetValue(codepoint, out var space))
                {
                    advance = space;
                }
                else
                {
                    if (!glyphs.TryGetValue(codepoint, out var glyph) && !glyphs.TryGetValue('?', out glyph))
                    {
                        x += 6;
                        continue;
                    }
                    if (obfuscated) glyph = RandomGlyph(glyph.Advance) ?? glyph;
                    GUI.color = current;
                    var matrix = GUI.matrix;
                    if (italic)
                    {
                        // vanilla italic: the glyph leans one pixel to the right at its top (a shear around the baseline)
                        var shear = Matrix4x4.identity;
                        var baseline = y + 7;
                        shear.m01 = -1f / 7f;
                        shear.m03 = baseline / 7f;
                        GUI.matrix = matrix * shear;
                    }
                    GUI.DrawTextureWithTexCoords(new Rect(x, y + glyph.Top, glyph.Width, glyph.Height), glyph.Texture, glyph.UV);
                    if (bold) GUI.DrawTextureWithTexCoords(new Rect(x + 1, y + glyph.Top, glyph.Width, glyph.Height), glyph.Texture, glyph.UV);
                    if (italic) GUI.matrix = matrix;
                    advance = glyph.Advance;
                }
                if (bold) advance += 1;
                GUI.color = current;
                if (underline) GUI.DrawTexture(new Rect(x - 1, y + 8, advance + 1, 1), white);
                if (strike) GUI.DrawTexture(new Rect(x - 1, y + 3.5f, advance + 1, 1), white);
                x += advance;
            }
            GUI.color = previous;
        }

        private List<Glyph> obfuscatedPool;

        /// <summary>§k: a random glyph with the same width, changing every frame.</summary>
        private Glyph? RandomGlyph(float advance)
        {
            if (obfuscatedPool == null)
            {
                obfuscatedPool = new List<Glyph>();
                for (var c = 33; c < 127; c++)
                {
                    if (glyphs.TryGetValue(c, out var glyph)) obfuscatedPool.Add(glyph);
                }
            }
            var start = Random.Range(0, obfuscatedPool.Count);
            for (var i = 0; i < obfuscatedPool.Count; i++)
            {
                var glyph = obfuscatedPool[(start + i) % obfuscatedPool.Count];
                if (glyph.Advance == advance) return glyph;
            }
            return null;
        }

        private readonly Dictionary<Texture2D, Color32[]> pixelCache = new Dictionary<Texture2D, Color32[]>();

        /// <summary>
        /// Draws text into a pixel buffer (bottom up rows, like Texture2D.SetPixels32) at x, y from the top left, for text
        /// in the world (signs). Colors (§ codes, §#RRGGBB) are applied; glyphs keep their pixel size.
        /// </summary>
        public void Rasterize(string text, Color32[] target, int width, int height, float x, float y, Color32 color, bool shadow = false)
        {
            // the alpha of the given color applies to every color; a shadow darkens the § colors to a quarter
            Color32 Styled(Color value) => shadow
                ? new Color32((byte)(value.r * 63), (byte)(value.g * 63), (byte)(value.b * 63), color.a)
                : new Color32((byte)(value.r * 255), (byte)(value.g * 255), (byte)(value.b * 255), color.a);
            var current = color;
            for (var i = 0; i < text.Length; i++)
            {
                if (text[i] == '§' && i + 1 < text.Length)
                {
                    var length = CodeLength(text, i);
                    var code = char.ToLowerInvariant(text[i + 1]);
                    if (code == '#' && TryHex(text, i, out var hex)) current = Styled(hex);
                    else if (code == 'r') current = color;
                    else if ("0123456789abcdef".IndexOf(code) >= 0) current = Styled(FormattingColor(code, color));
                    i += length - 1;
                    continue;
                }
                var codepoint = char.ConvertToUtf32(text, i);
                if (char.IsHighSurrogate(text[i])) i++;
                if (spaces.TryGetValue(codepoint, out var space))
                {
                    x += space;
                    continue;
                }
                if (!glyphs.TryGetValue(codepoint, out var glyph) && !glyphs.TryGetValue('?', out glyph))
                {
                    x += 6;
                    continue;
                }
                if (!pixelCache.TryGetValue(glyph.Texture, out var source)) pixelCache[glyph.Texture] = source = glyph.Texture.GetPixels32();
                int textureWidth = glyph.Texture.width, textureHeight = glyph.Texture.height;
                var glyphWidth = Mathf.CeilToInt(glyph.Width);
                var glyphHeight = Mathf.CeilToInt(glyph.Height);
                for (var py = 0; py < glyphHeight; py++)
                {
                    var ty = Mathf.FloorToInt(y + glyph.Top) + py;
                    if (ty < 0 || ty >= height) continue;
                    var sourceTop = (1 - glyph.UV.yMax) * textureHeight + (py + 0.5f) / glyph.Height * glyph.UV.height * textureHeight;
                    var sourceRow = textureHeight - 1 - Mathf.Clamp(Mathf.FloorToInt(sourceTop), 0, textureHeight - 1);
                    for (var px = 0; px < glyphWidth; px++)
                    {
                        var tx = Mathf.FloorToInt(x) + px;
                        if (tx < 0 || tx >= width) continue;
                        var sourceColumn = Mathf.Clamp(Mathf.FloorToInt(glyph.UV.x * textureWidth + (px + 0.5f) / glyph.Width * glyph.UV.width * textureWidth), 0, textureWidth - 1);
                        if (source[sourceRow * textureWidth + sourceColumn].a == 0) continue;
                        target[(height - 1 - ty) * width + tx] = current;
                    }
                }
                x += glyph.Advance;
            }
        }

        private static readonly Color[] Formatting =
        {
            new Color32(0x00, 0x00, 0x00, 0xFF), new Color32(0x00, 0x00, 0xAA, 0xFF), new Color32(0x00, 0xAA, 0x00, 0xFF), new Color32(0x00, 0xAA, 0xAA, 0xFF),
            new Color32(0xAA, 0x00, 0x00, 0xFF), new Color32(0xAA, 0x00, 0xAA, 0xFF), new Color32(0xFF, 0xAA, 0x00, 0xFF), new Color32(0xAA, 0xAA, 0xAA, 0xFF),
            new Color32(0x55, 0x55, 0x55, 0xFF), new Color32(0x55, 0x55, 0xFF, 0xFF), new Color32(0x55, 0xFF, 0x55, 0xFF), new Color32(0x55, 0xFF, 0xFF, 0xFF),
            new Color32(0xFF, 0x55, 0x55, 0xFF), new Color32(0xFF, 0x55, 0xFF, 0xFF), new Color32(0xFF, 0xFF, 0x55, 0xFF), new Color32(0xFF, 0xFF, 0xFF, 0xFF),
        };

        /// <summary>Legacy § color codes (0-9, a-f); r resets.</summary>
        private static Color FormattingColor(char code, Color fallback)
        {
            var index = "0123456789abcdef".IndexOf(code);
            if (index < 0) return fallback;
            var color = Formatting[index];
            color.a = fallback.a;
            return color;
        }
    }
}
