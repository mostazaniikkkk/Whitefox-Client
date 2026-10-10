// WhiteFox - Unity port of Minosoft (https://gitlab.bixilon.de/bixilon/minosoft), Copyright (C) 2020-2023 Moritz Zwerger
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Collections.Generic;
using UnityEngine;
using WhiteFox.Core.Data;
using WhiteFox.Core.Game;
using WhiteFox.Core.Network;
using WhiteFox.Core.Util;

namespace WhiteFox.Client.Gui
{
    /// <summary>
    /// Screens with text the player reads or writes: the sign editor (vanilla SignEditScreen, sends Update Sign), the
    /// written book reader (BookViewScreen) and the book and quill editor with signing (BookEditScreen, sends Edit Book).
    /// </summary>
    public sealed class TextScreens
    {
        private const int PageWidth = 114;
        private const int PageLines = 14;

        private readonly GuiRenderer gui;

        // sign
        private BlockPos? signPosition;
        private bool signFront;
        private readonly string[] signLines = { "", "", "", "" };
        private int signLine;

        // book
        private List<string> pages;
        private bool editing;
        private bool signing;
        private int bookSlot;
        private int page;
        private string title = "";
        private bool changed;

        public bool Open => signPosition.HasValue || pages != null;

        public TextScreens(GuiRenderer gui) => this.gui = gui;

        public void OpenSign(BlockPos position, bool front)
        {
            signPosition = position;
            signFront = front;
            for (var i = 0; i < 4; i++) signLines[i] = "";
            signLine = 0;
        }

        public void OpenBook(ItemStack stack, int slot, bool edit)
        {
            pages = new List<string>();
            if (Nbt.Get(stack.Nbt, "pages") is List<object> list)
            {
                foreach (var entry in list)
                {
                    var text = entry as string ?? "";
                    // written books store JSON text components, books and quills plain text
                    pages.Add(edit ? text : TextComponent.FromJson(text).ToLegacy());
                }
            }
            if (pages.Count == 0) pages.Add("");
            editing = edit;
            signing = false;
            bookSlot = slot;
            page = 0;
            title = "";
            changed = false;
        }

        /// <summary>Draws the open screen; Escape closes it (a sign is sent, an edited book is saved).</summary>
        public void Draw(GameSession session)
        {
            if (signPosition.HasValue) DrawSign(session);
            else if (pages != null) DrawBook(session);
        }

        // ------------------------------------------------------------------ sign

        private void DrawSign(GameSession session)
        {
            gui.Fill(new Rect(0, 0, gui.Width, gui.Height), new Color(0.06f, 0.06f, 0.06f, 0.75f));
            var center = Mathf.Floor(gui.Width / 2);
            gui.TextCentered("Editar el texto del cartel", center, 40);

            // the board (vanilla draws the sign model, here its texture area)
            var board = new Rect(center - 96, 70, 192, 96);
            var sprite = gui.Assets.Get("assets/minecraft/textures/entity/signs/oak.png");
            if (sprite != null && gui.Repaint) GUI.DrawTextureWithTexCoords(board, sprite.Texture, new Rect(2 / 64f, 1 - 14 / 32f, 24 / 64f, 12 / 32f));
            else gui.Fill(board, new Color(0.6f, 0.45f, 0.25f));

            var current = Event.current;
            if (current.type == EventType.KeyDown)
            {
                if (current.keyCode == KeyCode.Escape)
                {
                    FinishSign(session);
                    current.Use();
                    return;
                }
                if (current.keyCode == KeyCode.UpArrow) signLine = (signLine + 3) % 4;
                else if (current.keyCode == KeyCode.DownArrow || current.keyCode == KeyCode.Return || current.keyCode == KeyCode.KeypadEnter || current.keyCode == KeyCode.Tab) signLine = (signLine + 1) % 4;
                else if (current.keyCode == KeyCode.Backspace && signLines[signLine].Length > 0) signLines[signLine] = signLines[signLine].Substring(0, signLines[signLine].Length - 1);
                else if (current.character >= ' ' && current.character != '§')
                {
                    var candidate = signLines[signLine] + current.character;
                    if (gui.Font.Width(candidate) <= 90) signLines[signLine] = candidate;
                }
                current.Use();
            }
            for (var i = 0; i < 4; i++)
            {
                var y = board.y + 20 + i * 15;
                var text = signLines[i];
                if (i == signLine && (int)(Time.unscaledTime * 2) % 2 == 0) text += "_";
                gui.TextScaled(text, center, y, 1.5f, Color.black);
            }
            if (gui.Button(new Rect(center - 100, gui.Height / 4 * 3 + 10, 200, 20), "Listo")) FinishSign(session);
        }

        private void FinishSign(GameSession session)
        {
            if (signPosition.HasValue) session.Server?.SendUpdateSign(signPosition.Value, signFront, signLines);
            signPosition = null;
        }

        // ------------------------------------------------------------------ book

        private void DrawBook(GameSession session)
        {
            var x = Mathf.Floor((gui.Width - 192) / 2);
            const float y = 2;
            var book = gui.Assets.Get("assets/minecraft/textures/gui/book.png");
            if (book != null && gui.Repaint) GUI.DrawTextureWithTexCoords(new Rect(x, y, 192, 192), book.Texture, new Rect(0, 1 - 192f / book.Height, 192f / book.Width, 192f / book.Height));

            var current = Event.current;
            if (current.type == EventType.KeyDown && current.keyCode == KeyCode.Escape)
            {
                CloseBook(session, null);
                current.Use();
                return;
            }

            if (signing)
            {
                DrawSigning(session, x, y, current);
                return;
            }

            if (editing && current.type == EventType.KeyDown)
            {
                var text = pages[page];
                if (current.keyCode == KeyCode.Backspace)
                {
                    if (text.Length > 0) pages[page] = text.Substring(0, text.Length - 1);
                }
                else if (current.keyCode == KeyCode.Return || current.keyCode == KeyCode.KeypadEnter)
                {
                    TryAppend(text + "\n");
                }
                else if (current.character >= ' ' && current.character != '§')
                {
                    TryAppend(text + current.character);
                }
                if (current.keyCode == KeyCode.PageUp && page > 0) page--;
                if (current.keyCode == KeyCode.PageDown && page < pages.Count - 1) page++;
                current.Use();
            }

            var pageLabel = "Página " + (page + 1) + " de " + pages.Count;
            gui.TextRight(pageLabel, x + 192 - 44, y + 18, Color.black, false);
            var lines = gui.Wrap(pages[page], PageWidth);
            for (var i = 0; i < lines.Length && i < PageLines; i++) gui.Text(lines[i], x + 36, y + 32 + i * 9, Color.black, false);
            if (editing && (int)(Time.unscaledTime * 2) % 2 == 0)
            {
                var last = lines.Length > 0 ? lines[lines.Length - 1] : "";
                var row = Mathf.Min(lines.Length - 1, PageLines - 1);
                gui.Text("_", x + 36 + gui.Font.Width(last), y + 32 + Mathf.Max(0, row) * 9, Color.black, false);
            }

            // page turning (vanilla adds a new page when moving past the last one while editing)
            if (page < pages.Count - 1 || (editing && pages.Count < 100))
            {
                if (SpriteButton("widget/page_forward", new Rect(x + 116, y + 159, 23, 13)))
                {
                    if (page == pages.Count - 1) pages.Add("");
                    page++;
                }
            }
            if (page > 0 && SpriteButton("widget/page_backward", new Rect(x + 43, y + 159, 23, 13))) page--;

            if (editing)
            {
                if (gui.Button(new Rect(gui.Width / 2 - 100, 196, 98, 20), "Firmar")) signing = true;
                if (gui.Button(new Rect(gui.Width / 2 + 2, 196, 98, 20), "Listo")) CloseBook(session, null);
            }
            else if (gui.Button(new Rect(gui.Width / 2 - 100, 196, 200, 20), "Listo"))
            {
                CloseBook(session, null);
            }
        }

        private void TryAppend(string text)
        {
            if (gui.Wrap(text, PageWidth).Length > PageLines || text.Length > 1024) return;
            pages[page] = text;
            changed = true;
        }

        private void DrawSigning(GameSession session, float x, float y, Event current)
        {
            if (current.type == EventType.KeyDown)
            {
                if (current.keyCode == KeyCode.Backspace && title.Length > 0) title = title.Substring(0, title.Length - 1);
                else if (current.character >= ' ' && current.character != '§' && title.Length < 32) title += current.character;
                current.Use();
            }
            gui.TextCentered("Escribe el título:", x + 96, y + 34, Color.black, false);
            gui.TextCentered(title + ((int)(Time.unscaledTime * 2) % 2 == 0 ? "_" : ""), x + 96, y + 50, Color.black, false);
            gui.TextCentered("de " + (session.Server?.Username ?? "?"), x + 96, y + 60, GuiRenderer.DarkGray, false);
            gui.TextCentered("Una vez firmado, el libro", x + 96, y + 82, Color.black, false);
            gui.TextCentered("ya no se podrá editar.", x + 96, y + 91, Color.black, false);
            if (gui.Button(new Rect(gui.Width / 2 - 100, 196, 98, 20), "Firmar y cerrar", title.Trim().Length > 0)) CloseBook(session, title.Trim());
            if (gui.Button(new Rect(gui.Width / 2 + 2, 196, 98, 20), "Cancelar")) signing = false;
        }

        private void CloseBook(GameSession session, string signTitle)
        {
            if (editing && (changed || signTitle != null))
            {
                // trailing empty pages are dropped like vanilla
                while (pages.Count > 1 && pages[pages.Count - 1].Length == 0) pages.RemoveAt(pages.Count - 1);
                session.Server?.SendEditBook(bookSlot, pages, signTitle);
            }
            pages = null;
            signing = false;
        }

        private bool SpriteButton(string sprite, Rect rect)
        {
            var hovered = rect.Contains(gui.Mouse);
            gui.Sprite(sprite + (hovered ? "_highlighted" : ""), rect);
            var current = Event.current;
            if (!hovered || current.type != EventType.MouseDown || current.button != 0) return false;
            current.Use();
            return true;
        }
    }
}
