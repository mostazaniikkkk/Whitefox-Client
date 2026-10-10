// WhiteFox - Unity port of Minosoft (https://gitlab.bixilon.de/bixilon/minosoft), Copyright (C) 2020-2023 Moritz Zwerger
// SPDX-License-Identifier: GPL-3.0-or-later

using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using WhiteFox.Client.Gui;
using WhiteFox.Core.Game;
using WhiteFox.Core.Network;

namespace WhiteFox.Client
{
    /// <summary>
    /// Server driven HUD elements (vanilla Gui: titles, boss bars, scoreboard sidebar, advancement toasts) and the chat
    /// with formatting, clickable links (with a confirmation for URLs) and hover texts.
    /// </summary>
    public sealed partial class Hud
    {
        private static readonly string[] BossColors = { "pink", "blue", "red", "green", "yellow", "purple", "white" };
        private static readonly string[] BossNotches = { null, "notched_6", "notched_10", "notched_12", "notched_20" };

        private string pendingUrl;

        private void DrawServerUi(GameSession session)
        {
            var ui = session.ServerUi;
            DrawBossBars(ui);
            DrawSidebar(session, ui);
            DrawTitle(session, ui);
            DrawToasts(ui);
        }

        /// <summary>Titles: big title and subtitle in the middle, fading in and out (times in ticks).</summary>
        private void DrawTitle(GameSession session, ServerHud ui)
        {
            if (ui.TitleStart < 0 || ui.Title == null) return;
            var age = session.Ticks - ui.TitleStart;
            var total = ui.FadeIn + ui.Stay + ui.FadeOut;
            if (age > total)
            {
                ui.TitleStart = -1;
                return;
            }
            float alpha = 1;
            if (age < ui.FadeIn) alpha = ui.FadeIn > 0 ? (float)age / ui.FadeIn : 1;
            else if (age > ui.FadeIn + ui.Stay) alpha = ui.FadeOut > 0 ? (float)(total - age) / ui.FadeOut : 0;
            alpha = Mathf.Clamp01(alpha);
            if (alpha <= 0.05f) return;
            var center = gui.Width / 2;
            var middle = gui.Height / 2;
            gui.TextScaled(ui.Title, center, middle - 10 * 4, 4, new Color(1, 1, 1, alpha));
            if (!string.IsNullOrEmpty(ui.Subtitle)) gui.TextScaled(ui.Subtitle, center, middle + 5 * 2, 2, new Color(1, 1, 1, alpha));
        }

        /// <summary>Boss bars at the top center (vanilla BossHealthOverlay sprites).</summary>
        private void DrawBossBars(ServerHud ui)
        {
            var y = 12f;
            foreach (var id in ui.BossBarOrder)
            {
                if (!ui.BossBars.TryGetValue(id, out var bar)) continue;
                var x = Mathf.Floor(gui.Width / 2 - 91);
                var color = BossColors[Mathf.Clamp(bar.Color, 0, BossColors.Length - 1)];
                DrawBar("boss_bar/" + color, x, y, 1);
                DrawBar("boss_bar/" + color, x, y, Mathf.Clamp01(bar.Health), true);
                var notches = BossNotches[Mathf.Clamp(bar.Division, 0, BossNotches.Length - 1)];
                if (notches != null)
                {
                    DrawBar("boss_bar/" + notches, x, y, 1);
                    DrawBar("boss_bar/" + notches, x, y, Mathf.Clamp01(bar.Health), true);
                }
                gui.TextCentered(bar.Title, gui.Width / 2, y - 9);
                y += 19;
                if (y >= gui.Height / 3) break;
            }
        }

        private void DrawBar(string sprite, float x, float y, float fraction, bool progress = false)
        {
            var asset = gui.Assets.Sprite(sprite + (progress ? "_progress" : "_background"));
            if (asset == null || fraction <= 0) return;
            if (!gui.Repaint) return;
            GUI.DrawTextureWithTexCoords(new Rect(x, y, 182 * fraction, 5), asset.Texture, new Rect(0, 0, fraction, 1));
        }

        /// <summary>Scoreboard sidebar on the right (vanilla Gui.displayScoreboardSidebar): up to 15 scores, highest first.</summary>
        private void DrawSidebar(GameSession session, ServerHud ui)
        {
            var objective = ui.SidebarFor(session.Server?.Username);
            if (objective == null || !ui.Scores.TryGetValue(objective.Name, out var scores)) return;
            var entries = scores.Values
                .Where(score => !score.Holder.StartsWith("#"))
                .OrderByDescending(score => score.Value)
                .ThenBy(score => score.Holder, StringComparer.OrdinalIgnoreCase)
                .Take(15)
                .ToList();
            var names = new List<string>();
            var values = new List<string>();
            var width = gui.Font.Width(objective.DisplayName);
            foreach (var score in entries)
            {
                var name = score.DisplayName ?? (ui.TeamOf.TryGetValue(score.Holder, out var team) ? team.Decorate(score.Holder) : score.Holder);
                var format = score.NumberFormat >= 0 ? score.NumberFormat : objective.NumberFormat;
                var fixedText = score.NumberFormat >= 0 ? score.FixedFormat : objective.FixedFormat;
                var value = format == 0 ? "" : format == 2 ? fixedText ?? "" : "§c" + score.Value;
                names.Add(name);
                values.Add(value);
                width = Mathf.Max(width, gui.Font.Width(name) + (value.Length > 0 ? gui.Font.Width(": " + LegacyFormatting.Strip(value)) : 0));
            }
            var lines = entries.Count;
            var bottom = gui.Height / 2 + lines * 9 / 3f;
            var right = gui.Width - 3;
            var left = right - width - 2;
            for (var i = 0; i < lines; i++)
            {
                var y = bottom - (lines - i) * 9;
                gui.Fill(new Rect(left - 2, y, width + 4, 9), new Color(0, 0, 0, 0.3f));
                gui.Text(names[i], left, y, Color.white, false);
                gui.TextRight(values[i], right, y, Color.white, false);
            }
            var titleY = bottom - lines * 9 - 10;
            gui.Fill(new Rect(left - 2, titleY - 1, width + 4, 10), new Color(0, 0, 0, 0.4f));
            gui.TextCentered(objective.DisplayName, left + width / 2, titleY, Color.white, false);
        }

        /// <summary>Advancement toasts in the top right corner, sliding in, 5 seconds each.</summary>
        private void DrawToasts(ServerHud ui)
        {
            if (ui.Toasts.Count == 0) return;
            var now = Environment.TickCount;
            var y = 0f;
            for (var i = 0; i < ui.Toasts.Count && i < 5; i++)
            {
                var toast = ui.Toasts[i];
                if (toast.Shown == 0) toast.Shown = now;
                var age = now - toast.Shown;
                if (age > 5000)
                {
                    ui.Toasts.RemoveAt(i);
                    i--;
                    continue;
                }
                var slide = age < 600 ? age / 600f : age > 4400 ? (5000 - age) / 600f : 1;
                slide = 1 - Mathf.Pow(1 - Mathf.Clamp01(slide), 3);
                var x = gui.Width - 160 * slide;
                gui.Sprite("toast/advancement", new Rect(x, y, 160, 32));
                var key = toast.Frame == 1 ? "advancements.toast.challenge" : toast.Frame == 2 ? "advancements.toast.goal" : "advancements.toast.task";
                var header = TextComponent.Language.TryGetValue(key, out var translated) ? translated : "Advancement Made!";
                gui.Text(header, x + 30, y + 7, toast.Frame == 1 ? new Color32(0xFF, 0x88, 0xFF, 0xFF) : new Color32(0xFF, 0xFF, 0x00, 0xFF), false);
                gui.Text(toast.Title, x + 30, y + 18, Color.white, false);
                var icon = toast.Icon?.Item != null ? icons.Get(toast.Icon.Item.Name.Path) : null;
                if (icon != null && gui.Repaint) GUI.DrawTexture(new Rect(x + 8, y + 8, 16, 16), icon);
                y += 32;
            }
        }

        // ------------------------------------------------------------------ chat

        private struct ChatRow
        {
            public string Text;
            public ChatLine Line;
            public int PlainStart;
            public float Alpha;
        }

        private readonly List<ChatRow> chatRows = new List<ChatRow>();

        private void DrawChat(GameSession session, ChatInput chat)
        {
            const float chatWidth = 320;
            var bottom = touchLayout && !chat.Open ? gui.Height - 104 : gui.Height - 40;
            var now = Environment.TickCount;
            chatRows.Clear();
            for (var i = session.Chat.Count - 1; i >= 0 && chatRows.Count < (chat.Open ? 20 : 10); i--)
            {
                var line = session.Chat[i];
                var age = now - line.Received;
                if (!chat.Open && age > 10000) break;
                var alpha = chat.Open ? 1 : Mathf.Clamp01((10000 - age) / 1000f);
                var wrapped = gui.Wrap(line.Text, chatWidth - 4);
                // plain text offset of every wrapped row (to find the clicked link)
                var plain = LegacyFormatting.Strip(line.Text);
                var offsets = new int[wrapped.Length];
                var search = 0;
                for (var w = 0; w < wrapped.Length; w++)
                {
                    var rowPlain = LegacyFormatting.Strip(wrapped[w]);
                    var found = rowPlain.Length == 0 ? search : plain.IndexOf(rowPlain, search, StringComparison.Ordinal);
                    offsets[w] = found < 0 ? search : found;
                    search = offsets[w] + rowPlain.Length;
                }
                for (var w = wrapped.Length - 1; w >= 0; w--) chatRows.Add(new ChatRow { Text = wrapped[w], Line = line, PlainStart = offsets[w], Alpha = alpha });
            }

            TextLink hovered = null;
            var mouse = gui.Mouse;
            for (var i = 0; i < chatRows.Count; i++)
            {
                var row = chatRows[i];
                var y = bottom - (i + 1) * GuiFont.LineHeight;
                gui.Fill(new Rect(2, y, chatWidth, GuiFont.LineHeight), new Color(0, 0, 0, 0.5f * row.Alpha));
                gui.Text(row.Text, 4, y + 1, new Color(1, 1, 1, row.Alpha));
                if (chat.Open && row.Line.Links != null && row.Line.Links.Count > 0 && mouse.y >= y && mouse.y < y + GuiFont.LineHeight && mouse.x >= 4)
                {
                    hovered = LinkAt(row, mouse.x - 4);
                }
            }

            if (hovered != null)
            {
                if (hovered.Hover != null) DrawTooltip(hovered.Hover, mouse);
                var current = Event.current;
                if (current.type == EventType.MouseDown && current.button == 0 && hovered.Action != null)
                {
                    current.Use();
                    OnLinkClicked(session, chat, hovered);
                }
            }

            if (pendingUrl != null) DrawUrlConfirm();

            var message = chat.Draw(gui);
            if (!string.IsNullOrWhiteSpace(message)) session.SendChat(message);
        }

        private TextLink LinkAt(ChatRow row, float x)
        {
            var plain = LegacyFormatting.Strip(row.Text);
            float width = 0;
            for (var c = 0; c < plain.Length; c++)
            {
                var next = width + gui.Font.Width(plain[c].ToString());
                if (x < next)
                {
                    var index = row.PlainStart + c;
                    foreach (var link in row.Line.Links)
                    {
                        if (index >= link.Start && index < link.End) return link;
                    }
                    return null;
                }
                width = next;
            }
            return null;
        }

        /// <summary>Vanilla click events: open_url (after confirmation), run_command, suggest_command, copy_to_clipboard.</summary>
        private void OnLinkClicked(GameSession session, ChatInput chat, TextLink link)
        {
            switch (link.Action)
            {
                case "open_url":
                    if (link.Value != null && (link.Value.StartsWith("http://") || link.Value.StartsWith("https://"))) pendingUrl = link.Value;
                    break;
                case "run_command":
                    if (!string.IsNullOrEmpty(link.Value)) session.SendChat(link.Value);
                    chat.Open = false;
                    break;
                case "suggest_command":
                    chat.Show(link.Value ?? "");
                    break;
                case "copy_to_clipboard":
                    GUIUtility.systemCopyBuffer = link.Value ?? "";
                    break;
            }
        }

        private void DrawUrlConfirm()
        {
            var center = Mathf.Floor(gui.Width / 2);
            var box = new Rect(center - 160, gui.Height / 2 - 45, 320, 90);
            gui.Fill(box, new Color(0, 0, 0, 0.85f));
            gui.TextCentered("Open this link?", center, box.y + 8, GuiRenderer.Yellow);
            gui.TextCentered(pendingUrl.Length > 52 ? pendingUrl.Substring(0, 51) + "…" : pendingUrl, center, box.y + 24, GuiRenderer.Gray);
            gui.TextCentered("Never open links from people you don't trust!", center, box.y + 38, GuiRenderer.Red);
            if (gui.Button(new Rect(center - 155, box.y + 60, 100, 20), "Open"))
            {
                Application.OpenURL(pendingUrl);
                pendingUrl = null;
            }
            else if (gui.Button(new Rect(center - 50, box.y + 60, 100, 20), "Copy"))
            {
                GUIUtility.systemCopyBuffer = pendingUrl;
                pendingUrl = null;
            }
            else if (gui.Button(new Rect(center + 55, box.y + 60, 100, 20), "Cancel"))
            {
                pendingUrl = null;
            }
        }

        /// <summary>Vanilla tooltip box (dark background, purple border) at the mouse.</summary>
        public void DrawTooltip(string text, Vector2 mouse)
        {
            var lines = gui.Wrap(text, 200);
            var width = lines.Max(line => gui.Font.Width(line)) + 6;
            var height = lines.Length * 10 + 4;
            var x = Mathf.Min(mouse.x + 12, gui.Width - width - 2);
            var y = Mathf.Max(2, mouse.y - 12 - height);
            gui.Fill(new Rect(x - 1, y - 1, width + 2, height + 2), new Color32(0x50, 0x00, 0xFF, 0x50));
            gui.Fill(new Rect(x, y, width, height), new Color32(0x10, 0x00, 0x10, 0xF0));
            for (var i = 0; i < lines.Length; i++) gui.Text(lines[i], x + 3, y + 2 + i * 10);
        }
    }
}
