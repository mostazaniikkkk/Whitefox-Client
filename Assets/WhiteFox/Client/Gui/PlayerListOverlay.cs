// WhiteFox - Unity port of Minosoft (https://gitlab.bixilon.de/bixilon/minosoft), Copyright (C) 2020-2023 Moritz Zwerger
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using WhiteFox.Core.Entities;
using WhiteFox.Core.Game;

namespace WhiteFox.Client.Gui
{
    /// <summary>Tab list (vanilla PlayerTabOverlay): listed players sorted by gamemode and name, with the ping icon.</summary>
    public static class PlayerListOverlay
    {
        private const int RowHeight = 9;
        private const int MaxRows = 20;

        public static void Draw(GuiRenderer gui, GameSession session)
        {
            var players = session.Entities.Players.Values
                .Where(player => player.Listed)
                .OrderBy(player => player.Gamemode == 3 ? 1 : 0)
                .ThenBy(player => player.Name, System.StringComparer.OrdinalIgnoreCase)
                .Take(80)
                .ToList();
            if (players.Count == 0) return;
            var ui = session.ServerUi;
            var objective = ui.DisplayObjective(0);
            string Name(PlayerInfo player) => player.DisplayName ?? (player.Name != null && ui.TeamOf.TryGetValue(player.Name, out var team) ? team.Decorate(player.Name) : player.Name ?? "?");
            string ScoreText(PlayerInfo player)
            {
                if (objective == null || player.Name == null) return null;
                var score = ui.GetScore(objective.Name, player.Name);
                return score == null ? null : "§e" + score.Value;
            }

            var columns = (players.Count + MaxRows - 1) / MaxRows;
            var rows = (players.Count + columns - 1) / columns;
            var scoreWidth = objective == null ? 0 : players.Max(player => ScoreText(player) is string text ? gui.Font.Width(text) + 4 : 0);
            var nameWidth = players.Max(player => gui.Font.Width(Name(player)));
            var columnWidth = Mathf.Min(nameWidth + 13 + scoreWidth, 150 * 4);
            var header = string.IsNullOrEmpty(ui.TabHeader) ? new string[0] : gui.Wrap(ui.TabHeader, gui.Width - 50);
            var footer = string.IsNullOrEmpty(ui.TabFooter) ? new string[0] : gui.Wrap(ui.TabFooter, gui.Width - 50);
            var width = Mathf.Max(columns * columnWidth + (columns - 1) * 5, Mathf.Max(header.Length > 0 ? header.Max(line => gui.Font.Width(line)) : 0, footer.Length > 0 ? footer.Max(line => gui.Font.Width(line)) : 0));
            var x = Mathf.Floor(gui.Width / 2 - width / 2);
            var y = 10f + header.Length * 9;

            gui.Fill(new Rect(x - 1, 10 - 1, width + 2, rows * RowHeight + 1 + (header.Length + footer.Length) * 9), new Color(0, 0, 0, 0.5f));
            for (var i = 0; i < header.Length; i++) gui.TextCentered(header[i], gui.Width / 2, 10 + i * 9);
            for (var i = 0; i < footer.Length; i++) gui.TextCentered(footer[i], gui.Width / 2, y + rows * RowHeight + 1 + i * 9);
            for (var i = 0; i < players.Count; i++)
            {
                var player = players[i];
                var column = i / rows;
                var row = i % rows;
                var left = x + column * (columnWidth + 5);
                var top = y + row * RowHeight;
                gui.Fill(new Rect(left, top, columnWidth, 8), new Color(1, 1, 1, 0.125f));
                gui.Text(Name(player), left, top, player.Gamemode == 3 ? new Color(1, 1, 1, 0.6f) : Color.white);
                if (ScoreText(player) is string scoreText) gui.TextRight(scoreText, left + columnWidth - 13, top);
                gui.Sprite(PingIcon(player.Latency), new Rect(left + columnWidth - 11, top, 10, 8));
            }
        }

        private static string PingIcon(int latency) =>
            latency < 0 ? "icon/ping_unknown" : latency < 150 ? "icon/ping_5" : latency < 300 ? "icon/ping_4" : latency < 600 ? "icon/ping_3" : latency < 1000 ? "icon/ping_2" : "icon/ping_1";
    }
}
