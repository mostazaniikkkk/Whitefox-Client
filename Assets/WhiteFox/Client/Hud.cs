// WhiteFox - Unity port of Minosoft (https://gitlab.bixilon.de/bixilon/minosoft), Copyright (C) 2020-2023 Moritz Zwerger
// SPDX-License-Identifier: GPL-3.0-or-later

using System;
using System.Collections.Generic;
using UnityEngine;
using WhiteFox.Client.Gui;
using WhiteFox.Core.Game;
using WhiteFox.Core.Network;
using WhiteFox.Core.Physics;
using WhiteFox.Core.Util;

namespace WhiteFox.Client
{
    /// <summary>Chat input line (Minosoft's ChatElement): opened with T or /, Enter sends, Esc closes.</summary>
    public sealed class ChatInput
    {
        public readonly GuiTextField Field = new GuiTextField { MaxLength = 256 };
        public bool Open;
        private int openedFrame = -1;

        public void Show(string start)
        {
            Open = true;
            Field.Text = start;
            Field.Focused = true;
            openedFrame = Time.frameCount;
        }

        /// <summary>Draws the input line at the bottom; returns the text to send when Enter was pressed.</summary>
        public string Draw(GuiRenderer gui)
        {
            if (!Open) return null;
            var current = Event.current;
            // the key that opened the chat must not end up in the text
            if (Time.frameCount == openedFrame && current.type == EventType.KeyDown && current.character != '\0') current.Use();
            if (current.type == EventType.KeyDown && current.keyCode == KeyCode.Escape)
            {
                Open = false;
                current.Use();
                return null;
            }
            var rect = new Rect(2, gui.Height - 14, gui.Width - 4, 12);
            gui.Fill(rect, new Color(0, 0, 0, 0.5f));
            Field.Focused = true;
            if (!Field.Draw(gui, new Rect(rect.x - 2, rect.y, rect.width, rect.height), false)) return null;
            Open = false;
            return Field.Text;
        }
    }

    /// <summary>
    /// In game HUD with the vanilla sprites and font (Minosoft's HUDManager: hotbar, hearts, food, crosshair, chat,
    /// action bar, item name, F3 debug screen).
    /// </summary>
    public sealed partial class Hud
    {
        private readonly GuiRenderer gui;
        private readonly ItemIcons icons;
        private readonly WhiteFox.Core.Data.ItemRegistry items;
        private int lastSlot = -1;
        private string lastItem;
        private int itemNameShown;

        public Hud(GuiRenderer gui, ItemIcons icons, WhiteFox.Core.Data.ItemRegistry items)
        {
            this.gui = gui;
            this.icons = icons;
            this.items = items;
        }

        public void Draw(GameSession session, WhiteFoxClient client, PlayerInputReader input, ChatInput chat, float fps)
        {
            var width = gui.Width;
            var height = gui.Height;
            var player = session.Player;

            // crosshair
            if (input.Perspective == 0) gui.Sprite("hud/crosshair", new Rect(Mathf.Floor(width / 2 - 7), Mathf.Floor(height / 2 - 7), 15, 15), new Color(1, 1, 1, 0.9f));

            // hotbar (182x22) and selection (24x23)
            var hotbarX = Mathf.Floor(width / 2 - 91);
            var hotbarY = height - 22;
            gui.Sprite("hud/hotbar", new Rect(hotbarX, hotbarY, 182, 22));
            gui.Sprite("hud/hotbar_selection", new Rect(hotbarX - 1 + session.SelectedSlot * 20, hotbarY - 1, 24, 23));
            for (var slot = 0; slot < 9; slot++)
            {
                var item = session.HotbarItems[slot];
                if (item == null) continue;
                var x = hotbarX + 3 + slot * 20;
                var y = hotbarY + 3;
                var stack = session.Inventory.Player.Slots[36 + slot];
                var icon = icons.Get(stack) ?? icons.Get(item);
                if (icon != null && gui.Repaint)
                {
                    GUI.DrawTexture(new Rect(x, y, 16, 16), icon);
                    if (Core.Data.ItemProperties.HasFoil(stack)) icons.DrawGlint(new Rect(x, y, 16, 16), icon);
                }
                else if (icon == null) gui.TextCentered(item.Substring(0, Math.Min(2, item.Length)), x + 8, y + 4);
                var count = session.HotbarCounts[slot];
                if (count > 1) gui.TextRight(count.ToString(), x + 17, y + 9);
            }

            // name of the selected item, shown for 2 seconds after it changes
            var selected = session.HotbarItems[session.SelectedSlot];
            if (session.SelectedSlot != lastSlot || selected != lastItem)
            {
                lastSlot = session.SelectedSlot;
                lastItem = selected;
                itemNameShown = Environment.TickCount;
            }
            var nameAge = Environment.TickCount - itemNameShown;
            if (selected != null && nameAge < 2000)
            {
                var name = items.Get(ResourceLocation.Minecraft(selected))?.DisplayName ?? selected;
                var alpha = Mathf.Clamp01((2000 - nameAge) / 500f);
                gui.TextCentered(name, width / 2, height - 59, new Color(1, 1, 1, alpha));
            }

            // off hand (left of the hotbar)
            var offhand = session.Inventory.Player.Slots[45];
            if (offhand != null)
            {
                gui.Sprite("hud/hotbar_offhand_left", new Rect(hotbarX - 29, height - 23, 29, 24));
                var icon = icons.Get(offhand);
                if (icon != null && gui.Repaint)
                {
                    GUI.DrawTexture(new Rect(hotbarX - 26, height - 19, 16, 16), icon);
                    if (Core.Data.ItemProperties.HasFoil(offhand)) icons.DrawGlint(new Rect(hotbarX - 26, height - 19, 16, 16), icon);
                }
                else if (icon == null) gui.TextCentered(offhand.Item.Name.Path.Substring(0, Mathf.Min(2, offhand.Item.Name.Path.Length)), hotbarX - 18, height - 15);
                if (offhand.Count > 1) gui.TextRight(offhand.Count.ToString(), hotbarX - 9, height - 10);
            }

            DrawEffects(player);

            // hearts and food (survival / adventure)
            if (player.Gamemode == Gamemode.Survival || player.Gamemode == Gamemode.Adventure)
            {
                // experience bar and level, or the jump bar while riding a horse
                var jump = session.RidingJumpable;
                gui.Sprite(jump ? "hud/jump_bar_background" : "hud/experience_bar_background", new Rect(hotbarX, height - 29, 182, 5));
                var progress = Mathf.Clamp01(session.ExperienceBar);
                if (jump)
                {
                    var charge = Mathf.Clamp01(session.HorseJump);
                    var bar = gui.Assets.Sprite("hud/jump_bar_progress");
                    if (bar != null && gui.Repaint && charge > 0) GUI.DrawTextureWithTexCoords(new Rect(hotbarX, height - 29, 182 * charge, 5), bar.Texture, new Rect(0, 0, charge, 1));
                }
                else if (progress > 0)
                {
                    var bar = gui.Assets.Sprite("hud/experience_bar_progress");
                    if (bar != null && gui.Repaint) GUI.DrawTextureWithTexCoords(new Rect(hotbarX, height - 29, 182 * progress, 5), bar.Texture, new Rect(0, 0, progress, 1));
                }
                if (session.ExperienceLevel > 0)
                {
                    var level = session.ExperienceLevel.ToString();
                    var x = Mathf.Floor(width / 2 - gui.Font.Width(level) / 2);
                    var y = height - 35;
                    foreach (var offset in new[] { new Vector2(1, 0), new Vector2(-1, 0), new Vector2(0, 1), new Vector2(0, -1) }) gui.Text(level, x + offset.x, y + offset.y, Color.black, false);
                    gui.Text(level, x, y, new Color32(0x80, 0xFF, 0x20, 0xFF), false);
                }

                // armor (above the hearts)
                var armor = session.ArmorPoints;
                if (armor > 0)
                {
                    for (var i = 0; i < 10; i++)
                    {
                        var rect = new Rect(hotbarX + i * 8, height - 49, 9, 9);
                        gui.Sprite(armor >= i * 2 + 2 ? "hud/armor_full" : armor == i * 2 + 1 ? "hud/armor_half" : "hud/armor_empty", rect);
                    }
                }

                // air bubbles (above the food) while under water or refilling
                if (player.EyeInWater || session.Air < 300)
                {
                    var air = Mathf.Max(0, session.Air);
                    var full = Mathf.CeilToInt((air - 2) * 10f / 300f);
                    var popping = Mathf.CeilToInt(air * 10f / 300f) - full;
                    for (var i = 0; i < full + popping && i < 10; i++)
                    {
                        gui.Sprite(i < full ? "hud/air" : "hud/air_bursting", new Rect(hotbarX + 182 - 9 - i * 8, height - 49, 9, 9));
                    }
                }
                var health = Mathf.CeilToInt(session.Health);
                for (var i = 0; i < 10; i++)
                {
                    var rect = new Rect(hotbarX + i * 8, height - 39, 9, 9);
                    gui.Sprite("hud/heart/container", rect);
                    if (health >= i * 2 + 2) gui.Sprite("hud/heart/full", rect);
                    else if (health == i * 2 + 1) gui.Sprite("hud/heart/half", rect);
                }
                for (var i = 0; i < 10; i++)
                {
                    var rect = new Rect(hotbarX + 182 - 9 - i * 8, height - 39, 9, 9);
                    gui.Sprite("hud/food_empty", rect);
                    if (session.Food >= i * 2 + 2) gui.Sprite("hud/food_full", rect);
                    else if (session.Food == i * 2 + 1) gui.Sprite("hud/food_half", rect);
                }
            }

            // survival digging progress under the crosshair
            if (session.DiggingProgress > 0)
            {
                var bar = new Rect(Mathf.Floor(width / 2 - 15), Mathf.Floor(height / 2 + 9), 30, 2);
                gui.Fill(bar, new Color(0, 0, 0, 0.6f));
                gui.Fill(new Rect(bar.x, bar.y, bar.width * Mathf.Clamp01(session.DiggingProgress), bar.height), Color.white);
            }

            // action bar
            if (session.ActionBar != null && Environment.TickCount - session.ActionBarReceived < 3000)
            {
                gui.TextCentered(session.ActionBar, width / 2, height - 68);
            }

            DrawServerUi(session);
            DrawChat(session, chat);
            if (input.DebugScreen) DrawDebug(session, client, fps);
        }

        /// <summary>Active status effects in the top right corner (vanilla effect icons on their background).</summary>
        private void DrawEffects(LocalPlayer player)
        {
            var index = 0;
            foreach (var effect in player.Effects.Values)
            {
                var name = effect.Id >= 0 && effect.Id < Effects.Names.Length ? Effects.Names[effect.Id] : null;
                if (name == null) continue;
                var x = gui.Width - 25 * (index + 1);
                gui.Sprite("hud/effect_background", new Rect(x, 1, 24, 24));
                var icon = gui.Assets.Get("assets/minecraft/textures/mob_effect/" + name + ".png");
                // blink during the last 10 seconds, like vanilla
                var alpha = effect.Duration >= 0 && effect.Duration < 200 && (effect.Duration / 10) % 2 == 0 ? 0.4f : 1.0f;
                gui.Sprite(icon, new Rect(x + 3, 4, 18, 18), new Color(1, 1, 1, alpha));
                index++;
            }
        }

        private void DrawDebug(GameSession session, WhiteFoxClient client, float fps)
        {
            var player = session.Player;
            var position = player.Position;
            var block = BlockPos.Floor(position);
            var server = session.Server;
            var light = session.World.GetLight(block.X, block.Y, block.Z);
            var left = new List<string>
            {
                "WhiteFox 2.0 (" + Protocol.VersionName + ")",
                $"{fps:0} fps, graphics: {(client.Modern ? "modern" : "classic")}",
                server == null ? "Local test world" : $"{server.Host}:{server.Port} as {server.Username}, {server.State}",
                server == null ? null : $"Network: {server.BytesReceived / 1024} KiB in, {server.BytesSent / 1024} KiB out, {server.PendingPackets} queued",
                $"C: {client.Renderer.VisibleCount}/{client.Renderer.SectionCount} sections{(client.Renderer.CullingEnabled ? "" : " (no culling)")}, queue {client.Renderer.QueueSize} (+{client.Renderer.RunningCount}), {session.World.ChunkCount} chunks",
                client.DebugInfo,
                "",
                $"XYZ: {position.X:0.000} / {position.Y:0.00000} / {position.Z:0.000}",
                $"Block: {block.X} {block.Y} {block.Z}",
                $"Chunk: {block.X & 15} {block.Y & 15} {block.Z & 15} in {block.X >> 4} {block.Y >> 4} {block.Z >> 4}",
                $"Facing: {Placement.HorizontalFacing(player.Yaw).ToString().ToLowerInvariant()} ({player.Yaw:0.0} / {player.Pitch:0.0})",
                $"Light: {light >> 4} sky, {light & 15} block",
                $"Dimension: {session.DimensionName}, time {session.World.TimeOfDay}",
                $"Mode: {player.Gamemode}, flying {player.Flying}, sprinting {player.Sprinting}, {player.Pose.ToString().ToLowerInvariant()}",
            };
            var y = 2f;
            foreach (var line in left)
            {
                if (line == null) continue;
                if (line.Length > 0)
                {
                    gui.Fill(new Rect(1, y - 1, gui.Font.Width(line) + 2, GuiFont.LineHeight), new Color32(0x50, 0x50, 0x50, 0x90));
                    gui.Text(line, 2, y, new Color32(0xE0, 0xE0, 0xE0, 0xFF), false);
                }
                y += GuiFont.LineHeight;
            }

            var right = new List<string>
            {
                "Unity " + Application.unityVersion,
                $"Mem: {GC.GetTotalMemory(false) / (1024 * 1024)} MiB managed",
                "",
                $"Display: {Screen.width}x{Screen.height}, GUI scale {gui.Scale}",
                SystemInfo.graphicsDeviceName,
                SystemInfo.graphicsDeviceVersion,
                SystemInfo.processorType,
            };
            if (session.Target.HasValue)
            {
                var target = session.Target.Value;
                right.Add("");
                right.Add($"Targeted block: {target.Position.X}, {target.Position.Y}, {target.Position.Z}");
                right.Add(target.State.Block.Name.ToString());
                foreach (var property in target.State.PropertyValues()) right.Add(property.Key + ": " + property.Value);
            }
            y = 2f;
            foreach (var line in right)
            {
                if (line.Length > 0)
                {
                    var lineWidth = gui.Font.Width(line);
                    gui.Fill(new Rect(gui.Width - lineWidth - 3, y - 1, lineWidth + 2, GuiFont.LineHeight), new Color32(0x50, 0x50, 0x50, 0x90));
                    gui.TextRight(line, gui.Width - 2, y, new Color32(0xE0, 0xE0, 0xE0, 0xFF), false);
                }
                y += GuiFont.LineHeight;
            }
        }
    }
}
