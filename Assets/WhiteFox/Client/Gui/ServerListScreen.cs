// WhiteFox - Unity port of Minosoft (https://gitlab.bixilon.de/bixilon/minosoft), Copyright (C) 2020-2023 Moritz Zwerger
// SPDX-License-Identifier: GPL-3.0-or-later

using System;
using System.Collections.Generic;
using UnityEngine;
using WhiteFox.Core.Network;

namespace WhiteFox.Client.Gui
{
    /// <summary>
    /// Vanilla's multiplayer screen: the saved servers with their icon, MOTD, player count and ping, and the screens to
    /// add, edit and delete a server or connect to an address directly. The list is saved in config.toml.
    /// </summary>
    public sealed class ServerListScreen
    {
        public enum Result
        {
            None,
            Join,
            Back,
        }

        private enum Page
        {
            List,
            Edit,
            Direct,
            Delete,
        }

        /// <summary>The live status of a saved server (not saved).</summary>
        private sealed class Status
        {
            public volatile bool Pinging;
            public volatile ServerStatus Result;
            public volatile string Error;
            public Texture2D Icon;
            public bool IconLoaded;
        }

        private const float RowHeight = 36;
        private const float ListWidth = 305;

        private readonly Settings settings;
        private readonly Dictionary<ServerEntry, Status> statuses = new Dictionary<ServerEntry, Status>();
        private readonly GuiTextField nameField = new GuiTextField { MaxLength = 64, Placeholder = "Minecraft Server" };
        private readonly GuiTextField addressField = new GuiTextField { MaxLength = 255, Placeholder = "localhost" };
        private readonly GuiTextField directField = new GuiTextField { MaxLength = 255, Placeholder = "localhost" };
        private Page page = Page.List;
        private int selected = -1;
        private ServerEntry editing; // null while adding a new server
        private float scroll;
        private bool dragging;
        private float lastClickTime;
        private int lastClickIndex = -1;

        /// <summary>The address to join after <see cref="Result.Join"/>.</summary>
        public string JoinAddress { get; private set; }

        public ServerListScreen(Settings settings)
        {
            this.settings = settings;
        }

        /// <summary>Opens the list and pings every server, like vanilla.</summary>
        public void Open()
        {
            page = Page.List;
            Refresh();
        }

        /// <summary>Esc: back one page. Returns false on the list itself (the caller goes back to the title).</summary>
        public bool Back()
        {
            if (page == Page.List) return false;
            page = Page.List;
            return true;
        }

        public Result Draw(GuiRenderer gui)
        {
            MessageScreen.DirtBackground(gui);
            switch (page)
            {
                case Page.Edit: return DrawEdit(gui);
                case Page.Direct: return DrawDirect(gui);
                case Page.Delete: return DrawDelete(gui);
                default: return DrawList(gui);
            }
        }

        // ------------------------------------------------------------------ list

        private Result DrawList(GuiRenderer gui)
        {
            var servers = settings.Servers;
            if (selected >= servers.Count) selected = servers.Count - 1;
            var center = Mathf.Floor(gui.Width / 2);
            gui.TextCentered("Play Multiplayer", center, 12);
            var top = 32f;
            var bottom = gui.Height - 64;
            var listHeight = bottom - top;
            var left = center - ListWidth / 2;
            gui.Fill(new Rect(0, top, gui.Width, listHeight), new Color(0, 0, 0, 0.5f));

            // scrolling: the wheel, or dragging the list (touch screens)
            var maxScroll = Mathf.Max(0, servers.Count * RowHeight - listHeight + 4);
            var current = Event.current;
            var listRect = new Rect(0, top, gui.Width, listHeight);
            if (current.type == EventType.ScrollWheel && listRect.Contains(gui.Mouse))
            {
                scroll += current.delta.y * 6;
                current.Use();
            }
            if (current.type == EventType.MouseDown && listRect.Contains(gui.Mouse)) dragging = false;
            if (current.type == EventType.MouseDrag && listRect.Contains(gui.Mouse) && Mathf.Abs(current.delta.y) > 0)
            {
                scroll -= current.delta.y;
                dragging = true;
                current.Use();
            }
            scroll = Mathf.Clamp(scroll, 0, maxScroll);

            Result result = Result.None;
            for (var i = 0; i < servers.Count; i++)
            {
                var y = top + 2 + i * RowHeight - scroll;
                if (y < top || y + RowHeight - 4 > bottom) continue; // only whole rows (no clipping)
                var row = new Rect(left, y, ListWidth, RowHeight - 4);
                if (i == selected)
                {
                    gui.Fill(new Rect(row.x - 2, row.y - 2, row.width + 4, row.height + 4), new Color(0.5f, 0.5f, 0.5f, 1));
                    gui.Fill(new Rect(row.x - 1, row.y - 1, row.width + 2, row.height + 2), Color.black);
                }
                DrawEntry(gui, servers[i], row);
                if (current.type == EventType.MouseUp && current.button == 0 && row.Contains(gui.Mouse) && !dragging)
                {
                    // a second click on the same row joins it (vanilla double click)
                    var now = Time.realtimeSinceStartup;
                    if (lastClickIndex == i && now - lastClickTime < 0.4f) result = Join(servers[i].Address);
                    lastClickIndex = i;
                    lastClickTime = now;
                    selected = i;
                    current.Use();
                }
            }
            if (servers.Count == 0) gui.TextCentered("No servers yet: add one or connect directly.", center, top + 20, GuiRenderer.Gray);

            var hasSelection = selected >= 0 && selected < servers.Count;
            var y1 = gui.Height - 52;
            if (gui.Button(new Rect(center - 154, y1, 100, 20), "Join Server", hasSelection)) result = Join(servers[selected].Address);
            if (gui.Button(new Rect(center - 50, y1, 100, 20), "Direct Connect"))
            {
                directField.Text = settings.Address ?? "";
                directField.Focused = true;
                page = Page.Direct;
            }
            if (gui.Button(new Rect(center + 54, y1, 100, 20), "Add Server"))
            {
                editing = null;
                nameField.Text = "Minecraft Server";
                addressField.Text = "";
                addressField.Focused = true;
                page = Page.Edit;
            }
            var y2 = gui.Height - 28;
            if (gui.Button(new Rect(center - 154, y2, 74, 20), "Edit", hasSelection))
            {
                editing = servers[selected];
                nameField.Text = editing.Name;
                addressField.Text = editing.Address;
                page = Page.Edit;
            }
            if (gui.Button(new Rect(center - 76, y2, 74, 20), "Delete", hasSelection)) page = Page.Delete;
            if (gui.Button(new Rect(center + 2, y2, 74, 20), "Refresh")) Refresh();
            if (gui.Button(new Rect(center + 80, y2, 74, 20), "Back")) result = Result.Back;
            return result;
        }

        private void DrawEntry(GuiRenderer gui, ServerEntry server, Rect row)
        {
            var status = StatusOf(server);
            // icon: the server's favicon, else vanilla's unknown server picture
            if (!status.IconLoaded && status.Result != null)
            {
                status.IconLoaded = true;
                if (status.Result.Favicon != null)
                {
                    var texture = new Texture2D(2, 2, TextureFormat.RGBA32, false) { filterMode = FilterMode.Bilinear, name = "Server icon" };
                    if (texture.LoadImage(status.Result.Favicon)) status.Icon = texture;
                    else UnityEngine.Object.Destroy(texture);
                }
            }
            var iconRect = new Rect(row.x, row.y, 32, 32);
            if (status.Icon != null)
            {
                if (gui.Repaint) GUI.DrawTexture(iconRect, status.Icon);
            }
            else
            {
                gui.Sprite(gui.Assets.Get("assets/minecraft/textures/misc/unknown_server.png"), iconRect);
            }

            gui.Text(server.Name, row.x + 35, row.y + 1);
            string motd;
            Color motdColor = GuiRenderer.Gray;
            if (status.Pinging) motd = "Pinging...";
            else if (status.Error != null)
            {
                motd = "Can't connect to server";
                motdColor = new Color32(0xAA, 0x00, 0x00, 0xFF);
            }
            else motd = status.Result?.FormattedMotd ?? "";
            var lines = gui.Wrap(motd, ListWidth - 38);
            for (var line = 0; line < lines.Length && line < 2; line++) gui.Text(lines[line], row.x + 35, row.y + 12 + line * 9, motdColor);

            // ping bars and players at the right
            var result = status.Result;
            string pingSprite;
            if (status.Pinging) pingSprite = "server_list/pinging_" + (1 + (int)(Time.realtimeSinceStartup * 8) % 5);
            else if (status.Error != null || result == null) pingSprite = "server_list/unreachable";
            else if (!result.Compatible) pingSprite = "server_list/incompatible";
            else
            {
                var ms = result.PingMilliseconds;
                pingSprite = "server_list/ping_" + (ms < 150 ? 5 : ms < 300 ? 4 : ms < 600 ? 3 : ms < 1000 ? 2 : 1);
            }
            var pingRect = new Rect(row.xMax - 15, row.y, 10, 8);
            gui.Sprite(pingSprite, pingRect);
            if (result != null && status.Error == null)
            {
                var players = result.Compatible ? $"{result.OnlinePlayers}/{result.MaxPlayers}" : "§c" + result.VersionName;
                gui.TextRight(players, pingRect.x - 2, row.y + 1, GuiRenderer.Gray);
            }
        }

        private Status StatusOf(ServerEntry server)
        {
            if (!statuses.TryGetValue(server, out var status)) statuses[server] = status = new Status();
            return status;
        }

        private void Refresh()
        {
            foreach (var status in statuses.Values)
            {
                if (status.Icon != null) UnityEngine.Object.Destroy(status.Icon);
            }
            statuses.Clear();
            foreach (var server in settings.Servers) Ping(server);
        }

        private async void Ping(ServerEntry server)
        {
            var status = StatusOf(server);
            status.Pinging = true;
            try
            {
                var (host, port) = await ServerAddress.ResolveAsync(server.Address);
                status.Result = await ServerStatus.PingAsync(host, port);
            }
            catch (Exception exception)
            {
                status.Error = exception.Message;
            }
            status.Pinging = false;
        }

        private Result Join(string address)
        {
            JoinAddress = address;
            return Result.Join;
        }

        // ------------------------------------------------------------------ add / edit, direct connect, delete

        private Result DrawEdit(GuiRenderer gui)
        {
            var center = Mathf.Floor(gui.Width / 2);
            var y = Mathf.Floor(gui.Height / 4);
            gui.TextCentered(editing == null ? "Add Server" : "Edit Server Info", center, 17);
            gui.Text("Server Name", center - 100, y, GuiRenderer.Gray);
            nameField.Draw(gui, new Rect(center - 100, y + 10, 200, 20));
            gui.Text("Server Address", center - 100, y + 36, GuiRenderer.Gray);
            var submit = addressField.Draw(gui, new Rect(center - 100, y + 46, 200, 20));
            var valid = !string.IsNullOrWhiteSpace(addressField.Text);
            if (gui.Button(new Rect(center - 100, y + 96, 200, 20), "Done", valid) || submit && valid)
            {
                var name = string.IsNullOrWhiteSpace(nameField.Text) ? nameField.Placeholder : nameField.Text.Trim();
                if (editing == null)
                {
                    editing = new ServerEntry();
                    settings.Servers.Add(editing);
                    selected = settings.Servers.Count - 1;
                }
                editing.Name = name;
                editing.Address = addressField.Text.Trim();
                settings.Save();
                statuses.Remove(editing);
                Ping(editing);
                page = Page.List;
            }
            if (gui.Button(new Rect(center - 100, y + 120, 200, 20), "Cancel")) page = Page.List;
            return Result.None;
        }

        private Result DrawDirect(GuiRenderer gui)
        {
            var center = Mathf.Floor(gui.Width / 2);
            var y = Mathf.Floor(gui.Height / 4);
            gui.TextCentered("Direct Connection", center, 17);
            gui.Text("Server Address", center - 100, y + 36, GuiRenderer.Gray);
            var submit = directField.Draw(gui, new Rect(center - 100, y + 46, 200, 20));
            var valid = !string.IsNullOrWhiteSpace(directField.Text);
            var result = Result.None;
            if (gui.Button(new Rect(center - 100, y + 96, 200, 20), "Join Server", valid) || submit && valid)
            {
                // vanilla remembers the last direct address
                settings.Address = directField.Text.Trim();
                settings.Save();
                page = Page.List;
                result = Join(settings.Address);
            }
            if (gui.Button(new Rect(center - 100, y + 120, 200, 20), "Cancel")) page = Page.List;
            return result;
        }

        private Result DrawDelete(GuiRenderer gui)
        {
            var servers = settings.Servers;
            if (selected < 0 || selected >= servers.Count)
            {
                page = Page.List;
                return Result.None;
            }
            var center = Mathf.Floor(gui.Width / 2);
            var y = Mathf.Floor(gui.Height / 2 - 40);
            gui.TextCentered("Are you sure you want to remove this server?", center, y);
            gui.TextCentered("'" + servers[selected].Name + "' will be lost forever! (A long time!)", center, y + 20, GuiRenderer.Gray);
            if (gui.Button(new Rect(center - 154, y + 56, 150, 20), "Delete"))
            {
                statuses.Remove(servers[selected]);
                servers.RemoveAt(selected);
                selected = -1;
                settings.Save();
                page = Page.List;
            }
            if (gui.Button(new Rect(center + 4, y + 56, 150, 20), "Cancel")) page = Page.List;
            return Result.None;
        }
    }
}
