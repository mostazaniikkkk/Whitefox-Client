// WhiteFox - Unity port of Minosoft (https://gitlab.bixilon.de/bixilon/minosoft), Copyright (C) 2020-2023 Moritz Zwerger
// SPDX-License-Identifier: GPL-3.0-or-later

using System;
using UnityEngine;

namespace WhiteFox.Client.Gui
{
    /// <summary>
    /// Immediate mode drawing in vanilla "GUI pixels" (Minosoft's GUIRenderer): the screen is scaled by the largest
    /// integer that still fits 320x240, sprites are drawn with their nine slice borders, text with the bitmap font.
    /// Call <see cref="Begin"/> at the start of OnGUI; afterwards all coordinates are GUI pixels.
    /// </summary>
    public sealed class GuiRenderer : IDisposable
    {
        public readonly GuiAssets Assets;
        public readonly GuiFont Font;

        public int Scale { get; private set; } = 2;
        public float Width => Screen.width / (float)Scale;
        public float Height => Screen.height / (float)Scale;

        public static readonly Color White = Color.white;
        public static readonly Color Gray = new Color32(0xA0, 0xA0, 0xA0, 0xFF);
        public static readonly Color DarkGray = new Color32(0x70, 0x70, 0x70, 0xFF);
        public static readonly Color Yellow = new Color32(0xFF, 0xFF, 0x55, 0xFF);
        public static readonly Color Red = new Color32(0xFF, 0x55, 0x55, 0xFF);

        private Texture2D white;

        public GuiRenderer(WhiteFox.Core.Assets.AssetManager assets)
        {
            Assets = new GuiAssets(assets);
            Font = new GuiFont(Assets);
        }

        /// <summary>The controller cursor in screen pixels while it drives the menus, else null.</summary>
        public static Vector2? PointerOverride;

        /// <summary>Vanilla automatic GUI scale ("Auto" in the video settings).</summary>
        public void Begin(int maxScale = 0)
        {
            var scale = 1;
            while ((maxScale <= 0 || scale < maxScale) && Screen.width / (scale + 1) >= 320 && Screen.height / (scale + 1) >= 240) scale++;
            Scale = scale;
            GUI.matrix = Matrix4x4.Scale(new Vector3(scale, scale, 1));
        }

        public bool Repaint => Event.current.type == EventType.Repaint;

        /// <summary>The pointer in GUI units: the mouse, or the controller cursor while it drives the menus.</summary>
        public Vector2 Mouse => PointerOverride.HasValue ? PointerOverride.Value / Scale : Event.current.mousePosition;

        // ------------------------------------------------------------------ drawing

        public void Fill(Rect rect, Color color)
        {
            if (!Repaint) return;
            if (white == null) white = Texture2D.whiteTexture;
            var previous = GUI.color;
            GUI.color = color;
            GUI.DrawTexture(rect, white);
            GUI.color = previous;
        }

        public void Sprite(string name, Rect rect, Color? tint = null) => Sprite(Assets.Sprite(name), rect, tint);

        /// <summary>Draws a sprite into the rect, using its nine slice borders if it has some.</summary>
        public void Sprite(GuiSprite sprite, Rect rect, Color? tint = null)
        {
            if (!Repaint || sprite == null) return;
            var previous = GUI.color;
            GUI.color = tint ?? Color.white;
            var texture = sprite.Texture;
            var b = sprite.NineSlice;
            if (b == null || (rect.width == sprite.Width && rect.height == sprite.Height))
            {
                GUI.DrawTexture(rect, texture);
            }
            else
            {
                float w = texture.width, h = texture.height;
                // columns and rows in texture pixels (x from the left, y from the top) and in screen space
                float[] tx = { 0, b.left, w - b.right, w };
                float[] ty = { 0, b.top, h - b.bottom, h };
                float[] sx = { rect.x, rect.x + b.left, rect.xMax - b.right, rect.xMax };
                float[] sy = { rect.y, rect.y + b.top, rect.yMax - b.bottom, rect.yMax };
                for (var row = 0; row < 3; row++)
                {
                    for (var column = 0; column < 3; column++)
                    {
                        var target = Rect.MinMaxRect(sx[column], sy[row], sx[column + 1], sy[row + 1]);
                        if (target.width <= 0 || target.height <= 0) continue;
                        var uv = Rect.MinMaxRect(tx[column] / w, 1 - ty[row + 1] / h, tx[column + 1] / w, 1 - ty[row] / h);
                        GUI.DrawTextureWithTexCoords(target, texture, uv);
                    }
                }
            }
            GUI.color = previous;
        }

        /// <summary>Tiles a texture (the dirt background of vanilla menus), darkened.</summary>
        public void Tiled(GuiSprite sprite, Rect rect, float tileSize, Color tint)
        {
            if (!Repaint || sprite == null) return;
            sprite.Texture.wrapMode = TextureWrapMode.Repeat;
            var previous = GUI.color;
            GUI.color = tint;
            GUI.DrawTextureWithTexCoords(rect, sprite.Texture, new Rect(0, 0, rect.width / tileSize, rect.height / tileSize));
            GUI.color = previous;
        }

        public void Text(string text, float x, float y, Color? color = null, bool shadow = true) => Font.Draw(text, x, y, color ?? White, shadow);

        public void TextCentered(string text, float centerX, float y, Color? color = null, bool shadow = true) => Font.Draw(text, centerX - Font.Width(text) / 2, y, color ?? White, shadow);

        public void TextRight(string text, float rightX, float y, Color? color = null, bool shadow = true) => Font.Draw(text, rightX - Font.Width(text), y, color ?? White, shadow);

        /// <summary>Draws text scaled around its top left corner (titles).</summary>
        public void TextScaled(string text, float centerX, float y, float scale, Color color)
        {
            var matrix = GUI.matrix;
            var width = Font.Width(text) * scale;
            GUI.matrix = matrix * Matrix4x4.TRS(new Vector3(centerX - width / 2, y, 0), Quaternion.identity, new Vector3(scale, scale, 1));
            Font.Draw(text, 0, 0, color);
            GUI.matrix = matrix;
        }

        /// <summary>Word wraps text to a width in GUI pixels.</summary>
        public string[] Wrap(string text, float width) => Font.Wrap(text, width);

        // ------------------------------------------------------------------ widgets

        /// <summary>Vanilla button (widget/button, 200x20 nine slice). Returns true when clicked.</summary>
        public bool Button(Rect rect, string label, bool enabled = true)
        {
            var hovered = enabled && rect.Contains(Mouse);
            var sprite = !enabled ? "widget/button_disabled" : hovered ? "widget/button_highlighted" : "widget/button";
            Sprite(sprite, rect);
            var color = enabled ? White : Gray;
            TextCentered(label, rect.center.x, rect.y + (rect.height - 8) / 2, color);

            var current = Event.current;
            if (enabled && current.type == EventType.MouseDown && current.button == 0 && hovered)
            {
                current.Use();
                Clicked?.Invoke();
                return true;
            }
            return false;
        }

        /// <summary>Any button was clicked (the client plays ui.button.click).</summary>
        public static event System.Action Clicked;

        private Rect? activeSlider;

        /// <summary>Vanilla slider (widget/slider + slider_handle); returns the new value (0..1) while dragged.</summary>
        public float Slider(Rect rect, string label, float value)
        {
            var current = Event.current;
            var hovered = rect.Contains(Mouse);
            if (current.type == EventType.MouseDown && current.button == 0 && hovered) activeSlider = rect;
            var dragging = activeSlider.HasValue && activeSlider.Value == rect;
            if (dragging && (current.type == EventType.MouseDrag || current.type == EventType.MouseDown))
            {
                value = Mathf.Clamp01((Mouse.x - rect.x - 4) / (rect.width - 8));
                current.Use();
            }
            if (dragging && current.rawType == EventType.MouseUp) activeSlider = null;

            Sprite(hovered || dragging ? "widget/slider_highlighted" : "widget/slider", rect);
            Sprite(hovered || dragging ? "widget/slider_handle_highlighted" : "widget/slider_handle", new Rect(rect.x + Mathf.Round(value * (rect.width - 8)), rect.y, 8, rect.height));
            TextCentered(label, rect.center.x, rect.y + (rect.height - 8) / 2);
            return value;
        }

        public void Dispose()
        {
            Assets.Dispose();
        }
    }

    /// <summary>Vanilla style text field (widget/text_field): click to focus, typing, backspace, paste, Enter.</summary>
    public sealed class GuiTextField
    {
        public string Text = "";
        public int MaxLength = 256;
        public bool Focused;
        public string Placeholder = "";

        /// <summary>On phones and tablets: the system keyboard while the field is focused.</summary>
        private TouchScreenKeyboard keyboard;
        private int keyboardOpened;

        /// <summary>Draws and handles input; returns true when Enter was pressed while focused.</summary>
        public bool Draw(GuiRenderer gui, Rect rect, bool background = true)
        {
            var current = Event.current;
            if (current.type == EventType.MouseDown && current.button == 0) Focused = rect.Contains(gui.Mouse);

            var submitted = false;
            if (TouchScreenKeyboard.isSupported && current.type == EventType.Repaint)
            {
                if (Focused && keyboard == null)
                {
                    keyboard = TouchScreenKeyboard.Open(Text, TouchScreenKeyboardType.Default, false, false, false, false, Placeholder);
                    keyboardOpened = Time.frameCount;
                }
                if (keyboard != null)
                {
                    Text = Limit(keyboard.text ?? "");
                    if (keyboard.status == TouchScreenKeyboard.Status.Done)
                    {
                        submitted = Focused;
                        Focused = false;
                    }
                    // hidden (back button, tapping outside): the field loses its focus; the first frames after opening
                    // the keyboard may not report it visible yet
                    else if (keyboard.status != TouchScreenKeyboard.Status.Visible && Time.frameCount > keyboardOpened + 10) Focused = false;
                    if (!Focused)
                    {
                        if (keyboard.active) keyboard.active = false;
                        keyboard = null;
                    }
                }
            }
            if (Focused && current.type == EventType.KeyDown)
            {
                if (current.keyCode == KeyCode.Return || current.keyCode == KeyCode.KeypadEnter)
                {
                    submitted = true;
                    current.Use();
                }
                else if (current.keyCode == KeyCode.Backspace)
                {
                    if (Text.Length > 0) Text = current.control ? "" : Text.Substring(0, Text.Length - 1);
                    current.Use();
                }
                else if (current.keyCode == KeyCode.V && (current.control || current.command))
                {
                    Text = Limit(Text + GUIUtility.systemCopyBuffer.Replace("\n", " ").Replace("\r", ""));
                    current.Use();
                }
                else if (current.character >= ' ' && current.character != '\u007f' && !current.control)
                {
                    Text = Limit(Text + current.character);
                    current.Use();
                }
                else if (current.character != '\0')
                {
                    current.Use();
                }
            }

            if (background) gui.Sprite(Focused ? "widget/text_field_highlighted" : "widget/text_field", rect);
            var textX = rect.x + 4;
            var textY = rect.y + (rect.height - 8) / 2;
            var visible = Text;
            var maxWidth = rect.width - 10;
            while (visible.Length > 0 && gui.Font.Width(visible) > maxWidth) visible = visible.Substring(1);
            if (Text.Length == 0 && !Focused) gui.Text(Placeholder, textX, textY, GuiRenderer.DarkGray, false);
            else gui.Text(visible, textX, textY, new Color32(0xE0, 0xE0, 0xE0, 0xFF));
            if (Focused && (Environment.TickCount / 300) % 2 == 0) gui.Text("_", textX + gui.Font.Width(visible), textY, new Color32(0xE0, 0xE0, 0xE0, 0xFF));
            return submitted;
        }

        private string Limit(string text) => text.Length > MaxLength ? text.Substring(0, MaxLength) : text;
    }
}
